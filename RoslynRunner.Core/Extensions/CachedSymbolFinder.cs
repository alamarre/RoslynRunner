using Microsoft.CodeAnalysis;

namespace RoslynRunner.Core.Extensions;

public class CachedSymbolFinder
{
    public SymbolCache SymbolCache { get; }

    public static async Task<CachedSymbolFinder> FromCache(Solution solution,
        string? projectName = null,
        CancellationToken cancellationToken = default)
    {
        var resultCache = await MemoryCache.GetOrAddAsync<string, string, SymbolCache>("solution_cache", solution.FilePath!, async () =>
        {
            var cache = await solution.BuildSymbolCacheAsync(projectName, cancellationToken);
            return cache;
        });
        return new CachedSymbolFinder(resultCache);
    }

    public CachedSymbolFinder(SymbolCache symbolCache)
    {
        SymbolCache = symbolCache;
    }

    public static string GetFullyQualifiedMetadataName(ISymbol symbol)
    {
        return symbol.GetFullMetadataName();
    }

    public List<ISymbol>? FindTypeImplementations(ISymbol symbol)
    {
        string key = GetFullyQualifiedMetadataName(symbol);
        return SymbolCache.TypeImplementations.TryGetValue(key, out var implementations) ? implementations : null;
    }

    public List<IMethodSymbol>? FindCallers(IMethodSymbol method)
    {
        return SymbolCache.CallerCache.TryGetValue(method, out var callers) ? callers : null;
    }

    public MethodData? GetMethodData(IMethodSymbol methodSymbol)
    {
        return SymbolCache.MethodCache.TryGetValue(GetFromOwnCompilation(methodSymbol), out var methodData) ? methodData : null;
    }

    public HashSet<IMethodSymbol>? GetImplementations(IMethodSymbol methodSymbol)
    {
        return SymbolCache.ImplementationCache.GetValueOrDefault(methodSymbol.OriginalDefinition.GetMethodId());
    }


    public List<IMethodSymbol> GetMethods(string typeMetadataName, string? methodName)
    {
        List<IMethodSymbol> methods = new();
        var type = GetSymbolByMetadataName(typeMetadataName);
        if (type is null)
        {
            return methods;
        }

        if (string.IsNullOrEmpty(methodName))
        {
            return type.GetMembers().OfType<IMethodSymbol>().ToList();
        }
        return type.GetMembers(methodName).OfType<IMethodSymbol>().ToList();
    }


    /// <summary>
    /// This method is necessary because the method symbol from the compilation of a different DLL may will match the original symbol from its own compilation,
    /// </summary>
    /// <param name="methodSymbol">The method symbol to start from</param>
    /// <returns></returns>
    public IMethodSymbol GetFromOwnCompilation(IMethodSymbol methodSymbol)
    {
        if (SymbolCache.MethodNameCache.TryGetValue(methodSymbol.OriginalDefinition.GetMethodId(), out var cachedMethodSymbol))
        {
            return cachedMethodSymbol;
        }
        return methodSymbol;
    }

    public INamedTypeSymbol? GetSymbolByMetadataName(string metadataName)
    {
        return SymbolCache.MetadataNameCache.TryGetValue(metadataName, out var symbol) ? symbol : null;
    }
}
