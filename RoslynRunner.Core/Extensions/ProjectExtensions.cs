using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using RoslynRunner.Core.QueueProcessing;

namespace RoslynRunner.Core.Extensions;

public record SourceLocation(SemanticModel Model, Location Location);

public record MethodData(
    SourceLocation[] Locations,
    IOperation[] InvokedOperations);

public record SymbolCache(
    Dictionary<IMethodSymbol, MethodData> MethodCache,
    Dictionary<string, IMethodSymbol> MethodNameCache,
    Dictionary<IMethodSymbol, List<IMethodSymbol>> ImplementationCache,
    Dictionary<string, List<ISymbol>> TypeImplementations,
    Dictionary<string, INamedTypeSymbol> MetadataNameCache,
    Dictionary<IMethodSymbol, List<IMethodSymbol>> CallerCache);

public static class ProjectExtensions
{
    public static async Task<SymbolCache> BuildSymbolCacheAsync(
        this Solution solution,
        string? projectName,
        CancellationToken cancellationToken = default)
    {
        Dictionary<IMethodSymbol, MethodData> methodCache = new(SymbolEqualityComparer.Default);
        Dictionary<IMethodSymbol, List<IMethodSymbol>> implementationCache = new(SymbolEqualityComparer.Default);
        Dictionary<string, List<ISymbol>> typeImplementations = new();
        Dictionary<IMethodSymbol, List<IMethodSymbol>> callerCache = new();
        Dictionary<string, INamedTypeSymbol> metadataNameCache = new();
        Dictionary<string, IMethodSymbol> methodNameCache = new();

        var projects = solution.Projects;
        if (projectName is not null)
        {
            var startingProject = solution.Projects.FirstOrDefault(p => p.Name == projectName);
            if (startingProject is null)
            {
                throw new ArgumentException($"Project {projectName} not found");
            }
            // get all transitive project references
            var projectReferences = startingProject.ProjectReferences;
            var transitiveReferences = DedupingQueueRunner.ProcessResults<ProjectReference>(projectRef =>
            {
                var project = solution.Projects.FirstOrDefault(p => p.Id == projectRef.ProjectId);
                if (project is null)
                {
                    return Enumerable.Empty<ProjectReference>();
                }
                return project.ProjectReferences;
            }, projectReferences.ToArray());

            projects = transitiveReferences
                .Select(projectRef => solution.GetProject(projectRef.ProjectId))
                .Where(p => p != null)
                .Cast<Project>()
                .ToList();
        }
        foreach (Project project in projects)
        {
            Compilation? compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            // Use global namespace traversal to get all types.
            List<INamedTypeSymbol> allTypes = GetAllTypes(compilation);
            foreach (var type in allTypes)
            {
                metadataNameCache[CachedSymbolFinder.GetFullyQualifiedMetadataName(type)] = type;
            }

            // Process each type to populate typeImplementations and process its methods.
            foreach (INamedTypeSymbol namedType in allTypes)
            {
                // Map implemented interfaces.
                IEnumerable<ISymbol> interfaceSymbols = namedType.AllInterfaces;
                foreach (ISymbol interfaceSymbol in interfaceSymbols)
                {
                    List<ISymbol>? implementations;
                    string key = CachedSymbolFinder.GetFullyQualifiedMetadataName(interfaceSymbol);
                    if (!typeImplementations.TryGetValue(key, out implementations))
                    {
                        implementations = new List<ISymbol>();
                        typeImplementations.Add(key, implementations);
                    }
                    implementations.Add(namedType);
                }

                DedupingQueueRunner.ProcessResults(baseSymbol =>
                {
                    if (baseSymbol?.BaseType is not null && baseSymbol.BaseType.SpecialType != SpecialType.System_Object)
                    {
                        ISymbol baseTypeSymbol = baseSymbol.BaseType;
                        List<ISymbol>? baseImplementations;
                        string key = CachedSymbolFinder.GetFullyQualifiedMetadataName(baseTypeSymbol);
                        if (!typeImplementations.TryGetValue(key, out baseImplementations))
                        {
                            baseImplementations = new List<ISymbol>();
                            typeImplementations.Add(key, baseImplementations);
                        }
                        baseImplementations.Add(baseTypeSymbol);
                        return [baseSymbol.BaseType];
                    }
                    return [];
                }, namedType.BaseType);


                // Process each method declared on the type.
                foreach (IMethodSymbol methodSymbol in namedType.GetMembers().OfType<IMethodSymbol>())
                {
                    methodNameCache[methodSymbol.GetMethodId()] = methodSymbol;
                    var syntaxReferences = methodSymbol.DeclaringSyntaxReferences;
                    if (syntaxReferences.Length == 0)
                    {
                        continue;
                    }

                    SyntaxReference syntaxReference = syntaxReferences[0];
                    SyntaxNode syntaxNode = await syntaxReference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);

                    SemanticModel semanticModel = compilation.GetSemanticModel(syntaxNode.SyntaxTree);
                    IOperation? methodOperation = semanticModel.GetOperation(syntaxNode, cancellationToken);
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

                    // Process interface implementations.
                    INamedTypeSymbol containingType = methodSymbol.ContainingType;
                    foreach (INamedTypeSymbol interfaceType in containingType.AllInterfaces)
                    {
                        foreach (IMethodSymbol interfaceMember in interfaceType.GetMembers().OfType<IMethodSymbol>())
                        {
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
                    foreach (IInvocationOperation invocationOperation in invokedOperations.Where(i => i is IInvocationOperation))
                    {
                        List<IMethodSymbol>? callers;
                        if (!callerCache.TryGetValue(invocationOperation.TargetMethod, out callers))
                        {
                            callers = new List<IMethodSymbol>();
                            callerCache.Add(invocationOperation.TargetMethod, callers);
                        }
                        callers.Add(methodSymbol);
                    }
                }
            }
        }

        return new SymbolCache(methodCache, methodNameCache, implementationCache, typeImplementations, metadataNameCache, callerCache);
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
