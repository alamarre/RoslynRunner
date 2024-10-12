using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using RoslynRunner.Core;

namespace LegacyWebAppConverter;

public record ConvertToMinimalApiContext(string OutputRoot);

public class ConvertToMinimalApi : ISolutionProcessor<ConvertToMinimalApiContext>
{
    public async Task ProcessSolution(Solution solution, ConvertToMinimalApiContext? context, ILogger logger, CancellationToken cancellationToken)
    {
        var project = solution.Projects.First(p => p.Name == "ModernWebApi");
        var compilation = await project.GetCompilationAsync(cancellationToken);
        var controllerAttribute = compilation!.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.ApiControllerAttribute");

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
                var newClassDeclaration = ConvertControllerToEndpoint(controller, httpMethods);
                syntaxRoot = ReplaceControllerWithEndpoint(syntaxRoot, controller, newClassDeclaration);

                var newDocument = document.WithSyntaxRoot(syntaxRoot);
                solution = solution.WithDocumentSyntaxRoot(newDocument.Id, syntaxRoot);
            }
        }
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

    private ClassDeclarationSyntax ConvertControllerToEndpoint(ClassDeclarationSyntax controller, IEnumerable<MethodDeclarationSyntax> httpMethods)
    {
        var className = controller.Identifier.Text.Replace("Controller", "Endpoint");

        // Separate methods into HTTP methods and others (private or non-HTTP methods)
        var nonHttpMethods = controller.Members
            .OfType<MethodDeclarationSyntax>()
            .Where(m => !httpMethods.Contains(m))
            .Select(m => m.ToFullString());

        // Convert only HTTP methods
        var httpMethodDeclarations = httpMethods.Select(ConvertMethodToMinimalApi);

        var newClassText = $@"
        public partial class {className}
        {{
            {string.Join(Environment.NewLine, httpMethodDeclarations)}

            {string.Join(Environment.NewLine, nonHttpMethods)}
        }}";

        return ParseClassFromText(newClassText);
    }

    private string ConvertMethodToMinimalApi(MethodDeclarationSyntax method)
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
        return $@"
        [Api(HttpVerb.{httpMethod}, ""{routeArgument}"")]
        public IResult {methodName}()
        {{
            return TypedResults.Text(""pong"");
        }}";
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
