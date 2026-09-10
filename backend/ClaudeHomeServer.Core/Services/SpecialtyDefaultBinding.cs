using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Типовое умение роли: привязка-заготовка, материализуемая в Persona.Bindings при
// создании персоны (модель «копия при создании», не динамическое наследование).
// Цель НЕ хранится: конкретную цель подбирает AI по каталогу владельца; исключение —
// «Навык» (Skill): там явное имя скилла (SkillName), отсутствующие скиллы при
// материализации пропускаются молча.
//
// Вынесено из `Services/Llm/SpecialtySettingsStore.cs` в Core на этапе 5, волне 4
// переноса каталогов специальностей: `SpecialtyPromptPresets.DefaultBindingsProfile`
// (тоже в Core) отдаёт наружу `IReadOnlyList<SpecialtyDefaultBinding>`, и без Core-типа
// Llm не мог бы стоять в отдельной сборке — SpecialtyPromptPresets жил бы в Main
// и тянул SpecialtySettingsStore обратно.
public class SpecialtyDefaultBinding
{
    public PersonaBindingType Type { get; set; }
    public PersonaBindingMode Mode { get; set; } = PersonaBindingMode.Auto;
    // Условие «когда применять» — попадает в индекс системного промпта (как Condition привязки)
    public string Condition { get; set; } = "";
    // Имя скилла из каталога владельца — только при Type == Skill
    public string? SkillName { get; set; }
}
