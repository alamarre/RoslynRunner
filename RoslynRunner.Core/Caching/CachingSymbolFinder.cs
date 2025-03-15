using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RoslynRunner.Core.Caching;

public static class CachingSymbolFinder
{
    public static async Task<IEnumerable<SymbolCallerInfo>> FindCallersAsync(ISymbol symbol, Solution solution,
        CancellationToken cancellationToken = default)
    {
        const string cacheKey = "caller-cache";
        if (!MemoryCache.Cache.TryGetValue(cacheKey, out var cached)
            || cached is not Dictionary<ISymbol, IEnumerable<SymbolCallerInfo>> symbolCallerCache)
        {
            MemoryCache.Cache[cacheKey] = symbolCallerCache =
                new Dictionary<ISymbol, IEnumerable<SymbolCallerInfo>>(SymbolEqualityComparer.Default);
        }

        if (symbolCallerCache.TryGetValue(symbol, out var callers))
        {
            return callers;
        }

        var symbols = await SymbolFinder.FindCallersAsync(symbol, solution, cancellationToken);
        symbolCallerCache[symbol] = symbols;
        return symbols;
    }

    public static async Task<IEnumerable<ISymbol>> FindImplementationsAsync(ISymbol symbol, Solution solution,
        CancellationToken cancellationToken = default)
    {
        const string cacheKey = "implementations-cache";
        if (!MemoryCache.Cache.TryGetValue(cacheKey, out var cached)
            || cached is not Dictionary<ISymbol, IEnumerable<ISymbol>> symbolCallerCache)
        {
            MemoryCache.Cache[cacheKey] = symbolCallerCache =
                new Dictionary<ISymbol, IEnumerable<ISymbol>>(SymbolEqualityComparer.Default);
        }

        if (symbolCallerCache.TryGetValue(symbol, out var implementations))
        {
            return implementations;
        }


        implementations =
            await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: cancellationToken);
        symbolCallerCache[symbol] = implementations;
        return implementations;
    }
}
