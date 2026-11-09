using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Knowledge;
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
    // Датасет проекта чата для узкой рамки knowledge_search — тот же стор, которым DifyToolset
    // резолвит дефолтную базу: расхождение формул дало бы обход — одна база открыта одним
    // тулсетом и закрыта соседним (находка R-S2a-3b)
    ClaudeHomeServer.Services.Knowledge.WorkspaceKnowledgeStore workspaceKnowledge,
    ClaudeHomeServer.Services.Mcp.McpRegistry mcpRegistry,
    ClaudeHomeServer.Services.Mcp.McpStatusStore mcpStatus,
    SessionManager sessions,
    ITurnFrameSource frames) : IMcpParameterizedToolset
{
    // Имя сервера = первый сегмент маршрута POST /mcp/personas/{sessionId}
    public const string ServerName = McpEndpoints.PersonasName;

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

        // Узкая рамка хода (общий проект, S2a-3b, решение 29.10; дополнения по ревью R-S2a-3b
        // от 30.10): персоны — личное пространство владельца: глобальные персоны (носят его
        // личную память), управление персонами (создание/правка/удаление, автоматизация, реестр
        // и доступы MCP, дефолт, аватар, сборка команды) закрыты в его общем чате всем, включая
        // самого владельца. Так же закрыты: сферные персоны и их память, AI-подбор привязок
        // (каталог подбора — всё пространство владельца) и поиск по базам вне проекта чата.
        // Проектная команда этого чата остаётся: personas_list по проекту, persona_ask к
        // проектным персонам, чтение и правка привязок проектной персоны. Вердикт только читается.
        var frame = frames.Frame(context.CallerSessionId, context.OwnerId);
        var narrow = frame == TurnFrameKind.Narrow;
        if (narrow && (tool is "personas_create" or "personas_update" or "personas_delete"
            or "personas_set_default" or "personas_mcp_list" or "personas_mcp_grant"
            or "personas_generate_avatar" or "personas_ai_team"
            || tool.StartsWith("personas_automation_", StringComparison.Ordinal)))
            return Deny(NarrowManageText);

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
                // Узкая рамка: глобальные персоны — личное пространство владельца, scope
                // global/all в общем проекте закрыты целиком
                if (narrow && scope is "global" or "all") return Deny(NarrowGlobalText);
                if (scope == "all") return Json(personas.GetByOwner(ownerId));
                if (scope == "project" && projectId is null)
                    return Deny("Текущая сессия вне проекта — проектных персон здесь нет "
                        + "(используй scope \"global\" или \"all\").");
                if (scope == "project")
                    return Json(personas.GetProjectTeam(ownerId, projectId));
                if (scope == "global")
                    return Json(personas.GetByOwner(ownerId)
                        .Where(p => p.Scope == PersonaScope.Global).ToList());
                // context: глобальные + проекта чата + кросс-проектные привязки персоны.
                // В узкой рамке ambient-глобальные из списка убираются: они не контекст этого
                // проекта. Явно привязанные к проекту (extraPersonaIds) остаются — это
                // конфигурация самой проектной персоны, не личное пространство владельца.
                // Сферные убираются целиком (дефект 837bb1c8): личная группировка владельца
                // закрыта так же, как persona_ask (NarrowSpherePersonaText), — список, из
                // которого нельзя спросить, модели не нужен
                var contextList = personas.GetForContextWithExtras(ownerId, projectId,
                    extraProjectIds, extraPersonaIds);
                if (narrow)
                    contextList = contextList
                        .Where(p => !PersonaZone.IsSpherePersona(p)
                            && (p.Scope != PersonaScope.Global || extraPersonaIds.Contains(p.Id)))
                        .ToList();
                return Json(contextList);
            }

            case "personas_get":
            {
                if (personas.Get(StringArg(arguments, "id"), ownerId) is not { } found)
                    return Deny($"Персона {StringArg(arguments, "id")} не найдена.");
                if (narrow && found.Scope == PersonaScope.Global) return Deny(NarrowGlobalText);
                // Профиль сферной персоны — та же личная группировка, что закрытая память и
                // persona_ask (дефект 837bb1c8)
                if (narrow && PersonaZone.IsSpherePersona(found)) return Deny(NarrowSpherePersonaText);
                return Json(found);
            }

            case "personas_set_default":
            {
                // Анти-рекурсия — тот же гейт, что [DenyOnDelegatedTurn] на REST-паре;
                // гейт по чату-вызывателю (онбординг — любая персона, обычный чат — только
                // руководитель своего проекта) проверяет crud по callerSessionId.
                // failOpenWhenUnknown: false формально недостижим (сессия резолвится выше
                // TryResolve и сюда попадает только непустой id) — fail-closed защита на
                // будущее, если гейт вынесут из-под резолва
                var gate = DelegatedTurnGate.Decide(sessions, ownerId, session.Id,
                    "Назначение дефолт-персоны",
                    alsoWhenExecutorSuppressed: false,
                    allowInTeamImplement: false,
                    failOpenWhenUnknown: false);
                if (!gate.Allowed) return Deny(gate.DenyText!);
                return Unwrap(await crud.MakeDefaultAsync(ownerId,
                    StringArg(arguments, "personaId"), session.Id));
            }

            case "personas_create":
            {
                if (ScopeDenial(arguments, null, isCreate: true) is { } createDenied) return Deny(createDenied);
                PersonasCrudService.AutoVoiceResult? autoVoice = null;
                var createdResult = Unwrap(await crud.CreateAsync(ownerId,
                    BuildCreateRequest(arguments, projectId), session.Id, onAutoVoice: r => autoVoice = r));
                if (createdResult.IsError) return createdResult;
                // Автоподбор голоса best-effort: без голоса персона говорит голосом инстанса, и
                // модель должна знать почему. Голос задан явно (в т.ч. пустым) — подбора не было
                return autoVoice switch
                {
                    PersonasCrudService.AutoVoiceResult.Failed => Text(createdResult.Text
                        + "\n\nГолос подобрать не удалось — персона говорит голосом инстанса. "
                        + "Задай его через personas_update (voice, voiceRole)."),
                    PersonasCrudService.AutoVoiceResult.NoneFits => Text(createdResult.Text
                        + "\n\nПодходящего голоса в каталоге не нашлось — персона говорит голосом "
                        + "инстанса. Если нужен свой — задай через personas_update (voice, voiceRole)."),
                    _ => createdResult,
                };
            }

            case "personas_update":
            {
                var id = StringArg(arguments, "id");
                var current = personas.Get(id, ownerId);
                if (ScopeDenial(arguments, current, isCreate: false) is { } updateDenied)
                    return Deny(updateDenied);
                // Голос проверяем ДО любых правок: отказ не должен оставлять персону полуизменённой
                var voicePatch = VoicePatch(arguments, current);
                if (voicePatch is not null && PersonasCrudService.VoiceError(voicePatch) is { } voiceError)
                    return Deny(voiceError);
                // Привязки — отдельным путём (как stdio): себе менять нельзя
                if (arguments.ContainsKey("bindings"))
                {
                    if (selfPersonaId is not null && id == selfPersonaId)
                        return Deny("Персона не может менять собственные привязки — попроси об этом пользователя.");
                    var setResult = Unwrap(await crud.SetBindingsAsync(ownerId, id, BindingRequests(arguments, "bindings")));
                    if (setResult.IsError) return setResult;
                }
                var req = BuildUpdateRequest(arguments, projectId);
                if (req is not null)
                {
                    // С голосом событие «updated» шлёт запись голоса — одно на всю правку
                    var updated = Unwrap(await crud.UpdateAsync(ownerId, id, req, session.Id,
                        broadcast: voicePatch is null));
                    if (updated.IsError || voicePatch is null) return updated;
                }
                // Голос — своим путём (у него своя запись и валидация, как PUT /voice)
                if (voicePatch is not null)
                    return Unwrap(await crud.SetVoiceAsync(ownerId, id, voicePatch));
                // Изменены только привязки — вернуть актуальную персону (семантика stdio)
                return personas.Get(id, ownerId) is { } fresh
                    ? Json(fresh) : Deny($"Персона {id} не найдена.");
            }

            case "personas_delete":
                return Unwrap(await crud.DeleteAsync(ownerId, StringArg(arguments, "id"), null),
                    okText: $"Персона {StringArg(arguments, "id")} удалена (вместе с её памятью).");

            case "personas_bindings_list":
            {
                if (personas.Get(StringArg(arguments, "id"), ownerId) is not { } bp)
                    return Deny($"Персона {StringArg(arguments, "id")} не найдена.");
                // Привязки глобальной персоны — её личная конфигурация (личный vault, личные
                // заметки); в общем проекте закрыта вместе с самой персоной. Привязки сферной —
                // закрыты так же (дефект 837bb1c8)
                if (narrow && bp.Scope == PersonaScope.Global) return Deny(NarrowGlobalText);
                if (narrow && PersonaZone.IsSpherePersona(bp)) return Deny(NarrowSpherePersonaText);
                return Json(bp.Bindings ?? []);
            }

            case "personas_suggest_bindings":
            {
                var id = StringArg(arguments, "id");
                if (personas.Get(id, ownerId) is not { } target) return Deny($"Персона {id} не найдена.");
                if (narrow && target.Scope == PersonaScope.Global) return Deny(NarrowGlobalText);
                // Узкая рамка (R-S2a-3b): каталог подбора — всё пространство владельца: его проекты,
                // заметки и базы (PersonasCrudService.AppendBindingCatalog), а не только датасеты;
                // сузить его до проекта чата — значит пересобрать весь промпт подбора. Инструмент
                // закрыт целиком (решение постановщика): id личных целей уходил в общий чат
                // кандидатами подсказки
                if (narrow) return Deny(NarrowSuggestText);
                try { return Json(new { candidates = await crud.SuggestBindingsAsync(ownerId, target, ct: ct) }); }
                catch (Exception ex) { return Deny($"Не удалось подобрать привязки: {ex.Message}"); }
            }

            case "personas_bindings_set":
            {
                var id = StringArg(arguments, "id");
                if (selfPersonaId is not null && id == selfPersonaId)
                    return Deny("Персона не может менять собственные привязки — попроси об этом пользователя.");
                var bindingRequests = BindingRequests(arguments, "bindings");
                if (narrow)
                {
                    // Цель вызова — те же точки отдачи персоны, что personas_get/bindings_list.
                    // Прежняя проверка знала только Global (дефект 67dcf4c2, находка R-S2a-5):
                    // привязки сферной персоны — её личная конфигурация, закрыта вместе с ней
                    if (personas.Get(id, ownerId) is { } target)
                    {
                        if (target.Scope == PersonaScope.Global) return Deny(NarrowGlobalText);
                        if (PersonaZone.IsSpherePersona(target)) return Deny(NarrowSpherePersonaText);
                    }
                    // Цели привязок — только принадлежащие проекту чата (тот же дефект): без проверки
                    // SetBindingsAsync применял к проектной персоне привязку к личной базе
                    // (id из подсказки) и другие чужие цели — личное пространство въезжало
                    // в конфигурацию проектной персоны через дверь, оставленную открытой
                    if (HasForeignBindingTarget(bindingRequests, session, ownerId))
                        return Deny(NarrowBindingTargetsText);
                }
                return Unwrap(await crud.SetBindingsAsync(ownerId, id, bindingRequests));
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
                if (narrow)
                {
                    // Узкая рамка (R-S2a-3b): только база проекта чата — та же формула дефолтного
                    // датасета и те же тексты отказа, что у DifyToolset (там датасеты вне проекта
                    // закрыты этим же решением). Прежняя проверка шла по полному каталогу
                    // владельца: id личной базы или «Заметок», попавший в общий чат через
                    // подсказку привязок, возвращал их содержимое
                    var projectDataset = ChatProjectDatasetId(session, ownerId);
                    if (string.IsNullOrEmpty(projectDataset))
                        return Deny("В общем проекте доступна только база знаний этого проекта — "
                            + "у этого проекта её нет.");
                    if (datasetId != projectDataset)
                        return Deny("В этом чате доступна только база знаний этого проекта.");
                }
                else if ((await bindings.KnowledgeTargetsAsync(ownerId)).All(d => d.Id != datasetId))
                {
                    // Wide: только датасеты, доступные владельцу (чужие скрыты). Привязки персоны
                    // здесь не проверяются — проверяется принадлежность каталогу владельца
                    // (прежняя формулировка в таблице шага не соответствовала коду, исправлено
                    // по R-S2a-3b)
                    return Deny("База знаний не найдена или недоступна");
                }

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
                    allowInTeamImplement: false,
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
                // Глобальная персона отвечает со своей долгой памятью и своими привязками —
                // это личная память владельца; в общем проекте она закрыта (тот же класс,
                // что и personas_list global). Сферная — закрыта так же (R-S2a-3b, решение
                // 30.10): сфера — личная группировка владельца, непоследовательно резать
                // полку её памяти и оставлять ответ, приносящий личную память её персоны
                if (narrow && target.Scope == PersonaScope.Global) return Deny(NarrowGlobalText);
                // Та же единая точка правды зоны, что в остальных местах файла (guard
                // PersonaZoneGuardTests: прямых сравнений зоны в бэкенде нет)
                if (narrow && PersonaZone.IsSpherePersona(target)) return Deny(NarrowSpherePersonaText);

                try
                {
                    // Приватность выводится на сервере из сессий-вызывателей, не из аргументов
                    var callerIsPrivate = ConsultationCallerIsPrivateOrUnknown(session,
                        context.CallerSessionId is { } cid ? sessions.GetById(cid) : null);
                    return Text(await ask.AskAsync(ownerId, target, question,
                        OptionalArg(arguments, "context"), ct, callerIsPrivate));
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

    // Запись фактов консультации в память целевой персоны (S2d-5b) закрыта, если вызыватель
    // приватный ИЛИ не определён: сессия маршрута (PrivateToUserId — S2d-1) и сессия из
    // X-Caller-Session-Id (как в MemoryToolset.CallerChatIsPrivate) обе должны быть известны
    // и не приватны. Иначе fail-closed: ответ отдаётся,
    // память не пишется.
    internal static bool ConsultationCallerIsPrivateOrUnknown(Session? routeSession, Session? headerSession) =>
        routeSession is null || headerSession is null
        || routeSession.PrivateToUserId is not null || headerSession.PrivateToUserId is not null;

    // --- Маршрут: /mcp/personas/{sessionId} ---

    /// <summary>Хвост маршрута для конфига хода: единая точка с TryParseRoute.</summary>
    internal static string RouteTail(string sessionId) => sessionId;

    /// <summary>URL эндпоинта в конфиге хода: базовый адрес + маршрут тулсета с хвостом.</summary>
    public static string EndpointFor(string apiUrl, string sessionId) =>
        McpEndpoints.EndpointFor(apiUrl, ServerName) + "/" + RouteTail(sessionId);

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

    // Дефолтный датасет проекта чата — ровно формула DifyToolset.TryResolve (EffectiveRoot
    // + WorkspaceKnowledgeStore): в узкой рамке knowledge_search и dify должны видеть одну и
    // ту же базу, иначе один тулсет обходит отказ соседнего (находка R-S2a-3b).
    // null — у проекта чата базы нет (или чат вне проекта)
    private string? ChatProjectDatasetId(Session session, string ownerId) =>
        session.ProjectId is { } pid && projects.GetById(pid) is { OwnerId: var owner } project
        && owner == ownerId && ProjectCapabilities.ServerContentEnabled(project)
            ? workspaceKnowledge.GetByPath(SessionManager.EffectiveRoot(session, project.RootPath))?.DifyDatasetId
            : null;

    /// <summary>
    /// Узкая рамка (дефект 67dcf4c2, находка ревью R-S2a-5): есть ли среди привязок цель,
    /// не принадлежащая проекту чата. Якорь — тот же, что у knowledge_search выше: проектные
    /// данные этого чата (файлы/заметки/задачи — его id; база — его Dify-датасет). Всё остальное
    /// — чужие проекты и их команды, personal-хранилища (личная база Dify, личный vault),
    /// скиллы/плагины владельца, Tool-ключи (в т.ч. «mcp:» личного реестра) — не цели этого чата.
    /// Тип не разбирается здесь повторно: неизвестный тип и несуществующую цель уже отвергает
    /// существующая валидация (PersonasCrudService.ParseBinding / ValidateAsync) — рамка не
    /// плодит вторую диагностику на тот же случай.
    /// </summary>
    private bool HasForeignBindingTarget(List<PersonaBindingRequest> requests, Session session, string ownerId)
    {
        var projectDataset = ChatProjectDatasetId(session, ownerId);
        foreach (var req in requests)
        {
            if (!Enum.TryParse<PersonaBindingType>(req.Type?.Trim() ?? "", ignoreCase: true, out var type))
                continue;
            var target = req.Target?.Trim() ?? "";
            var belongsToChatProject = type switch
            {
                // Notes.Target — sourceKey: «personal» | projectId (Persona.cs:42-43) — чужой
                // источник (личный vault) не цель этого чата
                PersonaBindingType.Project or PersonaBindingType.ProjectPath or PersonaBindingType.ProjectTasks
                    or PersonaBindingType.Notes => session.ProjectId is { } pid
                    && string.Equals(target, pid, StringComparison.OrdinalIgnoreCase),
                PersonaBindingType.Knowledge => projectDataset is { Length: > 0 } ds
                    && string.Equals(target, ds, StringComparison.OrdinalIgnoreCase),
                _ => false, // ProjectPersonas (чужие команды, сферные/глобальные персоны), Skill, Tool
            };
            if (!belongsToChatProject) return true;
        }
        return false;
    }

    // --- Ответы ---

    // Тексты отказа узкой рамки — фиксированные, без id и названий из личных данных
    // владельца (в аргументах модели они могут лежать — в ответе их быть не должно)
    private const string NarrowManageText =
        "Управление персонами владельца закрыто в общем проекте: персоны, их характеры, "
        + "автоматизация, реестр MCP и сборка команды — личное пространство владельца. "
        + "Проектная команда этого чата доступна: personas_list по проекту и persona_ask "
        + "к проектным персонам.";

    private const string NarrowGlobalText =
        "Глобальные персоны владельца недоступны в общем проекте: они несут его личную "
        + "память и его личные привязки. Доступны персоны этого проекта.";

    private const string NarrowSuggestText =
        "AI-подбор привязок закрыт в общем проекте: каталог подбора — проекты, заметки и "
        + "базы владельца, его личное пространство. Привязки проектной персоны этого чата "
        + "можно задать явно (personas_bindings_set).";

    private const string NarrowSpherePersonaText =
        "Персоны сферы владельца закрыты в общем проекте: сфера — его личная группировка, "
        + "её полка памяти и память её персон — его личное пространство. Доступны персоны "
        + "этого проекта.";

    private const string NarrowBindingTargetsText =
        "Привязки персоны в общем проекте — только к данным этого проекта: его файлам и папкам, "
        + "задачам, заметкам этого проекта и его базе знаний. Другие цели — чужие проекты и их "
        + "команды, личные базы и vault, скиллы и плагины, ключи инструментов — не данные этого "
        + "проекта: их ставит человек в своём чате.";

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

    // Патч контракта для personas_update: слот, которого нет в аргументах, остаётся null
    // («не менять» — PersonaManager.MergeContract), а пустая строка / пустой массив доходят
    // как есть — это явная очистка слота. OptionalArg/ListArg проглотили бы её в null.
    private static PersonaContract? ContractPatchFrom(JsonObject arguments)
    {
        string? Str(string name) => arguments.ContainsKey(name) ? StringArg(arguments, name) : null;
        List<string>? List(string name) => arguments.ContainsKey(name) ? ListArg(arguments, name) ?? [] : null;

        var contract = new PersonaContract
        {
            Character = Str("character"),
            Tone = Str("tone"),
            MustDo = List("mustDo"),
            MustNot = List("mustNot"),
            OutputFormat = Str("outputFormat"),
            SpeechExamples = List("speechExamples"),
        };
        var any = contract.Character is not null || contract.Tone is not null || contract.MustDo is not null
            || contract.MustNot is not null || contract.OutputFormat is not null || contract.SpeechExamples is not null;
        if (!any && OptionalArg(arguments, "systemPrompt") is { } legacy)
        {
            contract.Character = legacy;
            any = true;
        }
        return any ? contract : null;
    }

    // Голос из аргументов personas_create: voice не передан — null (подбор по характеру),
    // пустая строка — пустой объект (без голоса), иначе голос с амплуа voiceRole
    internal static PersonaVoice? CreateVoiceArg(JsonObject arguments)
    {
        if (!arguments.ContainsKey("voice") && !arguments.ContainsKey("voiceRole")) return null;
        return new PersonaVoice
        {
            Voice = NullIfBlank(StringArg(arguments, "voice")),
            Role = NullIfBlank(StringArg(arguments, "voiceRole")),
        };
    }

    // Патч голоса для personas_update: null — голос не трогали. voice "" — сброс (пустой
    // объект); новый voice без voiceRole — нейтральное амплуа (старое могло не подойти новому
    // голосу); один voiceRole — амплуа текущего голоса. Темп переносится от текущего голоса:
    // агент его не задаёт, и смена голоса не должна молча сбрасывать настройку человека
    internal static PersonaVoice? VoicePatch(JsonObject arguments, Persona? current)
    {
        var hasVoice = arguments.ContainsKey("voice");
        var hasRole = arguments.ContainsKey("voiceRole");
        if (!hasVoice && !hasRole) return null;

        var voice = hasVoice ? NullIfBlank(StringArg(arguments, "voice")) : current?.Voice?.Voice;
        if (hasVoice && voice is null) return new PersonaVoice();
        return new PersonaVoice
        {
            Voice = voice,
            Role = hasRole ? NullIfBlank(StringArg(arguments, "voiceRole")) : null,
            Speed = current?.Voice?.Speed,
        };
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PersonaSpecialty? SpecialtyArg(JsonObject arguments) =>
        Enum.TryParse<PersonaSpecialty>(StringArg(arguments, "specialty"), true, out var parsed) ? parsed : null;

    private static PersonaGender? GenderArg(JsonObject arguments) =>
        Enum.TryParse<PersonaGender>(StringArg(arguments, "gender"), true, out var parsed) ? parsed : null;

    internal const string SphereZoneDenial = "Зону персоны сферы меняет только человек";

    // Зону персоны через MCP не расширяют и не выдают: модель не создаёт персон сферы и не уводит
    // персону из сферы (в т.ч. себя) в глобальную — это решение человека. Неизвестное значение scope —
    // отказ, а не молчаливое «Global» (fail-open). Пустой scope при создании — как отсутствие (Global).
    internal static string? ScopeDenial(JsonObject arguments, Persona? current, bool isCreate)
    {
        var fromSphere = current is not null && PersonaZone.IsSpherePersona(current);
        if (!arguments.ContainsKey("scope")) return null;
        if (fromSphere) return SphereZoneDenial;

        var raw = StringArg(arguments, "scope").Trim();
        if (isCreate && raw.Length == 0) return null;
        if (string.Equals(raw, "sphere", StringComparison.OrdinalIgnoreCase)) return SphereZoneDenial;
        if (string.Equals(raw, "project", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "global", StringComparison.OrdinalIgnoreCase)) return null;
        return $"Неизвестный scope «{raw}»: допустимы «project» и «global».";
    }

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
            ProjectId: PersonaZone.IsProjectScope(scope)
                ? OptionalArg(arguments, "projectId") ?? sessionProjectId : null,
            Color: OptionalArg(arguments, "color"),
            Greeting: OptionalArg(arguments, "greeting"),
            MemoryEnabled: BoolArg(arguments, "memoryEnabled"),
            Contract: contract,
            Specialty: SpecialtyArg(arguments),
            Gender: GenderArg(arguments),
            Bindings: arguments.ContainsKey("bindings") ? BindingRequests(arguments, "bindings") : null,
            AutoBindings: BoolArg(arguments, "autoBindings"),
            // Персона из чата не выбирает аватар руками — просим сгенерировать (best-effort)
            AutoAvatar: true,
            AvatarPrompt: OptionalArg(arguments, "avatarPrompt"),
            Handle: OptionalArg(arguments, "handle"),
            ModelTier: OptionalArg(arguments, "modelTier"),
            Voice: CreateVoiceArg(arguments));
    }

    // null — в аргументах не было ни одного изменяемого поля (только привязки)
    // tier-ячейки пробрасываются через ContainsKey + StringArg (а не OptionalArg):
    // семантика бэкенда null = не менять, "" = сбросить к наследованию, иначе значение;
    // OptionalArg проглотил бы "" как null и сброс стал бы недоступен.
    internal static UpdatePersonaRequest? BuildUpdateRequest(JsonObject arguments, string? sessionProjectId)
    {
        string[] fields = ["name", "role", "description", "systemPrompt", "effort", "color", "greeting",
            "memoryEnabled", "scope", "projectId", "handle", "modelTier", "specialty", "gender",
            "character", "tone", "mustDo", "mustNot", "outputFormat", "speechExamples",
            "tierStrong", "tierMedium", "tierWeak"];
        if (!fields.Any(arguments.ContainsKey)) return null;

        PersonaScope? scope = arguments.ContainsKey("scope")
            ? string.Equals(StringArg(arguments, "scope"), "project", StringComparison.OrdinalIgnoreCase)
                ? PersonaScope.Project : PersonaScope.Global
            : null;
        var contract = ContractPatchFrom(arguments);
        return new UpdatePersonaRequest(
            Name: OptionalArg(arguments, "name"),
            Role: arguments.ContainsKey("role") ? StringArg(arguments, "role") : null,
            Description: OptionalArg(arguments, "description"),
            SystemPrompt: contract is null && arguments.ContainsKey("systemPrompt")
                ? StringArg(arguments, "systemPrompt") : null,
            Model: null,
            Effort: arguments.ContainsKey("effort") ? StringArg(arguments, "effort") : null,
            Scope: scope,
            ProjectId: PersonaZone.IsProjectScope(scope)
                ? OptionalArg(arguments, "projectId") ?? sessionProjectId
                : OptionalArg(arguments, "projectId"),
            Color: arguments.ContainsKey("color") ? StringArg(arguments, "color") : null,
            Greeting: arguments.ContainsKey("greeting") ? StringArg(arguments, "greeting") : null,
            MemoryEnabled: BoolArg(arguments, "memoryEnabled"),
            Contract: contract,
            Specialty: SpecialtyArg(arguments),
            Gender: arguments.ContainsKey("gender") ? GenderArg(arguments) : null,
            Handle: OptionalArg(arguments, "handle"),
            ModelTier: arguments.ContainsKey("modelTier") ? StringArg(arguments, "modelTier") : null,
            TierStrong: arguments.ContainsKey("tierStrong") ? StringArg(arguments, "tierStrong") : null,
            TierMedium: arguments.ContainsKey("tierMedium") ? StringArg(arguments, "tierMedium") : null,
            TierWeak: arguments.ContainsKey("tierWeak") ? StringArg(arguments, "tierWeak") : null);
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
