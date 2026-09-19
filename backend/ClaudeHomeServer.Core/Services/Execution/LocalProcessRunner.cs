using System.Diagnostics;

namespace ClaudeHomeServer.Services.Execution;

// Локальная среда: процессы запускаются на машине сервера (историческое поведение).
public sealed class LocalProcessRunner : IProcessLauncher
{
    public static readonly LocalProcessRunner Instance = new();

    public bool IsSandboxed => false;
    public bool TargetIsWindows => OperatingSystem.IsWindows();
    public IPathMapper Paths => IdentityPathMapper.Instance;
    public string ClaudeCliCommand => ClaudeCliLocator.FindClaudeExecutable();
    public string HostTempDir => Path.GetTempPath();
    public string? McpApiUrlOverride => null;

    public Process Start(ProcessSpec spec)
    {
        var options = IsolationOptions.Instance;
        // Резолв systemd-run по PATH — только при включённой изоляции: выключенная не платит
        // ни одним обращением к диску, а BuildStartInfo остаётся чистой функцией от параметров.
        var systemdRun = options.Enabled && !TargetIsWindows ? ResolveSystemdRunPath(options) : null;
        var (psi, _) = BuildStartInfo(spec, options, TargetIsWindows, systemdRun, ProbeOnce(options, systemdRun));
        var process = new Process { StartInfo = psi, EnableRaisingEvents = spec.EnableRaisingEvents };
        if (!process.Start())
            throw new InvalidOperationException($"Не удалось запустить {spec.FileName}");
        if (spec.Track) ProcessRegistry.Register(process);
        return process;
    }

    public int EstimateCommandLineLength(ProcessSpec spec)
    {
        // Симметрично BuildStartInfo:
        //   RawArguments — кладётся в psi.Arguments как есть, .NET не экранирует, и реальная
        //   argv будет этой строкой после имени exe + пробела. ArgCost для сырых строк не
        //   применим (там вложенные кавычки для cmd /s /c, и «верхняя оценка» — сама строка);
        //   если она длиннее CmdlineLimit — откажем сразу, без сюрпризов.
        //   Без RawArguments — psi.ArgumentList экранирует каждый аргумент, и верхняя оценка =
        //   sum(ArgCost) по ним + длина пути к exe. .NET обрамляет FileName кавычками ВСЕГДА,
        //   если в нём есть пробелы (ResolveExecutable на Windows может вернуть
        //   «C:\Program Files\…\claude.exe»), — ArgCost бы учёл это, но строка пути без пробелов
        //   остаётся «голой», и верхней оценки хватает: запас на типичном наборе аргументов
        //   остаётся положительным (проверено в DockerProcessRunnerCmdlineEstimationTests
        //   и LocalProcessRunnerEnvTests на живой сборке .NET).
        //   Обёртка systemd-run (изоляция включена, не Windows, systemd-run найден): FileName
        //   становится путь к systemd-run, а exe уезжает в ArgumentList после флагов обёртки
        //   и `--`. Проверку user-шины оценка не повторяет — при её отсутствии обёртки не будет,
        //   и оценка выйдет завышенной, а верхней границе это можно.
        var options = IsolationOptions.Instance;
        var systemdRun = options.Enabled && !TargetIsWindows && spec.RawArguments is null
            ? ResolveSystemdRunPath(options)
            : null;
        return EstimateCommandLineLength(spec, options, systemdRun);
    }

    // Чистая часть оценки: systemdRun != null означает «обёртка будет».
    internal static int EstimateCommandLineLength(ProcessSpec spec, IsolationOptions options, string? systemdRun)
    {
        var exePath = ExecutableResolver.ResolveExecutable(spec.FileName);
        if (spec.RawArguments is { } raw)
            return exePath.Length + 1 + raw.Length;
        int total;
        if (options.Enabled && !string.IsNullOrEmpty(systemdRun))
        {
            total = systemdRun.Length + CmdlineEstimate.ArgCost(exePath);
            foreach (var a in WrapperArgs(options)) total += CmdlineEstimate.ArgCost(a);
        }
        else total = exePath.Length;
        foreach (var a in spec.Args) total += CmdlineEstimate.ArgCost(a);
        return total;
    }

    // Изоляция процессов по памяти (инцидент 2026-09-19: systemd-oomd дважды убил весь
    // ccs.service, потому что сборки агентов живут в cgroup прода). При включённой изоляции
    // на не-Windows процесс запускается как
    //   systemd-run --user --scope --quiet --collect --slice=… --property=MemoryHigh=…
    //     --property=MemoryMax=… -- <exe> <args…>
    // и оказывается в …/ccs.slice/ccs-agents.slice/run-p<PID>-….scope; прод ccs.service
    // живёт в app.slice (default user-юнитов) — ccs.slice и app.slice сиблинги под
    // user@<uid>.service, и ccs.service «внутри» ccs.slice НЕ сидит. При нехватке памяти
    // умирает scope агента, а не прод. systemd-run --scope исполняет команду в своём же
    // процессе, поэтому PID тот же: Kill(entireProcessTree) и interrupt работают как раньше,
    // stdin/stdout идут насквозь, окружение psi.Environment (ClearEnv → Env) доезжает до
    // команды (проверено на прод-хосте).
    //
    // targetIsWindows и systemdRunPath — параметры, а не чтение OperatingSystem/PATH внутри:
    // решение «оборачивать или нет» проверяется тестом на любой ОС. systemdRunPath — уже
    // разрешённый путь (null — не найден); резолвит его Start.
    //
    // probeResult — результат пробы systemd-run; null — проба не выполнялась (изоляция
    // выключена, Windows, systemd-run не найден) или её не передали — обёртка решается
    // прежними проверками fail-open, как и до пробы.
    //
    // reason: null — обёртка применена; «no-isolation» — изоляция выключена или Windows;
    // иначе — текст причины fail-open (warning печатается один раз за процесс).
    public static (ProcessStartInfo psi, string? reason) BuildStartInfo(
        ProcessSpec spec,
        IsolationOptions? options = null,
        bool targetIsWindows = false,
        string? systemdRunPath = null,
        SystemdRunProbeResult? probeResult = null)
    {
        options ??= IsolationOptions.Instance;
        var psi = new ProcessStartInfo
        {
            FileName = ExecutableResolver.ResolveExecutable(spec.FileName),
            UseShellExecute = false,
            RedirectStandardInput = spec.RedirectStdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (spec.WorkingDirectory is not null)
            psi.WorkingDirectory = spec.WorkingDirectory;
        if (spec.StdioEncoding is { } enc)
        {
            psi.StandardOutputEncoding = enc;
            psi.StandardErrorEncoding = enc;
            if (spec.RedirectStdin) psi.StandardInputEncoding = enc;
        }
        // RawArguments — сырая строка ВМЕСТО экранированного списка (см. ProcessSpec):
        // ArgumentList экранирует " как \", что ломает cmd /s /c с вложенными кавычками
        if (spec.RawArguments is { } raw) psi.Arguments = raw;
        else foreach (var a in spec.Args) psi.ArgumentList.Add(a);
        // Сначала выкидываем унаследованное (psi.Environment — копия окружения хоста),
        // потом кладём оверрайды: осознанный Env всегда сильнее системной переменной.
        if (spec.ClearEnv is not null)
            foreach (var k in spec.ClearEnv)
                if (psi.Environment.Remove(k)) WarnClearedOnce(k);
        if (spec.Env is not null)
            foreach (var (k, v) in spec.Env) psi.Environment[k] = v;

        if (!options.Enabled || targetIsWindows) return (psi, "no-isolation");

        // Fail-open: обёртку не применить — запускаем как раньше, причину пишем в лог один раз
        string? reason = null;
        if (spec.RawArguments is not null)
            reason = "задан RawArguments (механизм Windows-cmd, в список аргументов не заворачивается)";
        else if (string.IsNullOrEmpty(systemdRunPath))
            reason = "systemd-run не найден в PATH";
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"))
                 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
            reason = "нет user-шины (XDG_RUNTIME_DIR и DBUS_SESSION_BUS_ADDRESS пусты)";
        else if (probeResult is not null && probeResult.Ok is false)
            // systemd-run в PATH есть, но в рантайме не работает (нет прав на slice,
            // битая шина, не тот systemd) — без пробы КАЖДЫЙ запуск агента падал и прод стоял.
            reason = probeResult.FailReason is { } r ? r : "проба systemd-run не прошла";
        if (reason is not null)
        {
            WarnIsolationOnce(reason);
            return (psi, reason);
        }

        // Висящие узлы MSBuild и VBCSCompiler держат гигабайты между сборками. MSBuild читает
        // переменные окружения как свойства; явный spec.Env не перебиваем.
        foreach (var (k, v) in BuildIsolationEnv)
            psi.Environment.TryAdd(k, v);

        var exeArgs = psi.ArgumentList.ToList();
        psi.ArgumentList.Clear();
        foreach (var a in WrapperArgs(options)) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(psi.FileName);
        foreach (var a in exeArgs) psi.ArgumentList.Add(a);
        psi.FileName = systemdRunPath!;
        return (psi, null);
    }

    // Только psi — для вызывающих, которым причина решения не нужна.
    public static ProcessStartInfo BuildStartInfoOnly(
        ProcessSpec spec,
        IsolationOptions? options = null,
        bool targetIsWindows = false,
        string? systemdRunPath = null,
        SystemdRunProbeResult? probeResult = null) =>
        BuildStartInfo(spec, options, targetIsWindows, systemdRunPath, probeResult).psi;

    // Флаги systemd-run до exe, заканчиваются `--`. Единственный источник и для сборки
    // запуска, и для оценки длины командной строки — иначе они разъедутся.
    // Свойства — длинной формой --property=…: короткое «-p X» одним элементом argv getopt
    // разобрал бы как значение с ведущим пробелом.
    internal static IEnumerable<string> WrapperArgs(IsolationOptions options)
    {
        yield return "--user";
        yield return "--scope";
        yield return "--quiet";
        yield return "--collect";
        if (!string.IsNullOrWhiteSpace(options.Slice))
            yield return $"--slice={options.Slice}";
        if (!string.IsNullOrWhiteSpace(options.MemoryHigh))
            yield return $"--property=MemoryHigh={options.MemoryHigh}";
        if (!string.IsNullOrWhiteSpace(options.MemoryMax))
            yield return $"--property=MemoryMax={options.MemoryMax}";
        yield return "--";
    }

    // Переменные против висящих узлов MSBuild: ставим ВСЕМ изолированным процессам,
    // а не только сборкам (для не-сборщиков безвредно — MSBuild их просто не читает).
    // Явный spec.Env сильнее (TryAdd).
    internal static readonly (string Key, string Value)[] BuildIsolationEnv =
    [
        ("MSBUILDDISABLENODEREUSE", "1"),
        ("DOTNET_CLI_USE_MSBUILD_SERVER", "0"),
        ("UseSharedCompilation", "false"),
    ];

    // Путь к systemd-run: явный из конфига (Execution:Isolation:SystemdRunPath) или поиск по PATH.
    public static string? ResolveSystemdRunPath(IsolationOptions options) =>
        !string.IsNullOrWhiteSpace(options.SystemdRunPath)
            ? options.SystemdRunPath
            : FindSystemdRun(Environment.GetEnvironmentVariable("PATH"));

    internal static string? FindSystemdRun(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir, "systemd-run");
                if (File.Exists(full)) return full;
            }
            catch (ArgumentException) { /* мусорная запись в PATH */ }
        }
        return null;
    }

    // ---- Проба systemd-run: обёртка есть в PATH, но работает ли она в рантайме? ----
    // systemd-run в PATH, user-шина есть, а обёртка в рантайме не сработала (нет прав на
    // slice, битая шина, не тот systemd) — без пробы КАЖДЫЙ запуск агента падал и прод стоял.
    //
    // Probe — шов: по умолчанию реальная команда (DefaultProbe), тесты подменяют стабом,
    // чтобы не зависеть от настоящего systemd. Проба — отдельный процесс, повторять её на
    // каждый запуск не нужно: результат кэшируется на жизнь процесса (лениво, потокобезопасно,
    // lock — как у warning'ов), поэтому «проба зовётся один раз» даже на N запусков.
    public static SystemdRunProbe Probe { get; set; } = DefaultProbe;
    // Потолок жизни пробы: systemd-run, зависший на мёртвой шине, не должен держать запуск агента.
    internal const int ProbeTimeoutMs = 5000;

    private static SystemdRunProbeResult? _probeResult;
    private static readonly object _probeLock = new();

    // Выполняет пробу один раз за процесс и возвращает кэш; null — проба не нужна:
    // изоляция выключена (или Windows) или systemd-run не найден — в этих случаях обёртки
    // не будет и без пробы (BuildStartInfo сам fail-open'ит). Для spec с RawArguments Start
    // тоже вызывает пробу: сам этот запуск решит fail-open на RawArguments, но следующий
    // запуск без RawArguments получит уже кэшированный результат, а не новую пробу.
    internal static SystemdRunProbeResult? ProbeOnce(IsolationOptions options, string? systemdRunPath)
    {
        if (!options.Enabled || string.IsNullOrEmpty(systemdRunPath)) return null;
        lock (_probeLock)
        {
            if (_probeResult is not null) return _probeResult;
            var result = Probe(systemdRunPath, WrapperArgs(options), "true", ProbeTimeoutMs);
            _probeResult = result;
            return result;
        }
    }

    // Сброс кэша пробы: только для тестов (статическое состояние процесса — коллекция
    // ProcessGlobalState сериализует тесты, а тест сам сохраняет/восстанавливает шов Probe).
    internal static void ResetProbeForTests()
    {
        lock (_probeLock) _probeResult = null;
    }

    // Дефолтная проба: systemd-run <WrapperArgs> -- true с таймаутом. Код 0 — обёртка
    // разрешена; ненулевой код, таймаут или исключение — проба не прошла с причиной.
    public static SystemdRunProbeResult DefaultProbe(
        string systemdRunPath,
        IEnumerable<string> wrapperArgs,
        string command,
        int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = systemdRunPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in wrapperArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(command);

        var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                return new SystemdRunProbeResult(false, $"не удалось запустить {systemdRunPath}");
            // stdout/stderr не читаем: `true` молчит, а неречиваемые байты в 64К буфере
            // не затасят 5-секундную команду; таймаут страхует зависшую шину.
            if (process.WaitForExit(timeoutMs))
                return process.ExitCode == 0
                    ? new SystemdRunProbeResult(true)
                    : new SystemdRunProbeResult(false, $"проба systemd-run завершилась с кодом {process.ExitCode}");
            process.Kill(entireProcessTree: true);
            return new SystemdRunProbeResult(false, $"проба systemd-run не завершилась за {timeoutMs / 1000} с");
        }
        catch (Exception ex)
        {
            return new SystemdRunProbeResult(false, $"проба systemd-run упала: {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    // Изоляция включена, но обёртка не применена: без этой строки «oomd снова убил прод»
    // не объяснить. Один раз на причину за жизнь процесса — иначе строка на каждом запуске.
    private static readonly HashSet<string> _isolationWarnings = [];
    private static void WarnIsolationOnce(string reason)
    {
        lock (_isolationWarnings)
        {
            if (!_isolationWarnings.Add(reason)) return;
        }
        Console.WriteLine($"[exec] изоляция процессов по памяти включена, но не применена: {reason}");
    }

    /// <summary>
    /// Имя исполняемого файла для запуска.
    ///
    /// На Windows `npm`, `npx`, `yarn`, `pnpm` — это .cmd-обёртки, а Process.Start с
    /// UseShellExecute=false расширения из PATHEXT не подставляет: команда «npm» просто
    /// не находится («Не удается найти указанный файл»). Из-за этого с хоста не стартовал
    /// НИ ОДИН сервис, найденный разбором package.json.
    ///
    /// Разворачиваем имя в полный путь сами. Не нашли — возвращаем как было, чтобы
    /// ошибка осталась прежней и понятной, а не подменялась нашей.
    /// </summary>
    public static string ResolveExecutable(string fileName) =>
        ExecutableResolver.ResolveExecutable(fileName);

    /// <summary>
    /// Поиск команды по каталогам PATH с подстановкой расширений PATHEXT — как это делает
    /// cmd. Тонкая обёртка над <see cref="ExecutableResolver.FindInPath"/>, оставлена
    /// ради существующих вызовов и тестов, которые звали правило по короткому пути.
    /// Сам примитив живёт в `ClaudeHomeServer.Core.Services.ExecutableResolver`
    /// (Этап 3, волна 1: поднят в Core ради CodeGraph; до этого — `Services.ExecutableResolver`,
    /// задача `57b5e9bc` шаг 5): чистая функция от fileName/path/pathext, без процессов и без DI.
    /// </summary>
    public static string? FindInPath(string fileName, string? path, string? pathext) =>
        ExecutableResolver.FindInPath(fileName, path, pathext);

    // Факт выброса системной переменной — в лог, по одному разу на ключ за жизнь процесса.
    // Без этого «вчера работало, сегодня не логинится» на машине с ANTHROPIC_API_KEY в
    // окружении превращается в гадание: снаружи видно только отказ авторизации CLI.
    // Значение НЕ печатаем — это секрет. Вернуть прежнее наследование: Claude:InheritSystemEnv=true.
    private static readonly HashSet<string> _warnedKeys = [];
    private static void WarnClearedOnce(string key)
    {
        lock (_warnedKeys)
        {
            if (!_warnedKeys.Add(key)) return;
        }
        Console.WriteLine($"[exec] системная переменная {key} не пущена в процесс claude "
            + "(маршрут задаёт сервер; вернуть наследование — Claude:InheritSystemEnv=true)");
    }

    public void Kill(Process process, string? turnId = null)
    {
        // Всё дерево: claude порождает node-процессы MCP-серверов
        try { process.Kill(entireProcessTree: true); }
        catch { /* процесс уже завершился */ }
    }
}
