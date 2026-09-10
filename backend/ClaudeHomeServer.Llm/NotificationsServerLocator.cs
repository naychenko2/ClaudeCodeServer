namespace ClaudeHomeServer.Services.Llm;

// index.js встроенного MCP-сервера уведомлений: рядом с exe (prod) или в корне репо (dev).
public static class NotificationsServerLocator
{
    public static string? FindNotificationsServerPath()
    {
        var nearExe = Path.Combine(AppContext.BaseDirectory, "mcp", "notifications-server", "index.js");
        if (File.Exists(nearExe)) return nearExe;
        var nearCwd = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(), "..", "..", "mcp", "notifications-server", "index.js"));
        if (File.Exists(nearCwd)) return nearCwd;
        return null;
    }
}
