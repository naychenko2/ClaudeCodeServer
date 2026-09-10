using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Dossiers;

// Запрос пассивного recall паспортов (этап 2, ADR-004 §5): якоря, известные на старте хода
// (linkedFiles задачи + пути из текста хода + файлы предыдущего хода сессии), плюс сам текст.
// RootPath — EffectiveRoot чата (WorktreePath ?? project.RootPath): у worktree-чата своё дерево.
// TaskId — задача чата (у чата-исполнителя): её LinkedFiles тоже якорь (сервис сам проверит
// принадлежность задаче этого проекта, по образцу guard'а захвата).
//
// Этап 5, узкие швы Turn: пара контрактных DTO переехала из `Services/Dossiers` в спину
// (Core), потому что запрос собирает ОДИН потребитель — контрибьютор промпта в Turn, —
// а исполняет его Memory (`PersonaMemoryService.BuildRecallAsync`). Держать общий DTO
// внутри вертикали значило бы, что оба посторонних слоя ссылаются на Dossiers ради
// формы данных, а не ради поведения. Namespace сохранён — переезд не трогает вызывающих.
public sealed record DossierRecallRequest(
    string ProjectId,
    string? RootPath,
    string? TaskId,
    IReadOnlyList<string> AnchorFiles,
    string TurnText);

// Результат recall'а: markdown-кусок для блока PersonaMemoryService.BuildRecallAsync + записи,
// реально попавшие в блок (для манифеста атрибуции F3). Text=null — подмешивать нечего.
public sealed record DossierRecallResult(string? Text, IReadOnlyList<ChangeDossier> Used);
