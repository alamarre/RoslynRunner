using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynRunner.Core;
using RoslynRunner.SolutionProcessors;

namespace RoslynRunner;

public class RunCommandProcessor(ILogger<RunCommandProcessor> logger)
{
    private readonly Dictionary<string, Solution> _persistentSolutions = new Dictionary<string, Solution>();

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

            TestAssemblyLoadContext loadContext = new TestAssemblyLoadContext(runCommand.AssemblyLoadContextPath);
            Assembly? assembly = CompilationTools.GetAssembly(compilation, loadContext);
            if (assembly == null)
            {
                throw new Exception();
            }

            var instance = assembly.CreateInstance(runCommand.ProcessorName);
            processor = instance as ISolutionProcessor;
        }
        else
        {
            if (runCommand.ProcessorName == nameof(AnalyzerRunner))
            {
                processor = new AnalyzerRunner();
            }
        }

        if (processor == null)
        {
            throw new Exception("no processor found");
        }

        await processor.ProcessSolution(solution, runCommand.Context, cancellationToken);
        logger.LogInformation("run command processed");
    }
}