using Microsoft.CodeAnalysis;

namespace RoslynRunner.Utilities.InvocationTrees;

internal sealed class InvocationPathBuilder
{
    private readonly Dictionary<IMethodSymbol, (InvocationMethod Method, IMethodSymbol? Parent)> _paths =
        new(SymbolEqualityComparer.Default);

    public InvocationPathBuilder(InvocationRoot root, HashSet<IMethodSymbol> validMethods)
    {
        var queue = new Queue<InvocationMethod>();
        foreach (var method in root.Methods.Where(m => validMethods.Contains(m.MethodSymbol)))
        {
            if (_paths.TryAdd(method.MethodSymbol, (method, null)))
            {
                queue.Enqueue(method);
            }
        }

        // Breadth-first traversal selects one shortest path and terminates on cycles.
        while (queue.TryDequeue(out var method))
        {
            foreach (var child in method.InvokedMethods.Values.Concat(method.Implementations))
            {
                if (validMethods.Contains(child.MethodSymbol) &&
                    _paths.TryAdd(child.MethodSymbol, (child, method.MethodSymbol)))
                {
                    queue.Enqueue(child);
                }
            }
        }
    }

    public List<InvocationMethod> CreatePath(InvocationMethod caller, InvocationMethod matchingCall)
    {
        if (!_paths.ContainsKey(caller.MethodSymbol))
        {
            return [];
        }

        var path = new List<InvocationMethod>();
        IMethodSymbol? current = caller.MethodSymbol;
        while (current != null)
        {
            var entry = _paths[current];
            path.Add(entry.Method);
            current = entry.Parent;
        }
        path.Reverse();
        path.Add(matchingCall);

        // Copy only the selected edges. Keeping the original nodes would expose
        // sibling calls, cross-links, and recursive calls to the diagram writers.
        var copies = path.Select(m => new InvocationMethod(m.MethodSymbol, [], [], [])).ToList();
        for (var i = 0; i < path.Count - 1; i++)
        {
            var next = copies[i + 1];
            next.Callers.Add(copies[i]);
            var invocation = path[i].InvokedMethods.FirstOrDefault(pair =>
                SymbolEqualityComparer.Default.Equals(pair.Value.MethodSymbol, next.MethodSymbol));
            if (invocation.Key != null)
            {
                copies[i].InvokedMethods.Add(invocation.Key, next);
            }
            else
            {
                copies[i].Implementations.Add(next);
            }
        }
        return copies;
    }
}
