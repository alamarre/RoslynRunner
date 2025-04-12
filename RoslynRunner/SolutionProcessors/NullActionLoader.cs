using System;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using RoslynRunner.Abstractions;
using RoslynRunner.Core;
using RoslynRunner.Core.Extensions;

namespace RoslynRunner.SolutionProcessors;

public record LoadContext(bool CacheSymbols, string? ProjectName = null);

public class NullActionLoader : ISolutionProcessor
{
    public async Task ProcessSolution(Solution solution, string? context, ILogger logger, CancellationToken cancellationToken)
    {

        var loadContext = context is null ? null : JsonSerializer.Deserialize<LoadContext>(context);
        var runContext = RunContextAccessor.RunContext;
        runContext.Output.Add("Solution loaded");

        if (loadContext?.CacheSymbols is true)
        {
            await CachedSymbolFinder.FromCache(solution, loadContext.ProjectName, cancellationToken);

            runContext.Output.Add("Symbols cached");
        }
    }
}
