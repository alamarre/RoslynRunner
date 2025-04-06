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

app.MapPost("/runs", async (
    IRunQueue queue,
    [FromBody] RunCommand runCommand,
    CancellationToken cancellationToken) =>
{
    var id = await queue.Enqueue(runCommand, cancellationToken);
    return Results.Ok(new Run(id));
});

app.MapPost("/rerun", async (
    IRunQueue queue,
    CancellationToken cancellationToken) =>
{
    var id = await queue.ReRunLastEnqueuedCommand(cancellationToken);
    return Results.Ok(new Run(id));
});

app.MapPost("/assemblies/global", async (
    [FromBody] LibraryReference reference,
    CancellationToken cancellationToken) =>
{
    await CompilationTools.LoadAssembly(reference.Path, cancellationToken, globalContext: true);
    return Results.Created();
});

app.MapGet("/runs", (CommandRunningService commandRunningService) =>
     Results.Ok(commandRunningService.RunParameters));

app.MapGet("/runs/{runId}", async (Guid runId, CommandRunningService commandRunningService, CancellationToken cancellationToken) =>
{
    var result = await commandRunningService.WaitForTaskAsync(runId, TimeSpan.FromSeconds(30), cancellationToken);
    return result.ToMinimalApiResult();
});

app.MapPost("/analyze", async (
    IRunQueue queue,
    [FromBody] RunCommand<AnalyzerContext> analyzeCommand,
    CancellationToken cancellationToken) =>
{
    await queue.Enqueue(analyzeCommand.ToRunCommand(), cancellationToken);
    return Results.Created();
});

app.MapDelete("/run", (
    ICancellationTokenManager cancellationTokenManager) =>
{
    cancellationTokenManager.CancelCurrentTask();
    return Results.Empty;
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
    [Description("Loads a solution for analysis")]
    public static async Task<string> LoadSolution(
        IRunQueue queue,
        [Description("The absolute path to the solution to load")] string solutionPath)
    {

        await Task.Delay(1000);
        return $"loaded {solutionPath}";

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
