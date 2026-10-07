using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.TestRuns;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Прогон тестов с прогрессом в чате (run_tests, docs/research/test-progress-2026-10.md).
/// Маршрут — <c>POST /mcp/tests/{sessionId}</c>: хвост несёт СЕССИЮ-ВЫЗЫВАТЕЛЬ, по ней тулсет
/// знает владельца, проект (среда запуска) и рабочее дерево (worktree чата или корень проекта).
///
/// Вызов синхронный: ответ — когда прогон кончился, оборван «Стопом» (обрыв HTTP = отмена
/// <c>ct</c>) или серверным потолком (540 с, меньше MCP_TOOL_TIMEOUT хода).
///
/// Гейты на КАЖДЫЙ вызов: сессия владельца токена (fail-closed), подсистема test-runs, чат
/// проекта, проект на сервере (ADR-016 — локальный проект в v1 не поддерживаем), персона не
/// ReadOnly. Отказы — текстом с IsError, не исключением. Ходы без человека не запрещены:
/// исполнители гоняют тесты чаще всех.
/// ИНВАРИАНТ состава: tools/list зависит только от сессии-вызывателя, не от хода.
/// </summary>
public sealed class TestsToolset(
    SessionManager sessions,
    ProjectManager projects,
    PersonaManager personas,
    // Из отключаемой вертикали TestRuns: выключена — инструмент честно отказывает
    TestRunService? runs = null,
    // Живые фазы прогона в ленту; нет — вызов работает без прогресса
    ISessionBroadcaster? broadcaster = null) : IMcpParameterizedToolset
{
    public const string ServerName = McpEndpoints.TestsName;
    public const string ToolName = "run_tests";

    internal const string LocalProjectReason =
        "Тесты локального проекта прогоняй Bash'ем на устройстве: run_tests работает только с проектами на сервере.";

    private static readonly ProjectRunCaller.Texts Refusals = new(
        BadRoute: "Некорректный маршрут сервера прогона тестов — вызов отклонён.",
        ProjectOnly: "Прогон тестов работает только в чате проекта.",
        LocalProject: LocalProjectReason,
        ReadOnly: "Персона с доступом «Только чтение» тесты не запускает: прогон пишет bin/obj и файлы проекта.",
        NoBash: "Персоне запрещён Bash — значит, и запуск кода тестов: run_tests исполняет код проекта.");

    private readonly ProjectRunCaller _caller = new(sessions, projects, personas, broadcaster);

    public string Name => ServerName;
    public string Version => "1.0.0";

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) =>
        _caller.TryResolveSession(context, Refusals, out _, out _) ? Tools : [];

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        if (tool != ToolName) throw new ArgumentException($"Неизвестный инструмент: {tool}", nameof(tool));
        if (!_caller.TryResolveSession(context, Refusals, out var session, out var error)) return Deny(error);
        if (runs is null)
            return Deny("Прогон тестов выключен на этом сервере — запусти тесты Bash'ем.");
        _caller.SendStarted(session.Id, context.ToolUseId);
        if (!_caller.TryResolveProject(session, context.OwnerId, Refusals, out var project, out error)) return Deny(error);

        var root = ProjectRunCaller.RootOf(session, project);
        if (!TryParseKind(StringArg(arguments, "kind"), out var kind, out error)) return Deny(error);
        var filter = StringArg(arguments, "filter");
        if (TestRunService.LooksLikeOption(filter))
            return Deny("filter — выражение отбора тестов, а не опция: начинаться с «-» или «@» он не может "
                + "(«@файл» раскрылся бы в опции из файла).");

        string? target;
        string? nodeBin = null;
        IReadOnlyList<string> files = [];
        IReadOnlyDictionary<string, string> env = new Dictionary<string, string>();
        if (kind == TestRunKind.Dotnet)
        {
            if (!TryResolveTarget(root, StringArg(arguments, "target"), out target, out error)) return Deny(error);
            if (arguments["files"] is JsonArray { Count: > 0 })
                return Deny("files — только для vitest и Playwright; у dotnet выбирай тесты аргументом filter.");
        }
        else
        {
            if (!TryResolveNodeTarget(root, StringArg(arguments, "target"), kind, out target, out nodeBin, out error))
                return Deny(error);
            if (!TryResolveFiles(root, target!, arguments["files"], out files, out error)) return Deny(error);
            if (!TryResolveEnv(arguments["env"], kind, out env, out error)) return Deny(error);
        }

        var noBuild = arguments["no_build"] is JsonValue flag && flag.TryGetValue<bool>(out var nb) && nb;
        var request = new TestRunRequest(project, root, session.Id, target, filter, noBuild)
        {
            Kind = kind,
            Files = files,
            Env = env,
            NodeBin = nodeBin,
        };
        var stages = new TestRunStages(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        TestRunResult? result = null;
        try
        {
            result = await runs.RunAsync(request, p => _caller.SendProgress(session.Id, context.ToolUseId, p, stages), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Сбой запуска (нет dotnet/node, отказ среды) — честный текст модели, а не 500 транспорта
            return Deny($"Не удалось запустить {TestRunSummaryFormatter.KindTitle(kind)}: {e.Message}");
        }
        finally
        {
            // Этапы и итог — на карточку и в историю вызова и при обрыве: строка этапов
            // «прервано · сборка 1:10» должна пережить F5. result null — RunAsync бросил (отмена,
            // сбой запуска): этап, на котором оборвалось, — неудача
            _caller.SendFinal(session.Id, context.ToolUseId, stages,
                failed: result is null || TestRunStages.EndedBadly(result),
                totals: result is null ? null : TestRunStages.Totals(result));
        }
        // Упавшие тесты — штатный исход вызова, а не ошибка инструмента; ошибка — только отказ
        return new McpToolCallResult(runs.FormatResult(result), IsError: result.Refusal is not null);
    }

    internal static bool TryParseKind(string? raw, out TestRunKind kind, out string error)
    {
        error = "";
        kind = TestRunKind.Dotnet;
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null or "" or "dotnet": return true;
            case "vitest": kind = TestRunKind.Vitest; return true;
            case "playwright": kind = TestRunKind.Playwright; return true;
            default:
                error = $"kind «{raw}» не поддерживается: dotnet, vitest или playwright.";
                return false;
        }
    }

    // Расширения, которые dotnet test принимает целью; каталог допустим отдельно
    internal static readonly string[] TargetExtensions = [".csproj", ".sln", ".slnx", ".slnf"];

    // Сколько кандидатов в цель показывать в отказе и как глубоко их искать
    private const int MaxCandidatesShown = 10;
    private const int SearchDepth = 4;

    // Цель — путь проекта/решения ОТНОСИТЕЛЬНО дерева: абсолютный путь хоста в песочнице не
    // существует, а выход за дерево — дыра. Модели уходит нормализованный относительный путь.
    // Цель едет ПОЗИЦИОННЫМ аргументом dotnet test, поэтому строка с «-» стала бы опцией
    // (--diag:/x, --results-directory=/x, -e:DOTNET_STARTUP_HOOKS=…, --settings:…): запись
    // файлов вне дерева и подмена env testhost, а «@файл» dotnet раскрывает в опции из файла
    // (response-файл, M1-bis ревью этапа 2). Отсюда два правила: «-» и «@» в начале — отказ (и
    // до нормализации, и после), и цель обязана СУЩЕСТВОВАТЬ — каталог или файл проекта/решения.
    // Цель не задана — ищем единственное решение в дереве (в корне его часто нет: модель без
    // target падала первым вызовом), а без решений — единственный тестовый проект.
    internal static bool TryResolveTarget(string root, string? raw, out string? target, out string error)
    {
        target = null;
        error = "";
        if (string.IsNullOrWhiteSpace(raw)) return TryFindDefaultDotnetTarget(root, out target, out error);
        if (!TryResolveInside(root, raw, "dotnet test", out var full, out var relative, out error)) return false;
        var isProjectFile = File.Exists(full)
            && TargetExtensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase);
        if (!isProjectFile && !Directory.Exists(full))
        {
            error = $"target «{relative}» не найден: нужен каталог или файл {string.Join('/', TargetExtensions)} внутри проекта.";
            return false;
        }
        target = relative;
        return true;
    }

    // Общая проверка пути от модели: не опция и не response-файл, относительный, внутри дерева
    internal static bool TryResolveInside(string root, string raw, string tool, out string full, out string relative,
        out string error)
    {
        full = relative = error = "";
        if (TestRunService.LooksLikeOption(raw))
        {
            error = $"target — путь, а не опция {tool}: начинаться с «-» или «@» он не может.";
            return false;
        }
        if (Path.IsPathRooted(raw) || raw.StartsWith('/') || raw.StartsWith('\\'))
        {
            error = "target — путь ОТНОСИТЕЛЬНО корня проекта (например backend/ClaudeHomeServer.Tests или frontend), не абсолютный.";
            return false;
        }
        try
        {
            full = SafePath.Join(root, raw);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = "target выходит за пределы проекта — вызов отклонён.";
            return false;
        }
        relative = Path.GetRelativePath(root, full).Replace('\\', '/').TrimEnd('/');
        if (relative == ".") relative = "";
        if (TestRunService.LooksLikeOption(relative))
        {
            error = $"target — путь, а не опция {tool}: начинаться с «-» или «@» он не может.";
            return false;
        }
        return true;
    }

    private static bool TryFindDefaultDotnetTarget(string root, out string? target, out string error)
    {
        target = null;
        error = "";
        var solutions = FindFiles(root, name => name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));
        var candidates = solutions.Count > 0
            ? solutions
            : FindFiles(root, name => name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                && name.Contains("test", StringComparison.OrdinalIgnoreCase));
        return PickSingle(candidates, solutions.Count > 0 ? "решений (*.sln/*.slnx)" : "тестовых проектов",
            "Решение или тестовый проект в дереве не найдены — укажи target (каталог или файл .csproj/.sln/.slnx).",
            out target, out error);
    }

    // Один кандидат — он и цель; несколько — отказ со списком: угадывать нельзя. Кандидат,
    // похожий на опцию («-», «@»), отсеивается и здесь — второй рубеж после FindFiles
    internal static bool PickSingle(IReadOnlyList<string> found, string what, string none,
        out string? target, out string error)
    {
        target = null;
        error = "";
        var candidates = found.Where(c => !TestRunService.LooksLikeOption(c)).ToList();
        if (candidates.Count == 1)
        {
            target = candidates[0];
            return true;
        }
        if (candidates.Count == 0)
        {
            error = none;
            return false;
        }
        var shown = string.Join("\n", candidates.Take(MaxCandidatesShown).Select(c => "- " + (c.Length == 0 ? "." : c)));
        var more = candidates.Count > MaxCandidatesShown ? $"\n…и ещё {candidates.Count - MaxCandidatesShown}" : "";
        error = $"target не задан, а {what} в дереве несколько — укажи нужный аргументом target:\n{shown}{more}";
        return false;
    }

    // Файлы по имени в дереве (относительные пути через «/»), обход шириной до SearchDepth
    // уровней мимо служебных и скрытых каталогов (.git, node_modules, bin/obj, worktree'ы в .claude)
    internal static List<string> FindFiles(string root, Func<string, bool> match)
    {
        var found = new List<string>();
        var level = new List<string> { root };
        for (var depth = 0; depth <= SearchDepth && level.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var dir in level)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        if (!match(Path.GetFileName(file))) continue;
                        // Путь «-x.sln» или «@dir/…» ушёл бы в dotnet/node опцией или
                        // response-файлом — такой кандидат не цель по умолчанию
                        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                        if (!TestRunService.LooksLikeOption(relative)) found.Add(relative);
                    }
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        var name = Path.GetFileName(sub);
                        if (!name.StartsWith('.') && !TreeExcludes.Contains(name)) next.Add(sub);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* недоступный каталог */ }
            }
            level = next;
        }
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    // Конфиги, по которым узнаётся каталог прогона node-инструмента
    private static readonly string[] ConfigExtensions = [".ts", ".mts", ".cts", ".js", ".mjs", ".cjs"];

    private static bool IsConfigOf(string fileName, TestRunKind kind)
    {
        var stem = kind == TestRunKind.Vitest ? "vitest.config" : "playwright.config";
        return ConfigExtensions.Any(ext => fileName.Equals(stem + ext, StringComparison.OrdinalIgnoreCase));
    }

    // Цель vitest/Playwright — КАТАЛОГ с конфигом (cwd процесса). Не задана — единственный такой
    // каталог в дереве (у нас frontend). Скрипт пакета — в ближайшем node_modules от цели вверх
    // до корня дерева (nodeBin — путь относительно цели): запускаем `node <bin>`, а не npx
    // (см. TestRunService)
    internal static bool TryResolveNodeTarget(string root, string? raw, TestRunKind kind, out string? target,
        out string? nodeBin, out string error)
    {
        target = null;
        nodeBin = null;
        var tool = TestRunService.KindName(kind);
        if (string.IsNullOrWhiteSpace(raw))
        {
            var dirs = FindFiles(root, name => IsConfigOf(name, kind))
                .Select(f => f.Contains('/') ? f[..f.LastIndexOf('/')] : "")
                .Distinct(StringComparer.Ordinal).ToList();
            if (!PickSingle(dirs, $"каталогов с {(kind == TestRunKind.Vitest ? "vitest" : "playwright")}.config.*",
                    $"Каталог с {(kind == TestRunKind.Vitest ? "vitest" : "playwright")}.config.* в дереве не найден — "
                    + "укажи target (каталог фронта, например frontend).", out target, out error))
                return false;
        }
        else
        {
            if (!TryResolveInside(root, raw, tool, out var full, out var relative, out error)) return false;
            if (!Directory.Exists(full))
            {
                error = $"target «{relative}» не найден: для {tool} нужен КАТАЛОГ с конфигом (например frontend).";
                return false;
            }
            target = relative;
        }

        // Монорепа: пакет лежит в node_modules выше цели (до корня дерева, не выше)
        nodeBin = TestRunService.FindNodeBin(root, target!, kind, out error);
        return nodeBin is not null;
    }

    // Пути тестов vitest/Playwright: те же правила, что у цели (не опция, внутри дерева,
    // существуют), плюс внутри каталога прогона — процесс получает их ОТНОСИТЕЛЬНО него
    public const int MaxFiles = 50;

    internal static bool TryResolveFiles(string root, string target, JsonNode? node, out IReadOnlyList<string> files,
        out string error)
    {
        files = [];
        error = "";
        if (node is null) return true;
        if (node is not JsonArray array)
        {
            error = "files — массив путей тестов относительно корня проекта.";
            return false;
        }
        if (array.Count > MaxFiles)
        {
            error = $"files — не больше {MaxFiles} путей; больше — запускай каталогом.";
            return false;
        }
        var cwd = Path.Combine(root, target);
        var result = new List<string>();
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                error = "files — массив непустых строк.";
                return false;
            }
            if (!TryResolveInside(root, raw, "тестового раннера", out var full, out var relative, out error))
            {
                error = error.Replace("target", $"Путь «{raw}» в files");
                return false;
            }
            if (!File.Exists(full) && !Directory.Exists(full))
            {
                error = $"Путь «{relative}» в files не найден (пути — относительно корня проекта, например frontend/src/lib/x.test.ts).";
                return false;
            }
            var fromCwd = Path.GetRelativePath(cwd, full).Replace('\\', '/');
            if (fromCwd == ".." || fromCwd.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(fromCwd))
            {
                error = $"Путь «{relative}» вне каталога прогона «{target}» — раннер его не найдёт.";
                return false;
            }
            if (TestRunService.LooksLikeOption(fromCwd))
            {
                error = $"Путь «{relative}» начинается с «-» или «@» — раннер принял бы его за опцию.";
                return false;
            }
            result.Add(fromCwd);
        }
        files = result;
        return true;
    }

    public const int MaxEnvValueLength = 256;

    // env — только Playwright и только белый список: адрес стенда (только loopback — сервер
    // стучится по нему сам, проверяя стенд) и учётка e2e
    internal static bool TryResolveEnv(JsonNode? node, TestRunKind kind, out IReadOnlyDictionary<string, string> env,
        out string error)
    {
        env = new Dictionary<string, string>();
        error = "";
        if (node is null) return true;
        if (node is not JsonObject obj)
        {
            error = "env — объект «имя: значение».";
            return false;
        }
        if (obj.Count == 0) return true;
        if (kind != TestRunKind.Playwright)
        {
            error = "env — только для Playwright.";
            return false;
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in obj)
        {
            if (!TestRunService.PlaywrightEnvKeys.Contains(key))
            {
                error = $"Переменная {key} не из белого списка: {string.Join(", ", TestRunService.PlaywrightEnvKeys)}.";
                return false;
            }
            if (value is not JsonValue v || !v.TryGetValue<string>(out var text) || text.Length > MaxEnvValueLength
                || text.Any(char.IsControl))
            {
                error = $"Значение {key} — строка до {MaxEnvValueLength} символов без управляющих символов.";
                return false;
            }
            if (key == "PLAYWRIGHT_BASE_URL"
                && !(Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" && url.IsLoopback))
            {
                error = "PLAYWRIGHT_BASE_URL — только локальный стенд: http(s)://localhost, 127.0.0.1 или [::1].";
                return false;
            }
            result[key] = text;
        }
        env = result;
        return true;
    }

    private static McpToolCallResult Deny(string text) => ProjectRunCaller.Deny(text);

    private static string? StringArg(JsonObject arguments, string name) => ProjectRunCaller.StringArg(arguments, name);

    // Состав постоянный — один инструмент; описание не зависит ни от хода, ни от сессии
    internal static IReadOnlyList<McpToolSchema> Tools { get; } =
    [
        new(ToolName,
            "Прогнать тесты в рабочем дереве чата с прогрессом в чате: kind=dotnet (по умолчанию) — dotnet test "
            + "(сборка → подсчёт → прогон, «N из M · упало K»), kind=vitest — юнит-тесты фронта (прогресс по "
            + "файлам), kind=playwright — e2e: стенд поднимает webServer из конфига, без него — против УЖЕ "
            + "запущенного стенда. Прогон встаёт в "
            + "общую очередь сборок и останавливается по «Стоп». Всегда передавай target: без него берётся "
            + "единственное решение или каталог с конфигом в дереве, а при нескольких — отказ. Потолок прогона — "
            + $"{TestRunsOptions.DefaultCeilingSeconds / 60} минут (вместе с ожиданием очереди): длинный прогон "
            + "дроби аргументами filter/files. Возвращает сводку и упавшие тесты (сообщение и стек) либо первые "
            + "ошибки сборки; полный вывод и отчёт — в папке артефактов из ответа.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["kind"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("dotnet", "vitest", "playwright"),
                        ["description"] = "Вид прогона; по умолчанию dotnet",
                    },
                    ["target"] = new JsonObject
                    {
                        ["type"] = "string",
                        // Без примеров имён: модель подставляла их буквально
                        ["description"] = "ОТНОСИТЕЛЬНО корня проекта. dotnet — путь к .sln/.slnx/.csproj "
                            + "(или к каталогу тестового проекта), как он лежит в дереве проекта (имя не "
                            + "придумывай — возьми из дерева); vitest/playwright — каталог с конфигом "
                            + "(node_modules — в нём или выше, в корне монорепы)",
                    },
                    ["filter"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["maxLength"] = TestRunService.MaxFilterLength,
                        ["description"] = "dotnet — выражение --filter (FullyQualifiedName~SessionManagerTests); "
                            + "vitest — шаблон имени теста (-t); playwright — --grep",
                    },
                    ["files"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["maxItems"] = MaxFiles,
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Только vitest/playwright: файлы или каталоги тестов ОТНОСИТЕЛЬНО корня "
                            + "проекта, внутри target (например frontend/src/lib/__tests__/x.test.ts)",
                    },
                    ["env"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Только playwright: PLAYWRIGHT_BASE_URL (локальный стенд), E2E_USER, E2E_PASS",
                    },
                    ["no_build"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Только dotnet: true — пропустить сборку (дерево уже собрано после последней правки)",
                    },
                },
            }),
    ];
}
