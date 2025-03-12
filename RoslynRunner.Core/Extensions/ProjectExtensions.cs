using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RoslynRunner.Core.Extensions;

public record SourceLocation(SemanticModel Model, Location Location);

public record MethodData(
    SourceLocation[] Locations,
    IOperation[] InvokedOperations);

public record SymbolCache(
    Dictionary<IMethodSymbol, MethodData> MethodCache,
    Dictionary<IMethodSymbol, List<IMethodSymbol>> ImplementationCache,
    Dictionary<ISymbol, List<ISymbol>> TypeImplementations,
    Dictionary<string, INamedTypeSymbol> MetadataNameCache,
    Dictionary<IOperation, List<IMethodSymbol>> CallerCache);

public static class ProjectExtensions
{
    public static async Task<SymbolCache> BuildSymbolCacheAsync(
        this Project startingProject,
        Func<Document, bool>? filterFunction = null,
        CancellationToken cancellationToken = default)
    {
        Dictionary<IMethodSymbol, MethodData> methodCache = new Dictionary<IMethodSymbol, MethodData>(SymbolEqualityComparer.Default);
        Dictionary<IMethodSymbol, List<IMethodSymbol>> implementationCache = new Dictionary<IMethodSymbol, List<IMethodSymbol>>(SymbolEqualityComparer.Default);
        Dictionary<ISymbol, List<ISymbol>> typeImplementations = new Dictionary<ISymbol, List<ISymbol>>(SymbolEqualityComparer.Default);
        Dictionary<IOperation, List<IMethodSymbol>> callerCache = new Dictionary<IOperation, List<IMethodSymbol>>();
        Dictionary<string, INamedTypeSymbol> metadataNameCache = new Dictionary<string, INamedTypeSymbol>();
        foreach (Project project in startingProject.Solution.Projects)
        {
            Compilation? compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            // Use global namespace traversal to get all types.
            List<INamedTypeSymbol> allTypes = GetAllTypes(compilation);
            foreach(var type in allTypes)
            {
                metadataNameCache[type.ContainingNamespace+"."+type.MetadataName] = type;
            }

            // Process each type to populate typeImplementations and process its methods.
            foreach (INamedTypeSymbol namedType in allTypes)
            {
                // Map implemented interfaces.
                IEnumerable<ISymbol> interfaceSymbols = namedType.AllInterfaces;
                foreach (ISymbol interfaceSymbol in interfaceSymbols)
                {
                    List<ISymbol>? implementations;
                    if (!typeImplementations.TryGetValue(interfaceSymbol, out implementations))
                    {
                        implementations = new List<ISymbol>();
                        typeImplementations.Add(interfaceSymbol, implementations);
                    }
                    implementations.Add(namedType);
                }

                // Map base type if applicable.
                if (namedType.BaseType is not null && namedType.BaseType.SpecialType != SpecialType.System_Object)
                {
                    ISymbol baseTypeSymbol = namedType.BaseType;
                    List<ISymbol>? baseImplementations;
                    if (!typeImplementations.TryGetValue(baseTypeSymbol, out baseImplementations))
                    {
                        baseImplementations = new List<ISymbol>();
                        typeImplementations.Add(baseTypeSymbol, baseImplementations);
                    }
                    baseImplementations.Add(namedType);
                }

                // Process each method declared on the type.
                foreach (ISymbol member in namedType.GetMembers())
                {
                    if (member is not IMethodSymbol methodSymbol)
                    {
                        continue;
                    }

                    // Optionally filter methods based on a document filter.
                    // If a filter is provided, check one syntax reference.
                    var syntaxReferences = methodSymbol.DeclaringSyntaxReferences;
                    if (syntaxReferences.Length == 0)
                    {
                        continue;
                    }

                    SyntaxReference syntaxReference = syntaxReferences[0];
                    SyntaxNode syntaxNode = await syntaxReference.GetSyntaxAsync().ConfigureAwait(false);
                    if (filterFunction is not null)
                    {
                        Document? document = project.GetDocument(syntaxNode.SyntaxTree);
                        if (document is null || !filterFunction(document))
                        {
                            continue;
                        }
                    }

                    // Retrieve the semantic model for the method.
                    SemanticModel semanticModel = compilation.GetSemanticModel(syntaxNode.SyntaxTree);
                    IOperation? methodOperation = semanticModel.GetOperation(syntaxNode);
                    IOperation[] invokedOperations;
                    if (methodOperation is not null)
                    {
                        IEnumerable<IOperation> invocationOps = methodOperation.Descendants();
                        invokedOperations = invocationOps.ToArray();
                    }
                    else
                    {
                        invokedOperations = Array.Empty<IOperation>();
                    }

                    SourceLocation[] sourceLocations = new SourceLocation[]
                    {
                        new SourceLocation(semanticModel, syntaxNode.GetLocation())
                    };

                    MethodData methodData = new MethodData(sourceLocations, invokedOperations);
                    if (!methodCache.ContainsKey(methodSymbol))
                    {
                        methodCache.Add(methodSymbol, methodData);
                    }

                    // Process method overrides.
                    if (methodSymbol.OverriddenMethod is not null)
                    {
                        IMethodSymbol baseMethod = methodSymbol.OverriddenMethod;
                        List<IMethodSymbol>? implementations;
                        if (!implementationCache.TryGetValue(baseMethod, out implementations))
                        {
                            implementations = new List<IMethodSymbol>();
                            implementationCache.Add(baseMethod, implementations);
                        }
                        implementations.Add(methodSymbol);
                    }

                    // Process explicit interface implementations.
                    foreach (IMethodSymbol explicitInterfaceMethod in methodSymbol.ExplicitInterfaceImplementations)
                    {
                        List<IMethodSymbol>? implementations;
                        if (!implementationCache.TryGetValue(explicitInterfaceMethod, out implementations))
                        {
                            implementations = new List<IMethodSymbol>();
                            implementationCache.Add(explicitInterfaceMethod, implementations);
                        }
                        implementations.Add(methodSymbol);
                    }

                    // Process implicit interface implementations.
                    INamedTypeSymbol containingType = methodSymbol.ContainingType;
                    foreach (INamedTypeSymbol interfaceType in containingType.AllInterfaces)
                    {
                        foreach (IMethodSymbol interfaceMember in interfaceType.GetMembers().OfType<IMethodSymbol>())
                        {
                            if (methodSymbol.ExplicitInterfaceImplementations.Contains(interfaceMember))
                            {
                                continue;
                            }
                            IMethodSymbol? implementation = containingType.FindImplementationForInterfaceMember(interfaceMember) as IMethodSymbol;
                            if (implementation is not null && SymbolEqualityComparer.Default.Equals(implementation, methodSymbol))
                            {
                                List<IMethodSymbol>? implementations;
                                if (!implementationCache.TryGetValue(interfaceMember, out implementations))
                                {
                                    implementations = new List<IMethodSymbol>();
                                    implementationCache.Add(interfaceMember, implementations);
                                }
                                implementations.Add(methodSymbol);
                            }
                        }
                    }

                    // Map caller information: record each invocation operation to the method.
                    foreach (IOperation invocationOperation in invokedOperations)
                    {
                        List<IMethodSymbol>? callers;
                        if (!callerCache.TryGetValue(invocationOperation, out callers))
                        {
                            callers = new List<IMethodSymbol>();
                            callerCache.Add(invocationOperation, callers);
                        }
                        callers.Add(methodSymbol);
                    }
                }
            }
        }

        return new SymbolCache(methodCache, implementationCache, typeImplementations, metadataNameCache, callerCache);
    }

    // Helper method to recursively collect all types from the compilation's global namespace.
    private static List<INamedTypeSymbol> GetAllTypes(Compilation compilation)
    {
        List<INamedTypeSymbol> allTypes = new List<INamedTypeSymbol>();
        INamespaceSymbol globalNamespace = compilation.Assembly.GlobalNamespace;
        CollectTypes(globalNamespace, allTypes);
        return allTypes;
    }

    private static void CollectTypes(INamespaceSymbol namespaceSymbol, List<INamedTypeSymbol> allTypes)
    {
        var members = namespaceSymbol.GetMembers();
        foreach (var member in members)
        {
            if (member is INamespaceSymbol nestedNamespace)
            {
                CollectTypes(nestedNamespace, allTypes);
            }
            else if (member is INamedTypeSymbol typeSymbol)
            {
                allTypes.Add(typeSymbol);
                // Also add nested types recursively.
                IEnumerable<INamedTypeSymbol> nestedTypes = typeSymbol.GetTypeMembers();
                foreach (INamedTypeSymbol nestedType in nestedTypes)
                {
                    allTypes.Add(nestedType);
                }
            }
        }
    }
}
