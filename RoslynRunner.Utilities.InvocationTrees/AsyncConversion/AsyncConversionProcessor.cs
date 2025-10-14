using System.Linq;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using RoslynRunner.Abstractions;
using RoslynRunner.Core;
using RoslynRunner.Core.Extensions;

namespace RoslynRunner.Utilities.InvocationTrees;

public class AsyncConversionProcessor : ISolutionProcessor<AsyncConversionParameters>, ISolutionProcessor
{
    public async Task ProcessSolution(Solution solution, AsyncConversionParameters? context, ILogger logger, CancellationToken cancellationToken)
    {
        if (context == null)
        {
            throw new ArgumentException("context required");
        }

        var cache = await CachedSymbolFinder.FromCache(solution);
        var serviceType = cache.GetSymbolByMetadataName(context.TypeName);
        if (serviceType == null)
        {
            logger.LogError("service type not found");
            return;
        }

        var engine = new AsyncConversionEngine(cache, solution);
        var conversionResult = await engine.GenerateAsyncVersion(serviceType, context.MethodName, cancellationToken);
        if (conversionResult == null)
        {
            logger.LogInformation("no methods to convert");
            return;
        }

        var outputRoot = context.ReplaceExistingMethods
            ? conversionResult.UpdatedRoot
            : AppendAsyncMethods(conversionResult);

        await File.WriteAllTextAsync(context.OutputPath, outputRoot.ToFullString(), cancellationToken);
        if (RunContextAccessor.TryGet(out var runContext) && runContext is not null)
        {
            runContext.Output.Add($"Created file: {Path.GetFullPath(context.OutputPath)}");
        }
    }

    public async Task ProcessSolution(Solution solution, string? context, ILogger logger, CancellationToken cancellationToken)
    {
        if (context == null)
        {
            throw new ArgumentException("context required");
        }
        var parameters = JsonSerializer.Deserialize<AsyncConversionParameters>(context);
        await ProcessSolution(solution, parameters, logger, cancellationToken);
    }

    private static CompilationUnitSyntax AppendAsyncMethods(AsyncConversionResult conversionResult)
    {
        var updatedRoot = conversionResult.OriginalRoot;
        foreach (var grouping in conversionResult.ConvertedMethods.GroupBy(m => m.OriginalMethod.Parent))
        {
            if (grouping.Key is not ClassDeclarationSyntax classDeclaration)
            {
                continue;
            }

            var asyncMethods = grouping.Select(m => m.AsyncMethod).ToArray();
            var newClass = classDeclaration.AddMembers(asyncMethods);
            updatedRoot = (CompilationUnitSyntax)updatedRoot.ReplaceNode(classDeclaration, newClass);
        }

        return updatedRoot;
    }
}
