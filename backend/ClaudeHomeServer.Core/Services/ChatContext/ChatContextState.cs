using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.ChatContext;

// Состояние контекста чата (ADR-023 §1). Kind — вид объекта, объявленный вертикалью ("image", "audio",
// "video-scene", "project-file", "image-character", "audio-voice"…). Ref — непрозрачный для спины
// JSON-объект: смысл знает только владелец вида ({threadId, versionId} у картинки, {slug} у голоса).
public sealed record ChatContextState(long Revision, ContextItem? Primary, IReadOnlyList<ContextItem> Refs);

public sealed record ContextItem(
    string Id,             // id элемента в контексте (для ✕ и undo), не id объекта
    string Kind,
    JsonObject Ref,
    string? Role,          // у Primary — null; у референса — роль из списка ролей владельца
    ContextActor By,       // Human | Agent — чип ✦
    DateTime AddedAt);

public enum ContextActor { Human, Agent }
