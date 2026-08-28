using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Один запускаемый сервис проекта (инференс из манифеста или из <c>.claude/launch.json</c>).
/// Единый DTO, который отдаётся фронту (обогащается runtime-статусом в контроллере).
/// </summary>
public record ProjectServiceInfo(
    string Id,
    string Name,
    string Source,          // launch.json | rider | npm | dotnet | docker-compose | procfile | makefile | custom
    string Command,
    string[] Args,
    string? Cwd,            // относительный путь от RootPath (null = корень)
    int? SuggestedPort,
    bool AutoPort,
    bool Saved,             // из .claude/launch.json (можно редактировать/удалять)
    Dictionary<string, string>? Env = null,
    // Составной запуск (multilaunch у Rider): id входящих сервисов. У группы своей
    // команды нет — она запускает участников, каждый остаётся отдельным процессом.
    string[]? Members = null
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
        // Занятые Id — отдельно от сигнатур. Дедупликация схлопывает ОДИН И ТОТ ЖЕ запуск,
        // а здесь ловится обратное: разные запуски, которым Slug выдал одинаковый Id.
        // Слаг оставляет только латиницу и цифры, поэтому «Витрина: панель хостов» и
        // «Витрина: панель сессий» без латинского хвоста в имени — это один Id на двоих.
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) Сохранённые из launch.json — приоритет.
        foreach (var saved in await ReadSavedAsync(project))
        {
            AddUnique(saved);
        }

        // 2) Инференс из манифестов; дубли (по сигнатуре) отбрасываем в пользу saved.
        // Конфигурации Rider идут первыми среди инференса: это явное намерение человека
        // с осмысленным именем («Backend»), а разбор манифестов — догадка («dev»).
        if (Directory.Exists(root))
        {
            foreach (var svc in SafeParse(() => ParseRider(root), "конфигурации Rider")
                .Concat(SafeParse(() => ParseNode(root), "package.json"))
                .Concat(SafeParse(() => ParseDotnet(root), "launchSettings.json"))
                .Concat(SafeParse(() => ParseCompose(root), "docker-compose"))
                .Concat(SafeParse(() => ParseProcfile(root), "Procfile"))
                .Concat(SafeParse(() => ParseMakefile(root), "Makefile")))
            {
                AddUnique(svc);
            }
        }

        _cache[project.Id] = (DateTime.UtcNow, result);
        return result;

        // Добавить сервис, сохранив ДВА инварианта списка: одна сигнатура — одна запись,
        // один Id — одна запись. Второй появился не для красоты: Id это ключ сервиса во
        // всём API (запуск, остановка, реестр запущенных, превью), и дубль означал не
        // просто спорный список, а 500 на панели «Сервисы» — ToDictionary по Id падал
        // с ArgumentException, унося весь запрос.
        //
        // Коллизию не отбрасываем: за одинаковым слагом стоят РАЗНЫЕ запуски, и потеря
        // одного из них выглядела бы как пропавшая кнопка. Вместо этого добавляем суффикс
        // от сигнатуры — он стабилен между вызовами, пока не изменилась сама команда,
        // поэтому запущенный процесс не осиротеет (его ключ — Id).
        //
        // Известное ограничение: участник составной конфигурации, которому сменили Id,
        // выпадет из своей группы (Members ссылаются на исходные Id). Случай редкий —
        // группы Rider перечисляют участников по именам конфигураций, а коллизия слага
        // означает, что имена и так неразличимы.
        void AddUnique(ProjectServiceInfo svc)
        {
            var signature = Signature(svc);
            if (!seen.Add(signature)) return;

            if (!ids.Add(svc.Id))
            {
                var unique = $"{svc.Id}-{ShortHash(signature)}";
                _log.LogWarning(
                    "Сервисы проекта {Project}: идентификатор {Id} занят другим запуском, "
                    + "выдан {NewId} (имена конфигураций различаются только вне латиницы)",
                    project.Id, svc.Id, unique);
                ids.Add(unique);
                svc = svc with { Id = unique };
            }

            result.Add(svc);
        }
    }

    /// <summary>
    /// Порт, на котором сервис слушает по конфигурации. У составной конфигурации своего порта
    /// нет — берём первого участника, которому есть что показать (тем же правилом, что
    /// PreviewController.GroupDto выбирает порт группе).
    ///
    /// Единственная точка правила: раньше оно было переписано в двух местах контроллера, и
    /// третья копия в маршруте внешнего доступа означала бы, что «показать снаружи» и
    /// «показать в панели» однажды начнут указывать на разные порты.
    /// </summary>
    public async Task<int?> ResolvePortAsync(Project project, string serviceId)
    {
        var known = await DiscoverAsync(project);
        var svc = known.FirstOrDefault(s => s.Id == serviceId);
        if (svc is null) return null;
        if (svc.Members is not { Length: > 0 }) return svc.SuggestedPort is > 0 ? svc.SuggestedPort : null;

        var byId = known.ToDictionary(s => s.Id);
        // Последний участник, а не первый: в составном запуске сначала идут зависимости,
        // а приложение, ради которого всё затевалось, ждёт их и стоит в конце
        return svc.Members
            .Reverse()
            .Select(id => byId.TryGetValue(id, out var m) ? m.SuggestedPort : null)
            .FirstOrDefault(p => p is > 0);
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

    // ── Конфигурации запуска Rider ────────────────────────────────────────
    //
    // Два места: `.run/*.run.xml` (лежит рядом с solution — у нас это `backend/.run`)
    // и `.idea/**/runConfigurations/*.xml` (у Rider путь бывает вложенным:
    // `.idea/.idea.<Solution>/.idea/runConfigurations`). Обе папки начинаются с точки,
    // поэтому общий обход FindFiles их не видит — здесь свой, знающий про них.
    //
    // Поддерживаем три типа. Остальные пропускаем осознанно: `multilaunch` — это
    // несколько процессов разом, а у нас «один сервис — один процесс»; скриптовые
    // (ShConfigurationType, PowerShellRunType) неотличимы от несерверных.
    private List<ProjectServiceInfo> ParseRider(string root)
    {
        var list = new List<ProjectServiceInfo>();
        // Составные разбираем вторым проходом: их ссылки указывают на конфигурации из
        // ДРУГИХ файлов, поэтому резолвить их можно только когда собраны все простые.
        var compound = new List<(string Name, List<string> Refs)>();

        foreach (var (file, projectDir) in FindRiderConfigs(root))
        {
            XDocument doc;
            try { doc = XDocument.Load(file); }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Не разобрана конфигурация Rider {File}", file);
                continue;
            }

            foreach (var cfg in doc.Descendants("configuration"))
            {
                var name = cfg.Attribute("name")?.Value;
                var type = cfg.Attribute("type")?.Value;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type)) continue;

                if (type == "com.intellij.execution.configurations.multilaunch")
                {
                    var refs = cfg.Descendants("ExecutableSnapshot")
                        .Select(e => e.Elements("option").FirstOrDefault(o => o.Attribute("name")?.Value == "id")?
                            .Attribute("value")?.Value)
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Select(v => v!)
                        .ToList();
                    if (refs.Count > 0) compound.Add((name, refs));
                    continue;
                }

                var svc = type switch
                {
                    "LaunchSettings" => RiderLaunchSettings(root, projectDir, cfg, name),
                    "js.build_tools.npm" => RiderNpm(root, projectDir, cfg, name),
                    "docker-deploy" => RiderCompose(root, projectDir, cfg, name),
                    "NodeJSConfigurationType" => RiderNodeJs(root, projectDir, cfg, name),
                    // Скриптовые типы (ShConfigurationType, PowerShellRunType) пропускаем:
                    // отличить сервер от разовой утилиты в них нечем
                    _ => null,
                };
                if (svc != null) list.Add(svc);
            }
        }

        foreach (var (name, refs) in compound)
        {
            var members = refs.Select(r => ResolveRiderRef(r, list)).Where(m => m != null).Select(m => m!.Id).ToArray();
            // Группа без единого разрешённого участника бесполезна: скорее всего она
            // состоит из типов, которые мы не поддерживаем
            if (members.Length == 0) continue;
            list.Add(new ProjectServiceInfo(
                Id: Slug($"rider-group-{name}"),
                Name: name,
                Source: "rider",
                Command: "",
                Args: [],
                Cwd: null,
                SuggestedPort: null,
                AutoPort: false,
                Saved: false,
                Members: members));
        }

        return list;
    }

    /// <summary>
    /// Ссылка вида `runConfig:{фабрика}.{имя}` — например `runConfig:npm.Frontend` или
    /// `runConfig:.NET Launch Settings Profile.Backend (Telemetry Prod)`. Имя фабрики само
    /// содержит точки, поэтому сопоставляем по суффиксу и берём самое длинное совпадение:
    /// иначе «Backend» перехватил бы ссылку на «Telemetry Backend».
    /// </summary>
    private static ProjectServiceInfo? ResolveRiderRef(string reference, List<ProjectServiceInfo> known)
    {
        var id = reference.StartsWith("runConfig:", StringComparison.Ordinal)
            ? reference["runConfig:".Length..]
            : reference;

        return known
            .Where(s => s.Source == "rider" &&
                        id.EndsWith("." + s.Name, StringComparison.Ordinal))
            .OrderByDescending(s => s.Name.Length)
            .FirstOrDefault();
    }

    /// <summary>Файлы конфигураций вместе с их $PROJECT_DIR$ (папкой, где лежит .run/.idea).</summary>
    private static List<(string File, string ProjectDir)> FindRiderConfigs(string root)
    {
        var results = new List<(string, string)>();

        void Collect(string dir)
        {
            var run = Path.Combine(dir, ".run");
            if (Directory.Exists(run))
                foreach (var f in SafeGetFiles(run, "*.run.xml")) results.Add((f, dir));

            var idea = Path.Combine(dir, ".idea");
            if (!Directory.Exists(idea)) return;
            // Папка runConfigurations лежит на неизвестной глубине внутри .idea
            try
            {
                foreach (var rc in Directory.GetDirectories(idea, "runConfigurations", SearchOption.AllDirectories))
                    foreach (var f in SafeGetFiles(rc, "*.xml")) results.Add((f, dir));
            }
            catch { /* нет прав/битые ссылки — конфигураций просто нет */ }
        }

        void Walk(string dir, int depth)
        {
            Collect(dir);
            if (depth >= 2) return;   // solution обычно в корне или на уровень ниже
            string[] dirs;
            try { dirs = Directory.GetDirectories(dir); }
            catch { return; }
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith('.')) continue;
                if (FileService.TreeExcludes.Contains(name)) continue;
                Walk(d, depth + 1);
            }
        }

        Walk(root, 0);
        return results;
    }

    private static string[] SafeGetFiles(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); }
        catch { return []; }
    }

    private static string? RiderOption(XElement cfg, string name) =>
        cfg.Elements("option").FirstOrDefault(o => o.Attribute("name")?.Value == name)?.Attribute("value")?.Value;

    /// <summary>
    /// Абсолютный путь из значения конфигурации Rider. `$PROJECT_DIR$` подставляем, дальше
    /// проверяем SafeJoin: конфигурации сплошь ссылаются наружу (`$PROJECT_DIR$/../frontend`,
    /// интерпретатор в System32), а запускать мы имеем право только внутри проекта.
    /// Ссылка за пределы корня → null, конфигурация пропускается.
    /// </summary>
    private static string? ResolveRiderPath(string root, string projectDir, string? value)
    {
        var raw = value?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        raw = raw.Replace("$PROJECT_DIR$", projectDir);
        try
        {
            var rootFull = Path.GetFullPath(root);
            var full = Path.IsPathRooted(raw) ? Path.GetFullPath(raw) : Path.GetFullPath(Path.Combine(projectDir, raw));
            // SafeJoin принимает путь ОТНОСИТЕЛЬНО корня и срезает ведущие разделители.
            // Абсолютный путь ему давать нельзя: на Linux «/tmp/p/App.csproj» превращается
            // в относительный «tmp/p/App.csproj» и приклеивается к корню — вместо отказа
            // выходит путь-химера ВНУТРИ проекта, то есть заодно молча теряется проверка
            // «ссылка наружу». На Windows этого не видно (Path.Combine отдаёт приоритет
            // второму абсолютному пути), поэтому ловилось только в CI на ubuntu.
            return FileService.SafeJoinPublic(root, Path.GetRelativePath(rootFull, full));
        }
        catch (UnauthorizedAccessException) { return null; }   // за пределами проекта
        catch (ArgumentException) { return null; }             // недопустимые символы в пути
        catch (NotSupportedException) { return null; }
    }

    // Профиль launchSettings.json: `dotnet run --project X --launch-profile Y`.
    // Ровно то же, что собирает ParseDotnet, — вид аргументов обязан совпадать,
    // иначе дедуп по сигнатуре не сработает и в списке будет два одинаковых запуска.
    private ProjectServiceInfo? RiderLaunchSettings(string root, string projectDir, XElement cfg, string name)
    {
        var csproj = ResolveRiderPath(root, projectDir, RiderOption(cfg, "LAUNCH_PROFILE_PROJECT_FILE_PATH"));
        if (csproj is null) return null;

        var profile = RiderOption(cfg, "LAUNCH_PROFILE_NAME");
        var projRef = RelCwd(root, csproj) ?? Path.GetFileName(csproj);
        string[] args = string.IsNullOrWhiteSpace(profile)
            ? ["run", "--project", projRef]
            : ["run", "--project", projRef, "--launch-profile", profile];

        return new ProjectServiceInfo(
            Id: Slug($"rider-{name}"),
            Name: name,
            Source: "rider",
            Command: "dotnet",
            Args: args,
            Cwd: null,
            SuggestedPort: string.IsNullOrWhiteSpace(profile) ? null : LaunchProfilePort(csproj, profile),
            AutoPort: false,
            Saved: false);
    }

    /// <summary>Порт профиля из launchSettings.json рядом с csproj (http предпочтительнее https).</summary>
    private static int? LaunchProfilePort(string csprojPath, string profileName)
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(csprojPath)!, "Properties", "launchSettings.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("profiles", out var profiles)) return null;
            if (!profiles.TryGetProperty(profileName, out var profile)) return null;
            if (!profile.TryGetProperty("applicationUrl", out var url) || url.ValueKind != JsonValueKind.String) return null;
            return FirstHttpPort(url.GetString());
        }
        catch { return null; }
    }

    private ProjectServiceInfo? RiderNpm(string root, string projectDir, XElement cfg, string name)
    {
        var pkg = ResolveRiderPath(root, projectDir, cfg.Element("package-json")?.Attribute("value")?.Value);
        if (pkg is null) return null;
        var script = cfg.Element("scripts")?.Elements("script").FirstOrDefault()?.Attribute("value")?.Value;
        if (string.IsNullOrWhiteSpace(script)) return null;

        var command = cfg.Element("command")?.Attribute("value")?.Value ?? "run";
        var pkgDir = Path.GetDirectoryName(pkg)!;
        var cwd = RelCwd(root, pkgDir);
        var mgr = DetectPackageManager(pkgDir, root);
        // Как в ParseNode: npm требует «run <script>», у pnpm/yarn скрипт идёт сам по себе
        var args = mgr == "npm" ? new[] { command, script } : new[] { script };

        return new ProjectServiceInfo(
            Id: Slug($"rider-{name}"),
            Name: name,
            Source: "rider",
            Command: mgr,
            Args: args,
            Cwd: cwd,
            SuggestedPort: null,   // Vite/webpack печатают URL в вывод — поймаем при старте
            AutoPort: false,
            Saved: false);
    }

    /// <summary>
    /// Запуск node-скрипта (тип NodeJSConfigurationType): всё лежит в атрибутах —
    /// path-to-js-file, application-parameters, working-dir.
    ///
    /// В отличие от npm-конфигураций порт тут часто задан ЯВНО, аргументом командной
    /// строки, и вытащить его важно: без порта сервис не опознаётся как поднятый снаружи,
    /// и продукт предлагает запустить его поверх уже работающего процесса.
    /// </summary>
    private ProjectServiceInfo? RiderNodeJs(string root, string projectDir, XElement cfg, string name)
    {
        var script = ResolveRiderPath(root, projectDir, cfg.Attribute("path-to-js-file")?.Value);
        if (script is null) return null;

        var appArgs = SplitArgs(cfg.Attribute("application-parameters")?.Value);
        var nodeArgs = SplitArgs(cfg.Attribute("node-parameters")?.Value);
        var workingDir = ResolveRiderPath(root, projectDir, cfg.Attribute("working-dir")?.Value);
        // Путь к скрипту оставляем абсолютным только если он вне рабочей папки: иначе
        // командная строка распухает, а запуск идёт из cwd и так найдёт файл
        var cwd = workingDir is null ? RelCwd(root, Path.GetDirectoryName(script)!) : RelCwd(root, workingDir);

        return new ProjectServiceInfo(
            Id: Slug($"rider-{name}"),
            Name: name,
            Source: "rider",
            Command: "node",
            Args: [.. nodeArgs, script, .. appArgs],
            Cwd: cwd,
            SuggestedPort: PortFromArgs(appArgs),
            AutoPort: false,
            Saved: false);
    }

    /// <summary>Разбить строку аргументов по пробелам, уважая кавычки.</summary>
    private static string[] SplitArgs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;
        foreach (var ch in raw)
        {
            if (quote is not null)
            {
                if (ch == quote) quote = null;
                else current.Append(ch);
            }
            else if (ch == '\"' || ch == '\'')
            {
                quote = ch;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(ch);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return [.. parts];
    }

    /// <summary>
    /// Порт из аргументов запуска: «--port 5590», «--port=5590», «-p 5590».
    /// Значение вне диапазона портов игнорируем — «--port» бывает и у чужих флагов.
    /// </summary>
    private static int? PortFromArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? value = null;
            if (a is "--port" or "-p" && i + 1 < args.Length) value = args[i + 1];
            else if (a.StartsWith("--port=", StringComparison.Ordinal)) value = a["--port=".Length..];
            if (value is null) continue;
            if (int.TryParse(value, out var port) && port is > 0 and <= 65535) return port;
        }
        return null;
    }

    private ProjectServiceInfo? RiderCompose(string root, string projectDir, XElement cfg, string name)
    {
        var settings = cfg.Element("deployment")?.Element("settings");
        if (settings is null) return null;

        var sourceFile = settings.Elements("option")
            .FirstOrDefault(o => o.Attribute("name")?.Value == "sourceFilePath")?.Attribute("value")?.Value;
        var full = ResolveRiderPath(root, projectDir, sourceFile);
        if (full is null) return null;
        var rel = RelCwd(root, full) ?? Path.GetFileName(full);

        var args = new List<string> { "compose", "-f", rel };
        var profiles = settings.Elements("option")
            .FirstOrDefault(o => o.Attribute("name")?.Value == "profiles")?.Element("list")?
            .Elements("option").Select(o => o.Attribute("value")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v)) ?? [];
        foreach (var p in profiles) { args.Add("--profile"); args.Add(p!); }
        args.Add("up");

        return new ProjectServiceInfo(
            Id: Slug($"rider-{name}"),
            Name: name,
            Source: "rider",
            Command: "docker",
            Args: [.. args],
            Cwd: null,
            SuggestedPort: null,   // хостовые порты знает сам compose-файл, его парсит ParseCompose
            AutoPort: false,
            Saved: false);
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

    // Ключ дедупликации: один и тот же запуск, найденный разными парсерами, должен
    // схлопнуться. У составных конфигураций команды нет — их различает состав, иначе
    // все группы проекта выглядели бы одинаково («пустая команда») и остался бы один.
    private static string Signature(ProjectServiceInfo s) =>
        s.Members is { Length: > 0 }
            ? "group:" + string.Join(',', s.Members)
            : $"{s.Command} {string.Join(' ', s.Args)}@{s.Cwd ?? ""}";

    /// <summary>Короткий детерминированный суффикс — различить сервисы с одинаковым слагом.</summary>
    private static string ShortHash(string value)
        => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..6]
            .ToLowerInvariant();

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
