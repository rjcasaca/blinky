using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Blinky.Core;
using Blinky.Notes;

var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
if (args.Length == 2 && args[0] == "--download-model")
{
    var model = await WhisperModel.EnsureAsync(args[1], null, CancellationToken.None);
    Check(new FileInfo(model).Length == WhisperModel.Size, "verified multilingual model download");
    Check(await WhisperModel.EnsureAsync(args[1], null, CancellationToken.None) == model, "verified model cache reuse");
    return;
}
async Task<string> Exec(string executable, params string[] arguments)
{
    using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
    foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
    process.Start(); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync(); var text = await stdout; var error = await stderr;
    if (process.ExitCode != 0) throw new Exception(error);
    return text;
}
var root = Path.Combine(Path.GetTempPath(), "blinky-notes-" + Guid.NewGuid().ToString("N"));
var recordings = Path.Combine(root, "class recordings"); var data = Path.Combine(root, "data");
var tools = new EncoderTools(Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg", Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "ffprobe");
try
{
    var workspace = new NotesWorkspace(recordings, data);
    var audio = Path.Combine(recordings, "Pós graduação ' & class.m4a");
    // Use the exact production PCM → fragmented AAC arguments and a real pipe.
    using (var process = new Process { StartInfo = new ProcessStartInfo(tools.Ffmpeg) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true } })
    {
        foreach (var arg in AudioRecordingFormat.Arguments(audio)) process.StartInfo.ArgumentList.Add(arg);
        process.Start(); var error = process.StandardError.ReadToEndAsync();
        var bytes = new byte[48000 * 5 * 2];
        for (int i = 0; i < bytes.Length / 2; i++)
        {
            short value = (short)(5000 * (Math.Sin(2 * Math.PI * 440 * i / 48000) + Math.Sin(2 * Math.PI * 880 * i / 48000)));
            bytes[2 * i] = (byte)value; bytes[2 * i + 1] = (byte)(value >> 8);
        }
        await process.StandardInput.BaseStream.WriteAsync(bytes); process.StandardInput.Close(); await process.WaitForExitAsync();
        Check(process.ExitCode == 0, "production audio-only PCM pipeline encodes successfully: " + await error);
    }
    using (var metadata = JsonDocument.Parse(await Exec(tools.Ffprobe, "-v", "error", "-show_streams", "-of", "json", audio)))
    {
        var streams = metadata.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        Check(streams.Length == 1 && streams[0].GetProperty("codec_type").GetString() == "audio" && streams[0].GetProperty("codec_name").GetString() == "aac", "audio-only M4A contains AAC and no video stream");
    }
    File.WriteAllText(Path.Combine(recordings, "active.recording.mp4"), "unfinished");
    File.WriteAllText(Path.Combine(recordings, "active.recording.m4a"), "unfinished");
    Check(workspace.ListRecordings().Length == 1, "class library lists finished audio and excludes active recordings");
    Check(workspace.ListTemplates().Contains("class-notes") && workspace.ListTemplates().Contains("class-summary"), "postgraduate class notes and summary templates are available");
    workspace.SaveTemplate("custom-class", "# {{title}}\n\n## Concepts\n\nExplain in Portuguese.");
    Check(workspace.GetTemplate("custom-class").Contains("Portuguese"), "custom Markdown templates persist");
    bool protectedTemplate = false;
    try { workspace.SaveTemplate("custom-class", "replacement"); } catch (IOException) { protectedTemplate = true; }
    Check(protectedTemplate, "templates are not overwritten by default");
    bool pathRejected = false;
    try { workspace.RecordingPath("../outside.mp4"); } catch (ArgumentException) { pathRejected = true; }
    Check(pathRejected, "recording path traversal is rejected");
    bool templateTraversal = false;
    try { workspace.SaveTemplate("../escape", "bad"); } catch (ArgumentException) { templateTraversal = true; }
    Check(templateTraversal, "template path traversal is rejected");
    var pages = NotesWorkspace.Page("a😀bc", 0, 2);
    Check(pages.Text == "a" && pages.NextOffset == 1 && NotesWorkspace.Page("a😀bc", 1, 2).Text == "😀", "transcript paging does not split Unicode surrogate pairs");
    McpClientConfiguration.Write(Path.Combine(data, "config"), @"C:\My apps\Blinky\mcp\Blinky.Mcp.exe", recordings, data);
    using (var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "config", "claude-desktop.json"))))
        Check(config.RootElement.GetProperty("mcpServers").GetProperty("blinky").GetProperty("command").GetString()!.Contains("My apps"), "Claude setup correctly quotes executable paths containing spaces");

    var runtime = new FakeRuntime();
    await using var jobs = new TranscriptionJobs(workspace, runtime, _ => Task.FromResult(tools), chunkSeconds: 2);
    var job = jobs.Start(Path.GetFileName(audio), "pt");
    for (int attempt = 0; attempt < 200 && !jobs.Get(job.Id).Finished; attempt++) await Task.Delay(100);
    var completed = jobs.Get(job.Id);
    Check(completed.State == "completed", "long-class job completes bounded chunk extraction: " + completed.Error);
    Check(runtime.Chunks >= 3 && runtime.LargestWav < 70000, "audio is processed in bounded chunks rather than one full-class WAV");
    var transcript = jobs.ReadTranscript(job.Id, 0, 32000).Text;
    Check(transcript.Contains("Full class transcript") && transcript.Contains("[00:00:04"), "transcript timestamps include absolute offsets across chunks");
    Check(File.Exists(completed.TranscriptPath), "full transcript is exported as Markdown next to the recording");
    Check(jobs.NotesContext(job.Id).Contains("Pós graduação") && jobs.NotesContext(job.Id, "class-summary").Contains("Summarize the entire class"), "class notes and full-class summary contexts use the requested templates");
    var notes = jobs.SaveNotes(job.Id, "class-notes", "# Detailed class notes\n\n## Concepts\nExample [00:00:01].");
    var summary = jobs.SaveNotes(job.Id, "class-summary", "# Class summary\n\nKey ideas.");
    var secondNotes = jobs.SaveNotes(job.Id, "class-notes", "# Another version");
    Check(File.Exists(notes) && File.Exists(summary) && notes != secondNotes && File.ReadAllText(notes).Contains("Detailed"), "detailed notes and summaries are separate Markdown exports and prior notes survive");
    Check(!Directory.EnumerateFiles(workspace.JobFolder(job.Id), "*.wav").Any(), "completed jobs remove temporary extracted audio");

    var slowRuntime = new FakeRuntime { WaitForCancellation = true };
    await using var slow = new TranscriptionJobs(workspace, slowRuntime, _ => Task.FromResult(tools), 2);
    var slowJob = slow.Start(Path.GetFileName(audio));
    bool overlapBlocked = false;
    try { jobs.Start(Path.GetFileName(audio)); } catch (InvalidOperationException) { overlapBlocked = true; }
    Check(overlapBlocked, "separate clients cannot transcribe simultaneously in the same workspace");
    bool partialNotesBlocked = false;
    try { slow.SaveNotes(slowJob.Id, "class-summary", "# Partial"); } catch (InvalidOperationException) { partialNotesBlocked = true; }
    Check(partialNotesBlocked, "incomplete transcripts cannot be exported as final class summaries");
    slow.Cancel(slowJob.Id);
    for (int attempt = 0; attempt < 100 && !slow.Get(slowJob.Id).Finished; attempt++) await Task.Delay(50);
    Check(slow.Get(slowJob.Id).State == "cancelled" && !File.Exists(Path.Combine(workspace.JobFolder(slowJob.Id), "chunk.wav")), "cancellation stops work and removes temporary audio");
    Check(File.Exists(audio), "all class operations preserve the original audio recording");

    if (args.Length == 2 && args[0] == "--real")
    {
        // Caller provides the downloaded JFK speech WAV; run actual local inference.
        var realRecording = Path.Combine(recordings, "speech.m4a");
        await Exec(tools.Ffmpeg, "-v", "error", "-i", args[1], "-c:a", "aac", realRecording);
        await using var real = new TranscriptionJobs(workspace, new LocalWhisperRuntime(Path.Combine(Path.GetTempPath(), "blinky-models")), _ => Task.FromResult(tools), 600);
        var actual = real.Start("speech.m4a", "en");
        for (int attempt = 0; attempt < 1800 && !real.Get(actual.Id).Finished; attempt++) await Task.Delay(100);
        Check(real.Get(actual.Id).State == "completed", "real local Whisper inference completes: " + real.Get(actual.Id).Error);
        var speech = real.ReadTranscript(actual.Id, 0, 32000).Text;
        Check(speech.Contains("country", StringComparison.OrdinalIgnoreCase), "real speech is recognized in the timestamped transcript");
    }
    Console.WriteLine($"\n{passed} class-notes checks passed.");
}
finally { Directory.Delete(root, recursive: true); }

sealed class FakeRuntime : ITranscriptionRuntime
{
    public int Chunks; public long LargestWav; public bool WaitForCancellation;
    public Task<ITranscriptionSession> OpenAsync(string language, IProgress<OptimizationProgress>? progress, CancellationToken token) => Task.FromResult<ITranscriptionSession>(new Session(this));
    private sealed class Session(FakeRuntime owner) : ITranscriptionSession
    {
        public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(string wavPath, [EnumeratorCancellation] CancellationToken token)
        {
            owner.Chunks++; owner.LargestWav = Math.Max(owner.LargestWav, new FileInfo(wavPath).Length);
            if (owner.WaitForCancellation) await Task.Delay(Timeout.Infinite, token);
            await Task.Yield(); token.ThrowIfCancellationRequested();
            yield return new(0, 0.5, "Postgraduate class concept and example.");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
