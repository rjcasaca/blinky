namespace Blinky.Core;

public static class ToolInstallLease
{
    // Both the desktop app and separate MCP clients share the FFmpeg cache.
    public static async Task<FileStream> AcquireAsync(string folder, string name, CancellationToken token)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".install.lock");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (File.Exists(path)) { await Task.Delay(250, token); }
        }
    }
}
