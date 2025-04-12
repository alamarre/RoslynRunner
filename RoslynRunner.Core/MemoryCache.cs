using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Tokens;

namespace RoslynRunner.Core;

public static class MemoryCache
{
    public static Dictionary<object, object> Cache = new();

    public static async Task<T> GetOrAddAsync<TKey, T>(TKey key, Func<Task<T>> valueFactory)
        where TKey : notnull
    {
        if (Cache.ContainsKey(key) && Cache[key] is T)
        {
            return (T)Cache[key];
        }

        T value = await valueFactory();
        Cache[key] = value!;
        return value;
    }

    public static async Task<T> GetOrAddAsync<TKey1, TKey2, T>(TKey1 key1, TKey2 key2, Func<Task<T>> valueFactory)
        where TKey1 : notnull
        where TKey2 : notnull
    {
        if (Cache.ContainsKey(key1) && Cache[key1] is Dictionary<TKey2, T> dictionary && dictionary.ContainsKey(key2))
        {
            return dictionary[key2];
        }

        var value = await valueFactory();
        if (!Cache.ContainsKey(key1))
        {
            Cache[key1] = new Dictionary<TKey2, T>();
        }
        ((Dictionary<TKey2, T>)Cache[key1])[key2] = value;
        return value;
    }
}
