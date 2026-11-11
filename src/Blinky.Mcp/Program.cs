using Blinky.Core;
using Blinky.Notes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blinky");
var settings = (JsonStore.Load<Settings>(Path.Combine(appData, "settings.json")) ?? new Settings()).Validated();
var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length || args[i] is not ("--recordings" or "--data" or "--ffmpeg" or "--ffprobe" or "--model"))
    {
        Console.Error.WriteLine("Usage: Blinky.Mcp [--recordings <folder>] [--data <folder>] [--ffmpeg <path>] [--ffprobe <path>] [--model <ggml-model-path>]");
        return 1;
    }
    arguments.Add(args[i], args[i + 1]);
}
var data = arguments.GetValueOrDefault("--data", appData);
var workspace = new NotesWorkspace(arguments.GetValueOrDefault("--recordings", settings.OutputFolder), data);
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders().AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(workspace);
builder.Services.AddSingleton<ITranscriptionRuntime>(new LocalWhisperRuntime(Path.Combine(data, "tools"), arguments.GetValueOrDefault("--model")));
builder.Services.AddSingleton(services => new TranscriptionJobs(workspace, services.GetRequiredService<ITranscriptionRuntime>(), async token =>
{
    if (arguments.TryGetValue("--ffmpeg", out var ffmpeg))
        return new EncoderTools(ffmpeg, arguments.GetValueOrDefault("--ffprobe", Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe")));
    if (OperatingSystem.IsWindows()) return await OptimizerTools.EnsureAsync(Path.Combine(data, "tools"), null, token);
    return new EncoderTools("ffmpeg", arguments.GetValueOrDefault("--ffprobe", "ffprobe"));
}));
builder.Services.AddHostedService<TranscriptionLifetime>();
builder.Services.AddMcpServer(options => options.ServerInfo = new Implementation { Name = "blinky", Version = "0.3.0" })
    .WithStdioServerTransport().WithTools<ClassTools>().WithPrompts<ClassPrompts>();
await builder.Build().RunAsync();
return 0;

sealed class TranscriptionLifetime(TranscriptionJobs jobs) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken cancellationToken) => await jobs.DisposeAsync();
}
