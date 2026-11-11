namespace Blinky.Core;

public sealed record RecordingProfile(string Name, int FramesPerSecond, int VideoBitrate, int Quality)
{
    public static IReadOnlyList<RecordingProfile> All { get; } =
    [
        new("Compact", 15, 2_000_000, 65),
        new("Balanced", 30, 4_000_000, 75),
        new("Smooth", 60, 8_000_000, 85)
    ];

    // Nominal bitrate estimate; quality-based recordings vary with content.
    public double EstimatedGigabytesPerHour => (VideoBitrate + 128_000) * 3600d / 8 / 1_000_000_000;

    public static (int Width, int Height) Fit(int width, int height)
    {
        if (width < 2 || height < 2) throw new ArgumentOutOfRangeException(nameof(width));
        var scale = Math.Min(1d, Math.Min(1920d / width, 1080d / height));
        return (Math.Max(2, (int)(width * scale) / 2 * 2), Math.Max(2, (int)(height * scale) / 2 * 2));
    }
}

public static class DiskPolicy
{
    public static long ReserveBytes(int videoBitrate) => Math.Max(512L * 1024 * 1024, (videoBitrate + 128_000L) * 60 / 8);
    public static bool MustStop(long freeBytes, int videoBitrate) => freeBytes <= ReserveBytes(videoBitrate);
}
