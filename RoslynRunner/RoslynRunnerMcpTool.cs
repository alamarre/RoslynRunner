using System;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RoslynRunner.SolutionProcessors;
using RoslynRunner.Utilities.InvocationTrees;

namespace RoslynRunner;

[McpServerToolType]
public static class RoslynRunnerMcpTool
{
    [McpServerTool]
    [Description("Loads a solution to be cached for future analysis")]
    public static async Task<string> LoadSolution(
        IRunQueue queue,
        CommandRunningService commandRunningService,
        [Description("The absolute path of the project or solution to load")] string solutionPath,
        [Description("Whether to preload symbols in the cache")] bool cacheSymbols,
        [Description("The name of the project to cache symbols from")] string? projectName,
        [Description("The maximumum time to run in seconds")] int maxTime = 3600,
        CancellationToken cancellationToken = default)
    {
        var context = new RunCommand(
            PrimarySolution: solutionPath,
            PersistSolution: true,
            ProcessorSolution: null,
            ProcessorName: "SolutionLoader",
            AssemblyLoadContextPath: null,
            Context: null);
        Guid runId = await queue.Enqueue(context, cancellationToken);

        var result = await commandRunningService.WaitForTaskAsync(runId, TimeSpan.FromSeconds(maxTime), cancellationToken);
        return JsonSerializer.Serialize(result.Value);
    }

    [McpServerTool]
    [Description("Run a processor on a target project")]
    public static async Task<string> RunProcessor(
        IRunQueue queue,
        CommandRunningService commandRunningService,
        [Description("The absolute path of the project to analyze, or the solution which contains it")] string targetProjectPath,
        [Description("The absolute path to the csproj for the solution processor")] string processorProjectPath,
        [Description("The fully qualified domain name of the processor class")] string processorFqdn,
        [Description("The maximumum time for the processor to run in seconds")] int maxTime = 3600,
        CancellationToken cancellationToken = default)
    {
        var context = new RunCommand(
            PrimarySolution: targetProjectPath,
            PersistSolution: false,
            ProcessorSolution: processorProjectPath,
            ProcessorName: processorFqdn,
            ProcessorProjectName: null,
            AssemblyLoadContextPath: null,
            Context: null);
        Guid runId = await queue.Enqueue(context, cancellationToken);

        var result = await commandRunningService.WaitForTaskAsync(runId, TimeSpan.FromSeconds(maxTime), cancellationToken);
        return JsonSerializer.Serialize(result.Value);
    }

    [McpServerTool]
    [Description("Create a diagram of the calls made by a method, or all methods in a type")]
    public static async Task<string> CreateDiagram(
        IRunQueue queue,
        CommandRunningService commandRunningService,
        [Description("The absolute path solution file to analyze")] string targetSolutionPath,
        [Description("The fully qualified name of the type to analyze")] string typeName,
        [Description("Folder to save the diagram in")] string outputPath,
        [Description("The name, minus the extension, of the diagram file")] string diagramName,
        [Description("The maximum time for the processor to run in seconds")] int maxTime = 300,
        [Description("The name of the method to start with, if null all methods of the type will be analyzed")] string? methodName = null,
        CancellationToken cancellationToken = default)
    {
        var context = new RunCommand(
            PrimarySolution: targetSolutionPath,
            PersistSolution: false,
            ProcessorSolution: null,
            ProcessorName: "CallChains",
            AssemblyLoadContextPath: null,
            Context: JsonSerializer.Serialize(new InvocationTreeProcessorParameters(
                StartingSymbol: typeName,
                Diagrams: [
                    new InvocationDiagram(
                        OutputPath: outputPath,
                        Name: diagramName,
                        DiagramType: "d3",
                        SeparateDiagrams: false,
                        InclusivePruneFilter: null,
                        WriteAllMethods: true,
                        Filter: null)
                ],
                UseCache: true,
                MethodFilter: methodName)));
        Guid runId = await queue.Enqueue(context, cancellationToken);

        var result = await commandRunningService.WaitForTaskAsync(runId, TimeSpan.FromSeconds(maxTime), cancellationToken);
        return JsonSerializer.Serialize(result.Value);
    }

    [McpServerTool]
    [Description("Run an analyzer on a target project")]
    public static async Task<string> RunAnalyzer(
        IRunQueue queue,
        CommandRunningService commandRunningService,
        [Description("The absolute path of the project to analyze, or the solution which contains it")] string targetProjectPath,
        [Description("The name of the target project")] string targetProjectName,
        [Description("The absolute path to the analyzer project")] string analyzerProjectPath,
        [Description("The fully qualified name of the analyzer")] string analyzerName,
        CancellationToken cancellationToken = default)
    {
        var context = new RunCommand<AnalyzerContext>(
            PrimarySolution: targetProjectPath,
            PersistSolution: false,
            ProcessorName: "AnalyzerRunner",
            AssemblyLoadContextPath: null,
            Context: new AnalyzerContext(analyzerProjectPath,
            targetProjectName,
            new List<string> { analyzerName })
        );
        Guid runId = await queue.Enqueue(context.ToRunCommand(), cancellationToken);

        var result = await commandRunningService.WaitForTaskAsync(runId, TimeSpan.FromSeconds(120), cancellationToken);
        return JsonSerializer.Serialize(result.Value);
    }

    [McpServerTool]
    [Description("Convert synchronous call chains to async")]
    public static async Task<string> ConvertSyncToAsync(
        IRunQueue queue,
        CommandRunningService commandRunningService,
        [Description("The absolute path to the target solution")] string targetSolution,
        [Description("The absolute path to the repository containing the solution")] string repositoryPath,
        [Description("The fully qualified name of the type to convert")] string typeName,
        [Description("The branch name that should contain the async changes")] string branchName,
        [Description("Optional starting method name")] string? methodName = null,
        [Description("Optional commit message for the generated branch")] string? commitMessage = null,
        int maxTime = 300,
        CancellationToken cancellationToken = default)
    {
        var context = new RunCommand(
            PrimarySolution: targetSolution,
            PersistSolution: false,
            ProcessorSolution: null,
            ProcessorName: "AsyncConverter",
            AssemblyLoadContextPath: null,
            Context: JsonSerializer.Serialize(new AsyncConversionParameters(
                repositoryPath,
                typeName,
                branchName,
                methodName,
                ChangeId: null,
                CommitMessage: commitMessage)));

        Guid runId = await queue.Enqueue(context, cancellationToken);
        var result = await commandRunningService.WaitForTaskAsync(runId, TimeSpan.FromSeconds(maxTime), cancellationToken);
        return JsonSerializer.Serialize(result.Value);
    }
}
