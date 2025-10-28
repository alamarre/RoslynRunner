namespace RoslynRunner.Utilities.InvocationTrees;

public record AsyncConversionParameters(
    string RepositoryPath,
    string TypeName,
    string BranchName,
    string? MethodName = null,
    bool ReplaceExistingMethods = true,
    bool RenameTransformedMethods = true,
    string? ChangeId = null,
    string? CommitMessage = null);
