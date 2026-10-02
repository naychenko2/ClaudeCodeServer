namespace ClaudeHomeServer.Services.ImageEditor;

// Инструменты MCP-сервера image-editor, которые чат проекта выполняет без карточки разрешения
// (ADR-019 §4): каждое действие агента и так видно в ленте — тихой строкой или карточкой запуска
// с ценой, а спрашивать «можно?» на каждый вызов — шум. Список едет в ход через
// ImageEditorMcpContext и проверяется в ClaudeSession.DecidePermission. Имена обязаны совпадать
// со схемами тулсета модуля — это держит тест модуля.
public static class ImageEditorAgentTools
{
    public static readonly IReadOnlyList<string> AutoAllowTools =
    [
        "mcp__image-editor__image_state",
        "mcp__image-editor__image_focus",
        "mcp__image-editor__image_new",
        "mcp__image-editor__image_generate",
        "mcp__image-editor__image_cancel",
        "mcp__image-editor__image_suggest_prompt",
    ];
}
