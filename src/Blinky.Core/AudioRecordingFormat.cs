namespace Blinky.Core;

public static class AudioRecordingFormat
{
    public const int SampleRate = 48000;
    public const int Bitrate = 96000;
    public static string[] Arguments(string destination) =>
    [
        "-hide_banner", "-loglevel", "error", "-n", "-f", "s16le", "-ar", "48000", "-ac", "1", "-i", "pipe:0",
        "-c:a", "aac", "-b:a", "96000", "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-frag_duration", "2000000", destination
    ];
}
