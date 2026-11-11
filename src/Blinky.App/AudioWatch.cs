using System;
using System.Collections.Generic;
using System.Linq;
using Blinky.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Blinky.App;

public sealed record AudioDevice(string Id, string Name);
public sealed record AudioFailure(long Session, string Message);

// Independent WASAPI streams verify that both endpoints can actually be opened.
// They also keep microphone metering available before the video starts.
public sealed class AudioWatch : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _micDevice, _systemDevice;
    private WasapiCapture? _mic, _system;
    private readonly CaptureSessionGuard _sessions = new();
    public string? Failure => _sessions.Failure;
    public event Action<AudioFailure>? Failed;
    public bool IsCurrent(AudioFailure failure) => _sessions.IsCurrent(failure.Session);
    public float MicPeak => Peak(_micDevice);
    public float SystemPeak => Peak(_systemDevice);

    public static List<AudioDevice> Devices(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        if (enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
        {
            using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            defaultId = device.ID;
        }
        var result = new List<AudioDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device) result.Add(new(device.ID, device.FriendlyName + (device.ID == defaultId ? " (default)" : "")));
        }
        return result.OrderByDescending(d => d.Id == defaultId).ToList();
    }

    public void Open(string micId, string systemId)
    {
        CloseStreams();
        var session = _sessions.Begin();
        try
        {
            _micDevice = _enumerator.GetDevice(micId);
            _systemDevice = _enumerator.GetDevice(systemId);
            _mic = new WasapiCapture(_micDevice);
            _system = new WasapiLoopbackCapture(_systemDevice);
            _mic.RecordingStopped += (_, e) => UnexpectedStop(session, "Microphone", e.Exception);
            _system.RecordingStopped += (_, e) => UnexpectedStop(session, "System audio", e.Exception);
            _mic.StartRecording();
            _system.StartRecording();
        }
        catch
        {
            CloseStreams();
            throw;
        }
    }

    public void Verify()
    {
        if (Failure != null) throw new InvalidOperationException(Failure);
        if (_micDevice == null || _systemDevice == null) throw new InvalidOperationException("Select a working microphone and system audio device.");
        if (_micDevice.State != DeviceState.Active || _systemDevice.State != DeviceState.Active)
            throw new InvalidOperationException("An audio device disconnected. Both microphone and system audio are required.");
    }

    private void UnexpectedStop(long session, string source, Exception? error)
    {
        var message = source + " capture stopped" + (error == null ? "." : $": {error.Message} (0x{error.HResult:X8})");
        if (_sessions.TryFail(session, message)) Failed?.Invoke(new AudioFailure(session, message));
    }

    private static float Peak(MMDevice? device)
    {
        try { return device?.AudioMeterInformation.MasterPeakValue ?? 0; }
        catch { return 0; }
    }

    private void CloseStreams()
    {
        // Invalidate before stopping: NAudio can queue callbacks on the UI
        // synchronization context, delivering them after the next Open call.
        _sessions.Invalidate();
        _mic?.StopRecording(); _system?.StopRecording();
        _mic?.Dispose(); _system?.Dispose();
        _micDevice?.Dispose(); _systemDevice?.Dispose();
        _mic = null; _system = null; _micDevice = null; _systemDevice = null;
    }

    public void Suspend() { CloseStreams(); }
    public void Dispose() { CloseStreams(); _enumerator.Dispose(); }
}
