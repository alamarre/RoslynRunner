using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;

namespace RoslynRunner.Processors;

public static class MethodSymbolExtensions
{
    public static List<IMethodSymbol> FindImplementedInterfaceMethods(this IMethodSymbol methodSymbol)
    {
        List<IMethodSymbol> interfaceMethods = new();

        var containingType = methodSymbol.ContainingType;

        foreach (var interfaceType in containingType.AllInterfaces)
        {
            IEnumerable<ISymbol> interfaceMembers = interfaceType.GetMembers().Where(m => m.Kind == SymbolKind.Method);

            foreach (IMethodSymbol interfaceMethod in interfaceMembers)
            {
                var implementedMethod = containingType.FindImplementationForInterfaceMember(interfaceMethod);

                if (methodSymbol.Equals(implementedMethod, SymbolEqualityComparer.Default))
                {
                    interfaceMethods.Add(interfaceMethod);
                }
            }
        }

        return interfaceMethods;
    }
}
