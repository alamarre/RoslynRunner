using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RoslynRunner.Core.Extensions;

public record SourceLocation(SemanticModel Model, Location Location);
public record MethodData(SourceLocation[] Locations, IOperation[] InvokedOperations);

public static class ProjectExtensions
{
    public static async Task<Dictionary<IMethodSymbol, MethodData>> BuildMethodDictionaryAsync(
        this Project startingProject,
        Func<Document, bool>? filterFunction = null)
    {
        Dictionary<IMethodSymbol, MethodData> methodDictionary = new Dictionary<IMethodSymbol, MethodData>(SymbolEqualityComparer.Default);

        // Collect the starting project and its referenced projects.
        List<Project> projectsToProcess = new List<Project> { startingProject };
        foreach (ProjectReference projectReference in startingProject.ProjectReferences)
        {
            Project? referencedProject = startingProject.Solution.GetProject(projectReference.ProjectId);
            if (referencedProject != null)
            {
                projectsToProcess.Add(referencedProject);
            }
        }

        // Process each project using its compilation.
        foreach (Project project in projectsToProcess)
        {
            Compilation? compilation = await project.GetCompilationAsync().ConfigureAwait(false);
            if (compilation == null)
            {
                continue;
            }

            foreach (Document document in project.Documents)
            {
                if (filterFunction != null && !filterFunction(document))
                {
                    continue;
                }

                var syntaxTree = await document.GetSyntaxTreeAsync().ConfigureAwait(false);
                if (syntaxTree == null)
                {
                    continue;
                }

                // Retrieve the semantic model from the compilation.
                SemanticModel semanticModel = compilation.GetSemanticModel(syntaxTree);

                var root = await syntaxTree.GetRootAsync().ConfigureAwait(false);
                IEnumerable<MethodDeclarationSyntax> methodDeclarations = root
                    .DescendantNodes()
                    .OfType<MethodDeclarationSyntax>();

                foreach (MethodDeclarationSyntax methodDeclaration in methodDeclarations)
                {
                    IMethodSymbol? methodSymbol = semanticModel.GetDeclaredSymbol(methodDeclaration) as IMethodSymbol;
                    if (methodSymbol == null)
                    {
                        continue;
                    }

                    IOperation? methodOperation = semanticModel.GetOperation(methodDeclaration);
                    IOperation[] invokedOperations = (methodOperation != null)
                        ? methodOperation.DescendantsAndSelf().OfType<IInvocationOperation>().ToArray()
                        : Array.Empty<IOperation>();

                    SourceLocation[] sourceLocations = new SourceLocation[]
                    {
                        new SourceLocation(semanticModel, methodDeclaration.GetLocation())
                    };

                    MethodData methodData = new MethodData(sourceLocations, invokedOperations);
                    if (!methodDictionary.ContainsKey(methodSymbol))
                    {
                        methodDictionary.Add(methodSymbol, methodData);
                    }
                }
            }
        }

        return methodDictionary;
    }
}
