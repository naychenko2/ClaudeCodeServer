namespace ClaudeHomeServer.Services.Execution;

// Маппер путей хост ↔ sandbox-контейнер. Знает фиксированный набор правил
// (корень проектов песочницы, профили, temp, mcp-серверы образа) — путь вне
// правил в песочнице не существует, ToRuntime бросает исключение (аналог SafeJoin).
public sealed class DockerPathMapper : IPathMapper
{
    private readonly List<(string Host, string Runtime)> _rules = [];

    public DockerPathMapper(SandboxManager sandbox)
    {
        Add(sandbox.Options.ProjectsRoot, SandboxManager.ProjectsMount);
        Add(sandbox.ProfilesHostDir, SandboxManager.ProfilesMount);
        Add(sandbox.TmpHostDir, SandboxManager.TmpMount);
        // MCP-серверы: в образе песочницы лежат тем же деревом под /app
        Add(Path.Combine(AppContext.BaseDirectory, "mcp"), sandbox.Options.McpRoot);
        Add(Path.Combine(AppContext.BaseDirectory, "mcp-dify"), "/app/mcp-dify");
        // Дев-запуск бэкенда из bin/: локаторы отдают mcp/ из корня репозитория
        var repoMcp = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "mcp"));
        Add(repoMcp, sandbox.Options.McpRoot);
        var repoMcpDify = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "mcp-dify"));
        Add(repoMcpDify, "/app/mcp-dify");
    }

    private void Add(string host, string runtime)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        _rules.Add((Path.GetFullPath(host).TrimEnd('\\', '/'), runtime.TrimEnd('/')));
    }

    // Префикс-совпадение по ГРАНИЦЕ сегмента: правило "C:\SandboxProjects" не должно
    // матчить "C:\SandboxProjectsBackup". Правила хранятся без хвостового разделителя,
    // поэтому за префиксом обязан идти разделитель либо конец строки.
    private static bool IsUnderPrefix(string path, string prefix, StringComparison cmp)
    {
        if (!path.StartsWith(prefix, cmp)) return false;
        if (path.Length == prefix.Length) return true;
        var next = path[prefix.Length];
        return next is '/' or '\\';
    }

    public string ToRuntime(string hostPath)
    {
        var full = Path.GetFullPath(hostPath);
        foreach (var (host, runtime) in _rules)
        {
            if (!IsUnderPrefix(full, host, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = full[host.Length..].TrimStart('\\', '/');
            var mapped = rest.Length == 0 ? runtime : runtime + "/" + rest.Replace('\\', '/');
            return mapped;
        }
        throw new InvalidOperationException($"Путь недоступен в песочнице: {hostPath}");
    }

    public string ToHost(string runtimePath)
    {
        var norm = runtimePath.Replace('\\', '/');
        foreach (var (host, runtime) in _rules)
        {
            if (!IsUnderPrefix(norm, runtime, StringComparison.Ordinal)) continue;
            var rest = norm[runtime.Length..].TrimStart('/');
            return rest.Length == 0 ? host : Path.Combine(host, rest.Replace('/', Path.DirectorySeparatorChar));
        }
        throw new InvalidOperationException($"Путь песочницы вне известных точек монтирования: {runtimePath}");
    }

    // Есть ли правило для пути (для необязательных путей: --add-dir, user-scope серверы)
    public bool CanMap(string hostPath)
    {
        var full = Path.GetFullPath(hostPath);
        return _rules.Any(r => IsUnderPrefix(full, r.Host, StringComparison.OrdinalIgnoreCase));
    }
}
