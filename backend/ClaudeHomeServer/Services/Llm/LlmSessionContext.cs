using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm;

// Контекст MCP-сервера задач для сессии: адрес API, фабрика сервисного токена владельца
// и проект (null — чат вне проекта, контекст личных задач).
// ExtraProjectIds/ExtraProjectIdsReadOnly — кросс-проектные ProjectTasks-привязки текущей
// персоны (§ Кросс-проектные привязки): доступ к задачам ДРУГИХ проектов владельца поверх
// ProjectId; ReadOnly — подмножество только для чтения (create/update/delete запрещены).
// TokenFactory, а не строка (ADR-012, волна 2 — как widgets/memory с волны 1.1): контекст
// живёт столько же, сколько адаптер, а захваченный строкой JWT у чата старше ServiceTokenLifetime
// начал бы отдавать 401 и задачи пропадали бы у модели молча. stdio-ветка берёт токен
// фабрикой на каждую сборку конфига хода, http — фабрикой в заголовок.
// UseHttp — СХЕМА адреса допускает http (fail-closed по https и кривой форме строки):
// свойство ApiUrl, стабильно в жизни адаптера. Рубильник Mcp:HttpTransport сюда НЕ входит —
// он живой, спрашивается на каждый ход (LlmSessionContext.HttpMcpEnabledProvider), чтобы
// откат доезжал и до уже поднятых чатов (техдолг ADR-012 §1). false — ход объявляет
// прежний stdio-сервер на node (путь отката). Не Claude-специфичен: DeepSeek-адаптер
// может реализовать те же tasks_* инструменты нативно.
public record TasksMcpContext(string ApiUrl, Func<string> TokenFactory, string? ProjectId,
    IReadOnlyList<string>? ExtraProjectIds = null, IReadOnlyList<string>? ExtraProjectIdsReadOnly = null,
    bool UseHttp = false);

// Контекст MCP-сервера заметок: адрес API, фабрика сервисного токена владельца и проект
// (задаёт источник по умолчанию для создания заметок; null — личный vault).
// AnnotationsEnabled — модуль комментариев к документам и редких операций заметок
// (ключ notes-annotations, дефолт выключен): решается ПО ПЕРСОНЕ, не по ходу.
// TokenFactory/UseHttp — тот же идиом доставки токена и отката, что у tasks (ADR-012).
public record NotesMcpContext(string ApiUrl, Func<string> TokenFactory, string? ProjectId,
    bool AnnotationsEnabled = true, bool UseHttp = false);

// Контекст MCP-сервера памяти персоны: адрес API, фабрика сервисного токена владельца, id
// персоны, чья долгая память доступна инструментами mcp__memory__* в этой сессии, и проект
// ТЕКУЩЕГО ЧАТА (③-3.4: даёт доступ к team_memory_* — общей памяти команды; null — чат вне
// проекта, командной памяти нет). ProjectId — от чата, не от scope персоны: глобальная персона и
// консультант другого проекта тоже получают team_memory_list/search (read-only) внутри
// проектного чата — пишет ли персона в команду, решает бэкенд (TeamMemoryService.
// WriteDeniedFor: Persona.Scope==Project && Persona.ProjectId==id проекта памяти),
// состав MCP-инструментов от этого не зависит (диета памяти команды, ч.3).
// DossierToolsEnabled — секция dossier_lookup/dossier_get (этап 2, ADR-004 §5): включается
// по флагу ВЛАДЕЛЬЦА change-dossiers-recall (не по свойствам хода — инвариант стабильности
// состава tools/list); сама секция требует ещё и проектный чат.
// TokenFactory, а не строка: контекст живёт столько же, сколько адаптер, а у чата старше
// срока жизни сервисного JWT эндпоинт начал бы отвечать 401, и инструменты памяти пропали
// бы молча (ADR-012, урок фазы 1). UseHttp — схема адреса (рубильник живой, см.
// TasksMcpContext): false — ход объявляет прежний stdio-сервер на node (путь отката).
public record MemoryMcpContext(string ApiUrl, Func<string> TokenFactory, string PersonaId,
    string? ProjectId = null, bool DossierToolsEnabled = false, bool UseHttp = false);

// Контекст MCP-сервера рабочего пространства: доступ сессии ко всем проектам владельца
// (список, файлы, базы знаний, единый поиск). Sections — включённые секции инструментов
// (projects/files/knowledge/search[,chats,destructive]); AllowedProjectIds — сужение зоны
// до перечисленных проектов (null — все проекты владельца). SelfSessionId — id самой сессии
// (запрет self-send/self-delete), AgentDepth — глубина делегирования (анти-рекурсия:
// на агентных ходах секции chats/destructive срезаются). Не Claude-специфичен.
// TokenFactory/UseHttp — тот же идиом доставки токена и отката, что у tasks/notes (ADR-012,
// волна 3): захваченный строкой JWT у чата старше ServiceTokenLifetime отдавал бы 401.
// ChatContextEnabled — инструмент context_list (материалы, закреплённые за чатом): включается
// по флагу ВЛАДЕЛЬЦА chat-context, как DossierToolsEnabled у памяти. Не по свойствам хода и
// не по непустоте контекста — состав tools/list обязан быть постоянным в рамках сессии.
public record WorkspaceMcpContext(string ApiUrl, Func<string> TokenFactory, string? ProjectId,
    IReadOnlyList<string> Sections, IReadOnlyList<string>? AllowedProjectIds = null,
    string? SelfSessionId = null, int AgentDepth = 0, bool UseHttp = false,
    bool ChatContextEnabled = false);

// Контекст MCP-сервера персон: адрес API, сервисный токен владельца и проект сессии
// (дефолтный projectId для создания проектных персон; null — глобальный контекст).
// MentionsHint != null — включены @упоминания (флаг persona-mentions): сервер получает
// инструмент persona_ask, SelfPersonaId — персона самого чата (исключается из списка
// собеседников), а MentionsHint — готовый блок-подсказка для системного промпта.
// BindingsEnabled — у владельца включён флаг persona-bindings: сервер персон получает
// инструменты привязок (personas_bindings_*), а подсказка в промпте упоминает их.
// ExtraProjectIds/ExtraPersonaIds — кросс-проектные ProjectPersonas-привязки текущей персоны:
// расширяют personas_list(scope=context) и резолв handle в persona_ask за пределы ProjectId —
// ExtraProjectIds даёт всю команду проекта, ExtraPersonaIds — точечных персон.
// ManageEnabled — модуль manage (personas_create/update/delete/bindings_set/generate_avatar/
// ai_team), AutomationEnabled — модуль automation (personas_automation_*): секции сервера
// за отдельными tool-ключами personas-manage/personas-automation с дефолтом по роли персоны.
// Ядро сервера (personas_list/get, привязки, persona_ask) от них не зависит.
// MentionsToolsEnabled — «в составе ли persona_ask»: ЕДИНАЯ формула SessionManager.
// MentionsToolsEnabled (не «MentionsHint != null» — подсказка гаснет и при единственной
// персоне владельца, а инструмент остаётся); поле входит в отпечаток сигнатуры запуска
// (shape), и расхождение с tools/list — холостой перезапуск процесса CLI (волна 2.1).
// TokenFactory/UseHttp — тот же идиом доставки токена и отката, что у tasks/notes (ADR-012).
// Не Claude-специфичен, как и остальные контексты.
public record PersonasMcpContext(string ApiUrl, Func<string> TokenFactory, string? ProjectId,
    string? SelfPersonaId = null, string? MentionsHint = null, bool BindingsEnabled = false,
    IReadOnlyList<string>? ExtraProjectIds = null, IReadOnlyList<string>? ExtraPersonaIds = null,
    bool ManageEnabled = true, bool AutomationEnabled = true, bool UseHttp = false,
    bool MentionsToolsEnabled = true);

// Элемент манифеста recall — что персона подтянула в ход (память/заметка/база/команда) для
// атрибуции «опирается на…» / «использовано сейчас» (F3). Kind ∈ memory|note|knowledge|team|
// dossier (team — память команды проекта, ③-3.4; dossier — паспорт изменения, ADR-004 §5);
// Ref — id/ссылка.
public sealed record RecallItem(string Kind, string? Ref, string Title, string? Snippet);

// Результат recall-провайдера: текст для системного промпта + айтемы манифеста (F3).
// DossierText — блок паспортов изменений ОТДЕЛЬНО от Text (план «Секции промптов» этап 3,
// флаг specialty-prompt-sections): секция dossier-recall клеится своим местом промпта;
// null — флаг выключен/досье нет (тогда, если есть, оно уже внутри Text — как до фичи).
public sealed record RecallBlock(string? Text, IReadOnlyList<RecallItem> Items, string? DossierText = null);

// Контекст MCP-сервера уведомлений: адрес API и сервисный токен владельца.
// Всегда подключается, когда есть владелец сессии — Claude и агенты могут
// создавать уведомления через инструмент notifications_create.
// SelfPersonaId — персона текущей сессии: notifications-server проставит её как
// personaId в создаваемое уведомление (лицо персоны на уведомлении).
// TokenFactory/UseHttp — тот же идиом доставки токена и отката, что у tasks/notes (ADR-012,
// волна 3).
public record NotificationsMcpContext(string ApiUrl, Func<string> TokenFactory,
    string? SelfPersonaId = null, bool UseHttp = false);

// Контекст MCP-сервера баз знаний Dify (ADR-012, фаза 2 волна 4). Отличие от всех прочих:
// у dify НЕТ своего прокси-слоя — тулсет в Kestrel ходит во внешний Dify API напрямую тем же
// KnowledgeService, что REST/заметки/память. ApiUrl — адрес НАШЕГО бэкенда (эндпоинт
// /mcp/dify/{sessionId}, как у волн 2–3), DifyUrl/DifyKey — внешний API Dify из секции
// конфига Dify; они нужны ТОЛЬКО stdio-ветке отката (env DIFY_API_URL/DIFY_API_KEY узла
// mcp-dify/dist/index.js). На http-ветке ключ наружу не уезжает вовсе — ни в env процесса,
// ни в конфиг хода (улучшение волны 4, зафиксировано в ADR-012).
// Проект/дефолтный датасет в контексте НЕ живут: тулсет резолвит их живьём из
// сессии-вызывателя на каждый tools/list и вызов (датасет может появиться у проекта
// в середине жизни чата). TokenFactory/UseHttp — тот же идиом, что у tasks/notes.
public sealed record DifyMcpContext(string ApiUrl, string DifyUrl, string DifyKey,
    Func<string> TokenFactory, bool UseHttp = false);

// Контекст MCP-сервера виджетов (widget_show): адрес API и фабрика сервисного токена
// владельца. Сервер переехал с node-процесса в Kestrel (ADR-012) — ход подключает его
// по http (POST {ApiUrl}/mcp/widgets), поэтому маркера «сессия с владельцем» уже мало.
// HTML по-прежнему рендерит фронт, инструмент лишь валидирует input.
//
// TokenFactory, а не строка — как у памяти (ADR-012, урок фазы 1): контекст живёт столько
// же, сколько адаптер, а захваченный строкой JWT у чата старше срока жизни токена начал бы
// отдавать 401 и widget_show пропал бы молча. stdio-ветка отката токен не использует вовсе.
//
// UseHttp — СХЕМА адреса допускает http (свойство ApiUrl, стабильно в жизни адаптера);
// fail-closed: https по локальному адресу CLI не осилит, а инструмент пропадёт у модели
// молча. Рубильник Mcp:HttpTransport НЕ входит — живой, на каждый ход (HttpMcpEnabledProvider).
public sealed record WidgetsMcpContext(string ApiUrl, Func<string> TokenFactory, bool UseHttp);

// Контекст MCP-сервера сторожей чатов (ADR-013): адрес API и фабрика сервисного токена
// владельца; сессия-вызыватель едет хвостом URL (/mcp/watch/{sessionId}) — по ней тулсет
// резолвит владельца, проект и будимый чат. TokenFactory/UseHttp — тот же идиом доставки
// токена и гейта схемы, что у widgets. stdio-ветки отката НЕТ (node-сервера не
// существовало): при UseHttp=false или выключенном рубильнике Mcp:HttpTransport тулсет
// ходу не объявляется вовсе.
public sealed record WatchMcpContext(string ApiUrl, Func<string> TokenFactory, bool UseHttp);

// Контекст MCP-сервера графа кода (codegraph_find/neighbors/hubs): адрес API, сервисный
// токен владельца и проект, чей граф доступен инструментами. ProjectId обязателен —
// граф ключуется проектом, в чате вне проекта сервер не подключается.
// SessionId уезжает в X-Caller-Session-Id (наблюдаемость GET /api/mcp/calls).
// RootPath — рабочее дерево сессии: у чата с отдельным worktree свой граф (ADR-003);
// null/пусто — граф корня проекта.
// TokenFactory/UseHttp — тот же идиом доставки токена и отката, что у tasks/notes (ADR-012,
// волна 3). На http-ветке RootPath в маршрут не кладётся: тулсет резолвит дерево живьём
// из сессии-вызывателя (хвост), поэтому состав и адрес от worktree не зависят.
public sealed record CodeGraphMcpContext(string ApiUrl, Func<string> TokenFactory, string ProjectId,
    string? SessionId = null, string? RootPath = null, bool UseHttp = false);

// Контекст MCP-сервера десктопной грани (ADR-008): адрес API, capability-токен хода
// и id чата. Токен отдельный — сервисный JWT владельца эндпоинты /api/devices/* не
// принимают вовсе (иначе руками ходил бы любой чат владельца, включая ночной
// tasks-executor): audience desktop, claims ownerId + sessionId + deviceId, TTL — минуты.
// Чат-вызыватель бэкенд выводит ИЗ ТОКЕНА; DESKTOP_SESSION_ID уезжает в X-Caller-Session-Id
// и служит только диагностикой (GET /api/mcp/calls) — в решении об авторизации он не
// участвует (спуфится). null — грань чату не доставляется.
public sealed record DesktopMcpContext(string ApiUrl, string Token, string SessionId);

// Один MCP-сервер внешнего модуля (контракт docs/modules/integration-contract.md §6):
// Key — ключ сервера в mcp-конфиге хода, Command/Args — запуск из манифеста (args уже
// резолвнуты от каталога модуля), ModuleId — id модуля, ApiUrl — адрес модуля ЧЕРЕЗ gateway
// ядра ({ядро}/api/modules/{id}), TokenFactory — свежий модульный токен chan=mcp (TTL 60 мин)
// на каждый ход. Не Claude-специфичен.
public sealed record ModuleMcpServer(string Key, string Command, IReadOnlyList<string> Args,
    string ModuleId, string ApiUrl, Func<string> TokenFactory);

// Контекст MCP-серверов внешних модулей: аддитивный список поверх встроенных серверов
// (null/пусто — модулей нет или все скрыты фич-флагами module-{id}).
public sealed record ModulesMcpContext(IReadOnlyList<ModuleMcpServer> Servers);

// Один MCP-сервер из личного реестра владельца (Services/Mcp/McpRegistry): Key — ключ
// сервера в конфиге хода, Transport — stdio|http|sse. Значения Env/Headers приходят уже
// РАЗВЁРНУТЫМИ (плейсхолдеры secret:<id> резолвнуты в McpSecretStore) — секрет живёт только
// во временном конфиге хода. AuthVersion входит в отпечаток запуска: заголовки запекаются
// в файл на старте процесса, и обновлённый токен живому процессу иначе не доедет.
public sealed record ExternalMcpServer(string Key, string Transport,
    string? Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env,
    string? Url, IReadOnlyDictionary<string, string> Headers,
    bool AlwaysLoad, int AuthVersion);

// Контекст серверов личного реестра: аддитивно к наследству из .mcp.json (одноимённая
// запись реестра его перекрывает), но встроенные серверы продукта ставятся позже и выигрывают.
public sealed record ExternalMcpContext(IReadOnlyList<ExternalMcpServer> Servers);

// Выделенный memory-сервер персоны-консультанта (файлового сабагента): ключ сервера
// в MCP-конфиге хода ("pmem_<handle>") + контекст memory-server ЭТОЙ персоны (personaId/
// projectId чата едут хвостом URL, ADR-012 фаза 2). Файл агента ссылается на сервер по
// имени (mcpServers: [pmem_<handle>]), а токен живёт только во временном конфиге хода —
// фабрика выдаёт свежий на каждый ход, секреты не попадают в персистентные файлы.
// UseHttp — схема адреса (рубильник Mcp:HttpTransport применяется живьём на каждый ход
// в ClaudeSession, как у остальных контекстов): false — stdio-процесс node.
public sealed record ConsultantMemoryServer(string ServerKey, string ApiUrl, Func<string> TokenFactory,
    string PersonaId, string? ProjectId = null, bool UseHttp = false);

// Файловые сабагенты-персоны: папки для --add-dir хода
// (внутри — .claude/agents/{handle}.md) + pmem-серверы смонтированных персон
// + список имён (handle) для подсказки в системный промпт.
public sealed record PersonaAgentsContext(IReadOnlyList<string> AddDirs,
    IReadOnlyList<ConsultantMemoryServer> MemoryServers,
    IReadOnlyList<string> AgentHandles);

// Per-session контекст, общий для всех адаптеров — то, что SessionManager передаёт
// при создании сессии независимо от провайдера. Claude-специфичные зависимости
// (MCP-конфиг, скиллы, disallowed tools) живут в фабрике адаптеров.
public sealed record LlmSessionContext(
    string RootPath,
    Func<ServerMessage, Task> OnMessage,
    string? RawSystemPrompt,
    Func<IReadOnlyList<PermissionRule>>? PermissionRules,
    TasksMcpContext? TasksMcp,
    NotesMcpContext? NotesMcp = null,
    // Auto-recall заметок: по тексту хода возвращает блок релевантных заметок
    // (текст для промпта + айтемы манифеста «использовано сейчас», F3). Ошибки → null.
    Func<string, Task<RecallBlock?>>? RecallProvider = null,
    // Провайдер системного промпта персоны (имя, роль, контракт характера, дисциплина):
    // вызывается на КАЖДЫЙ ход — правки персоны и смена модели применяются без пересоздания
    // адаптера. null — обычная сессия; вызов может вернуть null (персону удалили).
    Func<string?>? PersonaPromptProvider = null,
    // MCP-сервер долгой памяти персоны (null — сессия без памяти персоны).
    MemoryMcpContext? MemoryMcp = null,
    // Auto-recall долгой памяти персоны: по тексту хода возвращает блок релевантных записей
    // памяти (текст для промпта + айтемы манифеста «использовано сейчас», F3). Ошибки → null.
    Func<string, Task<RecallBlock?>>? PersonaRecallProvider = null,
    // Дополнительные запрещённые инструменты сессии (поверх конфига Claude:DisallowedTools) —
    // например, WebSearch/WebFetch у персоны с выключенной возможностью «web».
    IReadOnlyList<string>? ExtraDisallowedTools = null,
    // MCP-сервер персон: CRUD из любого чата + @упоминания/persona_ask
    // (null — фича выключена или нет владельца).
    PersonasMcpContext? PersonasMcp = null,
    // MCP-сервер уведомлений: создание уведомлений из Claude/агентов
    // (null — владелец не определён, сессия без MCP).
    NotificationsMcpContext? NotificationsMcp = null,
    // MCP-сервер рабочего пространства: проекты/файлы/знания/поиск владельца
    // (null — флаг workspace-tools выключен или нет владельца).
    WorkspaceMcpContext? WorkspaceMcp = null,
    // Блок «Привязанные знания и правила» персоны (флаг persona-bindings): по тексту хода
    // возвращает индекс привязанных источников + выжимки режима «всегда» для системного
    // промпта (null — фича выключена или сессия без персоны). Вычисляется каждый ход;
    // флаг проверяется внутри, ошибки — тихо в null (ход идёт без блока).
    Func<string, Task<string?>>? BindingsProvider = null,
    // Per-ход slice top-10 god-nodes Code Graph в системный промпт (ADR вариант A): компактный
    // список хабов по связности + (при isStale) пометка устаревания. Принимает текст хода
    // (не используется — god-узлы структурны), null — фича выключена или сессия без rootPath;
    // ошибки внутри → null (ход идёт без блока).
    Func<string?, Task<string?>>? CodeGraphProvider = null,
    // Секции промпта специальности персоны (план «Секции промптов» этап 3, флаг
    // specialty-prompt-sections): сценарные инструкции «когда и как» по роли (история,
    // граф кода, процессы, правила роли) — текст хода не используется (секции статичны для
    // owner+специальности), null — фича выключена, персона без специальности, групповой
    // чат или сессия без владельца/персоны. Гейт по флагу — внутри, на каждый ход.
    Func<string?, Task<string?>>? PromptSectionsProvider = null,
    // Файловые сабагенты-персоны: вычисляется на КАЖДЫЙ ход
    // (актуальные персоны/модель сессии), внутри — троттлёный reconcile файлов.
    // null — фича выключена или нет владельца; вызов может вернуть null.
    Func<PersonaAgentsContext?>? PersonaAgentsProvider = null,
    // Драйвер среды исполнения владельца (local / docker-песочница);
    // null — локальный запуск, историческое поведение
    Execution.IProcessLauncher? Launcher = null,
    // MCP-серверы внешних модулей из реестра (контракт §6, ТЗ R7); null — модулей нет
    // или скрыты фич-флагами. Аддитивно к встроенным серверам, коллизии ключей — пропуск.
    ModulesMcpContext? ModulesMcp = null,
    // MCP-сервер виджетов чата (widget_show): null — нет владельца сессии.
    WidgetsMcpContext? WidgetsMcp = null,
    // MCP-сервер графа кода (codegraph_find/neighbors/hubs): навигация агента по структуре
    // проекта. null — чат вне проекта или нет владельца.
    CodeGraphMcpContext? CodeGraphMcp = null,
    // MCP-сервер баз знаний Dify (dify: search_knowledge и CRUD датасетов/документов).
    // null — нет владельца или Dify не настроен (секция Dify: ApiUrl/ApiKey).
    DifyMcpContext? DifyMcp = null,
    // Браузер (плагин playwright, 24 browser_*-инструмента): false — плагин гасится на
    // запуске CLI (ClaudeRuntimeSettings). Решение принимается по персоне, не по ходу —
    // Tool-ключ «browser» с дефолтом по роли (тестировщику включён). true — как раньше:
    // чат без персоны и все прочие пути ничего не теряют.
    bool BrowserEnabled = true,
    // Приёмник снимков промпта хода: принимает черновик, возвращает id записанного снимка
    // (null — записать не удалось; снимок диагностический и ход не роняет). Замыкает
    // Session.Id на стороне SessionManager — адаптер ключа хранилища не знает.
    Func<PromptSnapshotDraft, string?>? PromptSnapshotSink = null,
    // Дописать в уже записанный снимок состав инструментов и статусы MCP-серверов: они
    // известны только из system/init, который приходит после старта процесса.
    // Аргументы: id снимка, имена инструментов, серверы.
    Action<string, IReadOnlyList<string>, IReadOnlyList<McpServerInfo>>? PromptSnapshotToolsSink = null,
    // Корень профиля claude CLI (CLAUDE_CONFIG_DIR) этого хода: оттуда берутся глобальный
    // CLAUDE.md и каталог скиллов для блока «слой CLI». Резолвится в SessionManager
    // (ConfigRootFor знает раскладку профилей и песочницы), сюда приходит готовым;
    // null — слой CLI собирается без файлов профиля.
    string? CliConfigRoot = null,
    // MCP-серверы личного реестра владельца: вычисляется на КАЖДЫЙ ход (правка реестра
    // применяется без пересоздания адаптера, как у PersonaAgentsProvider). Решение
    // принимается только по owner/project/persona — от свойств хода состав не зависит.
    // null — фича выключена, нет владельца или реестр пуст.
    Func<ExternalMcpContext?>? ExternalMcpProvider = null,
    // Подсказка про трейлер CCS-Session/CCS-Task в системный промпт (ADR-004, «Паспорта
    // изменений»): null — чат вне проекта. Вычисляется при построении контекста (не Func —
    // как WorkspaceMcp/NotificationsMcp, тоже не живёт мид-сессию без пересоздания адаптера).
    string? DossierTrailerHint = null,
    // Персист сессий (SessionManager.SaveSessions): фолбэк-адаптер вызывает его после restore
    // модели в finally, чтобы переписать sessions.json восстановленными значениями — иначе
    // финальный result успевает сохранить подменённую модель (Major 1 ревью). null (тесты) —
    // персист не вызывается.
    Action? PersistSessions = null,
    // Requeue хода в серверную Pending-очередь взамен байпаса в _inner (инцидент 2026-08-10 П3):
    // фолбэк-адаптер вызывает при попытке доставки под активной оркестрацией — SessionManager
    // ставит ход в очередь (kind=Agent, дедуп по text+persona), и он разбирается штатным drain
    // после завершения оркестрации (см. OrchestrationDone). Аргументы: sessionId, text, вложения,
    // глубина делегирования, suppressTasksExecute. null (тесты без SessionManager) — адаптер
    // откатывается к прежнему байпасу в _inner, чтобы не терять ходы.
    Func<string, string, IReadOnlyList<string>?, int, bool, Task>? EnqueueBypass = null,
    // Сигнал «оркестрация хода завершена» (finally фолбэк-адаптера, _turn сброшен): SessionManager
    // запускает разбор Pending-очереди — ходы, накопленные через EnqueueBypass во время
    // оркестрации, доставляются штатно (теперь уже в свободный адаптер). null (тесты) — no-op.
    Action<string>? OrchestrationDone = null,
    // Приёмник паспортов прогонов сабагентов (диагностика обрывов, SubagentRunLog): вызывается
    // на завершении каждого агента хода. null (тесты, сессия без стора) — паспорта не ведутся.
    Action<Claude.SubagentRunPassport>? SubagentRunSink = null,
    // MCP-сервер десктопной грани (ADR-008): руки на машине пользователя.
    // null — грань чату не положена (не десктопный чат, выключена в проекте, нет флага,
    // чат-исполнитель задачи / автоматизации / групповой). Решается по КОНФИГУРАЦИИ
    // на момент запуска CLI — от свойств хода состав не зависит.
    DesktopMcpContext? DesktopMcp = null,
    // Сводный признак «у сессии есть продуктовые MCP-серверы, чей АДРЕС допускает http»:
    // от него (вместе с живым рубильником ниже) ClaudeSession ставит NO_PROXY хода
    // (ADR-012) — обход прокси нужен ЛЮБОМУ http-серверу, а не одному виджету. Решение
    // принимает SessionManager на базе единого гейта схемы адреса; pmem-консультанты
    // приезжают списком на каждый ход и уточняют признак на месте (UseHttp в
    // ConsultantMemoryServer).
    bool HttpMcpActive = false,
    // Рубильник Mcp:HttpTransport (откат всех продуктовых серверов на stdio) — ЖИВОЙ:
    // вызывается на КАЖДЫЙ ход, как PromptSectionsProvider/ExternalMcpProvider. Захваченный
    // bool вмораживал транспорт в контекст адаптера, и откат не доезжал до уже поднятых
    // чатов до рестарта бэкенда (техдолг ADR-012 §1). Смена значения меняет конфиг хода
    // и сигнатуру запуска — процесс CLI перезапустится штатно. null — рубильник включён
    // (тесты без SessionManager).
    Func<bool>? HttpMcpEnabledProvider = null,
    // Живая персона чата: матрицы персоны/специальности участвуют в постройке цепочки
    // фолбэка (ClaudeSession.EffectiveTurnChain → ModelAssignmentResolver.ResolveChain
    // с персоной) — старт и хвост хода резолвятся по одним правилам. Перечитывается каждый
    // ход — правка матриц применяется без пересоздания адаптера. null — сессия без персоны.
    Func<Persona?>? PersonaProvider = null,
    // Состав контекста чата (материалы, закреплённые кнопкой «в контекст чата»): вызывается
    // на КАЖДЫЙ ход — материал, добавленный в идущем разговоре, попадает в подсказку
    // следующего хода без пересоздания адаптера (обычные поля контекста мид-сессию не живут).
    // Влияет ТОЛЬКО на промпт: состав MCP-инструментов от содержимого контекста не зависит
    // (гейт самого инструмента — WorkspaceMcpContext.ChatContextEnabled).
    // null — фича выключена или сессия без владельца.
    Func<IReadOnlyList<SessionContextEntry>>? ChatContextProvider = null,
    // MCP-сервер сторожей чатов (watch_*): null — чат без владельца. Наличие контекста —
    // свойство владельца (инвариант стабильности состава ADR-012).
    WatchMcpContext? WatchMcp = null);
