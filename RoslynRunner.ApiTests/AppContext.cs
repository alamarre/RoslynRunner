using System.Diagnostics;
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

        if (BaseDirectory is not null)
        {
            await RestoreLegacySampleAsync(BaseDirectory);
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

    private static async Task RestoreLegacySampleAsync(string baseDirectory)
    {
        var solutionPath = Path.Combine(baseDirectory, "samples", "LegacyWebApp", "LegacyWebApp.sln");
        if (!File.Exists(solutionPath))
        {
            return;
        }

        var startInfo = new ProcessStartInfo("dotnet", $"restore \"{solutionPath}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(solutionPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.EnvironmentVariables["DOTNET_DISABLE_TERMINAL_LOGGER"] = "1";

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start dotnet restore process.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await Task.WhenAll(process.WaitForExitAsync(), stdoutTask, stderrTask);

        if (process.ExitCode != 0)
        {
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            throw new InvalidOperationException($"dotnet restore failed: {stderr}\n{stdout}");
        }
    }
}
