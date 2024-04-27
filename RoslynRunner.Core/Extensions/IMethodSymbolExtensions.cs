using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;

namespace D2L.RoslynRunner.Processors;



public static class MethodSymbolExtensions
{
	public static List<IMethodSymbol> FindImplementedInterfaceMethods(this IMethodSymbol methodSymbol)
	{
		List<IMethodSymbol> interfaceMethods = new List<IMethodSymbol>();

		INamedTypeSymbol containingType = methodSymbol.ContainingType;

		foreach (INamedTypeSymbol interfaceType in containingType.AllInterfaces) { 
			IEnumerable<ISymbol> interfaceMembers = interfaceType.GetMembers().Where(m => m.Kind == SymbolKind.Method);

			foreach (IMethodSymbol interfaceMethod in interfaceMembers)
			{
				ISymbol? implementedMethod = containingType.FindImplementationForInterfaceMember(interfaceMethod);

				if (methodSymbol.Equals(implementedMethod, SymbolEqualityComparer.Default))
				{
					interfaceMethods.Add(interfaceMethod);
				}
			}
		}

		return interfaceMethods;
	}
}
