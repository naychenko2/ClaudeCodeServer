namespace ClaudeHomeServer.Services.Llm;

// index.js встроенного MCP-сервера графа кода: рядом с exe (prod) или в корне репо (dev).
public static class CodeGraphServerLocator
{
    public static string? FindCodeGraphServerPath()
    {
        var nearExe = Path.Combine(AppContext.BaseDirectory, "mcp", "codegraph-server", "index.js");
        if (File.Exists(nearExe)) return nearExe;
        var nearCwd = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(), "..", "..", "mcp", "codegraph-server", "index.js"));
        if (File.Exists(nearCwd)) return nearCwd;
        return null;
    }
}
