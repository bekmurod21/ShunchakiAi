namespace ShunchakiAi;

/// <summary>
/// Ctrl+C handling: while a turn runs, Ctrl+C cancels only that turn; when idle at the
/// prompt it exits the process as usual.
/// </summary>
public sealed class TurnCancellation : IDisposable
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _current;

    public TurnCancellation() => Console.CancelKeyPress += OnCancelKeyPress;

    public CancellationToken BeginTurn()
    {
        lock (_gate)
        {
            _current?.Dispose();
            _current = new CancellationTokenSource();
            return _current.Token;
        }
    }

    public void EndTurn()
    {
        lock (_gate)
        {
            _current?.Dispose();
            _current = null;
        }
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        lock (_gate)
        {
            if (_current is { IsCancellationRequested: false } cts)
            {
                e.Cancel = true; // keep the process alive, cancel the running turn
                cts.Cancel();
            }
        }
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        EndTurn();
    }
}
