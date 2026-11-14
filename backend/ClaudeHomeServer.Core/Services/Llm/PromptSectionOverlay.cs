namespace ClaudeHomeServer.Services.Llm;

// Одна функция наложения секций промпта (разрез 2 «ai-fabrika-workflow-2», §2.2 и Г6):
// одно правило для всех слоёв — «параметр задан — перекрывает, не задан — не затирает».
// База — состояние предыдущего слоя (состояния кода несут SectionSource.Code); source —
// откуда значения этого слоя (админ — Global, позже персона — Persona, P1c).
//
// Наследование ПОПАРАМЕТНОЕ (не «замена записи целиком»): Enabled и Text каждой секции
// наследуются каждый своим параметром, поэтому настройка одной секции не сносит базовые
// тексты соседних. Та же семантика, что была в инлайновом цикле SpecialtySettingsStore
// (перенесена сюда без изменения поведения, P1a).
//
// Чистая функция: без состояния, без I/O, без блокировок — вызывающий читает свою базу
// под своим снимком (SpecialtySettingsStore — volatile _file) и отдаёт результат.
public static class PromptSectionOverlay
{
    public static IReadOnlyList<EffectivePromptSection> Apply(
        IReadOnlyList<EffectivePromptSection> baseStates,
        IReadOnlyList<PromptSectionOverride>? overrides,
        SectionSource source)
    {
        if (overrides is not { Count: > 0 }) return baseStates;

        var result = new List<EffectivePromptSection>(baseStates.Count);
        foreach (var s in baseStates)
        {
            // Поиск идёт по базе: порядок секций — порядок базы, переопределение с id,
            // которого в базе нет, игнорируется; первое совпадение id выигрывает
            // (как прежний FirstOrDefault в сторе).
            var o = overrides.FirstOrDefault(o =>
                string.Equals(o.Id, s.Id, StringComparison.OrdinalIgnoreCase));
            if (o is null)
            {
                result.Add(s);
                continue;
            }
            var hasText = !string.IsNullOrWhiteSpace(o.Text);
            result.Add(new EffectivePromptSection(
                s.Id,
                o.Enabled ?? s.Enabled,
                hasText ? o.Text!.Trim() : s.Text,
                o.Enabled is null ? s.EnabledSource : source,
                hasText ? source : s.TextSource));
        }
        return result;
    }
}
