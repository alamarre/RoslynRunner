using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Simplification;

namespace RoslynRunner.Git;

public sealed class RoslynChanges
{
    private readonly List<RoslynChangeSet> _changeSets = new();
    private readonly string _repositoryPath;

    public RoslynChanges(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        _repositoryPath = repositoryPath;
    }

    public RoslynChangeSet NewChangeSet(string id, string message)
    {
        var changeSet = new RoslynChangeSet(id, message);
        _changeSets.Add(changeSet);
        return changeSet;
    }

    public async Task ApplyEachAsync(string branchPrefix, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchPrefix);

        var baseBranch = await GetCurrentBranchNameAsync(cancellationToken).ConfigureAwait(false);

        foreach (var changeSet in _changeSets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var branchName = branchPrefix + changeSet.Id;
            await ApplyToBranchAsync(branchName, changeSet.Message, new[] { changeSet }, baseBranch, commitChangeSetsSeparately: false, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task ApplyAllAsync(string branchName, string message, bool commitChangeSetsSeparately = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        return ApplyAllInternalAsync(branchName, message, commitChangeSetsSeparately, cancellationToken);
    }

    private async Task ApplyAllInternalAsync(string branchName, string message, bool commitChangeSetsSeparately, CancellationToken cancellationToken)
    {
        var baseBranch = await GetCurrentBranchNameAsync(cancellationToken).ConfigureAwait(false);
        await ApplyToBranchAsync(branchName, message, _changeSets, baseBranch, commitChangeSetsSeparately, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetDiffAsync(CancellationToken cancellationToken)
    {
        if (_changeSets.Count == 0)
        {
            return string.Empty;
        }

        var updatedDocuments = await BuildUpdatedDocumentsAsync(_changeSets, cancellationToken).ConfigureAwait(false);
        if (updatedDocuments.Count == 0)
        {
            return string.Empty;
        }

        var cleanedDocuments = await CleanupDocumentsAsync(updatedDocuments, cancellationToken).ConfigureAwait(false);
        return await GenerateDiffAsync(cleanedDocuments, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyToBranchAsync(string branchName, string commitMessage, IEnumerable<RoslynChangeSet> changeSets, string baseBranch, bool commitChangeSetsSeparately, CancellationToken cancellationToken)
    {
        var changeSetList = changeSets.ToList();
        if (changeSetList.Count == 0)
        {
            return;
        }

        using var repo = new Repository(_repositoryPath);
        var baseBranchRef = repo.Branches[baseBranch] ?? throw new InvalidOperationException($"Base branch '{baseBranch}' not found.");
        var targetBranch = ResetBranch(repo, branchName, baseBranchRef);
        Commands.Checkout(repo, targetBranch);

        try
        {
            if (commitChangeSetsSeparately)
            {
                await CommitEachChangeSetAsync(repo, changeSetList, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await CommitCombinedChangeSetsAsync(repo, commitMessage, changeSetList, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Commands.Checkout(repo, baseBranchRef);
        }
    }

    private async Task CommitCombinedChangeSetsAsync(
        Repository repo,
        string commitMessage,
        IReadOnlyCollection<RoslynChangeSet> changeSets,
        CancellationToken cancellationToken)
    {
        var updatedDocuments = await BuildUpdatedDocumentsAsync(changeSets, cancellationToken).ConfigureAwait(false);
        if (updatedDocuments.Count == 0)
        {
            return;
        }

        var cleanedDocuments = await CleanupDocumentsAsync(updatedDocuments, cancellationToken).ConfigureAwait(false);
        if (!await HasDocumentChangesAsync(cleanedDocuments, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await WriteDocumentsAsync(cleanedDocuments, cancellationToken).ConfigureAwait(false);
        StageDocuments(repo, cleanedDocuments.Values, cancellationToken);
        Commit(repo, commitMessage);
    }

    private async Task CommitEachChangeSetAsync(
        Repository repo,
        IReadOnlyCollection<RoslynChangeSet> changeSets,
        CancellationToken cancellationToken)
    {
        var appliedChangeSets = new List<RoslynChangeSet>();

        foreach (var changeSet in changeSets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            appliedChangeSets.Add(changeSet);

            var updatedDocuments = await BuildUpdatedDocumentsAsync(appliedChangeSets, cancellationToken).ConfigureAwait(false);
            if (updatedDocuments.Count == 0)
            {
                continue;
            }

            var cleanedDocuments = await CleanupDocumentsAsync(updatedDocuments, cancellationToken).ConfigureAwait(false);
            var hasChanges = await HasDocumentChangesAsync(cleanedDocuments, cancellationToken).ConfigureAwait(false);
            if (!hasChanges)
            {
                continue;
            }

            await WriteDocumentsAsync(cleanedDocuments, cancellationToken).ConfigureAwait(false);
            StageDocuments(repo, cleanedDocuments.Values, cancellationToken);
            Commit(repo, changeSet.Message);
        }
    }

    private async Task<Dictionary<DocumentId, Document>> BuildUpdatedDocumentsAsync(
        IEnumerable<RoslynChangeSet> changeSets,
        CancellationToken cancellationToken)
    {
        var documents = new Dictionary<DocumentId, Document>();

        foreach (var changeSet in changeSets)
        {
            foreach (var (documentId, change) in changeSet.DocumentChanges)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var startingDocument = documents.TryGetValue(documentId, out var currentDocument)
                    ? currentDocument
                    : change.Document;

                var updated = await change.ApplyAsync(startingDocument, cancellationToken).ConfigureAwait(false);
                documents[documentId] = updated;
            }
        }

        return documents;
    }

    private async Task<Dictionary<DocumentId, Document>> CleanupDocumentsAsync(Dictionary<DocumentId, Document> documents, CancellationToken cancellationToken)
    {
        var cleaned = new Dictionary<DocumentId, Document>();

        foreach (var (documentId, document) in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cleanedDocument = await CleanupDocumentAsync(document, cancellationToken).ConfigureAwait(false);
            cleaned[documentId] = cleanedDocument;
        }

        return cleaned;
    }

    private async Task WriteDocumentsAsync(Dictionary<DocumentId, Document> documents, CancellationToken cancellationToken)
    {
        foreach (var (_, document) in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(document.FilePath))
            {
                throw new InvalidOperationException($"Document '{document.Name}' does not have a file path.");
            }

            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(document.FilePath, text.ToString(), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> HasDocumentChangesAsync(Dictionary<DocumentId, Document> documents, CancellationToken cancellationToken)
    {
        foreach (var document in documents.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(document.FilePath))
            {
                throw new InvalidOperationException($"Document '{document.Name}' does not have a file path.");
            }

            var existingContent = await File.ReadAllTextAsync(document.FilePath!, cancellationToken).ConfigureAwait(false);
            var newContent = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

            if (!string.Equals(existingContent, newContent.ToString(), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<string> GenerateDiffAsync(Dictionary<DocumentId, Document> documents, CancellationToken cancellationToken)
    {
        var originalContents = new Dictionary<string, string>();
        try
        {
            foreach (var document in documents.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(document.FilePath))
                {
                    throw new InvalidOperationException($"Document '{document.Name}' does not have a file path.");
                }

                var filePath = document.FilePath!;
                originalContents[filePath] = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
                var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(filePath, text.ToString(), cancellationToken).ConfigureAwait(false);
            }

            var diff = await GenerateLibGitDiffAsync(documents.Values.Select(d => d.FilePath!).ToArray(), cancellationToken).ConfigureAwait(false);
            return diff;
        }
        finally
        {
            foreach (var (filePath, content) in originalContents)
            {
                await File.WriteAllTextAsync(filePath, content, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private Task<string> GenerateLibGitDiffAsync(string[] paths, CancellationToken cancellationToken)
    {
        using var repo = new Repository(_repositoryPath);
        var relativePaths = paths.Select(p => Path.GetRelativePath(_repositoryPath, p)).ToArray();
        var patch = repo.Diff.Compare<Patch>(repo.Head.Tip.Tree, DiffTargets.WorkingDirectory, relativePaths);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(patch.Content);
    }

    // based on https://github.com/dotnet/roslyn/blob/aff251d4cf72d8f84fa16876ae94c30d975b4aaa/src/Workspaces/Core/Portable/CodeActions/CodeAction.cs#L521
    private static async Task<Document> CleanupDocumentAsync(Document document, CancellationToken cancellationToken)
    {
        document = await ImportAdder.AddImportsAsync(
            document, Simplifier.AddImportsAnnotation, cancellationToken: cancellationToken).ConfigureAwait(false);

        document = await Simplifier.ReduceAsync(document, Simplifier.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);

        // format any node with explicit formatter annotation
        document = await Formatter.FormatAsync(document, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);

        // format any elastic whitespace
        document = await Formatter.FormatAsync(document, SyntaxAnnotation.ElasticAnnotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        
        return document;
    }

    private Task<string> GetCurrentBranchNameAsync(CancellationToken cancellationToken)
    {
        using var repo = new Repository(_repositoryPath);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(repo.Head.FriendlyName);
    }

    private static Branch ResetBranch(Repository repo, string branchName, Branch baseBranch)
    {
        var existingBranch = repo.Branches[branchName];
        if (existingBranch is not null)
        {
            repo.Branches.Remove(existingBranch);
        }

        return repo.CreateBranch(branchName, baseBranch.Tip);
    }

    private static void StageDocuments(Repository repo, IEnumerable<Document> documents, CancellationToken cancellationToken)
    {
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(repo.Info.WorkingDirectory, document.FilePath!);
            Commands.Stage(repo, relativePath);
        }
    }

    private static void Commit(Repository repo, string message)
    {
        var signature = repo.Config.BuildSignature(DateTimeOffset.Now);
        repo.Commit(message, signature, signature);
    }

    private static async Task<Document> AddMissingUsingsAsync(Document document, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is not CompilationUnitSyntax compilationUnit)
        {
            return document;
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (semanticModel is null)
        {
            return document;
        }

        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in compilationUnit.DescendantNodes().Where(n => n.HasAnnotation(Simplifier.AddImportsAnnotation)))
        {
            var symbol = semanticModel.GetSymbolInfo(node, cancellationToken).Symbol ?? semanticModel.GetTypeInfo(node, cancellationToken).Type;
            var namespaceName = symbol?.ContainingNamespace?.ToDisplayString();
            if (!string.IsNullOrWhiteSpace(namespaceName))
            {
                namespaces.Add(namespaceName);
            }
        }

        if (namespaces.Count == 0)
        {
            return document;
        }

        var existingUsings = new HashSet<string>(
            compilationUnit.Usings
                .Select(u => u.Name?.ToString())
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!),
            StringComparer.Ordinal);
        var newUsings = namespaces.Except(existingUsings).ToList();

        if (newUsings.Count == 0)
        {
            return document;
        }

        var directives = newUsings
            .OrderBy(ns => ns, StringComparer.Ordinal)
            .Select(ns => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(ns)).WithAdditionalAnnotations(Formatter.Annotation))
            .ToArray();

        var newRoot = compilationUnit.AddUsings(directives);
        return document.WithSyntaxRoot(newRoot);
    }
}
