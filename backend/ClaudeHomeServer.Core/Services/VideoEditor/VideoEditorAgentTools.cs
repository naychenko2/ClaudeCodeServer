namespace ClaudeHomeServer.Services.VideoEditor;

// Инструменты MCP-сервера video-editor, которые чат выполняет без карточки разрешения (ADR-022 §5, как
// AudioEditorAgentTools у звука): каждое действие агента и так видно в ленте теми же карточками, что и ручное
// (сцена, запуск с ценой, тихие строки сохранения, правки и сборки). Список едет в ход через
// VideoEditorMcpContext и проверяется в ClaudeSession.DecidePermission. Имена обязаны совпадать со схемами
// тулсета модуля — это держит тест модуля.
public static class VideoEditorAgentTools
{
    public static readonly IReadOnlyList<string> AutoAllowTools =
    [
        "mcp__video-editor__video_state",
        "mcp__video-editor__video_focus",
        "mcp__video-editor__video_new",
        "mcp__video-editor__video_scene_set",
        "mcp__video-editor__video_suggest_prompt",
        "mcp__video-editor__video_shoot",
        "mcp__video-editor__video_cancel",
        "mcp__video-editor__video_wait",
        "mcp__video-editor__video_save_scene",
        "mcp__video-editor__video_film_edit",
        "mcp__video-editor__video_film_build",
    ];
}
