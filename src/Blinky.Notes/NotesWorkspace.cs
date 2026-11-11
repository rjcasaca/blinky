using System.Text;
using System.Text.RegularExpressions;
using Blinky.Core;

namespace Blinky.Notes;

public sealed record RecordingInfo(string Id, long Bytes, DateTime ModifiedUtc);
public sealed record TextPage(string Text, int Offset, int? NextOffset, int TotalCharacters);

public sealed class NotesWorkspace
{
    public string RecordingsFolder { get; }
    public string DataFolder { get; }
    public string TemplatesFolder { get; }
    public string JobsFolder { get; }
    public static readonly IReadOnlyDictionary<string, string> BuiltInTemplates = new Dictionary<string, string>
    {
        ["class-notes"] = "# {{title}} — Class notes\n\nDate: {{date}}\nCourse / lecturer: only if stated in the transcript\n\n## Class overview and learning goals\n\n## Detailed notes by topic\n\nExplain each substantive topic in the order taught. Include definitions, reasoning, distinctions, caveats, and relationships between concepts. Cite transcript timestamps.\n\n## Examples and case studies\n\nPreserve the lecturer's concrete examples and worked steps.\n\n## Models, formulas and methods\n\nUse Markdown/LaTeX where helpful. Do not reconstruct unreadable or unspoken slide content; mark it as unavailable.\n\n## Class discussion and questions\n\n## Assignments, readings and deadlines\n\nList only what was explicitly stated.\n\n## Points to clarify\n\n## Key terminology\n\nUse the language of the class unless the user requests another. Preserve technical terminology. These are postgraduate study notes: explain the material thoroughly, rather than reducing the class to a short summary. Do not invent examples, references or exam expectations.\n",
        ["class-summary"] = "# {{title}} — Class summary\n\nDate: {{date}}\n\n## Main theme\n\n## Key concepts and definitions\n\n## Main arguments and conclusions\n\n## Important examples\n\n## Connections between topics\n\n## What to revisit\n\n## Assignments and next class\n\nSummarize the entire class, not only the opening or ending. Include timestamps for major concepts. Keep this concise enough for revision while preserving the lecturer's important distinctions. Use the class language unless requested otherwise. Do not invent missing details or exam topics.\n",
        ["study-guide"] = "# {{title}} — Study guide\n\nDate: {{date}}\n\n## Learning checklist\n\n## Concepts to explain in your own words\n\n## Review questions\n\nCreate questions from the actual class content. Clearly label these as generated study questions, not lecturer-provided or predicted exam questions.\n\n## Short answer key with transcript timestamps\n\n## Applications and worked examples from class\n\n## Gaps and questions for the lecturer\n\nUse the class language unless requested otherwise. Separate source facts from generated study prompts. Do not claim exam relevance unless the lecturer explicitly stated it.\n"
    };

    public NotesWorkspace(string recordingsFolder, string dataFolder)
    {
        RecordingsFolder = Path.GetFullPath(recordingsFolder);
        DataFolder = Path.GetFullPath(dataFolder);
        Directory.CreateDirectory(RecordingsFolder);
        Directory.CreateDirectory(DataFolder);
        RejectLinks(RecordingsFolder); RejectLinks(DataFolder);
        TemplatesFolder = SafeDirectory(Path.Combine(DataFolder, "templates"));
        JobsFolder = SafeDirectory(Path.Combine(DataFolder, "classes"));
        foreach (var (name, markdown) in BuiltInTemplates)
        {
            var path = Path.Combine(TemplatesFolder, name + ".md");
            if (!File.Exists(path)) WriteNew(path, markdown);
        }
    }

    public RecordingInfo[] ListRecordings(int offset = 0, int limit = 50)
    {
        if (offset < 0 || limit is < 1 or > 200) throw new ArgumentException("Use offset >= 0 and a limit from 1 to 200.");
        RejectLinks(RecordingsFolder);
        return new DirectoryInfo(RecordingsFolder).EnumerateFiles().Where(f => IsFinishedName(f.Name) && !f.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .OrderByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).Skip(offset).Take(limit)
            .Select(f => new RecordingInfo(f.Name, f.Length, f.LastWriteTimeUtc)).ToArray();
    }

    public string RecordingPath(string recordingId)
    {
        if (recordingId.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || !IsFinishedName(recordingId))
            throw new ArgumentException("Use the exact recording ID returned by list_recordings.");
        RejectLinks(RecordingsFolder);
        var path = Path.Combine(RecordingsFolder, recordingId);
        RejectLinks(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Recording not found in the configured recordings folder.");
        return path;
    }

    private static bool IsFinishedName(string name) => (name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase))
        && !name.EndsWith(".recording.mp4", StringComparison.OrdinalIgnoreCase)
        && !name.EndsWith(".recording.m4a", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith(".blinky.", StringComparison.OrdinalIgnoreCase);

    public string JobFolder(string id, bool create = false)
    {
        if (!Regex.IsMatch(id, "\\A[a-f0-9]{32}\\z")) throw new ArgumentException("Invalid job ID.");
        RejectLinks(JobsFolder);
        var path = Path.Combine(JobsFolder, id);
        RejectLinks(path);
        if (create) Directory.CreateDirectory(path);
        return path;
    }

    public string[] ListTemplates()
    {
        RejectLinks(TemplatesFolder);
        return Directory.EnumerateFiles(TemplatesFolder, "*.md").Where(p => !File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint))
            .Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal).ToArray();
    }

    public string GetTemplate(string name)
    {
        var path = TemplatePath(name);
        if (new FileInfo(path).Length > 128 * 1024) throw new ArgumentException("Templates must be at most 128 KiB.");
        return File.ReadAllText(path);
    }

    public string SaveTemplate(string name, string markdown, bool overwrite = false)
    {
        ValidateMarkdown(markdown, 128 * 1024);
        var path = TemplatePath(name);
        if (!overwrite) WriteNew(path, markdown);
        else
        {
            var temp = Path.Combine(TemplatesFolder, ".template-" + Guid.NewGuid().ToString("N") + ".tmp");
            try { WriteNew(temp, markdown); File.Move(temp, path, overwrite: true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return path;
    }

    private string TemplatePath(string name)
    {
        if (!Regex.IsMatch(name, "\\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}\\z")) throw new ArgumentException("Template names may contain letters, numbers, underscores and hyphens (1–64 characters).");
        RejectLinks(TemplatesFolder);
        var path = Path.Combine(TemplatesFolder, name + ".md");
        RejectLinks(path);
        return path;
    }

    public string Export(string recordingId, string suffix, string markdown)
    {
        var source = RecordingPath(recordingId);
        ValidateMarkdown(markdown, 16 * 1024 * 1024);
        if (!Regex.IsMatch(suffix, "\\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,100}\\z")) throw new ArgumentException("Invalid export suffix.");
        var stem = Path.GetFileNameWithoutExtension(source) + "." + suffix;
        // Commit a complete Markdown file atomically; never replace an existing export.
        var temporary = Path.Combine(RecordingsFolder, ".blinky.markdown-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            WriteNew(temporary, markdown);
            for (var n = 1; n <= 1000; n++)
            {
                var path = Path.Combine(RecordingsFolder, stem + (n == 1 ? "" : "-" + n) + ".md");
                try { File.Move(temporary, path, overwrite: false); return path; }
                catch (IOException) when (File.Exists(path)) { }
            }
            throw new IOException("Too many exports with that name.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static TextPage Page(string text, int offset, int maxCharacters)
    {
        if (offset < 0 || offset > text.Length || maxCharacters is < 1 or > 32000) throw new ArgumentException("Use a valid offset and 1–32000 characters per page.");
        var end = Math.Min(text.Length, offset + maxCharacters);
        if (end < text.Length && end > offset && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
        if (end == offset && offset < text.Length) end = Math.Min(text.Length, offset + 2);
        return new(text[offset..end], offset, end < text.Length ? end : null, text.Length);
    }

    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("Symbolic links and reparse points are not allowed in the MCP workspace.");
    }

    public static string SafeDirectory(string path) { RejectLinks(path); Directory.CreateDirectory(path); return path; }
    private static void ValidateMarkdown(string text, int maxBytes)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\0') || Encoding.UTF8.GetByteCount(text) > maxBytes) throw new ArgumentException("Markdown is empty, contains a null character, or exceeds the size limit.");
    }
    private static void WriteNew(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }
}
