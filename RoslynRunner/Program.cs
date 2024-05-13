using Microsoft.AspNetCore.Mvc;
using Microsoft.Build.Locator;
using MudBlazor.Services;
using RoslynRunner;
using RoslynRunner.Core;
using RoslynRunner.SolutionProcessors;
using RoslynRunner.UI;

MSBuildLocator.RegisterDefaults();

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

builder.Services.AddHostedService<CommandRunningService>();
builder.AddServiceDefaults();

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

app.MapPost("/run", async (
    IRunQueue queue,
    [FromBody] RunCommand runCommand,
    CancellationToken cancellationToken) =>
{
    await queue.Enqueue(runCommand, cancellationToken);
    return Results.Created();
});

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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
