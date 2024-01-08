using System.Threading.Channels;

namespace RoslynRunner;

public interface IRunQueue
{
    Task Enqueue(RunCommand runCommand, CancellationToken cancellationToken = default);
    Task<RunCommand> Dequeue(CancellationToken cancellationToken = default);
}

public class RunQueue : IRunQueue
{
    private readonly Channel<RunCommand> _channel = Channel.CreateUnbounded<RunCommand>();
    
    public async Task Enqueue(RunCommand runCommand, CancellationToken cancellationToken = default)
    {
        await _channel.Writer.WriteAsync(runCommand, cancellationToken);
    }

    public async Task<RunCommand> Dequeue(CancellationToken cancellationToken = default)
    {
        return await _channel.Reader.ReadAsync(cancellationToken);
    }
}