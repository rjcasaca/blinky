# Blinky

[![Build Windows portable app](https://github.com/rjcasaca/blinky/actions/workflows/build.yml/badge.svg)](https://github.com/rjcasaca/blinky/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**Experimental preview, provided as is.** Recording, recovery, meeting detection and AI outputs are not guaranteed. You are responsible for required recording permissions and handling class/participant information. See [legal notices](LEGAL.md), [privacy/data handling](PRIVACY.md), [security](SECURITY.md) and [third-party licenses](THIRD-PARTY-NOTICES.md).

A Windows recorder for long classes and meetings. Capture a screen/window or audio only with **microphone and system audio together**, then leave it in the tray. Connect Claude/Codex through the local MCP server for full class transcripts, detailed notes and summaries in your Markdown templates.

## Run on Windows

Use Windows 10 22H2 or Windows 11, x64.

Build from source using the instructions below, or download the `Blinky-win-x64` artifact from a successful [Windows build workflow](https://github.com/rjcasaca/blinky/actions/workflows/build.yml). GitHub may require sign-in to download workflow artifacts. Generated binaries and personal recordings are not stored in the source repository.

1. Extract the entire `Blinky-win-x64.zip` archive to a local folder.
2. Double-click `Start-Blinky.cmd`. On first run it installs Microsoft's Visual C++ x64 runtime if needed; Windows may ask for administrator approval for that prerequisite. The bundled installer is checked for a valid Microsoft signature before it runs. Subsequent launches can use `Blinky.exe` directly.
3. Choose screen recording or **Audio only**, a microphone, and the speakers/headphones whose audio you want to record. Screen selection is disabled in audio-only mode.
4. Speak and play audio to check both meters. Choose a preset and save folder, then start recording.

.NET is included. No manual FFmpeg installation, Python, virtual audio driver or developer tools are needed. Audio-only recording, optimization and transcription download verified FFmpeg tools on first use; later runs work offline. Video capture itself does not need that download. Windows N editions need the Media Feature Pack. Enable desktop microphone access in Windows privacy settings. Use H.264 if live HEVC is unsupported; disable hardware encoding if your GPU encoder is unreliable.

Closing or minimizing the window leaves Blinky in the tray. Double-click the tray icon to reopen it; use its menu to stop/save or quit. An idle hidden app releases the audio devices. A visible idle app opens both devices to provide live meters.

| Shortcut | Action |
| --- | --- |
| Ctrl + Shift + F9 | Start recording / stop and save |
| Ctrl + Shift + F10 | Pause / resume |

Both shortcuts work outside Blinky. A message in the window indicates if another application has registered either combination.

## Audio is mandatory

Both a microphone and a system output must be selected and open successfully. Neither source can be disabled or reduced to zero in Blinky. Their sliders affect the recording mix, not Windows volume. Video uses mixed stereo AAC at 128 kbps. Audio-only mode downmixes both to mono AAC at 96 kbps in M4A (about 43 MB/hour), creating no video stream or screen frames. Use headphones to avoid echo.

Independent WASAPI capture streams provide endpoint checks and live endpoint peak meters. Blinky checks selected device availability about once per second, and listens for capture failures. A detected audio failure stops and saves the video and displays an alert. Ordinary silence is valid audio; meters do not certify that a recording contains audible speech. Windows mute switches, app-specific routing, microphone hardware switches, protected content, or a malfunctioning driver can still produce silence—check a short recording before a long session.

System audio means all sound routed to the selected output device. Moving an application to a different output does not move Blinky's selection. A window selection limits video; it does not limit system audio to that application. Keep captured windows restored rather than minimized.

## Long recordings and storage

| Preset | Frame-rate ceiling | Quality setting |
| --- | --- | --- |
| Compact | 15 fps | 65 |
| Balanced | 30 fps | 75 |
| Smooth | 60 fps | 85 |

Output fits within 1920 × 1080, preserves the source aspect ratio, uses even encoder dimensions, and never enlarges smaller sources. Hardware encoding is requested by default. Quality-based H.264 or optional HEVC allows file size to vary with scene complexity; presets do not impose a fixed storage budget. Screen text, scrolling, and playback quality need calibration on actual Windows hardware.

Video is encoded directly to disk with renderer throttling enabled, without retaining a recording in memory. Fragmented MP4 avoids depending on one final end-of-file header. While recording, the filename ends in `.recording.mp4`; after a successful stop it becomes `.mp4`. No second full-sized copy or final full-file rewrite is required.

Before starting and during recording, Blinky checks free space and stops at a reserve of at least 512 MiB (or a minute of the preset's nominal video/audio bitrate, whichever is larger). This is a best-effort reserve, not a quota; other programs can consume disk space concurrently. Use a local disk and leave plenty of headroom.

## Interrupted recordings

Preferences and the latest session journal live in `%LOCALAPPDATA%\Blinky`. If the last session was interrupted, the next launch shows the partial file's path. Blinky never deletes an interrupted video. Try opening its `.recording.mp4` in VLC; completed fragments may remain playable, with the unfinished tail missing. Crash recovery depends on what the encoder and OS flushed and is **not yet validated on Windows**. Keep the original file when attempting repair with another tool.

## Create a smaller copy

After stopping a recording, scroll to **Smaller copy** and click **Create smaller copy**. The latest finished recording is selected automatically, including after reopening Blinky. Use **Choose recording…** to select an earlier MP4. Unfinished `.recording.mp4` files cannot be optimized.

The first use downloads the pinned FFmpeg 9.0.2 Windows essentials build from Gyan.dev (about 109 MB) to `%LOCALAPPDATA%\Blinky\tools`. Blinky checks the archive against a pinned SHA-256 checksum before extracting the tools and verifies their cached hashes on later uses. The downloaded license and README stay with the tools. There is no installer or administrator step for the optimizer. Subsequent optimization works offline.

Compression runs as a separate process at below-normal priority using software HEVC (`libx265`, slow preset, CRF 26, bounded worker threads). It preserves frame timing and resolution and copies all existing audio tracks without re-encoding them. This does not require a Windows HEVC encoder, but playback of the resulting file requires an HEVC-capable player. Small text can lose detail during this lossy conversion; visually compare representative recordings before deciding which copy to retain.

The panel shows progress for download, encoding, and verification. Before publishing the result, Blinky checks video size/duration, audio stream properties and durations, and decodes the entire output to catch playback errors. It saves `name.smaller.mp4` alongside the original and shows the percentage and MB saved. Existing files are never overwritten; repeated copies receive a numeric suffix. If the encode is larger, its temporary output is discarded and no extra copy is kept. These checks catch technical failures; they do not measure perceptual text readability or certify audible speech.

**The original is always kept.** Use **Cancel** to terminate the optimizer and remove its temporary output. Starting a new recording or quitting Blinky also cancels optimization before proceeding. Closing the window keeps optimization running in the tray. There is one job at a time; automatic idle queues and resumable optimization are not implemented.

Leave at least the original file's size plus 512 MiB free for a second copy. Blinky monitors free space while encoding/validating and cancels on low disk space. This can take substantial time for hours-long recordings. If Windows or Blinky crashes during optimization, the original remains and a `.blinky.optimizing-*.mp4` temporary file may remain in that folder; it can be deleted after confirming no optimization is running. There is no automatic original deletion.

## Build

Install the .NET 10 SDK. From this folder:

```powershell
dotnet build src/Blinky.App/Blinky.App.csproj -c Release
dotnet run --project tests/Blinky.Core.Tests
dotnet run --project tests/Blinky.Optimizer.Tests
./scripts/publish.ps1
```

The publish script creates `artifacts/Blinky-win-x64.zip`, includes the .NET runtime and third-party licenses, and downloads the official signed Visual C++ prerequisite. Run it on Windows to validate that installer's signature. The GitHub Actions workflow runs core checks and produces the same archive when this project is pushed as a repository.

Linux can cross-build/publish the Windows executable using `EnableWindowsTargeting`; it cannot run WPF or validate desktop/audio capture. Core checks run on either platform without a test-framework dependency. Optimizer integration checks need `ffmpeg` with `libx264`/`libx265` and `ffprobe` on PATH, or paths supplied through `FFMPEG_PATH`/`FFPROBE_PATH`. On Windows, `dotnet run --project tests/Blinky.Optimizer.Tests -- --windows-tools <cache-folder>` tests first-use installation and runs the suite with that verified build. The CI workflow uses this Windows mode.

## Verification status and next work

Verified here: 50 core checks including meeting policy and shared tool-install locking; 23 real optimizer checks; 21 class workflow/audio-only encoding checks plus two real local speech-recognition checks; 20 actual stdio MCP checks; verified model/tool downloads and cache reuse. Windows builds compile without warnings/errors. Windows rendering, device capture/mixing, meeting detection, notification clicks, client integration and hours-long synchronization remain unverified here.

Before relying on Blinky for a long session, run the acceptance procedure in [WINDOWS-VALIDATION.md](WINDOWS-VALIDATION.md). No hours-long recording, memory benchmark, or successful crash-recovery test has been claimed.

This first implementation uses a single process and one fragmented MP4 per session. A separate recording worker, segment rotation, AV1, separate editable audio tracks, custom region capture, installer, and video editing remain future work.

## Layout

The larger window uses two columns and custom minimize, maximize/restore and close controls instead of the native title bar. Drag the header to move it. Content scales down on smaller screens without a scroll container.

Audio-only mode writes fragmented `.recording.m4a`, renaming to `.m4a` after saving. Pause excludes paused time, and capture buffers are bounded. Native WASAPI mixing, pause/device behavior and hours-long drift need Windows verification.

Meeting prompts watch active microphone sessions belonging to Teams, Zoom, Webex and Slack, including identifiable child processes. Stable activity offers a tray notification; click it or **Record detected meeting (audio only)** in the tray to begin. There is no floating overlay or recording without acceptance. Only prompt-started recordings stop automatically: immediately or after 60 seconds (default), 2 minutes or 5 minutes without meeting mic activity. Rejoining resets the timer. Manual recordings never auto-stop; disabling prompts releases ownership; failed scans do not count as meeting end.

Detection is a microphone-activity heuristic, not a provider calendar/call-state API. Pre-join previews can prompt, some mute/device/permission changes can look like call end, and app changes can prevent detection. Optional browser detection also includes non-meeting mic use, such as dictation. Enable it for browser meetings. Windows notification settings can hide prompts; the tray menu remains available. Start important classes manually until you have verified your apps.

For full class transcripts, detailed notes and summaries, click **MCP setup & templates** and follow [MCP-SETUP.md](MCP-SETUP.md). The included `mcp/Blinky.Mcp.exe` transcribes finished MP4/M4A classes locally with timestamped Markdown and bounded chunks. No transcription API key or audio upload is needed. Your connected Claude/Codex model reads the full transcript and generates distinct `class-notes` and `class-summary` documents; `study-guide` is also available. Edit `%LOCALAPPDATA%\Blinky\templates` or use the MCP template tool. Client model/data handling applies to notes generation. Original recordings and old exports are preserved.

Additional developer checks: `dotnet run --project tests/Blinky.Notes.Tests`, `dotnet build src/Blinky.Mcp/Blinky.Mcp.csproj -c Release`, then `python tests/mcp_smoke.py`. The notes suite needs FFmpeg/ffprobe; `--real <speech.wav>` additionally tests local inference. Protocol checks need no LLM/network. See the Windows acceptance checklist for native validation.

- `src/Blinky.App`: WPF window, tray and global shortcuts, source discovery, WASAPI preflight/meters, native recording service.
- `src/Blinky.Notes`: local speech recognition, bounded class jobs, editable templates and Markdown exports.
- `src/Blinky.Mcp`: stdio class tools and the class-notes/summary prompt for Claude Desktop/Codex.
- `src/Blinky.Core`: recording profiles, output dimensions, disk reserve policy, preferences, recovery journals, optimizer tool installation, and background media compression/validation.
- `tests/Blinky.Core.Tests`: executable core regression checks.
- `tests/Blinky.Optimizer.Tests`: real media integration checks and optional Windows tool download verification.
- `scripts`: Windows portable packaging and launcher.

Native capture and encoding use [ScreenRecorderLib 7.0.1](https://github.com/sskodje/ScreenRecorderLib) and Microsoft Media Foundation. Audio device monitoring uses [NAudio 2.2.1](https://github.com/naudio/NAudio). See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## License

Blinky original code/documentation: [MIT](LICENSE), permitting commercial use, modification, redistribution and resale with its copyright/license notices retained. The standard license includes warranty and liability disclaimers; it is not a guarantee of immunity from claims or of enforceability in every jurisdiction. Dependencies and downloaded tools retain their own licenses. [LEGAL.md](LEGAL.md) explains scope and limitations without adding restrictions to the MIT grant.
