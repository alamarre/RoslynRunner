using System;
using Microsoft.CodeAnalysis;

namespace RoslynRunner.Utilities.InvocationTrees;

public record MethodCallInfo(
    string ContainingTypeFqdn,
    string MethodName,
    string MethodSignature,
    string Identifier,
    List<string> CallerIdentifiers,
    List<string> ImplementationIdentifiers,
    List<string> InvokedMethodIdentifiers
)
{
    private static readonly SymbolDisplayFormat _symbolDisplayFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);
    public static MethodCallInfo FromInvocationMethod(InvocationMethod invocationMethod)
    {
        var methodSymbol = invocationMethod.MethodSymbol;
        var containingTypeFqdn = methodSymbol.ContainingType.ToDisplayString(_symbolDisplayFormat);
        var methodName = methodSymbol.Name;
        var methodSignature = methodSymbol.GenerateMethodSignature(includeModifiers: false);

        var callIds = invocationMethod.Callers.Select(c => c.MethodSymbol.GetMethodId()).ToList();
        var implIds = invocationMethod.Implementations.Select(i => i.MethodSymbol.GetMethodId()).ToList();
        var invokedMethodIds = invocationMethod.InvokedMethods.Select(i => i.Value.MethodSymbol.GetMethodId()).ToList();
        return new MethodCallInfo(
            containingTypeFqdn,
            methodName,
            methodSignature,
            invocationMethod.MethodSymbol.GetMethodId(),
            callIds,
            implIds,
            invokedMethodIds
        );
    }
}
