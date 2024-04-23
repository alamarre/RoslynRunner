using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynRunner.Core;
using RoslynRunner.SolutionProcessors;

namespace RoslynRunner;

public class RunCommandProcessor(ILogger<RunCommandProcessor> logger, ILoggerFactory loggerFactory)
{
    private readonly Dictionary<string, Solution> _persistentSolutions = new Dictionary<string, Solution>();

	MethodInfo? processMethod = typeof(RunCommandProcessor).GetMethod(nameof(ProcessInstance), BindingFlags.NonPublic | BindingFlags.Instance);



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
            MSBuildWorkspace workspace = MSBuildWorkspace.Create();
            solution = await CompilationTools.GetSolution(workspace, runCommand.PrimarySolution, logger);
            if (runCommand.PersistSolution)
            {
                _persistentSolutions.Add(runCommand.PrimarySolution, solution);
            }
        }

        ISolutionProcessor? processor = null;
        TestAssemblyLoadContext? loadContext = null;


        if (runCommand.ProcessorSolution != null && runCommand.ProcessorProjectName != null)
        {
            MSBuildWorkspace processorWorkspace = MSBuildWorkspace.Create();
            Solution processorSolution =
                await CompilationTools.GetSolution(processorWorkspace, runCommand.ProcessorSolution, null);
            Project? project =
                processorSolution.Projects.FirstOrDefault(p => p.Name == runCommand.ProcessorProjectName);
            if (project == null)
            {
                throw new Exception("project does not exist");
            }

            var compilation = await project.GetCompilationAsync(cancellationToken);

            if (compilation == null)
            {
                throw new Exception();
            }

            loadContext = new TestAssemblyLoadContext(runCommand.AssemblyLoadContextPath);
            Assembly? assembly = CompilationTools.GetAssembly(compilation, loadContext);
            if (assembly == null)
            {
                throw new Exception();
            }

            var instance = assembly.CreateInstance(runCommand.ProcessorName);
            processor = instance as ISolutionProcessor;
            if (processor == null && instance != null)
            {
                Type type = instance.GetType();
                foreach (Type interfaceType in type.GetInterfaces())
                {
                    if (interfaceType.IsGenericType && interfaceType.GetGenericTypeDefinition() == typeof(ISolutionProcessor<>))
                    {
                        Type[] typeArguments = interfaceType.GetGenericArguments();
                        Type typeArgument = typeArguments[0];
                        var genericMethod = processMethod?.MakeGenericMethod(typeArgument);
                        object? result = genericMethod?.Invoke(this, new object?[] { instance, solution, runCommand.Context, cancellationToken });
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
            else
            {
                if (runCommand.ProcessorName == nameof(AnalyzerRunner))
                {
                    processor = new AnalyzerRunner();
                }
            }
        }
        if (processor == null)
        {
			throw new Exception("no processor found");
        }

        await processor.ProcessSolution(solution, runCommand.Context, cancellationToken);
        logger.LogInformation("run command processed");
		loadContext?.Unload();
	}

    private async Task ProcessInstance<T>(ISolutionProcessor<T> instance, Solution solution, string? context, CancellationToken cancellationToken = default)
    {
       T? contextData = context == null ? default : JsonSerializer.Deserialize<T>(context);
       await instance.ProcessSolution(solution, contextData, loggerFactory.CreateLogger(instance.GetType().Name), cancellationToken);
    }
}