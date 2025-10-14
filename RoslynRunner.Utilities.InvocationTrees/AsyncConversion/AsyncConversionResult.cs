using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynRunner.Utilities.InvocationTrees;

/// <summary>
/// Represents the results of running the async conversion generator across a solution.
/// </summary>
public sealed record AsyncConversionSolutionResult(
    ImmutableArray<AsyncDocumentConversion> Documents,
    InvocationTreeResult InvocationTree);

/// <summary>
/// Represents the async conversion result for a single document.
/// </summary>
public sealed record AsyncDocumentConversion(
    DocumentId DocumentId,
    Document Document,
    CompilationUnitSyntax OriginalRoot,
    CompilationUnitSyntax UpdatedRoot,
    ImmutableArray<AsyncMethodConversion> ConvertedMethods);

public sealed record AsyncMethodConversion(
    MethodDeclarationSyntax OriginalMethod,
    MethodDeclarationSyntax AsyncMethod);
