using System;
using Ardalis.Result.AspNetCore;
using Microsoft.AspNetCore.Mvc;

namespace RoslynRunner.Runs;

public static class RunEndpoints
{

    public static void MapRunEndpoints(this WebApplication app)
    {
        app.MapPost("/runs", async (
            IRunQueue queue,
            [FromBody] RunCommand runCommand,
            CancellationToken cancellationToken) =>
        {
            var result = await queue.Enqueue(runCommand, cancellationToken);
            return Results.Ok(result);
        });


        app.MapGet("/runs/{runId}", async (Guid runId, CommandRunningService commandRunningService, CancellationToken cancellationToken) =>
        {
            var result = await commandRunningService.WaitForTaskAsync(runId, TimeSpan.FromSeconds(30), cancellationToken);
            return result.ToMinimalApiResult();
        });

        app.MapPost("/rerun", async (
            IRunQueue queue,
            CancellationToken cancellationToken) =>
        {
            var id = await queue.ReRunLastEnqueuedCommand(cancellationToken);
            return Results.Ok(new Run(id));
        });

        app.MapDelete("/run", (
            ICancellationTokenManager cancellationTokenManager) =>
        {
            cancellationTokenManager.CancelCurrentTask();
            return Results.Empty;
        });
    }
}
