using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace RoslynRunner.Core;

public interface ISolutionProcessor
{
    Task ProcessSolution(Solution solution, string? context, CancellationToken cancellationToken);
}

public interface ISolutionProcessor<T>
{
	Task ProcessSolution(Solution solution, T? context, ILogger logger, CancellationToken cancellationToken);
}

