using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Реализация IPersonaSkillBindingLookup: тонкая обёртка над PersonaManager и
// PersonaBindingsService, достаёт ровно то, что нужно вызывающим (SkillSuggestService,
// PluginDirSource) — поля для контекста + пред-отфильтрованный список Skill-биндингов.
//
// Skill-цели берутся из EffectiveBindings (R-A2 M2): тот же эффективный список, что видит
// промпт хода. Четвёртый путь чтения привязок (--plugin-dir через PluginDirSource) обязан
// давать тот же набор пакетов, что и индекс промпта: с own-списком миграция A2, удалившая
// копии, выключила бы пакет при живом унаследованном умении.
//
// Character слит сюда же (Contract.Character ?? SystemPrompt), чтобы вызывающий
// не повторял ту же логику и не зависел от `PersonaContract`. Полная `Persona`
// через шов не утекает — Skills видит только DTO.
public sealed class PersonaSkillBindingLookup(PersonaManager personas, PersonaBindingsService bindings)
    : IPersonaSkillBindingLookup
{
    public PersonaForSkillSuggestion? Get(string ownerId, string personaId)
    {
        var persona = personas.Get(personaId, ownerId);
        if (persona is null) return null;

        var character = persona.Contract?.Character ?? persona.SystemPrompt;
        var mustDo = persona.Contract?.MustDo is { Count: > 0 } m
            ? m.Where(x => !string.IsNullOrWhiteSpace(x)).ToList()
            : null;
        // M4.2: выключенная привязка (Mode == Off) не открывает пакет — иначе --plugin-dir
        // доставлялся бы даже для выключенной привязки. Гашение своей Off-копией работает
        // через EffectiveBindings: унаследованная запись с тем же именем в список не попадает.
        var skillTargets = bindings.EffectiveBindings(ownerId, persona)
            .Where(e => e.Binding.Type == PersonaBindingType.Skill && e.Binding.Mode != PersonaBindingMode.Off)
            .Select(e => e.Binding.Target)
            .ToList();

        return new PersonaForSkillSuggestion(
            Name: persona.Name,
            Role: persona.Role,
            Description: persona.Description,
            Character: character,
            MustDo: mustDo,
            SkillBindingTargets: skillTargets);
    }
}
