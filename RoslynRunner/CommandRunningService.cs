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
            try
            {
                RunCommand runCommand = await runQueue.Dequeue(stoppingToken);
                await processor.ProcessRunCommand(runCommand, cancellationTokenManager.GetCancellationToken());
            }
            catch (Exception e)
            {
                logger.LogError("failed processing task {error}", e);
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationTokenManager.CancelCurrentTask();
        return base.StopAsync(cancellationToken);
    }
}