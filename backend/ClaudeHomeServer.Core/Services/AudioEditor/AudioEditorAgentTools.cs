namespace ClaudeHomeServer.Services.AudioEditor;

// Инструменты MCP-сервера audio-editor, которые чат выполняет без карточки разрешения (ADR-021 §5, как
// ImageEditorAgentTools у картинок): каждое действие агента и так видно — нитью в ленте или карточкой
// запуска с ценой. Список едет в ход через AudioEditorMcpContext и проверяется в
// ClaudeSession.DecidePermission. Имена обязаны совпадать со схемами тулсета модуля — это держит тест модуля.
public static class AudioEditorAgentTools
{
    public static readonly IReadOnlyList<string> AutoAllowTools =
    [
        "mcp__audio-editor__audio_state",
        "mcp__audio-editor__audio_focus",
        "mcp__audio-editor__audio_new",
        "mcp__audio-editor__audio_voices",
        "mcp__audio-editor__audio_generate",
        "mcp__audio-editor__audio_concat",
        "mcp__audio-editor__audio_suggest_prompt",
        "mcp__audio-editor__audio_cancel",
    ];
}
