namespace RoslynRunner;

public class CommandRunningService(
    IRunQueue runQueue, 
    RunCommandProcessor processor,
    ILogger<CommandRunningService> logger) : BackgroundService
{
    private CancellationTokenSource? _currentCancellationTokenSource;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            _currentCancellationTokenSource = new CancellationTokenSource();
            try
            {
                RunCommand runCommand = await runQueue.Dequeue(stoppingToken);
                await processor.ProcessRunCommand(runCommand, _currentCancellationTokenSource.Token);
            }
            catch (Exception e)
            {
                logger.LogError("failed processing task {error}", e);
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _currentCancellationTokenSource?.Cancel();
        return base.StopAsync(cancellationToken);
    }
}