using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.Extensions.Logging;
using RoslynRunner.Abstractions;
using RoslynRunner.Core;
using RoslynRunner.Utilities.InvocationTrees;

namespace LegacyWebAppConverter;

public record ConvertToMinimalApiContext(string OutputRoot, string AsyncOutputPath, string AsyncTypeName, string? AsyncMethodName = null);

public class ConvertToMinimalApi : ISolutionProcessor<ConvertToMinimalApiContext>
{
    public async Task ProcessSolution(Solution solution, ConvertToMinimalApiContext? context, ILogger logger, CancellationToken cancellationToken)
    {
        var project = solution.Projects.First(p => p.Name == "ModernWebApi");
        var compilation = await project.GetCompilationAsync(cancellationToken);
        var controllerAttribute = compilation!.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.ApiControllerAttribute");

        var editedProject = project;
        foreach (var document in project.Documents)
        {
            var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken);
            if (syntaxRoot == null)
            {
                continue;
            }
            var semanticModel = compilation.GetSemanticModel(syntaxRoot.SyntaxTree);

            var controllers = GetControllersWithAttribute(syntaxRoot, semanticModel, controllerAttribute);

            foreach (var controller in controllers)
            {
                var httpMethods = GetHttpMethods(controller, semanticModel);
                var newClassDeclaration = ConvertControllerToEndpoint(controller, httpMethods, semanticModel);
                syntaxRoot = ReplaceControllerWithEndpoint(syntaxRoot, controller, newClassDeclaration);

                syntaxRoot = ReplaceNamespace(syntaxRoot);

                syntaxRoot = Formatter.Format(syntaxRoot, solution.Workspace);

                // Get the current file path and replace the directory and filename
                var oldFilePath = document.FilePath!;  // Assuming document.FilePath is not null

                // Replace "Controllers" with "Endpoints" in the directory path
                var newFilePath = oldFilePath.Replace("Controllers", "Endpoints");

                // Replace "Controller" with "Endpoint" in the file name
                var fileName = Path.GetFileName(newFilePath);
                var newFileName = fileName.Replace("Controller", "Endpoint");

                // Combine the new directory path and renamed file
                newFilePath = Path.Combine(Path.GetDirectoryName(newFilePath)!, newFileName);

                var folders = new List<string>(document.Folders);
                if (folders.Contains("Controllers"))
                {
                    // Replace "Controllers" with "Endpoints" in the folder structure
                    var controllerIndex = folders.IndexOf("Controllers");
                    folders[controllerIndex] = "Endpoints";
                }

                RunContextAccessor.RunContext.Output.Add($"Created file: {Path.GetFullPath(newFilePath)}");
                await File.WriteAllTextAsync(newFilePath, syntaxRoot.ToFullString(), cancellationToken);
            }
        }

        if (context != null)
        {
            var cache = await CachedSymbolFinder.FromCache(solution);
            var serviceType = cache.GetSymbolByMetadataName(context.AsyncTypeName);
            if (serviceType != null)
            {
                var engine = new AsyncConversionEngine(cache, solution);
                var newRoot = await engine.GenerateAsyncVersion(serviceType, context.AsyncMethodName, cancellationToken);
                if (newRoot != null)
                {
                    await File.WriteAllTextAsync(context.AsyncOutputPath, newRoot.ToFullString(), cancellationToken);
                    RunContextAccessor.RunContext.Output.Add($"Created file: {Path.GetFullPath(context.AsyncOutputPath)}");
                }
            }
        }
    }
    }

    private SyntaxNode ReplaceNamespace(SyntaxNode syntaxRoot)
    {
        var namespaceDeclaration = syntaxRoot.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (namespaceDeclaration != null)
        {
            var newNamespace = namespaceDeclaration.Name.ToString().Replace("Controllers", "Endpoints");
            return syntaxRoot.ReplaceNode(namespaceDeclaration, namespaceDeclaration.WithName(SyntaxFactory.ParseName(newNamespace)));
        }

        return syntaxRoot;
    }

    private IEnumerable<ClassDeclarationSyntax> GetControllersWithAttribute(SyntaxNode syntaxRoot, SemanticModel semanticModel, INamedTypeSymbol? controllerAttribute)
    {
        return syntaxRoot.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Where(c => c.AttributeLists
                .SelectMany(a => a.Attributes)
                .Any(a => ModelExtensions.GetTypeInfo(semanticModel, a)
                    .Type?
                    .Equals(controllerAttribute, SymbolEqualityComparer.Default) == true));
    }

    private IEnumerable<MethodDeclarationSyntax> GetHttpMethods(ClassDeclarationSyntax controller, SemanticModel semanticModel)
    {
        return controller.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.AttributeLists
                .SelectMany(a => a.Attributes)
                .Any(a => a.Name.ToString().StartsWith("Http")));
    }

    private ClassDeclarationSyntax ConvertControllerToEndpoint(ClassDeclarationSyntax controller, IEnumerable<MethodDeclarationSyntax> httpMethods, SemanticModel model)
    {
        var className = controller.Identifier.Text.Replace("Controller", "Endpoint");

        // Separate methods into HTTP methods and others (private or non-HTTP methods)
        var nonHttpMethods = controller.Members
            .OfType<MethodDeclarationSyntax>()
            .Where(m => !httpMethods.Contains(m))
            .Select(m => m.ToFullString());

        // Convert only HTTP methods
        var httpMethodDeclarations = httpMethods
            .Select(m => ConvertMethodToMinimalApi(m, model));

        var newClassText = $@"
        public partial class {className}
        {{
            {string.Join(Environment.NewLine, httpMethodDeclarations)}

            {string.Join(Environment.NewLine, nonHttpMethods)}
        }}";

        return ParseClassFromText(newClassText);
    }

    private string ConvertMethodToMinimalApi(MethodDeclarationSyntax method, SemanticModel semanticModel)
    {
        var httpAttribute = method.AttributeLists
            .SelectMany(a => a.Attributes)
            .FirstOrDefault(a => a.Name.ToString().StartsWith("Http"));

        if (httpAttribute == null)
        {
            return string.Empty;
        }

        var httpMethod = httpAttribute.Name.ToString().Replace("Http", "").ToUpperInvariant();
        var routeArgument = httpAttribute.ArgumentList?.Arguments.FirstOrDefault()?.ToString().Trim('"');

        var methodName = method.Identifier.Text;
        var methodBody = method.Body != null ? TransformMethodBody(method, semanticModel) : string.Empty;

        return $@"
        [Api(HttpVerb.{ToPascalCase(httpMethod)}, ""{routeArgument}"")]
        public IResult {methodName}()
        {{
            {methodBody}
        }}";
    }

    private string ToPascalCase(string input)
    {
        return (input.Length > 1 ? char.ToUpperInvariant(input[0]) + input.Substring(1).ToLowerInvariant() : string.Empty);
    }

    private string TransformMethodBody(MethodDeclarationSyntax method, SemanticModel semanticModel)
    {
        var returnType = semanticModel.GetDeclaredSymbol(method)?.ReturnType;

        if (returnType == null || method.Body == null)
        {
            return method.Body?.ToFullString() ?? string.Empty;
        }

        var returnStatements = method.Body.DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .ToList();

        var transformedBody = method.Body.ToFullString();

        // Replace each return statement with appropriate TypedResults method
        foreach (var returnStatement in returnStatements)
        {
            var returnExpression = returnStatement.Expression?.ToFullString();

            if (returnExpression == null)
            {
                continue;
            }

            // Match the return type with appropriate TypedResults method
            string typedResultMethod = GetTypedResultForReturnType(returnType);
            var transformedReturn = $"TypedResults.{typedResultMethod}({returnExpression})";

            // Replace the original return statement with the transformed one
            transformedBody = transformedBody.Replace(returnStatement.ToFullString(), $"return {transformedReturn};");
        }

        return transformedBody;
    }

    private string GetTypedResultForReturnType(ITypeSymbol returnType)
    {
        // Map return types to appropriate TypedResults method
        if (returnType.SpecialType == SpecialType.System_String)
        {
            return "Text";
        }
        if (returnType.SpecialType == SpecialType.System_Int32 || returnType.SpecialType == SpecialType.System_Int64)
        {
            return "Json";  // You can change this mapping to other types like `Json` or `Ok`.
        }
        if (returnType.Name == "JsonResult")
        {
            return "Json";
        }

        // Default TypedResult method for unknown return types
        return "Ok";
    }

    private SyntaxNode ReplaceControllerWithEndpoint(SyntaxNode syntaxRoot, ClassDeclarationSyntax oldController, ClassDeclarationSyntax newController)
    {
        return syntaxRoot.ReplaceNode(oldController, newController);
    }

    private ClassDeclarationSyntax ParseClassFromText(string classText)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(classText);
        return syntaxTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().First();
    }
}
