using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Шаблон прав и инструментов персоны по специальности: что подставляется в поля
// Access/Tools/DisallowedTools при выборе специальности.
// После подстановки поля живут своей жизнью — источник правды у персоны, шаблоны
// ничего не ограничивают (жёсткого потолка нет).
// Tools: null — все возможности (tasks+notes+web), как у Persona.Tools=null.
// DisallowedTools имеет смысл только при Access == Custom.
//
// Чистый DTO, перенесён из Main (Services/SpecialtyCatalog.cs) в Core на этапе 5,
// волна 1 выноса Llm — `SpecialtySettingsStore.EffectiveTemplate` живёт в Llm и
// возвращает этот record наружу. Без переноса шов не нужен: SpecialtyCatalog.cs
// остаётся в Main (он тянет код в Llm, а не наоборот), а тип, который через границу
// ходит, едет с Llm в Core.
public sealed record SpecialtyTemplate(
    PersonaAccess Access,
    IReadOnlyList<string>? Tools,
    IReadOnlyList<string>? DisallowedTools);
