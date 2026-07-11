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

// Контекст MCP-сервера персон: адрес API, сервисный токен владельца и проект сессии
// (дефолтный projectId для создания проектных персон; null — глобальный контекст).
// MentionsHint != null — включены @упоминания (флаг persona-mentions): сервер получает
// инструмент persona_ask, SelfPersonaId — персона самого чата (исключается из списка
// собеседников), а MentionsHint — готовый блок-подсказка для системного промпта.
// Не Claude-специфичен, как и остальные контексты.
public record PersonasMcpContext(string ApiUrl, string Token, string? ProjectId,
    string? SelfPersonaId = null, string? MentionsHint = null);

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
    // Auto-recall долгой памяти персоны: по тексту хода возвращает markdown-блок
    // релевантных записей памяти. Подмешивается независимо от заметок. Ошибки → null.
    Func<string, Task<string?>>? PersonaRecallProvider = null,
    // Дополнительные запрещённые инструменты сессии (поверх конфига Claude:DisallowedTools) —
    // например, WebSearch/WebFetch у персоны с выключенной возможностью «web».
    IReadOnlyList<string>? ExtraDisallowedTools = null,
    // MCP-сервер персон: CRUD из любого чата + @упоминания/persona_ask
    // (null — фича выключена или нет владельца).
    PersonasMcpContext? PersonasMcp = null);
