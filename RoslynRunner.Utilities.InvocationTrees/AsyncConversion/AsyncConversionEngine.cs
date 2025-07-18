using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Operations;
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

    public async Task<CompilationUnitSyntax?> GenerateAsyncVersion(
        INamedTypeSymbol type,
        string? methodName,
        CancellationToken cancellationToken)
    {
        var (root, allMethods) = await InvocationTreeBuilder.BuildInvocationTreeWithCacheAsync(
            _cache, type, _solution, methodName, cancellationToken: cancellationToken);

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
        var rootNode = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (model == null || rootNode == null)
        {
            return null;
        }

        var rewriter = new AsyncRewriter(model, eligible, asyncAlts);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(rootNode);
        return (CompilationUnitSyntax)Formatter.Format(newRoot, document.Project.Solution.Workspace);
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
        if (candidate.Length < original.Length)
            return false;
        for (int i = 0; i < original.Length; i++)
        {
            if (!original[i].Type.Equals(candidate[i].Type, SymbolEqualityComparer.Default))
                return false;
        }
        for (int i = original.Length; i < candidate.Length; i++)
        {
            if (!candidate[i].HasExplicitDefaultValue)
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

    public AsyncRewriter(SemanticModel model, HashSet<IMethodSymbol> methods, Dictionary<IMethodSymbol, IMethodSymbol> alts)
    {
        _model = model;
        _methods = methods;
        _alts = alts;
    }

    public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var symbol = _model.GetDeclaredSymbol(node);
        var updated = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node);
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
                updated = updated.WithReturnType(SyntaxFactory.ParseTypeName($"System.Threading.Tasks.Task<{ret.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}>");
            }
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
                var expression = node.Expression is MemberAccessExpressionSyntax ma ?
                    ma.WithName(SyntaxFactory.IdentifierName(alt.Name)) : node.Expression;
                var newInvocation = node.WithExpression(expression)
                    .WithArgumentList(node.ArgumentList.AddArguments(SyntaxFactory.Argument(SyntaxFactory.IdentifierName("cancellationToken"))));
                return SyntaxFactory.AwaitExpression(newInvocation);
            }
            if (_methods.Contains(operation.TargetMethod))
            {
                var newInvocation = node.WithArgumentList(node.ArgumentList.AddArguments(SyntaxFactory.Argument(SyntaxFactory.IdentifierName("cancellationToken"))));
                return SyntaxFactory.AwaitExpression(newInvocation);
            }
        }
        return base.VisitInvocationExpression(node);
    }
}
