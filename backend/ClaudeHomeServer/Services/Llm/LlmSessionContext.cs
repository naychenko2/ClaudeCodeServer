using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm;

// Контекст MCP-сервера задач для сессии: адрес API, сервисный токен владельца
// и проект (null — чат вне проекта, контекст личных задач).
// Не Claude-специфичен: DeepSeek-адаптер может реализовать те же tasks_* инструменты нативно.
public record TasksMcpContext(string ApiUrl, string Token, string? ProjectId);

// Per-session контекст, общий для всех адаптеров — то, что SessionManager передаёт
// при создании сессии независимо от провайдера. Claude-специфичные зависимости
// (MCP-конфиг, скиллы, disallowed tools) живут в фабрике адаптеров.
public sealed record LlmSessionContext(
    string RootPath,
    Func<ServerMessage, Task> OnMessage,
    string? RawSystemPrompt,
    Func<IReadOnlyList<PermissionRule>>? PermissionRules,
    TasksMcpContext? TasksMcp);
