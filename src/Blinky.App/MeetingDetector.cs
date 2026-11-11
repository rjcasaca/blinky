using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Blinky.App;

public sealed record MeetingSignal(bool? Active, string Description);

public static class MeetingDetector
{
    private static readonly HashSet<string> NativeApps = new(StringComparer.OrdinalIgnoreCase) { "Teams", "ms-teams", "Zoom", "CptHost", "webex", "CiscoCollabHost", "slack" };
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase) { "chrome", "msedge", "firefox", "brave", "opera" };

    public static MeetingSignal Scan(bool includeBrowsers)
    {
        try
        {
            var processes = Snapshot();
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            using (device)
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    if ((int)session.State != 1) continue; // AudioSessionStateActive
                    int pid = (int)session.GetProcessID;
                    if (pid == Environment.ProcessId) continue;
                    for (int depth = 0; depth < 6 && pid > 0 && processes.TryGetValue(pid, out var process); depth++, pid = process.Parent)
                    {
                        if (NativeApps.Contains(process.Name)) return new(true, process.Name + " microphone activity");
                        if (includeBrowsers && Browsers.Contains(process.Name)) return new(true, process.Name + " browser microphone activity");
                    }
                }
            }
            return new(false, "Waiting for meeting microphone activity");
        }
        catch (Exception error) { return new(null, "Meeting detection unavailable: " + error.Message); }
    }

    private static Dictionary<int, (int Parent, string Name)> Snapshot()
    {
        var result = new Dictionary<int, (int, string)>();
        var handle = CreateToolhelp32Snapshot(2, 0);
        if (handle == new IntPtr(-1)) throw new InvalidOperationException("Could not inspect meeting applications.");
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Executable = "" };
            if (Process32First(handle, ref entry))
            do { result[(int)entry.ProcessId] = ((int)entry.ParentProcessId, System.IO.Path.GetFileNameWithoutExtension(entry.Executable)); }
            while (Process32Next(handle, ref entry));
        }
        finally { CloseHandle(handle); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
