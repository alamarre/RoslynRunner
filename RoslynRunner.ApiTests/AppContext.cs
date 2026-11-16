using System.Diagnostics;
using System.Text.Json;

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
    private static string? _databasePath;

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

        var tempDbFile = Path.Combine(Path.GetTempPath(), $"runhistory_tests_{Guid.NewGuid():N}.db");
        _databasePath = tempDbFile;
        Environment.SetEnvironmentVariable("ConnectionStrings__RunDatabase", $"Data Source={tempDbFile}");

        App = new CustomWebAppFactory();
        HttpClient = App.CreateClient();

        var pingResult = await HttpClient.GetAsync("/ping");
        Assert.That(pingResult.IsSuccessStatusCode, "Ping failed");
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try
        {
            App?.Dispose();
            HttpClient?.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__RunDatabase", null);

            if (!string.IsNullOrEmpty(_databasePath))
            {
                TryDeleteSqliteArtifacts(_databasePath);
                _databasePath = null;
            }
        }
    }

    private static async Task RestoreLegacySampleAsync(string baseDirectory)
    {
        var solutionPath = Path.Combine(baseDirectory, "samples", "LegacyWebApp", "LegacyWebApp.sln");
        if (!File.Exists(solutionPath))
        {
            return;
        }

        var startInfo = new ProcessStartInfo("dotnet", $"restore \"{solutionPath}\" --disable-build-servers")
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

        // Reused MSBuild workers can inherit these pipes and keep ReadToEndAsync
        // waiting after restore exits. Disable reuse above and bound the reads too.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(), stdoutTask, stderrTask).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            throw new TimeoutException($"dotnet restore for {solutionPath} exceeded two minutes.");
        }

        if (process.ExitCode != 0)
        {
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            throw new InvalidOperationException($"dotnet restore failed: {stderr}\n{stdout}");
        }
    }

    private static void TryDeleteSqliteArtifacts(string databasePath)
    {
        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Ignore clean-up failures – the temp files will be removed automatically later.
            }
        }

        TryDelete(databasePath);
        TryDelete(databasePath + "-shm");
        TryDelete(databasePath + "-wal");
    }
}
