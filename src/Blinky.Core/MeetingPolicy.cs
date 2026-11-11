namespace Blinky.Core;

public enum MeetingAction { None, OfferRecording, StopManagedRecording }

// Meeting signals are hints from active microphone sessions. Only a recording
// explicitly accepted from the prompt may be automatically stopped.
public sealed class MeetingPolicy
{
    private DateTimeOffset? _activeSince, _lastActive;
    private bool _offered, _managed;
    public bool HasMeeting => _lastActive != null;
    public void AcceptPrompt() { _managed = true; _offered = true; }
    public void ReleaseRecording() => _managed = false;
    public void Reset() { _activeSince = null; _lastActive = null; _offered = false; _managed = false; }

    public MeetingAction Observe(bool? active, bool recording, DateTimeOffset now, TimeSpan endDelay)
    {
        if (active == null)
        {
            // A failed scan must never be interpreted as a meeting ending.
            if (_lastActive != null) _lastActive = now;
            _activeSince = null;
            return MeetingAction.None;
        }
        if (active.Value)
        {
            _activeSince ??= now; _lastActive = now;
            if (recording) _offered = true;
            if (!_offered && !recording && now - _activeSince >= TimeSpan.FromSeconds(3))
            {
                _offered = true;
                return MeetingAction.OfferRecording;
            }
        }
        else
        {
            _activeSince = null;
            if (_lastActive != null && now - _lastActive >= endDelay)
            {
                bool stop = _managed && recording;
                Reset();
                if (stop) return MeetingAction.StopManagedRecording;
            }
        }
        return MeetingAction.None;
    }
}
