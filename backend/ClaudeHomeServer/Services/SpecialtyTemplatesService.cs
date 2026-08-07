using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Применение шаблона специальности к полям персоны.
// Правило одно на создание и обновление: при выборе специальности (создание)
// или её СМЕНЕ (обновление) неуказанные в запросе access/tools/disallowedTools
// подставляются из эффективного шаблона (SpecialtySettingsStore: per-owner →
// глобальный → дефолт кода). Явные поля запроса всегда побеждают. После
// подстановки поля правятся вручную и живут своей жизнью: источник правды —
// персона, жёсткого потолка у шаблона нет.
public class SpecialtyTemplatesService(SpecialtySettingsStore settings)
{
    // Полный набор возможностей персоны: передаём его вместо null-шаблона «все», чтобы
    // PersonaManager.Update (null = «не менять») честно перезаписал Tools на «без ограничений»
    public static readonly IReadOnlyList<string> AllToolKeys = ["tasks", "notes", "web"];

    public sealed record Applied(
        PersonaAccess? Access,
        List<string>? Tools,
        List<string>? DisallowedTools,
        bool TemplateApplied);

    // currentSpecialty: null — создание персоны; иначе — обновление (шаблон применяется
    // только при реальной смене значения).
    public Applied Apply(string ownerId, PersonaSpecialty newSpecialty,
        PersonaSpecialty? currentSpecialty,
        PersonaAccess? explicitAccess, List<string>? explicitTools, List<string>? explicitDisallowedTools)
    {
        var passthrough = new Applied(explicitAccess, explicitTools, explicitDisallowedTools, false);

        if (newSpecialty == PersonaSpecialty.None) return passthrough;
        if (currentSpecialty == newSpecialty) return passthrough; // та же специальность — не «смена»

        if (settings.EffectiveTemplate(ownerId, newSpecialty) is not { } template) return passthrough;

        // Tools шаблона null = «все» — разворачиваем в полный список: PersonaManager.Update
        // трактует null как «не менять», а полный список нормализует в «без ограничений»
        return new Applied(
            Access: explicitAccess ?? template.Access,
            Tools: explicitTools ?? template.Tools?.ToList() ?? AllToolKeys.ToList(),
            DisallowedTools: explicitDisallowedTools ?? template.DisallowedTools?.ToList(),
            TemplateApplied: true);
    }
}
