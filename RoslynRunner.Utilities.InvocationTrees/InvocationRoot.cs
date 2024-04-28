using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace RoslynRunner.Utilities.InvocationTrees;

public record InvocationRoot(
    List<InvocationMethod> Methods
    );

public record InvocationMethod(
    IMethodSymbol MethodSymbol,
	List<InvocationMethod> Callers,
	List<InvocationMethod> Implementations,
    Dictionary<IInvocationOperation, InvocationMethod> InvokedMethods
);

public record TransformedInvocationMethod<T>(T TransformedValue, InvocationMethod InvocationMethod);
