namespace Blinky.Core;

// Audio libraries may dispatch a stopped callback after Stop/Dispose returns.
// Retired sessions must not change the failure state of a replacement stream.
public sealed class CaptureSessionGuard
{
    private readonly object _gate = new();
    private long _generation;
    private bool _active;
    private string? _failure;

    public string? Failure { get { lock (_gate) return _failure; } }

    public long Begin()
    {
        lock (_gate)
        {
            _failure = null;
            _active = true;
            return ++_generation;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _active = false;
            _failure = null;
            ++_generation;
        }
    }

    public bool IsCurrent(long generation)
    {
        lock (_gate) return _active && generation == _generation;
    }

    public bool TryFail(long generation, string message)
    {
        lock (_gate)
        {
            if (!_active || generation != _generation || _failure != null) return false;
            _failure = message;
            return true;
        }
    }
}
