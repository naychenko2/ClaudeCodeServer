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

// Контекст MCP-сервера памяти персоны: адрес API, сервисный токен владельца и id персоны,
// чья долгая память доступна инструментами mcp__memory__* в этой сессии.
public record MemoryMcpContext(string ApiUrl, string Token, string PersonaId);

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

// Элемент манифеста recall — что персона подтянула в ход (память/заметка/база) для атрибуции
// «опирается на…» / «использовано сейчас» (F3). Kind ∈ memory|note|knowledge; Ref — id/ссылка.
public sealed record RecallItem(string Kind, string? Ref, string Title, string? Snippet);

// Результат recall-провайдера: текст для системного промпта + айтемы манифеста (F3).
public sealed record RecallBlock(string? Text, IReadOnlyList<RecallItem> Items);

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
    // Auto-recall заметок: по тексту хода возвращает готовый markdown-блок с
    // релевантными заметками для системного промпта (null — не подмешивать).
    // Провайдер-агностично; вычисляется каждый ход. Ошибки внутри — тихо в null.
    Func<string, Task<string?>>? RecallProvider = null,
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
    // MCP-сервер рабочего пространства: проекты/файлы/знания/поиск владельца
    // (null — флаг workspace-tools выключен или нет владельца).
    WorkspaceMcpContext? WorkspaceMcp = null,
    // Блок «Привязанные знания и правила» персоны (флаг persona-bindings): по тексту хода
    // возвращает индекс привязанных источников + выжимки режима «всегда» для системного
    // промпта (null — фича выключена или сессия без персоны). Вычисляется каждый ход;
    // флаг проверяется внутри, ошибки — тихо в null (ход идёт без блока).
    Func<string, Task<string?>>? BindingsProvider = null);
