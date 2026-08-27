using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Dossiers;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Долгая память персоны чата и память команды проекта (memory_* / team_memory_* / dossier_*)
/// поверх HTTP-транспорта — второй переехавший с node сервер (ADR-012, фаза 2 волна 1).
/// Раньше это был mcp/memory-server: тонкий JSON-RPC-фасад, который ходил в наш же бэкенд
/// сервисным JWT. Здесь фасад повёрнут напрямую к сервисам (PersonaMemoryService,
/// TeamMemoryService, DossierStore) — HTTP-хоп через собственный Kestrel не нужен.
///
/// Один тулсет обслуживает ВСЕ ключи конфига хода: «memory» (персона чата) и каждый
/// «pmem_&lt;handle&gt;» (файловые сабагенты-консультанты) — различаются только хвостом
/// маршрута <c>POST /mcp/memory/{personaId}/{projectId}</c>, где персона и проект чата
/// едут в ПУТИ (конфиг хода — наш код, тело контролирует модель). Имена инструментов
/// при этом не меняются: файлы агентов персон ссылаются на сервер по ключу
/// (mcpServers: [pmem_&lt;handle&gt;]) и зовут mcp__pmem_&lt;handle&gt;__*.
///
/// ИЗОЛЯЦИЯ. Хвост виден модели в конфиге хода, поэтому право на него проверяется на
/// КАЖДЫЙ вызов: владелец — только из claim sub сервисного JWT, персона из хвоста обязана
/// принадлежать владельцу токена, проект — его проекту. Чужой или несуществующий id —
/// отказ с текстом, а не пустая память (McpHttpOwnerIsolationTests + MemoryHttpTransportTests).
///
/// ИНВАРИАНТ состава (IMcpToolset): tools/list зависит только от хвоста и владельца
/// (персона/проект/флаг change-dossiers-recall) — всё это закреплено конфигом хода на
/// жизнь адаптера и от свойств хода не зависит. Сторож парности со stdio-веткой отката —
/// MemoryToolsetParityTests.
/// </summary>
public sealed class MemoryToolset(
    PersonaManager personas,
    ProjectManager projects,
    PersonaMemoryService memory,
    TeamMemoryService teamMemory,
    DossierStore dossiers,
    DossierRecallService dossierRecall,
    FeatureFlagService flags,
    IConfiguration config,
    IHubContext<SessionHub> hub) : IMcpParameterizedToolset
{
    // Имя сервера = первый сегмент маршрута POST /mcp/memory/{personaId}/{projectId}.
    // Константа — единственная точка правды: URL конфига хода (ClaudeSession) и хвост
    // собирает сам тулсет, литералы не дублируются
    public const string ServerName = "memory";

    // Сегмент «параметра нет» в хвосте маршрута: чат без персоны (team-only) или вне проекта.
    // Дефис не сталкивается с реальными id (это GUID) и не требует percent-кодирования
    private const string NoneSegment = "-";

    // Усечение текста записей команды на выдаче модели (team_memory_list): стор приходит
    // целиком (на нём UI «Командного центра»), а 200 записей по 3 КБ в контексте хода —
    // та простыня, из-за которой диета памяти команды и затевалась
    private const int TeamTextLimit = 200;

    // Порог recall-скоринга из конфига (тот же, что у REST /api/personas/{id}/memory/recall)
    private readonly double _recallMinScore =
        double.TryParse(config["Persona:RecallMinScore"],
            System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms : 0.30;

    // Ответы инструментов — как у stdio-ветки (JSON.stringify): camelCase, кириллица
    // без \u-экранирования (модель читала этот формат годами); enum'ы — строками
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Name => ServerName;
    public string Version => "1.0.0";

    // У параметризованного тулсета состав без хвоста не существует: контроллер на
    // /mcp/memory без хвоста отвечает 404 до диспетчера
    public IReadOnlyList<McpToolSchema> Tools => [];

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) => BuildTools(context);

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        // Хвост не разобрался или указывает на чужое — отказ текстом, а не пустая память:
        // право на персону/проект из URL проверяется на КАЖДЫЙ вызов (хвост виден модели
        // в конфиге хода, доверять ему нельзя)
        if (!TryResolve(context, out var persona, out var project, out var routeError))
            return Deny(routeError);

        var personal = persona is not null;
        var team = project is not null;

        switch (tool)
        {
            case "memory_remember":
            {
                if (!personal) return DenyPersonal();
                var text = StringArg(arguments, "text");
                if (string.IsNullOrWhiteSpace(text)) return Deny("Пустой текст");
                var type = Enum.TryParse<PersonaMemoryType>(StringArg(arguments, "type"), true, out var t)
                    ? t : PersonaMemoryType.Semantic;
                var entry = await memory.RememberAsync(context.OwnerId, persona!.Id, type, text,
                    TagsArg(arguments), null, DoubleArg(arguments, "salience"));
                if (entry is null) return Deny("Не удалось сохранить запись — персона недоступна.");
                memory.EnforceCap(context.OwnerId, persona.Id);
                await BroadcastMemoryAsync(context.OwnerId, persona.Id);
                return Json(entry);
            }

            case "memory_recall":
            {
                if (!personal) return DenyPersonal();
                var query = StringArg(arguments, "query");
                if (string.IsNullOrWhiteSpace(query)) return Deny("Пустой запрос");
                var recall = await memory.BuildRecallAsync(context.OwnerId, persona!.Id, query,
                    IntArg(arguments, "topK", 5, 1, 20), _recallMinScore);
                return Text(recall?.Text ?? "Память пуста или по этой теме ничего релевантного не нашлось.");
            }

            case "memory_search":
            {
                if (!personal) return DenyPersonal();
                var query = StringArg(arguments, "query");
                if (string.IsNullOrWhiteSpace(query)) return Deny("Пустой запрос");
                var hits = await memory.SearchAsync(context.OwnerId, persona!.Id, query,
                    IntArg(arguments, "topK", 8, 1, 20));
                return Json(hits);
            }

            case "memory_list":
            {
                if (!personal) return DenyPersonal();
                var filter = Enum.TryParse<PersonaMemoryType>(StringArg(arguments, "type"), true, out var t)
                    ? t : (PersonaMemoryType?)null;
                return Json(memory.List(context.OwnerId, persona!.Id, filter));
            }

            case "memory_forget":
            {
                if (!personal) return DenyPersonal();
                var id = StringArg(arguments, "id");
                if (!memory.Forget(context.OwnerId, persona!.Id, id))
                    return Deny($"Запись {id} не найдена в памяти.");
                await BroadcastMemoryAsync(context.OwnerId, persona.Id);
                return Text($"Запись {id} удалена из памяти.");
            }

            case "memory_rethink":
            {
                if (!personal) return DenyPersonal();
                var text = StringArg(arguments, "text");
                if (string.IsNullOrWhiteSpace(text)) return Deny("Пустой текст");
                var entry = memory.Update(context.OwnerId, persona!.Id, StringArg(arguments, "id"), text);
                if (entry is null) return Deny("Запись не найдена.");
                await BroadcastMemoryAsync(context.OwnerId, persona.Id);
                return Json(entry);
            }

            case "memory_to_note":
            {
                if (!personal) return DenyPersonal();
                return memory.MemoryToNote(context.OwnerId, persona!.Id, StringArg(arguments, "id")) is { } note
                    ? Text($"Запись вынесена в заметку «{note.NoteTitle}» (id {note.NoteId}).")
                    : Deny("Запись памяти не найдена.");
            }

            case "memory_from_note":
            {
                if (!personal) return DenyPersonal();
                if (!await memory.NoteToMemoryAsync(context.OwnerId, persona!.Id, StringArg(arguments, "noteId")))
                    return Deny("Заметка не найдена.");
                await BroadcastMemoryAsync(context.OwnerId, persona.Id);
                return Text($"Заметка {StringArg(arguments, "noteId")} закреплена в долгой памяти.");
            }

            case "memory_get_focus":
            {
                if (!personal) return DenyPersonal();
                return memory.GetFocus(context.OwnerId, persona!.Id) is { } focus
                    ? Json(focus)
                    : Text("Рабочий фокус не задан.");
            }

            case "memory_clear_focus":
            {
                if (!personal) return DenyPersonal();
                memory.ClearFocus(context.OwnerId, persona!.Id);
                await BroadcastMemoryAsync(context.OwnerId, persona.Id);
                return Text("Рабочий фокус сброшен.");
            }

            case "team_memory_remember":
            {
                if (!team) return DenyTeam();
                var denied = TeamWriteDenied(persona, project!);
                if (denied is not null) return Deny(denied);
                var text = StringArg(arguments, "text");
                if (string.IsNullOrWhiteSpace(text)) return Deny("Пустой текст");
                if (TeamMemoryService.LengthViolation(text, 0) is { } tooLong) return Deny(tooLong);
                var type = Enum.TryParse<TeamMemoryType>(StringArg(arguments, "type"), true, out var tt)
                    ? tt : TeamMemoryType.Fact;
                // Точный Add, как REST-путь UI (ProjectsController) и stdio-ветка отката,
                // которая POST-ит на тот же REST: рубильник Mcp:HttpTransport меняет только
                // транспорт, а не семантику записи — семантический дедуп AddAsync перезаписал
                // бы чужую близкую запись и вернул её модели под видом новой
                var entry = teamMemory.Add(context.OwnerId, project!.Id, text, type);
                await BroadcastTeamAsync(context.OwnerId, project.Id, "added", entry.Id);
                return Json(entry);
            }

            case "team_memory_search":
            {
                if (!team) return DenyTeam();
                var query = StringArg(arguments, "query");
                if (string.IsNullOrWhiteSpace(query)) return Json(Array.Empty<TeamMemoryEntry>());
                return Json(await teamMemory.SearchAsync(context.OwnerId, project!.Id, query.Trim(),
                    IntArg(arguments, "topK", 8, 1, 20)));
            }

            case "team_memory_list":
            {
                if (!team) return DenyTeam();
                // Пагинация и усечение — только на выдаче модели (стор приходит целиком,
                // на нём держится UI «Командного центра»)
                var list = teamMemory.List(context.OwnerId, project!.Id);
                var wantedId = StringArg(arguments, "id");
                if (wantedId.Length > 0)
                {
                    var entry = list.FirstOrDefault(e => e.Id == wantedId);
                    return entry is not null
                        ? Json(entry)
                        : Deny($"Запись {wantedId} не найдена в памяти команды.");
                }
                var limit = IntArg(arguments, "limit", 20, 1, 50);
                var offset = Math.Max(IntArg(arguments, "offset", 0, 0, int.MaxValue), 0);
                var full = arguments["full"] is JsonValue fv && fv.TryGetValue<bool>(out var f) && f;
                // Усечение — копия в виде анонимной проекции (TeamMemoryEntry — class, не record):
                // ключи сериализации те же, стор не мутируем
                var items = list.Skip(offset).Take(limit)
                    .Select(e => full || e.Text.Length <= TeamTextLimit
                        ? e
                        : (object)new
                        {
                            e.Id, e.OwnerId, e.ProjectId,
                            Text = e.Text[..TeamTextLimit].TrimEnd() + "…",
                            e.Type, e.Tags, e.Salience, e.Source, e.SourceSessionId, e.CreatedAt,
                        })
                    .ToList();
                return Json(new { total = list.Count, offset, limit, items });
            }

            case "team_memory_forget":
            {
                if (!team) return DenyTeam();
                var denied = TeamWriteDenied(persona, project!);
                if (denied is not null) return Deny(denied);
                var id = StringArg(arguments, "id");
                if (!teamMemory.Remove(context.OwnerId, project!.Id, id))
                    return Deny($"Запись {id} не найдена в памяти команды.");
                await BroadcastTeamAsync(context.OwnerId, project.Id, "removed", id);
                return Text($"Запись {id} удалена из памяти команды.");
            }

            case "team_memory_update":
            {
                if (!team) return DenyTeam();
                var denied = TeamWriteDenied(persona, project!);
                if (denied is not null) return Deny(denied);
                var text = StringArg(arguments, "text");
                if (string.IsNullOrWhiteSpace(text)) return Deny("Пустой текст");
                var id = StringArg(arguments, "id");
                var existing = teamMemory.List(context.OwnerId, project!.Id).FirstOrDefault(e => e.Id == id);
                if (existing is null) return Deny($"Запись {id} не найдена в памяти команды.");
                if (TeamMemoryService.LengthViolation(text, existing.Text.Trim().Length) is { } tooLongUpdate)
                    return Deny(tooLongUpdate);
                var entry = teamMemory.Update(context.OwnerId, project.Id, id, text);
                if (entry is null) return Deny("Запись не найдена.");
                await BroadcastTeamAsync(context.OwnerId, project.Id, "updated", id);
                return Json(entry);
            }

            case "dossier_lookup":
            {
                if (!team || !DossierToolsEnabled(context)) return DenyDossier();
                var found = dossierRecall.Lookup(context.OwnerId, project!.Id,
                    OptionalArg(arguments, "path"), OptionalArg(arguments, "symbol"),
                    OptionalArg(arguments, "query"), 20);
                if (found.Count == 0) return Text("Паспортов по этому запросу не нашлось.");
                var lines = found.Select(d =>
                    $"{d.Id} · {ShortSha(d.CommitSha)} · {d.CommitSubject}" + DossierStatusMark(d.Status)
                    + (string.IsNullOrEmpty(d.Why) ? "" : $"\n  Зачем: {d.Why}"));
                return Text($"Найдено паспортов: {found.Count}. Подробности — dossier_get(id).\n\n"
                    + string.Join("\n\n", lines));
            }

            case "dossier_get":
            {
                if (!team || !DossierToolsEnabled(context)) return DenyDossier();
                if (dossiers.GetById(context.OwnerId, project!.Id, StringArg(arguments, "id")) is not { } dossier)
                    return Deny($"Паспорт {StringArg(arguments, "id")} не найден.");
                var section = (string title, IReadOnlyList<string>? items) =>
                    items is { Count: > 0 } ? $"\n{title}:\n" + string.Join("\n", items.Select(s => $"- {s}")) : "";
                return Text(
                    $"Паспорт {ShortSha(dossier.CommitSha)} «{dossier.CommitSubject}»" + DossierStatusMark(dossier.Status)
                    + $"\nКоммит: {dossier.CommitSha}, {dossier.CommittedAt}"
                    + $"\nФайлы: {(dossier.Files is { Count: > 0 } files ? string.Join(", ", files) : "—")}"
                    + $"\nСимволы: {(dossier.Symbols is { Count: > 0 } symbols ? string.Join(", ", symbols) : "—")}"
                    + (string.IsNullOrEmpty(dossier.Why) ? "" : $"\nЗачем: {dossier.Why}")
                    + section("Решения", dossier.Decisions)
                    + section("Отвергнуто", dossier.Rejected)
                    + section("Грабли", dossier.Pitfalls)
                    + section("Инварианты", dossier.Invariants));
            }

            default:
                throw new ArgumentException($"Неизвестный инструмент: {tool}", nameof(tool));
        }
    }

    // --- Маршрут: /mcp/memory/{personaId}/{projectId}, «-» = параметра нет ---

    /// <summary>
    /// Хвост маршрута для конфига хода: наш код собирает URL, модель его только видит.
    /// Единая точка с <see cref="TryParseRoute"/> — форма хвоста не может разъехаться.
    /// </summary>
    internal static string RouteTail(string? personaId, string? projectId) =>
        Segment(personaId) + "/" + Segment(projectId);

    /// <summary>URL эндпоинта в конфиге хода: базовый адрес + маршрут тулсета с хвостом.</summary>
    public static string EndpointFor(string apiUrl, string? personaId, string? projectId) =>
        McpHttpTransport.EndpointFor(apiUrl, ServerName) + "/" + RouteTail(personaId, projectId);

    private static string Segment(string? id) =>
        string.IsNullOrEmpty(id) ? NoneSegment : id;

    // Разбор хвоста: ровно два сегмента, каждый — id или «-». Форма сегмента — как у
    // resumeSessionId-белого списка: буквы/цифры/дефис/подчёркивание, до 128, без трюков
    // кодирования (хвост строим мы, но проверяем форму всё равно — он приезжает из URL)
    private static bool TryParseRoute(string? route, out string? personaId, out string? projectId)
    {
        personaId = null;
        projectId = null;
        if (route is null) return false;
        var parts = route.Split('/');
        if (parts.Length != 2) return false;
        if (!IsSegment(parts[0]) || !IsSegment(parts[1])) return false;
        personaId = parts[0] == NoneSegment ? null : parts[0];
        projectId = parts[1] == NoneSegment ? null : parts[1];
        return true;
    }

    private static bool IsSegment(string value) =>
        value.Length is >= 1 and <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// Резолв хвоста в сущности ВЛАДЕЛЬЦА токена: персона — PersonaManager.Get(personaId, ownerId),
    /// проект — проект с совпадающим OwnerId. Чужое или несуществующее — false плюс текст отказа.
    /// </summary>
    private bool TryResolve(McpToolCallContext context,
        out Persona? persona, out Project? project, [NotNullWhen(false)] out string? error)
    {
        persona = null;
        project = null;
        if (!TryParseRoute(context.RouteTail, out var personaId, out var projectId))
        {
            error = "Некорректный маршрут сервера памяти — вызов отклонён.";
            return false;
        }
        if (personaId is not null)
        {
            persona = personas.Get(personaId, context.OwnerId);
            if (persona is null)
            {
                error = "Персона не найдена или принадлежит другому владельцу — доступ к её памяти закрыт.";
                return false;
            }
        }
        if (projectId is not null)
        {
            project = projects.GetById(projectId);
            if (project is null || project.OwnerId != context.OwnerId)
            {
                error = "Проект не найден или принадлежит другому владельцу — память команды недоступна.";
                return false;
            }
        }
        error = null;
        return true;
    }

    // Состав по хвосту: personal — при своей персоне, team — при своём проекте,
    // dossier — при проекте и флаге владельца change-dossiers-recall (решается по
    // владельцу, стабильно в рамках сессии — инвариант IMcpToolset)
    private IReadOnlyList<McpToolSchema> BuildTools(McpToolCallContext context)
    {
        if (!TryResolve(context, out var persona, out var project, out _)) return [];
        var tools = new List<McpToolSchema>();
        if (persona is not null) tools.AddRange(PersonalTools);
        if (project is not null)
        {
            tools.AddRange(TeamTools);
            if (DossierToolsEnabled(context)) tools.AddRange(DossierTools);
        }
        return tools;
    }

    private bool DossierToolsEnabled(McpToolCallContext context) =>
        flags.IsEnabled(context.OwnerId, FeatureFlagKeys.ChangeDossiersRecall);

    // Гейт записи команды (③-3.4): пишет либо «свой» вызов без персоны (обычный проектный
    // чат), либо персона САМОГО проекта. Вызывающая персона здесь — персона из хвоста
    // маршрута: при stdio ту же роль играл заголовок X-Caller-Persona-Id, который сервер
    // ставил из MEMORY_PERSONA_ID. id и персона идут парой — сюда с непустым id при
    // null-персоне не доехать (неразрешимый хвост отказал выше, в TryResolve)
    private static string? TeamWriteDenied(Persona? persona, Project project) =>
        TeamMemoryService.WriteDeniedFor(persona?.Id, persona, project.Id);

    // --- Ответы ---

    private static McpToolCallResult Text(string text) => new(text);

    private static McpToolCallResult Json<T>(T value) =>
        new(JsonSerializer.Serialize(value, JsonOpts));

    private static McpToolCallResult Deny(string text) => new(text, IsError: true);

    private static McpToolCallResult DenyPersonal() => Deny(
        "Личные инструменты памяти не доступны: у этого чата нет персоны (personaId не задан в маршруте).");

    private static McpToolCallResult DenyTeam() => Deny(
        "Память команды проекта не доступна: чат вне проекта либо проект недоступен.");

    private static McpToolCallResult DenyDossier() => Deny(
        "Паспорта изменений не доступны в этом чате (нет проекта либо выключен флаг владельца change-dossiers-recall).");

    // --- Аргументы вызова ---

    // Нестроковое значение (число, объект) — это «не передали», а не исключение: модель
    // получит понятный отказ валидации, а не разрыв вызова (как StringArg у WidgetsToolset)
    private static string StringArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static string? OptionalArg(JsonObject arguments, string name)
    {
        var value = StringArg(arguments, name);
        return value.Length == 0 ? null : value;
    }

    private static List<string>? TagsArg(JsonObject arguments) =>
        arguments["tags"] is JsonArray tags && tags.Count > 0
            ? tags.Where(t => t is JsonValue v && v.TryGetValue<string>(out _))
                .Select(t => t!.GetValue<string>()).ToList()
            : null;

    private static double? DoubleArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static int IntArg(JsonObject arguments, string name, int fallback, int min, int max)
    {
        if (arguments[name] is not JsonValue v || !v.TryGetValue<int>(out var i)) i = fallback;
        return Math.Clamp(i, min, max);
    }

    private static string ShortSha(string? sha) => sha is null ? "" : sha.Length <= 7 ? sha : sha[..7];

    private static string DossierStatusMark(DossierStatus status) => status switch
    {
        DossierStatus.Degraded => " [код с тех пор менялся]",
        DossierStatus.Archived => " [устарело: символа в коде больше нет]",
        _ => "",
    };

    // --- Broadcast (UI-панели памяти обновляются и при MCP-записи, как при stdio-прокси) ---

    private async Task BroadcastMemoryAsync(string ownerId, string personaId) =>
        await hub.Clients.Group("user_" + ownerId)
            .SendAsync("message", new PersonasChangedMessage("memory", personaId));

    private async Task BroadcastTeamAsync(string ownerId, string projectId, string action, string? entryId) =>
        await hub.Clients.Group("user_" + ownerId)
            .SendAsync("message", new TeamMemoryChangedMessage(action, projectId, entryId));

    // --- Схемы инструментов: копия mcp/memory-server/index.js (источник контракта — здесь,
    // index.js заморожен; сторож парности — MemoryToolsetParityTests). internal для того
    // же сторожа: обе ветки живые (рубильник Mcp:HttpTransport), правка обязана ехать парой ---

    internal static readonly IReadOnlyList<McpToolSchema> PersonalTools =
    [
        Tool("memory_remember",
            "Запомнить что-то в свою долгую память. type: \"semantic\" — устойчивый факт или "
            + "предпочтение пользователя; \"episodic\" — что произошло/обсуждалось (событие, итог разговора); "
            + "\"procedural\" — выученный приём или правило поведения. Запоминай лаконично, по одной мысли на запись.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "type", "text" },
                ["properties"] = new JsonObject
                {
                    ["type"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray { "semantic", "episodic", "procedural" },
                        ["description"] = "Тип памяти",
                    },
                    ["text"] = new JsonObject { ["type"] = "string", ["description"] = "Что запомнить (кратко, одна мысль)" },
                    ["tags"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Необязательные теги для группировки",
                    },
                    ["salience"] = new JsonObject
                    {
                        ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1,
                        ["description"] = "Важность записи 0..1 (1 — критично помнить, 0.3 — мелочь); по умолчанию 1",
                    },
                },
            }),
        Tool("memory_recall",
            "Собрать готовый блок памяти по теме: твой рабочий фокус («что я сейчас делаю») + "
            + "самые релевантные записи долгой памяти (скоринг: релевантность × свежесть × тип × важность) + "
            + "общая память команды проекта. Вызывай ПЕРВЫМ действием, когда начинаешь работать над вопросом, — "
            + "это тот же авто-recall, что получает персона в своих чатах.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "query" },
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Суть вопроса/задачи, над которой работаешь" },
                    ["topK"] = new JsonObject
                    {
                        ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 20,
                        ["description"] = "Сколько записей (по умолчанию 5)",
                    },
                },
            }),
        Tool("memory_search",
            "Поиск по своей долгой памяти по смыслу: возвращает релевантные записи со score "
            + "(учитывает свежесть и тип). Используй, когда нужно вспомнить, что известно по теме.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "query" },
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Смысловой запрос" },
                    ["topK"] = new JsonObject
                    {
                        ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 20,
                        ["description"] = "Сколько записей (по умолчанию 8)",
                    },
                },
            }),
        Tool("memory_list",
            "Перечислить записи памяти (можно сузить по типу). Полезно для обзора того, что уже запомнено.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["type"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray { "semantic", "episodic", "procedural" },
                        ["description"] = "Фильтр по типу",
                    },
                },
            }),
        Tool("memory_forget",
            "Удалить запись памяти по id (например, факт устарел или оказался неверным).",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "id" },
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "ID записи памяти" },
                },
            }),
        Tool("memory_rethink",
            "Переписать (уточнить) существующую запись памяти по id: заменяет её текст на новую "
            + "формулировку. Используй, когда факт изменился или ты хочешь сформулировать точнее — "
            + "вместо того чтобы плодить дубль через memory_remember. id узнаёшь через memory_list/memory_search.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "id", "text" },
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "ID переписываемой записи памяти" },
                    ["text"] = new JsonObject { ["type"] = "string", ["description"] = "Новый текст записи (заменит прежний)" },
                },
            }),
        Tool("memory_to_note",
            "Вынести запись памяти в заметку: инсайт выходит из твоего личного датасета в общий "
            + "vault — становится виден и доступен всей команде и вне чата с тобой. Возвращает id и заголовок "
            + "созданной заметки. id записи узнаёшь через memory_list/memory_search.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "id" },
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "ID выносимой записи памяти" },
                },
            }),
        Tool("memory_from_note",
            "Закрепить существующую заметку в своей долгой памяти: текст заметки попадает в recall "
            + "как устойчивый (semantic) факт с высокой важностью. Указывай id заметки (noteId).",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "noteId" },
                ["properties"] = new JsonObject
                {
                    ["noteId"] = new JsonObject { ["type"] = "string", ["description"] = "ID заметки, которую закрепить в памяти" },
                },
            }),
        Tool("memory_get_focus",
            "Показать текущий рабочий фокус — «над чем я сейчас работаю» (незавершённое дело: "
            + "что делаю, статус, следующий шаг). Если фокуса нет — сообщает об этом.",
            EmptySchema()),
        Tool("memory_clear_focus",
            "Сбросить рабочий фокус — когда текущее дело завершено и «над чем я работаю» больше не актуально.",
            EmptySchema()),
    ];

    internal static readonly IReadOnlyList<McpToolSchema> TeamTools =
    [
        Tool("team_memory_remember",
            "Запомнить факт в общую память КОМАНДЫ проекта — увидят и смогут использовать "
            + "ВСЕ персоны проекта, не только ты. Пиши сюда то, что относится к проекту в целом (общие "
            + "договорённости, структура данных, ограничения), а не личные заметки о себе. Доступно только "
            + "персоне ЭТОГО проекта — у глобальной персоны или консультанта другого проекта вызов откажет "
            + "(используй team_memory_list/team_memory_search для чтения).",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "text" },
                ["properties"] = new JsonObject
                {
                    ["text"] = new JsonObject { ["type"] = "string", ["description"] = "Общий факт/договорённость проекта (кратко)" },
                    ["type"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray { "decision", "convention", "fact", "glossary" },
                        ["description"] = "Тип знания: decision — принятое решение/выбор; convention — договорённость/правило "
                            + "проекта; fact — устойчивый факт (стек, адреса, структура); glossary — термин и его значение. "
                            + "По умолчанию fact.",
                    },
                },
            }),
        Tool("team_memory_search",
            "Поиск по общей памяти команды проекта по смыслу: релевантные записи проекта "
            + "(решения/договорённости/факты/термины). Используй, чтобы вспомнить, что команда уже знает по теме.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "query" },
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Смысловой запрос" },
                    ["topK"] = new JsonObject
                    {
                        ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 20,
                        ["description"] = "Сколько записей (по умолчанию 8)",
                    },
                },
            }),
        Tool("team_memory_list",
            "Перечислить, что команда проекта уже знает (общая память, не личная). Без "
            + "параметров — первая страница с усечёнными текстами (полный текст записи — id или full). "
            + "Ответ несёт total: если записей больше, чем показано, догрузи следующую страницу через offset.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Вернуть одну запись целиком по id (без усечения текста)" },
                    ["limit"] = new JsonObject
                    {
                        ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 50,
                        ["description"] = "Сколько записей на странице (по умолчанию 20)",
                    },
                    ["offset"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "Сколько записей пропустить (пагинация)" },
                    ["full"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Не усекать текст записей страницы (по умолчанию усекается до 200 символов)",
                    },
                },
            }),
        Tool("team_memory_forget",
            "Удалить запись из общей памяти команды проекта по id (устарела/оказалась неверной). "
            + "Доступно только персоне ЭТОГО проекта.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "id" },
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "ID записи командной памяти" },
                },
            }),
        Tool("team_memory_update",
            "Переписать (уточнить) запись общей памяти команды проекта по id: заменяет её текст. "
            + "Используй, когда общий факт/договорённость изменились — вместо дубля через team_memory_remember. "
            + "id узнаёшь через team_memory_list/team_memory_search. Доступно только персоне ЭТОГО проекта.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "id", "text" },
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "ID переписываемой записи командной памяти" },
                    ["text"] = new JsonObject { ["type"] = "string", ["description"] = "Новый текст записи (заменит прежний)" },
                },
            }),
    ];

    internal static readonly IReadOnlyList<McpToolSchema> DossierTools =
    [
        Tool("dossier_lookup",
            "Найти паспорта изменений («зачем, что решили, что отвергли, какие грабли») по "
            + "коду проекта: по пути файла, символу (FQN типа) или свободному тексту. Вызывай ПЕРЕД тем, "
            + "как предлагать архитектурное решение по файлу, — возможно, это уже обсуждали и отвергли. "
            + "В выдаче попадаются и устаревшие записи (символ удалён из кода). Вернёт до 20 кратких записей; "
            + "подробности — dossier_get(id).",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Путь файла от корня проекта (например backend/ClaudeHomeServer/Services/Foo.cs)",
                    },
                    ["symbol"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Полное имя типа (FQN), например ClaudeHomeServer.Services.Foo",
                    },
                    ["query"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Свободный текст: слова из «зачем»/решений/отказов",
                    },
                },
            }),
        Tool("dossier_get",
            "Прочитать паспорт изменения целиком по id (полные «зачем», решения, отказы, "
            + "грабли, инварианты, якоря и коммит). id приходит из dossier_lookup или пассивной подсказки "
            + "истории решений в промпте.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "id" },
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "ID паспорта из dossier_lookup" },
                },
            }),
    ];

    private static McpToolSchema Tool(string name, string description, JsonObject schema) =>
        new(name, description, schema);

    private static JsonObject EmptySchema() =>
        new() { ["type"] = "object", ["properties"] = new JsonObject() };
}
