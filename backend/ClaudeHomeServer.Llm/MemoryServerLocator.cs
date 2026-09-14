namespace ClaudeHomeServer.Services.Llm;

// index.js встроенного MCP-сервера долгой памяти персон: рядом с exe (prod) или в корне репо (dev).
public static class MemoryServerLocator
{
    public static string? FindMemoryServerPath()
    {
        var nearExe = Path.Combine(AppContext.BaseDirectory, "mcp", "memory-server", "index.js");
        if (File.Exists(nearExe)) return nearExe;
        var nearCwd = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(), "..", "..", "mcp", "memory-server", "index.js"));
        if (File.Exists(nearCwd)) return nearCwd;
        return null;
    }
}
