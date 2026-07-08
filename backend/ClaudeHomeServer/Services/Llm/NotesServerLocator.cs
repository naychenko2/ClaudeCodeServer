namespace ClaudeHomeServer.Services.Llm;

// index.js встроенного MCP-сервера заметок: рядом с exe (prod) или в корне репо (dev).
public static class NotesServerLocator
{
    public static string? FindNotesServerPath()
    {
        var nearExe = Path.Combine(AppContext.BaseDirectory, "mcp", "notes-server", "index.js");
        if (File.Exists(nearExe)) return nearExe;
        var nearCwd = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(), "..", "..", "mcp", "notes-server", "index.js"));
        if (File.Exists(nearCwd)) return nearCwd;
        return null;
    }
}
