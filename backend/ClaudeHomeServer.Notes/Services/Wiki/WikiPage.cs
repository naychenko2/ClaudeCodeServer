using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Модель страницы вики: шапка, заголовок, фиксированные секции, сноски (ADR-035, решение 3).
// Секции обычной страницы: Сводка → Хронология → Противоречия → История; у chronicle:
// «О чём», «Что решили», «Что выяснили», «Открытые вопросы» (ADR-035, решение 12б).
// Карточка источника (type: source) — данные, не рассказ: секций не имеет.
// Сноска: [^sN]: [[Карточка источника]] — «цитата»; диапазон session:{id}#m{a}-{b} — в ref карточки.
// Номера сносок перенумеровывает код: при разоре определения получают s1…sN по порядку
// в файле, ссылки в тексте переписываются к ним; рендер выдаёт канонические номера.
// Пустые строки не несут смысла и в модель не попадают. Чистые функции без I/O.
public sealed class WikiPage
{
    public static readonly IReadOnlyList<string> SectionNames =
        ["Сводка", "Хронология", "Противоречия", "История"];
    public static readonly IReadOnlyList<string> ChronicleSectionNames =
        ["О чём", "Что решили", "Что выяснили", "Открытые вопросы"];

    // Необязательная последняя секция «Заметки» (ADR-038, решение Андрея 1): свободный markdown
    // человека без сносок и цитат. Идёт после последней канонической секции и у обычной
    // страницы, и у chronicle. Пишет только пакет автора human (WikiGate); ИИ читает её
    // через карточку origin: human и не меняет. Утверждением не считается.
    public const string NotesSectionName = "Заметки";

    // Секции, чьё утверждение обязано заканчиваться сноской (ADR-035, решение 3:
    // утверждение — абзац или пункт «Сводки» и «Противоречий»; у хроники — решения и
    // находки, решение 12б). «Хронология» принимает утверждения по append (решение 12г),
    // «История» несёт перенесённые утверждения — сноска обязательна и там. «О чём» —
    // строка-описание, «Открытые вопросы» — вопросы.
    public static readonly IReadOnlyList<string> AssertionSectionNames =
        ["Сводка", "Хронология", "Противоречия", "История", "Что решили", "Что выяснили"];

    // Контракт цитаты: 8–300 символов (ADR-035, решение 3). Длину проверяет WikiGate.
    public const int MinQuoteLength = 8;
    public const int MaxQuoteLength = 300;

    private static readonly Regex TitleRx = new(@"^# (.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex SectionRx = new(@"^## (.+)$", RegexOptions.CultureInvariant);
    // Определение сноски: [^id]: <цель> — «цитата». Цель — только [[Карточка]].
    private static readonly Regex FootnoteDefRx = new(@"^\[\^([^]\r\n]+)\]: (.+?) — «(.*)»$", RegexOptions.CultureInvariant);
    private static readonly Regex FootnoteRefRx = new(@"\[\^([^]\r\n]+)\]", RegexOptions.CultureInvariant);
    private static readonly Regex LinkRx = new(@"\[\[([^]\r\n]+)\]\]", RegexOptions.CultureInvariant);
    private static readonly Regex SessionRefRx = new(@"^session:([A-Za-z0-9_-]+)#m([0-9]+)-([0-9]+)\z", RegexOptions.CultureInvariant);

    public required string Title { get; init; }
    public required WikiFrontmatter Frontmatter { get; init; }
    // Секции в фиксированном для типа порядке; ссылки сносок в строках уже канонические (s1…sN).
    public required IReadOnlyList<Section> Sections { get; init; }
    public IReadOnlyList<WikiFootnote> Footnotes { get; init; } = [];

    /// <summary>Секция «Заметки» (null — её на странице нет).</summary>
    public Section? Notes => Sections.FirstOrDefault(s => s.Name == NotesSectionName);

    public readonly record struct Result(WikiPage? Model, IReadOnlyList<string> Errors);

    /// <summary>Цель сноски или ссылки вики: ссылка [[…]] (IsLink) или закрытая ссылка
    /// источника (IsRef). Текст — как написан: без префикса «[[» для ссылки.</summary>
    public readonly record struct Target(bool IsLink, string Text);

    public readonly record struct WikiFootnote(Target Target, string Quote);

    public sealed class Section
    {
        public required string Name { get; init; }
        public required IReadOnlyList<string> Lines { get; init; }

        public bool Equals(Section? other) =>
            other is not null && Name == other.Name && Lines.SequenceEqual(other.Lines);
        public override bool Equals(object? obj) => Equals(obj as Section);
        public override int GetHashCode() => HashCode.Combine(Name, string.Join("\n", Lines));
    }

    /// <summary>Разбирает страницу целиком. Model — null при любой ошибке формата.</summary>
    public static Result Parse(string text)
    {
        var fm = WikiFrontmatter.Parse(text);
        if (fm.Model is null)
            return new Result(null, fm.Errors);

        var errors = new List<string>();
        var lines = WikiFrontmatter.SplitLines(text);
        var canonical = fm.Model.Type == WikiFrontmatter.TypeChronicle
            ? ChronicleSectionNames
            : fm.Model.Type == WikiFrontmatter.TypeSource
                ? []
                : SectionNames;

        string? title = null;
        // Секции в каноническом порядке, ещё не переписанные ссылки.
        var sections = new List<Section>();
        var openSection = -1;
        var definitions = new List<(string Id, Target Target, string Quote)>();
        var seenDefinitionIds = new HashSet<string>(StringComparer.Ordinal);

        for (var i = fm.CloseLineIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
                continue;

            var def = FootnoteDefRx.Match(line);
            if (def.Success)
            {
                var id = def.Groups[1].Value;
                if (!seenDefinitionIds.Add(id))
                {
                    errors.Add($"сноска '[^{id}]' определена дважды");
                    continue;
                }
                var target = ParseTarget(def.Groups[2].Value);
                if (target is null)
                {
                    errors.Add($"сноска '[^{id}]': цель не '[[Карточка]]' (прямая ссылка источника — только в ref карточки)");
                    continue;
                }
                definitions.Add((id, target.Value, def.Groups[3].Value));
                continue;
            }

            var titleMatch = TitleRx.Match(line);
            if (titleMatch.Success)
            {
                if (title is not null)
                {
                    errors.Add("на странице два заголовка '#'");
                    continue;
                }
                title = titleMatch.Groups[1].Value;
                continue;
            }

            var sectionMatch = SectionRx.Match(line);
            if (sectionMatch.Success)
            {
                var name = sectionMatch.Groups[1].Value;
                if (canonical.Count == 0)
                {
                    errors.Add("у карточки источника (type: source) не может быть секций");
                    continue;
                }
                var expectedIndex = sections.Count;
                // «Заметки» допустимы только сразу после полного канонического набора.
                var fits = expectedIndex < canonical.Count
                    ? name == canonical[expectedIndex]
                    : expectedIndex == canonical.Count && name == NotesSectionName;
                if (!fits)
                {
                    errors.Add($"секция '{name}' не в фиксированном порядке {string.Join(" → ", canonical)} [→ {NotesSectionName}]");
                    continue;
                }
                sections.Add(new Section { Name = name, Lines = new List<string>() });
                openSection = sections.Count - 1;
                continue;
            }

            // Обычная строка содержания.
            if (title is null)
            {
                errors.Add($"строка {i + 1}: текст до заголовка страницы");
                continue;
            }
            if (openSection < 0)
            {
                errors.Add($"строка {i + 1}: текст вне секций");
                continue;
            }
            ((List<string>)sections[openSection].Lines).Add(line);
        }

        if (title is null)
            errors.Add("у страницы нет заголовка '#'");
        for (var s = sections.Count; s < canonical.Count; s++)
            errors.Add($"нет секции '{canonical[s]}'");

        // Перенумерование сносок: s1…sN по порядку определений; ссылки в тексте — к ним.
        var remap = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var d = 0; d < definitions.Count; d++)
            remap[definitions[d].Id] = $"s{d + 1}";
        var canonicalSections = new List<Section>(sections.Count);
        foreach (var section in sections)
        {
            var rewritten = new List<string>(section.Lines.Count);
            foreach (var line in section.Lines)
            {
                var missing = false;
                var rewrittenLine = FootnoteRefRx.Replace(line, m =>
                {
                    if (!remap.TryGetValue(m.Groups[1].Value, out var canonicalId))
                    {
                        missing = true;
                        return m.Value;
                    }
                    return $"[^{canonicalId}]";
                });
                if (missing)
                    errors.Add($"ссылка на несуществующую сноску в строке: {line}");
                rewritten.Add(rewrittenLine);
            }
            canonicalSections.Add(new Section { Name = section.Name, Lines = rewritten });
        }

        if (errors.Count > 0)
            return new Result(null, errors);

        var footnotes = definitions
            .Select(d => new WikiFootnote(d.Target, d.Quote))
            .ToList();
        var model = new WikiPage
        {
            Title = title!,
            Frontmatter = fm.Model,
            Sections = canonicalSections,
            Footnotes = footnotes,
        };
        return new Result(model, []);
    }

    /// <summary>Рендерит страницу в каноническом виде: шапка, заголовок, секции
    /// (даже пустые — форма страницы фиксирована), сноски s1…sN.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append(Frontmatter.Render()).Append('\n');
        sb.Append("\n# ").Append(Title).Append('\n');
        foreach (var section in Sections)
        {
            sb.Append("\n## ").Append(section.Name).Append('\n');
            foreach (var line in section.Lines)
                sb.Append(line).Append('\n');
        }
        if (Footnotes.Count > 0)
        {
            sb.Append('\n');
            for (var d = 0; d < Footnotes.Count; d++)
            {
                var target = Footnotes[d].Target;
                var targetText = target.IsLink ? $"[[{target.Text}]]" : target.Text;
                sb.Append($"[^s{d + 1}]: {targetText} — «{Footnotes[d].Quote}»").Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>Ссылки страницы: все [[…]] из содержания и целей сносок — с маской
    /// «вне кода» (NotesService.CodeMask): ссылка внутри кода не ссылка (Obsidian).</summary>
    public IReadOnlyList<Target> OutboundLinks()
    {
        var result = new List<Target>();
        foreach (var section in Sections)
            foreach (var line in section.Lines)
                CollectLinks(line, result);
        foreach (var footnote in Footnotes)
            if (footnote.Target.IsLink)
                result.Add(footnote.Target);
        return result;
    }

    /// <summary>Разбирает сноску на чат: session:{id}#m{a}-{b} → (id, a, b).</summary>
    public static bool TryParseSessionRef(string text, out string sessionId, out int from, out int to)
    {
        sessionId = "";
        from = to = 0;
        var m = SessionRefRx.Match(text);
        if (!m.Success)
            return false;
        if (!int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var a) ||
            !int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var b) ||
            a < 1 || a > b)
            return false;
        sessionId = m.Groups[1].Value;
        from = a;
        to = b;
        return true;
    }

    private static Target? ParseTarget(string text)
    {
        if (text.StartsWith("[[") && text.EndsWith("]]"))
        {
            var inner = text[2..^2];
            return inner.Length > 0 ? new Target(true, inner) : null;
        }
        // Прямая ссылка на источник — не цель сноски: она живёт в ref карточки.
        return null;
    }

    private static void CollectLinks(string line, List<Target> result)
    {
        var mask = NotesService.CodeMask(line);
        foreach (Match m in LinkRx.Matches(line))
        {
            if (NotesService.InCode(mask, m.Index))
                continue;
            result.Add(new Target(true, m.Groups[1].Value));
        }
    }

    public bool Equals(WikiPage? other) =>
        other is not null
        && Title == other.Title
        && Frontmatter.Equals(other.Frontmatter)
        && Sections.Count == other.Sections.Count
        && Sections.Zip(other.Sections).All(p => p.First.Equals(p.Second))
        && Footnotes.SequenceEqual(other.Footnotes);

    public override bool Equals(object? obj) => Equals(obj as WikiPage);

    public override int GetHashCode() =>
        HashCode.Combine(Title, Frontmatter.GetHashCode(), Sections.Count, Footnotes.Count);
}
