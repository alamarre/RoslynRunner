using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Playwright;

namespace RosylnRunner.EndToEndTests;

[SetUpFixture]
public class RunnerContext
{
    public static CustomWebAppFactory? App;
    
    private static IPlaywright? PlaywrightInstance;
    
    public static string? BaseDirectory = null;
    
    public static IAPIRequestContext ApiRequestContext { get; private set; } = null!;
    
    public static string BaseUrl => HttpClient!.BaseAddress!.ToString();
    private static HttpClient HttpClient { get; set; } = null!;
    [OneTimeSetUp]
    public async Task Setup()
    {
        var startDirectory = AppDomain.CurrentDomain.BaseDirectory;
        for(var currentDirectory = startDirectory; currentDirectory != null; currentDirectory = Directory.GetParent(currentDirectory)?.FullName)
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
        
        var playwright = await Playwright.CreateAsync();
        PlaywrightInstance = playwright;

        string? baseAddress = App.BaseAddress?.ToString();
        ApiRequestContext = await playwright.APIRequest.NewContextAsync( new() {
            // All requests we send go to this API endpoint.
            BaseURL = baseAddress
        } );
    }
    
    [OneTimeTearDown]
    public void TearDown()
    {
        App?.Dispose();
        HttpClient?.Dispose();
    }
}
