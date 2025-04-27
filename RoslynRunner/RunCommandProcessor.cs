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
        logger.LogInformation("processing run command");
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
                        var result = genericMethod?.Invoke(this,
                            new object?[] { instance, solution, runCommand.Context, cancellationToken });
                        if (result is Task task)
                        {
                            await task;
                            logger.LogInformation("run command processed");
                            loadContext?.Unload();
                            return;
                        }
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
        else if (runCommand.ProcessorName == "SolutionLoader")
        {
            processor = new NullActionLoader();
        }

        if (processor == null)
        {
            throw new Exception("no processor found");
        }

        await processor.ProcessSolution(solution, runCommand.Context,
            loggerFactory.CreateLogger(processor.GetType().Name), cancellationToken);
        logger.LogInformation("run command processed");
        loadContext?.Unload();
    }

    private async Task ProcessInstance<T>(ISolutionProcessor<T> instance, Solution solution, string? context,
        CancellationToken cancellationToken = default)
    {
        var contextData = context == null ? default : JsonSerializer.Deserialize<T>(context);
        await instance.ProcessSolution(solution, contextData, loggerFactory.CreateLogger(instance.GetType().Name),
            cancellationToken);
    }
}
