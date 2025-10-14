using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using RoslynRunner.Abstractions;
using RoslynRunner.Core;
using RoslynRunner.Core.Extensions;

namespace RoslynRunner.Utilities.InvocationTrees;

public class AsyncConversionProcessor : ISolutionProcessor<AsyncConversionParameters>, ISolutionProcessor
{
    public async Task ProcessSolution(Solution solution, AsyncConversionParameters? context, ILogger logger, CancellationToken cancellationToken)
    {
        if (context == null)
        {
            throw new ArgumentException("context required");
        }

        var cache = await CachedSymbolFinder.FromCache(solution);
        var serviceType = cache.GetSymbolByMetadataName(context.TypeName);
        if (serviceType == null)
        {
            logger.LogError("service type not found");
            return;
        }

        var engine = new AsyncConversionEngine(cache, solution);
        var newRoot = await engine.GenerateAsyncVersion(serviceType, context.MethodName, cancellationToken);
        if (newRoot == null)
        {
            logger.LogInformation("no methods to convert");
            return;
        }

        await File.WriteAllTextAsync(context.OutputPath, newRoot.ToFullString(), cancellationToken);
        RunContextAccessor.RunContext.Output.Add($"Created file: {Path.GetFullPath(context.OutputPath)}");
    }

    public async Task ProcessSolution(Solution solution, string? context, ILogger logger, CancellationToken cancellationToken)
    {
        if (context == null)
        {
            throw new ArgumentException("context required");
        }
        var parameters = JsonSerializer.Deserialize<AsyncConversionParameters>(context);
        await ProcessSolution(solution, parameters, logger, cancellationToken);
    }
}
