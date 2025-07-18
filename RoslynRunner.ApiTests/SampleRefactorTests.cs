using System;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RoslynRunner.Abstractions;
using RoslynRunner.Utilities.InvocationTrees;
using LegacyWebAppConverter;
using YamlDotNet.Serialization;

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

        string asyncFile = Path.Combine(sampleRoot, "ModernWebApi", "Services", "RecursiveServiceAsync.cs");
        if (File.Exists(asyncFile))
        {
            File.Delete(asyncFile);
        }
        var ctx1 = JsonSerializer.Serialize(new ConvertToMinimalApiContext("", asyncFile, "ModernWebApi.Services.RecursiveService"));
        RunCommand runCommand = new(
            ProcessorSolution: legacyWebAppConverterCsproj,
            ProcessorName: "LegacyWebAppConverter.ConvertToMinimalApi",
            ProcessorProjectName: "LegacyWebAppConverter",
            PrimarySolution: legacyWebappSln,
            PersistSolution: false,
            Context: ctx1
        );

        var runResponse = await ValidateRun(runCommand);

        var runResult = await runResponse.Content.ReadFromJsonAsync<RunContext>(AppContext.JsonSerializerOptions);
        Assert.That(runResult?.Errors, Is.Empty);
        Assert.That(runResult?.Output, Does.Contain($"Created file: {Path.GetFullPath(targetFile)}"));
        Assert.That(runResult?.Output, Does.Contain($"Created file: {Path.GetFullPath(asyncFile)}"));
        Assert.That(runResult?.IsRunning, Is.False);

        Assert.That(File.Exists(targetFile), Is.True);
        Assert.That(File.Exists(asyncFile), Is.True);
    }

    [Test]
    public async Task ApiCanRunAsyncConversion()
    {
        var baseDirectory = AppContext.BaseDirectory!;
        var sampleRoot = Path.Combine(baseDirectory, "samples", "LegacyWebApp");
        string targetFile = Path.Combine(sampleRoot, "ModernWebApi", "Services", "RecursiveServiceAsync.cs");
        if (File.Exists(targetFile))
        {
            File.Delete(targetFile);
        }
        Assert.That(File.Exists(targetFile), Is.False);
        var legacyWebappSln = Path.Combine(sampleRoot, "LegacyWebApp.sln");
        var request = new
        {
            targetSolution = legacyWebappSln,
            typeName = "ModernWebApi.Services.RecursiveService",
            outputFile = targetFile,
            methodName = (string?)null,
            maxTime = 300
        };

        var runResponse = await AppContext.HttpClient.PostAsJsonAsync("http://localhost:5000/tools/RoslynRunnerMcpTool/ConvertSyncToAsync", request);
        Assert.That((int)runResponse.StatusCode, Is.EqualTo(200));
        var runResult = await runResponse.Content.ReadFromJsonAsync<RunContext>(AppContext.JsonSerializerOptions);
        Assert.That(runResult?.Errors, Is.Empty);
        Assert.That(runResult?.Output.Single(), Is.EqualTo($"Created file: {Path.GetFullPath(targetFile)}"));
        Assert.That(File.Exists(targetFile), Is.True);

        var contents = File.ReadAllText(targetFile);
        Assert.That(contents.Contains("async"));
        Assert.That(contents.Contains("QueryAsync"));
    }

    [Test]
    public async Task CheckThatDependenciesAreTheSameWithCacheAndSymbolFinder()
    {
        var baseDirectory = AppContext.BaseDirectory!;
        var sampleRoot = Path.Combine(baseDirectory, "samples", "LegacyWebApp");
        // create temporary directory
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDirectory);

        var legacyWebappSln = Path.Combine(sampleRoot, "LegacyWebApp.sln");
        Assert.That(File.Exists(legacyWebappSln), Is.True);

        var uncachedRunResult = await GetCallChainResult(sampleRoot, legacyWebappSln, false);
        var cachedRunResult = await GetCallChainResult(sampleRoot, legacyWebappSln, true);

        // do an unordered equality check
        Assert.That(uncachedRunResult, Is.EquivalentTo(cachedRunResult));
    }

    private static async Task<IEnumerable<MethodCallInfo>> GetCallChainResult(string sampleRoot, string legacyWebappSln, bool useCache)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDirectory);

        RunCommand runCommand = new RunCommand(
                    ProcessorSolution: null,
                    ProcessorName: "CallChains",
                    ProcessorProjectName: null,
                    PrimarySolution: legacyWebappSln,
                    PersistSolution: false,
                    Context: JsonSerializer.Serialize(new InvocationTreeProcessorParameters(
                        StartingSymbol: "ModernWebApi.Controllers.SampleController",
                        MethodFilter: "MethodWithDependencies",
                        Diagrams:
                        [
                            new InvocationDiagram(
                        OutputPath: tempDirectory,
                        Name: "SampleEndpoint",
                        DiagramType: "JSON",
                        SeparateDiagrams: false,
                        InclusivePruneFilter: null,
                        WriteAllMethods: true,
                        Filter: null
                    )
                        ],
                        UseCache: useCache
                    ))
                );
        await ValidateRun(runCommand);
        // read file from temp directory
        var result = JsonSerializer.Deserialize<List<MethodCallInfo>>(File.ReadAllText(Path.Combine(tempDirectory, "SampleEndpoint.json")));
        return result!;
    }

    private static async Task<HttpResponseMessage> ValidateRun(RunCommand runCommand)
    {
        var response = await AppContext.HttpClient.PostAsJsonAsync("http://localhost:5000/runs", runCommand);
        Assert.That((int)response.StatusCode, Is.EqualTo(200));

        var run = await response.Content.ReadFromJsonAsync<Run>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        Assert.That(run, Is.Not.Null);
        Assert.That(run?.RunId, Is.Not.EqualTo(Guid.Empty));
        HttpResponseMessage runResponse = await AppContext.HttpClient.GetAsync($"http://localhost:5000/runs/{run.RunId}");
        while ((int)runResponse.StatusCode == StatusCodes.Status202Accepted)
        {
            runResponse = await AppContext.HttpClient.GetAsync($"http://localhost:5000/runs/{run.RunId}");
        }
        Assert.That((int)runResponse.StatusCode, Is.EqualTo(200));
        return runResponse;
    }
}
