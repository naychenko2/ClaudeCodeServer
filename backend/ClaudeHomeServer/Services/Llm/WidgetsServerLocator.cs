namespace ClaudeHomeServer.Services.Llm;

// index.js встроенного MCP-сервера виджетов: рядом с exe (prod) или в корне репо (dev).
public static class WidgetsServerLocator
{
    public static string? FindWidgetsServerPath()
    {
        var nearExe = Path.Combine(AppContext.BaseDirectory, "mcp", "widgets-server", "index.js");
        if (File.Exists(nearExe)) return nearExe;
        var nearCwd = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(), "..", "..", "mcp", "widgets-server", "index.js"));
        if (File.Exists(nearCwd)) return nearCwd;
        return null;
    }
}
