using static Blinky.Core.MediaProcess;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Blinky.Core;

public sealed record OptimizationResult(string? OutputPath, long OriginalBytes, long OutputBytes)
{
    public bool IsSmaller => OutputPath != null;
    public double SavedPercent => IsSmaller ? 100d * (OriginalBytes - OutputBytes) / OriginalBytes : 0;
}

public sealed class MediaOptimizer(EncoderTools tools, Func<string, long>? freeSpace = null)
{
    public const long DiskReserve = 512L * 1024 * 1024;
    private int _running;

    public async Task<OptimizationResult> OptimizeAsync(string source, IProgress<OptimizationProgress>? progress, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("Another optimization is already running.");
        string? temporary = null;
        try
        {
            source = Path.GetFullPath(source);
            if (source.EndsWith(".recording.mp4", StringComparison.OrdinalIgnoreCase) || source.Contains(".optimizing-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Finish recording before creating a smaller copy.");
            if (!string.Equals(Path.GetExtension(source), ".mp4", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose a finished MP4 recording.");
            // Hold a read lease throughout the job. On Windows this prevents
            // another program from overwriting/deleting the original mid-encode.
            using var original = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            var originalBytes = original.Length;
            if (originalBytes == 0) throw new InvalidOperationException("The original video is empty.");
            long Available() => freeSpace?.Invoke(source) ?? new DriveInfo(Path.GetPathRoot(source)!).AvailableFreeSpace;
            if (Available() - DiskReserve < originalBytes)
                throw new IOException("Not enough free space. Leave room for a second copy of the original plus 512 MiB.");
            void CheckDisk()
            {
                if (Available() <= DiskReserve) throw new IOException("Optimization stopped because disk space is low. The original is unchanged.");
            }

            token.ThrowIfCancellationRequested();
            progress?.Report(new("Reading recording…"));
            var input = await ProbeAsync(source, token);
            RequireMedia(input);
            temporary = Path.Combine(Path.GetDirectoryName(source)!, ".blinky.optimizing-" + Guid.NewGuid().ToString("N") + ".mp4");
            var threads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
            progress?.Report(new("Compressing video · audio is copied unchanged…", 0));
            try
            {
                await RunAsync(tools.Ffmpeg,
                [
                    "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-xerror", "-err_detect", "explode", "-threads", "2", "-i", source,
                    "-map", "0:v:0", "-map", "0:a", "-map_metadata", "0", "-map_chapters", "0",
                    "-c:v", "libx265", "-preset", "slow", "-crf", "26", "-threads", "2",
                    "-x265-params", $"pools={threads}:frame-threads=1:log-level=error",
                    "-pix_fmt", "yuv420p", "-tag:v", "hvc1", "-fps_mode", "passthrough", "-enc_time_base:v", "demux", "-c:a", "copy",
                    "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", temporary
                ], line => ReportTime(line, input.Duration, "Compressing video · audio is copied unchanged…", progress), token, () =>
                {
                    CheckDisk();
                    if (File.Exists(temporary) && new FileInfo(temporary).Length >= originalBytes) throw new NoSavingsException();
                });
            }
            catch (NoSavingsException) { return new(null, originalBytes, originalBytes); }

            var outputBytes = new FileInfo(temporary).Length;
            if (outputBytes >= originalBytes) return new(null, originalBytes, outputBytes);
            progress?.Report(new("Checking duration, picture size and audio…"));
            var output = await ProbeAsync(temporary, token);
            Validate(input, output);
            progress?.Report(new("Verifying video and audio playback…", 0));
            await RunAsync(tools.Ffmpeg,
            [
                "-hide_banner", "-loglevel", "error", "-nostdin", "-xerror", "-err_detect", "explode", "-threads", "2", "-i", temporary,
                "-map", "0:v:0", "-map", "0:a", "-progress", "pipe:1", "-nostats", "-f", "null", "-"
            ], line => ReportTime(line, input.Duration, "Verifying video and audio playback…", progress), token, CheckDisk);
            token.ThrowIfCancellationRequested();
            CheckDisk();
            var destination = Commit(temporary, source);
            temporary = null;
            progress?.Report(new("Smaller copy saved · original kept", 100));
            return new(destination, originalBytes, outputBytes);
        }
        finally
        {
            try { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
            finally { Volatile.Write(ref _running, 0); }
        }
    }

    private static string Commit(string temporary, string source)
    {
        var folder = Path.GetDirectoryName(source)!;
        var name = Path.GetFileNameWithoutExtension(source);
        for (int suffix = 1; suffix <= 1000; suffix++)
        {
            var destination = Path.Combine(folder, name + ".smaller" + (suffix == 1 ? "" : "-" + suffix) + ".mp4");
            try { File.Move(temporary, destination, overwrite: false); return destination; }
            catch (IOException) when (File.Exists(destination)) { }
        }
        throw new IOException("Too many smaller copies already exist. Rename one and retry.");
    }

    private sealed class NoSavingsException : Exception;
    private sealed record StreamInfo(string Type, string Codec, int Width, int Height, int Channels, string SampleRate, double Duration, double? StartTime);
    private sealed record MediaInfo(double Duration, List<StreamInfo> Streams);

    private async Task<MediaInfo> ProbeAsync(string path, CancellationToken token)
    {
        var json = new StringBuilder();
        await RunAsync(tools.Ffprobe,
        ["-v", "error", "-show_entries", "format=duration:stream=codec_type,codec_name,width,height,channels,sample_rate,duration,start_time", "-of", "json", path],
        line =>
        {
            if (json.Length > 1_000_000) throw new InvalidOperationException("Unexpectedly large video metadata.");
            json.AppendLine(line);
        }, token);
        using var doc = JsonDocument.Parse(json.ToString());
        static string Text(JsonElement element, string key) => element.TryGetProperty(key, out var value) ? value.ToString() : "";
        static int Number(JsonElement element, string key) => int.TryParse(Text(element, key), out var value) ? value : 0;
        static double Seconds(JsonElement element) => double.TryParse(Text(element, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;
        static double? Start(JsonElement element) => double.TryParse(Text(element, "start_time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().Select(s => new StreamInfo(Text(s, "codec_type"), Text(s, "codec_name"), Number(s, "width"), Number(s, "height"), Number(s, "channels"), Text(s, "sample_rate"), Seconds(s), Start(s))).ToList();
        var duration = doc.RootElement.TryGetProperty("format", out var format) ? Seconds(format) : 0;
        if (duration <= 0) duration = streams.Select(s => s.Duration).DefaultIfEmpty().Max();
        return new(duration, streams);
    }

    private static void RequireMedia(MediaInfo media)
    {
        if (media.Duration <= 0 || !media.Streams.Any(s => s.Type == "video") || !media.Streams.Any(s => s.Type == "audio"))
            throw new InvalidOperationException("The recording must contain a playable video and audio track.");
    }

    private static void Validate(MediaInfo input, MediaInfo output)
    {
        RequireMedia(output);
        var sourceVideo = input.Streams.First(s => s.Type == "video");
        var video = output.Streams.First(s => s.Type == "video");
        if (video.Codec != "hevc" || video.Width != sourceVideo.Width || video.Height != sourceVideo.Height || Math.Abs(input.Duration - output.Duration) > 0.5)
            throw new InvalidOperationException("The smaller copy failed video/duration validation. The original is unchanged.");
        var inputAudio = input.Streams.Where(s => s.Type == "audio").ToArray();
        var outputAudio = output.Streams.Where(s => s.Type == "audio").ToArray();
        if (inputAudio.Length != outputAudio.Length || inputAudio.Where((s, i) =>
                s.Codec != outputAudio[i].Codec || s.Channels != outputAudio[i].Channels || s.SampleRate != outputAudio[i].SampleRate
                || (s.Duration > 0 && outputAudio[i].Duration > 0 && Math.Abs(s.Duration - outputAudio[i].Duration) > 0.5)).Any())
            throw new InvalidOperationException("The smaller copy failed audio validation. The original is unchanged.");
        for (int i = 0; i < inputAudio.Length; i++)
        {
            if (sourceVideo.StartTime is double inputVideoStart && video.StartTime is double outputVideoStart
                && inputAudio[i].StartTime is double inputAudioStart && outputAudio[i].StartTime is double outputAudioStart
                && Math.Abs((inputVideoStart - inputAudioStart) - (outputVideoStart - outputAudioStart)) > 0.05)
                throw new InvalidOperationException("The smaller copy failed audio/video alignment validation. The original is unchanged.");
        }
    }

    private static void ReportTime(string line, double duration, string stage, IProgress<OptimizationProgress>? progress)
    {
        const string key = "out_time_us=";
        if (line.StartsWith(key, StringComparison.Ordinal) && long.TryParse(line.AsSpan(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
            progress?.Report(new(stage, Math.Clamp(microseconds / 1_000_000d / duration * 100, 0, 100)));
    }

}
