# Third-party components

Blinky original code is MIT licensed; this does not relicense third-party components. See [DEPENDENCIES.md](DEPENDENCIES.md) and `licenses/Dependency-Notices.txt` for resolved direct/transitive package versions and retained notices. Runtime, native-library and separately downloaded model/tool notices below also apply.

- ModelContextProtocol / ModelContextProtocol.Core 2.4.0 — Model Context Protocol, a Series of LF Projects, LLC; Apache-2.0. See `licenses/MCP-Apache-2.0.txt`. Source: https://github.com/modelcontextprotocol/csharp-sdk
- Whisper.net and CPU runtimes 1.8.1 (including NoAvx) — Sandro Hanea and contributors; MIT. See `licenses/Whisper.net.txt`. Source: https://github.com/sandrohanea/whisper.net/tree/1.8.1 ; native whisper.cpp: https://github.com/ggml-org/whisper.cpp (MIT).
- Multilingual Whisper base model — OpenAI Whisper, MIT. Downloaded separately from https://huggingface.co/ggerganov/whisper.cpp ; source/model license: https://github.com/openai/whisper/blob/main/LICENSE . The pinned checksum is verified before use. Not bundled in the portable archive.

- ScreenRecorderLib 7.0.1 — Copyright Sverre Skodje; MIT. Native capture and Media Foundation encoding. License in `licenses/ScreenRecorderLib.txt`. Source: https://github.com/sskodje/ScreenRecorderLib
- NAudio 2.2.1 and its component assemblies — Copyright Mark Heath and contributors; MIT. License in `licenses/NAudio.txt`. Source: https://github.com/naudio/NAudio
- Microsoft .NET / Windows Desktop runtime — included in self-contained distributions. See `licenses/DotNet.txt`, `licenses/WindowsDesktop.txt`, and `licenses/DotNet-ThirdParty.txt`. Source: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf
- Microsoft Visual C++ Redistributable — Microsoft's signed prerequisite installer is included in packaged releases downloaded by the publishing script. It is governed by Microsoft's installer license terms. Source: https://aka.ms/vs/17/release/vc_redist.x64.exe
- Optional FFmpeg 9.0.2 essentials Windows build by Gyan Doshi — downloaded directly from the publisher for audio-only recording, optimization or transcription, not bundled in the Blinky archive. GPLv3 build including libx265. Download: https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip ; published checksum: https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip.sha256 . Build, license and source information: https://www.gyan.dev/ffmpeg/builds/ ; FFmpeg source: https://github.com/FFmpeg/FFmpeg/commit/946fcce07b . Downloaded license and README are retained with the cached tools.

Live recording uses Windows Media Foundation and the installed hardware/drivers. Optional post-recording optimization uses the separately downloaded FFmpeg executables as child processes. Blinky's portable archive does not bundle FFmpeg or its codec binaries.
