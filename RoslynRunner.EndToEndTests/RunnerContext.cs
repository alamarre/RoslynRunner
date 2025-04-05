using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Playwright;

namespace RoslynRunner.EndToEndTests;

[SetUpFixture]
public class RunnerContext : RoslynRunner.ApiTests.AppContext
{
    private static IPlaywright? PlaywrightInstance;

    public static IAPIRequestContext ApiRequestContext { get; private set; } = null!;

    private static HttpClient HttpClient { get; set; } = null!;
    [OneTimeSetUp]
    public override async Task Setup()
    {
        await base.Setup();

        var playwright = await Playwright.CreateAsync();
        PlaywrightInstance = playwright;

        string? baseAddress = BaseUrl;
        ApiRequestContext = await playwright.APIRequest.NewContextAsync(new()
        {
            // All requests we send go to this API endpoint.
            BaseURL = baseAddress
        });
    }
}
