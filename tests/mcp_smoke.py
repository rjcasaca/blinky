"""Real stdio MCP handshake and tool/prompt integration; no LLM or network."""
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
import threading

passed = 0
def check(condition, name):
    global passed
    assert condition, name
    passed += 1
    print("PASS:", name)

with tempfile.TemporaryDirectory(prefix="blinky-mcp-") as temporary:
    root = Path(temporary)
    recordings, data = root / "class recordings", root / "data"
    recordings.mkdir()
    source = recordings / "Postgraduate class.mp4"
    source.write_bytes(b"original recording must survive")
    (recordings / "unfinished.recording.mp4").write_bytes(b"in progress")
    (recordings / "audio class.m4a").write_bytes(b"finished audio")
    command = sys.argv[1:] or ["dotnet", "src/Blinky.Mcp/bin/Release/net10.0/Blinky.Mcp.dll"]
    process = subprocess.Popen(command + ["--recordings", str(recordings), "--data", str(data)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8", bufsize=1)
    responses = queue.Queue()
    errors = []
    def stdout_reader():
        for line in process.stdout:
            try: responses.put(json.loads(line))
            except json.JSONDecodeError: responses.put({"invalid_stdout": line})
    def stderr_reader():
        for line in process.stderr:
            errors.append(line)
            del errors[:-30]
    threading.Thread(target=stdout_reader, daemon=True).start()
    threading.Thread(target=stderr_reader, daemon=True).start()
    request_id = 0
    def rpc(method, params=None, notify=False):
        global request_id
        request_id += 1
        request = {"jsonrpc": "2.0", "method": method}
        if not notify: request["id"] = request_id
        if params is not None: request["params"] = params
        process.stdin.write(json.dumps(request) + "\n")
        process.stdin.flush()
        if notify: return None
        while True:
            try: result = responses.get(timeout=15)
            except queue.Empty: raise AssertionError("MCP response timed out: " + "".join(errors))
            if "invalid_stdout" in result: raise AssertionError("Non-protocol data on stdout: " + result["invalid_stdout"])
            if result.get("id") == request_id:
                if "error" in result: raise AssertionError(result["error"])
                return result["result"]
    def call(name, arguments=None):
        return rpc("tools/call", {"name": name, "arguments": arguments or {}})
    def text(result):
        return "\n".join(item.get("text", "") for item in result.get("content", []))
    try:
        initialized = rpc("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "blinky-smoke", "version": "1"}})
        check(initialized["serverInfo"]["name"] == "blinky", "official MCP stdio initialize handshake")
        rpc("notifications/initialized", notify=True)
        tools = rpc("tools/list")["tools"]
        names = {tool["name"] for tool in tools}
        check({"start_transcription", "read_transcript", "class_material_context", "save_class_notes", "save_template"} <= names, "class transcription/template/Markdown tools are advertised")
        read_tool = next(tool for tool in tools if tool["name"] == "read_transcript")
        check(read_tool["annotations"]["readOnlyHint"], "read-only tools advertise appropriate MCP annotations")
        check("class recordings" in text(call("blinky_status")), "server uses the explicitly configured recordings folder")
        listed = text(call("list_recordings"))
        check("Postgraduate class.mp4" in listed and "audio class.m4a" in listed and "unfinished" not in listed, "MCP lists finished video and audio classes only")
        check("class-summary" in text(call("list_templates")), "class-summary template is exposed")
        saved = call("save_template", {"name": "my-course", "markdown": "# {{title}}\n\n## Concepts\n\nUse Portuguese."})
        check(not saved.get("isError") and (data / "templates/my-course.md").exists(), "MCP saves a custom Markdown course template")
        check("Portuguese" in text(call("get_template", {"name": "my-course"})), "MCP reads the user's custom template")
        check(call("save_template", {"name": "../outside", "markdown": "bad"}).get("isError"), "MCP rejects template traversal")
        check(call("start_transcription", {"recordingId": "../outside.mp4"}).get("isError"), "MCP rejects recording traversal before starting any download")
        check(call("start_transcription", {"recordingId": "unfinished.recording.mp4"}).get("isError"), "MCP rejects active recording transcription")
        check(call("read_transcript", {"jobId": "../outside"}).get("isError"), "MCP rejects job path traversal")

        job = "a" * 32
        folder = data / "classes" / job
        folder.mkdir()
        transcript = "# Full class transcript\n\n[00:00:00] First topic.\n\n[00:04:00] Last topic.\n"
        (folder / "transcript.md").write_text(transcript, encoding="utf-8")
        (folder / "job.json").write_text(json.dumps({"Id": job, "RecordingId": source.name, "Language": "pt", "StartedUtc": "2026-11-10T12:00:00+00:00", "State": "completed", "Stage": "Complete", "CompletedSeconds": 300, "DurationSeconds": 300, "OwnerProcessId": process.pid}), encoding="utf-8")
        check("Last topic" in text(call("read_transcript", {"jobId": job})), "completed full-class transcript is readable over MCP")
        check("nextoffset" in text(call("read_transcript", {"jobId": job, "maxCharacters": 20})).lower(), "long transcripts expose pagination")
        context = text(call("class_material_context", {"jobId": job, "template": "class-notes"}))
        check("EVERY page" in context and "postgraduate" in context, "class context asks the client to cover the entire transcript")
        prompts = rpc("prompts/list")["prompts"]
        check(any(p["name"] == "class_notes_and_summary" for p in prompts), "class-notes-and-summary MCP prompt is advertised")
        prompt = rpc("prompts/get", {"name": "class_notes_and_summary", "arguments": {"jobId": job}})
        check("class-summary" in json.dumps(prompt), "prompt requests both detailed notes and a separate summary")
        notes = call("save_class_notes", {"jobId": job, "template": "class-notes", "markdown": "# Detailed notes\n\nConcepts with timestamps."})
        summary = call("save_class_notes", {"jobId": job, "template": "class-summary", "markdown": "# Class summary\n\nKey takeaways."})
        check(not notes.get("isError") and not summary.get("isError") and len(list(recordings.glob("*.md"))) == 2, "notes and summary save as separate Markdown files over MCP")
        check(source.read_bytes() == b"original recording must survive", "MCP leaves the original recording intact")
    finally:
        process.stdin.close()
        try: process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.terminate()
            process.wait(timeout=5)
            raise AssertionError("MCP server did not stop when its stdio client disconnected")
    check(process.returncode == 0, "MCP server shuts down cleanly on client disconnect")
print(f"\n{passed} MCP protocol checks passed.")
