using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using RoslynRunner.Abstractions;
using RoslynRunner.Core;
using RoslynRunner.Core.Extensions;
using RoslynRunner.Git;

namespace RoslynRunner.Utilities.InvocationTrees;

public class AsyncConversionProcessor : ISolutionProcessor<AsyncConversionParameters>, ISolutionProcessor
{
    public async Task ProcessSolution(
        Solution solution,
        AsyncConversionParameters? context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var parameters = ValidateParameters(context);

        var cache = await CachedSymbolFinder.FromCache(solution).ConfigureAwait(false);
        var serviceType = GetServiceType(cache, parameters.TypeName, logger);
        if (serviceType is null)
        {
            return;
        }

        var engine = new AsyncConversionEngine(cache, solution);
        var conversionResults = await GenerateConversionResultsAsync(
            solution,
            engine,
            serviceType,
            parameters.MethodName,
            cancellationToken).ConfigureAwait(false);

        if (conversionResults.Count == 0)
        {
            logger.LogInformation("no methods to convert");
            return;
        }

        await WritePreviewFileAsync(parameters, conversionResults[0], cancellationToken).ConfigureAwait(false);

        var repositoryPath = GetRepositoryPath(solution, parameters);
        await ApplyChangesToBranchAsync(repositoryPath, conversionResults, parameters, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ProcessSolution(Solution solution, string? context, ILogger logger, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context))
        {
            throw new ArgumentException("context required", nameof(context));
        }

        var parameters = JsonSerializer.Deserialize<AsyncConversionParameters>(context);
        if (parameters is null)
        {
            throw new ArgumentException("context required", nameof(context));
        }

        await ProcessSolution(solution, parameters, logger, cancellationToken).ConfigureAwait(false);
    }

    private static AsyncConversionParameters ValidateParameters(AsyncConversionParameters? parameters)
    {
        if (parameters is null)
        {
            throw new ArgumentException("context required");
        }

        if (string.IsNullOrWhiteSpace(parameters.BranchName))
        {
            throw new ArgumentException("BranchName must be provided on the async conversion parameters.");
        }

        return parameters;
    }

    private static INamedTypeSymbol? GetServiceType(CachedSymbolFinder cache, string typeName, ILogger logger)
    {
        var serviceType = cache.GetSymbolByMetadataName(typeName) as INamedTypeSymbol;
        if (serviceType is null)
        {
            logger.LogError("service type not found");
        }

        return serviceType;
    }

    private static async Task<IReadOnlyList<AsyncConversionDocumentResult>> GenerateConversionResultsAsync(
        Solution solution,
        AsyncConversionEngine engine,
        INamedTypeSymbol startingType,
        string? methodName,
        CancellationToken cancellationToken)
    {
        var results = new List<AsyncConversionDocumentResult>();

        var initialResult = await engine.GenerateAsyncVersion(startingType, methodName, cancellationToken)
            .ConfigureAwait(false);
        if (initialResult is null || initialResult.ConvertedMethods.IsDefaultOrEmpty || initialResult.ConvertedMethods.Length == 0)
        {
            return results;
        }

        var processedDocuments = new HashSet<DocumentId>();
        var startingDocument = GetDocument(solution, startingType);
        if (startingDocument is not null && !string.IsNullOrWhiteSpace(startingDocument.FilePath))
        {
            results.Add(new AsyncConversionDocumentResult(startingDocument, initialResult));
            processedDocuments.Add(startingDocument.Id);
        }

        var invocationTree = initialResult.InvocationTree;
        var processedTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default)
        {
            startingType,
        };

        foreach (var type in invocationTree.AllMethods
                     .Select(m => m.MethodSymbol.ContainingType)
                     .OfType<INamedTypeSymbol>())
        {
            if (!processedTypes.Add(type))
            {
                continue;
            }

            if (!type.Locations.Any(l => l.IsInSource))
            {
                continue;
            }

            var document = GetDocument(solution, type);
            if (document is null || string.IsNullOrWhiteSpace(document.FilePath) || !processedDocuments.Add(document.Id))
            {
                continue;
            }

            var conversion = await engine.GenerateAsyncVersion(type, methodName: null, cancellationToken, invocationTree)
                .ConfigureAwait(false);
            if (conversion is null || conversion.ConvertedMethods.IsDefaultOrEmpty || conversion.ConvertedMethods.Length == 0)
            {
                continue;
            }

            results.Add(new AsyncConversionDocumentResult(document, conversion));
        }

        return results;
    }

    private static Document? GetDocument(Solution solution, INamedTypeSymbol type)
    {
        var tree = type.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree;
        return tree is null ? null : solution.GetDocument(tree);
    }

    private static async Task WritePreviewFileAsync(
        AsyncConversionParameters parameters,
        AsyncConversionDocumentResult primaryResult,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(parameters.OutputPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(parameters.OutputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var outputRoot = parameters.ReplaceExistingMethods
            ? primaryResult.Result.UpdatedRoot
            : AppendAsyncMethods(primaryResult.Result);

        await File.WriteAllTextAsync(parameters.OutputPath, outputRoot.ToFullString(), cancellationToken)
            .ConfigureAwait(false);

        AddOutputMessage($"Created file: {Path.GetFullPath(parameters.OutputPath)}");
    }

    private static async Task ApplyChangesToBranchAsync(
        string repositoryPath,
        IReadOnlyList<AsyncConversionDocumentResult> conversionResults,
        AsyncConversionParameters parameters,
        CancellationToken cancellationToken)
    {
        var changes = new RoslynChanges(repositoryPath);
        var commitMessage = BuildCommitMessage(parameters);
        var changeSet = changes.NewChangeSet(Guid.NewGuid().ToString("N"), commitMessage);

        foreach (var conversion in conversionResults)
        {
            var finalRoot = parameters.ReplaceExistingMethods
                ? conversion.Result.UpdatedRoot
                : AppendAsyncMethods(conversion.Result);

            changeSet.ReplaceNode(conversion.Document, conversion.Result.OriginalRoot, finalRoot);
        }

        await changes.ApplyAllAsync(parameters.BranchName!, commitMessage, cancellationToken).ConfigureAwait(false);
        AddOutputMessage($"Updated branch: {parameters.BranchName}");
    }

    private static string BuildCommitMessage(AsyncConversionParameters parameters)
    {
        var methodPart = string.IsNullOrWhiteSpace(parameters.MethodName)
            ? "all methods"
            : parameters.MethodName;
        return $"Convert {parameters.TypeName}.{methodPart} to async";
    }

    private static string GetRepositoryPath(Solution solution, AsyncConversionParameters parameters)
    {
        var searchRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(solution.FilePath))
        {
            var directory = Path.GetDirectoryName(solution.FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                searchRoots.Add(directory);
            }
        }

        foreach (var project in solution.Projects)
        {
            if (!string.IsNullOrWhiteSpace(project.FilePath))
            {
                var directory = Path.GetDirectoryName(project.FilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    searchRoots.Add(directory);
                }
            }

            foreach (var document in project.Documents)
            {
                if (!string.IsNullOrWhiteSpace(document.FilePath))
                {
                    var directory = Path.GetDirectoryName(document.FilePath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        searchRoots.Add(directory);
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(parameters.OutputPath))
        {
            var directory = Path.GetDirectoryName(parameters.OutputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                searchRoots.Add(directory);
            }
        }

        foreach (var root in searchRoots)
        {
            var repositoryRoot = FindGitRoot(root);
            if (repositoryRoot is not null)
            {
                return repositoryRoot;
            }
        }

        throw new InvalidOperationException("Unable to locate a git repository for the provided solution.");
    }

    private static string? FindGitRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            var gitDirectory = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitDirectory))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
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

    private static void AddOutputMessage(string message)
    {
        try
        {
            RunContextAccessor.RunContext.Output.Add(message);
        }
        catch (InvalidOperationException)
        {
            // No active run context; ignore.
        }
    }

    private sealed record AsyncConversionDocumentResult(Document Document, AsyncConversionResult Result);
}
