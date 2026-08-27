using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Models;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Персоны владельца (personas_*, persona_ask) поверх HTTP-транспорта — пятый переехавший
/// с node сервер (ADR-012, фаза 2 волна 2). Тяжёлая оркестрация (создание/правка/удаление,
/// дефолт-персона, подбор привязок, аватар, состав команды) живёт в <see cref="PersonasCrudService"/> —
/// общем с REST-контроллером: дублировать её значило бы гарантированный рассинхрон веток.
///
/// Маршрут — <c>POST /mcp/personas/{sessionId}</c>: хвост несёт СЕССИЮ-ВЫЗЫВАТЕЛЯ (эквивалент
/// env PERSONAS_SESSION_ID/PROJECT_ID/SELF_ID/EXTRA_* stdio-ветки), по ней тулсет живьём
/// резолвит проект чата, персону-вызывателя и её кросс-проектные привязки. Сессия обязана
/// принадлежать владельцу токена (GetOwned) — чужая это отказ и пустой tools/list.
///
/// СОСТАВ зависит только от свойств СЕССИИ: ядро — всегда; модули manage/automation — по живой
/// привязке персоны (SectionEnabled), persona_ask — по MentionsToolsEnabled (единая формула
/// SessionManager, её же читает отпечаток сигнатуры запуска). В отличие от
/// stdio-ветки, mentions НЕ зависит от наличия файловых сабагентов в ходе: это свойство ХОДА,
/// и из состава оно убрано (ADR-012, таблица разбора per-turn флагов).
///
/// ГЕЙТЫ: personas_set_default и persona_ask проходят DelegatedTurnGate — тот же, что
/// [DenyOnDelegatedTurn] у их REST-пар (MVC-фильтр к McpTransportController не применяется).
/// Сторож парности со stdio-веткой — PersonasToolsetParityTests.
/// </summary>
public sealed partial class PersonasToolset(
    PersonaManager personas,
    ProjectManager projects,
    PersonaBindingsService bindings,
    PersonaAskService ask,
    PersonaAutomationService automation,
    PersonasCrudService crud,
    KnowledgeService knowledge,
    ClaudeHomeServer.Services.Mcp.McpRegistry mcpRegistry,
    ClaudeHomeServer.Services.Mcp.McpStatusStore mcpStatus,
    SessionManager sessions) : IMcpParameterizedToolset
{
    // Имя сервера = первый сегмент маршрута POST /mcp/personas/{sessionId}
    public const string ServerName = "personas";

    // Ответы — как у stdio-ветки (JSON.stringify): camelCase, кириллица без экранирования
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // Разрешённые операторы фильтра метаданных Dify — копия набора REST-эндпоинта
    private static readonly HashSet<string> MetadataFilterOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "contains", "not contains", "start with", "end with", "is", "is not", "empty", "not empty",
    };

    public string Name => ServerName;
    public string Version => "1.0.0";

    public IReadOnlyList<McpToolSchema> Tools => [];

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context)
    {
        if (!TryResolve(context, out var session, out var persona, out _)) return [];
        var manage = ManageEnabled(context.OwnerId, session, persona);
        // Единая формула mentions (SessionManager.MentionsToolsEnabled) — её же читает
        // отпечаток сигнатуры запуска (shape): две формулы расходились при единственной
        // персоне владельца (блокер приёмки волны 2.1)
        var mentions = sessions.MentionsToolsEnabled(context.OwnerId, session, persona);
        var automationOn = bindings.SectionEnabled(context.OwnerId, persona, "personas-automation");
        var inProject = session.ProjectId is not null;
        // Порядок групп — как в stdio-ветке (index.js): ядро, manage-create/update,
        // привязки (+manage-набор внутри), knowledge_search, automation, хвост manage, mentions
        var tools = new List<McpToolSchema>(CoreTools(inProject));
        if (manage) tools.AddRange(ManageHeadTools(inProject));
        tools.AddRange(BindingsReadTools);
        if (manage) tools.AddRange(ManageBindingsTools);
        tools.Add(KnowledgeSearchTool);
        if (automationOn) tools.AddRange(AutomationTools);
        if (manage) tools.AddRange(ManageTailTools(inProject));
        if (mentions) tools.AddRange(MentionsTools);
        return tools;
    }

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        if (!TryResolve(context, out var session, out var persona, out var routeError))
            return Deny(routeError);

        var ownerId = context.OwnerId;
        var projectId = session.ProjectId;
        var selfPersonaId = session.PersonaId;
        var manage = ManageEnabled(ownerId, session, persona);
        var mentions = sessions.MentionsToolsEnabled(ownerId, session, persona);
        var automationOn = bindings.SectionEnabled(ownerId, persona, "personas-automation");

        // Defense-in-depth, как у stdio: выключенный модуль не отрабатывает и при ошибке
        // экспозиции состава. Для mentions это не косметика: без проверки вызов persona_ask
        // при выключенном tool:consultants запускал бы ПЛАТНЫЙ one-shot ход другой персоны
        if (!manage && ManageToolNames.Contains(tool))
            return Deny("Инструмент управления персонами недоступен этой персоне (модуль manage выключен). "
                + "Попроси пользователя включить его привязкой tool:personas-manage.");
        if (!mentions && MentionsToolNames.Contains(tool))
            return Deny("Инструмент persona_ask недоступен этой персоне (привязка tool:consultants выключена). "
                + "Попроси пользователя включить её.");
        if (!automationOn && AutomationToolNames.Contains(tool))
            return Deny("Инструменты правил проактивности недоступны этой персоне (модуль automation выключен). "
                + "Попроси пользователя включить его привязкой tool:personas-automation.");

        // Кросс-проектные привязки вызывающей персоны — живой эквивалент PERSONAS_EXTRA_*
        var externalScopes = bindings.BuildExternalPersonaScopes(ownerId, persona);
        var extraProjectIds = externalScopes.Where(s => s.PersonaId is null)
            .Select(s => s.ProjectId).Distinct().ToList();
        var extraPersonaIds = externalScopes.Where(s => s.PersonaId is not null)
            .Select(s => s.PersonaId!).Distinct().ToList();

        switch (tool)
        {
            case "personas_list":
            {
                var scope = StringArg(arguments, "scope") is { Length: > 0 } s ? s : "context";
                if (scope == "all") return Json(personas.GetByOwner(ownerId));
                if (scope == "project" && projectId is null)
                    return Deny("Текущая сессия вне проекта — проектных персон здесь нет "
                        + "(используй scope \"global\" или \"all\").");
                if (scope == "project")
                    return Json(personas.GetByOwner(ownerId)
                        .Where(p => p.Scope == PersonaScope.Project && p.ProjectId == projectId).ToList());
                if (scope == "global")
                    return Json(personas.GetByOwner(ownerId)
                        .Where(p => p.Scope == PersonaScope.Global).ToList());
                // context: глобальные + проекта чата + кросс-проектные привязки персоны
                var result = personas.GetForContext(ownerId, projectId).ToList();
                if (extraProjectIds.Count > 0 || extraPersonaIds.Count > 0)
                {
                    var seen = result.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
                    var extraSet = extraPersonaIds.ToHashSet(StringComparer.Ordinal);
                    foreach (var p in personas.GetByOwner(ownerId))
                    {
                        if (seen.Contains(p.Id)) continue;
                        var included = extraSet.Contains(p.Id)
                            || (p.Scope == PersonaScope.Project && p.ProjectId is not null
                                && extraProjectIds.Contains(p.ProjectId));
                        if (!included) continue;
                        result.Add(p);
                        seen.Add(p.Id);
                    }
                }
                return Json(result);
            }

            case "personas_get":
                return personas.Get(StringArg(arguments, "id"), ownerId) is { } found
                    ? Json(found)
                    : Deny($"Персона {StringArg(arguments, "id")} не найдена.");

            case "personas_set_default":
            {
                // Анти-рекурсия — тот же гейт, что [DenyOnDelegatedTurn] на REST-паре;
                // онбординг-гейт (только сессия знакомства) проверяет crud по callerSessionId.
                // failOpenWhenUnknown: false формально недостижим (сессия резолвится выше
                // TryResolve и сюда попадает только непустой id) — fail-closed защита на
                // будущее, если гейт вынесут из-под резолва
                var gate = DelegatedTurnGate.Decide(sessions, ownerId, session.Id,
                    "Назначение дефолт-персоны",
                    alsoWhenExecutorSuppressed: false,
                    allowInTeamImplement: false, allowInWorkLoop: false,
                    failOpenWhenUnknown: false);
                if (!gate.Allowed) return Deny(gate.DenyText!);
                return Unwrap(await crud.MakeDefaultAsync(ownerId,
                    StringArg(arguments, "personaId"), session.Id));
            }

            case "personas_create":
                return Unwrap(await crud.CreateAsync(ownerId,
                    BuildCreateRequest(arguments, projectId), session.Id));

            case "personas_update":
            {
                var id = StringArg(arguments, "id");
                // Привязки — отдельным путём (как stdio): себе менять нельзя
                if (arguments.ContainsKey("bindings"))
                {
                    if (selfPersonaId is not null && id == selfPersonaId)
                        return Deny("Персона не может менять собственные привязки — попроси об этом пользователя.");
                    var setResult = Unwrap(await crud.SetBindingsAsync(ownerId, id, BindingRequests(arguments, "bindings")));
                    if (setResult.IsError) return setResult;
                }
                var req = BuildUpdateRequest(arguments, projectId);
                // Изменены только привязки — вернуть актуальную персону (семантика stdio)
                if (req is null)
                    return personas.Get(id, ownerId) is { } current
                        ? Json(current) : Deny($"Персона {id} не найдена.");
                return Unwrap(await crud.UpdateAsync(ownerId, id, req, session.Id));
            }

            case "personas_delete":
                return Unwrap(await crud.DeleteAsync(ownerId, StringArg(arguments, "id"), null),
                    okText: $"Персона {StringArg(arguments, "id")} удалена (вместе с её памятью).");

            case "personas_bindings_list":
                return personas.Get(StringArg(arguments, "id"), ownerId) is { } bp
                    ? Json(bp.Bindings ?? [])
                    : Deny($"Персона {StringArg(arguments, "id")} не найдена.");

            case "personas_suggest_bindings":
            {
                var id = StringArg(arguments, "id");
                if (personas.Get(id, ownerId) is not { } target) return Deny($"Персона {id} не найдена.");
                try { return Json(new { candidates = await crud.SuggestBindingsAsync(ownerId, target, ct: ct) }); }
                catch (Exception ex) { return Deny($"Не удалось подобрать привязки: {ex.Message}"); }
            }

            case "personas_bindings_set":
            {
                var id = StringArg(arguments, "id");
                if (selfPersonaId is not null && id == selfPersonaId)
                    return Deny("Персона не может менять собственные привязки — попроси об этом пользователя.");
                return Unwrap(await crud.SetBindingsAsync(ownerId, id, BindingRequests(arguments, "bindings")));
            }

            case "personas_mcp_list":
            {
                var servers = mcpRegistry.GetByOwner(ownerId);
                var personaId = StringArg(arguments, "id");
                // Allow-модель: «нет записи = не выдан» (как у REST binding-targets)
                Dictionary<string, bool>? enabledByKey = null;
                if (personaId.Length > 0 && personas.Get(personaId, ownerId) is { } grantee)
                    // Ключ каталога привязок — полный, с префиксом «mcp:» (см. McpServerGranted)
                    enabledByKey = servers.ToDictionary(s => s.Key,
                        s => bindings.McpServerGranted(grantee, "mcp:" + s.Key), StringComparer.Ordinal);
                return Json(servers.Select(s => enabledByKey is null
                    ? (object)new
                    {
                        key = s.Key,
                        label = s.Label,
                        transport = s.Transport,
                        status = mcpStatus.Get(ownerId, s.Key)?.Status,
                    }
                    : new
                    {
                        key = s.Key,
                        label = s.Label,
                        transport = s.Transport,
                        status = mcpStatus.Get(ownerId, s.Key)?.Status,
                        enabledForPersona = enabledByKey.GetValueOrDefault(s.Key),
                    }));
            }

            case "personas_mcp_grant":
            {
                var id = StringArg(arguments, "id");
                if (selfPersonaId is not null && id == selfPersonaId)
                    return Deny("Персона не может менять собственные доступы — попроси об этом пользователя.");
                if (personas.Get(id, ownerId) is not { } grantee) return Deny($"Персона {id} не найдена.");
                var key = StringArg(arguments, "key").Trim();
                if (key.Length == 0) return Deny("Укажи key MCP-сервера (см. personas_mcp_list).");
                var revoke = arguments["revoke"] is JsonValue rv && rv.TryGetValue<bool>(out var r) && r;
                var target = "mcp:" + key;
                // Точечная правка: текущие привязки минус цель mcp:<key> плюс свежая
                var list = (grantee.Bindings ?? [])
                    .Where(b => !(b.Type == PersonaBindingType.Tool
                        && string.Equals(b.Target, target, StringComparison.Ordinal)))
                    .Select(b => new PersonaBindingRequest(b.Type.ToString(), b.Target, b.Path,
                        b.Condition, b.Mode.ToString()))
                    .ToList();
                // mode работает в обеих моделях доступа: auto — выдан/включён, off — отозван
                list.Add(new PersonaBindingRequest("tool", target, null, null, revoke ? "off" : "auto"));
                var applied = Unwrap(await crud.SetBindingsAsync(ownerId, id, list));
                if (applied.IsError) return applied;
                return Text(revoke
                    ? $"Сервер «{key}» отозван у персоны {id}."
                    : $"Сервер «{key}» выдан персоне {id}.");
            }

            case "knowledge_search":
            {
                var datasetId = StringArg(arguments, "datasetId");
                var query = StringArg(arguments, "query");
                if (datasetId.Length == 0 || query.Length == 0) return Deny("Нужны datasetId и query");
                if (!knowledge.IsConfigured) return Deny("База знаний (Dify) не настроена");
                // Только датасеты, доступные владельцу (чужие скрыты)
                if ((await bindings.KnowledgeTargetsAsync(ownerId)).All(d => d.Id != datasetId))
                    return Deny("База знаний не найдена или недоступна");

                IReadOnlyList<KnowledgeMetadataFieldInfo> fields;
                try { fields = await knowledge.ListMetadataFieldsAsync(datasetId); }
                catch { fields = []; }

                List<KnowledgeMetadataFilter>? filters = null;
                if (arguments["filters"] is JsonArray rawFilters && rawFilters.Count > 0)
                {
                    filters = [];
                    foreach (var node in rawFilters)
                    {
                        if (node is not JsonObject f) continue;
                        var name = StringArg(f, "name");
                        var op = StringArg(f, "operator");
                        if (name.Length == 0 || op.Length == 0) return Deny("У фильтра нужны name и operator");
                        if (!MetadataFilterOperators.Contains(op))
                            return Deny($"Недопустимый оператор «{op}»; допустимы: "
                                + string.Join(", ", MetadataFilterOperators));
                        if (fields.All(x => !string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                            return Deny($"В этой базе знаний нет поля метаданных «{name}» — фильтровать по нему нельзя. "
                                + "Доступные поля: " + string.Join(", ", fields.Select(x => x.Name)));
                        filters.Add(new KnowledgeMetadataFilter(name, op, OptionalArg(f, "value")));
                    }
                }

                var topK = arguments["topK"] is JsonValue tv && tv.TryGetValue<int>(out var k)
                    && k is > 0 and <= 20 ? k : 6;
                var chunks = await knowledge.RetrieveAsync(datasetId, query, topK, filters,
                    OptionalArg(arguments, "logic") ?? "and");
                return Json(new
                {
                    metadataFields = fields.Select(x => new { x.Name, x.Type }),
                    hits = chunks.Select(c => new
                    {
                        document = c.DocumentName,
                        score = c.Score,
                        content = c.Content,
                        metadata = c.Metadata,
                    }),
                });
            }

            case "personas_automation_list":
                return personas.Get(StringArg(arguments, "id"), ownerId) is { } ap
                    ? Json(ap.AutomationRules ?? [])
                    : Deny($"Персона {StringArg(arguments, "id")} не найдена.");

            case "personas_automation_create":
                return Unwrap(await crud.AddAutomationRuleAsync(ownerId, StringArg(arguments, "id"),
                    BuildAutomationRequest(arguments)));

            case "personas_automation_update":
                return Unwrap(await crud.UpdateAutomationRuleAsync(ownerId, StringArg(arguments, "id"),
                    StringArg(arguments, "ruleId"), BuildAutomationRequest(arguments)));

            case "personas_automation_delete":
                return Unwrap(await crud.DeleteAutomationRule(ownerId, StringArg(arguments, "id"),
                        StringArg(arguments, "ruleId")),
                    okText: $"Правило {StringArg(arguments, "ruleId")} удалено.");

            case "personas_automation_test":
            {
                var id = StringArg(arguments, "id");
                var ruleId = StringArg(arguments, "ruleId");
                if (personas.Get(id, ownerId) is not { } rulePersona) return Deny($"Персона {id} не найдена.");
                if (rulePersona.AutomationRules?.Any(r => r.Id == ruleId) != true)
                    return Deny($"Правило {ruleId} не найдено.");
                _ = automation.TestAsync(ownerId, id, ruleId);   // в фон — ход может быть долгим
                return Text("Правило запущено вручную (в фоне).");
            }

            case "personas_generate_avatar":
            {
                var id = StringArg(arguments, "id");
                var generated = await crud.GenerateAvatarAsync(ownerId, id,
                    new GenerateAvatarRequest(OptionalArg(arguments, "prompt"), 1));
                var unwrapped = Unwrap(generated);
                if (unwrapped.IsError) return unwrapped;
                // Кандидат сразу становится аватаром — как двухшаговый путь stdio-ветки
                var file = FirstCandidate((generated as ObjectResult)?.Value);
                if (file is null) return Deny("Генерация не вернула ни одного кандидата.");
                return Unwrap(await crud.SelectAvatarAsync(ownerId, id, new SelectAvatarRequest(file)));
            }

            case "personas_ai_team":
            {
                var targetProjectId = OptionalArg(arguments, "projectId") ?? projectId;
                if (targetProjectId is null)
                    return Deny("Нужен projectId: текущая сессия вне проекта — укажи projectId проекта, "
                        + "под который формируется команда.");
                return Unwrap(await crud.AiTeamAsync(ownerId, targetProjectId,
                    StringArg(arguments, "prompt"), ct));
            }

            case "persona_ask":
            {
                // Анти-рекурсия: с делегированного хода персону не переспрашивают
                // (failOpenWhenUnknown: false — недостижимая сейчас, но дешёвая страховка,
                // см. комментарий у personas_set_default)
                var gate = DelegatedTurnGate.Decide(sessions, ownerId, session.Id,
                    "Вопрос другой персоне",
                    alsoWhenExecutorSuppressed: false,
                    allowInTeamImplement: false, allowInWorkLoop: false,
                    failOpenWhenUnknown: false);
                if (!gate.Allowed) return Deny(gate.DenyText!);

                var question = StringArg(arguments, "question");
                if (string.IsNullOrWhiteSpace(question)) return Deny("Пустой вопрос");
                var personaId = StringArg(arguments, "personaId");
                var handle = personaId.Length > 0 ? "" : StringArg(arguments, "handle").TrimStart('@').Trim();
                if (personaId.Length == 0 && handle.Length == 0)
                    return Deny("Укажи handle или personaId персоны.");

                Persona? target;
                if (personaId.Length > 0)
                {
                    // Тот же пул достижимости, что у резолва по handle — personaId не лазейка
                    target = personas.GetReachable(ownerId, personaId, projectId, extraProjectIds, extraPersonaIds);
                    if (target is null) return Deny("Персона не найдена или недоступна в этом контексте");
                }
                else
                {
                    var candidates = personas.ResolveHandleCandidates(ownerId, handle, projectId,
                        extraProjectIds, extraPersonaIds);
                    if (candidates.Count == 0) return Deny($"Персона @{handle} не найдена");
                    if (candidates.Count > 1)
                        return Deny($"Персона @{handle} есть в нескольких проектах — уточни personaId. "
                            + "Повтори вызов с personaId одного из кандидатов:\n"
                            + string.Join("\n", candidates.Select(c =>
                                $"- personaId={c.Id} — "
                                + (string.IsNullOrEmpty(c.Role) ? c.Name : $"{c.Role} ({c.Name})")
                                + (c.ProjectId is null ? "" : $", проект «{projects.GetById(c.ProjectId)?.Name}»"))));
                    target = candidates[0];
                }
                if (selfPersonaId is not null && target.Id == selfPersonaId)
                    return Deny("Это твой собственный id/handle — отвечай сам, спрашивать себя не нужно.");

                try
                {
                    return Text(await ask.AskAsync(ownerId, target, question, OptionalArg(arguments, "context"), ct));
                }
                catch (Exception ex)
                {
                    return Deny($"Не удалось получить ответ персоны: {ex.Message}");
                }
            }

            default:
                throw new ArgumentException($"Неизвестный инструмент: {tool}", nameof(tool));
        }
    }

    // --- Маршрут: /mcp/personas/{sessionId} ---

    /// <summary>Хвост маршрута для конфига хода: единая точка с TryParseRoute.</summary>
    internal static string RouteTail(string sessionId) => sessionId;

    /// <summary>URL эндпоинта в конфиге хода: базовый адрес + маршрут тулсета с хвостом.</summary>
    public static string EndpointFor(string apiUrl, string sessionId) =>
        McpHttpTransport.EndpointFor(apiUrl, ServerName) + "/" + RouteTail(sessionId);

    private static bool TryParseRoute(string? route, out string sessionId)
    {
        sessionId = "";
        if (route is null || route.Split('/').Length != 1) return false;
        if (route.Length is < 1 or > 128 || !route.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return false;
        sessionId = route;
        return true;
    }

    /// <summary>
    /// Резолв хвоста в сессию ВЛАДЕЛЬЦА токена + её живую персону. Право на сервер персон —
    /// формула SessionManager.PersonasEnabled (Off-привязка tool:personas, исключение для
    /// групповых чатов), проверяется на КАЖДЫЙ вызов.
    /// </summary>
    private bool TryResolve(McpToolCallContext context, out Session session, out Persona? persona,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        session = null!;
        persona = null;
        if (!TryParseRoute(context.RouteTail, out var sessionId))
        {
            error = "Некорректный маршрут сервера персон — вызов отклонён.";
            return false;
        }
        var owned = sessions.GetOwned(sessionId, context.OwnerId);
        if (owned is null)
        {
            error = "Чат-вызыватель не найден или принадлежит другому владельцу — доступ к персонам закрыт.";
            return false;
        }
        session = owned;
        persona = session.PersonaId is { } pid ? personas.Get(pid, context.OwnerId) : null;
        if (!sessions.PersonasEnabled(context.OwnerId, session, persona))
        {
            error = "Сервер персон недоступен этой персоне (привязка tool:personas выключена). "
                + "Попроси пользователя включить её.";
            return false;
        }
        error = null;
        return true;
    }

    // Модуль manage: тот же гейт, что у SessionManager.BuildPersonasContext — проектный
    // онбординг форсирует его (шаг команды зовёт personas_create), иначе решает привязка
    private bool ManageEnabled(string ownerId, Session session, Persona? persona) =>
        session.OnboardingKind == OnboardingKinds.Project
        || bindings.SectionEnabled(ownerId, persona, "personas-manage");

    // --- Ответы ---

    private static McpToolCallResult Text(string text) => new(text);

    private static McpToolCallResult Json<T>(T value) =>
        new(JsonSerializer.Serialize(value, JsonOpts));

    private static McpToolCallResult Deny(string text) => new(text, IsError: true);

    /// <summary>
    /// ActionResult REST-семантики crud-сервиса → ответ инструмента: 2xx-значение отдаём
    /// JSON'ом (или текстом okText у пустого тела), не-2xx — content-ошибкой с текстом из тела.
    /// Так модель получает ту же диагностику, что видел stdio-сервер в теле HTTP-ответа.
    /// </summary>
    private static McpToolCallResult Unwrap<T>(ActionResult<T> result, string? okText = null) =>
        result.Result is { } action ? Unwrap(action, okText)
        : result.Value is { } value ? Json(value)
        : Text(okText ?? "Готово.");

    private static McpToolCallResult Unwrap(IActionResult result, string? okText = null)
    {
        switch (result)
        {
            case ObjectResult obj:
            {
                var code = obj.StatusCode ?? 200;
                if (code >= 400) return Deny(ErrorText(obj.Value));
                return obj.Value is { } value ? Json(value) : Text(okText ?? "Готово.");
            }
            case StatusCodeResult status:
                return status.StatusCode >= 400
                    ? Deny($"Отказ (HTTP {status.StatusCode}).")
                    : Text(okText ?? "Готово.");
            default:
                return Text(okText ?? "Готово.");
        }
    }

    // Текст отказа из тела ответа: у контроллеров это { error = "…" } либо строка
    private static string ErrorText(object? value) => value switch
    {
        null => "Отказ без объяснения.",
        string s => s,
        _ => value.GetType().GetProperty("error")?.GetValue(value) as string
             ?? JsonSerializer.Serialize(value, JsonOpts),
    };

    // Первый кандидат аватара из анонимного ответа GenerateAvatarAsync ({ candidates = [...] })
    private static string? FirstCandidate(object? value) =>
        value?.GetType().GetProperty("candidates")?.GetValue(value) is IEnumerable<string> files
            ? files.FirstOrDefault()
            : null;

    // --- Аргументы вызова ---

    private static string StringArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static string? OptionalArg(JsonObject arguments, string name)
    {
        var value = StringArg(arguments, name);
        return value.Length == 0 ? null : value;
    }

    private static List<string>? ListArg(JsonObject arguments, string name) =>
        arguments[name] is JsonArray arr && arr.Count > 0
            ? arr.Where(t => t is JsonValue v && v.TryGetValue<string>(out _))
                .Select(t => t!.GetValue<string>()).ToList()
            : null;

    private static bool? BoolArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    // Слоты характера → контракт (как contractFrom stdio-ветки; systemPrompt — legacy-алиас)
    private static PersonaContract? ContractFrom(JsonObject arguments)
    {
        var contract = new PersonaContract();
        var any = false;
        if (OptionalArg(arguments, "character") is { } character) { contract.Character = character; any = true; }
        if (OptionalArg(arguments, "tone") is { } tone) { contract.Tone = tone; any = true; }
        if (ListArg(arguments, "mustDo") is { } mustDo) { contract.MustDo = mustDo; any = true; }
        if (ListArg(arguments, "mustNot") is { } mustNot) { contract.MustNot = mustNot; any = true; }
        if (OptionalArg(arguments, "outputFormat") is { } format) { contract.OutputFormat = format; any = true; }
        if (ListArg(arguments, "speechExamples") is { } examples) { contract.SpeechExamples = examples; any = true; }
        if (!any && OptionalArg(arguments, "systemPrompt") is { } legacy)
        {
            contract.Character = legacy;
            any = true;
        }
        return any ? contract : null;
    }

    private static PersonaSpecialty? SpecialtyArg(JsonObject arguments) =>
        Enum.TryParse<PersonaSpecialty>(StringArg(arguments, "specialty"), true, out var parsed) ? parsed : null;

    private static CreatePersonaRequest BuildCreateRequest(JsonObject arguments, string? sessionProjectId)
    {
        var scope = string.Equals(StringArg(arguments, "scope"), "project", StringComparison.OrdinalIgnoreCase)
            ? PersonaScope.Project : PersonaScope.Global;
        var contract = ContractFrom(arguments);
        return new CreatePersonaRequest(
            Name: StringArg(arguments, "name"),
            Role: OptionalArg(arguments, "role"),
            Description: OptionalArg(arguments, "description"),
            SystemPrompt: contract is null ? OptionalArg(arguments, "systemPrompt") : null,
            Model: null,   // конкретная модель через MCP не задаётся — только уровнями
            Effort: OptionalArg(arguments, "effort"),
            Scope: scope,
            ProjectId: scope == PersonaScope.Project
                ? OptionalArg(arguments, "projectId") ?? sessionProjectId : null,
            Color: OptionalArg(arguments, "color"),
            Greeting: OptionalArg(arguments, "greeting"),
            MemoryEnabled: BoolArg(arguments, "memoryEnabled"),
            Contract: contract,
            Specialty: SpecialtyArg(arguments),
            Bindings: arguments.ContainsKey("bindings") ? BindingRequests(arguments, "bindings") : null,
            AutoBindings: BoolArg(arguments, "autoBindings"),
            // Персона из чата не выбирает аватар руками — просим сгенерировать (best-effort)
            AutoAvatar: true,
            AvatarPrompt: OptionalArg(arguments, "avatarPrompt"),
            Handle: OptionalArg(arguments, "handle"),
            ModelTier: OptionalArg(arguments, "modelTier"));
    }

    // null — в аргументах не было ни одного изменяемого поля (только привязки)
    private static UpdatePersonaRequest? BuildUpdateRequest(JsonObject arguments, string? sessionProjectId)
    {
        string[] fields = ["name", "role", "description", "systemPrompt", "effort", "color", "greeting",
            "memoryEnabled", "scope", "projectId", "handle", "modelTier", "specialty",
            "character", "tone", "mustDo", "mustNot", "outputFormat", "speechExamples"];
        if (!fields.Any(arguments.ContainsKey)) return null;

        PersonaScope? scope = arguments.ContainsKey("scope")
            ? string.Equals(StringArg(arguments, "scope"), "project", StringComparison.OrdinalIgnoreCase)
                ? PersonaScope.Project : PersonaScope.Global
            : null;
        var contract = ContractFrom(arguments);
        return new UpdatePersonaRequest(
            Name: OptionalArg(arguments, "name"),
            Role: arguments.ContainsKey("role") ? StringArg(arguments, "role") : null,
            Description: OptionalArg(arguments, "description"),
            SystemPrompt: contract is null && arguments.ContainsKey("systemPrompt")
                ? StringArg(arguments, "systemPrompt") : null,
            Model: null,
            Effort: arguments.ContainsKey("effort") ? StringArg(arguments, "effort") : null,
            Scope: scope,
            ProjectId: scope == PersonaScope.Project
                ? OptionalArg(arguments, "projectId") ?? sessionProjectId
                : OptionalArg(arguments, "projectId"),
            Color: arguments.ContainsKey("color") ? StringArg(arguments, "color") : null,
            Greeting: arguments.ContainsKey("greeting") ? StringArg(arguments, "greeting") : null,
            MemoryEnabled: BoolArg(arguments, "memoryEnabled"),
            Contract: contract,
            Specialty: SpecialtyArg(arguments),
            Handle: OptionalArg(arguments, "handle"),
            ModelTier: arguments.ContainsKey("modelTier") ? StringArg(arguments, "modelTier") : null);
    }

    private static List<PersonaBindingRequest> BindingRequests(JsonObject arguments, string name)
    {
        var list = new List<PersonaBindingRequest>();
        if (arguments[name] is not JsonArray arr) return list;
        foreach (var node in arr)
        {
            if (node is not JsonObject b) continue;
            list.Add(new PersonaBindingRequest(
                StringArg(b, "type"), StringArg(b, "target"),
                OptionalArg(b, "path"), OptionalArg(b, "condition"), OptionalArg(b, "mode")));
        }
        return list;
    }

    private static AutomationRuleRequest BuildAutomationRequest(JsonObject arguments)
    {
        AutomationTriggerType? triggerType =
            Enum.TryParse<AutomationTriggerType>(StringArg(arguments, "triggerType"), true, out var tt) ? tt : null;
        AutomationActionWeight? weight =
            Enum.TryParse<AutomationActionWeight>(StringArg(arguments, "actionWeight"), true, out var aw) ? aw : null;
        // triggerArgs — гибкий объект: форма зависит от типа триггера (см. справочник сервера)
        Dictionary<string, JsonElement>? triggerArgs = null;
        if (arguments["triggerArgs"] is JsonObject argsObj)
            triggerArgs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsObj.ToJsonString());
        return new AutomationRuleRequest(
            Name: OptionalArg(arguments, "name"),
            Enabled: BoolArg(arguments, "enabled"),
            TriggerType: triggerType,
            TriggerArgs: triggerArgs,
            ConditionOnlyIf: OptionalArg(arguments, "conditionOnlyIf"),
            QuietFrom: OptionalArg(arguments, "quietFrom"),
            QuietTo: OptionalArg(arguments, "quietTo"),
            MinIntervalMinutes: arguments["minIntervalMinutes"] is JsonValue mv
                && mv.TryGetValue<int>(out var mi) ? mi : null,
            ActionWeight: weight,
            ActionInstruction: OptionalArg(arguments, "actionInstruction"),
            RememberInHistory: BoolArg(arguments, "rememberInHistory"),
            ActionExpiresAfterMinutes: arguments["actionExpiresAfterMinutes"] is JsonValue ev
                && ev.TryGetValue<int>(out var ttl) ? ttl : null);
    }
}
