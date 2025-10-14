using System.Collections.Concurrent;
using System.Diagnostics;
using Ardalis.Result;
using IdentityModel.OidcClient;
using Microsoft.Extensions.DependencyInjection;
using RoslynRunner.Abstractions;
using RoslynRunner.Data;
using Result = Ardalis.Result.Result;

namespace RoslynRunner;

public class CommandRunningService(
    IRunQueue runQueue,
    RunCommandProcessor processor,
    ILogger<CommandRunningService> logger,
    ICancellationTokenManager cancellationTokenManager,
    IServiceScopeFactory serviceScopeFactory) : BackgroundService
{
    private ConcurrentDictionary<Guid, TaskCompletionSource<RunContext>> _taskRuns = new();
    private ConcurrentQueue<RunParameters> _runParameters = new();
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var activity = new Activity("Queue Processing");
            Guid? runId = null;
            RunContext? runContext = null;
            RunParameters? currentRunParameters = null;
            var succeeded = false;
            try
            {
                var runParameters = await runQueue.Dequeue(stoppingToken);
                currentRunParameters = runParameters;
                RunContextAccessor.RunContext = new(runParameters.RunId);
                runContext = RunContextAccessor.RunContext;
                _runParameters.Enqueue(runParameters);
                // Start a new activity with the extracted context
                activity.SetParentId(runParameters.TraceId, runParameters.SpanId, ActivityTraceFlags.Recorded);
                activity.Start();

                var tcs = new TaskCompletionSource<RunContext>(TaskCreationOptions.RunContinuationsAsynchronously);
                _taskRuns[runParameters.RunId] = tcs;

                var task = processor.ProcessRunCommand(runParameters.RunCommand,
                    cancellationTokenManager.GetCancellationToken());
                runId = runParameters.RunId;
                await task;
                succeeded = true;
                tcs.TrySetResult(runContext);
            }
            catch (Exception e)
            {
                logger.LogError("failed processing task {error}", e);
                if (runContext is not null && runId.HasValue && _taskRuns.TryGetValue(runId.Value, out var tcs))
                {
                    runContext.Errors.Add($"Error processing task: {e.Message}\nStack Trace: {e.StackTrace}\nInner Exception: {e.InnerException?.Message}");
                    tcs.TrySetResult(runContext); // Mark as finished without throwing
                }
            }
            finally
            {
                if (runContext is not null)
                {
                    runContext.IsRunning = false;
                    if (currentRunParameters is not null)
                    {
                        using var scope = serviceScopeFactory.CreateScope();
                        var runHistoryService = scope.ServiceProvider.GetRequiredService<IRunHistoryService>();
                        await runHistoryService.RecordRunAsync(currentRunParameters, runContext, succeeded, CancellationToken.None);
                    }
                }
                RunContextAccessor.Clear();
                if (!activity.IsStopped)
                {
                    activity.Stop();
                }
            }
        }
    }

    public List<RunParameters> RunParameters => _runParameters.ToList();

    public RunContext? WaitForTask(Guid id, TimeSpan timeout)
    {
        if (!_taskRuns.TryGetValue(id, out var tcs))
        {
            return null;
        }

        if (!tcs.Task.Wait(timeout))
        {
            return null;
        }
        return tcs.Task.Result;
    }

    public async Task<Result<RunContext?>> WaitForTaskAsync(
        Guid id,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!_taskRuns.TryGetValue(id, out var tcs))
        {
            return Result.NotFound($"Task with ID {id} not found.");
        }
        try
        {
            var result = await tcs.Task.WaitAsync(timeout, cancellationToken);
            return result;
        }
        catch (TimeoutException)
        {
            // should instruct the client to wait
            return Result.Unavailable();
        }
        catch (Exception e)
        {
            return Result.Error($"Error waiting for task: {e.Message}");
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationTokenManager.CancelCurrentTask();
        return base.StopAsync(cancellationToken);
    }
}
