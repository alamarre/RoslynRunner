using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Threading.Channels;

namespace RoslynRunner;

public record RunParameters(RunCommand RunCommand, ActivityTraceId TraceId, ActivitySpanId SpanId);

public interface IRunQueue
{
    Task Enqueue(RunCommand runCommand, CancellationToken cancellationToken = default);
    Task<RunParameters> Dequeue(CancellationToken cancellationToken = default);
    Task<bool> ReRunLastEnqueuedCommand(CancellationToken cancellationToken = default);
}

public class RunQueue : IRunQueue
{
    private readonly Channel<RunParameters> _channel = Channel.CreateUnbounded<RunParameters>();
    private RunCommand? lastEnqueuedCommand = null;

    public async Task Enqueue(RunCommand runCommand, CancellationToken cancellationToken = default)
    {
        using var activity = Activity.Current;
        var parameters = new RunParameters(runCommand, activity!.TraceId, activity!.SpanId);
        await _channel.Writer.WriteAsync(parameters, cancellationToken);
        lastEnqueuedCommand = runCommand;
    }

    public async Task<RunParameters> Dequeue(CancellationToken cancellationToken = default)
    {
        var parameters = await _channel.Reader.ReadAsync(cancellationToken);

        return parameters;
    }

    public async Task<bool> ReRunLastEnqueuedCommand(CancellationToken cancellationToken)
    {
       if(lastEnqueuedCommand == null)
        {
            return false;
        }
        await Enqueue(lastEnqueuedCommand);
        return true;
    }
}
