using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
            await ApplyToBranchAsync(branchName, changeSet.Message, new[] { changeSet }, baseBranch, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task ApplyAllAsync(string branchName, string message, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        return ApplyAllInternalAsync(branchName, message, cancellationToken);
    }

    private async Task ApplyAllInternalAsync(string branchName, string message, CancellationToken cancellationToken)
    {
        var baseBranch = await GetCurrentBranchNameAsync(cancellationToken).ConfigureAwait(false);
        await ApplyToBranchAsync(branchName, message, _changeSets, baseBranch, cancellationToken).ConfigureAwait(false);
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

    private async Task ApplyToBranchAsync(string branchName, string commitMessage, IEnumerable<RoslynChangeSet> changeSets, string baseBranch, CancellationToken cancellationToken)
    {
        var changeSetList = changeSets.ToList();
        if (changeSetList.Count == 0)
        {
            return;
        }

        var updatedDocuments = await BuildUpdatedDocumentsAsync(changeSetList, cancellationToken).ConfigureAwait(false);
        if (updatedDocuments.Count == 0)
        {
            return;
        }

        var cleanedDocuments = await CleanupDocumentsAsync(updatedDocuments, cancellationToken).ConfigureAwait(false);
        await RunGitAsync($"checkout {QuoteArgument(baseBranch)}", cancellationToken).ConfigureAwait(false);
        await RunGitAsync($"checkout -B {QuoteArgument(branchName)} {QuoteArgument(baseBranch)}", cancellationToken).ConfigureAwait(false);

        try
        {
            await WriteDocumentsAsync(cleanedDocuments, cancellationToken).ConfigureAwait(false);

            foreach (var document in cleanedDocuments.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(_repositoryPath, document.FilePath!);
                await RunGitAsync($"add {QuoteArgument(relativePath)}", cancellationToken).ConfigureAwait(false);
            }

            await RunGitAsync($"commit -m {QuoteArgument(commitMessage)}", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await RunGitAsync($"checkout {QuoteArgument(baseBranch)}", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<DocumentId, Document>> BuildUpdatedDocumentsAsync(IEnumerable<RoslynChangeSet> changeSets, CancellationToken cancellationToken)
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

            var diff = await ExecuteGitDiffAsync(documents.Values.Select(d => d.FilePath!).ToArray(), cancellationToken).ConfigureAwait(false);
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

    private async Task<string> ExecuteGitDiffAsync(string[] paths, CancellationToken cancellationToken)
    {
        var arguments = new StringBuilder("diff --");
        foreach (var path in paths)
        {
            var escaped = path.Replace("\"", "\\\"");
            arguments.Append(' ').Append('"').Append(escaped).Append('"');
        }

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _repositoryPath,
            Arguments = arguments.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException("Unable to start git diff process.");
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);

        return output;
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

    private async Task<string> GetCurrentBranchNameAsync(CancellationToken cancellationToken)
    {
        var output = await RunGitAsync("rev-parse --abbrev-ref HEAD", cancellationToken, captureOutput: true).ConfigureAwait(false);
        return output.Trim();
    }

    private async Task<string> RunGitAsync(string arguments, CancellationToken cancellationToken, bool captureOutput = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _repositoryPath,
            Arguments = arguments,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git process.");

        var stdOutTask = captureOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult(string.Empty);
        var stdErrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var error = await stdErrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed with exit code {process.ExitCode}:{Environment.NewLine}{error}");
        }

        return captureOutput ? await stdOutTask.ConfigureAwait(false) : string.Empty;
    }

    private static string QuoteArgument(string value)
    {
        var escaped = value.Replace("\"", "\\\"");
        return $"\"{escaped}\"";
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
