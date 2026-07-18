using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm;

// Контекст MCP-сервера задач для сессии: адрес API, сервисный токен владельца
// и проект (null — чат вне проекта, контекст личных задач).
// Не Claude-специфичен: DeepSeek-адаптер может реализовать те же tasks_* инструменты нативно.
public record TasksMcpContext(string ApiUrl, string Token, string? ProjectId);

// Контекст MCP-сервера заметок: адрес API, сервисный токен владельца и проект
// (задаёт источник по умолчанию для создания заметок; null — личный vault).
public record NotesMcpContext(string ApiUrl, string Token, string? ProjectId);

// Контекст MCP-сервера памяти персоны: адрес API, сервисный токен владельца, id персоны,
// чья долгая память доступна инструментами mcp__memory__* в этой сессии, и проект персоны
// (③-3.4: проектная персона дополнительно получает team_memory_* — общую память команды
// проекта; null — глобальная персона, командной памяти нет).
public record MemoryMcpContext(string ApiUrl, string Token, string PersonaId, string? ProjectId = null);

// Контекст MCP-сервера рабочего пространства: доступ сессии ко всем проектам владельца
// (список, файлы, базы знаний, единый поиск). Sections — включённые секции инструментов
// (projects/files/knowledge/search[,chats,destructive]); AllowedProjectIds — сужение зоны
// до перечисленных проектов (null — все проекты владельца). SelfSessionId — id самой сессии
// (запрет self-send/self-delete), AgentDepth — глубина делегирования (анти-рекурсия:
// на агентных ходах секции chats/destructive срезаются). Не Claude-специфичен.
public record WorkspaceMcpContext(string ApiUrl, string Token, string? ProjectId,
    IReadOnlyList<string> Sections, IReadOnlyList<string>? AllowedProjectIds = null,
    string? SelfSessionId = null, int AgentDepth = 0);

// Контекст MCP-сервера персон: адрес API, сервисный токен владельца и проект сессии
// (дефолтный projectId для создания проектных персон; null — глобальный контекст).
// MentionsHint != null — включены @упоминания (флаг persona-mentions): сервер получает
// инструмент persona_ask, SelfPersonaId — персона самого чата (исключается из списка
// собеседников), а MentionsHint — готовый блок-подсказка для системного промпта.
// BindingsEnabled — у владельца включён флаг persona-bindings: сервер персон получает
// инструменты привязок (personas_bindings_*), а подсказка в промпте упоминает их.
// Не Claude-специфичен, как и остальные контексты.
public record PersonasMcpContext(string ApiUrl, string Token, string? ProjectId,
    string? SelfPersonaId = null, string? MentionsHint = null, bool BindingsEnabled = false);

// Элемент манифеста recall — что персона подтянула в ход (память/заметка/база/команда) для
// атрибуции «опирается на…» / «использовано сейчас» (F3). Kind ∈ memory|note|knowledge|team
// (team — память команды проекта, ③-3.4); Ref — id/ссылка.
public sealed record RecallItem(string Kind, string? Ref, string Title, string? Snippet);

// Результат recall-провайдера: текст для системного промпта + айтемы манифеста (F3).
public sealed record RecallBlock(string? Text, IReadOnlyList<RecallItem> Items);

// Контекст MCP-сервера уведомлений: адрес API и сервисный токен владельца.
// Всегда подключается, когда есть владелец сессии — Claude и агенты могут
// создавать уведомления через инструмент notifications_create.
public record NotificationsMcpContext(string ApiUrl, string Token);

// Выделенный memory-сервер персоны-консультанта (файлового сабагента): ключ сервера
// в MCP-конфиге хода ("pmem_<handle>") + env memory-server ЭТОЙ персоны. Файл агента
// ссылается на сервер по имени (mcpServers: [pmem_<handle>]), а определение с токеном
// живёт только во временном конфиге хода — секреты не попадают в персистентные файлы.
public sealed record ConsultantMemoryServer(string ServerKey, string ApiUrl, string Token,
    string PersonaId, string? ProjectId = null);

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
    // Файловые сабагенты-персоны: вычисляется на КАЖДЫЙ ход
    // (актуальные персоны/модель сессии), внутри — троттлёный reconcile файлов.
    // null — фича выключена или нет владельца; вызов может вернуть null.
    Func<PersonaAgentsContext?>? PersonaAgentsProvider = null,
    // Драйвер среды исполнения владельца (local / docker-песочница);
    // null — локальный запуск, историческое поведение
    Execution.IProcessLauncher? Launcher = null);
