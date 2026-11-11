# Privacy and data handling

This document describes the repository's implementation, not a guarantee about your operating system, connected clients, modified copies or third-party software. See [LEGAL.md](LEGAL.md) for recording and consent responsibilities.

## What recording captures

Screen mode captures the selected display/window and mixes the selected microphone with audio routed to the selected system output. Audio-only mode captures and mixes those audio sources without a video stream. Background sounds, private information, other applications and other people's speech may be included. Window selection limits video, not system audio to that window's application.

While the settings window is visible and idle, Blinky opens audio devices for preflight checks and meters. Hiding an idle window releases those streams. Recording keeps them open. Meeting prompts inspect active microphone audio sessions and process identities/ancestry to recognize configured application types. This inspection is local; the project does not receive the process list. Browser detection can also recognize non-meeting microphone activity.

## Local storage

- Recordings and smaller copies are saved in the folder you select.
- Preferences and the latest recording journal normally live in `%LOCALAPPDATA%\Blinky` and include device identifiers, local paths and status information.
- Transcription jobs, status, partial/full transcripts and segment metadata live under that data folder's `classes` directory. Extracted chunk WAVs are removed after processing or cancellation; a crash can leave temporary files.
- Editable templates live in `templates`; downloaded tools/models in `tools`; generated client configuration snippets in `mcp-config`.
- Completed transcripts, class notes and summaries are exported next to the source recording. Older exports and original recordings are preserved.

The application does not provide built-in encryption, secure erasure, anonymization, account authentication or automatic retention/deletion policies. Local files and configuration snippets are ordinary files accessible according to your Windows account and filesystem permissions. Backups, synced folders, device administrators and other software may make additional copies. Secure and delete your files according to your needs and obligations. Do not remove an in-use file during recording or transcription.

## Network activity

The project contains no analytics collector or author-operated upload service. The app does not itself upload recordings for transcription; the implemented Whisper inference runs locally.

First use of relevant features downloads verified FFmpeg tools from Gyan.dev and a verified Whisper model from Hugging Face, potentially via their redirects/CDNs. Packaging downloads Microsoft's Visual C++ installer; running vendor prerequisites can have their own behavior. Download hosts can receive ordinary connection information such as IP addresses. Those providers' policies apply. Build/package dependency restoration also contacts NuGet and other configured development services.

## Claude, Codex and other MCP clients

The MCP server is a local stdio process launched by the client. It is not a public HTTP server and exposes no listening port. Its tools can return recording filenames, job metadata, templates and transcript text to the connected client and can write the Markdown requested by that client. Clients may send received information to cloud models, retain conversation history, or invoke additional tools. Their own settings, agreements, policies and administrative controls determine that handling; Blinky cannot control or guarantee it.

Choose the client and material deliberately, use its tool approvals, and check whether your class, employer or participants permit that sharing. Sharing transcripts with an AI client is separate from keeping speech recognition local.

The MCP workspace restricts its exposed file operations to configured folders and rejects path traversal and symbolic links/reparse points. These checks are not an operating-system sandbox: the server process runs with your user permissions. Configure only folders you intend that client to access, and run trusted binaries on a trusted account.

## Logs, support and deletion

Local MCP diagnostics use stderr and can appear in client logs. Error messages can contain paths, filenames or device details. There is no requirement to send recordings, transcripts or diagnostic logs to the maintainer. Use a fabricated or consented minimal reproduction for issues. Redact personal data, credentials, paths and confidential course content before sharing anything.

The maintainer does not automatically receive or store files recorded by your local copy. To delete local material, stop the relevant job and remove the files and copies you control, including exports, client history, synced storage and backups where applicable. Deleting a local file does not delete copies already sent to a connected client or provider.
