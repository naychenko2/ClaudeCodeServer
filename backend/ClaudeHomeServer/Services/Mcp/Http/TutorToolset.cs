using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Knowledge;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Материалы предмета для репетитора детского режима (ADR-041, «Редакция 3», разрез «Знания
/// репетитора» §3, К3): поиск по базе знаний предмета и чтение файлов предмета. Единственный
/// сервер, который объявляется ходу ребёнка (allow-list <see cref="ChildModeAllowList"/>).
///
/// Маршрут — <c>POST /mcp/tutor/{sessionId}</c>: хвост пишет наш конфиг хода, а не модель.
/// В схеме нет <c>dataset_id</c>, путей, имён папок и «прочитать файл целиком»: датасет и файлы
/// берутся ТОЛЬКО из записи предмета (<see cref="ChildSubject.Knowledge"/>, описи
/// <see cref="ChildSubjectMaterialsStore"/>), <c>childId</c> — это владелец токена, а не аргумент.
/// Fail-closed построен отсутствием параметра, а не его проверкой.
///
/// Резолв на КАЖДЫЙ вызов и tools/list (состояния у тулсета нет): владелец — ребёнок без DenyAll;
/// чат его, не чат правил, с предметом; <c>X-Caller-Session-Id</c>, если пришёл, равен хвосту;
/// сверка И1-бис той же функцией, что на запуске хода (<see cref="IChildModeGate.LaunchMismatch"/>) —
/// второй сверки «предмет включён и тот же» здесь нет. Привязку базы <c>search_materials</c>
/// перепроверяет на каждом вызове: админ привязки жив и админ по стору, датасет всё ещё проходит
/// <see cref="KnowledgeAccess.IsTutorAttachable"/> по свежему списку Dify (админ мог переименовать
/// датасет или перестать быть админом). Подстановки «поищу в другом» нет.
///
/// Состав tools/list зависит от владельца и сессии (хвост — свойство чата), от хода — нет: три
/// инструмента на любой валидной паре, пусто на невалидной и в чате правил. Привязка и новые
/// файлы состав не меняют — меняется только ответ. Вертикаль «Знаний» необязательна: её нет —
/// <c>search_materials</c> отвечает отказом, файлы предмета работают.
///
/// Любое исключение (Dify, диск, стор) — isError с детским нейтральным текстом; причина — в журнал
/// без текста запроса ребёнка.
/// </summary>
public sealed class TutorToolset(
    IChildModeGate childGate,
    SessionManager sessions,
    IUserStore users,
    ChildSubjectMaterialsStore materials,
    KnowledgeService? knowledge = null,
    ILogger<TutorToolset>? log = null) : IMcpParameterizedToolset
{
    public const string ServerName = McpEndpoints.TutorName;

    public const int MaxQueryLength = 500;
    public const int DefaultTopK = 3;
    public const int MaxTopK = 5;
    public const int MaxFragmentChars = 1500;
    public const int PartChars = 6000;

    public const string UnavailableText = "Материалы предмета сейчас недоступны.";
    private const string NoKnowledgeText = "У предмета нет базы знаний. Файлы предмета — через list_materials.";

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
        if (tool is not (SearchTool or ListTool or ReadTool))
            throw new ArgumentException($"Неизвестный инструмент: {tool}", nameof(tool));

        // Резолв — на каждый вызов: предмет могли выключить, удалить или сменить ему репетитора
        if (!TryResolve(context, out var resolved)) return Fail(UnavailableText);

        try
        {
            return tool switch
            {
                SearchTool => await SearchAsync(resolved, arguments, ct),
                ListTool => await ListAsync(resolved),
                _ => await ReadAsync(resolved, arguments),
            };
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

        // Шаг 5 разреза §3: предмет без базы — отдельный честный ответ, не отказ привязки
        if (r.Subject.Knowledge is not { } binding) return Fail(NoKnowledgeText);

        // Привязка перепроверяется на КАЖДОМ поиске: админ-привязывший жив и админ по стору, датасет
        // проходит предикат по свежему списку Dify. Нет сервиса «Знаний» или Dify — отказ, не пропуск.
        if (knowledge is not { IsConfigured: true }) return Fail(UnavailableText);
        if (users.GetById(binding.AttachedByUserId) is not { } admin
            || !string.Equals(admin.Role, "admin", StringComparison.OrdinalIgnoreCase))
            return Fail(UnavailableText);

        // Имена ВСЕХ остальных пользователей: предикат считает базу глобальной, если её владельца
        // нет в others (тот же состав, что у валидатора привязки)
        var others = users.GetAll()
            .Select(u => u.Username)
            .Where(u => !string.IsNullOrWhiteSpace(u) && !u.Equals(admin.Username, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var datasets = await knowledge.ListDatasetsAsync();
        var dataset = datasets.FirstOrDefault(d => d.Id == binding.DatasetId);
        if (dataset is null || string.IsNullOrWhiteSpace(dataset.Name)
            || string.IsNullOrWhiteSpace(admin.Username)
            || !KnowledgeAccess.IsTutorAttachable(dataset.Name, admin.Username, others))
            return Fail(UnavailableText);

        ct.ThrowIfCancellationRequested();
        var chunks = await knowledge.RetrieveAsync(binding.DatasetId, query, topK);
        return Json(new
        {
            items = chunks.Take(topK).Select(c => new
            {
                document = c.DocumentName,
                text = Clip(c.Content, MaxFragmentChars),
            }),
        });
    }

    private async Task<McpToolCallResult> ListAsync(Resolved r)
    {
        var list = await materials.ListAsync(r.OwnerId, r.Subject.Id);
        var items = new List<object>(list.Count);
        foreach (var m in list)
        {
            // Число частей — по тому же разбиению, что у read_material: список не обещает лишнего
            var text = await materials.ReadTextAsync(r.OwnerId, r.Subject.Id, m.Id);
            items.Add(new { id = m.Id, name = m.Name, kind = m.Kind, parts = text is null ? 0 : Split(text).Count });
        }
        return Json(new { items });
    }

    private async Task<McpToolCallResult> ReadAsync(Resolved r, JsonObject args)
    {
        var id = StringArg(args, "id")?.Trim() ?? "";
        if (id.Length == 0) return Fail("Нужен параметр id (из list_materials).");
        var part = IntArg(args, "part");
        if (part is null) return Fail("Нужен параметр part: номер части, начиная с 1.");

        // id ищется только в описи предмета: чужой id (другой предмет, другой ребёнок) — «нет такого»
        var list = await materials.ListAsync(r.OwnerId, r.Subject.Id);
        if (list.All(m => m.Id != id)) return Fail("Такого материала нет. Список — list_materials.");

        var text = await materials.ReadTextAsync(r.OwnerId, r.Subject.Id, id);
        if (text is null) return Fail(UnavailableText);

        var parts = Split(text);
        if (parts.Count == 0) return Fail("Материал пуст.");
        if (part is < 1 || part > parts.Count)
            return Fail($"Часть вне диапазона: в материале частей — {parts.Count} (нужно от 1 до {parts.Count}).");

        var (start, length) = parts[part.Value - 1];
        return new McpToolCallResult($"часть {part} из {parts.Count}\n{text.Substring(start, length)}");
    }

    // Разбиение текста на части ≤ PartChars: общая точка для parts в списке и для чтения.
    // Граница не рвёт суррогатную пару — граница сдвигается назад на один знак (часть не длиннее).
    private static List<(int Start, int Length)> Split(string text)
    {
        var parts = new List<(int, int)>();
        var pos = 0;
        while (pos < text.Length)
        {
            var end = Math.Min(pos + PartChars, text.Length);
            if (end < text.Length && char.IsLowSurrogate(text[end])) end--;
            parts.Add((pos, end - pos));
            pos = end;
        }
        return parts;
    }

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
    public const string ListTool = "list_materials";
    public const string ReadTool = "read_material";

    private const string ImageNote =
        "У страниц-картинок (kind image) текст распознан с фото, нечитаемые места помечены [?].";

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
        new(ListTool,
            "Список файлов предмета: id, название, вид (kind) и число частей. " + ImageNote,
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
                ["additionalProperties"] = false,
            }),
        new(ReadTool,
            $"Прочитать одну часть файла предмета (не длиннее {PartChars} символов) с пометкой «часть k из N». "
            + "id — из list_materials. " + ImageNote,
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "id материала из list_materials" },
                    ["part"] = new JsonObject
                    {
                        ["type"] = "integer", ["minimum"] = 1,
                        ["description"] = "Номер части, начиная с 1",
                    },
                },
                ["required"] = new JsonArray("id", "part"),
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
