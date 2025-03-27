using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Threading.Channels;

namespace RoslynRunner;

public record RunParameters(RunCommand RunCommand, Guid RunId, ActivityTraceId TraceId, ActivitySpanId SpanId);

public interface IRunQueue
{
    Task<Guid> Enqueue(RunCommand runCommand, CancellationToken cancellationToken = default);
    Task<RunParameters> Dequeue(CancellationToken cancellationToken = default);
    Task<Guid?> ReRunLastEnqueuedCommand(CancellationToken cancellationToken = default);
}

public class RunQueue : IRunQueue
{
    private readonly Channel<RunParameters> _channel = Channel.CreateUnbounded<RunParameters>();
    private RunCommand? lastEnqueuedCommand = null;

    public async Task<Guid> Enqueue(RunCommand runCommand, CancellationToken cancellationToken = default)
    {
        Guid runId = Guid.NewGuid();
        using var activity = Activity.Current;
        var parameters = new RunParameters(runCommand, runId, activity!.TraceId, activity!.SpanId);
        await _channel.Writer.WriteAsync(parameters, cancellationToken);
        lastEnqueuedCommand = runCommand;
        return runId;
    }

    public async Task<RunParameters> Dequeue(CancellationToken cancellationToken = default)
    {
        var parameters = await _channel.Reader.ReadAsync(cancellationToken);

        return parameters;
    }

    public async Task<Guid?> ReRunLastEnqueuedCommand(CancellationToken cancellationToken)
    {
        if(lastEnqueuedCommand == null)
        {
            return null;
        }
        var id = await Enqueue(lastEnqueuedCommand, cancellationToken);
        return id;
    }
}
