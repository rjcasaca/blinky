using System.IO.Compression;
using System.Security.Cryptography;

namespace Blinky.Core;

public sealed record EncoderTools(string Ffmpeg, string Ffprobe);
public sealed record OptimizationProgress(string Stage, double? Percent = null);

public static class OptimizerTools
{
    public const string Version = "9.0.2";
    public const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip";
    public const string ArchiveSha256 = "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba";
    private sealed record Installation(string FfmpegHash, string FfprobeHash);

    public static async Task<EncoderTools> EnsureAsync(string cacheFolder, IProgress<OptimizationProgress>? progress, CancellationToken token)
    {
        using var lease = await ToolInstallLease.AcquireAsync(cacheFolder, "ffmpeg", token);
        var destination = Path.Combine(cacheFolder, "ffmpeg-" + Version);
        var tools = new EncoderTools(Path.Combine(destination, "ffmpeg.exe"), Path.Combine(destination, "ffprobe.exe"));
        var manifestPath = Path.Combine(destination, "installation.json");
        var installed = JsonStore.Load<Installation>(manifestPath);
        if (installed != null && File.Exists(tools.Ffmpeg) && File.Exists(tools.Ffprobe)
            && await HashAsync(tools.Ffmpeg, token) == installed.FfmpegHash
            && await HashAsync(tools.Ffprobe, token) == installed.FfprobeHash) return tools;

        Directory.CreateDirectory(cacheFolder);
        var staging = Path.Combine(cacheFolder, "download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var archivePath = Path.Combine(staging, "ffmpeg.zip");
            progress?.Report(new("Downloading optimizer (first use)…", 0));
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            const long maxBytes = 512L * 1024 * 1024;
            if (total > maxBytes) throw new IOException("The optimizer download exceeds its size limit.");
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, useAsync: true))
            {
                var buffer = new byte[131072];
                long received = 0;
                var lastUpdate = DateTime.UtcNow;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    received += count;
                    if (received > maxBytes) throw new IOException("The optimizer download exceeds its size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    if (DateTime.UtcNow - lastUpdate > TimeSpan.FromMilliseconds(250))
                    {
                        progress?.Report(new("Downloading optimizer (first use)…", total > 0 ? 100d * received / total.Value : null));
                        lastUpdate = DateTime.UtcNow;
                    }
                }
            }
            progress?.Report(new("Checking optimizer download…"));
            if (!string.Equals(await HashAsync(archivePath, token), ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The optimizer download checksum did not match. Retry the download.");

            var extracted = Path.Combine(staging, "verified");
            Directory.CreateDirectory(extracted);
            using (var zip = ZipFile.OpenRead(archivePath))
            {
                foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe", "LICENSE", "README.txt" })
                {
                    var entry = zip.Entries.SingleOrDefault(e => e.Name == name);
                    if (entry == null)
                    {
                        if (name.EndsWith(".exe", StringComparison.Ordinal)) throw new IOException("The optimizer archive is missing " + name);
                        continue;
                    }
                    await using var input = entry.Open();
                    await using var output = new FileStream(Path.Combine(extracted, name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, useAsync: true);
                    await input.CopyToAsync(output, token);
                }
            }
            var manifest = new Installation(await HashAsync(Path.Combine(extracted, "ffmpeg.exe"), token), await HashAsync(Path.Combine(extracted, "ffprobe.exe"), token));
            JsonStore.Save(Path.Combine(extracted, "installation.json"), manifest);
            token.ThrowIfCancellationRequested();
            // Preserve an incomplete/obsolete installation instead of merging it
            // with the newly verified pair of executables.
            if (Directory.Exists(destination)) Directory.Move(destination, destination + ".old-" + Guid.NewGuid().ToString("N"));
            Directory.Move(extracted, destination);
            return tools;
        }
        finally { Directory.Delete(staging, recursive: true); }
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }
}
