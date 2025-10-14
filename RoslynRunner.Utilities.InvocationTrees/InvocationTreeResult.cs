using System.Collections.Generic;
using System.Collections.Immutable;

namespace RoslynRunner.Utilities.InvocationTrees;

/// <summary>
/// Represents the result of crawling the invocation tree for a symbol.
/// </summary>
public sealed record InvocationTreeResult(InvocationRoot Root, ImmutableArray<InvocationMethod> AllMethods)
{
    public static InvocationTreeResult Empty { get; } =
        new(new InvocationRoot(new List<InvocationMethod>()), ImmutableArray<InvocationMethod>.Empty);
}
