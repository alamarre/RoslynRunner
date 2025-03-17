using System.Text.Json;
using Microsoft.Playwright;

namespace RosylnRunner.EndToEndTests;

public static class IApiResponseExtensions
{
    private static JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    public static async Task<T?> FromJson<T>(this IAPIResponse response) where T : class
    {
        return await response.JsonAsync<T>(JsonSerializerOptions);
    }
}
