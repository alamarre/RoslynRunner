using System;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Playwright;
using NUnit.Framework;

namespace RoslynRunner.EndToEndTests;

[SetUpFixture]
public class RunnerContext : RoslynRunner.ApiTests.AppContext
{
    private static IPlaywright? PlaywrightInstance;

    public static IAPIRequestContext ApiRequestContext { get; private set; } = null!;

    internal const string PlaywrightRunEnvironmentVariable = "RUN_PLAYWRIGHT_TESTS";

    [OneTimeSetUp]
    public override async Task Setup()
    {
        var shouldRunPlaywrightTests = Environment.GetEnvironmentVariable(PlaywrightRunEnvironmentVariable);
        var playwrightEnabled = string.Equals(shouldRunPlaywrightTests, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(shouldRunPlaywrightTests, "1", StringComparison.OrdinalIgnoreCase);

        if (!playwrightEnabled)
        {
            Assert.Ignore($"Playwright tests skipped. Set {PlaywrightRunEnvironmentVariable}=true to enable.");
        }

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
