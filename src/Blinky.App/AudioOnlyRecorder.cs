using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Blinky.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Blinky.App;

// Encode a real audio-only, fragmented M4A. No screen frames or video encoder
// are created. The sample clock excludes pauses and bounds capture buffers.
public sealed class AudioOnlyRecorder : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _micDevice, _systemDevice;
    private WasapiCapture? _mic, _system;
    private BufferedWaveProvider? _micBuffer, _systemBuffer;
    private Process? _encoder;
    private readonly Stopwatch _clock = new();
    private volatile bool _stop, _paused;
    private string? _failure;
    public event Action<string?>? Completed;

    public void Start(Settings settings, string path, EncoderTools tools)
    {
        _micDevice = _enumerator.GetDevice(settings.MicrophoneId!);
        _systemDevice = _enumerator.GetDevice(settings.SystemAudioId!);
        _mic = new WasapiCapture(_micDevice);
        _system = new WasapiLoopbackCapture(_systemDevice);
        _micBuffer = Buffer(_mic); _systemBuffer = Buffer(_system);
        var mic = new VolumeSampleProvider(Normalize(_micBuffer)) { Volume = (float)settings.MicrophoneVolume };
        var system = new VolumeSampleProvider(Normalize(_systemBuffer)) { Volume = (float)settings.SystemVolume };
        var mix = new MixingSampleProvider(new[] { mic, system }) { ReadFully = true };
        var pcm = new SampleToWaveProvider16(mix);
        _encoder = new Process { StartInfo = new ProcessStartInfo(tools.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true } };
        foreach (var arg in AudioRecordingFormat.Arguments(path)) _encoder.StartInfo.ArgumentList.Add(arg);
        _encoder.Start();
        _mic.StartRecording(); _system.StartRecording();
        _clock.Start();
        _ = Task.Run(() => WriteAsync(pcm));
    }

    private BufferedWaveProvider Buffer(WasapiCapture capture)
    {
        var buffer = new BufferedWaveProvider(capture.WaveFormat) { BufferDuration = TimeSpan.FromSeconds(2), DiscardOnBufferOverflow = true, ReadFully = true };
        capture.DataAvailable += (_, e) => { if (!_stop && !_paused) buffer.AddSamples(e.Buffer, 0, e.BytesRecorded); };
        capture.RecordingStopped += (_, e) => { if (!_stop) { _failure = "Audio capture stopped" + (e.Exception == null ? "." : ": " + e.Exception.Message); Stop(); } };
        return buffer;
    }

    private static ISampleProvider Normalize(BufferedWaveProvider buffer)
    {
        ISampleProvider source = buffer.ToSampleProvider();
        if (source.WaveFormat.Channels > 1) source = new MonoDownmix(source);
        if (source.WaveFormat.SampleRate != 48000) source = new WdlResamplingSampleProvider(source, 48000);
        return source;
    }

    public void Pause(bool pause)
    {
        _paused = pause;
        if (pause) _clock.Stop();
        else { _micBuffer?.ClearBuffer(); _systemBuffer?.ClearBuffer(); _clock.Start(); }
    }
    public void Stop() { _clock.Stop(); _stop = true; }

    private async Task WriteAsync(IWaveProvider pcm)
    {
        string? error = null;
        var encoder = _encoder!;
        // Drain stderr immediately so a failed encoder cannot deadlock a full pipe.
        var errors = encoder.StandardError.ReadToEndAsync();
        try
        {
            long writtenFrames = 0;
            var bytes = new byte[1920];
            while (!_stop)
            {
                if (encoder.HasExited) throw new IOException("The audio encoder stopped: " + await errors);
                if (_paused) { await Task.Delay(10); continue; }
                long due = (long)(_clock.Elapsed.TotalSeconds * 48000);
                while (writtenFrames < due && !_stop && !_paused)
                {
                    var frames = (int)Math.Min(960, due - writtenFrames);
                    var read = pcm.Read(bytes, 0, frames * 2);
                    await encoder.StandardInput.BaseStream.WriteAsync(bytes.AsMemory(0, read));
                    writtenFrames += read / 2;
                }
                await Task.Delay(10);
            }
            encoder.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await encoder.WaitForExitAsync(timeout.Token);
            var details = await errors;
            if (encoder.ExitCode != 0) error = "Audio encoder failed: " + details;
            error ??= _failure;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            try { if (!encoder.HasExited) encoder.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await encoder.WaitForExitAsync();
            await errors;
        }
        finally { _stop = true; Completed?.Invoke(error); }
    }

    public void Dispose()
    {
        Stop();
        _mic?.StopRecording(); _system?.StopRecording();
        _mic?.Dispose(); _system?.Dispose();
        _micDevice?.Dispose(); _systemDevice?.Dispose(); _enumerator.Dispose();
        if (_encoder != null)
        {
            try { if (!_encoder.HasExited) _encoder.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _encoder.Dispose();
        }
    }

    private sealed class MonoDownmix(ISampleProvider input) : ISampleProvider
    {
        private float[] _buffer = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(input.WaveFormat.SampleRate, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            int channels = input.WaveFormat.Channels;
            if (_buffer.Length < count * channels) _buffer = new float[count * channels];
            int read = input.Read(_buffer, 0, count * channels) / channels;
            for (int frame = 0; frame < read; frame++)
            {
                float sum = 0;
                for (int channel = 0; channel < channels; channel++) sum += _buffer[frame * channels + channel];
                buffer[offset + frame] = sum / channels;
            }
            return read;
        }
    }
}
