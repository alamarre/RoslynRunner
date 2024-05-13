using Microsoft.CodeAnalysis;

namespace RoslynRunner.Core.Extensions;

public static class LocationExtensions
{
    public static SyntaxNode? GetSyntaxNode(this Location location)
    {
        var root = location.SourceTree?.GetRoot();
        return root?.FindNode(location.SourceSpan);
    }

    public static async Task<SyntaxNode?> GetSyntaxNodeAsync(this Location location,
        CancellationToken cancellationToken = default)
    {
        if (location.SourceTree == null)
        {
            return null;
        }

        var root = await location.SourceTree.GetRootAsync(cancellationToken);
        return root?.FindNode(location.SourceSpan);
    }
}
