using Ardalis.Result;
using Ardalis.Result.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using RoslynRunner.Data;
using System.IO;
using System.Text.Json;

namespace RoslynRunner.Runs;

public static class RunEndpoints
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static void MapRunEndpoints(this WebApplication app)
    {
        app.MapPost("/runs", async (
            HttpRequest request,
            IRunQueue queue,
            IRunHistoryService runHistoryService,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(body))
            {
                return Results.BadRequest("Request body cannot be empty.");
            }

            RunCommand? runCommand = null;
            string? saveAsName = null;

            try
            {
                var runRequest = JsonSerializer.Deserialize<RunRequest>(body, SerializerOptions);
                if (runRequest?.RunCommand is not null)
                {
                    runCommand = runRequest.RunCommand;
                    saveAsName = runRequest.SaveAsName;
                }
            }
            catch
            {
                // Ignore and fall back to RunCommand parsing
            }

            if (runCommand is null)
            {
                try
                {
                    runCommand = JsonSerializer.Deserialize<RunCommand>(body, SerializerOptions);
                }
                catch
                {
                    return Results.BadRequest("Unable to deserialize run command.");
                }
            }

            if (runCommand is null)
            {
                return Results.BadRequest("Run command is required.");
            }

            if (!string.IsNullOrWhiteSpace(saveAsName))
            {
                await runHistoryService.SaveRunAsync(saveAsName, runCommand, cancellationToken);
            }

            var result = await queue.Enqueue(runCommand, cancellationToken);
            return Results.Ok(new Run(result));
        });


        app.MapGet("/runs/{runId}", async (Guid runId, CommandRunningService commandRunningService, CancellationToken cancellationToken) =>
        {
            var result = await commandRunningService.WaitForTaskAsync(runId, TimeSpan.FromSeconds(30), cancellationToken);
            if (result.IsUnavailable())
            {
                return Results.Accepted();
            }
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

        app.MapGet("/runs/recent", async (
            IRunHistoryService runHistoryService,
            CancellationToken cancellationToken) =>
        {
            var runs = await runHistoryService.GetRecentRunsAsync(cancellationToken);
            return Results.Ok(runs);
        });

        app.MapGet("/runs/saved", async (
            IRunHistoryService runHistoryService,
            CancellationToken cancellationToken) =>
        {
            var runs = await runHistoryService.GetSavedRunsAsync(cancellationToken);
            return Results.Ok(runs);
        });

        app.MapPost("/runs/saved", async (
            IRunHistoryService runHistoryService,
            [FromBody] SaveRunRequest request,
            CancellationToken cancellationToken) =>
        {
            await runHistoryService.SaveRunAsync(request.Name, request.RunCommand, cancellationToken);
            return Results.Ok();
        });

        app.MapDelete("/runs/saved/{name}", async (
            string name,
            IRunHistoryService runHistoryService,
            CancellationToken cancellationToken) =>
        {
            await runHistoryService.DeleteSavedRunAsync(name, cancellationToken);
            return Results.NoContent();
        });

        app.MapPost("/runs/by-name/{name}", async (
            string name,
            IRunHistoryService runHistoryService,
            IRunQueue queue,
            CancellationToken cancellationToken) =>
        {
            var runCommand = await runHistoryService.GetSavedRunCommandAsync(name, cancellationToken);
            if (runCommand is null)
            {
                return Results.NotFound();
            }

            var result = await queue.Enqueue(runCommand, cancellationToken);
            return Results.Ok(new Run(result));
        });
    }
}

public record RunRequest(RunCommand RunCommand, string? SaveAsName);

public record SaveRunRequest(string Name, RunCommand RunCommand);
