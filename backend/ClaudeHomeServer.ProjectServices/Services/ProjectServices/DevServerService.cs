using System.Collections.Concurrent;
using System.Diagnostics;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.ProjectServices;

/// <summary>Экземпляр запущенного сервиса проекта (один процесс).</summary>
internal sealed class DevServerInstance : IDisposable
{
    public string ProjectId { get; }
    public string ServiceId { get; }
    public string Name { get; set; }
    public Process Process { get; set; }
    public string UserId { get; }
    public int Port { get; set; }
    public string Status { get; set; } = "starting"; // starting | started | stopped | error
    public string? Error { get; set; }
    public DateTime LastActivity { get; set; }

    // Адреса, объявленные Kestrel строками «Now listening on:», и признак «Application
    // started» (после него все адреса уже названы). По ним видно, что dotnet-приложение
    // слушает не навязанный порт, — без 120 с ожидания впустую.
    private readonly ConcurrentQueue<int> _announced = new();
    public IReadOnlyCollection<int> AnnouncedPorts => _announced.ToArray();
    public bool AppStarted { get; private set; }

    public void ObserveLine(string line)
    {
        if (DevServerLaunchPolicy.ParseNowListening(line) is { } p) _announced.Enqueue(p);
        else if (DevServerLaunchPolicy.IsApplicationStarted(line)) AppStarted = true;
    }

    // Вывод (stdout+stderr) живёт ровно столько, сколько инстанс в реестре, на диск не
    // пишется. Служит двум целям: реплей вкладке «Логи» при подписке и хвост для текста
    // ошибки, когда сервис не поднялся.
    private const int ErrorTailLines = 40;
    private readonly OutputRingBuffer _output = new();

    // Очередь на рассылку. Строки не уходят подписчикам поштучно: `dotnet build` и
    // `vite` печатают их тысячами, и каждая означала бы отдельное сообщение SignalR
    // и отдельный write в xterm. Копим и отдаём тиком раз в LogFlushMs.
    private readonly object _pendingLock = new();
    private readonly System.Text.StringBuilder _pending = new();

    public void AppendOutput(string chunk)
    {
        _output.Append(chunk);
        lock (_pendingLock) _pending.Append(chunk);
    }

    /// <summary>Забрать накопленное на рассылку. Пусто — null.</summary>
    public string? TakePending()
    {
        lock (_pendingLock)
        {
            if (_pending.Length == 0) return null;
            var text = _pending.ToString();
            _pending.Clear();
            return text;
        }
    }

    public string GetBufferedOutput() => _output.GetAll();

    /// <summary>Последние строки вывода — текст ошибки, когда сервис не начал слушать порт.</summary>
    public string OutputTail() => _output.TailLines(ErrorTailLines);

    // Драйвер среды, запустивший процесс + метка хода: в песочнице убить процесс
    // может только он (Kill docker-клиента не трогает процесс в контейнере)
    private readonly Execution.IProcessLauncher _launcher;
    private readonly string _turnId;

    public DevServerInstance(string projectId, string serviceId, string name, Process process, string userId,
        Execution.IProcessLauncher launcher, string turnId)
    {
        ProjectId = projectId;
        ServiceId = serviceId;
        Name = name;
        Process = process;
        UserId = userId;
        LastActivity = DateTime.UtcNow;
        _launcher = launcher;
        _turnId = turnId;
    }

    public void Dispose()
    {
        if (!Process.HasExited)
        {
            _launcher.Kill(Process, _turnId);
            Process.WaitForExit(5000);
        }
        Process.Dispose();
    }
}

/// <summary>Одна запущенная запись сервиса (для отдачи фронту).</summary>
public record RunningServiceInfo(string ServiceId, string Name, int? Port, string Status, string? Error);

/// <summary>
/// Менеджер сервисов Preview: несколько процессов на проект (ключ = projectId:serviceId).
/// Держит «активный для превью» сервис на проект — на его порт указывает iframe-прокси.
/// </summary>
public sealed class DevServerService : IDisposable
{
    private readonly ConcurrentDictionary<string, DevServerInstance> _servers = new();
    // projectId → serviceId активного для превью сервиса
    private readonly ConcurrentDictionary<string, string> _activePreview = new();
    // projectId → сервис, запущенный ВНЕ продукта (Rider, терминал), выбранный для превью.
    // Процесс не наш: остановить его нельзя и логов у него нет — только проксируем порт.
    private readonly ConcurrentDictionary<string, (string ServiceId, int Port)> _externalPreview = new();
    private readonly DevServerPortMemory _portMemory;
    private readonly IProjectManager _projects;
    private readonly ISessionBroadcaster _broadcaster;
    private readonly ILogger<DevServerService> _log;
    private readonly Execution.ILauncherFactory _launchers;
    private readonly Execution.ISandboxPortRange _sandbox;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Timer _cleanupTimer;
    private readonly Timer _logFlushTimer;
    // Тик рассылки логов. 100 мс: глазу неотличимо от мгновенного, а поток из тысяч
    // строк схлопывается в десяток сообщений в секунду вместо тысяч.
    private const int LogFlushMs = 100;

    // Автопорты, выданные запуску, но ещё не записанные в его инстанс (процесс стартует).
    // Без резерва два параллельных запуска видели один и тот же свободный порт.
    private readonly object _portLock = new();
    private readonly HashSet<int> _reservedPorts = [];

    public DevServerService(IProjectManager projects, ISessionBroadcaster broadcaster, ILogger<DevServerService> log,
        Execution.ILauncherFactory launchers, Execution.ISandboxPortRange sandbox, DevServerPortMemory portMemory)
    {
        _portMemory = portMemory;
        _projects = projects;
        _broadcaster = broadcaster;
        _log = log;
        _launchers = launchers;
        _sandbox = sandbox;
        _cleanupTimer = new Timer(_ => CleanupStale(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        _logFlushTimer = new Timer(_ => FlushLogs(), null,
            TimeSpan.FromMilliseconds(LogFlushMs), TimeSpan.FromMilliseconds(LogFlushMs));
    }

    /// <summary>
    /// Выдать автопорт и зарезервировать его до <see cref="ReleasePort"/>. В песочнице — из
    /// опубликованного пула (порты проброшены на хост, preview-форвардер идёт на
    /// 127.0.0.1:{этот порт}), на хосте — из 55xx–56xx мимо всего, что слушается в системе.
    /// Выбор и резерв под одним локом: иначе параллельные запуски получали один порт.
    /// null — свободных нет.
    /// </summary>
    internal int? ReserveAutoPort(bool sandboxed)
    {
        lock (_portLock)
        {
            var used = _servers.Values.Where(s => s.Port > 0).Select(s => s.Port)
                .Concat(_reservedPorts).ToHashSet();
            int? picked = null;
            if (sandboxed)
            {
                var start = _sandbox.PortRangeStart;
                for (var p = start; p < start + _sandbox.PortRangeSize && picked is null; p++)
                    if (!used.Contains(p)) picked = p;
            }
            else
                picked = DevServerLaunchPolicy.PickAutoPort(used.Concat(DevServerLaunchPolicy.SystemListeningPorts()));
            if (picked is { } port) _reservedPorts.Add(port);
            return picked;
        }
    }

    /// <summary>Снять резерв автопорта: порт уже записан в инстанс или запуск не состоялся.</summary>
    internal void ReleasePort(int port)
    {
        lock (_portLock) _reservedPorts.Remove(port);
    }

    private static string Key(string projectId, string serviceId) => projectId + ":" + serviceId;

    /// <summary>
    /// Запустить сервис. Уже запущен — возвращаем его порт и делаем активным для превью.
    /// <paramref name="readyTimeout"/> — сколько ждать, пока сервис начнёт слушать порт
    /// (по умолчанию <see cref="DevServerLaunchPolicy.DefaultReadyTimeout"/>, с запасом на
    /// холодную сборку `dotnet run`).
    /// </summary>
    public async Task<DevServerStartResult> StartAsync(string projectId, string userId, string serviceId,
        string name, string command, string[] args, string? cwd = null,
        int? port = null, bool autoPort = false, Dictionary<string, string>? env = null,
        TimeSpan? readyTimeout = null)
    {
        // Боевой инстанс и Dify сервису не отдаём ни при каком конфиге — отказ до запуска
        if (port is { } requested && DevServerLaunchPolicy.IsForbidden(requested))
            return new DevServerStartResult(false, null, "error", DevServerLaunchPolicy.ForbiddenReason(requested));

        var key = Key(projectId, serviceId);
        if (_servers.TryGetValue(key, out var existing))
        {
            if (existing.Status == "started")
            {
                SetActivePreview(projectId, serviceId);
                return new DevServerStartResult(true, existing.Port, "started");
            }
            if (existing.Status == "starting")
                return new DevServerStartResult(true, null, "starting");
            _servers.TryRemove(key, out _);
            existing.Dispose();
        }

        var project = _projects.GetById(projectId);
        if (project is null)
            return new DevServerStartResult(false, null, "error", "Проект не найден");

        string workingDir;
        try
        {
            workingDir = string.IsNullOrWhiteSpace(cwd)
                ? project.RootPath
                // Защита пути через Core-примитив SafePath.Join: ссылка на FileService
                // — это ссылка на чужую вертикаль, сторож границ её ловит.
                : SafePath.Join(project.RootPath, cwd);
        }
        catch (UnauthorizedAccessException)
        {
            return new DevServerStartResult(false, null, "error", "Недопустимый рабочий каталог");
        }

        var launcher = _launchers.ForProject(project);
        var envVars = new Dictionary<string, string>();
        if (env != null)
            foreach (var (k, v) in env) envVars[k] = v;

        // autoPort без явного порта → берём свободный из 55xx–56xx. Явный/авто-порт навязываем
        // сервису: dotnet-приложению — аргументом `--urls` (env перебивают launchSettings.json и
        // `Urls` в appsettings), остальным — через окружение (PORT для Node-фреймворков).
        // В песочнице порт обязан быть из опубликованного пула (иначе он не проброшен на хост
        // и preview-форвардер на 127.0.0.1 не достучится); хостовый порт не подходит.
        int? fixedPort = port;
        var autoPicked = (launcher.IsSandboxed && (fixedPort is null || autoPort)) || (!fixedPort.HasValue && autoPort);
        if (autoPicked)
        {
            fixedPort = ReserveAutoPort(launcher.IsSandboxed);
            if (fixedPort is null)
                return new DevServerStartResult(false, null, "error", launcher.IsSandboxed
                    ? "Исчерпан пул preview-портов песочницы — остановите неиспользуемые dev-серверы или увеличьте Sandbox:PortRangeSize"
                    : $"Нет свободного порта в диапазоне {DevServerLaunchPolicy.AutoPortFirst}–{DevServerLaunchPolicy.AutoPortLast}.");
        }

        DevServerInstance instance;
        Process process;
        // Резерв автопорта держим, пока порт не окажется в инстансе: с этого момента его
        // видят все следующие выборы через реестр. Не запустилось — резерв снимается тоже.
        try
        {
            if (fixedPort.HasValue)
            {
                envVars["PORT"] = fixedPort.Value.ToString();
                // Наследованный ASPNETCORE_URLS процесса бэкенда не перебиваем (историческое поведение)
                if (!envVars.ContainsKey("ASPNETCORE_URLS")
                    && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
                    envVars["ASPNETCORE_URLS"] = $"http://localhost:{fixedPort.Value}";
                args = DevServerLaunchPolicy.BuildArgs(command, args,
                    DevServerLaunchPolicy.ListenUrl(fixedPort.Value, launcher.IsSandboxed));
            }

            var turnId = Guid.NewGuid().ToString("N")[..12];
            try
            {
                process = launcher.Start(new Execution.ProcessSpec
                {
                    FileName = command,
                    Args = args,
                    WorkingDirectory = workingDir,
                    Env = envVars,
                    RedirectStdin = false,
                    EnableRaisingEvents = true,
                    TurnId = turnId,
                });
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Не удалось запустить сервис {ServiceId} ({Command})", serviceId, command);
                return new DevServerStartResult(false, null, "error", $"Не удалось запустить: {ex.Message}");
            }

            instance = new DevServerInstance(projectId, serviceId, name, process, userId, launcher, turnId);
            // Известный порт (из конфига/launchSettings) фиксируем до попадания в реестр, но
            // «started» ставим ТОЛЬКО когда приложение реально слушает порт — иначе iframe
            // грузится в мёртвый порт (502/пустая страница).
            if (fixedPort.HasValue) instance.Port = fixedPort.Value;
            _servers[key] = instance;
        }
        finally
        {
            if (autoPicked && fixedPort is { } reserved) ReleasePort(reserved);
        }
        process.Exited += (_, _) => OnExited(key);

        // Всегда дренируем оба потока (иначе буфер переполнится и процесс зависнет);
        // попутно детектим порт, если он не задан.
        _ = DrainStreams(process, instance);

        // Ждём: порт известен (fixed или из stdout) И реально принимает соединения.
        // `dotnet run` собирает внутри ожидания — ему потолок больше.
        var readyWait = readyTimeout ?? DevServerLaunchPolicy.ReadyTimeoutFor(command, args);
        var deadline = DateTime.UtcNow + readyWait;
        string? mismatch = null;
        while (DateTime.UtcNow < deadline)
        {
            // Наш инстанс могли убрать из реестра, пока мы ждём: уборщик CleanupStale
            // (тик раз в минуту), повторный запуск того же сервиса или остановка. Тогда
            // его процесс уже освобождён, и ждать нечего — иначе дальше по циклу мы
            // трогаем чужой/мёртвый объект и перезаписываем статус победившего запуска.
            if (!_servers.TryGetValue(key, out var current) || !ReferenceEquals(current, instance))
                break;
            if (SafeHasExited(process)) break;
            // Порт, пойманный из вывода, проходит тот же запрет, что и заданный явно: иначе
            // сервис с `--port 8080` в своих аргументах стал бы «started», а превью — туннелем
            // в Dify или бой. Проверка ДО пробы соединения: тот порт как раз слушается.
            // Порт читаем один раз на итерацию: его пишет дренаж вывода из другого потока, и
            // проверка запрета обязана касаться ровно того порта, что уйдёт в пробу и в реестр.
            var seenPort = instance.Port;
            if (seenPort != 0 && DevServerLaunchPolicy.IsForbidden(seenPort))
            {
                mismatch = DevServerLaunchPolicy.ForbiddenReason(seenPort);
                break;
            }
            if (seenPort != 0 && await LoopbackResolver.IsListeningAsync(seenPort))
            {
                instance.Status = "started";
                // Порт запоминаем именно здесь: он уже проверен соединением, и это
                // единственный момент, когда мы точно знаем, где сервис слушает. После
                // перезапуска продукта реестр процессов пуст, и опознать живой сервис
                // можно будет только по этой записи (см. DevServerPortMemory)
                // PID нужен, чтобы после перезапуска продукта отличить свой осиротевший
                // процесс от постороннего, занявшего тот же порт
                _portMemory.Remember(projectId, serviceId, seenPort, SafePid(process));
                SetActivePreview(projectId, serviceId);
                await BroadcastStatus(projectId, serviceId, "started", seenPort);
                return new DevServerStartResult(true, seenPort, "started");
            }
            // Приложение назвало свои адреса, и навязанного среди них нет — честный отказ сразу
            if (fixedPort.HasValue
                && DevServerLaunchPolicy.PortMismatch(fixedPort.Value, instance.AnnouncedPorts, instance.AppStarted) is { } m)
            {
                mismatch = m;
                break;
            }
            await Task.Delay(500);
        }

        // Не поднялся: честная ошибка с хвостом вывода процесса.
        var exited = SafeHasExited(process);
        var exitCode = exited ? SafeExitCode(process) : -1;
        var tail = instance.OutputTail();
        // Занятый порт — самая частая причина: тот же сервис уже поднят снаружи (Rider,
        // терминал, второй инстанс продукта). Голый хвост лога об этом не говорит.
        var reason = mismatch
            ?? (DevServerLaunchPolicy.LooksLikePortInUse(tail) && instance.Port > 0
                ? $"Порт {instance.Port} уже занят — возможно, сервис запущен снаружи."
                : exited ? $"Процесс завершился с кодом {exitCode}."
                : DevServerLaunchPolicy.ReadyTimeoutReason(readyWait,
                    builds: DevServerLaunchPolicy.ReadyTimeoutFor(command, args) == DevServerLaunchPolicy.DotnetRunReadyTimeout));
        instance.Status = "error";
        instance.Error = string.IsNullOrWhiteSpace(tail) ? reason : reason + "\n" + tail;
        // Удаляем ТОЛЬКО свой инстанс: за время ожидания под этим ключом мог оказаться
        // другой запуск (повторный клик, перезапуск после уборки). Простой TryRemove(key)
        // снёс бы живой сервис соседа и оставил его процесс без присмотра.
        _servers.TryRemove(new KeyValuePair<string, DevServerInstance>(key, instance));
        instance.Dispose();
        await BroadcastStatus(projectId, serviceId, "error", null, instance.Error);
        return new DevServerStartResult(false, null, "error", instance.Error);
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    /// <summary>
    /// Безопасная проверка «процесс завершился».
    ///
    /// Объект <see cref="Process"/> могли освободить параллельно: уборщик
    /// <c>CleanupStale</c>, повторный запуск того же сервиса или остановка — все они
    /// зовут <c>Dispose</c>. После этого <c>HasExited</c> бросает
    /// «No process is associated with this object», и падал весь запрос.
    ///
    /// Ловилось это регулярно, потому что с проектом работают ДВА инстанса продукта:
    /// они делят папки и порты, второй дев-сервер падает сразу («порт занят»), и
    /// уборщик успевает освободить процесс прямо посреди ожидания в StartAsync.
    ///
    /// Для вызывающего освобождённый процесс неотличим от завершённого — отвечаем true.
    /// </summary>
    /// <summary>PID процесса, либо 0 — объект мог быть уже освобождён.</summary>
    private static int SafePid(System.Diagnostics.Process p)
    {
        try { return p.Id; }
        catch (InvalidOperationException) { return 0; }
    }

    private static bool SafeHasExited(Process p)
    {
        // ObjectDisposedException наследует InvalidOperationException — одного catch хватает
        // на оба случая: «процесс не запускался» и «объект уже освобождён».
        try { return p.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>Остановить сервис.</summary>
    public async Task StopAsync(string projectId, string userId, string serviceId)
    {
        // Погасили сами — значит порт свободен, и помнить его опасно: место мог занять
        // посторонний процесс, и сервис показался бы «поднятым снаружи» по чужому серверу
        _portMemory.Forget(projectId, serviceId);
        if (_servers.TryGetValue(Key(projectId, serviceId), out var instance))
        {
            if (instance.UserId != userId)
                throw new UnauthorizedAccessException("Доступ запрещён");
            // Хвост вывода (причина падения, прощальные строки) — до удаления инстанса:
            // после Dispose забирать его будет неоткуда
            await FlushLog(instance);
            _servers.TryRemove(Key(projectId, serviceId), out _);
            instance.Dispose();
            if (_activePreview.TryGetValue(projectId, out var active) && active == serviceId)
                _activePreview.TryRemove(projectId, out _);
            _log.LogInformation("Сервис {ServiceId} проекта {ProjectId} остановлен", serviceId, projectId);
            await BroadcastStatus(projectId, serviceId, "stopped", null);
        }
    }

    /// <summary>Назначить активный для превью сервис (на его порт указывает iframe-прокси).</summary>
    public void SetActivePreview(string projectId, string serviceId)
    {
        _activePreview[projectId] = serviceId;
        // Активный ровно один: свой процесс вытесняет выбранный внешний
        _externalPreview.TryRemove(projectId, out _);
    }

    /// <summary>
    /// Назначить для превью сервис, поднятый вне продукта. Порт сюда приходит уже
    /// проверенным вызывающим (см. PreviewController): он обязан быть портом сервиса
    /// ЭТОГО проекта, иначе прокси стал бы универсальным туннелем на любой локальный порт.
    /// </summary>
    /// <returns>false — порт запрещён (<see cref="DevServerLaunchPolicy.IsForbidden"/>), превью не назначено.</returns>
    public bool SetActiveExternal(string projectId, string serviceId, int port)
    {
        // Последний рубеж: какой бы путь ни привёл сюда порт боя или Dify, туннелем он не станет
        if (DevServerLaunchPolicy.IsForbidden(port)) return false;
        _externalPreview[projectId] = (serviceId, port);
        _activePreview.TryRemove(projectId, out _);
        return true;
    }

    /// <summary>Внешний сервис, выбранный для превью (для отдачи фронту).</summary>
    public (string ServiceId, int Port)? GetActiveExternal(string projectId) =>
        _externalPreview.TryGetValue(projectId, out var ext) ? ext : null;

    /// <summary>Порт активного для превью сервиса проекта. Владельца проверяет вызывающий
    /// (preview-middleware сверяет OwnerId по токену до вызова); фолбэк ограничен тем же projectId.</summary>
    public int? GetActivePreviewPort(string projectId)
    {
        // Последний рубеж прокси: порт боя или Dify туннелем не станет, каким бы путём ни
        // попал в реестр — запрещённый пропускаем, как будто его нет
        static bool Allowed(int port) => port > 0 && !DevServerLaunchPolicy.IsForbidden(port);

        if (_activePreview.TryGetValue(projectId, out var serviceId) &&
            _servers.TryGetValue(Key(projectId, serviceId), out var inst) &&
            inst.Status == "started" && inst.Port is var activePort && Allowed(activePort))
            return activePort;

        // Выбранный внешний процесс — он не в реестре, статуса и порта из него не узнать
        if (_externalPreview.TryGetValue(projectId, out var ext) && Allowed(ext.Port)) return ext.Port;

        // Фолбэк: первый запущенный сервис проекта, если активный не задан.
        var prefix = projectId + ":";
        foreach (var (k, v) in _servers)
            if (k.StartsWith(prefix, StringComparison.Ordinal) && v.Status == "started" && v.Port is var p && Allowed(p))
                return p;
        return null;
    }

    /// <summary>Список запущенных (и недавно упавших) сервисов проекта.</summary>
    public List<RunningServiceInfo> GetRunning(string projectId, string userId)
    {
        var prefix = projectId + ":";
        var list = new List<RunningServiceInfo>();
        foreach (var (k, inst) in _servers)
        {
            if (!k.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (inst.UserId != userId) continue;
            list.Add(new RunningServiceInfo(inst.ServiceId, inst.Name, inst.Port == 0 ? null : inst.Port, inst.Status, inst.Error));
        }
        return list;
    }

    /// <summary>
    /// Фактический порт ИМЕННО ЭТОГО запущенного сервиса, либо null.
    ///
    /// Нужен потому, что конфигурация порта может не знать: сервис стартует с автопортом
    /// или отдаёт порт в выводе, и в launch.json/манифесте его нет вовсе.
    ///
    /// В отличие от GetActivePreviewPort никаких фолбэков здесь нет намеренно: подстановка
    /// «какого-нибудь запущенного» увела бы внешнюю ссылку на посторонний процесс.
    /// </summary>
    public int? GetRunningPort(string projectId, string serviceId, string userId)
    {
        if (_servers.TryGetValue(Key(projectId, serviceId), out var inst)
            && inst.UserId == userId
            && inst.Status == "started"
            && inst.Port > 0)
            return inst.Port;
        return null;
    }

    /// <summary>Id активного для превью сервиса проекта (для владельца).</summary>
    public string? GetActiveServiceId(string projectId, string userId)
    {
        if (_activePreview.TryGetValue(projectId, out var serviceId) &&
            _servers.TryGetValue(Key(projectId, serviceId), out var inst) &&
            inst.UserId == userId)
            return serviceId;
        return null;
    }

    /// <summary>Остановить всё (при shutdown).</summary>
    public void ShutdownAll()
    {
        foreach (var (id, instance) in _servers)
        {
            _log.LogInformation("Shutdown: останов сервиса {ServiceId}", id);
            instance.Dispose();
        }
        _servers.Clear();
        _activePreview.Clear();
    }

    // Фоновая очистка зависших сервисов: процесс умер, но статус не обновился;
    // сервис в error висит бессрочно; старт не завершился за 5 мин.
    private void CleanupStale()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, instance) in _servers)
        {
            var idle = now - instance.LastActivity;
            var shouldClean = false;

            if (SafeHasExited(instance.Process) && instance.Status != "stopped")
            {
                shouldClean = true;
                _log.LogInformation("DevServer {ServiceId}: процесс завершился, а статус {Status} — очистка", key, instance.Status);
            }
            else if (instance.Status == "error" && idle.TotalMinutes >= 5)
            {
                shouldClean = true;
                _log.LogInformation("DevServer {ServiceId}: в статусе error {Idle:0} мин — очистка", key, idle.TotalMinutes);
            }
            else if (instance.Status == "starting" && idle.TotalMinutes >= 5)
            {
                shouldClean = true;
                _log.LogInformation("DevServer {ServiceId}: не запустился за {Idle:0} мин — очистка", key, idle.TotalMinutes);
            }

            if (shouldClean && _servers.TryRemove(key, out var removed))
            {
                removed.Dispose();
                if (_activePreview.TryGetValue(removed.ProjectId, out var active) && active == removed.ServiceId)
                    _activePreview.TryRemove(removed.ProjectId, out _);
                _ = BroadcastStatus(removed.ProjectId, removed.ServiceId, "stopped", null);
            }
        }
    }

    public void Dispose()
    {
        _shutdownCts.Cancel();
        _cleanupTimer.Dispose();
        _logFlushTimer.Dispose();
        foreach (var (_, instance) in _servers) instance.Dispose();
        _servers.Clear();
    }

    private void OnExited(string key)
    {
        if (_servers.TryGetValue(key, out var inst) && inst.Status == "started")
        {
            inst.Status = "stopped";
            _ = FlushLog(inst);   // последние строки процесса не должны пропасть
            if (_activePreview.TryGetValue(inst.ProjectId, out var active) && active == inst.ServiceId)
                _activePreview.TryRemove(inst.ProjectId, out _);
            _ = BroadcastStatus(inst.ProjectId, inst.ServiceId, "stopped", null);
        }
    }

    private async Task DrainStreams(Process process, DevServerInstance instance)
    {
        // stdout и stderr дренируются одинаково: оба идут в один буфер в порядке появления
        async Task Pump(TextReader reader)
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                instance.LastActivity = DateTime.UtcNow;
                // CRLF, а не LF: xterm переносит строку только по возврату каретки.
                // Подписчикам строка уйдёт тиком FlushLogs — дренаж не ждёт сети:
                // застопорится он, переполнится буфер процесса, и сервис повиснет.
                instance.AppendOutput(line + "\r\n");
                instance.ObserveLine(line);
                // Порт из вывода нужен только если он не задан заранее; готовность и запрет
                // 80/443/8080 проверит StartAsync.
                if (instance.Port != 0) continue;
                if (DevServerLaunchPolicy.PortFromOutput(line) is { } p) instance.Port = p;
            }
        }

        try
        {
            await Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Дренаж потоков сервиса {ServiceId} прерван", instance.ServiceId);
        }
    }

    /// <summary>
    /// Имя SignalR-группы подписчиков логов сервиса. Группа на КОНКРЕТНЫЙ сервис, а не на
    /// владельца: иначе вывод дев-сервера сыпался бы во все открытые вкладки пользователя.
    /// </summary>
    public static string LogGroup(string projectId, string serviceId) => $"preview_{projectId}:{serviceId}";

    /// <summary>
    /// Накопленный вывод сервиса — реплей новому подписчику. null, если сервис не запущен.
    /// </summary>
    public string? GetLogBuffer(string projectId, string serviceId, string userId)
    {
        if (!_servers.TryGetValue(Key(projectId, serviceId), out var inst)) return null;
        if (inst.UserId != userId) throw new UnauthorizedAccessException("Доступ запрещён");
        return inst.GetBufferedOutput();
    }

    /// <summary>Тик рассылки: у каждого живого сервиса забираем накопленное и шлём одним сообщением.</summary>
    private void FlushLogs()
    {
        foreach (var (_, instance) in _servers) _ = FlushLog(instance);
    }

    private async Task FlushLog(DevServerInstance instance)
    {
        var data = instance.TakePending();
        if (data is null) return;
        try
        {
            await _broadcaster.ToPreviewLog(instance.ProjectId, instance.ServiceId,
                new PreviewLogMessage(instance.ServiceId, data));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Не удалось разослать лог сервиса {ServiceId}", instance.ServiceId);
        }
    }

    private async Task BroadcastStatus(string projectId, string serviceId, string status, int? port, string? error = null)
    {
        try
        {
            var project = _projects.GetById(projectId);
            if (project is null || string.IsNullOrEmpty(project.OwnerId)) return;
            await _broadcaster.ToOwner(project.OwnerId,
                new PreviewStatusMessage(status, port, error, serviceId));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ошибка броадкаста статуса preview проекта {ProjectId}", projectId);
        }
    }
}

public record DevServerStartResult(bool Success, int? Port, string Status, string? Error = null);
