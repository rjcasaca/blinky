using System.Text.Json;

namespace Blinky.Core;

public static class McpClientConfiguration
{
    public static void Write(string folder, string executable, string recordings, string data)
    {
        Directory.CreateDirectory(folder);
        var arguments = new[] { "--recordings", recordings, "--data", data };
        var json = JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object> { ["blinky"] = new { command = executable, args = arguments } } }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(folder, "claude-desktop.json"), json);
        File.WriteAllText(Path.Combine(folder, "codex.toml"), "[mcp_servers.blinky]\ncommand = " + JsonSerializer.Serialize(executable) + "\nargs = " + JsonSerializer.Serialize(arguments) + "\nstartup_timeout_sec = 30\ntool_timeout_sec = 60\n");
    }
}
