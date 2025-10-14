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
        var processorWorkspace = MSBuildWorkspace.Create();
        var processorSolution =
            await GetSolution(processorWorkspace, path, null);
        var project =
            processorSolution.Projects.FirstOrDefault(p => projectName == null || p.Name == projectName);
        if (project == null)
        {
            throw new Exception("project does not exist");
        }

        var compilation = await project.GetCompilationAsync(cancellationToken);

        if (compilation == null)
        {
            throw new Exception();
        }


        var loadContext = globalContext
            ? AssemblyLoadContext.Default
            : new TestAssemblyLoadContext(assemblyLoadContextPath);
        var assembly = GetAssembly(compilation, loadContext);

        return assembly;
    }

    public static Assembly? GetAssembly(Compilation compilation, AssemblyLoadContext context)
    {
        using var assemblyStream = new MemoryStream();
        using var pdbStream = new MemoryStream();

        compilation = compilation.WithOptions(compilation.Options.WithOptimizationLevel(OptimizationLevel.Debug));

        var emitOptions = new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb);
        var result = compilation.Emit(assemblyStream, pdbStream, options: emitOptions);

        if (!result.Success)
        {
            var diagnostics = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"{d.Id}: {d.GetMessage()}")
                .ToArray();

            var message = diagnostics.Length == 0
                ? "Unknown compilation error while emitting in-memory assembly."
                : $"Compilation failed with {diagnostics.Length} error(s): {string.Join("; ", diagnostics)}";

            throw new InvalidOperationException(message);
        }

        assemblyStream.Seek(0, SeekOrigin.Begin);
        pdbStream.Seek(0, SeekOrigin.Begin);

        var assembly = context.LoadFromStream(assemblyStream, pdbStream);
        return assembly;
    }

    public static async Task<Solution> GetSolution(MSBuildWorkspace workspace, string path, ILogger? logger)
    {
        var isProject = path.EndsWith("csproj");

        var reporter = new LoggingProgressReporter(logger);
        if (isProject)
        {
            return (await workspace.OpenProjectAsync(path, reporter)).Solution;
        }

        return await workspace.OpenSolutionAsync(path, reporter);
    }
}
