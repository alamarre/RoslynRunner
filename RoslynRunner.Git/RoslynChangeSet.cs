using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;

namespace RoslynRunner.Git;

public sealed class RoslynChangeSet
{
    private readonly Dictionary<DocumentId, RoslynDocumentChange> _documentChanges = new();

    internal RoslynChangeSet(string id, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Id = id;
        Message = message;
    }

    public string Id { get; }

    public string Message { get; }

    internal IReadOnlyDictionary<DocumentId, RoslynDocumentChange> DocumentChanges => _documentChanges;

    public void ReplaceMethod(Document document, MethodDeclarationSyntax originalMethod, MethodDeclarationSyntax replacementMethod)
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        if (originalMethod is null)
        {
            throw new ArgumentNullException(nameof(originalMethod));
        }

        if (replacementMethod is null)
        {
            throw new ArgumentNullException(nameof(replacementMethod));
        }

        var annotatedReplacement = replacementMethod.WithAdditionalAnnotations(
            Formatter.Annotation,
            Simplifier.Annotation,
            Simplifier.AddImportsAnnotation);

        AddTransformation(document, async (doc, ct) =>
        {
            var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            var currentMethod = root!
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.IsEquivalentTo(originalMethod));

            if (currentMethod is null)
            {
                return doc;
            }

            var newRoot = root.ReplaceNode(currentMethod, annotatedReplacement);
            return doc.WithSyntaxRoot(newRoot);
        });
    }

    public void ReplaceNode(Document document, SyntaxNode originalNode, SyntaxNode replacementNode)
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        if (originalNode is null)
        {
            throw new ArgumentNullException(nameof(originalNode));
        }

        if (replacementNode is null)
        {
            throw new ArgumentNullException(nameof(replacementNode));
        }

        var annotatedReplacement = replacementNode.WithAdditionalAnnotations(
            Formatter.Annotation,
            Simplifier.Annotation,
            Simplifier.AddImportsAnnotation);

        AddTransformation(document, async (doc, ct) =>
        {
            var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            var currentNode = root!
                .DescendantNodesAndSelf()
                .FirstOrDefault(node => node.IsEquivalentTo(originalNode));

            if (currentNode is null)
            {
                return doc;
            }

            var newRoot = root.ReplaceNode(currentNode, annotatedReplacement);
            return doc.WithSyntaxRoot(newRoot);
        });
    }

    public void TransformDocument(Document document, Func<Document, CancellationToken, Task<Document>> transformation)
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        if (transformation is null)
        {
            throw new ArgumentNullException(nameof(transformation));
        }

        AddTransformation(document, transformation);
    }

    private void AddTransformation(Document document, Func<Document, CancellationToken, Task<Document>> transformation)
    {
        if (!_documentChanges.TryGetValue(document.Id, out var change))
        {
            change = new RoslynDocumentChange(document);
            _documentChanges.Add(document.Id, change);
        }

        change.AddTransformation(transformation);
    }
}
