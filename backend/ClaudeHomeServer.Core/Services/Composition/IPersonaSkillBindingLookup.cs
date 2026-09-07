namespace ClaudeHomeServer.Services.Composition;

// Шов «персона + её Skill-биндинги для подбора скиллов».
// SkillSuggestService больше не держит конкретный PersonaManager: вся фильтрация
// биндингов по PersonaBindingType.Skill спрятана здесь, а полная модель `Persona`
// (которая живёт в Main) в Core не уезжает — seam возвращает узкий DTO с полями,
// реально нужными подбору (Role/Name/Description/Character/MustDo) плюс
// пред-отфильтрованный список Skill-биндингов.
//
// Если завтра SkillSuggestService понадобится ещё и фильтр по Knowledge/Project —
// расширим, а пока seam узкий и повторяет форму `PersonaManager.Get(id, ownerId)`.
// Character уже слит с SystemPrompt (Contract.Character ?? SystemPrompt), чтобы
// вызывающий не повторял ту же логику и не зависел от `PersonaContract`.
public interface IPersonaSkillBindingLookup
{
    // null — персона не найдена.
    PersonaForSkillSuggestion? Get(string ownerId, string personaId);
}

/// <summary>
/// Минимальный DTO для подбора скиллов под персону: имя/роль/описание для шапки
/// контекста, характер для блока обязанностей, отфильтрованные Skill-биндинги
/// для исключения уже привязанных.
/// </summary>
public sealed record PersonaForSkillSuggestion(
    string Name,
    string? Role,
    string? Description,
    string? Character,
    IReadOnlyList<string>? MustDo,
    IReadOnlyList<string> SkillBindingTargets);
