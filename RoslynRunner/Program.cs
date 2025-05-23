using System.ComponentModel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Build.Locator;
using ModelContextProtocol.Server;
using MudBlazor.Services;
using RoslynRunner;
using RoslynRunner.Core;
using RoslynRunner.SolutionProcessors;
using RoslynRunner.UI;
using System.Text.Json;
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




public partial class Program { }
