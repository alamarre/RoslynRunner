using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace RoslynRunner.Core;

public sealed partial class LoggingProgressReporter(ILogger? logger) : IProgress<ProjectLoadProgress>
{
    [LoggerMessage(
        EventId = 0,
        Level = LogLevel.Information,
        Message = "{Operation} {ElapsedTime} {ProjectDisplay}")]
    private static partial void LogReport(ILogger logger, string operation, TimeSpan elapsedTime,
        string projectDisplay);

    public void Report(ProjectLoadProgress loadProgress)
    {
        if (logger == null)
        {
            return;
        }

        var projectDisplay = Path.GetFileName(loadProgress.FilePath);
        if (loadProgress.TargetFramework != null)
        {
            projectDisplay += $" ({loadProgress.TargetFramework})";
        }

        LogReport(logger, loadProgress.Operation.ToString(), loadProgress.ElapsedTime, projectDisplay);
    }
}
