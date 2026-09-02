using System.Diagnostics;

namespace ClaudeHomeServer.Services.Execution;

// Среда «песочница»: процессы пользователя исполняются внутри общего docker-контейнера
// (docker exec -i). Клиентский процесс docker на хосте — лишь пайп stdio; настоящий
// процесс живёт в контейнере и убивается изнутри по метке хода (run-turn.sh + pid-файл).
public sealed class DockerProcessRunner : IProcessLauncher
{
    private readonly SandboxManager _sandbox;
    private readonly string _ownerId;
    private readonly DockerPathMapper _paths;
    // Троттлинг сидинга профиля: раз в 5 минут на процесс (как SyncUserProfile)
    private static readonly Dictionary<string, DateTime> _profileSeeded = new();
    private static readonly object _seedLock = new();

    public DockerProcessRunner(SandboxManager sandbox, string ownerId)
    {
        _sandbox = sandbox;
        _ownerId = ownerId;
        _paths = new DockerPathMapper(sandbox);
    }

    public bool IsSandboxed => true;
    public bool TargetIsWindows => false;
    public IPathMapper Paths => _paths;
    public string ClaudeCliCommand => "claude";
    public string? McpApiUrlOverride => _sandbox.Options.McpApiUrl;
    public string HostTempDir
    {
        get
        {
            // per-user подкаталог: изолированные пользователи не видят чужие temp-файлы
            var dir = Path.Combine(_sandbox.TmpHostDir, _ownerId);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public Process Start(ProcessSpec spec)
    {
        // Сырая командная строка — механизм Windows-cmd (см. ProcessSpec.RawArguments):
        // песочница всегда Linux, сюда это поле дойти не должно — молча исполнить Args
        // вместо заданной строки было бы тихой подменой команды
        if (spec.RawArguments is not null)
            throw new NotSupportedException("RawArguments — только local-раннер (cmd /s /c); песочница всегда Linux");

        // Дешёвый (троттлёный) гарант, что контейнер поднят и актуален
        _sandbox.EnsureRunningAsync().GetAwaiter().GetResult();

        var env = BuildTurnEnv(spec.Env);

        var turnId = spec.TurnId ?? Guid.NewGuid().ToString("N")[..12];
        var dockerArgs = new List<string> { "exec" };
        if (spec.RedirectStdin) dockerArgs.Add("-i");
        if (spec.WorkingDirectory is not null)
        {
            dockerArgs.Add("-w");
            dockerArgs.Add(_paths.ToRuntime(spec.WorkingDirectory));
        }
        foreach (var (k, v) in env)
        {
            dockerArgs.Add("-e");
            dockerArgs.Add($"{k}={v}");
        }
        dockerArgs.Add(_sandbox.Options.ContainerName);
        // Обвязка убиваемости: setsid-группа + pid-файл /tmp/turns/{turnId}.pid
        dockerArgs.Add("/app/run-turn.sh");
        dockerArgs.Add(turnId);
        dockerArgs.Add(spec.FileName);
        dockerArgs.AddRange(spec.Args);

        var psi = new ProcessStartInfo
        {
            FileName = _sandbox.Options.DockerPath,
            UseShellExecute = false,
            RedirectStandardInput = spec.RedirectStdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (spec.StdioEncoding is { } enc)
        {
            psi.StandardOutputEncoding = enc;
            psi.StandardErrorEncoding = enc;
            if (spec.RedirectStdin) psi.StandardInputEncoding = enc;
        }
        foreach (var a in dockerArgs) psi.ArgumentList.Add(a);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = spec.EnableRaisingEvents };
        if (!process.Start())
            throw new InvalidOperationException($"Не удалось запустить docker exec для {spec.FileName}");
        if (spec.Track) ProcessRegistry.Register(process);
        return process;
    }

    public void Kill(Process process, string? turnId = null)
    {
        // Сначала добиваем группу процессов внутри контейнера (docker-клиент её не убьёт)
        if (turnId is not null)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _sandbox.Options.DockerPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in new[]
                {
                    "exec", _sandbox.Options.ContainerName, "sh", "-c",
                    // dash-builtin kill не поддерживает "--"; отрицательный аргумент =
                    // группа процессов. Пустой pid-файл → "kill -KILL -" тихо игнорируем
                    $"P=$(cat /tmp/turns/{turnId}.pid 2>/dev/null); [ -n \"$P\" ] && kill -KILL \"-$P\" 2>/dev/null; exit 0",
                })
                    psi.ArgumentList.Add(a);
                using var killer = Process.Start(psi);
                killer?.WaitForExit(5000);
            }
            catch { /* контейнер мог уже остановиться */ }
        }
        // Затем клиентский docker-процесс на хосте (освобождает пайпы)
        try { process.Kill(entireProcessTree: true); }
        catch { /* процесс уже завершился */ }
    }

    // Сборка env хода: копия spec.Env + песочный CLAUDE_CONFIG_DIR + фолбэк токена подписки.
    // Вынесено из Start, чтобы правило сборки можно было проверить тестом без запуска
    // процессов (как LocalProcessRunner.BuildStartInfo) — тесты гоняются и на linux-раннере CI.
    // ClearEnv здесь намеренно не применяется: окружение хода собирается с нуля и уезжает
    // в контейнер через -e, хостовые переменные в него не наследуются — вычищать нечего.
    // Изоляция от системных ANTHROPIC_* получается сама собой.
    internal Dictionary<string, string> BuildTurnEnv(IReadOnlyDictionary<string, string>? specEnv)
    {
        var env = specEnv is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(specEnv);
        RewriteProfileEnv(env);

        // Единая точка правды для токена подписки в песочнице: раньше он клался только
        // при docker run (SandboxManager.BuildRunArgs), поэтому контейнер, созданный ДО
        // появления токена, жил без него навсегда. Докладываем на каждый exec, кроме ходов
        // на чужой эндпоинт/аккаунт пула — токен подписки не должен уезжать чужому эндпоинту.
        if (!env.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN")
            && !env.ContainsKey("ANTHROPIC_AUTH_TOKEN")
            && !env.ContainsKey("ANTHROPIC_API_KEY")
            && Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN") is { Length: > 0 } token)
            env["CLAUDE_CODE_OAUTH_TOKEN"] = token;

        return env;
    }

    // CLAUDE_CONFIG_DIR: хостовый путь профиля (BuildCliEnv кладёт data/claude-profiles/{key})
    // переписывается на профиль песочницы /sandbox-profiles/{userId}/{key}; без явного
    // профиля — {userId}/default. Хостовая сторона создаётся и регистрируется как корень
    // транскриптов (SubagentStreamWatcher/WorkflowController читают её напрямую).
    private void RewriteProfileEnv(Dictionary<string, string> env)
    {
        var key = "default";
        if (env.TryGetValue("CLAUDE_CONFIG_DIR", out var hostProfile)
            && Path.GetFileName(hostProfile.TrimEnd('\\', '/')) is { Length: > 0 } name)
            key = name;

        var profileHostDir = Path.Combine(_sandbox.ProfilesHostDir, _ownerId, key);
        EnsureProfile(profileHostDir);
        env["CLAUDE_CONFIG_DIR"] = $"{SandboxManager.ProfilesMount}/{_ownerId}/{key}";
    }

    private static void EnsureProfile(string profileHostDir)
    {
        lock (_seedLock)
        {
            if (_profileSeeded.TryGetValue(profileHostDir, out var at)
                && DateTime.UtcNow - at < TimeSpan.FromMinutes(5)) return;

            var projectsDir = Path.Combine(profileHostDir, "projects");
            Directory.CreateDirectory(projectsDir);
            WorkflowAgentParser.AddAllowedRoot(projectsDir);

            // Сидинг поставляемых workflow-скриптов (как entrypoint основного контейнера).
            // ЛИЧНЫЕ настройки админа (~/.claude) в профили изолированных пользователей
            // сознательно НЕ синкаются — чистый профиль.
            var defaults = Path.Combine(AppContext.BaseDirectory, "claude-defaults", "workflows");
            if (Directory.Exists(defaults))
            {
                var target = Path.Combine(profileHostDir, "workflows");
                Directory.CreateDirectory(target);
                foreach (var file in Directory.GetFiles(defaults, "*.js"))
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }
            _profileSeeded[profileHostDir] = DateTime.UtcNow;
        }
    }
}
