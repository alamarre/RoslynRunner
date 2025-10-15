using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynRunner.Core;
using RoslynRunner.SolutionProcessors;
using RoslynRunner.Utilities.InvocationTrees;

namespace RoslynRunner;

public class RunCommandProcessor(ILogger<RunCommandProcessor> logger, ILoggerFactory loggerFactory)
{
    private static readonly Meter Meter = new("RoslynRunner.RunCommandProcessor");
    private static readonly Histogram<double> CompilationDurationHistogram = Meter.CreateHistogram<double>(
        "runcommand.compilation.duration",
        unit: "ms",
        description: "Time taken to compile dynamic processors for RunCommand requests.");
    private static readonly Histogram<double> ExecutionDurationHistogram = Meter.CreateHistogram<double>(
        "runcommand.execution.duration",
        unit: "ms",
        description: "Time taken to execute processors for RunCommand requests.");

    private readonly Dictionary<string, Solution> _persistentSolutions = new();

    private MethodInfo? processMethod =
        typeof(RunCommandProcessor).GetMethod(nameof(ProcessInstance), BindingFlags.NonPublic | BindingFlags.Instance);

    private TestAssemblyLoadContext? _assemblyLoadContext;
    public void RemovePersistedSolution(string solution)
    {
        _persistentSolutions.Remove(solution);
    }

    public List<string> GetPersistedSolutions()
    {
        return _persistentSolutions.Keys.ToList();
    }

    public async Task ProcessRunCommand(RunCommand runCommand, CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing run command for {PrimarySolution} using {ProcessorName}",
            runCommand.PrimarySolution,
            runCommand.ProcessorName);
        if (!_persistentSolutions.TryGetValue(runCommand.PrimarySolution, out var solution))
        {
            var workspace = MSBuildWorkspace.Create();
            solution = await CompilationTools.GetSolution(workspace, runCommand.PrimarySolution, logger);
            if (runCommand.PersistSolution)
            {
                _persistentSolutions.Add(runCommand.PrimarySolution, solution);
            }
        }

        ISolutionProcessor? processor = null;
        TestAssemblyLoadContext? loadContext = null;


        if (runCommand.ProcessorSolution != null)
        {
            var compilationStopwatch = Stopwatch.StartNew();
            var processorWorkspace = MSBuildWorkspace.Create();
            var processorSolution =
                await CompilationTools.GetSolution(processorWorkspace, runCommand.ProcessorSolution, null);
            var project =
                processorSolution.Projects.FirstOrDefault(p => p.Name == runCommand.ProcessorProjectName);
            if (project == null && runCommand.ProcessorSolution.EndsWith(".csproj"))
            {
                project = processorSolution.Projects.FirstOrDefault(p => p.FilePath == runCommand.ProcessorSolution);
            }
            if (project == null)
            {
                throw new Exception("project does not exist");
            }

            var compilation = await project.GetCompilationAsync(cancellationToken);

            if (compilation == null)
            {
                throw new Exception();
            }

            AssemblyLoadContext secondaryContext = AssemblyLoadContext.Default;
            if (runCommand.AssemblyLoadContextPath is not null)
            {
                if (runCommand.AssemblyLoadContextPath != _assemblyLoadContext?.LibDirectory)
                {
                    _assemblyLoadContext?.Unload();
                    _assemblyLoadContext = new TestAssemblyLoadContext(runCommand.AssemblyLoadContextPath);
                }
                secondaryContext = _assemblyLoadContext;
            }

            loadContext = new TestAssemblyLoadContext(null, secondaryContext);
            var assembly = CompilationTools.GetAssembly(compilation, loadContext);
            if (assembly == null)
            {
                throw new Exception();
            }

            compilationStopwatch.Stop();
            var compilationDuration = compilationStopwatch.Elapsed;
            RecordCompilationDuration(runCommand, compilationDuration);
            logger.LogInformation(
                "Compiled processor {ProcessorName} from {ProcessorProject} in {CompilationDurationMs} ms",
                runCommand.ProcessorName,
                runCommand.ProcessorProjectName ?? runCommand.ProcessorSolution ?? string.Empty,
                compilationDuration.TotalMilliseconds);

            var instance = assembly.CreateInstance(runCommand.ProcessorName);
            processor = instance as ISolutionProcessor;
            if (processor == null && instance != null)
            {
                var type = instance.GetType();
                foreach (var interfaceType in type.GetInterfaces())
                {
                    if (interfaceType.IsGenericType &&
                        interfaceType.GetGenericTypeDefinition() == typeof(ISolutionProcessor<>))
                    {
                        Type[] typeArguments = interfaceType.GetGenericArguments();
                        var typeArgument = typeArguments[0];
                        var genericMethod = processMethod?.MakeGenericMethod(typeArgument);
                        var runStopwatch = Stopwatch.StartNew();
                        var result = genericMethod?.Invoke(this,
                            new object?[] { instance, solution, runCommand.Context, cancellationToken });
                        if (result is Task task)
                        {
                            await task;
                            runStopwatch.Stop();
                            RecordExecutionDuration(runCommand, runStopwatch.Elapsed);
                            logger.LogInformation(
                                "Processed run command for {PrimarySolution} in {RunDurationMs} ms",
                                runCommand.PrimarySolution,
                                runStopwatch.Elapsed.TotalMilliseconds);
                            loadContext?.Unload();
                            return;
                        }
                        runStopwatch.Stop();
                    }
                }
            }
        }
        else if (runCommand.ProcessorName == nameof(AnalyzerRunner))
        {
            processor = new AnalyzerRunner();
        }
        else if (runCommand.ProcessorName == "CallChains")
        {
            processor = new InvocationTreeProcessor();
        }
        else if (runCommand.ProcessorName == "AsyncConverter")
        {
            processor = new AsyncConversionProcessor();
        }
        else if (runCommand.ProcessorName == "SolutionLoader")
        {
            processor = new NullActionLoader();
        }

        if (processor == null)
        {
            throw new Exception("no processor found");
        }

        var processorLogger = loggerFactory.CreateLogger(processor.GetType().Name);
        var executionStopwatch = Stopwatch.StartNew();
        await processor.ProcessSolution(solution, runCommand.Context,
            processorLogger, cancellationToken);
        executionStopwatch.Stop();
        RecordExecutionDuration(runCommand, executionStopwatch.Elapsed);
        logger.LogInformation("Processed run command for {PrimarySolution} in {RunDurationMs} ms",
            runCommand.PrimarySolution,
            executionStopwatch.Elapsed.TotalMilliseconds);
        loadContext?.Unload();
    }

    private async Task ProcessInstance<T>(ISolutionProcessor<T> instance, Solution solution, string? context,
        CancellationToken cancellationToken = default)
    {
        var contextData = context == null ? default : JsonSerializer.Deserialize<T>(context);
        await instance.ProcessSolution(solution, contextData, loggerFactory.CreateLogger(instance.GetType().Name),
            cancellationToken);
    }

    private static void RecordCompilationDuration(RunCommand runCommand, TimeSpan elapsed)
    {
        var tags = CreateCommonTags(runCommand);
        if (runCommand.ProcessorSolution is { } processorSolution)
        {
            tags.Add("processor.solution", processorSolution);
        }
        if (runCommand.ProcessorProjectName is { } projectName)
        {
            tags.Add("processor.project", projectName);
        }

        CompilationDurationHistogram.Record(elapsed.TotalMilliseconds, tags);
    }

    private static void RecordExecutionDuration(RunCommand runCommand, TimeSpan elapsed)
    {
        var tags = CreateCommonTags(runCommand);
        tags.Add("processor.kind", runCommand.ProcessorSolution is null ? "built-in" : "dynamic");
        ExecutionDurationHistogram.Record(elapsed.TotalMilliseconds, tags);
    }

    private static TagList CreateCommonTags(RunCommand runCommand)
    {
        var tags = new TagList
        {
            { "processor.name", runCommand.ProcessorName },
            { "primary.solution", runCommand.PrimarySolution }
        };

        tags.Add("persist.solution", runCommand.PersistSolution);

        return tags;
    }
}
