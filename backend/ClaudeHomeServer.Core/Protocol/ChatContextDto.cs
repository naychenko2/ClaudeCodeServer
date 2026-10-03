using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ChatContext;

namespace ClaudeHomeServer.Protocol;

// DTO контекста чата для фронта (ADR-023 §2.1). Подписи уже посчитаны Describe провайдера владельца:
// фронт их не форматирует, поэтому строка, панель и хвост хода не расходятся.
// Исполнитель («Чем») в DTO не входит (решение Р2) — его даёт quote по операции действия.
public sealed record ChatContextDto(long Revision, ChatContextItemDto? Primary, IReadOnlyList<ChatContextRefDto> Refs);

// Основной объект: Role всегда null
public sealed record ChatContextItemDto(
    string Id,
    string Kind,
    JsonObject Ref,
    string? Role,
    ContextActor By,
    DateTime AddedAt,
    string Label,
    string? Version,
    string? Thumb,
    bool Missing);

// Референс: UsedBy — операции основного объекта, которые его берут; пусто — чип серый
public sealed record ChatContextRefDto(
    string Id,
    string Kind,
    JsonObject Ref,
    string? Role,
    ContextActor By,
    DateTime AddedAt,
    string Label,
    string? Version,
    string? Thumb,
    bool Missing,
    IReadOnlyList<string> UsedBy);

public static class ChatContextEventNames
{
    public const string Changed = "chat_context_changed";
}

// Контекст чата сменился (любая запись в IChatContextStore); SessionId — базовое поле, чат-владелец
public record ChatContextChangedMessage(ChatContextDto Context) : ServerMessage(ChatContextEventNames.Changed);

// Тело 409 context_changed: свежий DTO, по нему фронт перерисует чипы и цену
public sealed record ChatContextConflictDto(string Error, ChatContextDto Context);
