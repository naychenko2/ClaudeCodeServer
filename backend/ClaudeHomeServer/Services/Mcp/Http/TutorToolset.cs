using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Knowledge;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Материалы предмета для репетитора детского режима (ADR-041, «Редакция 3», разрез «Знания
/// репетитора» §3, К3): поиск по базе знаний проекта-предмета (Б17c: чтение файлов описи
/// предмета снято — файлы ребёнок и репетитор читают штатно из папки проекта). Единственный
/// сервер, который объявляется ходу ребёнка (allow-list <see cref="ChildModeAllowList"/>).
///
/// Маршрут — <c>POST /mcp/tutor/{sessionId}</c>: хвост пишет наш конфиг хода, а не модель.
/// В схеме нет <c>dataset_id</c>, путей, имён папок и «прочитать файл целиком»: датасет берётся
/// ТОЛЬКО из проекта-предмета этого чата (<see cref="WorkspaceKnowledgeStore"/> по рабочей папке
/// сессии — той же формулой EffectiveRoot, что инжекция хода DIFY_DEFAULT_DATASET_ID и DifyToolset),
/// <c>childId</c> — это владелец токена, а не аргумент. Fail-closed построен отсутствием параметра, а не его проверкой.
///
/// Резолв на КАЖДЫЙ вызов и tools/list (состояния у тулсета нет): владелец — ребёнок без DenyAll;
/// чат его, не чат правил, с предметом; <c>X-Caller-Session-Id</c>, если пришёл, равен хвосту;
/// сверка И1-бис той же функцией, что на запуске хода (<see cref="IChildModeGate.LaunchMismatch"/>) —
/// второй сверки «предмет включён и тот же» здесь нет. Она же и есть граница датасета между
/// владельцами: чат обязан быть чатом проекта-предмета этого ребёнка (ChildModeGate), и датасет
/// берётся из проекта сессии, переданного сервером, а не из аргумента модели. Привязанной базы
/// предмета (поле Knowledge) больше нет — Б17c. Подстановки «поищу в другом» нет.
///
/// Состав tools/list зависит от владельца и сессии (хвост — свойство чата), от хода — нет: один
/// инструмент на любой валидной паре, пусто на невалидной и в чате правил. Наличие базы у проекта
/// состав не меняет — меняется только ответ. Вертикаль «Знаний» необязательна: её нет —
/// <c>search_materials</c> отвечает отказом.
///
/// Любое исключение (Dify, диск, стор) — isError с детским нейтральным текстом; причина — в журнал
/// без текста запроса ребёнка.
/// </summary>
public sealed class TutorToolset(
    IChildModeGate childGate,
    SessionManager sessions,
    ProjectManager projects,
    WorkspaceKnowledgeStore workspaceKnowledge,
    KnowledgeService? knowledge = null,
    ILogger<TutorToolset>? log = null) : IMcpParameterizedToolset
{
    public const string ServerName = McpEndpoints.TutorName;

    public const int MaxQueryLength = 500;
    public const int DefaultTopK = 3;
    public const int MaxTopK = 5;
    public const int MaxFragmentChars = 1500;

    public const string UnavailableText = "Материалы предмета сейчас недоступны.";
    private const string NoKnowledgeText = "У предмета нет базы знаний.";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Name => ServerName;
    public string Version => "1.0.0";

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) =>
        TryResolve(context, out _) ? Tools : [];

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        if (tool is not SearchTool)
            throw new ArgumentException($"Неизвестный инструмент: {tool}", nameof(tool));

        // Резолв — на каждый вызов: предмет могли выключить, удалить или сменить ему репетитора
        if (!TryResolve(context, out var resolved)) return Fail(UnavailableText);

        try
        {
            return await SearchAsync(resolved, arguments, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // Ребёнку — нейтральный текст; в журнал — тип и чат, без запроса и без содержимого
            log?.LogWarning(e, "Детский режим: инструмент репетитора {Tool} не отработал (чат {Chat}, {ErrorType})",
                tool, resolved.Session.Id, e.GetType().Name);
            return Fail(UnavailableText);
        }
    }

    // ── инструменты ─────────────────────────────────────────────────────────────────

    private async Task<McpToolCallResult> SearchAsync(Resolved r, JsonObject args, CancellationToken ct)
    {
        var query = StringArg(args, "query")?.Trim() ?? "";
        if (query.Length == 0) return Fail("Нужен параметр query.");
        if (query.Length > MaxQueryLength) return Fail($"Запрос слишком длинный: не больше {MaxQueryLength} символов.");
        var topK = Math.Clamp(IntArg(args, "topK") ?? DefaultTopK, 1, MaxTopK);

        // Датасет — только проекта-предмета этого чата (Б13b): записи или датасета у проекта нет —
        // отдельный честный ответ, не отказ привязки.
        var datasetId = SubjectProjectDatasetId(r);
        if (datasetId is null) return Fail(NoKnowledgeText);

        // Нет сервиса «Знаний» или Dify — отказ, не пропуск.
        if (knowledge is not { IsConfigured: true }) return Fail(UnavailableText);

        ct.ThrowIfCancellationRequested();
        var chunks = await knowledge.RetrieveAsync(datasetId, query, topK);
        return Json(new
        {
            items = chunks.Take(topK).Select(c => new
            {
                document = c.DocumentName,
                text = Clip(c.Content, MaxFragmentChars),
            }),
        });
    }

    // Дефолтный датасет проекта-предмета чата — ровно формула DifyToolset.TryResolve и инжекции
    // хода (EffectiveRoot + WorkspaceKnowledgeStore): тулсет и ход обязаны видеть одну и ту же базу,
    // расхождение формул означало бы холостой перезапуск CLI (урок приёмки волны 2). Границу
    // владения держит тот же гейт, что и запуск хода: TryResolve уже отверг чат не этого
    // проекта-предмета (ChildModeGate.LaunchMismatch), здесь читается только запись проекта.
    // null — у проекта базы ещё нет (синк Б13a не создан) или чат вне проекта-предмета.
    private string? SubjectProjectDatasetId(Resolved r) =>
        r.Session.ProjectId is { } pid && projects.GetById(pid) is { } project
            ? workspaceKnowledge.GetByPath(SessionManager.EffectiveRoot(r.Session, project.RootPath))?.DifyDatasetId
            : null;

    private static string Clip(string? content, int max)
    {
        var text = content ?? "";
        if (text.Length <= max) return text;
        // Всего не больше max, включая многоточие; суррогатную пару не рвём
        var end = max - 1;
        if (char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end] + "…";
    }

    // ── резолв ──────────────────────────────────────────────────────────────────────

    private sealed record Resolved(string OwnerId, Session Session, ChildSubject Subject);

    // Шаги 1–4 разреза §3. Любой отказ — false без причины наружу: ребёнок и модель не различают,
    // чужой это чат, выключенный предмет или чат правил.
    private bool TryResolve(McpToolCallContext context, out Resolved resolved)
    {
        resolved = null!;

        // 1. Владелец из сервисного JWT — ребёнок и не DenyAll
        if (string.IsNullOrWhiteSpace(context.OwnerId)) return false;
        if (childGate.PolicyFor(context.OwnerId) is not { DenyAll: false, Profile: { } profile }) return false;

        // 2. Сессия по хвосту — чат этого владельца, «Уроков», с предметом; заголовок чата, если
        // пришёл, равен хвосту
        if (!TryParseRoute(context.RouteTail, out var sessionId)) return false;
        if (!string.IsNullOrEmpty(context.CallerSessionId) && context.CallerSessionId != sessionId) return false;
        if (sessions.GetOwned(sessionId, context.OwnerId) is not { } session) return false;
        if (session.ChildSpace is null || session.ChildSubject is not { } subjectId) return false;
        if (session.OnboardingKind == OnboardingKinds.ChildRules) return false;

        // 3. Сверка И1-бис той же функцией, что на запуске хода: выключенный или удалённый предмет,
        // сменённый репетитор, рассогласованный чат отказываются там же, где отказывается ход
        if (childGate.LaunchMismatch(context.OwnerId, session) is not null) return false;

        // 4. Запись предмета — из живого профиля
        var subject = profile.Subjects?.FirstOrDefault(s => string.Equals(s.Id, subjectId, StringComparison.OrdinalIgnoreCase));
        if (subject is null) return false;

        resolved = new Resolved(context.OwnerId, session, subject);
        return true;
    }

    /// <summary>URL эндпоинта в конфиге хода: базовый адрес + маршрут тулсета с хвостом-сессией.</summary>
    public static string EndpointFor(string apiUrl, string sessionId) =>
        McpEndpoints.EndpointFor(apiUrl, ServerName, sessionId);

    // Один сегмент — id сессии; та же форма, что у соседних тулсетов (белый список resumeSessionId)
    private static bool TryParseRoute(string? route, out string sessionId)
    {
        sessionId = "";
        if (route is null || route.Split('/').Length != 1) return false;
        if (route.Length is < 1 or > 128 || !route.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return false;
        sessionId = route;
        return true;
    }

    // ── схемы ───────────────────────────────────────────────────────────────────────

    public const string SearchTool = "search_materials";

    private static readonly IReadOnlyList<McpToolSchema> Tools =
    [
        new(SearchTool,
            "Найти в материалах предмета фрагменты по запросу. Возвращает до topK фрагментов (не длиннее "
            + $"{MaxFragmentChars} символов каждый) с названием документа. Материалы — для тебя, чтобы опереться "
            + "на программу; ребёнку их содержимое не показывай и не пересказывай целиком.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject
                    {
                        ["type"] = "string", ["minLength"] = 1, ["maxLength"] = MaxQueryLength,
                        ["description"] = "Что искать, своими словами",
                    },
                    ["topK"] = new JsonObject
                    {
                        ["type"] = "integer", ["minimum"] = 1, ["maximum"] = MaxTopK, ["default"] = DefaultTopK,
                        ["description"] = "Сколько фрагментов вернуть",
                    },
                },
                ["required"] = new JsonArray("query"),
                ["additionalProperties"] = false,
            }),
    ];

    // ── утилиты ─────────────────────────────────────────────────────────────────────

    private static string? StringArg(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // Целое из аргумента; число с дробью и нечисловое — null (дальше по умолчанию или отказ)
    private static int? IntArg(JsonObject args, string key)
    {
        if (args[key] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l is > int.MaxValue ? int.MaxValue : l < int.MinValue ? int.MinValue : (int)l;
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) < int.MaxValue) return (int)d;
        return null;
    }

    private static McpToolCallResult Json(object value) =>
        new(JsonSerializer.Serialize(value, JsonOpts));

    private static McpToolCallResult Fail(string text) => new(text, IsError: true);
}
