# Blinky class transcripts, notes and summaries

Blinky includes a local stdio MCP server at `mcp/Blinky.Mcp.exe`. Claude Desktop or Codex starts it automatically when connected. There is no web server, port, Python installation or Node.js dependency. Keep the entire portable Blinky folder together.

The server transcribes finished MP4 video and M4A audio-only recordings locally. Your connected Claude/Codex model creates the detailed class notes and class summary using your Markdown templates, then saves them next to the recording. The MCP server does not run its own summarization LLM or use a separate paid transcription API.

## Generate your configuration snippets

Open Blinky and click **MCP setup & templates**. It creates configuration snippets with your actual executable and recording-folder paths in `%LOCALAPPDATA%\Blinky\mcp-config` and opens that folder. This does not modify either client automatically. If you move Blinky or change its recordings folder, generate new snippets and update the clients.

Run `Start-Blinky.cmd` at least once to ensure the Microsoft Visual C++ runtime is installed. The MCP package includes its .NET runtime and CPU Whisper runtime (including a fallback for older CPUs).

## Claude Desktop

Open Claude Desktop's Settings → Developer → Edit Config. Its Windows file is `%APPDATA%\Claude\claude_desktop_config.json`. Merge the `blinky` entry from the generated `claude-desktop.json` into your existing `mcpServers` object. Preserve any other servers. For example:

```json
{
  "mcpServers": {
    "blinky": {
      "command": "C:\\Apps\\Blinky-win-x64\\mcp\\Blinky.Mcp.exe",
      "args": ["--recordings", "C:\\Users\\you\\Videos\\Blinky", "--data", "C:\\Users\\you\\AppData\\Local\\Blinky"]
    }
  }
}
```

Restart Claude Desktop and confirm Blinky's tools appear. Availability may depend on your organization's Claude Desktop policies. Follow the client's normal tool approvals. [Official local-MCP setup guide](https://modelcontextprotocol.io/docs/develop/connect-local-servers).

## Codex

Merge the generated `codex.toml` section into your Codex MCP configuration (`%USERPROFILE%\.codex\config.toml` on a native Windows installation). Preserve other configuration. For example:

```toml
[mcp_servers.blinky]
command = 'C:\Apps\Blinky-win-x64\mcp\Blinky.Mcp.exe'
args = ['--recordings', 'C:\Users\you\Videos\Blinky', '--data', 'C:\Users\you\AppData\Local\Blinky']
startup_timeout_sec = 30
tool_timeout_sec = 60
```

Use the generated paths rather than the example. Codex's app and CLI share MCP configuration; use the app's MCP settings if preferred. The stdio command must be executable in the environment where Codex launches MCP servers. A WSL-based installation needs a command/path accessible there; the Windows snippet above targets native Windows Codex. [Official Codex MCP documentation](https://developers.openai.com/codex/mcp/).

## Ask for a full class workflow

> Use Blinky to find my latest postgraduate class. Transcribe the entire class in Portuguese, then create detailed class notes with the class-notes template and a separate class summary with class-summary. Read the entire transcript, include timestamps, and save all three as Markdown next to the recording.

Or select an exact recording by the filename returned by `list_recordings`. Transcription returns a job ID immediately; the client should poll `transcription_status` every 5–15 seconds. Once complete, it reads `read_transcript` pages using `nextOffset` until null, obtains each template via `class_material_context`, writes the content and calls `save_class_notes` once per document. The optional `class_notes_and_summary` MCP prompt provides the same instructions.

The timestamped full transcript is saved automatically. Notes and summary are distinct files, for example:

```text
class.m4a
class.transcript.abc12345.md
class.notes.class-notes.md
class.notes.class-summary.md
```

Existing exports are never replaced: repeat exports get numbered filenames. Original media is always retained. You can browse old jobs with `list_transcriptions` and request a new summary without transcribing again.

## Your templates

Three templates are created on first server startup in `%LOCALAPPDATA%\Blinky\templates`:

- `class-notes.md`: thorough postgraduate notes, definitions, explanations, models/formulas, worked examples, class discussions, readings and assignments.
- `class-summary.md`: a concise overview of the whole class, key concepts, conclusions and topics to revisit.
- `study-guide.md`: learning checklist and generated review questions with answers tied to transcript timestamps.

Edit them in any text editor, or ask the client to use `save_template`. Template names use letters, numbers, hyphens and underscores. Existing templates require explicit `overwrite=true`. `{{title}}` becomes the recording title; `{{date}}` is explicitly labeled as the transcription date, not an assumed class date. Other content is your requested Markdown structure and instructions. State the desired language, level of detail and headings in the template. User templates are preserved across server restarts.

Example custom template:

```markdown
# {{title}}

Escreve em português europeu, mantendo os termos técnicos usados pelo docente.

## Notas detalhadas por tema
Explica definições, argumentos e exemplos, com referências temporais.

## Resumo para revisão

## Dúvidas para esclarecer

## Leituras e tarefas referidas na aula
Não inventes referências, conteúdo de slides ou perguntas de exame.
```

## Local transcription and long classes

First use downloads the verified multilingual Whisper base model (about 148 MB) and the verified FFmpeg tools if absent (about 109 MB). These downloads contain software/model files; recording audio is not uploaded by the transcription server. Later transcription works offline. The transcript is provided to your chosen Claude/Codex client, so that client's normal data handling applies when it generates notes.

Audio is extracted one ten-minute mono 16 kHz PCM chunk at a time, then discarded. Timestamp offsets are retained across chunks. CPU inference uses at most four worker threads. A full class may take substantial time; check the job's state/progress. Only one transcription runs at a time across clients sharing the same Blinky data folder. For better accuracy, the server also accepts `--model <path-to-your-compatible-ggml-model>` in its launch arguments.

Automatic transcription can mishear technical terms, omit speech or hallucinate during silence, particularly at chunk boundaries. It cannot recover text that was visible only on slides, and mixed audio does not provide reliable speaker identities. The templates tell the client to mark missing information rather than invent it. Review the transcript and generated material before relying on it for study.

`cancel_transcription` cancels a job started by that MCP client. Disconnecting/quitting its client cancels its active job; partial transcripts and status remain under `%LOCALAPPDATA%\Blinky\classes\<job-id>`. Completed jobs remain usable after restart. Interrupted jobs can be retried as new jobs; automatic resume is not implemented. The standalone Blinky recorder does not need to remain open to process an existing recording.

## Access and troubleshooting

The server works only inside the explicitly configured recordings, templates and data folders. It does not expose shell commands or arbitrary file reads, and rejects unfinished recordings, path traversal and symbolic links/reparse points. Use an ordinary local recordings folder.

If the server does not connect, check the absolute executable path, run `Start-Blinky.cmd` once, and check the client's MCP logs. Logs go to stderr; stdout is reserved for MCP. If a first-use download fails, restore internet access and start another transcription. If the recording folder changes in Blinky, update the explicit `--recordings` argument in the client configuration.

Verified in Linux: real stdio initialize/tool/prompt exchanges, Markdown templates/exports, bounded audio extraction, cancellation, audio-only AAC output, and actual local speech recognition using a public speech sample. Connecting the Windows executable in Claude Desktop/Codex and native device recording still require Windows acceptance checks.
