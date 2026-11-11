using Blinky.Core;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}

Check(RecordingProfile.Fit(3840, 2160) == (1920, 1080), "4K is reduced to 1080p");
Check(RecordingProfile.Fit(1080, 1920) == (606, 1080), "portrait keeps aspect ratio within encoder bounds");
Check(RecordingProfile.Fit(1280, 720) == (1280, 720), "small sources are never enlarged");
Check(RecordingProfile.Fit(1921, 1081) is var odd && odd.Width % 2 == 0 && odd.Height % 2 == 0, "dimensions are even");
Check(RecordingProfile.Fit(5120, 1440) == (1920, 540), "ultrawide aspect ratio preserved");
bool rejected = false;
try { RecordingProfile.Fit(0, 1080); } catch (ArgumentOutOfRangeException) { rejected = true; }
Check(rejected, "invalid source dimensions rejected");

var profile = RecordingProfile.All[1];
var reserve = DiskPolicy.ReserveBytes(profile.VideoBitrate);
Check(reserve >= 512L * 1024 * 1024, "minimum disk reserve is 512 MiB");
Check(DiskPolicy.MustStop(reserve, profile.VideoBitrate), "recording stops exactly at reserve");
Check(DiskPolicy.MustStop(0, profile.VideoBitrate), "full disks cannot start");
Check(!DiskPolicy.MustStop(reserve + 1, profile.VideoBitrate), "space above reserve is usable");
Check(DiskPolicy.ReserveBytes(100_000_000) > reserve, "reserve grows for high bitrate recording");

var validated = new Settings { Profile = "obsolete", MicrophoneVolume = -3, SystemVolume = 8, OutputFolder = " " }.Validated();
Check(validated.Profile == "Balanced", "unknown saved presets fall back safely");
Check(validated.MicrophoneVolume == 0.05 && validated.SystemVolume == 1, "neither mandatory audio source can be muted in settings");
Check(!string.IsNullOrWhiteSpace(validated.OutputFolder), "empty output folder gets default");
Check(new Settings { MicrophoneVolume = double.NaN }.Validated().MicrophoneVolume == 0.8, "non-finite gain gets a valid default");

var temp = Path.Combine(Path.GetTempPath(), "blinky-tests-" + Guid.NewGuid().ToString("N"));
try
{
    var path = Path.Combine(temp, "preferences.json");
    Check(JsonStore.Load<Settings>(path) == null, "first run has no saved settings");
    var original = new Settings { OutputFolder = Path.Combine(temp, "Vídeos"), MicrophoneId = "mic-1", SystemAudioId = "speakers-1" };
    JsonStore.Save(path, original);
    Check(JsonStore.Load<Settings>(path) == original, "device IDs and Unicode paths round trip");
    JsonStore.Save(path, original with { Profile = "Compact" });
    Check(JsonStore.Load<Settings>(path)?.Profile == "Compact", "atomic preferences overwrite works");
    Check(!File.Exists(path + ".tmp"), "successful save leaves no temporary file");
    File.WriteAllText(path, "{incomplete");
    Check(JsonStore.Load<Settings>(path) == null, "corrupt settings do not crash startup");
    var session = new SessionJournal(Path.Combine(temp, "session.recording.mp4"), DateTimeOffset.UtcNow, "recording");
    JsonStore.Save(path, session);
    Check(JsonStore.Load<SessionJournal>(path) == session, "unfinished recording journal survives restart");
    JsonStore.Save(path, session with { State = "interrupted", Message = "Microphone disconnected" });
    Check(JsonStore.Load<SessionJournal>(path)?.Message == "Microphone disconnected", "failure reason survives restart");
}
finally { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
// Reproduce the UI-dispatch ordering: stop old streams, open replacements,
// then receive the old streams' deferred RecordingStopped notifications.
var sessions = new CaptureSessionGuard();
var oldSession = sessions.Begin();
sessions.Invalidate();
var newSession = sessions.Begin();
Check(!sessions.TryFail(oldSession, "System audio capture stopped."), "delayed stop from replaced system audio stream is ignored");
Check(!sessions.TryFail(oldSession, "Microphone capture stopped."), "delayed stop from replaced microphone stream is ignored");
Check(sessions.Failure == null, "old callbacks leave replacement capture healthy");
Check(sessions.TryFail(newSession, "System audio disconnected"), "genuine current device failure is reported");
Check(sessions.Failure == "System audio disconnected", "current device failure remains visible to preflight");
Check(!sessions.TryFail(newSession, "Duplicate callback"), "duplicate failure notifications are suppressed");
sessions.Invalidate();
Check(!sessions.IsCurrent(newSession), "queued UI failure is ignored after intentional shutdown");
Check(!sessions.TryFail(newSession, "Stopped during dispose"), "intentional shutdown does not become a device failure");
Check(sessions.Failure == null, "intentional shutdown clears obsolete failure");
var recoveredSession = sessions.Begin();
Check(sessions.IsCurrent(recoveredSession), "new recording can start after an audio failure");
Check(sessions.TryFail(recoveredSession, "Microphone disconnected"), "microphone failure remains mandatory after recovery");
var meeting = new MeetingPolicy();
var now = DateTimeOffset.UtcNow;
var grace = TimeSpan.FromSeconds(60);
Check(meeting.Observe(true, false, now, grace) == MeetingAction.None, "brief microphone activity does not immediately prompt");
Check(meeting.Observe(true, false, now.AddSeconds(3), grace) == MeetingAction.OfferRecording, "stable meeting activity offers recording");
Check(meeting.Observe(true, false, now.AddSeconds(6), grace) == MeetingAction.None, "one meeting receives only one prompt");
meeting.AcceptPrompt();
Check(meeting.Observe(false, true, now.AddSeconds(20), grace) == MeetingAction.None, "short meeting signal gaps do not stop recording");
Check(meeting.Observe(true, true, now.AddSeconds(30), grace) == MeetingAction.None, "rejoining resets meeting-end timer");
Check(meeting.Observe(false, true, now.AddSeconds(89), grace) == MeetingAction.None, "meeting end waits for configured grace");
Check(meeting.Observe(false, true, now.AddSeconds(90), grace) == MeetingAction.StopManagedRecording, "accepted meeting recording stops after grace");
Check(!meeting.HasMeeting, "ended meeting resets prompt state");
meeting.Observe(true, true, now, grace);
Check(meeting.Observe(false, true, now.AddSeconds(70), grace) == MeetingAction.None, "manual recordings are never automatically stopped");
meeting.Reset(); meeting.Observe(true, false, now, grace); meeting.Observe(true, false, now.AddSeconds(3), grace); meeting.AcceptPrompt();
Check(meeting.Observe(null, true, now.AddSeconds(100), grace) == MeetingAction.None, "failed detection is not treated as meeting end");
Check(meeting.Observe(false, true, now.AddSeconds(101), grace) == MeetingAction.None, "unknown detection cannot bypass the end grace");
meeting.ReleaseRecording();
Check(meeting.Observe(false, true, now.AddSeconds(200), grace) == MeetingAction.None, "released recordings lose automatic-stop ownership");
meeting.Reset(); meeting.Observe(true, false, now, TimeSpan.Zero); meeting.AcceptPrompt();
Check(meeting.Observe(false, true, now.AddSeconds(1), TimeSpan.Zero) == MeetingAction.StopManagedRecording, "immediate auto-stop mode works");
Check(new Settings { MeetingEndDelaySeconds = -1 }.Validated().MeetingEndDelaySeconds == 0, "meeting stop delay cannot be negative");
Check(new Settings { MeetingEndDelaySeconds = 9000 }.Validated().MeetingEndDelaySeconds == 600, "meeting stop delay is bounded");
var leaseFolder = Path.Combine(Path.GetTempPath(), "blinky-lease-" + Guid.NewGuid().ToString("N"));
try
{
    using (var firstLease = await ToolInstallLease.AcquireAsync(leaseFolder, "tools", CancellationToken.None))
    using (var cancellation = new CancellationTokenSource(100))
    {
        bool blocked = false;
        try { using var secondLease = await ToolInstallLease.AcquireAsync(leaseFolder, "tools", cancellation.Token); }
        catch (OperationCanceledException) { blocked = true; }
        Check(blocked, "app and MCP tool installations serialize and waiting can be cancelled");
    }
    using var retryLease = await ToolInstallLease.AcquireAsync(leaseFolder, "tools", CancellationToken.None);
    Check(retryLease.CanWrite, "installer lease can be acquired after the prior owner releases it");
}
finally { Directory.Delete(leaseFolder, recursive: true); }
Console.WriteLine($"\n{passed} checks passed.");
