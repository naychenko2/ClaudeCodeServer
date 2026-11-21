namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Детская поверхность: ОДИН замороженный список того, что ребёнку открыто во взрослом
/// WorkspacePage (ADR-041 ред. 8, решения 76–84, И11; разрез §14). Доставляется фронту
/// полем childSurface в GET /api/auth/me.
///
/// Не в матрице проекта: ProjectCapabilities — функция от проекта, а проект-предмет открыт
/// и куратору; ограничение зависит от того, кто смотрит, а не от проекта.
///
/// Default-deny: ключа нет в списке — он не показывается; поля нет у детского профиля —
/// экран «нет связи», а не взрослый набор.
///
/// Это ОПИСАНИЕ поверхности UI, а не серверный замок: ChildModeGate, ChildHubFilter,
/// детский allow-list MCP и --tools остаются там, где были, и в этот файл не переносятся.
/// </summary>
public static partial class ChildModeAllowList
{
    public static readonly ChildSurface Surface = new(
        // Ровно ключи реестра панелей фронта (panelCatalog.ts, WORKSPACE_KEYS): то, что
        // ребёнку открыто в рельсе, — пересечение этого списка с набором экрана и матрицей
        // проекта (Ф1). Открытые серверные ручки у этих панелей — Б20.
        Panels: ["files", "knowledge", "notes", "chatContext", "tasks", "chats"],
        // Возможности штатного Composer для ребёнка. voiceInput — браузерное распознавание
        // речи (решение Андрея 19.11): серверного распознавания речи нет и не заводится.
        // askUserAnswer — ответы на «вопросы с кнопками» AskUserQuestion (Б19).
        ComposerFeatures: ["text", "attachFromSubject", "voiceInput", "askUserAnswer"],
        // Белый список видов детской ленты — перенесён один в один из childFeed
        // (ChildLessonChat.tsx:66-95): реплики (user_message, text), итог хода (result),
        // сбои (error, error_group, rate_limit, provider_limit, model_switched,
        // work_loop_stopped, hands_notice) и вызов виджета репетитора (tool_use —
        // рисуется только без ошибки, как делал childFeed). Всё вне списка — мимо ленты,
        // включая будущие виды.
        FeedKinds:
        [
            "user_message", "text", "result", "error", "error_group", "rate_limit",
            "provider_limit", "model_switched", "work_loop_stopped", "hands_notice", "tool_use",
        ]);
}

/// <summary>
/// Снимок детской поверхности для доставки фронту. Ключи — идентификаторы фронта:
/// панели — ключи реестра panelCatalog.ts; возможности ввода и виды ленты — согласованные
/// с фронтом строки (Ф1). Списки заморожены: изменение — только правкой этого файла
/// вместе с тестом-снимком (AuthControllerTests).
/// </summary>
public sealed record ChildSurface(
    IReadOnlyList<string> Panels,
    IReadOnlyList<string> ComposerFeatures,
    IReadOnlyList<string> FeedKinds);
