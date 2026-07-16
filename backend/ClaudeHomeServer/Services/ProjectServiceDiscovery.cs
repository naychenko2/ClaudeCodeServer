using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Один запускаемый сервис проекта (инференс из манифеста или из <c>.claude/launch.json</c>).
/// Единый DTO, который отдаётся фронту (обогащается runtime-статусом в контроллере).
/// </summary>
public record ProjectServiceInfo(
    string Id,
    string Name,
    string Source,          // launch.json | npm | dotnet | docker-compose | procfile | makefile | custom
    string Command,
    string[] Args,
    string? Cwd,            // относительный путь от RootPath (null = корень)
    int? SuggestedPort,
    bool AutoPort,
    bool Saved,             // из .claude/launch.json (можно редактировать/удалять)
    Dictionary<string, string>? Env = null
);

/// <summary>
/// Определяет, какие сервисы можно запустить в проекте: парсит манифесты
/// (package.json, launchSettings.json, docker-compose, Procfile/Makefile) и объединяет
/// с сохранёнными конфигурациями из <c>.claude/launch.json</c>. Скан слушающих портов не делается.
/// </summary>
public sealed class ProjectServiceDiscovery
{
    private readonly LaunchConfigService _launch;
    private readonly ILogger<ProjectServiceDiscovery> _log;

    // Короткий кэш, чтобы частые опросы фронта не били по ФС (образец — NotesService).
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);
    private readonly ConcurrentDictionary<string, (DateTime At, List<ProjectServiceInfo> Items)> _cache = new();

    public ProjectServiceDiscovery(LaunchConfigService launch, ILogger<ProjectServiceDiscovery> log)
    {
        _launch = launch;
        _log = log;
    }

    public async Task<List<ProjectServiceInfo>> DiscoverAsync(Project project)
    {
        if (_cache.TryGetValue(project.Id, out var c) && DateTime.UtcNow - c.At < CacheTtl)
            return c.Items;

        var root = project.RootPath;
        var result = new List<ProjectServiceInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) Сохранённые из launch.json — приоритет.
        foreach (var saved in await ReadSavedAsync(project))
        {
            if (seen.Add(Signature(saved))) result.Add(saved);
        }

        // 2) Инференс из манифестов; дубли (по сигнатуре) отбрасываем в пользу saved.
        if (Directory.Exists(root))
        {
            foreach (var svc in SafeParse(() => ParseNode(root), "package.json")
                .Concat(SafeParse(() => ParseDotnet(root), "launchSettings.json"))
                .Concat(SafeParse(() => ParseCompose(root), "docker-compose"))
                .Concat(SafeParse(() => ParseProcfile(root), "Procfile"))
                .Concat(SafeParse(() => ParseMakefile(root), "Makefile")))
            {
                if (seen.Add(Signature(svc))) result.Add(svc);
            }
        }

        _cache[project.Id] = (DateTime.UtcNow, result);
        return result;
    }

    /// <summary>Сбросить кэш проекта (после записи launch.json).</summary>
    public void Invalidate(string projectId) => _cache.TryRemove(projectId, out _);

    private async Task<List<ProjectServiceInfo>> ReadSavedAsync(Project project)
    {
        var entries = await _launch.ReadAsync(project);
        var list = new List<ProjectServiceInfo>();
        foreach (var e in entries)
        {
            var command = e.RuntimeExecutable ?? (e.Program != null ? "node" : null);
            if (string.IsNullOrWhiteSpace(command)) continue;
            var args = e.Program != null
                ? new[] { e.Program }.Concat(e.Args ?? []).ToArray()
                : (e.RuntimeArgs ?? []);
            var name = string.IsNullOrWhiteSpace(e.Name) ? command : e.Name!;
            list.Add(new ProjectServiceInfo(
                Id: Slug($"launch-{name}-{e.Cwd}"),
                Name: name,
                Source: "launch.json",
                Command: command,
                Args: args,
                Cwd: NormalizeCwd(e.Cwd),
                SuggestedPort: e.Port,
                AutoPort: e.AutoPort ?? false,
                Saved: true,
                Env: e.Env));
        }
        return list;
    }

    private List<ProjectServiceInfo> SafeParse(Func<List<ProjectServiceInfo>> parse, string label)
    {
        try { return parse(); }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Парсер {Label} упал", label);
            return [];
        }
    }

    // ── package.json scripts ──────────────────────────────────────────────
    private List<ProjectServiceInfo> ParseNode(string root)
    {
        var list = new List<ProjectServiceInfo>();
        foreach (var pkgPath in FindFiles(root, n => n.Equals("package.json", StringComparison.OrdinalIgnoreCase), maxDepth: 2))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(pkgPath));
            if (!doc.RootElement.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object)
                continue;

            var pkgDir = Path.GetDirectoryName(pkgPath)!;
            var cwd = RelCwd(root, pkgDir);
            var mgr = DetectPackageManager(pkgDir, root);

            foreach (var s in scripts.EnumerateObject())
            {
                if (!IsServerScript(s.Name)) continue;
                var args = mgr == "npm" ? new[] { "run", s.Name } : new[] { s.Name };
                list.Add(new ProjectServiceInfo(
                    Id: Slug($"npm-{cwd}-{s.Name}"),
                    Name: cwd is null ? s.Name : $"{cwd}: {s.Name}",
                    Source: "npm",
                    Command: mgr,
                    Args: args,
                    Cwd: cwd,
                    SuggestedPort: null,   // Vite/webpack печатают URL в вывод — ловим при старте
                    AutoPort: false,
                    Saved: false));
            }
        }
        return list;
    }

    private static string DetectPackageManager(string dir, string root)
    {
        bool Has(string file) =>
            File.Exists(Path.Combine(dir, file)) || File.Exists(Path.Combine(root, file));
        if (Has("pnpm-lock.yaml")) return "pnpm";
        if (Has("yarn.lock")) return "yarn";
        return "npm";
    }

    private static bool IsServerScript(string name)
    {
        var l = name.ToLowerInvariant();
        if (l.StartsWith("pre") || l.StartsWith("post")) return false; // npm-lifecycle хуки
        string[] exact = ["dev", "start", "serve", "preview", "watch"];
        if (exact.Contains(l)) return true;
        return l.StartsWith("dev:") || l.StartsWith("start:") || l.StartsWith("serve:");
    }

    // ── ASP.NET Core launchSettings.json ─────────────────────────────────
    private List<ProjectServiceInfo> ParseDotnet(string root)
    {
        var list = new List<ProjectServiceInfo>();
        foreach (var lsPath in FindFiles(root, n => n.Equals("launchSettings.json", StringComparison.OrdinalIgnoreCase), maxDepth: 4))
        {
            var propsDir = Path.GetDirectoryName(lsPath)!;
            if (!Path.GetFileName(propsDir).Equals("Properties", StringComparison.OrdinalIgnoreCase))
                continue;
            var projDir = Path.GetDirectoryName(propsDir)!;
            var csproj = Directory.GetFiles(projDir, "*.csproj").FirstOrDefault();
            var projRef = csproj != null ? RelCwd(root, csproj) ?? Path.GetFileName(csproj) : RelCwd(root, projDir) ?? ".";
            var projName = Path.GetFileName(projDir);

            using var doc = JsonDocument.Parse(File.ReadAllText(lsPath));
            if (!doc.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var p in profiles.EnumerateObject())
            {
                var prof = p.Value;
                if (!prof.TryGetProperty("commandName", out var cn) || cn.GetString() != "Project")
                    continue;
                int? port = null;
                if (prof.TryGetProperty("applicationUrl", out var appUrl) && appUrl.ValueKind == JsonValueKind.String)
                    port = FirstHttpPort(appUrl.GetString());

                list.Add(new ProjectServiceInfo(
                    Id: Slug($"dotnet-{projRef}-{p.Name}"),
                    Name: $"{projName} ({p.Name})",
                    Source: "dotnet",
                    Command: "dotnet",
                    Args: ["run", "--project", projRef, "--launch-profile", p.Name],
                    Cwd: null,
                    SuggestedPort: port,
                    AutoPort: false,
                    Saved: false));
            }
        }
        return list;
    }

    // ── docker-compose ────────────────────────────────────────────────────
    private List<ProjectServiceInfo> ParseCompose(string root)
    {
        var list = new List<ProjectServiceInfo>();
        string[] names = ["docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml"];
        foreach (var file in names)
        {
            var path = Path.Combine(root, file);
            if (!File.Exists(path)) continue;
            foreach (var (svc, port) in ParseComposeServices(File.ReadAllLines(path)))
            {
                list.Add(new ProjectServiceInfo(
                    Id: Slug($"compose-{file}-{svc}"),
                    Name: $"{svc} (compose)",
                    Source: "docker-compose",
                    Command: "docker",
                    Args: ["compose", "-f", file, "up", svc],
                    Cwd: null,
                    SuggestedPort: port,
                    AutoPort: false,
                    Saved: false));
            }
            break; // один compose-файл на проект достаточно
        }
        return list;
    }

    /// <summary>Минимальный indentation-парсер: имена сервисов + первый хостовый порт. Best-effort.</summary>
    private static IEnumerable<(string Service, int? Port)> ParseComposeServices(string[] lines)
    {
        int servicesIndent = -1;
        int serviceNameIndent = -1;
        string? current = null;
        int? currentPort = null;
        bool inPorts = false;
        var results = new List<(string, int?)>();

        void Flush()
        {
            if (current != null) results.Add((current, currentPort));
            current = null; currentPort = null; inPorts = false;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith('#')) continue;
            int indent = line.Length - line.TrimStart().Length;
            var trimmed = line.TrimStart();

            if (servicesIndent < 0)
            {
                if (Regex.IsMatch(trimmed, @"^services:\s*$")) servicesIndent = indent;
                continue;
            }

            // Вышли из блока services (индент вернулся к корневому уровню).
            if (indent <= servicesIndent)
            {
                Flush();
                if (!Regex.IsMatch(trimmed, @"^services:\s*$")) break;
                continue;
            }

            if (serviceNameIndent < 0) serviceNameIndent = indent;

            if (indent == serviceNameIndent)
            {
                // Новое имя сервиса.
                Flush();
                var m = Regex.Match(trimmed, @"^([A-Za-z0-9._-]+):\s*$");
                if (m.Success) current = m.Groups[1].Value;
                continue;
            }

            if (current == null) continue;

            if (Regex.IsMatch(trimmed, @"^ports:\s*$")) { inPorts = true; continue; }
            // Другой ключ на уровне свойств сервиса завершает блок ports.
            if (indent <= serviceNameIndent + 2 && !trimmed.StartsWith('-')) inPorts = false;

            if (inPorts && currentPort == null && trimmed.StartsWith('-'))
            {
                var val = trimmed.TrimStart('-', ' ', '"', '\'').TrimEnd('"', '\'');
                currentPort = ComposeHostPort(val);
            }
        }
        Flush();
        return results;
    }

    private static int? ComposeHostPort(string mapping)
    {
        // Формы: "8080:80", "127.0.0.1:8080:80", "3000", "8080:80/tcp", "8080-8090:80"
        var m = mapping.Split('/')[0].Trim().Trim('"', '\'');
        if (m.Length == 0) return null;
        var parts = m.Split(':');
        string hostPart = parts.Length switch
        {
            1 => parts[0],
            2 => parts[0],
            _ => parts[^2],
        };
        hostPart = hostPart.Split('-')[0]; // диапазон 8080-8090 → 8080
        return int.TryParse(hostPart, out var p) ? p : null;
    }

    // ── Procfile ──────────────────────────────────────────────────────────
    private List<ProjectServiceInfo> ParseProcfile(string root)
    {
        var list = new List<ProjectServiceInfo>();
        var path = Path.Combine(root, "Procfile");
        if (!File.Exists(path)) return list;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var m = Regex.Match(line, @"^([A-Za-z0-9_-]+):\s*(.+)$");
            if (!m.Success) continue;
            var name = m.Groups[1].Value;
            var cmd = m.Groups[2].Value.Trim();
            var tokens = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;
            list.Add(new ProjectServiceInfo(
                Id: Slug($"procfile-{name}"),
                Name: $"{name} (Procfile)",
                Source: "procfile",
                Command: tokens[0],
                Args: tokens.Skip(1).ToArray(),
                Cwd: null,
                SuggestedPort: null,
                AutoPort: false,
                Saved: false));
        }
        return list;
    }

    // ── Makefile ──────────────────────────────────────────────────────────
    private List<ProjectServiceInfo> ParseMakefile(string root)
    {
        var list = new List<ProjectServiceInfo>();
        string[] names = ["Makefile", "makefile", "GNUmakefile"];
        var path = names.Select(n => Path.Combine(root, n)).FirstOrDefault(File.Exists);
        if (path == null) return list;

        foreach (var raw in File.ReadAllLines(path))
        {
            if (raw.Length == 0 || raw[0] == '\t' || raw[0] == '#' || raw.StartsWith('.')) continue;
            var m = Regex.Match(raw, @"^([A-Za-z0-9_-]+)\s*:(?!=)");
            if (!m.Success) continue;
            var target = m.Groups[1].Value;
            if (!IsServerTarget(target)) continue;
            list.Add(new ProjectServiceInfo(
                Id: Slug($"make-{target}"),
                Name: $"make {target}",
                Source: "makefile",
                Command: "make",
                Args: [target],
                Cwd: null,
                SuggestedPort: null,
                AutoPort: false,
                Saved: false));
        }
        return list;
    }

    private static bool IsServerTarget(string name)
    {
        var l = name.ToLowerInvariant();
        string[] hints = ["run", "dev", "serve", "start", "up", "watch", "server"];
        return hints.Any(h => l == h || l.StartsWith(h + "-") || l.StartsWith(h + "_"));
    }

    // ── helpers ───────────────────────────────────────────────────────────

    /// <summary>Bounded-обход: файлы по имени, пропуская тяжёлые и скрытые папки.</summary>
    private static List<string> FindFiles(string root, Func<string, bool> nameMatch, int maxDepth)
    {
        var results = new List<string>();
        void Walk(string dir, int depth)
        {
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { return; }
            foreach (var f in files)
                if (nameMatch(Path.GetFileName(f))) results.Add(f);

            if (depth >= maxDepth) return;
            string[] dirs;
            try { dirs = Directory.GetDirectories(dir); }
            catch { return; }
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith('.')) continue;                 // .git, .vs, .claude…
                if (FileService.TreeExcludes.Contains(name)) continue; // node_modules, bin, obj…
                Walk(d, depth + 1);
            }
        }
        Walk(root, 0);
        return results;
    }

    private static string? RelCwd(string root, string path)
    {
        var full = Path.GetFullPath(path);
        var rootFull = Path.GetFullPath(root);
        if (string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase)) return null;
        var rel = Path.GetRelativePath(rootFull, full).Replace('\\', '/');
        return string.IsNullOrEmpty(rel) || rel == "." ? null : rel;
    }

    private static string? NormalizeCwd(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        var c = cwd.Replace('\\', '/').Trim('/');
        return c.Length == 0 || c == "." ? null : c;
    }

    private static int? FirstHttpPort(string? applicationUrl)
    {
        if (string.IsNullOrWhiteSpace(applicationUrl)) return null;
        // Предпочитаем http (без TLS проще проксировать), иначе https.
        var matches = Regex.Matches(applicationUrl, @"(https?)://[^:/;]+:(\d+)");
        int? https = null;
        foreach (Match mm in matches)
        {
            var port = int.Parse(mm.Groups[2].Value);
            if (mm.Groups[1].Value == "http") return port;
            https ??= port;
        }
        return https;
    }

    private static string Signature(ProjectServiceInfo s) =>
        $"{s.Command} {string.Join(' ', s.Args)}@{s.Cwd ?? ""}";

    private static string Slug(string s)
    {
        var lower = s.ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lower.Length);
        bool prevDash = false;
        foreach (var ch in lower)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(ch);
                prevDash = false;
            }
            else if (!prevDash)
            {
                sb.Append('-');
                prevDash = true;
            }
        }
        var res = sb.ToString().Trim('-');
        return res.Length == 0 ? "svc" : res;
    }
}
