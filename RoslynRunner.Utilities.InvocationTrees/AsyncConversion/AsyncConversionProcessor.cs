using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.Extensions.Logging;
using RoslynRunner.Abstractions;
using RoslynRunner.Core;
using RoslynRunner.Core.Extensions;
using RoslynRunner.Git;

namespace RoslynRunner.Utilities.InvocationTrees;

public class AsyncConversionProcessor : ISolutionProcessor<AsyncConversionParameters>, ISolutionProcessor
{
    private readonly Func<string, RoslynChanges> _changesFactory;

    public AsyncConversionProcessor()
        : this(repositoryPath => new RoslynChanges(repositoryPath))
    {
    }

    internal AsyncConversionProcessor(Func<string, RoslynChanges> changesFactory)
    {
        _changesFactory = changesFactory ?? throw new ArgumentNullException(nameof(changesFactory));
    }

    public async Task ProcessSolution(Solution solution, AsyncConversionParameters? context, ILogger logger, CancellationToken cancellationToken)
    {
        if (context == null)
        {
            throw new ArgumentException("context required");
        }

        if (string.IsNullOrWhiteSpace(context.RepositoryPath))
        {
            throw new ArgumentException("RepositoryPath is required", nameof(context));
        }

        if (string.IsNullOrWhiteSpace(context.BranchName))
        {
            throw new ArgumentException("BranchName is required", nameof(context));
        }

        var cache = await CachedSymbolFinder.FromCache(solution);
        var serviceType = cache.GetSymbolByMetadataName(context.TypeName);
        if (serviceType == null)
        {
            logger.LogError("service type not found");
            return;
        }

        var generator = new AsyncConversionGenerator(cache, solution);
        var conversionResult = await generator.GenerateAsyncVersion(
            serviceType,
            context.MethodName,
            cancellationToken,
            renameTransformedMethods: context.RenameTransformedMethods);
        if (conversionResult == null)
        {
            logger.LogInformation("no methods to convert");
            return;
        }

        if (conversionResult.Documents.IsDefaultOrEmpty)
        {
            logger.LogInformation("no documents were updated by the async conversion");
            return;
        }

        var roslynChanges = _changesFactory(context.RepositoryPath);
        var changeSetId = context.ChangeId ?? BuildChangeSetId(context);
        var commitMessage = context.CommitMessage ?? BuildCommitMessage(context);

        var changeSet = roslynChanges.NewChangeSet(changeSetId, commitMessage);
        ApplyDocumentConversions(changeSet, conversionResult.Documents, solution, context.ReplaceExistingMethods);

        await roslynChanges.ApplyAllAsync(context.BranchName, commitMessage, cancellationToken).ConfigureAwait(false);

        RunContextAccessor.RunContext.Output.Add($"Updated branch '{context.BranchName}' with async conversions.");
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

    private static string BuildChangeSetId(AsyncConversionParameters parameters)
    {
        if (!string.IsNullOrWhiteSpace(parameters.ChangeId))
        {
            return parameters.ChangeId!;
        }

        var methodSuffix = string.IsNullOrWhiteSpace(parameters.MethodName)
            ? string.Empty
            : $".{parameters.MethodName}";

        return $"{parameters.TypeName}{methodSuffix}".Replace('`', '_').Replace('.', '_');
    }

    private static string BuildCommitMessage(AsyncConversionParameters parameters)
    {
        if (!string.IsNullOrWhiteSpace(parameters.CommitMessage))
        {
            return parameters.CommitMessage!;
        }

        var methodSuffix = string.IsNullOrWhiteSpace(parameters.MethodName)
            ? string.Empty
            : $".{parameters.MethodName}";

        return $"Convert {parameters.TypeName}{methodSuffix} to async";
    }

    private static void ApplyDocumentConversions(
        RoslynChangeSet changeSet,
        ImmutableArray<AsyncDocumentConversion> documents,
        Solution solution,
        bool replaceExistingMethods)
    {
        foreach (var documentConversion in documents)
        {
            var document = solution.GetDocument(documentConversion.DocumentId) ?? documentConversion.Document;

            if (replaceExistingMethods)
            {
                ReplaceMethods(changeSet, document, documentConversion.ConvertedMethods);
            }
            else
            {
                AppendMethods(changeSet, document, documentConversion.ConvertedMethods);
            }
        }
    }

    private static void ReplaceMethods(
        RoslynChangeSet changeSet,
        Document document,
        ImmutableArray<AsyncMethodConversion> conversions)
    {
        foreach (var conversion in conversions)
        {
            changeSet.ReplaceMethod(document, conversion.OriginalMethod, conversion.AsyncMethod);
        }
    }

    private static void AppendMethods(
        RoslynChangeSet changeSet,
        Document document,
        ImmutableArray<AsyncMethodConversion> conversions)
    {
        if (conversions.IsDefaultOrEmpty || conversions.Length == 0)
        {
            return;
        }

        changeSet.TransformDocument(document, async (doc, cancellationToken) =>
        {
            var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is not CompilationUnitSyntax compilationUnit)
            {
                return doc;
            }

            var updatedRoot = compilationUnit;

            foreach (var grouping in conversions.GroupBy(conversion => conversion.OriginalMethod.Parent))
            {
                if (grouping.Key is not ClassDeclarationSyntax classDeclaration)
                {
                    continue;
                }

                var asyncMethods = grouping
                    .Select(conversion => conversion.AsyncMethod.WithAdditionalAnnotations(
                        Formatter.Annotation,
                        Simplifier.Annotation,
                        Simplifier.AddImportsAnnotation))
                    .ToArray();

                var newClass = classDeclaration.AddMembers(asyncMethods);
                updatedRoot = (CompilationUnitSyntax)updatedRoot.ReplaceNode(classDeclaration, newClass);
            }

            return doc.WithSyntaxRoot(updatedRoot);
        });
    }
}
