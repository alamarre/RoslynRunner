namespace RoslynRunner;

public enum RunStatus
{
    Running,
    Stopped
}
public interface IStatusProvider
{
    RunStatus GetCurrentState();
}