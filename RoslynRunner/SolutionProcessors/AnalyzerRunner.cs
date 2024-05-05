using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynRunner.Core;

namespace RoslynRunner.SolutionProcessors;

public record AnalyzerContext(string AnalyzerProject, string TargetProject, List<string> AnalyzerNames);

public class AnalyzerRunner : ISolutionProcessor
{
    public async Task ProcessSolution(Solution solution, string? context, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (context == null) throw new ArgumentException("context must be an AnalyzerContext");

        var analyzerContext = JsonSerializer.Deserialize<AnalyzerContext>(context);
        if (analyzerContext == null) throw new ArgumentException("context must be an AnalyzerContext");

        if (!analyzerContext.AnalyzerProject.EndsWith(".csproj"))
            throw new ArgumentException("analyzer project must be a .csproj");
        var workspace = MSBuildWorkspace.Create();
        var analyzerSolution = await CompilationTools.GetSolution(workspace, analyzerContext.AnalyzerProject, null);

        var project = analyzerSolution.Projects.FirstOrDefault(p => p.FilePath == analyzerContext.AnalyzerProject);
        if (project == null) throw new Exception("analyzer project not found");
        var analyzerCompilation = await project.GetCompilationAsync(cancellationToken);
        var assemblyLoadContext = new TestAssemblyLoadContext(null);
        var assembly = CompilationTools.GetAssembly(analyzerCompilation!, assemblyLoadContext);

        var analyzers = analyzerContext.AnalyzerNames.Select(a => assembly!.CreateInstance(a))
            .Where(a => a != null).ToList();
        var diagnosticAnalyzers = analyzers.Where(a => a is DiagnosticAnalyzer).Cast<DiagnosticAnalyzer>().ToList();

        var targetProject = solution.Projects.FirstOrDefault(p => p.Name == analyzerContext.TargetProject);
        var projectCompilation = await targetProject!.GetCompilationAsync(cancellationToken);
        if (diagnosticAnalyzers.Any())
        {
            var diagnosticCompilation = projectCompilation!.WithAnalyzers(diagnosticAnalyzers.ToImmutableArray());
            var diagnostics = await diagnosticCompilation.GetAllDiagnosticsAsync(cancellationToken);
        }

        var incrementalGenerators = analyzers.Where(a => a is IIncrementalGenerator)
            .Cast<IIncrementalGenerator>()
            .Select(a => a.AsSourceGenerator());
        GeneratorDriver driver = CSharpGeneratorDriver.Create(incrementalGenerators);
        var nextStep = driver.RunGenerators(projectCompilation!);
    }
}
