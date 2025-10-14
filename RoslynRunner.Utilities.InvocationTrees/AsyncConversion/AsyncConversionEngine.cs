using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Operations;
using RoslynRunner.Core.Extensions;
using RoslynRunner.Core.QueueProcessing;

namespace RoslynRunner.Utilities.InvocationTrees;

public class AsyncConversionEngine
{
    private readonly CachedSymbolFinder _cache;
    private readonly Solution _solution;

    public AsyncConversionEngine(CachedSymbolFinder cache, Solution solution)
    {
        _cache = cache;
        _solution = solution;
    }

    public async Task<AsyncConversionResult?> GenerateAsyncVersion(
        INamedTypeSymbol type,
        string? methodName,
        CancellationToken cancellationToken,
        InvocationTreeResult? invocationTreeResult = null)
    {
        var treeResult = invocationTreeResult ?? await InvocationTreeBuilder.BuildInvocationTreeWithCacheAsync(
            _cache, type, _solution, methodName, cancellationToken: cancellationToken);
        var allMethods = treeResult.AllMethods;

        HashSet<IMethodSymbol> eligible = new(SymbolEqualityComparer.Default);
        Dictionary<IMethodSymbol, IMethodSymbol> asyncAlts = new(SymbolEqualityComparer.Default);

        foreach (var method in allMethods)
        {
            var data = _cache.GetMethodData(method.MethodSymbol);
            if (data == null) continue;
            foreach (var op in data.InvokedOperations.OfType<IInvocationOperation>())
            {
                var alt = GetAsyncAlternative(op.TargetMethod);
                if (alt != null)
                {
                    asyncAlts[op.TargetMethod] = alt;
                    eligible.Add(method.MethodSymbol);
                }
            }
        }

        var more = DedupingQueueRunner.ProcessResults(m => m.Callers, allMethods.Where(m => eligible.Contains(m.MethodSymbol)).ToArray());
        foreach (var m in more)
        {
            eligible.Add(m.MethodSymbol);
        }

        if (!eligible.Any())
        {
            return null;
        }

        var document = _solution.GetDocument(type.Locations.First().SourceTree)!;
        var model = await document.GetSemanticModelAsync(cancellationToken);
        var originalRoot = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (model == null || originalRoot == null)
        {
            return null;
        }

        var rewriter = new AsyncRewriter(model, eligible, asyncAlts);
        var visitedRoot = rewriter.Visit(originalRoot);
        if (visitedRoot is not CompilationUnitSyntax newRoot)
        {
            return null;
        }
        var formattedRoot = (CompilationUnitSyntax)Formatter.Format(newRoot, document.Project.Solution.Workspace);

        var conversions = rewriter.MethodUpdates
            .Select(update =>
            {
                var asyncMethod = formattedRoot.GetAnnotatedNodes(update.Annotation)
                    .OfType<MethodDeclarationSyntax>()
                    .Single();
                asyncMethod = asyncMethod.WithoutAnnotations(update.Annotation);
                return new AsyncMethodConversion(update.OriginalMethod, asyncMethod);
            })
            .ToImmutableArray();

        if (rewriter.MethodUpdates.Count > 0)
        {
            formattedRoot = (CompilationUnitSyntax)formattedRoot.WithoutAnnotations(
                rewriter.MethodUpdates.Select(m => m.Annotation).ToArray());
        }

        return new AsyncConversionResult(originalRoot, formattedRoot, conversions, treeResult);
    }

    private static IMethodSymbol? GetAsyncAlternative(IMethodSymbol method)
    {
        var containing = method.ContainingType;
        var candidateName = method.Name + "Async";
        foreach (var member in containing.GetMembers(candidateName).OfType<IMethodSymbol>())
        {
            if (ParametersMatch(method.Parameters, member.Parameters))
            {
                return member;
            }
        }
        return null;
    }

    private static bool ParametersMatch(ImmutableArray<IParameterSymbol> original, ImmutableArray<IParameterSymbol> candidate)
    {
        static IEnumerable<IParameterSymbol> RequiredParameters(ImmutableArray<IParameterSymbol> parameters) =>
            parameters.Where(p => !p.HasExplicitDefaultValue);

        var originalRequired = RequiredParameters(original).ToArray();
        var candidateRequired = RequiredParameters(candidate).ToArray();

        if (originalRequired.Length != candidateRequired.Length)
        {
            return false;
        }

        for (int i = 0; i < originalRequired.Length; i++)
        {
            if (!originalRequired[i].Type.Equals(candidateRequired[i].Type, SymbolEqualityComparer.Default))
            {
                return false;
            }
        }

        // Ensure candidate's additional parameters (if any) are optional so the rewritten invocation remains valid.
        if (candidate.Length > candidateRequired.Length &&
            candidate.Skip(candidateRequired.Length).Any(p => !p.HasExplicitDefaultValue))
        {
            return false;
        }

        return true;
    }
}

internal class AsyncRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly HashSet<IMethodSymbol> _methods;
    private readonly Dictionary<IMethodSymbol, IMethodSymbol> _alts;
    private readonly List<AsyncMethodUpdate> _methodUpdates = new();

    public IReadOnlyList<AsyncMethodUpdate> MethodUpdates => _methodUpdates;

    public AsyncRewriter(SemanticModel model, HashSet<IMethodSymbol> methods, Dictionary<IMethodSymbol, IMethodSymbol> alts)
    {
        _model = model;
        _methods = methods;
        _alts = alts;
    }

    public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = base.VisitMethodDeclaration(node);
        if (visited is not MethodDeclarationSyntax updated)
        {
            return visited ?? node;
        }

        var symbol = _model.GetDeclaredSymbol(node);
        if (symbol != null && _methods.Contains(symbol))
        {
            if (!updated.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            {
                updated = updated.WithModifiers(updated.Modifiers.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword)));
            }

            var ctParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier("cancellationToken"))
                .WithType(SyntaxFactory.ParseTypeName("System.Threading.CancellationToken"))
                .WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression("default")));
            updated = updated.WithParameterList(updated.ParameterList.AddParameters(ctParam));

            var ret = symbol.ReturnType;
            if (ret.SpecialType == SpecialType.System_Void)
            {
                updated = updated.WithReturnType(SyntaxFactory.ParseTypeName("System.Threading.Tasks.Task"));
            }
            else
            {
                var returnType = "System.Threading.Tasks.Task<" +
                    ret.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + ">";
                updated = updated.WithReturnType(SyntaxFactory.ParseTypeName(returnType));
            }

            var annotation = new SyntaxAnnotation();
            updated = updated.WithAdditionalAnnotations(annotation);
            _methodUpdates.Add(new AsyncMethodUpdate(node, annotation));
        }
        return updated;
    }

    public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var operation = _model.GetOperation(node) as IInvocationOperation;
        if (operation != null)
        {
            if (_alts.TryGetValue(operation.TargetMethod, out var alt))
            {
                var expression = node.Expression is MemberAccessExpressionSyntax ma
                    ? ma.WithName(SyntaxFactory.IdentifierName(alt.Name))
                    : node.Expression;

                var newInvocation = node.WithExpression(expression);

                if (alt.Parameters.Any(p =>
                        p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                        == "global::System.Threading.CancellationToken"))
                {
                    newInvocation = newInvocation.WithArgumentList(
                        node.ArgumentList.AddArguments(
                            SyntaxFactory.Argument(SyntaxFactory.IdentifierName("cancellationToken"))));
                }

                return SyntaxFactory.AwaitExpression(newInvocation);
            }
            if (_methods.Contains(operation.TargetMethod))
            {
                var newInvocation = node.WithArgumentList(
                    node.ArgumentList.AddArguments(
                        SyntaxFactory.Argument(SyntaxFactory.IdentifierName("cancellationToken"))));
                return SyntaxFactory.AwaitExpression(newInvocation);
            }
        }
        return base.VisitInvocationExpression(node) ?? node;
    }
}

internal sealed record AsyncMethodUpdate(MethodDeclarationSyntax OriginalMethod, SyntaxAnnotation Annotation);
