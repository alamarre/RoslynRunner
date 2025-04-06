using System.Collections.Concurrent;
using System.Diagnostics;

namespace RoslynRunner;

public class CommandRunningService(
    IRunQueue runQueue,
    RunCommandProcessor processor,
    ILogger<CommandRunningService> logger,
    ICancellationTokenManager cancellationTokenManager) : BackgroundService
{
    private ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _taskRuns = new();
    private ConcurrentQueue<RunParameters> _runParameters = new();
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var activity = new Activity("Queue Processing");
            Guid? runId = null;
            try
            {
                var runParameters = await runQueue.Dequeue(stoppingToken);
                _runParameters.Enqueue(runParameters);
                // Start a new activity with the extracted context
                activity.SetParentId(runParameters.TraceId, runParameters.SpanId, ActivityTraceFlags.Recorded);
                activity.Start();

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _taskRuns[runParameters.RunId] = tcs;

                var task = processor.ProcessRunCommand(runParameters.RunCommand,
                    cancellationTokenManager.GetCancellationToken());
                runId = runParameters.RunId;
                await task;
                tcs.TrySetResult(true);
            }
            catch (Exception e)
            {
                logger.LogError("failed processing task {error}", e);
                if (runId.HasValue && _taskRuns.TryGetValue(runId.Value, out var tcs))
                {
                    tcs.TrySetResult(false); // Mark as finished without throwing
                }
            }
            finally
            {
                if (!activity.IsStopped)
                {
                    activity.Stop();
                }
            }
        }
    }

    public List<RunParameters> RunParameters => _runParameters.ToList();

    public bool? WaitForTask(Guid id, TimeSpan timeout)
    {
        if (!_taskRuns.TryGetValue(id, out var tcs))
        {
            return null;
        }

        return tcs.Task.Wait(timeout) && tcs.Task.Result;
    }

    public async Task<bool?> WaitForTaskAsync(Guid id, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!_taskRuns.TryGetValue(id, out var tcs))
        {
            return null;
        }

        var result = await tcs.Task.WaitAsync(timeout, cancellationToken);
        return result;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationTokenManager.CancelCurrentTask();
        return base.StopAsync(cancellationToken);
    }
}
