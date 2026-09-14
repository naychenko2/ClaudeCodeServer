namespace ClaudeHomeServer.Services.Llm;

// index.js встроенного MCP-сервера задач: рядом с exe (prod) или в корне репо (dev).
// Общий для ClaudeSession (mcp-config) и DeepSeek (прямое подключение stdio-клиентом).
public static class TasksServerLocator
{
    public static string? FindTasksServerPath()
    {
        var nearExe = Path.Combine(AppContext.BaseDirectory, "mcp", "tasks-server", "index.js");
        if (File.Exists(nearExe)) return nearExe;
        var nearCwd = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(), "..", "..", "mcp", "tasks-server", "index.js"));
        if (File.Exists(nearCwd)) return nearCwd;
        return null;
    }
}
