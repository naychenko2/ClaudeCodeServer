namespace ClaudeHomeServer.Services.Llm;

// index.js встроенного MCP-сервера рабочего пространства: рядом с exe (prod) или в корне репо (dev).
public static class WorkspaceServerLocator
{
    public static string? FindWorkspaceServerPath()
    {
        var nearExe = Path.Combine(AppContext.BaseDirectory, "mcp", "workspace-server", "index.js");
        if (File.Exists(nearExe)) return nearExe;
        var nearCwd = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(), "..", "..", "mcp", "workspace-server", "index.js"));
        if (File.Exists(nearCwd)) return nearCwd;
        return null;
    }
}
