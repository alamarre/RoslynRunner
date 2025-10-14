using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynRunner.Utilities.InvocationTrees;

/// <summary>
/// Represents the results of running the async conversion engine.
/// </summary>
public sealed record AsyncConversionResult(
    CompilationUnitSyntax OriginalRoot,
    CompilationUnitSyntax UpdatedRoot,
    ImmutableArray<AsyncMethodConversion> ConvertedMethods,
    InvocationTreeResult InvocationTree);

public sealed record AsyncMethodConversion(
    MethodDeclarationSyntax OriginalMethod,
    MethodDeclarationSyntax AsyncMethod);
