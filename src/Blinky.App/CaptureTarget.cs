using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using ScreenRecorderLib;
using Forms = System.Windows.Forms;

namespace Blinky.App;

public sealed record CaptureTarget(string Name, string? DisplayName, IntPtr WindowHandle, int Width, int Height)
{
    public RecordingSourceBase CreateSource()
    {
        if (DisplayName != null) return new DisplayRecordingSource(DisplayName);
        if (!IsWindow(WindowHandle) || IsIconic(WindowHandle))
            throw new InvalidOperationException("The selected window was closed or minimized. Restore it and refresh sources.");
        return new WindowRecordingSource(WindowHandle);
    }

    public (int Width, int Height) CurrentSize()
    {
        if (DisplayName != null) return (Width, Height);
        if (!GetWindowRect(WindowHandle, out var rect)) throw new InvalidOperationException("Cannot read the selected window size.");
        return (rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public static List<CaptureTarget> Discover(IntPtr ownHandle)
    {
        var targets = new List<CaptureTarget>();
        foreach (var screen in Forms.Screen.AllScreens)
            targets.Add(new($"Screen · {screen.DeviceName} · {screen.Bounds.Width} × {screen.Bounds.Height}" + (screen.Primary ? " · primary" : ""), screen.DeviceName, IntPtr.Zero, screen.Bounds.Width, screen.Bounds.Height));
        EnumWindows((handle, _) =>
        {
            if (handle == ownHandle || !IsWindowVisible(handle) || IsIconic(handle)) return true;
            var title = new StringBuilder(512);
            GetWindowText(handle, title, title.Capacity);
            if (title.Length == 0 || !GetWindowRect(handle, out var rect) || rect.Right - rect.Left < 100 || rect.Bottom - rect.Top < 100) return true;
            targets.Add(new("Window · " + title, null, handle, rect.Right - rect.Left, rect.Bottom - rect.Top));
            return true;
        }, IntPtr.Zero);
        return targets;
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out Rect rect);
}
