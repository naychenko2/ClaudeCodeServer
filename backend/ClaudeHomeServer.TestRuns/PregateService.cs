using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.TestRuns;

// Настройки предгейта (секция Pregate). Набор проверок задаётся ТОЛЬКО конфигом: в коде ни
// одной зашитой команды — пустой набор значит «провервать нечего». Дефолты проекта лежат в
// appsettings.json, машинные правки — в appsettings.Local.json.
//
// Набор проверок — ОБЪЕКТ по имени проверки (Pregate:Checks:{"tsc": {…}, …}), а НЕ массив.
// Массив в конфигурации .NET склеивается по индексу: запись из appsettings.Local.json затирает
// базовую запись с тем же индексом и наследует её свойства (например WhenChanged) — базовая
// проверка молча выпадает. Объект склеивается по ключу: базовый набор сохраняется целиком,
// своя проверка добавляется к нему и несёт только свои свойства.
public sealed class PregateOptions
{
    public const string SectionName = "Pregate";
    public const string ChecksKey = "Checks";

    // Имена свойств проверки: по ним видно, где конфигурация разъехалась по «:», а где у
    // проверки просто есть вложенный объект (свойство объектом). Список берём из типа —
    // новое свойство проверки попадёт сюда само.
    private static readonly HashSet<string> OptionPropertyNames =
        typeof(PregateCheckOptions).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Собранный набор проверок в порядке прогона
    public IReadOnlyList<PregateCheckOptions> Checks { get; init; } = [];

    // Непустая строка — набор не собран (например секция задана массивом): предгейт обязан
    // отказать с этим текстом, а не молча прогонять неполный набор
    public string? Problem { get; init; }

    public static PregateOptions From(IConfiguration config)
    {
        var section = config.GetSection(SectionName).GetSection(ChecksKey);
        var checks = new List<PregateCheckOptions>();
        var problems = new List<string>();

        foreach (var child in section.GetChildren())
        {
            // Ключ массива — индекс («0», «1»): старая форма, в которой набор склеивается по
            // индексу. Имя проверки индексом не бывает.
            if (child.Key.All(char.IsDigit))
            {
                problems.Add($"«{child.Key}» — {SectionName}:{ChecksKey} задан МАССИВОМ, а набор проверок должен быть "
                    + "объектом по имени проверки (массив склеивается с базовым по индексу: своя проверка "
                    + "затирала бы базовую и наследовала её WhenChanged)");
                continue;
            }

            // Ключ с «:» конфигурация разъехала бы по пути («lint:design» → «lint» → «design»):
            // такая проверка осталась бы без Command. Ловим это здесь, а не красным прогоном.
            //
            // Признак — именно разъезд ИМЕНИ, а не «есть вложенный объект»: у разъехавшегося
            // элемента НЕТ ни одного собственного свойства проверки, а свойства проверки
            // (Command, Args…) лежат во вложенном объекте, который сам свойством проверки
            // не является. Вложенный объект у настоящей проверки (свойство объектом, например
            // «Env») живёт рядом с её собственными свойствами — такой набор собирается.
            var ownProperties = child.GetChildren().Where(c => OptionPropertyNames.Contains(c.Key)).ToList();
            if (ownProperties.Count == 0
                && child.GetChildren().Any(c => c.GetChildren().Any()
                    && !OptionPropertyNames.Contains(c.Key)
                    && c.GetChildren().Any(g => OptionPropertyNames.Contains(g.Key))))
            {
                problems.Add($"«{child.Key}» — вложенные ключи в {SectionName}:{ChecksKey}: имя проверки не может "
                    + "содержать «:» (например, «lint:design» → ключ «lint-design», а Name: \"lint:design\")");
                continue;
            }

            var options = child.Get<PregateCheckOptions>() ?? new PregateCheckOptions();
            // Отображаемое имя: из конфига, иначе ключ
            checks.Add(options with { Name = string.IsNullOrWhiteSpace(options.Name) ? child.Key : options.Name });
        }

        if (problems.Count > 0)
            return new PregateOptions { Problem = string.Join("; ", problems) };

        // Порядок: заданный Order, затем имя. Без Order проверка уходит в конец — локальная
        // добавка не задвигает базовый набор.
        return new PregateOptions
        {
            Checks = [.. checks.OrderBy(c => c.Order).ThenBy(c => c.Name, StringComparer.Ordinal)],
        };
    }
}

// Одна проверка. Kind = "process" (по умолчанию): Command + Args в WorkingDirectory (относительно
// корня дерева), красная при коде выхода ≠ 0. Kind = "ui-probe": замер интерфейса уже поднятого
// стенда Origin (UiProbeService), красная при ненулевых находках.
public sealed record PregateCheckOptions
{
    public const string KindProcess = "process";
    public const string KindUiProbe = "ui-probe";

    // Отображаемое имя проверки: в конфиге необязательно (по умолчанию — ключ секции)
    public string Name { get; init; } = "";

    // Порядок прогона; без Order — в конец
    public int Order { get; init; } = int.MaxValue;

    public string Kind { get; init; } = KindProcess;

    // Префиксы путей от корня дерева (через «/»): проверка идёт, только если изменён файл под
    // одним из них. Пусто — всегда
    public List<string> WhenChanged { get; init; } = [];

    public string Command { get; init; } = "";
    public List<string> Args { get; init; } = [];
    public string WorkingDirectory { get; init; } = ".";

    // ui-probe: адрес стенда, маршрут, ширины (пусто — 360 и 1280), каталог с node_modules playwright
    public string Origin { get; init; } = "";
    public string Path { get; init; } = "/";
    public List<int> Viewports { get; init; } = [];
    public string Directory { get; init; } = ".";
}

// Предгейт шага (шов ITaskPregate): проверки из конфига идут по очереди под одной блокировкой
// дерева, каждая со своим потолком и слотом очереди сборок (BuildConcurrencyGate), как
// `dotnet build`/`npm run` у DotnetBuildService и NpmBuildService. Модели нет: вердикт —
// код выхода и строки с предупреждением или ошибкой из вывода.
public sealed class PregateService : ITaskPregate
{
    // Сколько строк проблемы класть в Detail красной проверки и сколько символов в строке
    private const int DetailLines = 10;
    private const int LineLimit = 300;

    private readonly ILauncherFactory _launchers;
    private readonly TestRunsOptions _testRunsOptions;
    private readonly PregateOptions _options;
    private readonly PhasePipeline _pipeline;
    private readonly UiProbeService? _uiProbe;

    public PregateService(ILauncherFactory launchers, TestRunsOptions testRunsOptions, PregateOptions options,
        PhasePipeline pipeline, UiProbeService? uiProbe = null)
    {
        _launchers = launchers;
        _testRunsOptions = testRunsOptions;
        _options = options;
        _pipeline = pipeline;
        _uiProbe = uiProbe;
    }

    // Потолок прогона объявляем наружу: потребитель (TaskPregateService) ставит его ДО своих
    // git-проверок дерева, чтобы бюджет хода (MCP_TOOL_TIMEOUT) считался целиком
    public int CeilingSeconds => _testRunsOptions.CeilingSeconds;

    public async Task<PregateVerdict> RunAsync(PregateRequest request, CancellationToken ct)
    {
        // Набор не собран (секция задана массивом) — прогонять нечего: отказ с текстом, а не
        // молчаливый зелёный вердикт по неполному набору
        if (_options.Problem is not null)
            return PregateVerdict.Refused("Набор проверок предгейта не собран: " + _options.Problem);

        if (_options.Checks.Count == 0) return new PregateVerdict([]);

        using var treeLock = _pipeline.TryLockTree(request.WorkingDirectory);
        if (treeLock is null)
            return PregateVerdict.Refused("В этом дереве уже идёт сборка или прогон тестов — предгейт не начат."
                + PhasePipeline.BusyWaitHint);

        var results = new List<PregateCheckResult>();
        // Потолок — один на весь прогон (очередь сборок входит в него, как у run_tests): сумма проверок
        // обязана укладываться в MCP_TOOL_TIMEOUT хода, иначе CLI отваливается, а прогон держит замок дерева
        using var limit = PhasePipeline.Ceiling(_testRunsOptions.CeilingSeconds, ct);
        foreach (var check in _options.Checks)
        {
            if (!Applies(check, request.ChangedFiles))
            {
                results.Add(new PregateCheckResult(check.Name, PregateStatus.Skipped, "изменения не касаются проверки"));
                continue;
            }

            PregateCheckResult? result;
            try
            {
                result = check.Kind == PregateCheckOptions.KindUiProbe
                    ? await RunUiProbeAsync(check, request, limit.Token)
                    : await RunProcessAsync(check, request, limit.Token);
            }
            catch (OperationCanceledException)
            {
                result = null;
            }
            if (result is null)
                return PregateVerdict.Refused(ct.IsCancellationRequested
                    ? "Предгейт остановлен."
                    : $"Проверка «{check.Name}» не уложилась в общий потолок предгейта {_testRunsOptions.CeilingSeconds} с (вместе с очередью сборок). "
                      + Progress(results),
                    results);
            results.Add(result);
        }
        return new PregateVerdict(results);
    }

    // Пропущенные проверки (не касаются изменений) не выполнялись — выполненными не называются
    private static string Progress(IReadOnlyList<PregateCheckResult> results)
    {
        var done = results.Where(r => r.Status != PregateStatus.Skipped).ToList();
        var skipped = results.Count - done.Count;
        var text = done.Count == 0
            ? "Выполненных проверок нет."
            : "Уже выполнено: " + string.Join(", ", done.Select(r => $"{r.Name} — {r.Status}")) + ".";
        return skipped == 0 ? text : text + $" Пропущено как не касающихся изменений: {skipped}.";
    }

    private static bool Applies(PregateCheckOptions check, IReadOnlyCollection<string>? changed) =>
        check.WhenChanged.Count == 0 || changed is null
        || changed.Any(f => check.WhenChanged.Any(p => f.Replace('\\', '/').StartsWith(p, StringComparison.Ordinal)));

    // null — оборвано (отмена или потолок)
    private async Task<PregateCheckResult?> RunProcessAsync(PregateCheckOptions check, PregateRequest request,
        CancellationToken limit)
    {
        if (string.IsNullOrWhiteSpace(check.Command))
            return new PregateCheckResult(check.Name, PregateStatus.Red, "в конфиге Pregate не задан Command");
        string workDir;
        try { workDir = SafePath.Join(request.WorkingDirectory, check.WorkingDirectory); }
        catch (UnauthorizedAccessException)
        {
            return new PregateCheckResult(check.Name, PregateStatus.Red, "WorkingDirectory выходит за пределы дерева");
        }

        var turnId = ProcessTurnIds.New();
        var spec = new ProcessSpec
        {
            FileName = check.Command,
            Args = check.Args,
            WorkingDirectory = TestRunService.UpperDriveLetter(workDir),
            Env = new Dictionary<string, string>
            {
                // Разбору нужен английский вывод: «error CS…», «warning CS…»
                ["DOTNET_CLI_UI_LANGUAGE"] = "en",
                ["DOTNET_NOLOGO"] = "1",
            },
            RedirectStdin = false,
            Heavy = true,
            PrivateBuildNodes = true,
            Track = true,
            TurnId = turnId,
            SessionId = request.SessionId,
        };
        using var slot = await _pipeline.AcquireAsync(spec, _ => { }, limit);
        if (slot is null) return null;

        var problems = new List<string>();
        var tail = new Queue<string>();
        var gate = new object();
        void OnLine(string line)
        {
            var cut = line.Length > LineLimit ? line[..LineLimit] : line;
            lock (gate)
            {
                tail.Enqueue(cut);
                while (tail.Count > DetailLines) tail.Dequeue();
                if (problems.Count < DetailLines && IsProblem(line)) problems.Add(cut);
            }
        }

        var outcome = await _pipeline.RunProcessAsync(_launchers.ForProject(request.Project), spec, OnLine, limit);
        if (outcome.Aborted) return null;
        if (outcome.ExitCode == 0) return new PregateCheckResult(check.Name, PregateStatus.Green);
        lock (gate)
        {
            var lines = problems.Count > 0 ? problems : [.. tail];
            var head = outcome.ExitCode is { } code ? $"код выхода {code}" : "процесс не вернул код выхода";
            return new PregateCheckResult(check.Name, PregateStatus.Red, head + "\n" + string.Join('\n', lines));
        }
    }

    // Строка с диагностикой компилятора или линтера: «warning CS8618», «error TS2322», «: error »
    private static bool IsProblem(string line) =>
        line.Contains("warning ", StringComparison.OrdinalIgnoreCase)
        || line.Contains("error ", StringComparison.OrdinalIgnoreCase)
        || line.Contains("error:", StringComparison.OrdinalIgnoreCase);

    private async Task<PregateCheckResult?> RunUiProbeAsync(PregateCheckOptions check, PregateRequest request,
        CancellationToken limit)
    {
        if (request.UiRoutes is not { Count: > 0 } routes)
            return new PregateCheckResult(check.Name, PregateStatus.NotRun,
                "маршрут не задан: добавь в постановку задачи строку «UI-маршруты: /#/страница, …»");
        if (_uiProbe is null)
            return new PregateCheckResult(check.Name, PregateStatus.NotRun, "замер интерфейса недоступен");
        if (!Uri.TryCreate(check.Origin, UriKind.Absolute, out _))
            return new PregateCheckResult(check.Name, PregateStatus.NotRun, "адрес стенда (Origin) не задан в конфиге Pregate");
        string directory;
        try { directory = SafePath.Join(request.WorkingDirectory, check.Directory); }
        catch (UnauthorizedAccessException)
        {
            return new PregateCheckResult(check.Name, PregateStatus.Red, "Directory выходит за пределы дерева");
        }

        var viewports = check.Viewports.Count > 0 ? check.Viewports : [.. UiProbeReport.DefaultViewports];
        var found = new List<string>();
        foreach (var route in routes)
        {
            var probe = await _uiProbe.RunAsync(new UiProbeRequest(request.Project, request.WorkingDirectory, directory,
                request.SessionId, check.Origin, route, viewports, UiProbeReport.DefaultMinTargetPx, null, null, null),
                progress: null, limit);
            if (probe.Cancelled || probe.TimedOut) return null;
            // Нет стенда — причина вне кода шага: не красное, но и не замер (NotRun)
            if (probe.Refusal is { } refusal) return new PregateCheckResult(check.Name, PregateStatus.NotRun, refusal);
            if (probe.Json is not { } json)
                return new PregateCheckResult(check.Name, PregateStatus.Red, $"{route}: " + (probe.Failure ?? "замер не состоялся"));

            var totals = JsonNode.Parse(json)?["totals"] as JsonObject;
            found.AddRange(totals?.Where(kv => kv.Value is JsonValue v && v.TryGetValue<int>(out var n) && n > 0)
                .Select(kv => $"{route} {kv.Key}: {kv.Value}") ?? []);
        }
        return found.Count == 0
            ? new PregateCheckResult(check.Name, PregateStatus.Green)
            : new PregateCheckResult(check.Name, PregateStatus.Red, "находки ui_probe — " + string.Join(", ", found));
    }
}
