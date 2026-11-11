namespace Blinky.Notes;

public sealed record TranscriptionJob(string Id, string RecordingId, string Language, DateTimeOffset StartedUtc,
    string State = "queued", string Stage = "Queued", double CompletedSeconds = 0, double DurationSeconds = 0,
    string? Error = null, string? TranscriptPath = null)
{
    public bool Finished => State is "completed" or "cancelled" or "failed" or "interrupted";
    public double? Percent => DurationSeconds > 0 ? Math.Clamp(CompletedSeconds / DurationSeconds * 100, 0, 100) : null;
    public int OwnerProcessId { get; init; } = Environment.ProcessId;
}

public sealed record TranscriptSegment(double StartSeconds, double EndSeconds, string Text);

public interface ITranscriptionSession : IAsyncDisposable
{
    IAsyncEnumerable<TranscriptSegment> TranscribeAsync(string wavPath, CancellationToken token);
}

public interface ITranscriptionRuntime
{
    Task<ITranscriptionSession> OpenAsync(string language, IProgress<Blinky.Core.OptimizationProgress>? progress, CancellationToken token);
}
