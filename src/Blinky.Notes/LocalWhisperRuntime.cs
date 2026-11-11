using System.Runtime.CompilerServices;
using Blinky.Core;
using Whisper.net;

namespace Blinky.Notes;

public sealed class LocalWhisperRuntime(string cacheFolder, string? modelPath = null) : ITranscriptionRuntime
{
    public async Task<ITranscriptionSession> OpenAsync(string language, IProgress<OptimizationProgress>? progress, CancellationToken token)
    {
        var model = modelPath ?? await WhisperModel.EnsureAsync(cacheFolder, progress, token);
        token.ThrowIfCancellationRequested();
        return new Session(model, language);
    }

    private sealed class Session : ITranscriptionSession
    {
        private readonly WhisperFactory _factory;
        private readonly WhisperProcessor _processor;
        public Session(string model, string language)
        {
            _factory = WhisperFactory.FromPath(model);
            try
            {
                _processor = _factory.CreateBuilder().WithLanguage(language)
                    .WithThreads(Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2))).Build();
            }
            catch { _factory.Dispose(); throw; }
        }

        public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(string wavPath, [EnumeratorCancellation] CancellationToken token)
        {
            await using var stream = File.OpenRead(wavPath);
            await foreach (var segment in _processor.ProcessAsync(stream, token))
                yield return new(segment.Start.TotalSeconds, segment.End.TotalSeconds, segment.Text.Trim());
        }

        public async ValueTask DisposeAsync() { await _processor.DisposeAsync(); _factory.Dispose(); }
    }
}
