using System.ComponentModel;
using System.Diagnostics;

namespace Blinky.Core;

public static class MediaProcess
{
    public static async Task RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? stdout, CancellationToken token, Action? monitor = null)
    {
        token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException("Could not start the video optimizer.");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception e) when (e is Win32Exception or PlatformNotSupportedException or InvalidOperationException) { }
        var errors = new Queue<string>();
        async Task ReadOutput()
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) != null) stdout?.Invoke(line);
        }
        async Task ReadErrors()
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) != null)
            {
                if (errors.Count == 20) errors.Dequeue();
                errors.Enqueue(line.Length > 1000 ? line[..1000] : line);
            }
        }
        var outputTask = ReadOutput();
        var errorsTask = ReadErrors();
        var exit = process.WaitForExitAsync();
        try
        {
            while (!exit.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                monitor?.Invoke();
                if (outputTask.IsFaulted) await outputTask;
                await Task.WhenAny(exit, Task.Delay(250, token));
            }
            await exit;
            await Task.WhenAll(outputTask, errorsTask);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException("Video optimizer failed: " + string.Join("\n", errors));
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
            await exit;
            try { await Task.WhenAll(outputTask, errorsTask); } catch { /* Preserve the original failure/cancellation. */ }
            throw;
        }
    }
}
