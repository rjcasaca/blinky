using System.Text.Json;

namespace Blinky.Core;

public sealed record Settings
{
    public string OutputFolder { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Blinky");
    public string Profile { get; init; } = "Balanced";
    public bool UseHevc { get; init; }
    public bool HardwareEncoding { get; init; } = true;
    public string? MicrophoneId { get; init; }
    public string? SystemAudioId { get; init; }
    public double MicrophoneVolume { get; init; } = 0.8;
    public double SystemVolume { get; init; } = 0.8;
    public bool AudioOnly { get; init; }
    public bool MeetingPrompts { get; init; } = true;
    public bool IncludeBrowserMeetings { get; init; }
    public int MeetingEndDelaySeconds { get; init; } = 60;

    public Settings Validated() => this with
    {
        Profile = RecordingProfile.All.Any(p => p.Name == Profile) ? Profile : "Balanced",
        OutputFolder = string.IsNullOrWhiteSpace(OutputFolder) ? new Settings().OutputFolder : OutputFolder,
        MicrophoneVolume = double.IsFinite(MicrophoneVolume) ? Math.Clamp(MicrophoneVolume, 0.05, 1) : 0.8,
        SystemVolume = double.IsFinite(SystemVolume) ? Math.Clamp(SystemVolume, 0.05, 1) : 0.8,
        MeetingEndDelaySeconds = Math.Clamp(MeetingEndDelaySeconds, 0, 600)
    };
}

public static class JsonStore
{
    public static T? Load<T>(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }
}

public sealed record SessionJournal(string FilePath, DateTimeOffset StartedUtc, string State, string? Message = null);
