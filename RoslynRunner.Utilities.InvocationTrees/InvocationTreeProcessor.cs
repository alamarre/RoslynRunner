using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynRunner.Core;
using RoslynRunner.Core.Extensions;
using RoslynRunner.Core.QueueProcessing;
using System.Linq;
using System.Text.Json;
using System.Linq.Dynamic.Core;
using Microsoft.Extensions.Logging;
using RoslynRunner.Abstractions;

namespace RoslynRunner.Utilities.InvocationTrees;

public class InvocationTreeProcessor : ISolutionProcessor<InvocationTreeProcessorParameters>, ISolutionProcessor
{
    public async Task ProcessSolution(Solution solution, InvocationTreeProcessorParameters? parameters, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (parameters == null)
        {
            throw new Exception("context must be an InvocationTreeProcessorParameters");
        }

        var symbol = await FindSymbol(solution, parameters.StartingSymbol,
            cancellationToken);

        InvocationTreeResult invocationTreeResult;
        if (parameters.UseCache)
        {
            var cache = await CachedSymbolFinder.FromCache(solution);
            invocationTreeResult = await InvocationTreeBuilder.BuildInvocationTreeWithCacheAsync(
                cachedSymbolFinder: cache,
                startingType: (INamedTypeSymbol)symbol!,
                solution: solution,
                methodFilter: parameters.MethodFilter,
                maxLimit: parameters.MaxImplementations,
                cancellationToken: cancellationToken);
        }
        else
        {
            invocationTreeResult =
                await InvocationTreeBuilder.BuildInvocationTreeAsync(
                    startingType: (INamedTypeSymbol)symbol!,
                    solution: solution,
                    methodFilter: parameters.MethodFilter,
                    maxLimit: parameters.MaxImplementations,
                    cancellationToken: cancellationToken);

        }
        var results = invocationTreeResult.Root;
        var allMethods = invocationTreeResult.AllMethods.ToList();
        var runContext = RunContextAccessor.RunContext;

        if (parameters.Diagrams != null)
        {
            foreach (var diagram in parameters.Diagrams)
            {
                var diagramMethods = allMethods;

                if (!Directory.Exists(diagram.OutputPath))
                {
                    Directory.CreateDirectory(diagram.OutputPath);
                }

                if (diagram.InclusivePruneFilter != null)
                {
                    var root = results.Methods.First();
                    diagramMethods = DedupingQueueRunner.ProcessResults((InvocationMethod i) =>
                    {
                        bool selfIsSafe = (new[] { i }).AsQueryable().Any(diagram.InclusivePruneFilter, symbol!.ContainingAssembly.Name);
                        if (!selfIsSafe)
                        {
                            return [];
                        }
                        //var safeMethods = i.InvokedMethods.Values.AsQueryable().Where(diagram.InclusivePruneFilter);

                        List<InvocationMethod> children = i.InvokedMethods.Values.Concat(i.Implementations).ToList();
                        return children;
                    }, [root]).ToList();

                }
                var extension = diagram.DiagramType switch
                {
                    "dot" => ".dot",
                    "JSON" => ".json",
                    "d3" => ".html",
                    "mermaid" => ".md",
                    _ => throw new NotImplementedException($"Diagram type {diagram.DiagramType} is not supported"),
                };
                HashSet<IMethodSymbol> validMethods = new(diagramMethods.Select(m => m.MethodSymbol), SymbolEqualityComparer.Default);
                if (diagram.Filter != null)
                {
                    var filteredMethods = diagramMethods
                        .Where(m => m.InvokedMethods.AsQueryable().Where(diagram.Filter, symbol!.ContainingAssembly.Name).Any())
                        .ToArray();
                    if (diagram.SeparateDiagrams)
                    {
                        foreach (var method in filteredMethods)
                        {
                            var callChains = DedupingQueueRunner.ProcessResults(i => i.Callers.Where(c => validMethods.Contains(c.MethodSymbol)), new[] { method });
                            var result = await GetDiagram(diagram, results, callChains);

                            var fileName = method.MethodSymbol.ContainingType.ToDisplayString() + "." +
                                           method.MethodSymbol.Name + extension;
                            fileName = fileName.Replace('<', '_').Replace('>', '_');
                            try
                            {
                                var path = Path.Combine(diagram.OutputPath, fileName);
                                await File.WriteAllTextAsync(path, result);
                                runContext.Output.Add($"wrote diagram {path}");
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine(e);
                            }
                        }
                    }
                    else
                    {
                        var callChains = DedupingQueueRunner.ProcessResults(i => i.Callers.Where(c => validMethods.Contains(c.MethodSymbol)), filteredMethods);
                        var result = await GetDiagram(diagram, results, callChains);
                        var path = Path.Combine(diagram.OutputPath, diagram.Name + extension);
                        await File.WriteAllTextAsync(path, result);
                        runContext.Output.Add($"wrote diagram {path}");
                    }
                }
                else
                {
                    string result = await GetDiagram(diagram, results, diagramMethods);
                    var path = Path.Combine(diagram.OutputPath, diagram.Name + extension);
                    await File.WriteAllTextAsync(path, result);
                    runContext.Output.Add($"wrote diagram {path}");
                }
            }
        }


    }

    private static async Task<string> GetDiagram(InvocationDiagram diagram, InvocationRoot invocationRoot, IEnumerable<InvocationMethod> diagramMethods)
    {
        return diagram.DiagramType switch
        {
            "dot" => await InvocationTreeDotGraphWriter.GetDotGraphForCallers(diagramMethods, diagram.WriteAllMethods),
            "JSON" => InvocationTreeJsonWriter.WriteInvocationTreeToJson(diagramMethods),
            "mermaid" => InvocationTreeMermaidWriter.GetMermaidDagForInvocationTree(invocationRoot, diagramMethods.ToHashSet(), diagram.WriteAllMethods),
            "d3" => InvocationTreeD3Writer.GetD3GraphForCallers(diagramMethods),
            _ => throw new NotImplementedException($"Diagram type {diagram.DiagramType} is not supported"),
        };
    }

    public static async Task<ISymbol?> FindSymbol(Solution solution, string fullyQualified,
        CancellationToken cancellationToken = default, Project? project = null)
    {
        var shortName = fullyQualified.Substring(fullyQualified.LastIndexOf('.') + 1);
        if (project != null)
        {
            var symbols = await SymbolFinder.FindDeclarationsAsync(project, shortName, false, cancellationToken);
            return symbols.FirstOrDefault(s => s.ToDisplayString() == fullyQualified);
        }

        return (await SymbolFinder.FindSourceDeclarationsAsync(solution, shortName, false, cancellationToken))
            .FirstOrDefault(s => s.ToDisplayString() == fullyQualified);
    }

    public async Task ProcessSolution(Solution solution, string? context, ILogger logger, CancellationToken cancellationToken)
    {
        if (context == null)
        {
            throw new Exception("context must be an InvocationTreeProcessorParameters");
        }

        var parameters = JsonSerializer.Deserialize<InvocationTreeProcessorParameters>(context);
        await ProcessSolution(solution, parameters, logger, cancellationToken);
    }
}
