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

public class InvocationTreeProcessor : ISolutionProcessor<InvocationTreeProcessorParameters>
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

        var (results, allMethods) =
            await InvocationTreeBuilder.BuildInvocationTreeAsync((INamedTypeSymbol)symbol, solution, parameters.MethodFilter, parameters.MaxImplementations, cancellationToken);
        if (parameters.Diagrams != null)
        {
            foreach (var diagram in parameters.Diagrams)
            {
                var diagramMethods = allMethods;
               
                if(!Directory.Exists(diagram.OutputPath))
                {
                    Directory.CreateDirectory(diagram.OutputPath);
                }

                if (diagram.InclusivePruneFilter != null)
                {
                    var root = results.Methods.First();
                    diagramMethods = DedupingQueueRunner.ProcessResults((InvocationMethod i) => {
                        bool selfIsSafe = (new[] { i }).AsQueryable().Any(diagram.InclusivePruneFilter);
                        if (!selfIsSafe)
                        {
                            return [];
                        }
                        //var safeMethods = i.InvokedMethods.Values.AsQueryable().Where(diagram.InclusivePruneFilter);

                        List<InvocationMethod> children = i.InvokedMethods.Values.Concat(i.Implementations).ToList();
                        return children;
                    }, [root]).ToList();

                }
                HashSet<IMethodSymbol> validMethods = new(diagramMethods.Select(m => m.MethodSymbol), SymbolEqualityComparer.Default);
                if (diagram.Filter != null)
                {
                    var filteredMethods = diagramMethods
                        .Where(m => m.InvokedMethods.AsQueryable().Where(diagram.Filter).Any())
                        .ToArray();
                    if (diagram.SeparateDiagrams)
                    {
                        foreach (var method in filteredMethods)
                        {
                            var callChains = DedupingQueueRunner.ProcessResults(i => i.Callers.Where(c => validMethods.Contains(c.MethodSymbol)), new[] { method });
                            var result = diagram.DiagramType == "dot"
                                ? await InvocationTreeDotGraphWriter.GetDotGraphForCallers(callChains, diagram.WriteAllMethods, method)
                                : InvocationTreeMermaidWriter.GetMermaidDagForCallers(callChains);

                            var extension = diagram.DiagramType == "dot" ? ".dot" : ".md";
                            var fileName = method.MethodSymbol.ContainingType.ToDisplayString() + "." +
                                           method.MethodSymbol.Name + extension;
                            fileName = fileName.Replace('<', '_').Replace('>', '_');
                            try
                            {
                                File.WriteAllText(Path.Combine(diagram.OutputPath, fileName), result);
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine(e);
                            }
                        }
                    }
                    else
                    {
                        var callChains = DedupingQueueRunner.ProcessResults(i => i.Callers.Where( c => validMethods.Contains(c.MethodSymbol)), filteredMethods);
                        var result = diagram.DiagramType == "dot"
                            ? await  InvocationTreeDotGraphWriter.GetDotGraphForCallers(callChains, diagram.WriteAllMethods)
                            : InvocationTreeMermaidWriter.GetMermaidDagForCallers(callChains);


                        var extension = diagram.DiagramType == "dot" ? ".dot" : ".md";

                        File.WriteAllText(Path.Combine(diagram.OutputPath, diagram.Name) + extension, result);
                    }
                }
                else
                {
                    var result = diagram.DiagramType == "dot"
                            ? await InvocationTreeDotGraphWriter.GetDotGraphForCallers(diagramMethods, diagram.WriteAllMethods)
                            : InvocationTreeMermaidWriter.GetMermaidDagForCallers(diagramMethods);


                    var extension = diagram.DiagramType == "dot" ? ".dot" : ".md";
                    File.WriteAllText(Path.Combine(diagram.OutputPath, diagram.Name) + extension, result);
                }
            }
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
