using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Blinky.Core;
using ScreenRecorderLib;

namespace Blinky.App;

public enum CaptureState { Ready, Starting, Recording, Paused, Stopping }

public sealed class RecordingService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly string _journalPath;
    private Recorder? _recorder;
    private TaskCompletionSource<bool>? _completion;
    private readonly Stopwatch _elapsed = new();
    private SessionJournal? _journal;
    private string? _stopReason;
    private bool _stopIsFailure = true;
    private RecordingProfile? _profile;
    private bool _disposed;
    private bool _audioOnly, _cancelledBeforeAudio;
    private AudioOnlyRecorder? _audioRecorder;
    private CancellationTokenSource? _audioStartCancellation;
    public CaptureState State { get; private set; }
    public bool IsBusy => State != CaptureState.Ready;
    public string? FilePath { get; private set; }
    public TimeSpan Elapsed => _elapsed.Elapsed;
    public event Action<string>? Changed;
    public event Action<string>? Failed;

    public RecordingService(Dispatcher dispatcher, string journalPath)
    {
        _dispatcher = dispatcher;
        _journalPath = journalPath;
    }

    public void Start(Settings settings, RecordingSourceBase source, int width, int height)
    {
        if (IsBusy) throw new InvalidOperationException("A recording is already active.");
        if (string.IsNullOrWhiteSpace(settings.MicrophoneId) || string.IsNullOrWhiteSpace(settings.SystemAudioId))
            throw new InvalidOperationException("Both microphone and system audio are required.");
        Directory.CreateDirectory(settings.OutputFolder);
        _profile = RecordingProfile.All.First(p => p.Name == settings.Profile);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(settings.OutputFolder))!);
        if (DiskPolicy.MustStop(drive.AvailableFreeSpace, _profile.VideoBitrate))
            throw new IOException("There is too little free disk space to start safely. Free at least 512 MB.");

        FilePath = Path.Combine(settings.OutputFolder, $"Blinky-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.recording.mp4");
        var size = RecordingProfile.Fit(width, height);
        var options = RecorderOptions.Default;
        options.SourceOptions = new SourceOptions { RecordingSources = { source } };
        options.OutputOptions = new OutputOptions
        {
            RecorderMode = RecorderMode.Video,
            OutputFrameSize = new ScreenSize(size.Width, size.Height),
            Stretch = StretchMode.Uniform
        };
        options.AudioOptions = new AudioOptions
        {
            IsAudioEnabled = true,
            Bitrate = AudioBitrate.bitrate_128kbps,
            Channels = AudioChannels.Stereo,
            AudioSources =
            {
                new CaptureAudioSource(settings.MicrophoneId) { Volume = (float)settings.MicrophoneVolume },
                new LoopbackAudioSource(settings.SystemAudioId) { Volume = (float)settings.SystemVolume }
            }
        };
        options.VideoEncoderOptions = new VideoEncoderOptions
        {
            Framerate = _profile.FramesPerSecond,
            Quality = _profile.Quality,
            Bitrate = _profile.VideoBitrate,
            Encoder = settings.UseHevc
                ? new H265VideoEncoder { BitrateMode = H265BitrateControlMode.Quality }
                : new H264VideoEncoder { BitrateMode = H264BitrateControlMode.Quality },
            IsHardwareEncodingEnabled = settings.HardwareEncoding,
            IsThrottlingDisabled = false,
            IsFixedFramerate = false,
            IsFragmentedMp4Enabled = true,
            IsMp4FastStartEnabled = false,
            IsLowLatencyEnabled = false
        };
        options.MouseOptions = new MouseOptions { IsMousePointerEnabled = true };
        options.LogOptions = new LogOptions { IsLogEnabled = false };
        _stopReason = null;
        _audioOnly = false;
        _cancelledBeforeAudio = false;
        _elapsed.Reset();
        _journal = new SessionJournal(FilePath, DateTimeOffset.UtcNow, "recording");
        JsonStore.Save(_journalPath, _journal);
        _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SetState(CaptureState.Starting, "Starting both audio sources and video…");
        try
        {
            _recorder = Recorder.CreateRecorder(options);
            var current = _recorder;
            _recorder.OnStatusChanged += (_, e) => _dispatcher.BeginInvoke(() =>
            {
                if (_recorder != current) return;
                // Stop requests take precedence over late startup/recording callbacks.
                if (State == CaptureState.Stopping) return;
                if (e.Status == RecorderStatus.Recording)
                {
                    _elapsed.Start(); SetState(CaptureState.Recording, "Recording · microphone + system audio");
                }
                else if (e.Status == RecorderStatus.Paused)
                {
                    _elapsed.Stop(); SetState(CaptureState.Paused, "Paused · recording stays open");
                }
            });
            _recorder.OnRecordingComplete += (_, _) => _dispatcher.BeginInvoke(() => Finish(current, null));
            _recorder.OnRecordingFailed += (_, e) => _dispatcher.BeginInvoke(() => Finish(current, e.Error));
            _recorder.Record(FilePath);
        }
        catch (Exception e) { Finish(_recorder, e.Message); }
    }

    public void StartAudio(Settings settings)
    {
        if (IsBusy) throw new InvalidOperationException("A recording is already active.");
        if (string.IsNullOrWhiteSpace(settings.MicrophoneId) || string.IsNullOrWhiteSpace(settings.SystemAudioId))
            throw new InvalidOperationException("Both microphone and system audio are required.");
        Directory.CreateDirectory(settings.OutputFolder);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(settings.OutputFolder))!);
        if (DiskPolicy.MustStop(drive.AvailableFreeSpace, 96_000)) throw new IOException("Free at least 512 MiB before recording.");
        FilePath = Path.Combine(settings.OutputFolder, $"Blinky-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.recording.m4a");
        _journal = new SessionJournal(FilePath, DateTimeOffset.UtcNow, "recording");
        JsonStore.Save(_journalPath, _journal);
        _audioOnly = true; _cancelledBeforeAudio = false; _stopReason = null;
        _elapsed.Reset();
        _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _audioStartCancellation = new CancellationTokenSource();
        SetState(CaptureState.Starting, "Preparing audio-only recorder…");
        _ = StartAudioWorkerAsync(settings, _audioStartCancellation.Token);
    }

    private async Task StartAudioWorkerAsync(Settings settings, CancellationToken token)
    {
        try
        {
            var progress = new Progress<OptimizationProgress>(p => { if (State == CaptureState.Starting) Changed?.Invoke(p.Stage + (p.Percent is double n ? $" {n:0}%" : "")); });
            var tools = await OptimizerTools.EnsureAsync(Path.Combine(Path.GetDirectoryName(_journalPath)!, "tools"), progress, token);
            token.ThrowIfCancellationRequested();
            _audioRecorder = new AudioOnlyRecorder();
            _audioRecorder.Completed += error => _dispatcher.BeginInvoke(() => Finish(null, error));
            _audioRecorder.Start(settings, FilePath!, tools);
            _elapsed.Start(); SetState(CaptureState.Recording, "Recording audio only · microphone + system audio");
        }
        catch (OperationCanceledException) { _cancelledBeforeAudio = true; Finish(null, null); }
        catch (Exception error) { Finish(null, error.Message); }
    }

    public void TogglePause()
    {
        if (_audioOnly)
        {
            if (State == CaptureState.Recording) { _audioRecorder?.Pause(true); _elapsed.Stop(); SetState(CaptureState.Paused, "Audio recording paused"); }
            else if (State == CaptureState.Paused) { _audioRecorder?.Pause(false); _elapsed.Start(); SetState(CaptureState.Recording, "Recording audio only · microphone + system audio"); }
            return;
        }
        if (State == CaptureState.Recording) _recorder?.Pause();
        else if (State == CaptureState.Paused) _recorder?.Resume();
    }

    public Task StopAsync(string? reason = null, bool notifyAsFailure = true)
    {
        if (!IsBusy) return Task.CompletedTask;
        if (State != CaptureState.Stopping)
        {
            _stopReason = reason;
            _stopIsFailure = notifyAsFailure;
            _elapsed.Stop();
            SetState(CaptureState.Stopping, reason == null ? "Saving your recording…" : reason + " Saving…");
            try
            {
                if (_audioOnly) { _audioStartCancellation?.Cancel(); _audioRecorder?.Stop(); }
                else _recorder?.Stop();
            }
            catch (Exception e) { Finish(_recorder, e.Message); }
        }
        return _completion?.Task ?? Task.CompletedTask;
    }

    public long FileBytes
    {
        get { try { return FilePath == null ? 0 : new FileInfo(FilePath).Length; } catch (IOException) { return 0; } }
    }

    public void CheckDisk()
    {
        if (!IsBusy || State == CaptureState.Stopping || FilePath == null) return;
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(FilePath))!);
        if (DiskPolicy.MustStop(drive.AvailableFreeSpace, _audioOnly ? 96_000 : (_profile?.VideoBitrate ?? 4_000_000)))
            _ = StopAsync("Disk space is low. Recording stopped before the disk filled.");
    }

    private void Finish(Recorder? sender, string? error)
    {
        if (sender != _recorder || !IsBusy) return;
        _elapsed.Stop();
        var finishedRecorder = _recorder;
        var finishedAudio = _audioRecorder;
        _recorder = null;
        _audioRecorder = null;
        try
        {
            if (_cancelledBeforeAudio) FilePath = null;
            if (error == null && FilePath != null)
            {
                var completedPath = FilePath.Replace(".recording.mp4", ".mp4", StringComparison.Ordinal).Replace(".recording.m4a", ".m4a", StringComparison.Ordinal);
                File.Move(FilePath, completedPath);
                FilePath = completedPath;
            }
            if (_journal != null)
                JsonStore.Save(_journalPath, _journal with { FilePath = FilePath ?? _journal.FilePath, State = _cancelledBeforeAudio ? "cancelled" : error == null ? "complete" : "interrupted", Message = error ?? _stopReason });
        }
        catch (Exception e) { error = (error == null ? "" : error + " ") + "Could not finalize recording: " + e.Message; }
        finally
        {
            // Execute disposal after native callbacks have unwound.
            _dispatcher.BeginInvoke(() => { finishedRecorder?.Dispose(); finishedAudio?.Dispose(); });
            _audioStartCancellation?.Dispose(); _audioStartCancellation = null;
            SetState(CaptureState.Ready, error == null
                ? (_cancelledBeforeAudio ? "Audio recording cancelled." : _stopReason == null ? "Saved · " + Path.GetFileName(FilePath) : _stopReason)
                : "Recording interrupted · partial file kept");
            _completion?.TrySetResult(error == null);
        }
        if (error != null) Failed?.Invoke(error);
        else if (_stopReason != null && _stopIsFailure) Failed?.Invoke(_stopReason);
    }

    private void SetState(CaptureState state, string message) { State = state; Changed?.Invoke(message); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recorder?.Dispose();
    }
}
