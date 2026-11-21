using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Services.Team;

namespace ClaudeHomeServer.Services.Sessions;

// Слой персоны в контексте хода (S3-4, docs/research/session-manager-split-2026-11.md):
// провайдер текущей персоны и её привязок, кандидаты на консультацию и разделение на
// файловых сабагентов / persona_ask, план .md-агентов и pmem-серверов, подсказка об
// упоминаниях, контекст MCP-сервера персон, дополнительные запреты инструментов и
// ленивая уборка адаптеров при изменении профиля персоны.
// Тела перенесены из SessionManager дословно; фасад держит делегирующие обёртки прежних
// сигнатур — на них сидют точки сборки контекста и внешние тулсеты (PersonasToolset,
// ProjectsController). Голосовая оговорка остаётся последним блоком слоя персоны там же,
// где была: в PersonaPromptBuilder (её порядок держат golden-фикстуры Prompts.Tests).
//
// Зависимостей семь: store (реестр и резолв владельца), endpoint (адрес/токен/форма http),
// personas, bindings, agentSync, projects, spheres (GetService: без подсистемы сфер зона
// персоны работает через NoSphereDirectory, как и раньше).
// Класс public ТОЛЬКО потому, что он параметр публичного конструктора SessionManager (CS0051) и DI нужен
// публичный конструктор; обход фасада закрывает сторож формы ядра (правило 4: на типы Services.Sessions
// ссылаются лишь SessionManager, сами Services.Sessions и тесты).
public sealed class PersonaLayerBuilder
{
    private readonly SessionStore _store;
    private readonly McpEndpoint _endpoint;
    private readonly PersonaManager _personas;
    private readonly PersonaBindingsService _bindings;
    private readonly PersonaAgentFileSync? _agentSync;
    private readonly ProjectManager _projects;
    private readonly ISphereDirectory? _spheres;
    private readonly ILogger<PersonaLayerBuilder> _log;
    private readonly ChildMode.IChildModeGate? _childGate;

    public PersonaLayerBuilder(SessionStore store, McpEndpoint endpoint, PersonaManager personas,
        PersonaBindingsService bindings, PersonaAgentFileSync? agentSync, ProjectManager projects,
        ISphereDirectory? spheres, ILogger<PersonaLayerBuilder> log,
        ChildMode.IChildModeGate? childGate = null)
    {
        _childGate = childGate;
        _store = store;
        _endpoint = endpoint;
        _personas = personas;
        _bindings = bindings;
        _agentSync = agentSync;
        _projects = projects;
        _spheres = spheres;
        _log = log;
    }

    private ConcurrentDictionary<string, SessionEntry> _sessions => _store.Sessions;
    private string? ResolveOwnerId(Session s) => _store.ResolveOwnerId(s);
    private ISphereDirectory SphereDir => _spheres ?? NoSphereDirectory.Instance;

    // Резолв одной персоны по id (тот же путь, что у BuildPersonaProvider). Единая точка,
    // которой пользуются и сборщик контекста запуска, и создание чата (S3-5): PersonaManager
    // остаётся у этой роли, роли ядра не берут его в свои конструкторы. Отказ («Персона
    // не найдена») — у вызывающего: здесь только null, когда id или владельца нет.
    internal Persona? ResolvePersona(string? personaId, string? ownerId) =>
        personaId is { } pid && ownerId is not null ? _personas.Get(pid, ownerId) : null;

    // null — у чата нет персоны (или владельца).
    internal Func<Persona?> BuildPersonaProvider(Session session, string? ownerId) =>
        () => session.PersonaId is { } pid && ownerId is not null ? _personas.Get(pid, ownerId) : null;

    // Провайдер блока «Привязанные знания и правила» персоны (флаг persona-bindings):
    // на каждый ход перечитывает персону (привязки могли измениться) и собирает
    // индекс + always-выжимки. mountedSections — секции workspace, реально смонтированные
    // этой сессии (типы без своей секции в индекс не попадают). Ошибки → null (ход без блока).
    internal Func<string, Task<string?>>? BuildBindingsProvider(string? ownerId, string? personaId,
        IReadOnlyList<string>? mountedSections)
    {
        if (ownerId is null || personaId is null) return null;
        var sections = mountedSections ?? [];
        return async text =>
        {
            try { return await _bindings.BuildTurnBlockAsync(ownerId, personaId, text, sections); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Блок привязок персоны {Persona}", personaId);
                return null;
            }
        };
    }

    // Руки — свойство чата (LlmSessionContext.HandsEnabled): тумблер проекта или список
    // провайдеров владельца поменялся — адаптеры его живых чатов пересоздаются при следующем
    // сообщении. Уборка ленивая, как у персоны: активный ход и доживающих агентов не рвём.
    // projectId null — все проектные чаты владельца (правка списка провайдеров).
    internal void InvalidateHandsSessions(string ownerId, string? projectId)
    {
        foreach (var entry in _sessions.Values.Where(e => e.Info.ProjectId is not null
                     && (projectId is null ? ResolveOwnerId(e.Info) == ownerId : e.Info.ProjectId == projectId)))
            if (entry.Process is not null) entry.AdapterStale = true;
    }

    // Сброс адаптеров живых сессий персоны (изменился профиль/возможности/привязки):
    // процесс пересоздаётся при следующем сообщении с актуальным контекстом,
    // транскрипт продолжается через --resume (паттерн SetPersona)
    internal void InvalidatePersonaSessions(string personaId)
    {
        foreach (var entry in _sessions.Values.Where(e => e.Info.PersonaId == personaId))
            // Ленивая уборка (см. SwitchSpeaker): не рвём активный ход и доживающих агентов
            if (entry.Process is not null) entry.AdapterStale = true;
    }

    // Кандидаты на консультацию: участники группового чата либо доступные в контексте
    // персоны (глобальные + текущего проекта) + кросс-проектные ProjectPersonas-привязки
    // персоны САМОГО ЧАТА (persona) — команда/точечные персоны другого проекта; без персоны
    // самого чата. В групповом чате extra-персоны тоже примешиваются: остаются
    // консультантами через persona_ask (в MentionsHint, не участники/спикеры).
    internal List<Persona> ResolveOtherPersonas(string ownerId, string? projectId, Session session, Persona? persona = null)
    {
        var isGroup = session.Participants is { Count: > 1 };
        var result = (isGroup
                ? session.Participants!.Select(id => _personas.Get(id, ownerId)).OfType<Persona>()
                : _personas.GetForContext(ownerId, projectId))
            .Where(p => p.Id != session.PersonaId)
            .ToList();

        if (persona is not null)
        {
            var seen = result.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var (extProjectId, extPersonaId) in _bindings.BuildExternalPersonaScopes(ownerId, persona))
            {
                IEnumerable<Persona> toAdd;
                if (extPersonaId is not null)
                    toAdd = _personas.Get(extPersonaId, ownerId) is { } single
                        ? new[] { single } : Array.Empty<Persona>();
                else
                    toAdd = _personas.GetByOwner(ownerId).Where(p =>
                        PersonaZone.IsProjectTeam(p, extProjectId));
                foreach (var p in toAdd)
                {
                    if (p.Id == session.PersonaId || !seen.Add(p.Id)) continue;
                    result.Add(p);
                }
            }
        }
        return result;
    }

    // Разделение персон по способу консультации: с файлом сабагента — через встроенный
    // Task; остальные (зарезервированный handle, за капом файлов) — через persona_ask.
    // Провайдер модели персоны роли НЕ играет: файлы сабагентов пинят максимум алиас-тир
    // Claude (opus/sonnet/haiku, см. PersonaAgentFileSync.ModelAliasFor) — он резолвится
    // у любого провайдера, кросс-провайдерная персона остаётся запускаемой.
    internal (List<Persona> Subagents, List<Persona> ViaAsk) SplitConsultants(
        string ownerId, Session session, List<Persona> personas)
    {
        if (_agentSync is null)
            return ([], personas);
        var eligible = _agentSync.EligiblePersonas(ownerId).Select(p => p.Id)
            .ToHashSet(StringComparer.Ordinal);
        var subagents = new List<Persona>();
        var viaAsk = new List<Persona>();
        foreach (var p in personas)
        {
            // Файл сабагента персоны физически лежит в ЭТОМ проекте (Project-персона своего
            // проекта) либо во всех проектах (Global) — см. PersonaAgentFileSync. Персона
            // чужого проекта (видна здесь только через ProjectPersonas-привязку) файла тут не
            // имеет — Task(agentType=handle) её не найдёт, консультация только через persona_ask.
            var reachable = p.Scope == PersonaScope.Global || p.ProjectId == session.ProjectId;
            if (reachable && eligible.Contains(p.Id) && !PersonaAgentFileSync.IsReserved(p.Handle))
                subagents.Add(p);
            else
                viaAsk.Add(p);
        }
        return (subagents, viaAsk);
    }

    // Решение «даём ли персоне консультантов» (сабагенты .md + их pmem-серверы + подсказка
    // с persona_ask): ключ tool:consultants. В ГРУППОВОМ чате ключ ИГНОРИРУЕТСЯ — спикер
    // обязан уметь спросить коллег по чату (BuildGroupChatHint прямо отсылает к этому блоку),
    // иначе групповой чат ломается по замыслу. Решение зависит только от персоны и состава
    // чата — детерминировано на сессию, состав tools/list от хода не зависит.
    // internal: ту же формулу резолвит PersonasToolset по живой сессии-вызывателю (http,
    // ADR-012 волна 2) — право на сервер проверяется на каждый tools/list и tools/call,
    // а не только при построении контекста адаптера
    internal bool ConsultantsEnabled(string? ownerId, Session session, Persona? persona) =>
        session.Participants is { Count: > 1 }
        || _bindings.ServerToolEnabled(ownerId, persona, "consultants");

    // Решение «даём ли персоне сервер персон» (CRUD + persona_ask): ключ tool:personas.
    // В ГРУППОВОМ чате ключ ИГНОРИРУЕТСЯ по той же причине, что и tool:consultants —
    // BuildGroupChatHint безусловно отсылает к блоку о консультациях (MentionsHint из
    // этого же сервера), Off-привязка сняла бы сервер и подсказка стала бы враньём.
    // internal — по той же причине, что ConsultantsEnabled: PersonasToolset проверяет
    // право на сервер персон по живой сессии на каждый вызов (http, ADR-012 волна 2)
    internal bool PersonasEnabled(string? ownerId, Session session, Persona? persona) =>
        session.Participants is { Count: > 1 }
        || _bindings.ServerToolEnabled(ownerId, persona, "personas");

    // Решение «в составе ли mentions-группа (persona_ask)» — ЕДИНАЯ формула для tools/list
    // тулсета (живой резолв по сессии-вызывателю) и отпечатка сигнатуры запуска CLI (shape
    // через PersonasMcpContext.MentionsToolsEnabled). Две формулы расходились при единственной
    // персоне владельца: MentionsHint обнулялся (спрашивать некого), а инструмент оставался —
    // shape говорил m0, tools/list отдавал persona_ask, и любой из переходов бил по запуску
    // (блокер приёмки волны 2.1). НЕ «MentionsHint != null»: подсказка — про текст промпта,
    // инструмент — про состав, у них разные условия.
    internal bool MentionsToolsEnabled(string? ownerId, Session session, Persona? persona) =>
        PersonasEnabled(ownerId, session, persona) && ConsultantsEnabled(ownerId, session, persona);

    // План файловых сабагентов-персон на ход: папки для --add-dir + pmem-серверы памяти
    // видимых персон. Замыкание вычисляется на каждый ход (актуальные персоны и модель
    // сессии); внутри — троттлёный reconcile файлов. Ошибки → null (ход идёт без
    // консультантов, persona_ask остаётся).
    internal Func<PersonaAgentsContext?>? BuildPersonaAgentsProvider(string? ownerId, Session session, Persona? persona)
    {
        if (ownerId is null || _agentSync is null) return null;
        // Off-привязка tool:consultants убирает и pmem-серверы, и --add-dir с .md-агентами
        // (подсказка про Workflow отпадает следом — она условна от AgentHandles)
        if (!ConsultantsEnabled(ownerId, session, persona)) return null;
        return () =>
        {
            try
            {
                var projectId = session.ProjectId;
                var addDirs = _agentSync.GetAddDirs(ownerId, session.Model, projectId);
                // pmem — для ВСЕХ видимых персон с памятью (включая персону самого чата):
                // файлы в add-dir видны все, а Task(agentType=handle) может позвать любую.
                // С stdio объявление стоило процессом node на каждого консультанта (CLI
                // поднимает все stdio-серверы конфига на старте, alwaysLoad на это не
                // влияет — см. docs/architecture/mcp-servers.md); на http-транспорте
                // (ADR-012, фаза 2) все pmem_* живут в Kestrel одним тулсетом — процессов
                // нет, сколько бы персон ни было смонтировано. Сузишь список — сузишь и
                // круг персон, которых можно позвать сабагентом.
                var (subagents, _) = SplitConsultants(ownerId, session,
                    _personas.GetForContext(ownerId, projectId).ToList());
                // Порядок ВСЕГО плана — по handle (Ordinal). GetForContext отдаёт персон по UpdatedAt
                // по убыванию: без этой сортировки определения per-run --agents (ClaudeAgentsJson →
                // turnAgentsFingerprint → BuildLaunchSignature, ClaudeSession.cs) и порядок
                // pmem-серверов менялись бы от чата к чату при неизменном составе агентов: порядок
                // определений влияет на отпечаток запуска (та же причина, что у
                // AppendPersonaLines).
                subagents.Sort((a, b) => string.CompareOrdinal(a.Handle, b.Handle));
                var apiUrl = _endpoint.ResolveTasksApiUrl(ownerId);
                var tokenFactory = () => _endpoint.GetServiceToken(ownerId);
                var useHttp = _endpoint.HttpEndpointUsable(apiUrl);
                // ProjectId консультанта — проект ТЕКУЩЕГО чата (как у BuildPersonaLayer выше), не
                // scope самого консультанта: приглашённая в проектный workflow глобальная персона
                // тоже должна видеть team_memory_list/search этого проекта (read-only — пишет только
                // персона САМОГО проекта, гейт в TeamMemoryService.WriteDeniedFor).
                var servers = subagents
                    .Where(p => p.MemoryEnabled)
                    .Select(p => new ConsultantMemoryServer(
                        PersonaConsultantToolset.PmemServerKey(p.Handle),
                        apiUrl, tokenFactory, p.Id, projectId, useHttp))
                    .ToList();
                // Список «Доступные agentType» в промпте — уже в порядке handle (сортировка выше),
                // здесь только отсекаются пустые handles.
                var handles = subagents.Select(p => p.Handle).Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
                // Определения для per-run --agents JSON: omitClaudeMd ClaudeSession дополнит
                // по факту BareMode (свойство прогона, известное только там)
                var agents = _agentSync.GetAgentsDefs(subagents, omitClaudeMd: false);
                return new PersonaAgentsContext(addDirs, servers, handles, agents);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "План сабагентов-персон не собрался — ход без файловых консультантов");
                return null;
            }
        };
    }

    // Блок-подсказка о консультациях: две группы — сабагенты (Task) и persona_ask
    internal string? BuildMentionsHint(string ownerId, Session session, List<Persona> others)
    {
        if (others.Count == 0) return null;
        var (subagents, viaAsk) = SplitConsultants(ownerId, session, others);
        var sb = new System.Text.StringBuilder();
        if (subagents.Count > 0)
        {
            sb.AppendLine("Персоны-консультанты доступны как сабагенты: вызывай встроенный инструмент " +
                "Task с subagent_type=\"<handle>\" и самодостаточным вопросом в prompt — персона не видит " +
                "этот разговор; она сама прочитает нужные файлы, заметки и свою память и ответит от " +
                "своего лица. Не вызывай сабагента со своим собственным handle — отвечай сам. " +
                "Инструменты mcp__pmem_* НЕ вызывай напрямую — это личная память персон-консультантов, " +
                "она доступна только им самим; чтобы узнать, что персона помнит, спроси её через Task. " +
                "Когда пользователь упоминает персону через @handle — обязательно обратись к ней " +
                "и учти её ответ. В мультиагентных обсуждениях (workflow /panel-of-experts) по умолчанию " +
                "распределяй роли панели между этими персонами: передавай их handle в args.participants " +
                "(роли по порядку: Генератор, Критик, Адвокат, Модератор), подбирая персону под роль " +
                "по её характеру. Доступные консультанты:");
            AppendPersonaLines(sb, subagents, session.ProjectId);
        }
        if (viaAsk.Count > 0)
        {
            if (subagents.Count > 0)
                sb.AppendLine("Эти персоны доступны только инструментом persona_ask (параметры: handle, " +
                    "question, context?) — ответ придёт от их лица, с их характером и памятью:");
            else
                sb.AppendLine("Любую персону можно спросить инструментом persona_ask (параметры: handle, " +
                    "question, context?) — она ответит от своего лица, со своим характером и памятью. " +
                    "Когда пользователь упоминает персону через @handle — обязательно обратись к ней " +
                    "через persona_ask и учти её ответ. Вопрос формулируй самодостаточно: персона не " +
                    "видит этот разговор. Если вызов вернул «No such tool available» — сервер персон ещё " +
                    "подключается: подожди мгновение и повтори тот же вызов. Доступные собеседники:");
            AppendPersonaLines(sb, viaAsk, session.ProjectId);
        }
        return sb.ToString().TrimEnd();
    }

    // Процессы oh-my-claudecode: в тексте хода упомянута команда плагина — подсказка о замещении
    // его советнических ролей персонами-сабагентами (подготовка к S3-7: тело блока переехано из
    // BuildCliTurnText сюда — это ResolveOtherPersonas/SplitConsultants, форма та же, что у
    // BuildMentionsHint). null — плагин не упомянут, владельца нет или замещать некем.
    internal string? BuildOmcRoleHint(string text, Session session)
    {
        if (!OmcPersonaRouting.MentionsPluginCommand(text)) return null;
        var ownerId = ResolveOwnerId(session);
        if (ownerId is null) return null;
        var (subagents, _) = SplitConsultants(ownerId, session,
            ResolveOtherPersonas(ownerId, session.ProjectId, session));
        return OmcPersonaRouting.BuildHint(subagents);
    }

    // currentProjectId — ProjectId сессии: персона Project-скоупа ДРУГОГО проекта (видна здесь
    // только через кросс-проектную ProjectPersonas-привязку) помечается «[проект «Имя»]», чтобы
    // не путать тёзок из разных команд.
    internal void AppendPersonaLines(System.Text.StringBuilder sb, List<Persona> personas, string? currentProjectId)
    {
        // Персоны сферы — отдельной группой «Команда сферы»: в одном списке с проектными и
        // глобальными их не отличить.
        // Порядок внутри группы — по handle (Ordinal): строки подсказки не должны зависеть ни от
        // выборки (GetForContext — по UpdatedAt по убыванию: любое касание персоны переставляло бы
        // строки), ни от порядка входного списка — префикс системного промпта стабилен между
        // беседами (кэш префикса локальной модели).
        var ordered = personas.Where(p => !PersonaZone.IsSpherePersona(p))
            .OrderBy(p => p.Handle, StringComparer.Ordinal)
            .Concat(personas.Where(PersonaZone.IsSpherePersona)
                .OrderBy(p => p.Handle, StringComparer.Ordinal))
            .ToList();
        var sphereHeaderDone = false;
        foreach (var p in ordered)
        {
            if (PersonaZone.IsSpherePersona(p) && !sphereHeaderDone)
            {
                sphereHeaderDone = true;
                var sphereName = p.OwnerId is not null && p.SphereId is not null
                    ? SphereDir.SphereName(p.OwnerId, p.SphereId) : null;
                sb.AppendLine(sphereName is null ? "Команда сферы:" : $"Команда сферы «{sphereName}»:");
            }
            var title = string.IsNullOrWhiteSpace(p.Role) ? p.Name : $"{p.Role} ({p.Name})";
            sb.Append($"- @{p.Handle} — {title}");
            if (PersonaZone.IsProjectPersona(p) && p.ProjectId != currentProjectId
                && _projects.GetById(p.ProjectId!) is { } foreignProject)
                sb.Append($" [проект «{foreignProject.Name}»]");
            if (!string.IsNullOrWhiteSpace(p.Description)) sb.Append($": {p.Description.Trim()}");
            sb.AppendLine();
        }
    }

    // Контекст MCP-сервера персон: CRUD персон из любого чата (за флагом personas);
    // при включённом persona-mentions и наличии других персон в контексте — плюс
    // @упоминания: MentionsHint (блок «@handle — Роль (Имя)» для промпта) и persona_ask.
    // В групповом чате mentions-режим (persona_ask + подсказка по УЧАСТНИКАМ) включён
    // всегда, независимо от флага persona-mentions — иначе спикер не сможет спросить коллег.
    // persona — для кросс-проектных ProjectPersonas-привязок (доступ к команде ДРУГОГО проекта).
    internal PersonasMcpContext? BuildPersonasContext(string? ownerId, string? projectId, Session session, Persona? persona = null)
    {
        if (ownerId is null) return null;
        // Off-привязка tool:personas снимает сервер персон целиком (вместе с CRUD и persona_ask);
        // в групповом чате исключение — см. PersonasEnabled
        if (!PersonasEnabled(ownerId, session, persona)) return null;

        var selfPersonaId = session.PersonaId;
        // @упоминания (persona_ask + подсказка) включены всегда, кроме Off-привязки
        // tool:consultants: без консультаций и подсказки не нужно, и persona_ask
        // выключается вместе с ней (PERSONAS_MENTIONS собирается из MentionsHint).
        var mentionsHint = ConsultantsEnabled(ownerId, session, persona)
            ? BuildMentionsHint(ownerId, session, ResolveOtherPersonas(ownerId, projectId, session, persona))
            : null;

        var externalPersonaScopes = _bindings.BuildExternalPersonaScopes(ownerId, persona);
        var extraProjectIds = externalPersonaScopes.Where(s => s.PersonaId is null)
            .Select(s => s.ProjectId).Distinct().ToList();
        var extraPersonaIds = externalPersonaScopes.Where(s => s.PersonaId is not null)
            .Select(s => s.PersonaId!).Distinct().ToList();

        // Модули сервера персон: manage (CRUD персон) и automation (правила проактивности) —
        // за своими tool-ключами с дефолтом по роли (SectionEnabled → SpecialtySections).
        // Ядро (personas_list/get, привязки, persona_ask) остаётся у всех, у кого сервер включён.
        // Проектный онбординг (знакомство v2, п.5) форсирует manage: шаг команды зовёт
        // personas_ai_team/personas_create, а ведёт сессию скромная личная дефолт-персона,
        // у которой по роли этих инструментов нет. Решение — по СВОЙСТВУ сессии
        // (OnboardingKind пишется при создании и не мутирует), не по ходу: состав tools/list
        // стабилен. Форс не сужается состоянием PresetKey по той же причине. Работает только
        // при включённом сервере персон: Off-привязка tool:personas снимает его целиком,
        // и шаг команды честно проговаривается оверлеем как ограничение.
        var manage = session.OnboardingKind == OnboardingKinds.Project
            || _bindings.SectionEnabled(ownerId, persona, "personas-manage");
        var automation = _bindings.SectionEnabled(ownerId, persona, "personas-automation");

        var apiUrl = _endpoint.ResolveTasksApiUrl(ownerId);
        return new PersonasMcpContext(apiUrl, () => _endpoint.GetServiceToken(ownerId), projectId, selfPersonaId,
            mentionsHint, BindingsEnabled: true,
            extraProjectIds.Count > 0 ? extraProjectIds : null,
            extraPersonaIds.Count > 0 ? extraPersonaIds : null,
            ManageEnabled: manage, AutomationEnabled: automation,
            UseHttp: _endpoint.HttpEndpointUsable(apiUrl),
            MentionsToolsEnabled: MentionsToolsEnabled(ownerId, session, persona));
    }

    // Сессия нужна для правила «координатор не пишет код сам» (режим «Командная реализация»):
    // у чата-штаба поверх прав персоны запрещены инструменты правки файлов — работа идёт
    // задачами на исполнителей, а не руками координатора.
    // Встроенные инструменты CLI, закрытые ребёнку поверх его разрешающего --tools. Имена — из
    // набора, принимаемого CLI (тот же перечень держит OneShotClaudeRunner); неизвестные CLI имена
    // в запретах роняют запуск, поэтому список короткий и проверенный.
    // Файловых инструментов ребёнка (Read/Glob/Grep/Write/Edit — ChildBuiltinTools) здесь НЕТ:
    // они явно разрешены списком --tools, и запрет на них — не право детского слоя, а право
    // персоны/этапа (И10: поля персоны только вычитают). Названный здесь ChildBuiltinTools, он
    // доходил бы до CLI противоречием --tools/--disallowedTools и требовал снятия whole списком
    // (прежний Except в ClaudeSession), которое молча съедало и настоящий запрет персоны.
    internal static readonly string[] ChildDisallowedBuiltins =
        ["Bash", "NotebookEdit", "WebFetch", "WebSearch", "Task", "Skill"];

    internal IReadOnlyList<string>? BuildExtraDisallowed(string? ownerId, Persona? persona, Session session)
    {
        var byPersona = PersonaAccessPolicy.BuildExtraDisallowed(persona,
            webAllowed: _bindings.EffectiveToolEnabled(ownerId, persona, "web"),
            stageDisallowed: session.StageDisallowedTools);
        // Детский слой (И1, п.11): запреты складываются с запретами персоны и этапа. Основной
        // замок — пустой разрешающий список встроенных (--tools "" в ClaudeSession); этот список —
        // второй рубеж и признак для ветки сабагентов (Task в запретах = персон-агентов ходу не даём).
        if (_childGate?.IsChild(ownerId) == true)
            byPersona = [.. (byPersona ?? []), .. ChildDisallowedBuiltins.Except(byPersona ?? [], StringComparer.Ordinal)];
        if (session.TeamImplement is not { } team) return byPersona;
        // Режим включён — ExitPlanMode запрещён всегда (Э8): на стадиях интервью и планирования
        // штаб сидит в план-режиме, и штатная карточка plan_review дала бы второе согласование
        // поверх командной карточки плана. Правки файлов режет отдельная настройка.
        var extra = new List<string>(byPersona ?? []);
        extra.AddRange(TeamImplementPrompts.ModeDisallowed);
        if (team.CoordinatorNoCode) extra.AddRange(TeamImplementPrompts.CoordinatorDisallowed);
        return extra;
    }
}
