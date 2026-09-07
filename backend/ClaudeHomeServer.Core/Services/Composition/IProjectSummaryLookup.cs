namespace ClaudeHomeServer.Services.Composition;

// Шов «имя + системный промпт проекта по id» для подбора скиллов под проект.
// SkillSuggestService.SuggestForProjectAsync строит контекст из имени и системного
// промпта проекта и больше не держит конкретный ProjectManager.
//
// Контракт повторяет форму `ProjectManager.GetById(id)` (возвращает Project или null),
// но отдаёт DTO `ProjectSummary` с двумя полями, а не полный Project со всем графом
// зависимостей модели (PermissionRule, BoardColumn, ProjectIcon, RootPath, …) —
// тот живёт в Main и в Core не переезжает, чтобы не тащить модели вертикалей в спинку.
// RootPath отдельно НЕ отдаём: SkillsController не входит в вертикаль Services.Skills
// и под сторож границ не попадает; если в будущем понадобится и ему — seam расширим
// отдельным методом, не размывая текущий.
public interface IProjectSummaryLookup
{
    // null — проект не найден.
    ProjectSummary? GetById(string projectId);
}

/// <summary>Минимальный DTO: имя и системный промпт проекта.</summary>
public sealed record ProjectSummary(string Name, string? SystemPrompt);
