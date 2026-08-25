namespace ClaudeHomeServer.Services;

// Слой настроек: шаблоны специальностей, «любая специальность» и пресеты-цепочки.
public class SpecialtySettingsLayer
{
    public Dictionary<string, SpecialtyTemplateSettings> Specialties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // «Любая специальность» (наследник правила "any" v1): применяется, когда у конкретной
    // специальности записи нет. Та же форма, что у записи специальности. Семантика слоёв
    // сохраняется: owner-слой DefaultSpecialty заменяет глобальный целиком.
    public SpecialtyTemplateSettings? DefaultSpecialty { get; set; }
    public List<ModelRoutePreset> Presets { get; set; } = [];

    public bool IsEmpty => Specialties.Count == 0 && Presets.Count == 0
        && DefaultSpecialty is null;
}
