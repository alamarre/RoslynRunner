using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using RoslynRunner.Core;
using RoslynRunner.Core.Extensions;
using RoslynRunner.Core.QueueProcessing;

namespace RoslynRunner.Utilities.InvocationTrees;

public static class InvocationTreeBuilder
{
    public static async Task<(InvocationRoot, List<InvocationMethod>)> BuildInvocationTreeAsync(INamedTypeSymbol startingType, Solution solution, CancellationToken cancellationToken = default)
    {
        Dictionary<IMethodSymbol, InvocationMethod> methodCache = new Dictionary<IMethodSymbol, InvocationMethod>(SymbolEqualityComparer.Default);
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
            var methodNodes = node.DescendantNodes().OfType<MethodDeclarationSyntax>();
            var methodSymbols = methodNodes.Select(m => rootModel.GetDeclaredSymbol(m, cancellationToken)).Cast<IMethodSymbol>();
            var initialMethods = methodSymbols.Select(m =>
                new InvocationMethod(m, new List<InvocationMethod>(), new List<InvocationMethod>(), new Dictionary<IInvocationOperation, InvocationMethod>())).ToArray();

            var allMethods = await DedupingQueueRunner.ProcessResultsAsync<InvocationMethod>(async (InvocationMethod method) =>
            {
                List<InvocationMethod> newMethods = new List<InvocationMethod>();
                var methodSymbol = method.MethodSymbol;
                if (await methodSymbol.Locations.First().GetSyntaxNodeAsync(cancellationToken) 
                    is not MethodDeclarationSyntax currentMethodNode)
                {
                    return newMethods;
                }


                // TODO: make this conditional
                /*if(solution.GetProject(currentMethodNode)?.AssemblyName != rootModel.Compilation.AssemblyName)
                {
                    return newMethods;
                }*/

                var implementations = (await SymbolFinder.FindImplementationsAsync(methodSymbol, solution, cancellationToken: cancellationToken)).ToArray();
                if(implementations.Any() && implementations.Length < 6 )
                {
                    foreach (var implementation in implementations)
                    {
						var implementationSymbol = implementation as IMethodSymbol;
                        if (implementationSymbol == null)
                        {
							continue;
						}

                        if (!methodCache.TryGetValue(implementationSymbol, out InvocationMethod? invocationMethod))
                        {
							invocationMethod = new InvocationMethod(implementationSymbol,
							new List<InvocationMethod> { method },
							new List<InvocationMethod>(),
							new Dictionary<IInvocationOperation, InvocationMethod>());
							methodCache.Add(implementationSymbol, invocationMethod);
							newMethods.Add(invocationMethod);
						} else
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
                if(model == null)
                {
					return newMethods;
				}
                var methodOperation = model.GetOperation(methodBodyNode, cancellationToken);
                var operations = methodOperation.Descendants().OfType<IInvocationOperation>();
                Dictionary<IInvocationOperation, InvocationMethod> invocationMethods =
                    new Dictionary<IInvocationOperation, InvocationMethod>();
                foreach (var operation in operations)
                {
                    if (!methodCache.TryGetValue(operation.TargetMethod, out InvocationMethod? invocationMethod))
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
}