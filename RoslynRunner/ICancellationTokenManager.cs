namespace RoslynRunner;

public interface ICancellationTokenManager
{
    void CancelCurrentTask();

    CancellationToken GetCancellationToken();
}

public class CancellationTokenManager : ICancellationTokenManager
{
    private CancellationTokenSource? _currentCancellationTokenSource;

    public void CancelCurrentTask()
    {
        if (_currentCancellationTokenSource == null) return;
        _currentCancellationTokenSource.Cancel();
        _currentCancellationTokenSource = null;
    }

    public CancellationToken GetCancellationToken()
    {
        // we're going to just recycle the token until it's cancelled
        if (_currentCancellationTokenSource == null) _currentCancellationTokenSource = new CancellationTokenSource();
        return _currentCancellationTokenSource!.Token;
    }
}
