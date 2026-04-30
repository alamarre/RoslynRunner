using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynRunner.Abstractions;
using RoslynRunner.Core;

namespace RoslynRunner.SolutionProcessors;

public record AnalyzerContext(string AnalyzerProject, string TargetProject, List<string> AnalyzerNames, string? AssemblyLoadContextPath = null);

public record GeneratedFile(string path, string content);

public class AnalyzerRunner : ISolutionProcessor
{
    public async Task ProcessSolution(Solution solution, string? context, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (context == null)
        {
            throw new ArgumentException("context must be an AnalyzerContext");
        }

        var analyzerContext = JsonSerializer.Deserialize<AnalyzerContext>(context);
        if (analyzerContext == null)
        {
            throw new ArgumentException("context must be an AnalyzerContext");
        }

        if (!analyzerContext.AnalyzerProject.EndsWith(".csproj"))
        {
            throw new ArgumentException("analyzer project must be a .csproj");
        }

        var workspace = MSBuildWorkspace.Create();
        var analyzerSolution = await CompilationTools.GetSolution(workspace, analyzerContext.AnalyzerProject, null);

        var project = analyzerSolution.Projects.FirstOrDefault(p => p.FilePath == analyzerContext.AnalyzerProject);
        if (project == null)
        {
            throw new Exception("analyzer project not found");
        }

        var analyzerCompilation = await project.GetCompilationAsync(cancellationToken);
        var assemblyLoadContext = new TestAssemblyLoadContext(analyzerContext.AssemblyLoadContextPath);
        var assembly = CompilationTools.GetAssembly(analyzerCompilation!, assemblyLoadContext);

        var analyzers = analyzerContext.AnalyzerNames.Select(a => assembly!.CreateInstance(a))
            .Where(a => a != null).ToList();
        var diagnosticAnalyzers = analyzers.Where(a => a is DiagnosticAnalyzer).Cast<DiagnosticAnalyzer>().ToList();

        logger.LogInformation($"analyzers found {analyzers.Count}");
        var targetProject = solution.Projects.FirstOrDefault(p => p.Name == analyzerContext.TargetProject);
        var projectCompilation = await targetProject!.GetCompilationAsync(cancellationToken);
        if (projectCompilation is null)
        {
            return;
        }

       
        var runContext = RunContextAccessor.RunContext;
        if (diagnosticAnalyzers.Any())
        {
            var diagnosticCompilation = projectCompilation!.WithAnalyzers(diagnosticAnalyzers.ToImmutableArray());
            var diagnostics = await diagnosticCompilation.GetAllDiagnosticsAsync(cancellationToken);
            runContext.Errors.AddRange(diagnostics.Select(d => d.ToString()));
        }

        var incrementalGenerators = analyzers.Where(a => a is IIncrementalGenerator)
            .Cast<IIncrementalGenerator>()
            .Select(a => a.AsSourceGenerator());
       

        
       

        if(incrementalGenerators.Any())
        {

            // Build a base compilation without any source-generated syntax trees.
            IEnumerable<SyntaxTree> nonGeneratedTrees =
                projectCompilation.SyntaxTrees.Where(static tree => !tree.FilePath.Contains(".g.", StringComparison.OrdinalIgnoreCase)
                    && !tree.FilePath.Contains(".generated.", StringComparison.OrdinalIgnoreCase));

            CSharpCompilation originalCompilation = CSharpCompilation.Create(
                assemblyName: projectCompilation.AssemblyName,
                syntaxTrees: nonGeneratedTrees,
                references: projectCompilation.References,
                options: (CSharpCompilationOptions)projectCompilation.Options);

            GeneratorDriver driver = CSharpGeneratorDriver.Create(incrementalGenerators);
            var nextStep = driver.RunGeneratorsAndUpdateCompilation(originalCompilation!, out var updatedCompilation, out var runnerDiagnostics);

            var generatedTrees = updatedCompilation.SyntaxTrees.Where(t => !projectCompilation!.SyntaxTrees.Contains(t)).ToList();

            runContext.Errors.AddRange(runnerDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));

            GeneratedFile[] generatedFiles = generatedTrees.Select(t =>
            {
                var hintName = t.FilePath;
                var content = t.GetText().ToString();
                return new GeneratedFile(hintName, content);
            }).ToArray();

            var generated = updatedCompilation.SyntaxTrees.Where(s => s.FilePath.EndsWith(".generated.cs")).ToList();
            var shouldNotExist = generated.Where(g => !generatedTrees.Contains(g));
            if(shouldNotExist.Any())
            {
                generatedFiles = generatedFiles.Concat(shouldNotExist.Select(g => new GeneratedFile(g.FilePath, g.GetText().ToString()))).ToArray();
            }
            string contents = JsonSerializer.Serialize(generatedFiles);
            runContext.Output.Add(contents);
        }
    }
}
