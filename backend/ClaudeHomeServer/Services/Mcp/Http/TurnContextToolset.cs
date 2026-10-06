using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Контекст чата для агента (ADR-023 §3.2): context_state, context_attach, context_detach. Маршрут —
/// <c>POST /mcp/turn-context/{sessionId}</c>: хвост несёт чат, по нему тулсет находит владельца и область
/// (проект или личную). Ехать ли серверу в ход, решает Main (SessionManager.BuildTurnContextContext).
///
/// context_state отдаёт ТОТ ЖЕ текст, что хвост хода (TurnContextContributor.Compose, единая точка сборки,
/// с идентификаторами референсов). context_attach кладёт референс в контекст с пометкой «поставил Claude»
/// (By = Agent, чип ✦) и той же проверкой, что у ручки человека: вид зарегистрирован, Ref проходит Validate
/// провайдера до записи, роль принимает основной объект. context_detach убирает только референс: основной
/// объект агент меняет через image_focus / audio_focus.
///
/// ИНВАРИАНТ состава: tools/list зависит от сессии и флага владельца — не от хода и не от содержимого
/// контекста: ToolsFor стор не читает (McpToolsetStabilityTests). Пустой контекст — отказ в ответе, а не
/// исчезнувший инструмент. context_attach и context_detach меняют настройку чата за человека, поэтому
/// делегированный и реакционный ход получают отказ fail-closed (IDelegatedTurnGate); context_state — чтение,
/// разрешён всегда.
/// </summary>
public sealed class TurnContextToolset(
    IMcpSessionAccessor sessions,
    IProjectManager projects,
    ContextKindRegistry registry,
    IChatContextStore store,
    TurnContextContributor text,
    IDelegatedTurnGate? turnGate = null) : IMcpParameterizedToolset
{
    public const string ServerName = McpEndpoints.TurnContextName;

    public const string ToolState = "context_state";
    public const string ToolAttach = "context_attach";
    public const string ToolDetach = "context_detach";

    public string Name => ServerName;
    public string Version => "1.0.0";

    internal static readonly IReadOnlyList<McpToolSchema> Schemas =
    [
        new(ToolState,
            "Контекст этого чата — то, что человек видит в строке контекста: где работаем, основной объект, "
            + "чем исполняем, референсы (с идентификаторами path/slug). Тот же текст, что в блоке «Контекст хода».",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),
        new(ToolAttach,
            "Добавить референс в контекст чата: он помечается «поставил Claude» и станет входом image_generate / "
            + "audio_generate по умолчанию. Основной объект так не меняется — для него image_focus / audio_focus.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "kind", "ref" },
                ["properties"] = new JsonObject
                {
                    ["kind"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Вид объекта: project-file, image-character, audio-voice, image, audio",
                    },
                    ["ref"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] = "Идентификатор объекта своего вида: {path} у project-file, {slug} у image-character "
                            + "и audio-voice, {threadId, versionId?} у image и audio",
                    },
                    ["role"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Роль референса, если основной объект принимает его как вход: style, object, face, "
                            + "reference, piece… Без роли референс только для тебя",
                    },
                },
            }),
        new(ToolDetach,
            "Убрать референс из контекста чата по itemId (виден в ответе context_attach и в панели). "
            + "Основной объект так не снимается.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "itemId" },
                ["properties"] = new JsonObject
                {
                    ["itemId"] = new JsonObject { ["type"] = "string", ["description"] = "id элемента контекста (ci_…)" },
                },
            }),
    ];

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) =>
        TryResolve(context, out _, out _) ? Schemas : [];

    public Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments, McpToolCallContext context,
        CancellationToken ct)
    {
        if (!TryResolve(context, out var session, out var error)) return Task.FromResult(Deny(error));
        var result = tool switch
        {
            ToolState => State(context.OwnerId, session),
            ToolAttach => Attach(context.OwnerId, session, arguments),
            ToolDetach => Detach(context.OwnerId, session, arguments),
            _ => throw new ArgumentException($"Неизвестный инструмент: {tool}", nameof(tool)),
        };
        return Task.FromResult(result);
    }

    private McpToolCallResult State(string ownerId, Session session)
    {
        var project = ProjectOf(session);
        var body = text.Compose(ownerId, session, project, project?.RootPath);
        return new(body);
    }

    private McpToolCallResult Attach(string ownerId, Session session, JsonObject args)
    {
        if (Gate(ownerId, session, "Изменение контекста чата") is { } denied) return Deny(denied);
        var project = ProjectOf(session);
        if (project is not null && !ProjectCapabilityGuard.Allows(project, ProjectCapabilityArea.FileBound))
            return Deny(ProjectCapabilityGuard.FilesOnDeviceReason);

        var kind = args["kind"] is JsonValue k && k.TryGetValue<string>(out var ks) ? ks : null;
        var reference = args["ref"] as JsonObject;
        var role = args["role"] is JsonValue r && r.TryGetValue<string>(out var rs) && !string.IsNullOrWhiteSpace(rs)
            ? rs.Trim() : null;
        if (string.IsNullOrWhiteSpace(kind) || !registry.IsRegistered(kind))
            return Deny($"Вид контекста «{kind}» не зарегистрирован. Видны: {string.Join(", ", registry.Kinds.Order())}.");
        if (reference is null) return Deny("Не указан ref: идентификатор объекта своего вида.");

        // Проверка Ref до записи — провайдер владельца; мусор в стор не попадает
        var scope = new ContextScope(ownerId, session, project);
        if (registry.Validate(scope, kind, reference) is { } refusal) return Deny(refusal);

        var item = new ContextItem("ci_" + Guid.NewGuid().ToString("N")[..12], kind, reference.DeepClone().AsObject(),
            role, ContextActor.Agent, DateTime.UtcNow);
        try
        {
            if (role is not null)
            {
                var primary = store.Get(ownerId, session.Id).Primary;
                var accepted = primary is not null && registry.Find(primary.Kind) is { } owner
                    ? owner.AcceptedRefs(scope, primary, null)
                    : [];
                if (!accepted.Any(a => a.Role == role && a.Kinds.Contains(kind)))
                    return Deny($"Основной объект не принимает референс «{kind}» с ролью «{role}».");
            }
            // Ревизию агент не держит: запись без сверки, дубль стор отбрасывает сам
            var state = store.AddRef(ownerId, session.Id, item, null);
            var stored = state.Refs.FirstOrDefault(i => i.Kind == kind && JsonNode.DeepEquals(i.Ref, item.Ref) && i.Role == role)
                ?? state.Primary;
            return new($"Референс добавлен (itemId {stored?.Id ?? item.Id}). Теперь контекст:\n" + text.Compose(ownerId, session, project, project?.RootPath));
        }
        catch (ChatContextException ex)
        {
            return Deny(ex.Message);
        }
    }

    private McpToolCallResult Detach(string ownerId, Session session, JsonObject args)
    {
        if (Gate(ownerId, session, "Изменение контекста чата") is { } denied) return Deny(denied);
        var project = ProjectOf(session);
        if (project is not null && !ProjectCapabilityGuard.Allows(project, ProjectCapabilityArea.FileBound))
            return Deny(ProjectCapabilityGuard.FilesOnDeviceReason);
        var itemId = args["itemId"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (string.IsNullOrWhiteSpace(itemId)) return Deny("Не указан itemId.");
        if (!store.Get(ownerId, session.Id).Refs.Any(i => i.Id == itemId))
            return Deny($"Референса {itemId} в контексте нет. Основной объект снимается через image_focus / audio_focus.");
        store.RemoveRef(ownerId, session.Id, itemId, null);
        return new("Референс убран. Теперь контекст:\n" + text.Compose(ownerId, session, project, project?.RootPath));
    }

    // Изменять контекст может только ход, который видит человек; без гейта — отказ по построению
    private string? Gate(string ownerId, Session session, string action) =>
        turnGate is null
            ? "Изменение контекста недоступно: сервер не проверил ход — отказ по построению."
            : turnGate.Deny(ownerId, session.Id, action);

    private Project? ProjectOf(Session session) => session.ProjectId is { } pid ? projects.GetById(pid) : null;

    // Хвост — один сегмент, id сессии (белый список как у соседних тулсетов); сессия — только своего владельца;
    // флаг — свойство владельца, не хода
    private bool TryResolve(McpToolCallContext context, out Session session,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        session = null!;
        error = null;
        var route = context.RouteTail;
        if (route is null || route.Length is < 1 or > 128
            || !route.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            error = "Некорректный маршрут сервера контекста — вызов отклонён.";
            return false;
        }
        if (sessions.GetOwned(route, context.OwnerId) is not { } owned)
        {
            error = "Чат-вызыватель не найден или принадлежит другому владельцу — доступ к контексту закрыт.";
            return false;
        }
        session = owned;
        return true;
    }

    private static McpToolCallResult Deny(string text) => new(text, IsError: true);
}
