using System;

namespace RoslynRunner.Abstractions;

public class RunContext(Guid jobId)
{
    public bool IsRunning { get; set; } = true;
    public Guid JobId => jobId;
    public List<object> Errors = new();
    public List<object> Output = new();
}

public static class RunContextAccessor
{
    private static readonly AsyncLocal<RunContext?> _context = new();

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
