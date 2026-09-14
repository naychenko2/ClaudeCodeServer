using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeHomeServer.Services.Execution;

// Настройки общей docker-песочницы container-пользователей (секция Sandbox)
public sealed class SandboxOptions
{
    public string Image { get; init; } = "claude-sandbox:latest";
    public string ContainerName { get; init; } = "cc-sandbox";
    // Хостовый корень проектов изолированных пользователей (монтируется в /projects
    // целиком — новый пользователь не требует изменения mount'ов). Пусто = песочница выключена.
    public string ProjectsRoot { get; init; } = "";
    // Адрес Kestrel, достижимый ИЗ контейнера (для MCP *_API_URL)
    public string McpApiUrl { get; init; } = "http://host.docker.internal:5000";
    // Пул preview-портов: публикуется на хост при создании контейнера
    public int PortRangeStart { get; init; } = 42000;
    public int PortRangeSize { get; init; } = 20;
    // Корень MCP-серверов внутри образа (node <McpRoot>/{name}-server/index.js)
    public string McpRoot { get; init; } = "/app/mcp";
    public string DockerPath { get; init; } = "docker";
    // Egress-прокси процессов песочницы (HTTP_PROXY/HTTPS_PROXY); пусто — прямой интернет
    public string Proxy { get; init; } = "";
    // Лимиты контейнера (--memory/--cpus); пусто — без лимита
    public string Memory { get; init; } = "";
    public string Cpus { get; init; } = "";

    public bool Enabled => !string.IsNullOrWhiteSpace(ProjectsRoot);

    public static SandboxOptions FromConfig(IConfiguration config) => new()
    {
        Image = config["Sandbox:Image"] is { Length: > 0 } i ? i : "claude-sandbox:latest",
        ContainerName = config["Sandbox:ContainerName"] is { Length: > 0 } n ? n : "cc-sandbox",
        ProjectsRoot = config["Sandbox:ProjectsRoot"] ?? "",
        McpApiUrl = config["Sandbox:McpApiUrl"] is { Length: > 0 } u ? u : "http://host.docker.internal:5000",
        PortRangeStart = int.TryParse(config["Sandbox:PortRangeStart"], out var ps) ? ps : 42000,
        PortRangeSize = int.TryParse(config["Sandbox:PortRangeSize"], out var sz) ? sz : 20,
        McpRoot = config["Sandbox:McpRoot"] is { Length: > 0 } m ? m : "/app/mcp",
        DockerPath = config["Sandbox:DockerPath"] is { Length: > 0 } d ? d : "docker",
        Proxy = config["Sandbox:Proxy"] ?? "",
        Memory = config["Sandbox:Memory"] ?? "",
        Cpus = config["Sandbox:Cpus"] ?? "",
    };
}

// Жизненный цикл ОДНОГО общего sandbox-контейнера: следит, что он поднят,
// актуален (образ/параметры) и несёт нужные mount'ы. Создание/пересоздание —
// docker CLI; сам контейнер — idle (`sleep infinity`), процессы в него
// запускает DockerProcessRunner через docker exec.
public sealed class SandboxManager
{
    private readonly ILogger<SandboxManager> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTime _lastOkCheck = DateTime.MinValue;
    private static readonly TimeSpan CheckTtl = TimeSpan.FromSeconds(30);

    public SandboxOptions Options { get; }
    // Хостовые каталоги данных песочницы (bind-mount в контейнер)
    public string ProfilesHostDir { get; }
    public string TmpHostDir { get; }
    // Точки монтирования внутри контейнера
    public const string ProjectsMount = "/projects";
    public const string ProfilesMount = "/sandbox-profiles";
    public const string TmpMount = "/turn-tmp";
    public const string SystemPromptsMount = "/app/SystemPrompts";

    public SandboxManager(IConfiguration config, ILogger<SandboxManager> log)
    {
        _log = log;
        Options = SandboxOptions.FromConfig(config);
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        var dataDir = Path.GetDirectoryName(dataPath) ?? Path.Combine(AppContext.BaseDirectory, "data");
        ProfilesHostDir = Path.Combine(dataDir, "sandbox-profiles");
        TmpHostDir = Path.Combine(dataDir, "sandbox-tmp");
    }

    // Убедиться, что контейнер поднят и соответствует образу/параметрам.
    // Троттлинг 30с: docker exec ходов не должен платить inspect каждый раз.
    public async Task EnsureRunningAsync(CancellationToken ct = default)
    {
        if (!Options.Enabled)
            throw new InvalidOperationException(
                "Песочница не настроена: задайте Sandbox:ProjectsRoot в appsettings.Local.json");

        if (DateTime.UtcNow - _lastOkCheck < CheckTtl) return;
        await _lock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _lastOkCheck < CheckTtl) return;

            Directory.CreateDirectory(Options.ProjectsRoot);
            Directory.CreateDirectory(ProfilesHostDir);
            Directory.CreateDirectory(TmpHostDir);

            var desiredImageId = (await DockerAsync(ct, "image", "inspect", "--format", "{{.Id}}", Options.Image))
                is { Code: 0 } img
                ? img.Stdout.Trim()
                : throw new InvalidOperationException(
                    $"Образ песочницы «{Options.Image}» не найден. Соберите его: " +
                    "docker build --target sandbox -t claude-sandbox -f backend/ClaudeHomeServer/Dockerfile .");

            var confHash = ConfigHash(desiredImageId);
            var inspect = await DockerAsync(ct, "inspect", "--format",
                "{{.State.Running}}|{{.Image}}|{{index .Config.Labels \"cc.sandbox.config\"}}",
                Options.ContainerName);

            if (inspect.Code == 0)
            {
                var parts = inspect.Stdout.Trim().Split('|');
                var running = parts.Length > 0 && parts[0] == "true";
                var sameImage = parts.Length > 1 && parts[1] == desiredImageId;
                var sameConf = parts.Length > 2 && parts[2] == confHash;
                if (sameImage && sameConf)
                {
                    if (!running)
                    {
                        var start = await DockerAsync(ct, "start", Options.ContainerName);
                        if (start.Code != 0)
                            throw new InvalidOperationException($"docker start {Options.ContainerName}: {start.Stderr}");
                        _log.LogInformation("Песочница {Name} запущена", Options.ContainerName);
                    }
                    _lastOkCheck = DateTime.UtcNow;
                    return;
                }
                // Образ или параметры изменились — пересоздаём (активные ходы в контейнере оборвутся)
                _log.LogInformation("Пересоздание песочницы {Name}: образ/параметры изменились", Options.ContainerName);
                await DockerAsync(ct, "rm", "-f", Options.ContainerName);
            }

            if (Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN") is not { Length: > 0 })
                _log.LogWarning("В окружении бэкенда нет CLAUDE_CODE_OAUTH_TOKEN: ходы " +
                    "container-пользователей на primary-подписке будут падать «Not logged in»");

            var args = BuildRunArgs(confHash);
            var run = await DockerAsync(ct, args.ToArray());
            if (run.Code != 0)
                throw new InvalidOperationException($"docker run песочницы не удался: {run.Stderr}");
            _log.LogInformation("Песочница {Name} создана из {Image}", Options.ContainerName, Options.Image);
            _lastOkCheck = DateTime.UtcNow;
        }
        finally { _lock.Release(); }
    }

    // Pure-вариант для тестов: путь к SystemPrompts задаётся параметром, чтобы тесты
    // работали во временном каталоге и не трогали общий bin/SystemPrompts (см. ревью
    // 2026-09-05, H-3). Прод-код идёт через приватную BuildRunArgs ниже.
    internal static List<string> BuildRunArgsForHost(
        string systemPromptsHost,
        SandboxOptions opts,
        string profilesHostDir,
        string tmpHostDir,
        string confHash)
    {
        var portEnd = opts.PortRangeStart + opts.PortRangeSize - 1;
        // Каталога нет (поставка без карты — bareProvider.SystemPromptFile пустой или
        // BareMode не используется) — bind-mount подавляем, чтобы не плодить пустой
        // каталог в контейнере.
        var systemPromptsExists = Directory.Exists(systemPromptsHost);
        var args = new List<string>
        {
            "run", "-d",
            "--name", opts.ContainerName,
            "--restart", "unless-stopped",
            "--label", $"cc.sandbox.config={confHash}",
            "-v", $"{opts.ProjectsRoot}:{ProjectsMount}",
            "-v", $"{profilesHostDir}:{ProfilesMount}",
            "-v", $"{tmpHostDir}:{TmpMount}",
            "-p", $"{opts.PortRangeStart}-{portEnd}:{opts.PortRangeStart}-{portEnd}",
            "--add-host", "host.docker.internal:host-gateway",
        };
        if (systemPromptsExists)
            args.AddRange(["-v", $"{systemPromptsHost}:{SystemPromptsMount}"]);
        // Токен подписки в контейнер здесь НЕ кладём: он запёкся бы в момент создания и не
        // обновлялся бы после — единая точка правды теперь DockerProcessRunner.BuildTurnEnv
        // (доставка per-exec, на каждый docker exec заново).
        if (!string.IsNullOrWhiteSpace(opts.Proxy))
        {
            foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy" })
            {
                args.Add("-e");
                args.Add($"{key}={opts.Proxy}");
            }
            args.Add("-e");
            args.Add("NO_PROXY=localhost,127.0.0.1,::1,host.docker.internal");
            args.Add("-e");
            args.Add("no_proxy=localhost,127.0.0.1,::1,host.docker.internal");
        }
        if (!string.IsNullOrWhiteSpace(opts.Memory)) { args.Add("--memory"); args.Add(opts.Memory); }
        if (!string.IsNullOrWhiteSpace(opts.Cpus)) { args.Add("--cpus"); args.Add(opts.Cpus); }
        args.Add(opts.Image);
        return args;
    }

    // Хостовый путь краткой карты BareMode (SystemPrompts/) — от AppContext.BaseDirectory
    // бэкенда, как и mcp/mcp-dify (остальные mount'ы — от data dir, не BaseDirectory).
    // internal: путь ОБЯЗАН совпадать с хостом правила /app/SystemPrompts в DockerPathMapper.
    // Расхождение молча ломает container-владельцев (mount ведёт в одно место, --system-prompt-file
    // указывает в другое → «System prompt file not found», exit=1, ноль событий stream-json →
    // ложный Unreachable и цепочка фолбэка). Связку держит DockerPathMapperTests.
    internal static string DefaultSystemPromptsHost() =>
        Path.Combine(AppContext.BaseDirectory, "SystemPrompts");

    private List<string> BuildRunArgs(string confHash) =>
        BuildRunArgsForHost(DefaultSystemPromptsHost(), Options, ProfilesHostDir, TmpHostDir, confHash);

    // Хеш параметров запуска: несовпадение метки на контейнере → пересоздать
    // (смена диапазона портов/mount'ов/прокси не применяется к живому контейнеру)
    internal static string ConfigHashForHost(
        string systemPromptsHost,
        SandboxOptions opts,
        string profilesHostDir,
        string tmpHostDir,
        string imageId)
    {
        var systemPromptsExists = Directory.Exists(systemPromptsHost);
        var payload = string.Join("\n",
            imageId, opts.ProjectsRoot, profilesHostDir, tmpHostDir,
            opts.PortRangeStart.ToString(), opts.PortRangeSize.ToString(),
            opts.Proxy, opts.Memory, opts.Cpus,
            systemPromptsExists ? systemPromptsHost : "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..16];
    }

    private string ConfigHash(string imageId) =>
        ConfigHashForHost(DefaultSystemPromptsHost(), Options, ProfilesHostDir, TmpHostDir, imageId);

    private async Task<(int Code, string Stdout, string Stderr)> DockerAsync(CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Options.DockerPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Не удалось запустить docker CLI");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stdout, await stderr);
    }
}
