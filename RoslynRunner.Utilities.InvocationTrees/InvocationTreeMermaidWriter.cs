using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using RoslynRunner.Core.QueueProcessing;

namespace RoslynRunner.Utilities.InvocationTrees;

internal class InvocationTreeMermaidWriter
{
    public static string GetMermaidDag(IEnumerable<InvocationMethod> methods, bool writeAllMethods)
    {
        var sb = new StringBuilder();
        HashSet<string> written = new();
        sb.AppendLine("```mermaid");
        sb.AppendLine("classDiagram");
        foreach (var method in methods)
        {
            var sourceClass = method.MethodSymbol.ContainingType.Name;
            var sourceMethodName = method.MethodSymbol.Name;
            foreach (var call in method.InvokedMethods)
            {
                var newMethod = call.Value.MethodSymbol;
                sb.AppendLine($"{sourceClass} --|> {newMethod.ContainingType.Name} : {newMethod.Name}");
            }

            foreach (var implementation in method.Implementations)
            {
                var newMethod = implementation.MethodSymbol;
                var relationship = $"{sourceClass} <|-- {newMethod.ContainingType.Name}";
                if (written.Add(relationship))
                {
                    sb.AppendLine(relationship);
                }
            }
        }

        sb.AppendLine("```");
        return sb.ToString();
    }

    public static string GetMermaidDagForInvocationTree(
        InvocationRoot invocationRoot,
        HashSet<InvocationMethod> methods,
        bool writeAllMethods)
    {
        if (invocationRoot == null)
        {
            throw new ArgumentNullException(nameof(invocationRoot));
        }

        if (methods == null)
        {
            throw new ArgumentNullException(nameof(methods));
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("```mermaid");
        sb.AppendLine("graph TD");

        Dictionary<InvocationMethod, string> legend =
            new Dictionary<InvocationMethod, string>();
        Dictionary<string, List<InvocationMethod>> typeGroups =
            new Dictionary<string, List<InvocationMethod>>();
        int current = 0;

        StringBuilder graphBody = new StringBuilder();

        DedupingQueueRunner.ProcessResults<InvocationMethod>(
            delegate (InvocationMethod method)
            {
                if (!methods.Contains(method))
                {
                    return Enumerable.Empty<InvocationMethod>();
                }

                // assign legend name
                string? legendName;
                if (!legend.TryGetValue(method, out legendName))
                {
                    current++;
                    legendName = "X" + current.ToString("X");
                    legend.Add(method, legendName);
                }

                // group by fully-qualified type name
                string typeName = method.MethodSymbol.ContainingType
                    .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));

                List<InvocationMethod>? methodsInType;
                if (!typeGroups.TryGetValue(typeName, out methodsInType))
                {
                    methodsInType = new List<InvocationMethod>();
                    typeGroups.Add(typeName, methodsInType);
                }
                methodsInType.Add(method);

                // edges for invoked methods
                foreach (KeyValuePair<IInvocationOperation, InvocationMethod> call in method.InvokedMethods)
                {

                    InvocationMethod target = call.Value;
                    if (!methods.Contains(target))
                    {
                        continue;
                    }
                    string? targetLegend;
                    if (!legend.TryGetValue(target, out targetLegend))
                    {
                        current++;
                        targetLegend = "X" + current.ToString("X");
                        legend.Add(target, targetLegend);
                    }
                    graphBody.AppendLine($"{legendName} --> {targetLegend}");
                }

                // edges for implementations
                foreach (InvocationMethod impl in method.Implementations)
                {
                    if (!methods.Contains(impl))
                    {
                        continue;
                    }
                    string? implLegend;
                    if (!legend.TryGetValue(impl, out implLegend))
                    {
                        current++;
                        implLegend = "X" + current.ToString("X");
                        legend.Add(impl, implLegend);
                    }
                    graphBody.AppendLine($"{legendName} --> {implLegend}");
                }

                return method.InvokedMethods.Values
                    .Concat(method.Implementations)
                    .Where(m => methods.Contains(m));
            },
            invocationRoot.Methods.ToArray());

        // render subgraphs by type
        foreach (KeyValuePair<string, List<InvocationMethod>> group in typeGroups)
        {
            string rawName = group.Key;
            string safeId = SanitizeTypeName(rawName);
            sb.AppendLine($"  subgraph {safeId}[\"{rawName}\"]");

            foreach (InvocationMethod m in group.Value)
            {
                string nodeId = legend[m];
                bool isInterface = m.Implementations.Count > 0;

                string nodeDef;
                if (isInterface)
                {
                    nodeDef = $"{nodeId}{{\"{m.MethodSymbol.Name}\"}}";
                }
                else
                {
                    nodeDef = $"{nodeId}[{m.MethodSymbol.Name}]";
                }

                sb.AppendLine($"    {nodeDef}");
            }

            sb.AppendLine("  end");
        }

        sb.Append(graphBody.ToString());
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static string SanitizeTypeName(string fullTypeName)
    {
        if (string.IsNullOrWhiteSpace(fullTypeName))
        {
            return "UnknownType";
        }

        StringBuilder sb = new StringBuilder(fullTypeName.Length);
        foreach (char c in fullTypeName)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('_');
            }
        }
        return sb.ToString();
    }

    public static string GetMermaidDagForCallers(IEnumerable<InvocationMethod> methods, bool writeAllMethods)
    {
        var sb = new StringBuilder();
        HashSet<string> written = new();
        sb.AppendLine("```mermaid");
        sb.AppendLine("classDiagram");
        HashSet<string> lines = new();
        foreach (var method in methods)
        {
            var sourceClass = method.MethodSymbol.ContainingType.Name;
            var sourceMethodName = method.MethodSymbol.Name;
            foreach (var caller in method.Callers)
            {
                var newMethod = caller.MethodSymbol;
                var line = $"{sourceClass} --|> {newMethod.ContainingType.Name} : {newMethod.Name}";
                if (writeAllMethods)
                {
                    line = $"{sourceClass} --|> {newMethod.ContainingType.Name}";
                }
                if (lines.Add(line))
                {
                    sb.AppendLine(line);
                }
            }
        }

        sb.AppendLine("```");
        return sb.ToString();
    }
}
