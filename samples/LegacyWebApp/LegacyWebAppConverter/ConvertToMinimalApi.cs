using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using RoslynRunner.Core;

namespace LegacyWebAppConverter;

public record ConvertToMinimalApiContext(string OutputRoot);

public class ConvertToMinimalApi : ISolutionProcessor<ConvertToMinimalApiContext>
{
    public async Task ProcessSolution(Solution solution, ConvertToMinimalApiContext? context, ILogger logger,
        CancellationToken cancellationToken)
    {
        var project = solution.Projects.First(p => p.Name == "ModernWebApi");
        var compilation = await project.GetCompilationAsync(cancellationToken);
        var controllerAttribute = compilation!.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.ApiControllerAttribute");
        const string httpMethodAttributePrefix = "Microsoft.AspNetCore.Mvc.Routing.Http";
    }
}
