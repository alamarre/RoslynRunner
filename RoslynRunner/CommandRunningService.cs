using System.Diagnostics;

namespace RoslynRunner;

public class CommandRunningService(
    IRunQueue runQueue,
    RunCommandProcessor processor,
    ILogger<CommandRunningService> logger,
    ICancellationTokenManager cancellationTokenManager) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var activity = new Activity("Queue Processing");
            try
            {
                var runParameters = await runQueue.Dequeue(stoppingToken);
                // Start a new activity with the extracted context
                activity.SetParentId(runParameters.TraceId, runParameters.SpanId, ActivityTraceFlags.Recorded);
                activity.Start();


                await processor.ProcessRunCommand(runParameters.RunCommand,
                    cancellationTokenManager.GetCancellationToken());
            }
            catch (Exception e)
            {
                logger.LogError("failed processing task {error}", e);
            }
            finally
            {
                if (!activity.IsStopped) activity.Stop();
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationTokenManager.CancelCurrentTask();
        return base.StopAsync(cancellationToken);
    }
}
