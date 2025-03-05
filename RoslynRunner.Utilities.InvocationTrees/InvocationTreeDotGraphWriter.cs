using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DotNetGraph.Compilation;
using DotNetGraph.Core;
using DotNetGraph.Extensions;
using Microsoft.CodeAnalysis;

namespace RoslynRunner.Utilities.InvocationTrees;

public class InvocationTreeDotGraphWriter
{
    public static async Task<string> GetDotGraphForCallers(IEnumerable<InvocationMethod> methods,
         bool writeAllMethods = false,
        params InvocationMethod[] endpoints
       )
    {
        var graph = new DotGraph().WithIdentifier("Dependency graph");
        Dictionary<ISymbol, DotNode> written = new(SymbolEqualityComparer.Default);

        foreach (var type in methods.GroupBy(m => m.MethodSymbol.ContainingType, SymbolEqualityComparer.Default))
        {
            var typeNode = new DotNode().WithIdentifier(type.Key!.Name);
            if (type.Any(m => endpoints.Contains(m)))
            {
                typeNode.WithColor("red");
            }

            graph.Elements.Add(typeNode);
            written.Add(type.Key, typeNode);
        }
        List<DotEdge> writtenEdges = new();


        foreach (var method in methods)
        {
            foreach (var caller in method.Callers)
            {
                // TODO: maybe double check that we pruned results so this isn't an error
                if (!written.ContainsKey(caller.MethodSymbol.ContainingType))
                {
                    continue;
                }
                var callerNode = written[caller.MethodSymbol.ContainingType];
                var methodNode = written[method.MethodSymbol.ContainingType];
                if(!writeAllMethods)
                {                    
                    if(callerNode == methodNode)
                    {
                        continue;
                    }

                    if (writtenEdges.Any(e => e.From == callerNode.Identifier && e.To == methodNode.Identifier))
                    {
                        continue;
                    }
                }
                var edge = new DotEdge()
                    .From(callerNode)
                    .To(methodNode);
                writtenEdges.Add(edge);
                if (writeAllMethods)
                {
                    edge = edge.WithLabel(method.MethodSymbol.Name);
                }
                if (methodNode.Color?.Value != "red" &&
                    method.MethodSymbol.ContainingType.AllInterfaces.Any(i =>
                        i.Equals(caller.MethodSymbol.ContainingType, SymbolEqualityComparer.Default)))
                {
                    methodNode.WithColor("blue");
                }

                graph.Elements.Add(edge);
            }
        }

        await using var writer = new StringWriter();
        var context = new CompilationContext(writer, new DotNetGraph.Compilation.CompilationOptions());
        graph.Directed = true;
        var compilation = graph.CompileAsync(context);
        var result = writer.GetStringBuilder().ToString();
        return result;
    }
}
