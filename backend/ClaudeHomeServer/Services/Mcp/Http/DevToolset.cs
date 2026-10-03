using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ProjectServices;
using ClaudeHomeServer.Services.TestRuns;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Сборка с прогрессом в чате (build: dotnet build / npm run, docs/research/build-stand-progress-2026-10.md,
/// этап 4) и дев-стенд (start_stand / stop_stand, этап 5 — <see cref="DevStand"/>). Отдельный сервер,
/// а не инструменты в tests: <c>mcp__tests__build</c> модель читала бы как «сборка тестов». Маршрут —
/// <c>POST /mcp/dev/{sessionId}</c>: хвост несёт СЕССИЮ-ВЫЗЫВАТЕЛЬ.
///
/// Вызов синхронный, как у run_tests: ответ — когда сборка кончилась, оборвана «Стопом» (обрыв
/// HTTP = отмена <c>ct</c>) или серверным потолком (540 с, меньше MCP_TOOL_TIMEOUT хода). Движки —
/// вертикаль TestRuns (общий конвейер фаз и блокировка дерева с тестами); склейка — здесь.
///
/// Гейты на КАЖДЫЙ вызов те же, что у run_tests (инструмент исполняет код репозитория): сессия
/// владельца токена, подсистема test-runs, чат проекта, проект на сервере (ADR-016 — локальный
/// в v1 не поддерживаем), персоне не запрещён Bash и она не ReadOnly. Отказы — текстом с IsError.
/// Ходы без человека не запрещены: исполнители собирают чаще всех.
/// ИНВАРИАНТ состава: tools/list зависит только от сессии-вызывателя, не от хода.
/// </summary>
public sealed class DevToolset(
    SessionManager sessions,
    ProjectManager projects,
    PersonaManager personas,
    // Из отключаемой вертикали TestRuns: выключена — инструмент честно отказывает
    DotnetBuildService? dotnet = null,
    NpmBuildService? npm = null,
    // Живые фазы сборки в ленту; нет — вызов работает без прогресса
    ISessionBroadcaster? broadcaster = null,
    // Реестр «Сервисов» и их конфигурация (вертикаль ProjectServices): стенд — запись того же
    // реестра, что у панели. Нет — стенд честно отказывает, сборка работает
    DevServerService? devServer = null,
    ProjectServiceDiscovery? discovery = null,
    TestRunsOptions? options = null) : IMcpParameterizedToolset
{
    public const string ServerName = McpEndpoints.DevName;
    public const string ToolName = "build";
    public const string StartStandName = "start_stand";
    public const string StopStandName = "stop_stand";

    internal const string LocalProjectReason =
        "Сборку локального проекта запускай Bash'ем на устройстве: build работает только с проектами на сервере.";

    private static readonly ProjectRunCaller.Texts Refusals = new(
        BadRoute: "Некорректный маршрут сервера сборки — вызов отклонён.",
        ProjectOnly: "Сборка работает только в чате проекта.",
        LocalProject: LocalProjectReason,
        ReadOnly: "Персона с доступом «Только чтение» не собирает: сборка пишет bin/obj, dist и файлы проекта.",
        NoBash: "Персоне запрещён Bash — значит, и сборка: build исполняет код проекта (задачи MSBuild, скрипты npm).");

    // Стенд исполняет код проекта (сборка и сам процесс) — гейты те же, тексты свои
    private static readonly ProjectRunCaller.Texts StandRefusals = new(
        BadRoute: Refusals.BadRoute,
        ProjectOnly: "Стенд поднимается только в чате проекта.",
        LocalProject: "Стенд локального проекта поднимай панелью «Сервисы»: start_stand работает только с проектами на сервере.",
        ReadOnly: "Персона с доступом «Только чтение» не поднимает стенд: сборка пишет bin/obj, а стенд исполняет код проекта.",
        NoBash: "Персоне запрещён Bash — значит, и стенд: он собирает и исполняет код проекта.");

    // ProjectRunCaller без состояния: у стенда свой экземпляр с теми же зависимостями
    private readonly ProjectRunCaller _caller = new(sessions, projects, personas, broadcaster);
    private readonly DevStand _stand = new(new ProjectRunCaller(sessions, projects, personas, broadcaster),
        devServer, discovery, dotnet, npm, (options ?? new TestRunsOptions()).CeilingSeconds);

    public string Name => ServerName;
    public string Version => "1.0.0";

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) =>
        _caller.TryResolveSession(context, Refusals, out _, out _) ? Tools : [];

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        if (tool is StartStandName or StopStandName) return await CallStandAsync(tool, arguments, context, ct);
        if (tool != ToolName) throw new ArgumentException($"Неизвестный инструмент: {tool}", nameof(tool));
        if (!_caller.TryResolveSession(context, Refusals, out var session, out var error))
            return ProjectRunCaller.Deny(error);
        if (dotnet is null || npm is null)
            return ProjectRunCaller.Deny("Сборка с прогрессом выключена на этом сервере — собирай Bash'ем.");
        if (!_caller.TryResolveProject(session, context.OwnerId, Refusals, out var project, out error))
            return ProjectRunCaller.Deny(error);

        var root = ProjectRunCaller.RootOf(session, project);
        if (!TryParseKind(ProjectRunCaller.StringArg(arguments, "kind"), out var isNpm, out error))
            return ProjectRunCaller.Deny(error);
        var script = ProjectRunCaller.StringArg(arguments, "script");
        if (!isNpm && script is not null)
            return ProjectRunCaller.Deny("script — только для kind=npm; у dotnet цель задаётся аргументом target.");
        script ??= "build";
        // Ведущий «-»/«@» регулярка имени уже не пропускает (первый символ — буква или цифра)
        if (isNpm && !NpmBuildService.IsScriptName(script))
            return ProjectRunCaller.Deny("script — имя скрипта из package.json: латинские буквы, цифры и «:_.-», "
                + "первым символом буква или цифра, без пробелов, «&», «;» и кавычек.");
        // Дев-сервер не завершается сам: сборка висела бы до потолка, держа блокировку дерева и
        // слот очереди сборок
        if (isNpm && ProjectServiceDiscovery.IsServerScript(script))
            return ProjectRunCaller.Deny($"Скрипт «{script}» поднимает сервер и сам не завершается — это не сборка. "
                + "Дев-стенд поднимай инструментом start_stand: он живёт после хода и виден в панели «Сервисы».");

        var rawTarget = ProjectRunCaller.StringArg(arguments, "target");
        string? target;
        if (isNpm ? !TryResolveNpmTarget(root, rawTarget, out target, out error)
                  : !TryResolveDotnetTarget(root, rawTarget, out target, out error))
            return ProjectRunCaller.Deny(error);

        var stages = new TestRunStages(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        void Progress(TestRunProgress p) => _caller.SendProgress(session.Id, context.ToolUseId, p, stages);
        BuildRunResult? result = null;
        try
        {
            result = isNpm
                ? await npm.RunAsync(new NpmBuildRequest(project, root, session.Id, target, script), Progress, ct)
                : await dotnet.RunAsync(new DotnetBuildRequest(project, root, session.Id, target), Progress, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Сбой запуска (нет dotnet/node, отказ среды) — честный текст модели, а не 500 транспорта
            return ProjectRunCaller.Deny($"Не удалось запустить сборку {(isNpm ? "npm" : "dotnet")}: {e.Message}");
        }
        finally
        {
            // Этапы — на карточку и в историю вызова и при обрыве. Счётчиков тестов у сборки нет:
            // итог карточки — строка этапов и текст результата
            _caller.SendFinal(session.Id, context.ToolUseId, stages, failed: EndedBadly(result), totals: null);
        }
        // Упавшая сборка — штатный исход вызова (ошибки в тексте), ошибка инструмента — только отказ
        var text = isNpm ? npm.FormatResult(result) : dotnet.FormatResult(result);
        return new McpToolCallResult(text, IsError: result.Refusal is not null);
    }

    // Стенд: те же гейты вызывателя, что у сборки (стенд собирает и исполняет код проекта)
    private async Task<McpToolCallResult> CallStandAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        if (!_caller.TryResolveSession(context, StandRefusals, out var session, out var error))
            return ProjectRunCaller.Deny(error);
        if (!_caller.TryResolveProject(session, context.OwnerId, StandRefusals, out var project, out error))
            return ProjectRunCaller.Deny(error);
        return tool == StartStandName
            ? await _stand.StartAsync(session, project, context.OwnerId, arguments, context.ToolUseId, ct)
            : await _stand.StopAsync(session, project, context.OwnerId, arguments);
    }

    // Кончилась ли сборка неудачей на последнем этапе: исключение, «Стоп», потолок, ненулевой код
    internal static bool EndedBadly(BuildRunResult? r) =>
        r is null || r.Cancelled || r.TimedOut || r.ExitCode is not 0;

    internal static bool TryParseKind(string? raw, out bool isNpm, out string error)
    {
        error = "";
        isNpm = false;
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null or "" or "dotnet": return true;
            case "npm": isNpm = true; return true;
            default:
                error = $"kind «{raw}» не поддерживается: dotnet или npm.";
                return false;
        }
    }

    // Цель dotnet build — проект/решение/каталог ОТНОСИТЕЛЬНО дерева; позиционный аргумент, поэтому
    // «-»/«@» в начале — отказ (опция или response-файл), и цель обязана существовать. Не задана —
    // единственное решение в дереве, а без решений — единственный проект
    internal static bool TryResolveDotnetTarget(string root, string? raw, out string? target, out string error)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            var solutions = TestsToolset.FindFiles(root, name => name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));
            var candidates = solutions.Count > 0
                ? solutions
                : TestsToolset.FindFiles(root, name => name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
            return TestsToolset.PickSingle(candidates, solutions.Count > 0 ? "решений (*.sln/*.slnx)" : "проектов (*.csproj)",
                "Решение или проект в дереве не найдены — укажи target (каталог или файл .csproj/.sln/.slnx).",
                out target, out error);
        }
        if (!TestsToolset.TryResolveInside(root, raw, "dotnet build", out var full, out var relative, out error))
            return false;
        var isProjectFile = File.Exists(full)
            && TestsToolset.TargetExtensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase);
        if (!isProjectFile && !Directory.Exists(full))
        {
            error = $"target «{relative}» не найден: нужен каталог или файл {string.Join('/', TestsToolset.TargetExtensions)} внутри проекта.";
            return false;
        }
        target = relative;
        return true;
    }

    // Цель npm — КАТАЛОГ с package.json ОТНОСИТЕЛЬНО дерева. Не задана — единственный такой каталог
    // (обход мимо node_modules и скрытых каталогов). Скрипт, node_modules и ссылки за дерево
    // проверяет движок (NpmBuildService.Inspect) — здесь только форма пути
    internal static bool TryResolveNpmTarget(string root, string? raw, out string? target, out string error)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            var dirs = TestsToolset.FindFiles(root, name => name.Equals("package.json", StringComparison.Ordinal))
                .Select(f => f.Contains('/') ? f[..f.LastIndexOf('/')] : "")
                .ToList();
            return TestsToolset.PickSingle(dirs, "каталогов с package.json",
                "Каталог с package.json в дереве не найден — укажи target (каталог фронта, например frontend).",
                out target, out error);
        }
        if (!TestsToolset.TryResolveInside(root, raw, "npm", out var full, out var relative, out error)) return false;
        if (!Directory.Exists(full))
        {
            error = $"target «{relative}» не найден: для npm нужен КАТАЛОГ с package.json (например frontend).";
            return false;
        }
        target = relative;
        return true;
    }

    // Состав постоянный — три инструмента; описания не зависят ни от хода, ни от сессии
    internal static IReadOnlyList<McpToolSchema> Tools { get; } =
    [
        new(ToolName,
            "Собрать проект в рабочем дереве чата с прогрессом в чате: kind=dotnet (по умолчанию) — dotnet build "
            + "(«N из M проектов»), kind=npm — npm run <script> (по умолчанию build; «этап N из M» по шагам "
            + "скрипта). Сборка встаёт в общую очередь сборок, останавливается по «Стоп» и не идёт параллельно "
            + "с run_tests в том же дереве; не запускай рядом свою сборку Bash'ем — процессы подерутся за "
            + "obj/bin. Всегда передавай target: без него берётся единственное решение (каталог с package.json), "
            + $"а при нескольких — отказ. Потолок сборки — {TestRunsOptions.DefaultCeilingSeconds / 60} минут "
            + "(вместе с ожиданием очереди): оборванную повтори — следующий вызов будет инкрементальным. "
            + "Возвращает итог и первые ошибки сборки; полный лог — в папке из ответа.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["kind"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("dotnet", "npm"),
                        ["description"] = "Вид сборки; по умолчанию dotnet",
                    },
                    ["target"] = new JsonObject
                    {
                        ["type"] = "string",
                        // Без примера имени решения: модель подставляла его буквально
                        ["description"] = "ОТНОСИТЕЛЬНО корня проекта. dotnet — путь к .sln/.slnx/.csproj "
                            + "или к каталогу с ним, как он лежит в дереве проекта (имя не придумывай — "
                            + "возьми из дерева); npm — каталог с package.json",
                    },
                    ["script"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["maxLength"] = 100,
                        ["description"] = "Только npm: имя скрипта из package.json (по умолчанию build), "
                            + "без аргументов",
                    },
                },
            }),
        new(StartStandName,
            "Поднять дев-стенд проекта из рабочего дерева чата с прогрессом в чате: сборка (как у build) → запуск "
            + "без сборки → готов (порт принимает соединения). Стенд живёт после хода и виден в панели "
            + "«Сервисы» (логи, превью, «Стоп»); повторный вызов того же сервиса сразу возвращает его адрес. "
            + "Поднимай стенд им, а не `dotnet run &`/`npm run dev &` в Bash: фоновый процесс Bash умирает вместе "
            + "с CLI. Порт по умолчанию — свободный из 5500–5699 (в песочнице — из её пула). Умеет сервисы "
            + "`dotnet run` и скрипты npm/pnpm/yarn; потолок вместе со сборкой — "
            + $"{TestRunsOptions.DefaultCeilingSeconds / 60} минут. Возвращает URL — для e2e передай его в "
            + "env.PLAYWRIGHT_BASE_URL у run_tests.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["service"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["maxLength"] = 300,
                        ["description"] = "id сервиса из панели «Сервисы» (неизвестный id — отказ со списком "
                            + "доступных) ИЛИ путь к .csproj ОТНОСИТЕЛЬНО корня проекта",
                    },
                    ["port"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 5500,
                        ["maximum"] = 5699,
                        ["description"] = "Порт стенда из 5500–5699; не указан — свободный",
                    },
                    ["health_path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["maxLength"] = 200,
                        ["description"] = "Путь пробы готовности (например /health): стенд готов, когда он "
                            + "ответил по HTTP любым кодом",
                    },
                },
                ["required"] = new JsonArray("service"),
            }),
        new(StopStandName,
            "Погасить дев-стенд, поднятый продуктом (start_stand или кнопкой панели «Сервисы»). Процессы, "
            + "запущенные вне продукта, не трогает.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["service"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["maxLength"] = 300,
                        ["description"] = "id сервиса, как его вернул start_stand, или тот же service, что ему передавали",
                    },
                },
                ["required"] = new JsonArray("service"),
            }),
    ];
}
