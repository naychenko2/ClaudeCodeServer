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

    public bool LocalModelsPaused
    {
        get
        {
            var options = IsolationOptions.Instance;
            if (!options.Enabled || TargetIsWindows) return false;
            return CgroupSlices.IsFrozen(options.LocalModelSlice ?? options.Slice);
        }
    }

    public Process Start(ProcessSpec spec)
    {
        var options = IsolationOptions.Instance;
        // Резолв systemd-run по PATH — только при включённой изоляции: выключенная не платит
        // ни одним обращением к диску, а BuildStartInfo остаётся чистой функцией от параметров.
        var systemdRun = options.Enabled && !TargetIsWindows ? ResolveSystemdRunPath(options) : null;
        var unit = NewScopeUnitName();
        var (psi, reason) = BuildStartInfo(
            spec, options, TargetIsWindows, systemdRun, ProbeOnce(options, systemdRun), unit);
        var wrapped = reason is null;
        // Fail-closed по замороженному slice: systemd 259 не запускает юнит в замороженном
        // срезе (-EDEADLK, «Cannot start frozen unit»), а fail-open положил бы процесс в
        // cgroup прода — ровно тот OOM-инцидент, ради которого scope и существует (окна
        // бенча, инцидент 21.11). Две дороги к одному отказу:
        //  • срез заморожен на диске, проба прошла или её не было — проверка SliceIsFrozen;
        //  • срез замёрз между проверкой и пробой — проба упала frozen-отказом, и она не
        //    кэшируется и не становится fail-open (см. ProbeOnce).
        // Отказ — до process.Start(): вызывающий (S2) видит названный Reason и проходит после
        // оттаивания. Обе проверки — только там, где процесс реально шёл бы в scope
        // (обёртка применима или единственной причиной сняла её заморозка): процессу, который
        // и так живёт вне scope (нет user-шины, RawArguments, выключенная изоляция),
        // заморозка не помеха — не отказывать зря.
        var slice = EffectiveSlice(options, spec.LocalModel);
        if ((wrapped && SliceIsFrozen(slice)) || IsFrozenProbeReason(reason))
        {
            WarnIsolationOnce($"выбранный slice заморожен, изолированный процесс не запущен: {slice}");
            throw new IsolationUnavailableException(
                IsolationUnavailableException.SliceFrozen,
                $"Не могу запустить изолированный процесс: slice '{slice}' заморожен",
                slice);
        }
        var process = new Process { StartInfo = psi, EnableRaisingEvents = spec.EnableRaisingEvents || wrapped };
        // Процесс вышел — гасим его scope: оставшиеся в нём узлы MSBuild и VBCSCompiler
        // (реюз внутри хода) и прочие отпочковавшиеся потомки иначе живут в ccs-agents.slice
        // до своего idle-таймаута. Подписка ДО Start — выход не проскочит мимо обработчика.
        // Шов гашения фиксируется на запуске, а не читается в момент выхода: Exited и Task.Run
        // срабатывают с задержкой, и поздний вызов иначе уходит тому, кто подменил шов позже
        // (так плавал Start_ИзоляцияВыключена_ScopeНеГасится — ловил стоп чужого процесса).
        if (wrapped)
        {
            var systemctl = ResolveSystemctlPath(systemdRun!);
            var stop = StopScope;
            process.Exited += (_, _) => StopScopeInBackground(stop, systemctl, unit);
        }
        if (!process.Start())
            throw new InvalidOperationException($"Не удалось запустить {spec.FileName}");
        if (spec.Track) ProcessRegistry.Register(process);
        // Следующий успешный запуск — подходящий момент повторить отложенные гашения: шина
        // жива и обёртка только что работала. Фоном: горячий путь хода не ждёт systemctl.
        if (wrapped) _ = Task.Run(RetryDeferredStops);
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
        //   и `--` (имя юнита — заглушка той же длины). Проверку user-шины оценка не повторяет — при её отсутствии обёртки не будет,
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
            foreach (var a in WrapperArgs(options, ScopeUnitPlaceholder, spec.LocalModel)) total += CmdlineEstimate.ArgCost(a);
        }
        else total = exePath.Length;
        foreach (var a in spec.Args) total += CmdlineEstimate.ArgCost(a);
        return total;
    }

    // Изоляция процессов по памяти (инцидент 2026-09-19: systemd-oomd дважды убил весь
    // ccs.service, потому что сборки агентов живут в cgroup прода). При включённой изоляции
    // на не-Windows процесс запускается как
    //   systemd-run --user --scope --quiet --collect --unit=ccs-run-<guid>.scope --slice=…
    //     --property=MemoryHigh=…
    //     --property=MemoryMax=… -- <exe> <args…>
    // и оказывается в …/ccs.slice/ccs-agents.slice/ccs-run-<guid>.scope; прод ccs.service
    // живёт в app.slice (default user-юнитов) — ccs.slice и app.slice сиблинги под
    // user@<uid>.service, и ccs.service «внутри» ccs.slice НЕ сидит. При нехватке памяти
    // умирает scope агента, а не прод. systemd-run --scope исполняет команду в своём же
    // процессе, поэтому PID тот же: Kill(entireProcessTree) и interrupt работают как раньше,
    // stdin/stdout идут насквозь, окружение psi.Environment (ClearEnv → Env) доезжает до
    // команды (проверено на прод-хосте).
    //
    // targetIsWindows и systemdRunPath — параметры, а не чтение OperatingSystem/PATH внутри:
    // решение «оборачивать или нет» проверяется тестом на любой ОС. systemdRunPath — уже
    // разрешённый путь (null — не найден); резолвит его Start. scopeUnit — имя юнита scope
    // (null — сгенерировать): Start знает его заранее, чтобы погасить scope по выходу процесса.
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
        SystemdRunProbeResult? probeResult = null,
        string? scopeUnit = null)
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

        if (!options.Enabled || targetIsWindows)
        {
            ApplyPrivateBuildNodes(psi.Environment, spec);
            return (psi, "no-isolation");
        }

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
            ApplyPrivateBuildNodes(psi.Environment, spec);
            return (psi, reason);
        }

        var unit = scopeUnit ?? NewScopeUnitName();
        ApplyBuildEnv(psi, spec, options, unit);

        var exeArgs = psi.ArgumentList.ToList();
        psi.ArgumentList.Clear();
        foreach (var a in WrapperArgs(options, unit, spec.LocalModel)) psi.ArgumentList.Add(a);
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

    // Узлы сборки изолированного процесса. MSBuild читает переменные окружения как свойства;
    // явный spec.Env не перебиваем.
    //   Реюз (дефолт): узлы MSBuild и сервер компилятора живут весь ход и гаснут вместе со
    //   scope. Рукопожатие узлов солим именем юнита (MSBUILDNODEHANDSHAKESALT), трубу
    //   компилятора — им же (SharedCompilationId): без этого параллельные ходы одного
    //   пользователя цеплялись бы к узлам друг друга, и остановка чужого scope роняла бы
    //   идущую сборку. Унаследованные запреты реюза (бэкенд, запущенный из хода агента)
    //   снимаем — иначе режим молча выключен.
    //   Без реюза: прежние три запрета — висящие узлы держали гигабайты между сборками.
    private static void ApplyBuildEnv(ProcessStartInfo psi, ProcessSpec spec, IsolationOptions options, string unit)
    {
        if (!options.BuildNodeReuse)
        {
            foreach (var (k, v) in BuildIsolationEnv) psi.Environment.TryAdd(k, v);
            return;
        }
        foreach (var (k, _) in BuildIsolationEnv)
            if (spec.Env is null || !spec.Env.ContainsKey(k)) psi.Environment.Remove(k);
        psi.Environment.TryAdd("MSBUILDNODEHANDSHAKESALT", unit);
        psi.Environment.TryAdd("SharedCompilationId", unit);
    }

    // Уникальное имя scope на процесс: по нему scope гасится после выхода и солятся узлы сборки.
    // В имени — ОТПЕЧАТОК ВЛАДЕЛЬЦА (PID бэкенда, 8 hex-цифр): по нему сторож
    // ScopeOrphanSweeper при старте отличает сироту от живого scope соседнего инстанса.
    // PID в hex фиксированной ширины, поэтому длина имени постоянна — заглушка оценки
    // командной строки той же длины.
    internal static string NewScopeUnitName() =>
        $"ccs-run-{Environment.ProcessId:x8}-{Guid.NewGuid():N}.scope";
    internal static readonly string ScopeUnitPlaceholder = new('x', NewScopeUnitName().Length);

    // Флаги systemd-run до exe, заканчиваются `--`. Единственный источник и для сборки
    // запуска, и для оценки длины командной строки — иначе они разъедутся.
    // Свойства — длинной формой --property=…: короткое «-p X» одним элементом argv getopt
    // разобрал бы как значение с ведущим пробелом.
    internal static IEnumerable<string> WrapperArgs(IsolationOptions options) =>
        WrapperArgs(options, ScopeUnitPlaceholder);

    internal static IEnumerable<string> WrapperArgs(IsolationOptions options, string unit, bool localModel = false)
    {
        // Ход локальной модели — в свой slice (см. IsolationOptions.LocalModelSlice)
        var slice = localModel && !string.IsNullOrWhiteSpace(options.LocalModelSlice)
            ? options.LocalModelSlice
            : options.Slice;
        yield return "--user";
        yield return "--scope";
        yield return "--quiet";
        yield return "--collect";
        yield return $"--unit={unit}";
        if (!string.IsNullOrWhiteSpace(slice))
            yield return $"--slice={slice}";
        if (!string.IsNullOrWhiteSpace(options.MemoryHigh))
            yield return $"--property=MemoryHigh={options.MemoryHigh}";
        if (!string.IsNullOrWhiteSpace(options.MemoryMax))
            yield return $"--property=MemoryMax={options.MemoryMax}";
        // ManagedOOMPreference=avoid в КАЖДОМ scope: на slice свойство не наследуется (man systemd
        // 259, «not applied recursively»; oomd читает xattr только у листовой cgroup-кандидата).
        // Без него scope агента может стать жертвой oomd за чужое давление user@.service.
        yield return "--property=ManagedOOMPreference=avoid";
        // Before=<юнит сервера>: при выключении машины scope хода гасится ПОСЛЕ ccs.service,
        // а не раньше (инцидент 20.10 15:33 — CLI раньше сервера получал SIGTERM и чат
        // уходил в Error). Только порядок, членство в cgroup и OOM-лимиты slice не меняет.
        if (!string.IsNullOrWhiteSpace(options.StopAfterUnit))
            yield return $"--property=Before={options.StopAfterUnit}";
        yield return "--";
    }

    // Переменные против висящих узлов MSBuild (режим BuildNodeReuse=false): ставим ВСЕМ
    // изолированным процессам, а не только сборкам (для не-сборщиков безвредно — MSBuild их
    // просто не читает). Явный spec.Env сильнее (TryAdd).
    internal static readonly (string Key, string Value)[] BuildIsolationEnv =
    [
        ("MSBUILDDISABLENODEREUSE", "1"),
        ("DOTNET_CLI_USE_MSBUILD_SERVER", "0"),
        ("UseSharedCompilation", "false"),
    ];

    // Запреты для spec с меткой PrivateBuildNodes вне scope (ProcessSpec.PrivateBuildNodes):
    //  • MSBUILDDISABLENODEREUSE=1 — узлы выходят с концом сборки и не принимают чужих
    //    клиентов (флаг реюза входит в рукопожатие узла), поэтому дерево процесса — только своё;
    //  • DOTNET_CLI_USE_MSBUILD_SERVER=0 — то же для сервера MSBuild;
    //  • UseSharedCompilation=false — без сервера компилятора: VBCSCompiler общий на
    //    пользователя, висит минуты после сборки, а гашение нашего дерева роняло бы чужую
    //    компиляцию. Соль трубы (SharedCompilationId, как в scope) спасла бы от чужих, но не от
    //    висящего сервера: вне scope его некому погасить. Цена — холодный csc на проект.
    // Перебивают и унаследованное окружение хоста (там бывает «0»), но не явный spec.Env.
    // Внутри scope не нужны: там реюз приватен (соль = имя юнита) и гаснет со scope.
    public static IEnumerable<(string Key, string Value)> PrivateBuildNodeEnv(ProcessSpec spec) =>
        spec.PrivateBuildNodes
            ? BuildIsolationEnv.Where(e => spec.Env is null || !spec.Env.ContainsKey(e.Key))
            : [];

    private static void ApplyPrivateBuildNodes(IDictionary<string, string?> env, ProcessSpec spec)
    {
        foreach (var (k, v) in PrivateBuildNodeEnv(spec)) env[k] = v;
    }

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

    // systemctl лежит рядом с systemd-run (тот же пакет systemd); не нашёлся — по PATH
    internal static string ResolveSystemctlPath(string systemdRunPath)
    {
        var dir = Path.GetDirectoryName(systemdRunPath);
        var sibling = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "systemctl");
        return sibling is not null && File.Exists(sibling) ? sibling : "systemctl";
    }

    // Шов для тестов: чем гасить scope (путь к systemctl, имя юнита). По умолчанию — systemctl.
    internal static Action<string, string> StopScope { get; set; } = StopScopeViaSystemctl;

    // Остановка scope уходит с потока события Exited: systemctl — процесс, а обработчик
    // Exited не должен ждать чужой ввод-вывод. Fail-open: сбой — warning, не исключение;
    // сбой ещё и кладётся имя в список отложенных гашений (RunDeferredStop), успех — снимает.
    private static void StopScopeInBackground(Action<string, string> stop, string systemctl, string unit) =>
        _ = Task.Run(() => RunDeferredStop(stop, systemctl, unit));

    // Единственный путь гашения с учётом отложенных: успех снимает запись (если она была),
    // любой сбой шва (исключение/таймаут/ненулевой код — шов сигналит ScopeStopFailedException)
    // добавляет её с дедупликацией. Наружу не бросает ничего: сбой гашения — warning, не исключение.
    internal static void RunDeferredStop(Action<string, string> stop, string systemctl, string unit)
    {
        try
        {
            stop(systemctl, unit);
            CompleteDeferred(unit);
        }
        catch (ScopeStopFailedException ex)
        {
            WarnScopeStopOnce(ex.Kind, ex.Details);
            DeferStop(systemctl, unit);
        }
        catch (Exception ex)
        {
            WarnScopeStopOnce(ex.GetType().Name, $"{ex.GetType().Name}: {ex.Message}");
            DeferStop(systemctl, unit);
        }
    }

    // KillMode у scope по умолчанию control-group: stop шлёт SIGTERM всем процессам cgroup
    // (узлы MSBuild и VBCSCompiler на него выходят сразу), через TimeoutStopSec — SIGKILL.
    // --no-block: ждать гибели хвоста незачем. Гонки с --collect нет: если scope опустел
    // вместе с процессом, systemd уже собрал его, и stop отвечает «not loaded» (код 5) —
    // гасить нечего, это штатный исход, а не сбой.
    // Сбой (таймаут, ненулевой код кроме 0 и 5) — ScopeStopFailedException: вызывающий путь
    // (RunDeferredStop) по ней кладёт имя в отложенные гашения; warning печатает он же,
    // текстом из Kind/Details — прежним.
    private static void StopScopeViaSystemctl(string systemctl, string unit)
    {
        var psi = new ProcessStartInfo(systemctl)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "--user", "stop", "--no-block", unit }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("systemctl не запустился");
        var stderr = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(10_000))
        {
            try { p.Kill(); } catch { /* уже вышел */ }
            throw new ScopeStopFailedException("timeout", "systemctl --user stop не ответил за 10 с");
        }
        if (p.ExitCode is 0 or 5) return;
        throw new ScopeStopFailedException($"code-{p.ExitCode}",
            $"systemctl --user stop вернул код {p.ExitCode}: {stderr.GetAwaiter().GetResult().Trim()}");
    }

    // Сбой systemctl-остановки scope: названный класс и текст — для warning вызывающего и для
    // того, чтобы от него не отличали «не сбой» (InvalidOperationException «systemctl не
    // запустился» остаётся общим исключением: это не отказ остановки, а отсутствие процесса).
    internal sealed class ScopeStopFailedException(string kind, string details) : Exception(details)
    {
        public string Kind { get; } = kind;
        public string Details { get; } = details;
    }

    // Сбой остановки scope: один раз на класс сбоя (в тексте имя юнита — по нему не дедупить)
    private static readonly HashSet<string> _scopeStopWarnings = [];
    private static void WarnScopeStopOnce(string kind, string details)
    {
        lock (_scopeStopWarnings)
        {
            if (!_scopeStopWarnings.Add(kind)) return;
        }
        Console.WriteLine($"[exec] не удалось погасить scope процесса, хвост мог остаться в slice: {details}");
    }

    // ---- Отложенные гашения scope (S3) ----
    // Сбой гашения (таймаут/исключение/ненулевой код кроме штатных 0 и 5) не исчезает с
    // warning'ом: имя юнита кладётся in-memory списком и повторяется — после следующего
    // успешного запуска scope, общим таймером (≈5 мин, пока есть чем заниматься) и ручным
    // тиком. Успех снимает запись. Лок и версия — по ИМЕНИ unit, не по экземпляру: два
    // одновременных гашения/тика одного unit — одна запись и один активный повтор; поздний
    // отказ старой попытки после успеха новой не возвращает запись (версия попытки уже не
    // актуальна). Список — состояние процесса: после рестарта бэкенда за оставшееся отвечает
    // ScopeOrphanSweeper, не он. Сбой уборки наружу не бросается нигде.
    internal const int DeferredStopIntervalMs = 5 * 60_000;
    private static readonly Dictionary<string, DeferredStop> _deferredStops = new();
    private static readonly object _deferredLock = new();
    private static Timer? _deferredTimer;
    private static bool _periodicArmed;
    // Версия попытки — глобальная последовательная: между записями одного имени не повторяется,
    // по ней поздний отказ старой попытки опознаётся как старый.
    private static uint _deferredSeq;

    private sealed class DeferredStop(string systemctl)
    {
        public string Systemctl { get; } = systemctl;
        public uint Version { get; set; }  // 0 — повтор ещё не начинался
        public bool Retrying { get; set; }
    }

    // Добавление с дедупликацией: имя уже в списке — не трогается ничего, включая версию
    // (идущий повтор остаётся своим).
    private static void DeferStop(string systemctl, string unit)
    {
        lock (_deferredLock)
        {
            if (!_deferredStops.ContainsKey(unit))
                _deferredStops[unit] = new DeferredStop(systemctl);
            EnsureDeferredTimer();
        }
    }

    // Успех гашения: запись имени снимается всегда — даже когда её вернул поздний отказ
    // (чужой успех уже был). internal — точка учёта и для теста гонки (успех «чужим путём»).
    internal static void CompleteDeferred(string unit)
    {
        lock (_deferredLock)
        {
            _deferredStops.Remove(unit);
            MaybeDisposeTimer();
        }
    }

    // Взять повтор по имени: false — имени нет в списке (уже успешно) или его уже повторяет
    // другой («один активный повтор»). Версия попытки выдаётся здесь.
    private static bool TryBeginRetry(string unit, out uint version)
    {
        lock (_deferredLock)
        {
            version = 0;
            if (!_deferredStops.TryGetValue(unit, out var rec) || rec.Retrying) return false;
            version = ++_deferredSeq;
            rec.Version = version;
            rec.Retrying = true;
            return true;
        }
    }

    // Итог попытки. Отказ возвращает запись к ожиданию только если она ещё моя: чужой успех
    // или чужой отказ уже снял/поднял её — мой результат старый и запись не возвращает.
    private static void EndRetry(string unit, uint version, bool ok)
    {
        lock (_deferredLock)
        {
            if (ok) _deferredStops.Remove(unit);
            else if (_deferredStops.TryGetValue(unit, out var rec) && rec.Version == version)
                rec.Retrying = false;
            MaybeDisposeTimer();
        }
    }

    // Повтор всех отложенных: после успешного запуска scope и боевым тиком. Каждая запись
    // повторяется ровно один раз на тик (TryBeginRetry), а снятое другим успехом имя из
    // snapshot'а просто не берётся повтором. Сбой наружу не бросается: EndRetry остаётся на
    // своей записи, warning — один на класс (WarnScopeStopOnce).
    internal static void RetryDeferredStops()
    {
        List<(string Unit, string Systemctl)> snapshot;
        lock (_deferredLock)
        {
            if (_deferredStops.Count == 0) return;
            snapshot = [.. _deferredStops.Select(kv => (kv.Key, kv.Value.Systemctl))];
        }
        foreach (var (unit, systemctl) in snapshot)
        {
            if (!TryBeginRetry(unit, out var version)) continue;
            try
            {
                StopScope(systemctl, unit);
                EndRetry(unit, version, ok: true);
            }
            catch (ScopeStopFailedException ex)
            {
                WarnScopeStopOnce(ex.Kind, ex.Details);
                EndRetry(unit, version, ok: false);
            }
            catch (Exception ex)
            {
                WarnScopeStopOnce(ex.GetType().Name, $"{ex.GetType().Name}: {ex.Message}");
                EndRetry(unit, version, ok: false);
            }
        }
    }

    // Ручной тик — точка входа для тестов и будущего ручного вызова: тот же повтор, что
    // боевой тик, плюс обход сирот (по тому же правилу трёх замков, что при старте).
    internal static void RunDeferredTickNow()
    {
        RetryDeferredStops();
        ScopeOrphanSweeper.OnPeriodicTick();
    }

    // Боевой тик: общий таймер ≈5 мин, пока механизму есть чем заниматься (список не пуст
    // или периодический обход заведён — ArmPeriodicMaintenance). Таймер один на процесс.
    private static void EnsureDeferredTimer()
    {
        if (_deferredTimer is not null) return;
        if (!_periodicArmed && _deferredStops.Count == 0) return;
        _deferredTimer = new Timer(_ =>
        {
            try
            {
                RetryDeferredStops();
                ScopeOrphanSweeper.OnPeriodicTick();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[exec] периодический обход не удался: {ex.GetType().Name}: {ex.Message}");
            }
        }, null, DeferredStopIntervalMs, DeferredStopIntervalMs);
    }

    private static void MaybeDisposeTimer()
    {
        if (_periodicArmed || _deferredStops.Count > 0) return;
        _deferredTimer?.Dispose();
        _deferredTimer = null;
    }

    // Завести периодический механизм: зовёт ScopeOrphanSweeper.Sweep при старте бэкенда —
    // сироты с тех пор видны не только на следующем старте.
    internal static void ArmPeriodicMaintenance()
    {
        lock (_deferredLock)
        {
            _periodicArmed = true;
            EnsureDeferredTimer();
        }
    }

    // Сброс состояния отложенных: только для тестов (как ResetProbeForTests — прогон не
    // должен нести чужой список, чужой таймер или заведённый обход дальше своего теста).
    internal static void ResetDeferredForTests()
    {
        lock (_deferredLock)
        {
            _deferredStops.Clear();
            _periodicArmed = false;
            _deferredTimer?.Dispose();
            _deferredTimer = null;
        }
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

    // Выполняет пробу один раз за процесс и возвращает кэш; frozen-отказ возвращает без
    // кэша (ниже); null — проба не нужна:
    // изоляция выключена (или Windows) или systemd-run не найден — в этих случаях обёртки
    // не будет и без пробы (BuildStartInfo сам fail-open'ит). Для spec с RawArguments Start
    // тоже вызывает пробу: сам этот запуск решит fail-open на RawArguments, но следующий
    // запуск без RawArguments получит уже кэшированный результат, а не новую пробу.
    //
    // Имя юнита пробе выдаётся СВЕЖЕЕ (NewScopeUnitName), а не заглушка ScopeUnitPlaceholder:
    // проба обязана быть той же формы, что боевой запуск (иначе она проверяет не то, что
    // потом поедет), а фиксированное имя столкнулось бы с ещё живым юнитом соседнего
    // инстанса — systemd вернул бы «unit already exists», и мы бы решили, что обёртка мертва.
    internal static SystemdRunProbeResult? ProbeOnce(IsolationOptions options, string? systemdRunPath)
    {
        if (!options.Enabled || string.IsNullOrEmpty(systemdRunPath)) return null;
        lock (_probeLock)
        {
            if (_probeResult is not null) return _probeResult;
            var result = Probe(systemdRunPath, WrapperArgs(options, NewScopeUnitName()), "true", ProbeTimeoutMs);
            // Frozen-отказ — не приговор обёртке, а состояние среза на окно (бенч): его не
            // кэшируем, иначе вся оставшаяся жизнь процесса после одной заморозки жила бы
            // fail-open без изоляции. И не пропускаем его в fail-open: Start по такому
            // результату отказывает запуск (IsolationUnavailableException), а не ведёт
            // процесс в cgroup прода. Обычная проба возможности (битая шина, нет прав на
            // slice) кэшируется, как раньше.
            if (result is { Ok: false } && IsolationUnavailableException.IsFrozenUnitRefusal(result.FailReason))
                return result;
            _probeResult = result;
            return result;
        }
    }

    // Случайная причина fail-open — frozen-отказ пробы (гонка: срез замёрз после прямой
    // проверки SliceIsFrozen). Только проба даёт такую причину — другие fail-open причины
    // (RawArguments, user-шина, отсутствие systemd-run) заморозку не упоминают.
    private static bool IsFrozenProbeReason(string? reason) =>
        reason is not null && reason.StartsWith("проба systemd-run")
        && IsolationUnavailableException.IsFrozenUnitRefusal(reason);

    // Фактически выбранный slice запуска — то же правило, что у WrapperArgs: локальный ход
    // в LocalModelSlice (если задан), остальные в общий Slice.
    internal static string EffectiveSlice(IsolationOptions options, bool localModel) =>
        localModel && !string.IsNullOrWhiteSpace(options.LocalModelSlice)
            ? options.LocalModelSlice
            : options.Slice;

    // Шов проверки заморозки: реальная — CgroupSlices.IsFrozen (отсутствие файла/сбой
    // чтения — false, прежнее fail-open поведение: ход пойдёт как раньше). Тесты подменяют
    // стабом, чтобы не зависеть от настоящего cgroupfs машины прогона.
    internal static Func<string, bool> SliceIsFrozen { get; set; } = slice => CgroupSlices.IsFrozen(slice);

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
        foreach (var a in wrapperArgs)
            // --quiet глушит stderr systemd-run, а цели пробы — именно прочитать причину
            // отказа (замороженный slice, битая шина): с --quiet stderr пуст и различить их
            // нельзя. Флаг меняет только многословие вывода, не поведение обёртки.
            if (a != "--quiet") psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(command);

        var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                return new SystemdRunProbeResult(false, $"не удалось запустить {systemdRunPath}");
            if (process.WaitForExit(timeoutMs))
            {
                if (process.ExitCode == 0) return new SystemdRunProbeResult(true);
                // Причина отказа — в stderr: по одному «код 1» не отличить битую шину от
                // замороженного slice (первое кэшируется как fail-open, второе — нет,
                // см. ProbeOnce). stdout не читаем: `true` молчит. Чтение — с тем же
                // таймаутом: процесс уже вышел и обычно не держит трубу.
                var stderrTask = process.StandardError.ReadToEndAsync();
                var stderr = Task.WhenAny(stderrTask, Task.Delay(timeoutMs)).Result == stderrTask
                    ? stderrTask.Result.Trim()
                    : "";
                return new SystemdRunProbeResult(false, stderr.Length > 0
                    ? $"проба systemd-run завершилась с кодом {process.ExitCode}: {stderr}"
                    : $"проба systemd-run завершилась с кодом {process.ExitCode}");
            }
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
