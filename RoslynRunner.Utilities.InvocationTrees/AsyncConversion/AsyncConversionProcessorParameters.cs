namespace RoslynRunner.Utilities.InvocationTrees;

public record AsyncConversionParameters(string OutputPath, string TypeName, string? MethodName = null, bool ReplaceExistingMethods = true);
