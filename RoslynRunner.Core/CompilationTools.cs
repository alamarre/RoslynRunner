using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace RoslynRunner.Core;

public static class CompilationTools
{
    public static async Task<Assembly?> LoadAssembly(string path, 
        CancellationToken cancellationToken = default, 
        string? projectName = null, 
        string? assemblyLoadContextPath = null,
        bool globalContext = false)
    {
        MSBuildWorkspace processorWorkspace = MSBuildWorkspace.Create();
        Solution processorSolution =
            await GetSolution(processorWorkspace, path, null);
        Project? project =
            processorSolution.Projects.FirstOrDefault(p => projectName == null || p.Name == projectName );
        if (project == null)
        {
            throw new Exception("project does not exist");
        }

        var compilation = await project.GetCompilationAsync(cancellationToken);
            
        if (compilation == null)
        {
            throw new Exception();
        }

        
        AssemblyLoadContext loadContext = globalContext ?
            AssemblyLoadContext.Default
            :new TestAssemblyLoadContext(assemblyLoadContextPath);
        Assembly? assembly = GetAssembly(compilation, loadContext);

        return assembly;
    }
    
    public static Assembly? GetAssembly(Compilation compilation, AssemblyLoadContext context)
    {
        using MemoryStream assemblyStream = new MemoryStream();
        using MemoryStream pdbStream = new MemoryStream();

        compilation = compilation.WithOptions(compilation.Options.WithOptimizationLevel(OptimizationLevel.Debug));

        EmitOptions emitOptions = new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb);
        EmitResult result = compilation.Emit(assemblyStream, pdbStream, options: emitOptions);

        if (!result.Success)
        {
            return null;
        }

        assemblyStream.Seek(0, SeekOrigin.Begin);
        pdbStream.Seek(0, SeekOrigin.Begin);

        Assembly assembly = context.LoadFromStream(assemblyStream, pdbStream);
        return assembly;
    }

    public static async Task<Solution> GetSolution(MSBuildWorkspace workspace, string path, ILogger? logger)
    {
        bool isProject = path.EndsWith("csproj");

        var reporter = new LoggingProgressReporter(logger);
        if (isProject)
        {
            return (await workspace.OpenProjectAsync(path, reporter)).Solution;
        }

        return await workspace.OpenSolutionAsync(path, reporter);
    }
}