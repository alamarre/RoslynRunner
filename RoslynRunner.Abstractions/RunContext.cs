using System;

namespace RoslynRunner.Abstractions;

public class RunContext(Guid jobId)
{
    public bool IsRunning { get; set; } = true;
    public Guid JobId => jobId;
    public List<string> Errors { get; set; } = new();
    public List<string> Output { get; set; } = new();
}

public static class RunContextAccessor
{
    private static readonly AsyncLocal<RunContext?> _context = new();

    public static bool TryGet(out RunContext? runContext)
    {
        runContext = _context.Value;
        return runContext is not null;
    }

    public static RunContext RunContext
    {
        get
        {
            if (_context.Value is null)
            {
                throw new InvalidOperationException("RunContext is not set. Ensure this code is run within a job scope.");
            }
            return _context.Value;
        }
        set
        {
            _context.Value = value;
        }
    }

    public static void Clear()
    {
        _context.Value = null;
    }
}
