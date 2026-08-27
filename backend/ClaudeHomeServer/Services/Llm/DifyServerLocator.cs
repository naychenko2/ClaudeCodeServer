namespace ClaudeHomeServer.Services.Llm;

// dist/index.js MCP-сервера Dify (mcp-dify — TypeScript со сборкой tsc, единственный наш
// сервер с внешней зависимостью). Путь нужен ТОЛЬКО stdio-ветке отката (Mcp:HttpTransport):
// рядом с exe (прод), в дереве репо от cwd бэкенда (dev) или от корня репо (запуск из корня).
// dist не живёт в git (сборка mcp-dify/npm run build) — null означает «ветка отката недоступна»,
// и тогда dify продолжит ехать записью из внешнего базового конфига, как до волны 4.
public static class DifyServerLocator
{
    public static string? FindDifyServerPath()
    {
        var nearExe = Path.Combine(AppContext.BaseDirectory, "mcp-dify", "dist", "index.js");
        if (File.Exists(nearExe)) return nearExe;
        var cwd = Directory.GetCurrentDirectory();
        var fromBackend = Path.GetFullPath(Path.Combine(cwd, "..", "..", "mcp-dify", "dist", "index.js"));
        if (File.Exists(fromBackend)) return fromBackend;
        var fromRoot = Path.GetFullPath(Path.Combine(cwd, "mcp-dify", "dist", "index.js"));
        return File.Exists(fromRoot) ? fromRoot : null;
    }
}
