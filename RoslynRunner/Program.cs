using System.ComponentModel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Build.Locator;
using ModelContextProtocol.Server;
using MudBlazor.Services;
using RoslynRunner;
using RoslynRunner.Core;
using RoslynRunner.SolutionProcessors;
using RoslynRunner.UI;
using Microsoft.AspNetCore.Builder.Extensions;
using System.Text.Json;
using Ardalis.Result.AspNetCore;
using RoslynRunner.Runs;

try
{
    MSBuildLocator.RegisterDefaults();
}
catch (Exception) { }

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
});

builder.Services.AddSingleton<IRunQueue, RunQueue>();

builder.Services.AddSingleton<RunCommandProcessor>();
builder.Services.AddSingleton<ICancellationTokenManager, CancellationTokenManager>();

builder.Services.AddSingleton<CommandRunningService>();
builder.Services.AddHostedService<CommandRunningService>(ctx => ctx.GetRequiredService<CommandRunningService>());
builder.AddServiceDefaults();
builder.Services.AddMcpServer()
    .WithToolsFromAssembly();

var app = builder.Build();
app.UseRouting();

app.UseStaticFiles();
app.UseAntiforgery();

//if( app.Environment.IsDevelopment() ) {
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.EnablePersistAuthorization();
});
//}

app.MapRunEndpoints();

app.MapPost("/assemblies/global", async (
    [FromBody] LibraryReference reference,
    CancellationToken cancellationToken) =>
{
    await CompilationTools.LoadAssembly(reference.Path, cancellationToken, globalContext: true);
    return Results.Created();
});

app.MapPost("/analyze", async (
    IRunQueue queue,
    [FromBody] RunCommand<AnalyzerContext> analyzeCommand,
    CancellationToken cancellationToken) =>
{
    await queue.Enqueue(analyzeCommand.ToRunCommand(), cancellationToken);
    return Results.Created();
});

app.MapGet("/solutions", (RunCommandProcessor runCommandProcessor) =>
    Results.Ok((object?)runCommandProcessor.GetPersistedSolutions()));

app.MapDelete("/solutions", (
    [FromBody] List<string> solutionPaths,
    RunCommandProcessor runCommandProcessor) =>
{
    foreach (var solution in solutionPaths)
    {
        runCommandProcessor.RemovePersistedSolution(solution);
    }

    return Results.Empty;
});

app.MapGet("/ping", () =>
{
    return Results.Ok("Pong");
});


app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();


app.MapMcp();

await app.RunAsync();

public record Run(Guid? RunId);

[McpServerToolType]
public static class RoslynRunnerTool
{
    [McpServerTool]
    [Description("Loads a solution to be cached for future analysis")]
    public static async Task<string> LoadSolution(
        IRunQueue queue,
        CommandRunningService commandRunningService,
        [Description("The absolute path of the project or solution to load")] string solutionPath,
        [Description("Whether to preload symbols in the cache")] bool cacheSymbols,
        [Description("The name of the project to cache symbols from")] string? projectName,
        CancellationToken cancellationToken)
    {
        var context = new RunCommand(
            PrimarySolution: solutionPath,
            PersistSolution: true,
            ProcessorSolution: null,
            ProcessorName: "SolutionLoader",
            AssemblyLoadContextPath: null,
            Context: null);
        Guid runId = await queue.Enqueue(context, cancellationToken);

        var result = await commandRunningService.WaitForTaskAsync(runId, TimeSpan.FromSeconds(120), cancellationToken);
        return JsonSerializer.Serialize(result.Value);
    }

    [McpServerTool]
    [Description("Run an analyzer on a target project")]
    public static async Task<string> RunAnalyzer(
        IRunQueue queue,
        CommandRunningService commandRunningService,
        [Description("The absolute path of the project to analyze, or the solution which contains it")] string targetProjectPath,
        [Description("The name of the target project")] string targetProjectName,
        [Description("The absolute path to the analyzer project")] string analyzerProjectPath,
        [Description("The fully qualified name of the analyzer")] string analyzerName,
        CancellationToken cancellationToken)
    {
        var context = new RunCommand<AnalyzerContext>(
            PrimarySolution: targetProjectPath,
            PersistSolution: false,
            ProcessorName: "AnalyzerRunner",
            AssemblyLoadContextPath: null,
            Context: new AnalyzerContext(analyzerProjectPath,
            targetProjectName,
            new List<string> { analyzerName })
        );
        Guid runId = await queue.Enqueue(context.ToRunCommand(), cancellationToken);

        var result = await commandRunningService.WaitForTaskAsync(runId, TimeSpan.FromSeconds(120), cancellationToken);
        return JsonSerializer.Serialize(result.Value);
    }
}


public partial class Program { }
