using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Реализация IPersonaVoiceLookup: тонкая обёртка над PersonaManager, достаёт ровно
// голос — поле, нужное VoiceResolver. Полная `Persona` через шов не утекает, вертикаль
// Tts видит только `PersonaVoice`.
//
// Аналогично `PersonaSkillBindingLookup` (адаптер `IPersonaSkillBindingLookup`).
public sealed class PersonaVoiceLookup(PersonaManager personas) : IPersonaVoiceLookup
{
    public PersonaVoice? GetVoice(string ownerId, string personaId)
    {
        var persona = personas.Get(personaId, ownerId);
        return persona?.Voice;
    }
}
