using System.Diagnostics;
using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using Blinky.Core;

var count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    count++;
}

EncoderTools tools;
if (args.Length == 2 && args[0] is "--download-tools" or "--windows-tools")
{
    var downloadProgress = new InlineProgress(p => Console.WriteLine(p.Stage + (p.Percent is double n ? $" {n:0}%" : "")));
    tools = await OptimizerTools.EnsureAsync(args[1], downloadProgress, CancellationToken.None);
    Check(File.Exists(tools.Ffmpeg) && File.Exists(tools.Ffprobe), "pinned Windows tools download and extract successfully");
    var cached = await OptimizerTools.EnsureAsync(args[1], null, CancellationToken.None);
    Check(cached == tools, "verified installation is reused without downloading");
    if (args[0] == "--download-tools") return;
}
else tools = new(Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg", Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "ffprobe");

async Task<string> Exec(string executable, params string[] arguments)
{
    using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
    foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
    process.Start();
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var stdout = await output; var stderr = await error;
    if (process.ExitCode != 0) throw new Exception(stderr);
    return stdout.Trim();
}
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
var folder = Path.Combine(Path.GetTempPath(), "blinky-optimizer-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var source = Path.Combine(folder, "Vídeo space ' & $(literal).mp4");
    // Fragmented MP4 with variable frame timing matches Blinky's recorder,
    // rather than testing only a conventional fixed-rate movie export.
    await Exec(tools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=15", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000", "-filter_complex", "[1:a][2:a]amix=inputs=2:normalize=1[a]", "-map", "0:v", "-map", "[a]", "-vf", "select=not(mod(n\\,3))+between(n\\,30\\,45)", "-fps_mode", "vfr", "-t", "6", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "0", "-threads", "2", "-c:a", "aac", "-b:a", "128k", "-movflags", "+frag_keyframe+empty_moov+default_base_moof", source);
    var originalHash = Hash(source);
    var existing = Path.Combine(folder, Path.GetFileNameWithoutExtension(source) + ".smaller.mp4");
    File.WriteAllText(existing, "Existing copy must survive");
    bool progressSeen = false, validationSeen = false;
    var progress = new InlineProgress(p =>
    {
        if (p.Stage.StartsWith("Compressing") && p.Percent > 0) progressSeen = true;
        if (p.Stage.StartsWith("Verifying")) validationSeen = true;
    });
    var optimizer = new MediaOptimizer(tools);
    var result = await optimizer.OptimizeAsync(source, progress, CancellationToken.None);
    Check(result.IsSmaller && result.OutputBytes < result.OriginalBytes, "real HEVC encoding produces a smaller second file");
    Check(result.OutputPath!.EndsWith(".smaller-2.mp4"), "existing output files are never overwritten");
    Check(File.ReadAllText(existing) == "Existing copy must survive", "existing output content remains unchanged");
    Check(Hash(source) == originalHash, "original video remains byte-for-byte unchanged");
    Check(progressSeen && validationSeen, "real encoder reports progress and runs playback validation");
    var audioBefore = await Exec(tools.Ffmpeg, "-v", "error", "-i", source, "-map", "0:a", "-c:a", "copy", "-f", "hash", "-hash", "sha256", "-");
    var audioAfter = await Exec(tools.Ffmpeg, "-v", "error", "-i", result.OutputPath!, "-map", "0:a", "-c:a", "copy", "-f", "hash", "-hash", "sha256", "-");
    Check(audioBefore == audioAfter, "mixed audio packets are preserved exactly without re-encoding");
    var framesBefore = await Exec(tools.Ffprobe, "-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", source);
    var framesAfter = await Exec(tools.Ffprobe, "-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", result.OutputPath!);
    Check(framesBefore == framesAfter, "all recorded video frames survive optimization");
    async Task<double[]> FrameTimes(string path)
    {
        var data = await Exec(tools.Ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries", "frame=best_effort_timestamp_time", "-of", "json", path);
        using var doc = JsonDocument.Parse(data);
        return doc.RootElement.GetProperty("frames").EnumerateArray().Select(f => double.Parse(f.GetProperty("best_effort_timestamp_time").GetString()!, CultureInfo.InvariantCulture)).ToArray();
    }
    var timesBefore = await FrameTimes(source);
    var timesAfter = await FrameTimes(result.OutputPath!);
    Console.WriteLine($"Timing error: {timesBefore.Select((time, i) => Math.Abs((time - timesBefore[0]) - (timesAfter[i] - timesAfter[0]))).Max():0.000000} s");
    Check(timesBefore.Length == timesAfter.Length && timesBefore.Select((time, i) => Math.Abs((time - timesBefore[0]) - (timesAfter[i] - timesAfter[0]))).All(delta => delta < 0.001), "variable video frame timing is preserved within one millisecond");
    async Task<double> AudioStart(string path)
    {
        var value = await Exec(tools.Ffprobe, "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=start_time", "-of", "csv=p=0", path);
        return double.Parse(value, CultureInfo.InvariantCulture);
    }
    Check(Math.Abs((timesBefore[0] - await AudioStart(source)) - (timesAfter[0] - await AudioStart(result.OutputPath!))) < 0.001, "audio/video start alignment is preserved within one millisecond");
    Check(!Directory.EnumerateFiles(folder, ".blinky.optimizing-*").Any(), "successful optimization leaves no partial file");

    using (var cancellation = new CancellationTokenSource())
    {
        var cancelProgress = new InlineProgress(p => { if (p.Stage.StartsWith("Compressing") && p.Percent == 0) cancellation.CancelAfter(100); });
        bool cancelled = false;
        try { await optimizer.OptimizeAsync(source, cancelProgress, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "cancellation terminates an active encoder job");
        Check(Hash(source) == originalHash && !Directory.EnumerateFiles(folder, ".blinky.optimizing-*").Any(), "cancellation preserves source and removes its partial output");
    }

    bool lowSpace = false;
    try { await new MediaOptimizer(tools, _ => MediaOptimizer.DiskReserve + 1).OptimizeAsync(source, null, CancellationToken.None); }
    catch (IOException) { lowSpace = true; }
    Check(lowSpace, "insufficient temporary disk space rejects optimization before encoding");

    bool diskLow = false, interrupted = false;
    var diskProgress = new InlineProgress(p => { if (p.Stage.StartsWith("Compressing")) diskLow = true; });
    try { await new MediaOptimizer(tools, _ => diskLow ? 0 : long.MaxValue).OptimizeAsync(source, diskProgress, CancellationToken.None); }
    catch (IOException) { interrupted = true; }
    Check(interrupted && !Directory.EnumerateFiles(folder, ".blinky.optimizing-*").Any(), "disk pressure during encoding stops work and cleans partial output");

    var noAudio = Path.Combine(folder, "no-audio.mp4");
    await Exec(tools.Ffmpeg, "-v", "error", "-i", source, "-map", "0:v", "-c:v", "copy", noAudio);
    bool missingAudio = false;
    try { await optimizer.OptimizeAsync(noAudio, null, CancellationToken.None); }
    catch (InvalidOperationException) { missingAudio = true; }
    Check(missingAudio, "recordings without audio are rejected");

    var corruptProgress = new InlineProgress(p =>
    {
        if (p.Stage.StartsWith("Checking duration"))
            File.WriteAllText(Directory.EnumerateFiles(folder, ".blinky.optimizing-*").Single(), "corrupt candidate");
    });
    bool corruptRejected = false;
    try { await optimizer.OptimizeAsync(source, corruptProgress, CancellationToken.None); }
    catch (InvalidOperationException) { corruptRejected = true; }
    Check(corruptRejected && !Directory.EnumerateFiles(folder, ".blinky.optimizing-*").Any(), "invalid output is never published and is cleaned up");

    var corruptPlayback = new InlineProgress(p =>
    {
        if (p.Stage.StartsWith("Verifying") && p.Percent == 0)
            File.WriteAllText(Directory.EnumerateFiles(folder, ".blinky.optimizing-*").Single(), "damaged after metadata check");
    });
    bool playbackRejected = false;
    try { await optimizer.OptimizeAsync(source, corruptPlayback, CancellationToken.None); }
    catch (InvalidOperationException) { playbackRejected = true; }
    Check(playbackRejected && !Directory.EnumerateFiles(folder, ".blinky.optimizing-*").Any(), "playback decode failure prevents publication after metadata passes");

    var missingAudioProgress = new InlineProgress(p =>
    {
        if (p.Stage.StartsWith("Checking duration"))
            File.Copy(noAudio, Directory.EnumerateFiles(folder, ".blinky.optimizing-*").Single(), overwrite: true);
    });
    bool lostAudioRejected = false;
    try { await optimizer.OptimizeAsync(source, missingAudioProgress, CancellationToken.None); }
    catch (InvalidOperationException) { lostAudioRejected = true; }
    Check(lostAudioRejected, "a candidate that lost audio cannot become a finished copy");

    var tiny = Path.Combine(folder, "tiny.mp4");
    await Exec(tools.Ffmpeg, "-v", "error", "-f", "lavfi", "-i", "color=black:size=16x16:rate=10", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "0.1", "-c:v", "libx264", "-crf", "35", "-threads", "2", "-c:a", "aac", tiny);
    var tinyHash = Hash(tiny);
    var tinyResult = await optimizer.OptimizeAsync(tiny, null, CancellationToken.None);
    Check(!tinyResult.IsSmaller && !File.Exists(Path.Combine(folder, "tiny.smaller.mp4")), "larger encodes are discarded instead of wasting space");
    Check(Hash(tiny) == tinyHash, "no-savings result leaves the original intact");

    using (var tokenSource = new CancellationTokenSource())
    {
        var first = optimizer.OptimizeAsync(source, null, tokenSource.Token);
        bool busy = false;
        try { await optimizer.OptimizeAsync(source, null, tokenSource.Token); }
        catch (InvalidOperationException) { busy = true; }
        tokenSource.Cancel();
        try { await first; } catch (OperationCanceledException) { }
        Check(busy, "overlapping jobs cannot run in the same optimizer");
    }
    bool unfinishedRejected = false;
    try { await optimizer.OptimizeAsync(Path.Combine(folder, "active.recording.mp4"), null, CancellationToken.None); }
    catch (InvalidOperationException) { unfinishedRejected = true; }
    Check(unfinishedRejected, "unfinished recording paths cannot be optimized");
    Check(Hash(source) == originalHash, "all failure and cancellation paths preserve original bytes");
    Console.WriteLine($"\n{count} optimizer integration checks passed.");
}
finally { Directory.Delete(folder, recursive: true); }

sealed class InlineProgress(Action<OptimizationProgress> report) : IProgress<OptimizationProgress>
{
    public void Report(OptimizationProgress value) => report(value);
}
