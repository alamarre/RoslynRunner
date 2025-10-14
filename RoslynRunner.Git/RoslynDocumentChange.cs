using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace RoslynRunner.Git;

internal sealed class RoslynDocumentChange
{
    private readonly List<Func<Document, CancellationToken, Task<Document>>> _transformations = new();

    public RoslynDocumentChange(Document document)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
    }

    public Document Document { get; }

    public void AddTransformation(Func<Document, CancellationToken, Task<Document>> transformation)
    {
        _transformations.Add(transformation ?? throw new ArgumentNullException(nameof(transformation)));
    }

    public async Task<Document> ApplyAsync(Document baseDocument, CancellationToken cancellationToken)
    {
        var document = baseDocument ?? throw new ArgumentNullException(nameof(baseDocument));

        foreach (var transformation in _transformations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            document = await transformation(document, cancellationToken).ConfigureAwait(false);
        }

        return document;
    }
}
