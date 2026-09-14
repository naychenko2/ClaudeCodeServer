using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Реализация IPersonaSkillBindingLookup: тонкая обёртка над PersonaManager,
// достаёт ровно то, что нужно SkillSuggestService — поля для контекста
// + пред-отфильтрованный список Skill-биндингов.
//
// Character слит сюда же (Contract.Character ?? SystemPrompt), чтобы вызывающий
// не повторял ту же логику и не зависел от `PersonaContract`. Полная `Persona`
// через шов не утекает — Skills видит только DTO.
public sealed class PersonaSkillBindingLookup(PersonaManager personas) : IPersonaSkillBindingLookup
{
    public PersonaForSkillSuggestion? Get(string ownerId, string personaId)
    {
        var persona = personas.Get(personaId, ownerId);
        if (persona is null) return null;

        var character = persona.Contract?.Character ?? persona.SystemPrompt;
        var mustDo = persona.Contract?.MustDo is { Count: > 0 } m
            ? m.Where(x => !string.IsNullOrWhiteSpace(x)).ToList()
            : null;
        var skillTargets = persona.Bindings?
            .Where(b => b.Type == PersonaBindingType.Skill)
            .Select(b => b.Target)
            .ToList() ?? [];

        return new PersonaForSkillSuggestion(
            Name: persona.Name,
            Role: persona.Role,
            Description: persona.Description,
            Character: character,
            MustDo: mustDo,
            SkillBindingTargets: skillTargets);
    }
}
