using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Llm.Claude;

// Разбор CHANGELOG.md claude-code для сторожа обновлений CLI: пункты пропущенных версий
// (current, latest] и новые модели. Формат файла — не контракт Anthropic, поэтому разбор
// терпимый: что не распознано, просто не попадает в дайджест, исключений нет.
//
// Модель выделяется только по строгой формуле, которой объявлены последние релизы
// («Added Claude Sonnet 5.5 (`claude-sonnet-5-5`), now the default …»): ложное «вышла
// модель» хуже пропуска, а список изменений покажет пункт в любом случае.
public static class ClaudeCliChangelog
{
    public const string SourceUrl = "https://raw.githubusercontent.com/anthropics/claude-code/main/CHANGELOG.md";

    // Потолки дайджеста: он лежит в state-файле и уходит в каждый ответ эндпоинта
    internal const int MaxVersions = 15;
    internal const int MaxItems = 400;
    internal const int MaxItemLength = 600;

    // IsFamilyDefault — самая новая модель своего семейства среди пропущенных: алиас
    // семейства после `claude update` пойдёт на неё
    public sealed record NewModel(string Name, string Id, string CliVersion, bool IsFamilyDefault);

    // Hidden — сколько пунктов этой версии скрыто как относящиеся к другим продуктам
    public sealed record VersionChanges(string Version, IReadOnlyList<string> Items, int Hidden);

    public sealed record Digest(IReadOnlyList<VersionChanges> Versions, IReadOnlyList<NewModel> NewModels, bool Truncated)
    {
        public int ItemCount => Versions.Sum(v => v.Items.Count);
        public int HiddenCount => Versions.Sum(v => v.Hidden);
    }

    // Пункты других продуктов (метка — префикс пункта): серверу CCS не относятся
    private static readonly Regex HiddenPrefix = new(
        @"^\[(VSCode|Claude Tag|Claude Code on the web|Code Review|Cloud sessions|IDE)\]\s",
        RegexOptions.CultureInvariant);

    private static readonly Regex Heading = new(@"^##\s+(\d+\.\d+\.\d+)\s*$", RegexOptions.CultureInvariant);

    private static readonly Regex ModelAnnouncement = new(
        @"^Added Claude (?<name>[A-Z][a-z]+ \d+(?:\.\d+)?) \(`(?<id>claude-[a-z0-9-]+)`\)",
        RegexOptions.CultureInvariant);

    // Дайджест версий current < v ≤ latest (новые сверху); HasLatest — нашлась ли секция latest
    // (CHANGELOG на main может отставать от npm)
    public static (Digest Digest, bool HasLatest) Parse(string? markdown, string current, string latest)
    {
        var empty = new Digest([], [], false);
        if (string.IsNullOrWhiteSpace(markdown)
            || !Version.TryParse(current, out var cur) || !Version.TryParse(latest, out var lat))
            return (empty, false);

        var sections = new List<(Version V, string Raw, List<string> Items)>();
        (Version V, string Raw, List<string> Items)? section = null;
        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var h = Heading.Match(line);
            if (h.Success)
            {
                section = Version.TryParse(h.Groups[1].Value, out var v) && v > cur && v <= lat
                    ? (v, h.Groups[1].Value, new List<string>())
                    : null;
                if (section is not null) sections.Add(section.Value);
                continue;
            }
            // Прочие заголовки («# Changelog») секцию закрывают
            if (line.StartsWith('#')) { section = null; continue; }
            if (section is null) continue;

            if (line.StartsWith("- "))
                section.Value.Items.Add(line[2..].Trim());
            else if (!string.IsNullOrWhiteSpace(line) && section.Value.Items.Count > 0)
            {
                // Перенос строки внутри пункта — приклеиваем к предыдущему
                var items = section.Value.Items;
                items[^1] = items[^1] + " " + line.Trim();
            }
        }

        var hasLatest = sections.Any(s => s.V == lat);
        var ordered = sections.OrderByDescending(s => s.V).ToList();

        var versions = new List<VersionChanges>();
        var models = new List<NewModel>();
        var total = 0;
        var truncated = false;
        foreach (var s in ordered)
        {
            var visible = new List<string>();
            var hidden = 0;
            foreach (var item in s.Items)
            {
                if (HiddenPrefix.IsMatch(item)) { hidden++; continue; }
                var m = ModelAnnouncement.Match(item);
                if (m.Success)
                    models.Add(new NewModel(m.Groups["name"].Value, m.Groups["id"].Value, s.Raw, false));
                visible.Add(item.Length > MaxItemLength ? item[..MaxItemLength] + "…" : item);
            }
            // Старые версии отрезаются целиком: наполовину показанная версия путала бы
            if (versions.Count >= MaxVersions || (versions.Count > 0 && total + visible.Count > MaxItems))
            {
                truncated = true;
                break;
            }
            versions.Add(new VersionChanges(s.Raw, visible, hidden));
            total += visible.Count;
        }

        // Модели из отрезанных версий не теряем: выделение моделей важнее списка
        return (new Digest(versions, MarkFamilyDefaults(models), truncated), hasLatest);
    }

    // Оставить только версии и модели новее current; нечего показывать — null
    public static Digest? Since(Digest? digest, string? current)
    {
        if (digest is null || !Version.TryParse(current, out var cur)) return digest;
        var versions = digest.Versions.Where(v => Newer(v.Version, cur)).ToList();
        var models = digest.NewModels.Where(m => Newer(m.CliVersion, cur)).ToList();
        if (versions.Count == 0 && models.Count == 0) return null;
        return new Digest(versions, MarkFamilyDefaults(models), digest.Truncated);
    }

    private static bool Newer(string version, Version than) =>
        Version.TryParse(version, out var v) && v > than;

    // Самая новая модель каждого семейства (семейство — первое слово имени)
    private static IReadOnlyList<NewModel> MarkFamilyDefaults(List<NewModel> models)
    {
        var newest = models
            .GroupBy(m => m.Name.Split(' ')[0])
            .Select(g => g.MaxBy(m => ModelVersion(m.Name))!)
            .ToHashSet();
        return models.Select(m => m with { IsFamilyDefault = newest.Contains(m) }).ToList();
    }

    // «Opus 5» → 5.0, «Sonnet 5.5» → 5.5 (Version не парсит одиночное число)
    private static Version ModelVersion(string name)
    {
        var raw = name.Split(' ')[^1];
        return Version.TryParse(raw.Contains('.') ? raw : raw + ".0", out var v) ? v : new Version(0, 0);
    }
}
