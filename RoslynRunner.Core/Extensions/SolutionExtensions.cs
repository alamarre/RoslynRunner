using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynRunner.Core.Extensions;

public static class SolutionExtensions
{
    public static async Task<ISymbol?> FindSymbol(this Solution solution, string fullyQualified, CancellationToken cancellationToken = default, Project? project = null)
    {
        string shortName = fullyQualified.Substring(fullyQualified.LastIndexOf('.'));
        if (project != null)
        {
            var symbols = await SymbolFinder.FindDeclarationsAsync(project, shortName, false, cancellationToken);
            return symbols.FirstOrDefault();
        } 
        return (await SymbolFinder.FindSourceDeclarationsAsync(solution, fullyQualified, false, cancellationToken)).FirstOrDefault();
    } 
    
    public static Project? GetProject(this Solution solution, SyntaxNode node )
    {
        var project = solution.Projects.FirstOrDefault(p => p.Documents.Any(d => d.FilePath == node.SyntaxTree.FilePath));
        return project;
    }

    public static async Task<Compilation?> GetCompilation(this Solution solution, SyntaxNode node, CancellationToken cancellationToken = default)
    {
        var project = solution.GetProject(node);
        if (project == null)
        {
            return null;
        }
        var currentCompilation = await project.GetCompilationAsync(cancellationToken);

        return currentCompilation;
    }
    
    public static async Task<SemanticModel?> GetModel(this Solution solution, SyntaxNode node, CancellationToken cancellationToken = default)
    {
        var currentCompilation = await solution.GetCompilation(node, cancellationToken);

        var model = currentCompilation?.GetSemanticModel(node.SyntaxTree);
        return model;
    }
}