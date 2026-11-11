using System.Security.Cryptography;
using Blinky.Core;

namespace Blinky.Notes;

public static class WhisperModel
{
    public const string Sha256 = "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe";
    public const long Size = 147951465;
    public const string Url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";

    public static async Task<string> EnsureAsync(string cacheFolder, IProgress<OptimizationProgress>? progress, CancellationToken token)
    {
        var directory = NotesWorkspace.SafeDirectory(Path.Combine(cacheFolder, "whisper"));
        var destination = Path.Combine(directory, "ggml-base.bin");
        NotesWorkspace.RejectLinks(destination);
        if (File.Exists(destination) && new FileInfo(destination).Length == Size && await Hash(destination, token) == Sha256) return destination;
        var temporary = Path.Combine(directory, "model-" + Guid.NewGuid().ToString("N") + ".download");
        try
        {
            progress?.Report(new("Downloading multilingual transcription model…", 0));
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, useAsync: true))
            {
                var buffer = new byte[131072];
                long received = 0;
                var last = DateTime.UtcNow;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    received += read;
                    if (received > Size) throw new IOException("The transcription model exceeds its expected size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    if (DateTime.UtcNow - last > TimeSpan.FromSeconds(1))
                    {
                        progress?.Report(new("Downloading multilingual transcription model…", received * 100d / Size));
                        last = DateTime.UtcNow;
                    }
                }
            }
            if (new FileInfo(temporary).Length != Size || await Hash(temporary, token) != Sha256) throw new IOException("Transcription model verification failed. Retry the download.");
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<string> Hash(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }
}
