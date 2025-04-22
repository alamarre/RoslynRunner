using D2L.RoslynRunner.Processors;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.Extensions.Logging;
using RoslynRunner.Core;
using RoslynRunner.Core.Caching;
using RoslynRunner.Core.Extensions;
using RoslynRunner.Core.QueueProcessing;

namespace RoslynRunner.Utilities.InvocationTrees;

public static class InvocationTreeBuilder
{
    public static async Task<(InvocationRoot, List<InvocationMethod>)> BuildInvocationTreeAsync(
        ISymbol startingType,
        Solution solution,
        string? methodFilter = null,
        int? maxLimit = null,
        Dictionary<IMethodSymbol, InvocationMethod>? startingMethodCache = null,
        CancellationToken cancellationToken = default)
    {
        Dictionary<IMethodSymbol, InvocationMethod> methodCache = startingMethodCache ?? new(SymbolEqualityComparer.Default);
        foreach (var location in startingType.Locations)
        {
            if (!location.IsInSource)
            {
                continue;
            }

            var node = await location.GetSyntaxNodeAsync(cancellationToken);
            if (node == null)
            {
                continue;
            }

            var rootModel = await solution.GetModel(node, cancellationToken);
            if (rootModel == null)
            {
                continue;
            }

            var methodNodes = node.DescendantNodesAndSelf().OfType<MethodDeclarationSyntax>();
            if (methodFilter != null)
            {
                methodNodes = methodNodes.Where(m => m.Identifier.ValueText == methodFilter);
            }
            var methodSymbols = methodNodes.Select(m => rootModel.GetDeclaredSymbol(m, cancellationToken))
                .Cast<IMethodSymbol>();
            var initialMethods = methodSymbols.Select(m =>
                new InvocationMethod(m, new List<InvocationMethod>(), new List<InvocationMethod>(),
                    new Dictionary<IInvocationOperation, InvocationMethod>())).ToArray();

            var allMethods = await DedupingQueueRunner.ProcessResultsAsync<InvocationMethod>(
                async (InvocationMethod method) =>
                {
                    List<InvocationMethod> newMethods = new();
                    var methodSymbol = method.MethodSymbol;
                    if (await methodSymbol.Locations.First().GetSyntaxNodeAsync(cancellationToken)
                        is not MethodDeclarationSyntax currentMethodNode)
                    {
                        return newMethods;
                    }

                    var implementations =
                        (await CachingSymbolFinder.FindImplementationsAsync(methodSymbol, solution,
                            cancellationToken: cancellationToken)).ToArray();
                    if (implementations.Any() && (maxLimit is null || implementations.Length < maxLimit))
                    {
                        foreach (var implementation in implementations)
                        {
                            var implementationSymbol = implementation as IMethodSymbol;
                            if (implementationSymbol == null)
                            {
                                continue;
                            }

                            if (!methodCache.TryGetValue(implementationSymbol, out var invocationMethod))
                            {
                                invocationMethod = new InvocationMethod(implementationSymbol,
                                    new List<InvocationMethod> { method },
                                    new List<InvocationMethod>(),
                                    new Dictionary<IInvocationOperation, InvocationMethod>());
                                methodCache.Add(implementationSymbol, invocationMethod);
                                newMethods.Add(invocationMethod);
                            }
                            else
                            {
                                invocationMethod.Callers.Add(method);
                            }

                            method.Implementations.Add(invocationMethod);
                        }
                    }

                    SyntaxNode? methodBodyNode = currentMethodNode.Body;
                    if (methodBodyNode == null)
                    {
                        methodBodyNode = currentMethodNode.ExpressionBody?.Expression;
                    }

                    if (methodBodyNode == null)
                    {
                        return newMethods;
                    }

                    var model = await solution.GetModel(methodBodyNode, cancellationToken);
                    if (model == null)
                    {
                        return newMethods;
                    }

                    var methodOperation = model.GetOperation(methodBodyNode, cancellationToken);
                    var operations = methodOperation.Descendants().OfType<IInvocationOperation>();
                    Dictionary<IInvocationOperation, InvocationMethod> invocationMethods = new();
                    foreach (var operation in operations)
                    {
                        if (!methodCache.TryGetValue(operation.TargetMethod, out var invocationMethod))
                        {
                            invocationMethod = new InvocationMethod(operation.TargetMethod,
                                new List<InvocationMethod> { method },
                                new List<InvocationMethod>(),
                                new Dictionary<IInvocationOperation, InvocationMethod>());
                            methodCache.Add(operation.TargetMethod, invocationMethod);
                            newMethods.Add(invocationMethod);
                        }
                        else
                        {
                            invocationMethod.Callers.Add(method);
                        }

                        method.InvokedMethods.Add(operation, invocationMethod);
                    }

                    return newMethods;
                }, initialMethods);

            return (new InvocationRoot(initialMethods.ToList()), allMethods.ToList());
        }

        return (new InvocationRoot(new List<InvocationMethod>()), new List<InvocationMethod>());
    }

    public static async Task<(InvocationRoot, List<InvocationMethod>)> BuildInvocationTreeWithCacheAsync(
        CachedSymbolFinder cachedSymbolFinder,
        ISymbol startingType,
        Solution solution,
        string? methodFilter = null,
        int? maxLimit = null,
        Dictionary<IMethodSymbol, InvocationMethod>? startingMethodCache = null,
        CancellationToken cancellationToken = default)
    {
        Dictionary<IMethodSymbol, InvocationMethod> methodCache = startingMethodCache ?? new(SymbolEqualityComparer.Default);
        var methodSymbols = cachedSymbolFinder.SymbolCache.MethodCache.Keys.Where(m => m.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == startingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

        if (methodFilter != null)
        {
            methodSymbols = methodSymbols.Where(m => m.Name == methodFilter);
        }
        var initialMethods = methodSymbols.Select(m =>
            new InvocationMethod(m, new List<InvocationMethod>(), new List<InvocationMethod>(),
                new Dictionary<IInvocationOperation, InvocationMethod>())).ToArray();

        var allMethods = await DedupingQueueRunner.ProcessResultsAsync<InvocationMethod>(
            async (InvocationMethod method) =>
            {
                List<InvocationMethod> newMethods = new();
                var methodSymbol = method.MethodSymbol;

                if (await methodSymbol.Locations.First().GetSyntaxNodeAsync(cancellationToken)
                    is not MethodDeclarationSyntax currentMethodNode)
                {
                    return newMethods;
                }
                if (cachedSymbolFinder.SymbolCache.ImplementationCache.TryGetValue(methodSymbol, out var implementations)
                 && implementations is not null
                 && (maxLimit is null || implementations.Count() < maxLimit))
                {
                    foreach (var implementationSymbol in implementations)
                    {
                        if (!methodCache.TryGetValue(implementationSymbol, out var invocationMethod))
                        {
                            invocationMethod = new InvocationMethod(implementationSymbol,
                                new List<InvocationMethod> { method },
                                new List<InvocationMethod>(),
                                new Dictionary<IInvocationOperation, InvocationMethod>());
                            methodCache.Add(implementationSymbol, invocationMethod);
                            newMethods.Add(invocationMethod);
                        }
                        else
                        {
                            invocationMethod.Callers.Add(method);
                        }

                        method.Implementations.Add(invocationMethod);
                    }
                }

                var operations = cachedSymbolFinder.SymbolCache.MethodCache[methodSymbol].InvokedOperations.OfType<IInvocationOperation>().ToArray();
                Dictionary<IInvocationOperation, InvocationMethod> invocationMethods = new();
                foreach (var operation in operations)
                {
                    if (!methodCache.TryGetValue(operation.TargetMethod, out var invocationMethod))
                    {
                        invocationMethod = new InvocationMethod(operation.TargetMethod,
                            new List<InvocationMethod> { method },
                            new List<InvocationMethod>(),
                            new Dictionary<IInvocationOperation, InvocationMethod>());
                        methodCache.Add(operation.TargetMethod, invocationMethod);
                        newMethods.Add(invocationMethod);
                    }
                    else
                    {
                        invocationMethod.Callers.Add(method);
                    }

                    method.InvokedMethods.Add(operation, invocationMethod);
                }

                return newMethods;
            }, initialMethods);

        return (new InvocationRoot(initialMethods.ToList()), allMethods.ToList());
    }

    /// <summary>
    /// Provides a way to build an invocation tree by walking through callers plus additional metadata for transforming the nodes. 
    /// </summary>
    /// <typeparam name="T">The type for storing metadata to transform the methods and interfaces</typeparam>
    /// <param name="methodSymbols">The starting methods to crawl</param>
    /// <param name="solution">The solution</param>
    /// <param name="transformer">The method of providing metadata and filtering results. Return null to exclude a given method.</param>
    /// <param name="interfaceTransformer">The method of providing metadata and filtering results for interfaces. Return null to exclude a given method.</param>
    /// <param name="logger">Logger for providing run information, most useful for tracking queue processing state.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns></returns>
    public static async Task<IEnumerable<TransformedInvocationMethod<T>>>
        BuildTransformedInvocationTreeFromCallersAsync<T>(
            IEnumerable<IMethodSymbol> methodSymbols,
            Solution solution,
            Func<IMethodSymbol, IInvocationOperation?, TransformedInvocationMethod<T>?, T?> transformer,
            Func<IMethodSymbol, TransformedInvocationMethod<T>?, T?>? interfaceTransformer = null,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
    {
        Dictionary<IMethodSymbol, TransformedInvocationMethod<T>> methodCache = new(SymbolEqualityComparer.Default);
        var rootMethods = methodSymbols.Select(m =>
            {
                var transformed = transformer(m, null, null);
                if (transformed == null)
                {
                    return null;
                }

                return new TransformedInvocationMethod<T>(transformed,
                    new InvocationMethod(m!,
                        new List<InvocationMethod>(),
                        new List<InvocationMethod>(),
                        new Dictionary<IInvocationOperation, InvocationMethod>()));
            })
            .Where(t => t != null)
            .Cast<TransformedInvocationMethod<T>>()
            .ToArray();

        var allMethods = await DedupingQueueRunner.ProcessResultsAsync<TransformedInvocationMethod<T>>(
            async (TransformedInvocationMethod<T> method) =>
            {
                List<TransformedInvocationMethod<T>> newMethods = new();
                var methodSymbol = method.InvocationMethod.MethodSymbol;

                if (interfaceTransformer != null)
                {
                    var implementedInterfacMethods = methodSymbol.FindImplementedInterfaceMethods();
                    foreach (var interfaceMethod in implementedInterfacMethods)
                    {
                        if (interfaceMethod == null)
                        {
                            continue;
                        }


                        if (!methodCache.TryGetValue(interfaceMethod,
                                out TransformedInvocationMethod<T>? interfaceTransformedInvocation))
                        {
                            var interfaceTransformed = interfaceTransformer(interfaceMethod, method);
                            if (interfaceTransformed == null)
                            {
                                continue;
                            }

                            interfaceTransformedInvocation = new TransformedInvocationMethod<T>(
                                interfaceTransformed,
                                new InvocationMethod(interfaceMethod,
                                    new List<InvocationMethod>(),
                                    new List<InvocationMethod> { method.InvocationMethod },
                                    new Dictionary<IInvocationOperation, InvocationMethod>()));

                            methodCache.Add(interfaceMethod, interfaceTransformedInvocation);
                            newMethods.Add(interfaceTransformedInvocation);
                        }
                        else
                        {
                            interfaceTransformedInvocation.InvocationMethod.Implementations
                                .Add(method.InvocationMethod);
                        }
                    }
                }

                var callers = (await CachingSymbolFinder.FindCallersAsync(methodSymbol, solution, cancellationToken))
                    .ToArray();
                if (callers.Any())
                {
                    foreach (var caller in callers)
                    {
                        foreach (var location in caller.Locations.Where(l => l.IsInSource
                                                                             && !l.SourceTree.FilePath.EndsWith(
                                                                                 ".generated.cs")
                                 ))
                        {
                            if (!location.IsInSource)
                            {
                                continue;
                            }

                            var node = await location.GetSyntaxNodeAsync(cancellationToken);
                            if (node == null)
                            {
                                continue;
                            }

                            SyntaxNode? currentMethodNode = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                            if (currentMethodNode == null)
                            {
                                currentMethodNode = node.FirstAncestorOrSelf<ConstructorDeclarationSyntax>();
                            }

                            if (currentMethodNode == null)
                            {
                                continue;
                            }

                            var callerModel = await solution.GetModel(currentMethodNode, cancellationToken);
                            if (callerModel == null)
                            {
                                continue;
                            }

                            var callerMethodSymbol = callerModel.GetDeclaredSymbol(currentMethodNode, cancellationToken);
                            if (callerMethodSymbol is not IMethodSymbol implementationSymbol)
                            {
                                continue;
                            }

                            var invocationNode = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
                            if (invocationNode == null)
                            {
                                continue;
                            }

                            var operation =
                                callerModel!.GetOperation(invocationNode!, cancellationToken) as IInvocationOperation;
                            if (operation == null)
                            {
                                continue;
                            }

                            if (!methodCache.TryGetValue(implementationSymbol,
                                    out TransformedInvocationMethod<T>? transformedMethod))
                            {
                                var invocationMethod = new InvocationMethod(
                                    implementationSymbol,
                                    new List<InvocationMethod>(),
                                    new List<InvocationMethod>(),
                                    new Dictionary<IInvocationOperation, InvocationMethod>
                                    {
                                        { operation!, method.InvocationMethod }
                                    }
                                );
                                var transformed = transformer(implementationSymbol, operation, method);
                                if (transformed == null)
                                {
                                    continue;
                                }

                                transformedMethod = new TransformedInvocationMethod<T>(transformed, invocationMethod);
                                methodCache.Add(implementationSymbol, transformedMethod);
                                newMethods.Add(transformedMethod);
                            }
                            else
                            {
                                transformedMethod.InvocationMethod.InvokedMethods.Add(operation!, method.InvocationMethod);
                            }

                            method.InvocationMethod.Callers.Add(transformedMethod.InvocationMethod);
                        }
                    }
                }

                return newMethods;
            }, 1000 * 100, logger, rootMethods);


        return allMethods;
    }
}
