using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Разовая миграция персон к наследованию от специальности (разрез
// docs/research/ai-fabrika-workflow-cut-2026-11.md §2 «Миграция», шаг A1b — права;
// шаг A2 — удаление Skill-копий типовых умений, теперь наследуемых в рантайме). Чистая функция:
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
        bool AlreadyMigrated,
        // A2: Skill-привязки, совпавшие с профилем специальности по SkillName (поимённо —
        // строки отчёта сухого прогона). Пусто для уже решённых маркером: план описывает
        // то, что следующий прогон сделает, — а он их не тронет
        IReadOnlyList<RemovedSkillBinding> RemovedSkills);

    public sealed record SpecialtyCells(string? Strong, string? Medium, string? Weak);

    // Ячейка модели персоны, равная эффективной ячейке специальности. По разрез §2 её следовало
    // бы очистить, но шаг A1b её НЕ чистит (вопрос 2 Андрею) — только показывает в отчёте.
    public sealed record ModelCellMatch(string PersonaId, string Name, string Cell, string Value);

    // A2 (разрез §2 «Миграция», п.4): Skill-привязка персоны, совпавшая с профилем специальности
    // по SkillName, — копия, которая больше не нужна: умение наследуется в рантайме
    // (PersonaBindingsService.EffectiveBindings). Удаляется при применении плана (тот же маркер
    // и флаг, что у прав); в сухом прогоне — только строка отчёта. Несовпавшие и AI-привязки
    // не трогаются.
    public sealed record RemovedSkillBinding(string BindingId, string Target);

    public sealed record Plan(IReadOnlyList<Decision> Decisions, IReadOnlyList<ModelCellMatch> ModelCells)
    {
        public int CountOf(Group group) => Decisions.Count(d => d.Group == group);
        public int CountChanges() => Decisions.Count(d => d.IsChange && !d.AlreadyMigrated);
        public int CountRemovedSkills() => Decisions.Sum(d => d.RemovedSkills.Count);
        // Кого миграция запишет: все ещё не решённые персоны — маркер ставится и тем, чьё
        // значение уже верное (иначе следующий пуск пересчитал бы их заново)
        public IReadOnlyList<Decision> Pending => Decisions.Where(d => !d.AlreadyMigrated).ToList();
    }

    /// <summary>
    /// План по снимку персон. templateOf — эффективный шаблон прав специальности: единственная
    /// точка SpecialtyTemplatesService.EffectiveAccessTemplate (та же, что у Hydrate).
    /// cellsOf — ячейки моделей специальности (для отчёта о ячейках); null — отчёт без них.
    /// defaultBindingsOf — типовой профиль умений специальности (та же точка EffectiveDefaultBindings,
    /// что у рантайма): по нему находятся Skill-копии для удаления (A2); null — шаг Skill не выполняется.
    /// </summary>
    public static Plan Build(
        IEnumerable<Persona> personas,
        Func<string, PersonaSpecialty, SpecialtyTemplate?> templateOf,
        Func<string, PersonaSpecialty, SpecialtyCells?>? cellsOf = null,
        Func<string, PersonaSpecialty, IReadOnlyList<SpecialtyDefaultBinding>>? defaultBindingsOf = null)
    {
        var decisions = new List<Decision>();
        var cells = new List<ModelCellMatch>();

        // Шаблон, ячейки и профиль умений резолвятся ОДНОЖДЫ на ключ (владелец, специальность),
        // а не на каждую персону: план по одной специальности строится по одному снимку. Резолв
        // на персону собрал бы отчёт из двух моментов, если профиль правят во время прогона
        var templates = new Dictionary<(string OwnerId, PersonaSpecialty Specialty), SpecialtyTemplate?>();
        var cellsByKey = new Dictionary<(string OwnerId, PersonaSpecialty Specialty), SpecialtyCells?>();
        var profileByKey = new Dictionary<(string OwnerId, PersonaSpecialty Specialty),
            IReadOnlyList<SpecialtyDefaultBinding>>();

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

        IReadOnlyList<SpecialtyDefaultBinding> ProfileOf(string ownerId, PersonaSpecialty specialty)
        {
            if (defaultBindingsOf is null) return [];
            var key = (ownerId, specialty);
            if (!profileByKey.TryGetValue(key, out var profile))
            {
                profile = defaultBindingsOf(ownerId, specialty);
                profileByKey[key] = profile;
            }
            return profile;
        }

        // Порядок отчёта детерминирован (имя, затем id): один и тот же стор даёт тот же отчёт
        foreach (var p in personas.OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal))
        {
            var (group, inherits) = Decide(p, TemplateOf(p.OwnerId, p.Specialty));
            var already = p.InheritsAccessMigratedAt is not null;
            decisions.Add(new Decision(p.Id, p.Name, p.OwnerId, p.Specialty, group,
                p.Access, p.Tools, p.DisallowedTools, inherits,
                IsChange: p.InheritsAccess != inherits, AlreadyMigrated: already,
                // План описывает то, что следующий прогон сделает: решённую маркером персону
                // он не тронет — и строк удаления у неё нет
                RemovedSkills: already ? [] : MatchSkillCopies(p, ProfileOf(p.OwnerId, p.Specialty))));

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

    // A2 (разрез §2 «Миграция», п.4): Skill-привязки, совпавшие с профилем специальности по
    // SkillName, — поимённо. Совпадение без учёта регистра (то же сравнение целей, что
    // ValidateAsync/EffectiveBindings); Mode не учитывается: по имени это копия унаследованного
    // умения, её подавление фронт создаёт заново (A3b). Несовпавшие (чужой SkillName, плагин-
    // цель профиля против голового имени персоны) остаются; не-Skill привязки не трогаются.
    // Битый SkillName профиля (без имени) не с чем сравнить — он ничего не удаляет.
    private static List<RemovedSkillBinding> MatchSkillCopies(
        Persona p, IReadOnlyList<SpecialtyDefaultBinding> profile)
    {
        if (p.Bindings is not { Count: > 0 } || profile.Count == 0) return [];
        var names = profile
            .Where(e => e.Type == PersonaBindingType.Skill)
            .Select(e => e.SkillName?.Trim())
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();
        if (names.Count == 0) return [];
        return p.Bindings
            .Where(b => b.Type == PersonaBindingType.Skill
                && names.Any(n => string.Equals(b.Target.Trim(), n, StringComparison.OrdinalIgnoreCase)))
            .Select(b => new RemovedSkillBinding(b.Id, b.Target))
            .ToList();
    }
}
