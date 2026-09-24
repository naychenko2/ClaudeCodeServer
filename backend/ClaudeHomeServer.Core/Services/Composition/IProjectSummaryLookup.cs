namespace ClaudeHomeServer.Services.Composition;

// Шов «имя + системный промпт проекта по id» для подбора скиллов под проект.
// SkillSuggestService.SuggestForProjectAsync строит контекст из имени и системного
// промпта проекта и больше не держит конкретный ProjectManager.
//
// Контракт повторяет форму соседнего шва `IPersonaSkillBindingLookup.Get(ownerId, personaId)`
// — сверку владельца делает
// РЕАЛИЗАЦИЯ шва, а не вызывающий: чужой проект отдаётся как «не найден», и забыть
// сравнение на стороне вертикали невозможно (раньше шов принимал один projectId, и в
// контекст запроса к модели уезжали имя и системный промпт чужого проекта).
// Отдаёт DTO `ProjectSummary` с двумя полями, а не полный Project со всем графом
// зависимостей модели (PermissionRule, BoardColumn, ProjectIcon, RootPath, …) —
// тот живёт в Main и в Core не переезжает, чтобы не тащить модели вертикалей в спинку.
// RootPath отдельно НЕ отдаём: SkillsController не входит в вертикаль Services.Skills
// и под сторож границ не попадает; если в будущем понадобится и ему — seam расширим
// отдельным методом, не размывая текущий.
public interface IProjectSummaryLookup
{
    // null — проект не найден ИЛИ принадлежит другому владельцу (разницы наружу нет).
    ProjectSummary? GetById(string ownerId, string projectId);
}

/// <summary>Минимальный DTO: имя и системный промпт проекта.</summary>
public sealed record ProjectSummary(string Name, string? SystemPrompt);
