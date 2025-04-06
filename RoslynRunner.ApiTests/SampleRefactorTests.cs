using System;
using System.Net.Http.Json;
using RoslynRunner.Abstractions;

namespace RoslynRunner.ApiTests;

public class SampleRefactorTests
{
    [Test]
    public async Task ApiCanRunSampleConversion()
    {
        var baseDirectory = AppContext.BaseDirectory!;
        var sampleRoot = Path.Combine(baseDirectory, "samples", "LegacyWebApp");
        string targetFile = Path.Combine(sampleRoot, "ModernWebApi", "Endpoints", "SampleEndpoint.cs");
        if (File.Exists(targetFile))
        {
            File.Delete(targetFile);
        }
        Assert.That(File.Exists(targetFile), Is.False);
        var legacyWebappSln = Path.Combine(sampleRoot, "LegacyWebApp.sln");
        Assert.That(File.Exists(legacyWebappSln), Is.True);

        var legacyWebAppConverterCsproj = Path.Combine(sampleRoot, "LegacyWebAppConverter", "LegacyWebAppConverter.csproj");

        RunCommand runCommand = new(
            ProcessorSolution: legacyWebAppConverterCsproj,
            ProcessorName: "LegacyWebAppConverter.ConvertToMinimalApi",
            ProcessorProjectName: "LegacyWebAppConverter",
            PrimarySolution: legacyWebappSln,
            PersistSolution: false
        );

        var response = await AppContext.HttpClient.PostAsJsonAsync("http://localhost:5000/runs", runCommand);
        Assert.That((int)response.StatusCode, Is.EqualTo(200));
        var run = await response.Content.ReadFromJsonAsync<Run>();
        Assert.That(run, Is.Not.Null);
        Assert.That(run?.RunId, Is.Not.EqualTo(Guid.Empty));
        var runResponse = await AppContext.HttpClient.GetAsync($"http://localhost:5000/runs/{run.RunId}");

        Assert.That((int)runResponse.StatusCode, Is.EqualTo(200));

        var runResult = await runResponse.Content.ReadFromJsonAsync<RunContext>(AppContext.JsonSerializerOptions);
        Assert.That(runResult?.IsRunning, Is.False);
        Assert.That(runResult?.Errors, Is.Empty);
        Assert.That(File.Exists(targetFile), Is.True);
    }
}
