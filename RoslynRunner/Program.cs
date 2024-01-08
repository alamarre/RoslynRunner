using Microsoft.AspNetCore.Mvc;
using Microsoft.Build.Locator;
using RoslynRunner;
using RoslynRunner.SolutionProcessors;

MSBuildLocator.RegisterDefaults();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => { });

builder.Services.AddSingleton<IRunQueue, RunQueue>();

builder.Services.AddSingleton<RunCommandProcessor>();

builder.Services.AddHostedService<CommandRunningService>();

var app = builder.Build();

//if( app.Environment.IsDevelopment() ) {
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.EnablePersistAuthorization();
});
//}

app.MapGet("/", () => "Hello World!");

app.MapPost("/run", async (
    IRunQueue queue,
    [FromBody] RunCommand runCommand,
    CancellationToken cancellationToken) =>
{
    await queue.Enqueue(runCommand, cancellationToken);
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
    Results.Ok((object?)runCommandProcessor.GetPeristedSolutions()));

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

await app.RunAsync();

