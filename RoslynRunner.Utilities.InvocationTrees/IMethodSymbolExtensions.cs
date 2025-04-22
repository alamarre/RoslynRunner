using System;
using System.Text;
using Microsoft.CodeAnalysis;

namespace RoslynRunner.Utilities.InvocationTrees;

public static class IMethodSymbolExtensions
{
    public static string GetMethodDisplayName(this IMethodSymbol symbol)
    {
        return symbol.MethodKind switch
        {
            MethodKind.Constructor => ".ctor",
            MethodKind.StaticConstructor => ".cctor",
            // Add cases for operators (op_Implicit, op_Addition), conversions, etc. if needed
            _ => symbol.Name
        };
    }

    public static string GenerateMethodSignature(this IMethodSymbol symbol, bool includeModifiers = false)
    {
        var builder = new StringBuilder();
        builder.Append(GetMethodDisplayName(symbol)); // Use the helper for name

        // Append generic type arguments if present
        if (symbol.IsGenericMethod)
        {
            builder.Append("<");
            // Use FullyQualifiedFormat for type arguments
            builder.Append(string.Join(", ", symbol.TypeArguments.Select(ta => ta.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))));
            builder.Append(">");
        }

        builder.Append("(");
        // Append parameters with modifiers (ref, out, in) and fully qualified types
        builder.Append(string.Join(", ", symbol.Parameters.Select(p =>
        {
            string paramStr = includeModifiers ? p.RefKind switch
            {
                RefKind.Out => "out ",
                RefKind.Ref => "ref ",
                RefKind.In => "in ",
                _ => ""
            } : "";
            // Use FullyQualifiedFormat for parameter types
            paramStr += p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return paramStr;
        })));
        builder.Append(")");

        return builder.ToString();
    }
}
