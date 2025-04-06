using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace RoslynRunner.ApiTests;

[SetUpFixture]
public class AppContext
{
    public static CustomWebAppFactory? App;

    public static JsonSerializerOptions JsonSerializerOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true
    };
    public static string? BaseDirectory = null;

    public static string BaseUrl => HttpClient!.BaseAddress!.ToString();
    public static HttpClient HttpClient { get; set; } = null!;
    [OneTimeSetUp]
    public virtual async Task Setup()
    {
        var startDirectory = AppDomain.CurrentDomain.BaseDirectory;
        for (var currentDirectory = startDirectory; currentDirectory != null; currentDirectory = Directory.GetParent(currentDirectory)?.FullName)
        {
            if (Directory.GetFiles(currentDirectory, "*.sln").Any())
            {
                BaseDirectory = currentDirectory;
                break;
            }
        }

        App = new CustomWebAppFactory();
        HttpClient = App.CreateClient();
        HttpClient.BaseAddress = App.BaseAddress;

        var pingResult = await HttpClient.GetAsync("/ping");
        Assert.That(pingResult.IsSuccessStatusCode, "Ping failed");
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        App?.Dispose();
        HttpClient?.Dispose();
    }
}
