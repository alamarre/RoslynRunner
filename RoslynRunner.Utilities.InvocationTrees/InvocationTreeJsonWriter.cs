using System;
using System.Text.Json;

namespace RoslynRunner.Utilities.InvocationTrees;

public class InvocationTreeJsonWriter
{
    public static string WriteInvocationTreeToJson(IEnumerable<InvocationMethod> methods)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var methodCallInfos = methods.Select(m => MethodCallInfo.FromInvocationMethod(m)).ToList();

        return JsonSerializer.Serialize(methodCallInfos, options);
    }
}
