using Microsoft.CodeAnalysis;

namespace RoslynRunner.Core;

public interface ISolutionProcessor
{
    Task ProcessSolution(Solution solution, string? context, CancellationToken cancellationToken);
}