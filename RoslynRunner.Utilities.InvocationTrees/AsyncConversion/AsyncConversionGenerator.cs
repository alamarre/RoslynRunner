using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Operations;
using RoslynRunner.Core;
using RoslynRunner.Core.Extensions;
using RoslynRunner.Core.QueueProcessing;

namespace RoslynRunner.Utilities.InvocationTrees;

public sealed class AsyncConversionGenerator
{
    private readonly CachedSymbolFinder _cache;
    private readonly Solution _solution;

    public AsyncConversionGenerator(CachedSymbolFinder cache, Solution solution)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _solution = solution ?? throw new ArgumentNullException(nameof(solution));
    }

    public async Task<AsyncConversionSolutionResult?> GenerateAsyncVersion(
        INamedTypeSymbol type,
        string? methodName,
        CancellationToken cancellationToken,
        InvocationTreeResult? invocationTreeResult = null,
        bool renameTransformedMethods = true)
    {
        if (type is null)
        {
            throw new ArgumentNullException(nameof(type));
        }

        var treeResult = invocationTreeResult ?? await BuildInvocationTreeAsync(type, methodName, cancellationToken).ConfigureAwait(false);

        var allMethods = treeResult.AllMethods;
        if (allMethods.Length == 0)
        {
            return null;
        }

        var (eligibleMethods, asyncAlternatives) = DetermineEligibleMethods(allMethods);
        if (eligibleMethods.Count == 0)
        {
            return null;
        }

        var documents = await ConvertDocumentsAsync(
                eligibleMethods,
                asyncAlternatives,
                cancellationToken,
                renameTransformedMethods)
            .ConfigureAwait(false);
        if (documents.Count == 0)
        {
            return null;
        }

        return new AsyncConversionSolutionResult(documents.ToImmutableArray(), treeResult);
    }

    private async Task<InvocationTreeResult> BuildInvocationTreeAsync(
        INamedTypeSymbol type,
        string? methodName,
        CancellationToken cancellationToken)
    {
        return await InvocationTreeBuilder.BuildInvocationTreeWithCacheAsync(
            _cache,
            type,
            _solution,
            methodName,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static (HashSet<IMethodSymbol> EligibleMethods, Dictionary<IMethodSymbol, IMethodSymbol> AsyncAlternatives)
        DetermineEligibleMethods(ImmutableArray<InvocationMethod> allMethods)
    {
        HashSet<IMethodSymbol> eligible = new(SymbolEqualityComparer.Default);
        Dictionary<IMethodSymbol, IMethodSymbol> asyncAlternatives = new(SymbolEqualityComparer.Default);

        foreach (var method in allMethods)
        {
            foreach (var invocation in method.InvokedMethods.Keys.OfType<IInvocationOperation>())
            {
                var asyncAlternative = GetAsyncAlternative(invocation.TargetMethod);
                if (asyncAlternative is null)
                {
                    continue;
                }

                asyncAlternatives[invocation.TargetMethod] = asyncAlternative;
                eligible.Add(method.MethodSymbol);
            }
        }

        if (eligible.Count == 0)
        {
            return (eligible, asyncAlternatives);
        }

        var additional = DedupingQueueRunner.ProcessResults(m => m.Callers, allMethods.Where(m => eligible.Contains(m.MethodSymbol)).ToArray());
        foreach (var method in additional)
        {
            eligible.Add(method.MethodSymbol);
        }

        return (eligible, asyncAlternatives);
    }

    private async Task<List<AsyncDocumentConversion>> ConvertDocumentsAsync(
        HashSet<IMethodSymbol> eligibleMethods,
        Dictionary<IMethodSymbol, IMethodSymbol> asyncAlternatives,
        CancellationToken cancellationToken,
        bool renameTransformedMethods)
    {
        var documentsToProcess = CollectDocuments(eligibleMethods);
        var conversions = new List<AsyncDocumentConversion>();

        foreach (var document in documentsToProcess)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var conversion = await ConvertDocumentAsync(
                    document,
                    eligibleMethods,
                    asyncAlternatives,
                    cancellationToken,
                    renameTransformedMethods)
                .ConfigureAwait(false);
            if (conversion is not null)
            {
                conversions.Add(conversion);
            }
        }

        return conversions;
    }

    private IReadOnlyList<Document> CollectDocuments(HashSet<IMethodSymbol> eligibleMethods)
    {
        var documents = new List<Document>();
        var documentIds = new HashSet<DocumentId>();

        foreach (var method in eligibleMethods)
        {
            foreach (var reference in method.DeclaringSyntaxReferences)
            {
                var document = _solution.GetDocument(reference.SyntaxTree);
                if (document is null)
                {
                    document = _solution.Projects.SelectMany( d => d.Documents)
                        .FirstOrDefault(d => d.FilePath == reference.SyntaxTree.FilePath);
                }
                if (document is not null && documentIds.Add(document.Id))
                {
                    documents.Add(document);
                }
            }
        }

        return documents;
    }

    private async Task<AsyncDocumentConversion?> ConvertDocumentAsync(
        Document document,
        HashSet<IMethodSymbol> eligibleMethods,
        Dictionary<IMethodSymbol, IMethodSymbol> asyncAlternatives,
        CancellationToken cancellationToken,
        bool renameTransformedMethods)
    {
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

        if (semanticModel is null || syntaxRoot is not CompilationUnitSyntax originalRoot)
        {
            return null;
        }

        var rewriter = new AsyncDocumentRewriter(
            semanticModel,
            eligibleMethods,
            asyncAlternatives,
            renameTransformedMethods);
        var visitedRoot = rewriter.Visit(originalRoot);
        if (visitedRoot is not CompilationUnitSyntax updatedRoot || !rewriter.HasChanges)
        {
            return null;
        }

        var formattedRoot = (CompilationUnitSyntax)Formatter.Format(updatedRoot, document.Project.Solution.Workspace);
        var conversions = ExtractConversions(formattedRoot, rewriter.MethodUpdates);

        if (rewriter.MethodUpdates.Count > 0)
        {
            formattedRoot = (CompilationUnitSyntax)formattedRoot.WithoutAnnotations(rewriter.MethodUpdates.Select(m => m.Annotation).ToArray());
        }

        return new AsyncDocumentConversion(document.Id, document, originalRoot, formattedRoot, conversions);
    }

    private static ImmutableArray<AsyncMethodConversion> ExtractConversions(
        CompilationUnitSyntax formattedRoot,
        IReadOnlyList<AsyncMethodUpdate> methodUpdates)
    {
        if (methodUpdates.Count == 0)
        {
            return ImmutableArray<AsyncMethodConversion>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<AsyncMethodConversion>(methodUpdates.Count);

        foreach (var update in methodUpdates)
        {
            var asyncMethod = formattedRoot.GetAnnotatedNodes(update.Annotation)
                .OfType<MethodDeclarationSyntax>()
                .Single();

            asyncMethod = asyncMethod.WithoutAnnotations(update.Annotation);
            builder.Add(new AsyncMethodConversion(update.OriginalMethod, asyncMethod));
        }

        return builder.MoveToImmutable();
    }

    private static IMethodSymbol? GetAsyncAlternative(IMethodSymbol method)
    {
        var containingType = method.ContainingType;
        var candidateName = method.Name + "Async";

        foreach (var member in containingType.GetMembers(candidateName).OfType<IMethodSymbol>())
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
            parameters.Where(parameter => !parameter.HasExplicitDefaultValue);

        var originalRequired = RequiredParameters(original).ToArray();
        var candidateRequired = RequiredParameters(candidate).ToArray();

        if (originalRequired.Length != candidateRequired.Length)
        {
            return false;
        }

        for (var index = 0; index < originalRequired.Length; index++)
        {
            if (!originalRequired[index].Type.Equals(candidateRequired[index].Type, SymbolEqualityComparer.Default))
            {
                return false;
            }
        }

        if (candidate.Length > candidateRequired.Length &&
            candidate.Skip(candidateRequired.Length).Any(parameter => !parameter.HasExplicitDefaultValue))
        {
            return false;
        }

        return true;
    }
}

internal sealed class AsyncDocumentRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly HashSet<IMethodSymbol> _methods;
    private readonly Dictionary<IMethodSymbol, IMethodSymbol> _alternatives;
    private readonly bool _renameTransformedMethods;
    private readonly List<AsyncMethodUpdate> _methodUpdates = new();

    public AsyncDocumentRewriter(
        SemanticModel model,
        HashSet<IMethodSymbol> methods,
        Dictionary<IMethodSymbol, IMethodSymbol> alternatives,
        bool renameTransformedMethods)
    {
        _model = model;
        _methods = methods;
        _alternatives = alternatives;
        _renameTransformedMethods = renameTransformedMethods;
    }

    public bool HasChanges { get; private set; }

    public IReadOnlyList<AsyncMethodUpdate> MethodUpdates => _methodUpdates;

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = base.VisitMethodDeclaration(node);
        if (visited is not MethodDeclarationSyntax updated)
        {
            return visited ?? node;
        }

        var symbol = _model.GetDeclaredSymbol(node);
        if (symbol is null || _methods.All(m => m.OriginalDefinition.GetMethodId() != symbol.OriginalDefinition.GetMethodId()))
        { 
            return updated;
        }

        HasChanges = true;

        if (!updated.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.AsyncKeyword)))
        {
            updated = updated.WithModifiers(updated.Modifiers.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword)));
        }

        var cancellationTokenParameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier("cancellationToken"))
            .WithType(SyntaxFactory.ParseTypeName("System.Threading.CancellationToken"))
            .WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression("default")));

        if (!updated.ParameterList.Parameters.Any(parameter => parameter.Identifier.ValueText == "cancellationToken"))
        {
            updated = updated.WithParameterList(updated.ParameterList.AddParameters(cancellationTokenParameter));
        }

        var returnType = symbol.ReturnType;
        if (returnType.SpecialType == SpecialType.System_Void)
        {
            updated = updated.WithReturnType(SyntaxFactory.ParseTypeName("System.Threading.Tasks.Task"));
        }
        else
        {
            var displayType = returnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            updated = updated.WithReturnType(SyntaxFactory.ParseTypeName($"System.Threading.Tasks.Task<{displayType}>"));
        }

        if (_renameTransformedMethods)
        {
            var identifierText = updated.Identifier.ValueText;
            if (!identifierText.EndsWith("Async", StringComparison.Ordinal))
            {
                updated = updated.WithIdentifier(CreateIdentifier(updated.Identifier, identifierText + "Async"));
            }
        }

        var annotation = new SyntaxAnnotation();
        updated = updated.WithAdditionalAnnotations(annotation);
        _methodUpdates.Add(new AsyncMethodUpdate(node, annotation));

        return updated;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = base.VisitInvocationExpression(node);
        var invocation = visited as InvocationExpressionSyntax ?? node;

        var operation = _model.GetOperation(node) as IInvocationOperation;
        if (operation is null)
        {
            return visited;
        }

        if (_alternatives.TryGetValue(operation.TargetMethod, out var asyncAlternative))
        {
            HasChanges = true;

            var updatedExpression = invocation.Expression is MemberAccessExpressionSyntax memberAccess
                ? memberAccess.WithName(SyntaxFactory.IdentifierName(asyncAlternative.Name))
                : invocation.Expression;

            var newInvocation = invocation.WithExpression(updatedExpression);

            if (asyncAlternative.Parameters.Any(parameter =>
                    parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ==
                    "global::System.Threading.CancellationToken"))
            {
                newInvocation = newInvocation.WithArgumentList(
                    invocation.ArgumentList.AddArguments(
                        SyntaxFactory.Argument(SyntaxFactory.IdentifierName("cancellationToken"))));
            }

            return SyntaxFactory.AwaitExpression(newInvocation);
        }

        if (_methods.Contains(operation.TargetMethod))
        {
            HasChanges = true;

            var awaitedInvocation = invocation;

            if (_renameTransformedMethods)
            {
                var newName = operation.TargetMethod.Name.EndsWith("Async", StringComparison.Ordinal)
                    ? operation.TargetMethod.Name
                    : operation.TargetMethod.Name + "Async";
                awaitedInvocation = awaitedInvocation.WithExpression(
                    RenameInvocationTarget(awaitedInvocation.Expression, newName));
            }

            awaitedInvocation = awaitedInvocation.WithArgumentList(
                invocation.ArgumentList.AddArguments(
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName("cancellationToken"))));

            return SyntaxFactory.AwaitExpression(awaitedInvocation);
        }

        return visited;
    }

    private static SyntaxToken CreateIdentifier(SyntaxToken original, string newText)
    {
        return SyntaxFactory.Identifier(original.LeadingTrivia, newText, original.TrailingTrivia);
    }

    private static SimpleNameSyntax RenameSimpleName(SimpleNameSyntax name, string newName)
    {
        return name switch
        {
            IdentifierNameSyntax identifierName => identifierName.WithIdentifier(CreateIdentifier(identifierName.Identifier, newName)),
            GenericNameSyntax genericName => genericName.WithIdentifier(CreateIdentifier(genericName.Identifier, newName)),
            _ => SyntaxFactory.IdentifierName(newName),
        };
    }

    private static ExpressionSyntax RenameInvocationTarget(ExpressionSyntax expression, string newName)
    {
        return expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.WithName(RenameSimpleName(memberAccess.Name, newName)),
            MemberBindingExpressionSyntax memberBinding => memberBinding.WithName(RenameSimpleName(memberBinding.Name, newName)),
            IdentifierNameSyntax identifier => RenameSimpleName(identifier, newName),
            GenericNameSyntax genericName => RenameSimpleName(genericName, newName),
            _ => expression,
        };
    }
}

internal sealed record AsyncMethodUpdate(MethodDeclarationSyntax OriginalMethod, SyntaxAnnotation Annotation);
