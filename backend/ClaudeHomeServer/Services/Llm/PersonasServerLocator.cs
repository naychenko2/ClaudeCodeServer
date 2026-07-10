namespace ClaudeHomeServer.Services.Llm;

// index.js встроенного MCP-сервера персон: рядом с exe (prod) или в корне репо (dev).
public static class PersonasServerLocator
{
    public static string? FindPersonasServerPath()
    {
        var nearExe = Path.Combine(AppContext.BaseDirectory, "mcp", "personas-server", "index.js");
        if (File.Exists(nearExe)) return nearExe;
        var nearCwd = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(), "..", "..", "mcp", "personas-server", "index.js"));
        if (File.Exists(nearCwd)) return nearCwd;
        return null;
    }
}
