using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;

// Типы плана рабочего пространства остаются частью контракта фасада (их читает WorkspaceToolset
// и тесты по WorkspaceMcpPlan); здесь — обращение через alias, чтобы тела
// остались дословными (тот же приём, что с QueuedMessage в PendingQueueService).

namespace ClaudeHomeServer.Services.Sessions;

// Внешние MCP-серверы хода (S3-4, docs/research/session-manager-split-2026-11.md): план
// рабочего пространства (состав секций и зона проектов — единая формула ADR-012 для конфига
// хода и для живого tools/list WorkspaceToolset), серверы личного реестра владельца с
// разворачиванием секретов и заголовками авторизации, контекст уведомлений и живой состав
// контекста чата для подсказки хода.
// Тела перенесены из SessionManager дословно; фасад держит делегирующие обёртки прежних
// сигнатур.
//
// Зависимостей ровно десять: store (реестр и вердикт рамки хода), endpoint, bindings,
// projects, mcpRegistry, mcpSecrets, mcpOAuth, flags, users, config. Модули вынесены в
// ModulesMcpContexts отдельно: с ними здесь было бы двенадцать, а новая зависимость —
// сигнал резать класс, а не поднимать лимит (S3-0 §2).
// Класс public ТОЛЬКО потому, что он параметр публичного конструктора SessionManager (CS0051) и DI нужен
// публичный конструктор. Обход фасада к типам Services.Sessions закрывает сторож формы ядра (правило 4),
// но он смотрит только на ссылки с Namespace Services.Sessions (SessionCoreShapeGuardTests,
// Rule4OnlyFacadeReferences): alias выше ведёт к вложенному типу фасада WorkspaceMcpPlan
// с Namespace ClaudeHomeServer.Services — правило 4 такой ссылки не видит.
// Сохранение типа плана вложенным в фасад — осознанное решение (план — часть контракта фасада,
// см. комментарий у alias), а не пропуск сторожа; расширять правило 4 на этот случай — решение
// постановщика, не исполнителя.
public sealed class ExternalMcpContexts
{
    private readonly SessionStore _store;
    private readonly McpEndpoint _endpoint;
    private readonly PersonaBindingsService _bindings;
    private readonly ProjectManager _projects;
    private readonly Mcp.McpRegistry? _mcpRegistry;
    private readonly Mcp.McpSecretStore? _mcpSecrets;
    private readonly Mcp.McpOAuthService? _mcpOAuth;
    private readonly FeatureFlagService _flags;
    private readonly UserStore _users;
    private readonly IConfiguration _config;
    private readonly ILogger<ExternalMcpContexts> _log;

    // Id-заглушка пустой зоны: ни один проект владельца под ним не живёт
    internal const string NoProjectsZone = "__no-projects__";

    public ExternalMcpContexts(SessionStore store, McpEndpoint endpoint, PersonaBindingsService bindings,
        ProjectManager projects, Mcp.McpRegistry? mcpRegistry, Mcp.McpSecretStore? mcpSecrets,
        Mcp.McpOAuthService? mcpOAuth, FeatureFlagService flags, UserStore users, IConfiguration config,
        ILogger<ExternalMcpContexts> log)
    {
        _store = store;
        _endpoint = endpoint;
        _bindings = bindings;
        _projects = projects;
        _mcpRegistry = mcpRegistry;
        _mcpSecrets = mcpSecrets;
        _mcpOAuth = mcpOAuth;
        _flags = flags;
        _users = users;
        _config = config;
        _log = log;
    }

    private ConcurrentDictionary<string, SessionEntry> _sessions => _store.Sessions;

    // Контекст MCP-сервера рабочего пространства: доступ ко всем проектам владельца
    // (за флагом workspace-tools). Секции сужаются возможностями персоны (единая точка
    // истины — PersonaBindingsService.EffectiveToolEnabled: Tool-привязка приоритетнее
    // Persona.Tools); search остаётся при любом непустом наборе. Секция chats — за
    // отдельным флагом workspace-chat-send, секция destructive (безвозвратное удаление) —
    // за флагом workspace-destructive. Все возможности выключены → сервер не подключаем.
    // Project/ProjectPath-привязки персоны сужают зону (AllowedProjectIds) до привязанных
    // проектов + проекта текущей сессии; БЕЗ таких привязок поведение как у Claude —
    // все проекты владельца (null).
    internal WorkspaceMcpContext? BuildWorkspaceContext(string? ownerId, string? projectId,
        string? selfSessionId, Persona? persona)
    {
        if (ownerId is null) return null;
        var plan = BuildWorkspacePlan(ownerId, projectId, persona);
        if (plan is null) return null;
        var apiUrl = _endpoint.ResolveTasksApiUrl(ownerId);
        return new WorkspaceMcpContext(apiUrl, () => _endpoint.GetServiceToken(ownerId), projectId,
            plan.Sections, plan.AllowedProjectIds, selfSessionId,
            UseHttp: _endpoint.HttpEndpointUsable(apiUrl));
    }

    /// <summary>
    /// План сервера рабочего пространства: секции инструментов и зона проектов.
    /// ЕДИНАЯ формула состава (ADR-012, волна 3): её зовёт и BuildWorkspaceContext (конфиг
    /// хода и отпечаток shapes), и WorkspaceToolset (живой tools/list по сессии из хвоста
    /// маршрута). Состав и его отпечаток, посчитанные двумя формулами, расходятся — блокер
    /// приёмки волны 2 у personas. null — сервер не подключается (все секции выключены).
    /// </summary>
    internal WorkspaceMcpPlan? BuildWorkspacePlan(string ownerId, string? projectId, Persona? persona)
    {
        var sections = new List<string>();
        foreach (var key in new[] { "projects", "files", "knowledge" })
            if (_bindings.EffectiveToolEnabled(ownerId, persona, key)) sections.Add(key);
        // chats — явный Tool-ключ ИЛИ неявный opt-in через ProjectPersonas-привязки:
        // персона, допущенная к чужому проекту, может писать в его чаты (решение —
        // PersonaBindingsService.ChatsSectionEnabled, там же семантика).
        var chatScopes = _bindings.BuildChatScopes(ownerId, persona);
        var chatsEnabled = _bindings.ChatsSectionEnabled(ownerId, persona);
        if (chatsEnabled) sections.Add("chats");
        // Диагностика решения по chats: набор chats-инструментов обязан быть одинаковым на всех
        // ходах персоны — по этой строке видно, из чего решение сложилось в конкретной сессии
        if (persona is not null)
            _log.LogDebug("Секция chats для персоны {Persona}: {Decision} (tools={Tools}, " +
                "toolBinding={Binding}, chatScopes={Scopes})",
                persona.Handle ?? persona.Id, chatsEnabled ? "on" : "off",
                persona.Tools is null ? "null" : string.Join("|", persona.Tools),
                persona.Bindings?.LastOrDefault(b => b.Type == PersonaBindingType.Tool
                    && string.Equals(b.Target, "chats", StringComparison.OrdinalIgnoreCase))?.Mode
                    .ToString() ?? "нет",
                chatScopes is null ? "null" : string.Join("|", chatScopes));
        if (sections.Count == 0) return null;
        // Git-инструменты (read: status/diff/log; write: stage/commit — секция git_write)
        // и базы знаний Dify владельца (kb_list/search/add_document) —
        // надстройки над базовыми секциями files/knowledge: без базовой секции не монтируются,
        // а внутри неё решает свой tool-ключ (git/kb) с дефолтом по роли персоны
        // (PersonaBindingsService.SectionEnabled — пресет SpecialtySections). Раньше обе ехали
        // с базовой секцией безусловно и стоили контекста персонам, которым не нужны.
        // Секция git приходит двумя ступенями: пресет по роли даёт ЧТЕНИЕ истории
        // (git_status/diff/log), запись (git_stage/git_commit) добавляет только явно
        // включённый ключ git — исполнители коммитят через Bash, а ролям ReadOnly запись
        // не нужна по определению (SectionOrigin, там же данные использования).
        var gitOrigin = _bindings.SectionOrigin(ownerId, persona, "git");
        if (sections.Contains("files") && gitOrigin != SectionSource.Off)
        {
            sections.Add("git");
            if (gitOrigin == SectionSource.Explicit) sections.Add("git_write");
        }
        if (sections.Contains("knowledge") && _bindings.SectionEnabled(ownerId, persona, "kb"))
            sections.Add("knowledge_bases");
        // Разрушающие операции (files_delete/chats_delete) — за отдельным флагом
        // workspace-destructive; персоне дополнительно нужен tool-ключ destructive
        // (Tool-привязка или Persona.Tools). Одна destructive без базовых секций не монтируется.
        // Профиль «Только чтение» строже любых привязок — секцию не монтируем вовсе.
        if (_flags.IsEnabled(ownerId, FeatureFlagKeys.WorkspaceDestructive)
            && persona?.Access != PersonaAccess.ReadOnly
            && _bindings.EffectiveToolEnabled(ownerId, persona, "destructive"))
            sections.Add("destructive");
        // Выкатка прода из чата (ADR-010): секция монтируется только там, где контур реально
        // настроен, и только владельцу-админу — REST всё равно admin-only, а держать три схемы
        // в контексте у всех незачем. Профиль «Только чтение» её не получает: выкатка меняет
        // прод. Все три условия постоянны в рамках сессии (конфиг машины, роль пользователя,
        // профиль персоны), поэтому состав tools/list между ходами не мерцает.
        if (Deploy.DeployOptions.From(_config).Enabled
            && persona?.Access != PersonaAccess.ReadOnly
            && string.Equals(_users.GetById(ownerId)?.Role, "admin", StringComparison.OrdinalIgnoreCase))
            sections.Add("deploy");
        sections.Add("search");

        IReadOnlyList<string>? allowedIds = null;
        var fileScopes = _bindings.BuildFileScopes(ownerId, persona);
        // Персона сферы: зона — проекты сферы ∪ проекты привязок, никогда не null (иначе все проекты
        // владельца). Проект сессии вне сферы не добавляем — чат уже отказан по OutOfZoneRefusal.
        if (_bindings.BuildZoneScopes(persona) is { } zone)
        {
            var set = new HashSet<string>(zone);
            foreach (var id in fileScopes ?? []) set.Add(id);
            foreach (var id in chatScopes ?? []) set.Add(id);
            // Потребители читают пустой список как «без сужения» — пустую зону гасим заглушкой
            allowedIds = set.Count > 0 ? set.ToList() : [NoProjectsZone];
        }
        else if (fileScopes is { Count: > 0 } || chatScopes is { Count: > 0 })
        {
            // Привязки есть — зона ужимается; проект самой сессии всегда доступен
            var set = new HashSet<string>(fileScopes ?? []);
            if (chatScopes is { Count: > 0 })
                foreach (var id in chatScopes) set.Add(id);
            if (projectId is not null) set.Add(projectId);
            allowedIds = set.ToList();
        }

        return new WorkspaceMcpPlan(sections, allowedIds);
    }

    // Живой состав контекста чата для подсказки хода (фича chat-context): читаем сессию из
    // реестра на КАЖДЫЙ вызов — материал, добавленный кнопкой в идущем разговоре, обязан
    // попасть в промпт следующего хода без пересоздания адаптера. Снимок здесь был бы
    // тихой ложью: вкладки у человека есть, а модель о них не знает.
    // На СОСТАВ инструментов не влияет — только на текст подсказки (гейт самого инструмента
    // живёт в BuildWorkspaceContext и решается флагом владельца).
    internal Func<IReadOnlyList<SessionContextEntry>> BuildChatContextProvider(string sessionId) =>
        () => _sessions.TryGetValue(sessionId, out var entry)
            ? entry.Info.Context
            : [];

    // Серверы личного реестра владельца в ход. Решение принимается ТОЛЬКО по
    // owner/project/persona и вердикту рамки хода (свойство сессии/проекта, S2a-7) —
    // состав tools/list не смеет зависеть от свойств хода
    // (иначе сигнатура запуска мерцает и процесс CLI перезапускается со всеми MCP).
    // Провайдер, а не готовое значение: правка реестра применяется со следующего хода
    // без пересоздания адаптера. Секреты разворачиваются здесь и живут только во
    // временном конфиге хода.
    internal Func<ExternalMcpContext?>? BuildExternalMcpProvider(string? ownerId, string? projectId, Persona? persona, string? sessionId,
        IReadOnlyList<string>? stageMcpAllow = null)
    {
        if (ownerId is null || _mcpRegistry is null || _mcpSecrets is null) return null;
        // Профиль «Только чтение»: имён инструментов чужого сервера мы не знаем, а гасить их
        // deny-правилами нельзя — список живёт на стороне сервера и меняется, а неизвестное имя
        // в правиле роняет запуск CLI (см. историю MultiEdit в PersonaAccessPolicy). Поэтому
        // решение принимается ЦЕЛИКОМ по серверу: такая персона получает только записи с явным
        // разрешением AllowReadOnlyPersonas. Свойство персоны, не хода — состав не мерцает.
        var readOnly = persona?.Access == PersonaAccess.ReadOnly;
        var registry = _mcpRegistry;
        var secretStore = _mcpSecrets;
        return () =>
        {
            try
            {
                var servers = new List<ExternalMcpServer>();
                // Настройки проекта читаем на каждый ход — правка настроек проекта
                // применяется со следующего хода, без пересоздания адаптера.
                // Чат вне проекта каскад проекта пропускает.
                var project = projectId is null ? null : _projects.GetById(projectId);
                var onInProject = project?.McpServersOn;     // allow-модель доступа
                var isProjectChat = projectId is not null;
                // Рамка хода (S2a-7): тот же вердикт GetTurnFrame/ITurnFrameSource, что входит
                // в сигнатуру запуска (ClaudeSession). Его читаем по живому состоянию
                // сессии/проекта, а не по ходу: общий проект — Narrow любому актору включая
                // владельца (решение 29.10), личный и внепроектный — Wide. Добавление или
                // снятие участников меняет рамку вместе с сигнатурой — следующий ход стартует
                // новым процессом с новым составом реестра, состав доживающего процесса
                // при этом неизменен.
                var frame = _store.GetTurnFrame(sessionId, ownerId);
                foreach (var record in registry.GetByOwner(ownerId))
                {
                    if (!record.Enabled) continue;
                    // Сужение слепком этапа (И5): этап только убирает сервер из уже выданного
                    if (!BuiltinMcpContexts.StageAllows(stageMcpAllow, record.Key)) continue;
                    // Встроенные интеграции (IntegrationKeys: dify/fal-ai/glif/higgsfield)
                    // доставляются собственной веткой (TryAddHiggsfieldBuiltin) по рубильнику
                    // записи и RO-гейту. Реестровый путь для них НЕ применяется: иначе у
                    // доставки две точки истины — higgsfield, лежащий в реестре и включённый
                    // в проекте (или выданный персоне), доедет мимо продуктовой формулы, да
                    // ещё дублем. Сейчас записи dify/fal-ai/glif в реестре не заводятся
                    // (живут как HTTP-узлы Kestrel), условие держим общим — защита от случайного
                    // возврата в реестр.
                    if (Mcp.McpRegistry.IntegrationKeys.Contains(record.Key, StringComparer.OrdinalIgnoreCase)) continue;
                    // allow-модель: сервер едет, если включён «здесь» (проект этого чата
                    // по McpServersOn либо, вне проектов, AllowOutsideProjects записи) ИЛИ
                    // выдан персоне (McpServerGranted). В узкой рамке обе личные оси
                    // не смотрятся вовсе — единственный путь реестрового сервера в общий
                    // проект это явная пометка SharedWithMembers (S2a-7, решение 29.10).
                    // Чистое условие — Mcp.McpDelivery.ShouldDeliver,
                    // его OR-матрицу гоняем юнитами без SessionManager. Все входы — свойства
                    // owner/project/persona/записи и вердикт рамки, ни один не смотрит на ход.
                    var granted = _bindings.McpServerGranted(persona, "mcp:" + record.Key);
                    if (!Mcp.McpDelivery.ShouldDeliver(record, onInProject, isProjectChat, granted, readOnly, frame))
                        continue;
                    var stdio = record.Transport == McpTransport.Stdio;
                    // OAuth: токен, доживающий последние секунды, обновляем ДО сборки конфига —
                    // заголовок запекается на старте CLI и живому процессу уже не доедет.
                    // null — вход протух и не восстановился: сервер снимается с хода, а статус
                    // «нужен вход» уже записан (молча ронять инструменты в 401 нельзя)
                    var fresh = record.Auth.Kind == McpAuthKind.OAuth2 && _mcpOAuth is not null
                        ? _mcpOAuth.EnsureFresh(ownerId, record)
                        : record;
                    if (fresh is null)
                    {
                        _log.LogWarning("MCP-сервер «{Key}» снят с хода: нужен вход (OAuth)", record.Key);
                        continue;
                    }
                    var env = ResolveSecretValues(ownerId, fresh.Env);
                    var headers = ResolveSecretValues(ownerId, fresh.Headers);
                    if (!stdio && !TryApplyAuthHeaders(ownerId, fresh, headers)) continue;
                    servers.Add(new ExternalMcpServer(
                        fresh.Key,
                        fresh.Transport.ToString().ToLowerInvariant(),
                        stdio ? fresh.Command : null,
                        fresh.Args ?? [],
                        env,
                        stdio ? null : fresh.Url,
                        headers,
                        fresh.AlwaysLoad,
                        fresh.AuthVersion));
                }

                return servers.Count > 0 ? new ExternalMcpContext(servers) : null;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Реестр MCP-серверов не собрался — ход без своих серверов");
                return null;
            }
        };
    }

    // Локальные обёртки вокруг Mcp.McpAuthHeaders / секрет-стора — нужны в лямбде
    // BuildExternalMcpProvider, поэтому живут на классе. Поведение и сообщения
    // логов совпадают с теми, что были внутри лямбды.
    internal Dictionary<string, string> ResolveSecretValues(string ownerId, Dictionary<string, string>? map)
    {
        if (_mcpSecrets is null) return new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in map ?? [])
            result[name] = _mcpSecrets.Resolve(ownerId, value) ?? "";
        return result;
    }

    internal bool TryApplyAuthHeaders(string ownerId, McpServerRecord record, Dictionary<string, string> headers)
    {
        if (Mcp.McpAuthHeaders.TryApply(record, headers, r => _mcpSecrets?.Resolve(ownerId, r))) return true;
        // Заголовок авторизации http/sse-сервера (общая точка с пробой — Mcp.McpAuthHeaders).
        // Потерянный секрет (запись ссылается в пустоту) — не повод отдавать серверу заведомо
        // анонимный запрос: пропускаем сервер с предупреждением, иначе инструменты молча
        // отвечали бы 401.
        _log.LogWarning("MCP-сервер «{Key}» снят с хода: не найдено значение авторизации", record.Key);
        return false;
    }

    // Контекст MCP-сервера уведомлений: обычному чату — всегда, персоне — по роли
    // (модуль автоматизации) либо по явной привязке tool:notifications; Off-привязка
    // выключает в любом случае. Единая точка решения — PersonaBindingsService.NotificationsEnabled
    // (по ПЕРСОНЕ, не по ходу). Тот же сервисный токен, что у tasks/notes/workspace.
    internal NotificationsMcpContext? BuildNotificationsContext(string? ownerId, string? personaId, Persona? persona)
    {
        if (ownerId is null) return null;
        if (!_bindings.NotificationsEnabled(ownerId, persona)) return null;
        var apiUrl = _endpoint.ResolveTasksApiUrl(ownerId);
        return new NotificationsMcpContext(apiUrl, () => _endpoint.GetServiceToken(ownerId), personaId,
            UseHttp: _endpoint.HttpEndpointUsable(apiUrl));
    }
}
