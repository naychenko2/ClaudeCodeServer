using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Шов «голос персоны по id»: VoiceResolver больше не держит конкретный PersonaManager
// и не тащит полную модель `Persona` (с её Contract/Avatar/Bindings/...) — seam отдаёт
// ровно то, что нужно VoiceResolver: голос владельца, и ничего больше.
//
// Контракт повторяет форму `PersonaManager.Get(id, userId)` (Persona + проверка
// владельца), но возвращает сразу голос (Persona.Voice), а не полную персону.
// null — либо персоны нет, либо голос не задан; VoiceResolver трактует оба одинаково
// (дефолт инстанса), и отдельное ветвление на «не нашли» ему не нужно.
//
// Реализация — тонкая обёртка в Main (адаптер над PersonaManager), контракт живёт
// в Core. Это та же форма, что у `IPersonaSkillBindingLookup`/`IProjectSummaryLookup`.
public interface IPersonaVoiceLookup
{
    PersonaVoice? GetVoice(string ownerId, string personaId);
}
