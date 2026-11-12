using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Разовая миграция прав персон к наследованию от специальности (разрез
// docs/research/ai-fabrika-workflow-cut-2026-11.md §2 «Миграция», шаг A1b). Чистая функция:
// персоны + эффективные шаблоны → план изменений + отчёт. Ничего не пишет и персон не меняет:
// план применяет вызывающий — PersonaManager при загрузке стора (под флагом и маркером
// Persona.InheritsAccessMigratedAt) или админская ручка сухого прогона (только отчёт).
//
// Соглашение «нет эффективного шаблона» (замечание M3 из R-A1a) собрано здесь одной точкой:
// · Specialty=None — осознанный шаблон кода (Full без ограничений), а не «нет шаблона»;
// · Specialty≠None, а EffectiveAccessTemplate вернул null (у специальности нет шаблона,
//   глобальный снят) — наследовать нечего: InheritsAccess=false, материализованные права
//   персоны остаются.
// EffectiveAccessTemplate отдаёт null для обоих случаев, поэтому ветка None проверяется ЯВНО до
// разбора null. Та же reading — у PersonaManager.Hydrate; Apply трактует null так же. Нигде
// «нет шаблона» не превращается в Full.
public static class PersonaSpecialtyMigration
{
    // Группа персоны в отчёте
    public enum Group
    {
        // Группа прав персоны совпала с эффективным шаблоном → InheritsAccess=true
        Inherits,
        // Своя группа (или None при не-Full) → InheritsAccess=false, override сохраняется
        Override,
        // Специальность ≠ None, эффективного шаблона нет → false, свои права остаются
        NoTemplate,
    }

    public sealed record Decision(
        string PersonaId,
        string Name,
        string OwnerId,
        PersonaSpecialty Specialty,
        Group Group,
        // Текущая группа прав стора — почему решение такое: в отчёте сухого прогона она и есть
        // предмет разговора («что станет наследуемым, что останется override»)
        PersonaAccess CurrentAccess,
        List<string>? CurrentTools,
        List<string>? CurrentDisallowed,
        // Что должно стать после миграции
        bool InheritsAccess,
        // Значение в сторе отличается — это правка, а не подтверждение
        bool IsChange,
        // Маркер уже стоит: повторная миграция эту персону не пересматривает
        bool AlreadyMigrated);

    public sealed record SpecialtyCells(string? Strong, string? Medium, string? Weak);

    // Ячейка модели персоны, равная эффективной ячейке специальности. По разрез §2 её следовало
    // бы очистить, но шаг A1b её НЕ чистит (вопрос 2 Андрею) — только показывает в отчёте.
    public sealed record ModelCellMatch(string PersonaId, string Name, string Cell, string Value);

    public sealed record Plan(IReadOnlyList<Decision> Decisions, IReadOnlyList<ModelCellMatch> ModelCells)
    {
        public int CountOf(Group group) => Decisions.Count(d => d.Group == group);
        public int CountChanges() => Decisions.Count(d => d.IsChange && !d.AlreadyMigrated);
        // Кого миграция запишет: все ещё не решённые персоны — маркер ставится и тем, чьё
        // значение уже верное (иначе следующий пуск пересчитал бы их заново)
        public IReadOnlyList<Decision> Pending => Decisions.Where(d => !d.AlreadyMigrated).ToList();
    }

    /// <summary>
    /// План по снимку персон. templateOf — эффективный шаблон прав специальности: единственная
    /// точка SpecialtyTemplatesService.EffectiveAccessTemplate (та же, что у Hydrate).
    /// cellsOf — ячейки моделей специальности (для отчёта о ячейках); null — отчёт без них.
    /// </summary>
    public static Plan Build(
        IEnumerable<Persona> personas,
        Func<string, PersonaSpecialty, SpecialtyTemplate?> templateOf,
        Func<string, PersonaSpecialty, SpecialtyCells?>? cellsOf = null)
    {
        var decisions = new List<Decision>();
        var cells = new List<ModelCellMatch>();

        // Шаблон (и ячейки) резолвятся ОДНОЖДЫ на ключ (владелец, специальность), а не на каждую
        // персону: план по одной специальности строится по одному снимку. Резолв на персону
        // собрал бы отчёт из двух моментов, если шаблон правят во время прогона
        var templates = new Dictionary<(string OwnerId, PersonaSpecialty Specialty), SpecialtyTemplate?>();
        var cellsByKey = new Dictionary<(string OwnerId, PersonaSpecialty Specialty), SpecialtyCells?>();

        SpecialtyTemplate? TemplateOf(string ownerId, PersonaSpecialty specialty)
        {
            var key = (ownerId, specialty);
            if (!templates.TryGetValue(key, out var template))
            {
                template = templateOf(ownerId, specialty);
                templates[key] = template;
            }
            return template;
        }

        SpecialtyCells? CellsOf(string ownerId, PersonaSpecialty specialty)
        {
            if (cellsOf is null) return null;
            var key = (ownerId, specialty);
            if (!cellsByKey.TryGetValue(key, out var value))
            {
                value = cellsOf(ownerId, specialty);
                cellsByKey[key] = value;
            }
            return value;
        }

        // Порядок отчёта детерминирован (имя, затем id): один и тот же стор даёт тот же отчёт
        foreach (var p in personas.OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal))
        {
            var (group, inherits) = Decide(p, TemplateOf(p.OwnerId, p.Specialty));
            decisions.Add(new Decision(p.Id, p.Name, p.OwnerId, p.Specialty, group,
                p.Access, p.Tools, p.DisallowedTools, inherits,
                IsChange: p.InheritsAccess != inherits, AlreadyMigrated: p.InheritsAccessMigratedAt is not null));

            if (p.Specialty != PersonaSpecialty.None && CellsOf(p.OwnerId, p.Specialty) is { } sc)
                MatchCells(p, sc, cells);
        }

        return new Plan(decisions, cells);
    }

    private static (Group Group, bool Inherits) Decide(Persona p, SpecialtyTemplate? template)
    {
        // None — не «нет шаблона», а осознанный дефолт кода: Full без ограничений. Наследование
        // ставим только если у персоны ровно он же: иначе её override живёт байт в байт.
        if (p.Specialty == PersonaSpecialty.None)
            return SameGroup(p, PersonaAccess.Full, null, null)
                ? (Group.Inherits, true)
                : (Group.Override, false);

        if (template is null) return (Group.NoTemplate, false);
        return SameGroup(p, template.Access, template.Tools, template.DisallowedTools)
            ? (Group.Inherits, true)
            : (Group.Override, false);
    }

    // Группа прав сравнивается нормализованной — тем же нормализатором, что пишет их в стор:
    // Tools null ≡ полный набор (NormalizeTools), DisallowedTools живёт только при Custom
    // (PersonaAccessPolicy.BuildExtraDisallowed), поэтому вне Custom не учитывается с обеих сторон.
    private static bool SameGroup(Persona p, PersonaAccess access,
        IReadOnlyList<string>? tools, IReadOnlyList<string>? disallowed)
    {
        if (p.Access != access) return false;
        if (!SequenceEqual(PersonaManager.NormalizeTools(p.Tools?.ToList()),
                PersonaManager.NormalizeTools(tools?.ToList()))) return false;
        var own = p.Access == PersonaAccess.Custom ? PersonaManager.NormalizeDisallowed(p.DisallowedTools) : null;
        var tpl = access == PersonaAccess.Custom ? PersonaManager.NormalizeDisallowed(disallowed?.ToList()) : null;
        return SequenceEqual(own, tpl);
    }

    private static bool SequenceEqual(List<string>? a, List<string>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.SequenceEqual(b, StringComparer.Ordinal);
    }

    // Сравнение ячейки дословное (после трима): пустая ячейка специальности ничего не покрывает,
    // пустая ячейка персоны не с чем сравнивать — очищать нечего.
    private static void MatchCells(Persona p, SpecialtyCells sc, List<ModelCellMatch> cells)
    {
        AddCell(p, cells, nameof(Persona.TierStrong), p.TierStrong, sc.Strong);
        AddCell(p, cells, nameof(Persona.TierMedium), p.TierMedium, sc.Medium);
        AddCell(p, cells, nameof(Persona.TierWeak), p.TierWeak, sc.Weak);
    }

    private static void AddCell(Persona p, List<ModelCellMatch> cells, string cell, string? own, string? template)
    {
        if (string.IsNullOrWhiteSpace(own) || string.IsNullOrWhiteSpace(template)) return;
        if (string.Equals(own.Trim(), template.Trim(), StringComparison.Ordinal))
            cells.Add(new ModelCellMatch(p.Id, p.Name, cell, own.Trim()));
    }
}
