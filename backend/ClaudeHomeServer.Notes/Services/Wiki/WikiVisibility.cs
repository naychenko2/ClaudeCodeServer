using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Spheres;

namespace ClaudeHomeServer.Services.Wiki;

// Единая точка правды видимости разделов (ADR-036, решение 2): ни recall, ни инструменты,
// ни Dify набор не собирают — берут его только отсюда. Чистые функции без I/O.
// Набор — правило сборки контекста для моделей, а не права людей: человек в UI видит всю вики.
public static class WikiVisibility
{
    /// <summary>
    /// Контекст вызова из хвоста маршрута (ADR-012): проект сессии и собеседник.
    /// null — контекста нет вовсе: видим только «common». Контекст с пустым проектом —
    /// чат вне проекта: видим «personal» и «common».
    /// </summary>
    public sealed record CallContext(string? ProjectId, Persona? Persona);

    /// <summary>
    /// Видимые разделы — строго таблица ADR-036 §2. Порядок выдачи: свой проект,
    /// соседние по сфере (по id), раздел сферы, common; для чата вне проекта — personal, common.
    /// Всё неопределённое — fail-closed: набор сужается, а не расширяется; сироты не входят никогда.
    /// </summary>
    public static IReadOnlyList<string> SectionsFor(
        string ownerId,
        CallContext? context,
        ISphereDirectory dir,
        IProjectManager projects)
    {
        // Нет контекста — только «common» (ADR-036 §2).
        if (context is null)
            return [WikiSections.Common];

        // Чат вне проекта: «personal» и «common»; разделы проектов не видны и по явному запросу —
        // нужное знание поднимается в «Общее» (решение 2).
        if (string.IsNullOrEmpty(context.ProjectId))
            return [WikiSections.Personal, WikiSections.Common];

        // Раздел удалённого или чужого проекта — сирота, не входит ни в один набор (решение 1).
        if (projects.GetById(context.ProjectId) is not { } ownProject || ownProject.OwnerId != ownerId)
            return [WikiSections.Common];

        // Fail-closed по сфере: флаг выключен, сфера удалена, проект вне сфер — всё это null
        // у SphereOf, и набор сужается до «свой проект + common» (решение 2).
        var sphereId = dir.SphereOf(ownerId, context.ProjectId);

        // Пересечение с зоной персоны (ADR-024): выпадают только разделы проектов вне зоны;
        // null зона (глобальная персона, чат без персоны) не сужает. «Личное» в проектном
        // ходе не видно и так — правило таблицы, не зоны.
        var zone = context.Persona is { } persona ? PersonaZone.ProjectIds(persona, dir) : null;

        var sections = new List<string>();
        if (zone is null || zone.Contains(context.ProjectId, StringComparer.Ordinal))
            sections.Add(WikiSections.ProjectsPrefix + context.ProjectId);

        if (sphereId is not null)
        {
            foreach (var otherId in dir.ProjectsOf(ownerId, sphereId).OrderBy(id => id, StringComparer.Ordinal))
            {
                if (otherId == context.ProjectId)
                    continue;
                if (zone is not null && !zone.Contains(otherId, StringComparer.Ordinal))
                    continue;
                // Сироты не входят никогда — даже если справочник сфер отстал от удаления проекта.
                if (projects.GetById(otherId) is not { } otherProject || otherProject.OwnerId != ownerId)
                    continue;
                sections.Add(WikiSections.ProjectsPrefix + otherId);
            }
            sections.Add(WikiSections.SpheresPrefix + sphereId);
        }

        sections.Add(WikiSections.Common);
        return sections;
    }

    /// <summary>Пост-фильтр хитов по набору (решение 6, гарантия поверх Dify): хит раздела вне
    /// набора выпадает; хит с неразобранным разделом — тоже (он не видим нигде).
    /// keyOf возвращает ключ раздела хита (для пути хита — WikiSections.Parse).</summary>
    public static IReadOnlyList<T> Filter<T>(IEnumerable<T> hits, IReadOnlyCollection<string> visible, Func<T, string?> keyOf)
    {
        var set = new HashSet<string>(visible, StringComparer.Ordinal);
        var kept = new List<T>();
        foreach (var hit in hits)
        {
            if (keyOf(hit) is { } key && set.Contains(key))
                kept.Add(hit);
        }
        return kept;
    }

    /// <summary>Ссылку [[…]] на раздел вне набора заменяем [скрыто]: заголовок страницы чужого
    /// раздела — тоже текст (решение 2). Ссылка с неразбираемым целью — [скрыто] тоже
    /// (fail-closed: такая страница не принадлежит ни одному разделу). Ссылка внутри набора
    /// остаётся как есть, с алиасом и якорем.</summary>
    public static string RedactLinks(string text, IReadOnlyCollection<string> visible)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        var set = new HashSet<string>(visible, StringComparer.Ordinal);
        return LinkPattern.Replace(text, match =>
            TargetKey(match.Groups[1].Value) is { } key && set.Contains(key)
                ? match.Value
                : "[скрыто]");
    }

    private static readonly Regex LinkPattern = new(@"\[\[([^\[\]]*)\]\]", RegexOptions.Compiled);

    // Цель wikilink: без алиаса («|…») и якоря раздела («#…»).
    private static string? TargetKey(string rawTarget) =>
        WikiSections.Parse(rawTarget.Split('|', '#')[0].Trim());
}
