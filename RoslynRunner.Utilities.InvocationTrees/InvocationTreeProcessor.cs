using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynRunner.Core;
using RoslynRunner.Core.Extensions;
using RoslynRunner.Core.QueueProcessing;
using System.Linq;
using System.Text.Json;
using System.Linq.Dynamic.Core;
using Microsoft.Extensions.Logging;

namespace RoslynRunner.Utilities.InvocationTrees;

public class InvocationTreeProcessor : ISolutionProcessor
{
    public async Task ProcessSolution(Solution solution, string? context, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (context == null) throw new ArgumentException("context must be an InvocationTreeProcessorParameters");
        var parameters = JsonSerializer.Deserialize<InvocationTreeProcessorParameters>(context);
        if (parameters == null) throw new Exception("context must be an InvocationTreeProcessorParameters");

        var symbol = await FindSymbol(solution, parameters.StartingSymbol,
            cancellationToken);

        var (results, allMethods) =
            await InvocationTreeBuilder.BuildInvocationTreeAsync((INamedTypeSymbol)symbol, solution, cancellationToken);
        if (parameters.Diagrams != null)
            foreach (var diagram in parameters.Diagrams)
                if (diagram.Filter != null)
                {
                    var filteredMethods = allMethods
                        .Where(m => m.InvokedMethods.AsQueryable().Where(diagram.Filter).Any())
                        .ToArray();
                    if (diagram.SeparateDiagrams)
                    {
                        foreach (var method in filteredMethods)
                        {
                            var callChains = DedupingQueueRunner.ProcessResults(i => i.Callers, new[] { method });
                            var result = diagram.DiagramType == "dot"
                                ? await InvocationTreeDotGraphWriter.GetDotGraphForCallers(callChains, method)
                                : InvocationTreeMermaidWriter.GetMermaidDagForCallers(callChains);

                            var extension = diagram.DiagramType == "dot" ? ".dot" : ".md";
                            var fileName = method.MethodSymbol.ContainingType.ToDisplayString() + "." +
                                           method.MethodSymbol.Name + extension;

                            File.WriteAllText(Path.Combine(diagram.OutputPath, fileName), result);
                        }
                    }
                    else
                    {
                        var callChains = DedupingQueueRunner.ProcessResults(i => i.Callers, filteredMethods);
                        var result = InvocationTreeMermaidWriter.GetMermaidDagForCallers(callChains);
                        ;
                        File.WriteAllText(Path.Combine(diagram.OutputPath, diagram.Name) + ".md", result);
                    }
                }
                else
                {
                    var result = InvocationTreeMermaidWriter.GetMermaidDag(allMethods.ToList());
                    File.WriteAllText(Path.Combine(diagram.OutputPath, diagram.Name) + ".md", result);
                }
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
}
