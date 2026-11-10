using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Рендер пакета автора human в состояние раздела (ADR-038, решения 2–3; шаг B3b). Чистая
// функция без диска: на вход — состояние страниц и пакет, на выход — новые тексты страницы и
// карточки правки. Операции пакета — результат WikiHumanEditPlanner (create, append, edit,
// supersede «снято человеком», notes, header); действие restore — обмен местами: формулировка
// из «Истории» возвращается на место действующей, действующая уходит в «Историю».
//
// Правила, на которых держится цитатный контракт:
//  • каждое написанное человеком утверждение получает сноску на карточку правки; цитата —
//    сам текст утверждения (карточка хранит хэш снимка, текст снимка — тексты операций пакета);
//  • прежняя формулировка не теряется: при edit и «снято человеком» она дословно, вместе со
//    своими сносками, уходит в «Историю»; из «Истории» ничего не убирается (физического
//    удаления нет). У страницы без «Истории» (хроника) правка меняет строку на месте, а убрать
//    утверждение нельзя — отказ;
//  • restore новой сноски и новой карточки не создаёт: возвращённая строка несёт прежнюю
//    сноску на исходную карточку human.
// Ошибка операции — Result.Error с причиной для человека; исключений наружу нет.
internal static class WikiHumanRender
{
    private const string HistorySection = "История";
    private static readonly Regex FootnoteRefRx = new(@"\[\^s(\d+)\]", RegexOptions.CultureInvariant);

    /// <summary>Drafts — изменённые страницы в порядке записи (карточка правки — первой; её нет,
    /// пока человек не написал ни одного утверждения). Stale — операция опирается на то, чего в
    /// состоянии больше нет (страница, замещаемая строка): правка устарела.</summary>
    internal sealed record Result(IReadOnlyList<WikiGate.Draft> Drafts, string? Error, bool Stale)
    {
        public static Result Fail(string error, bool stale = false) => new([], error, stale);
    }

    private sealed class Sec(string name, List<string> lines)
    {
        public string Name { get; } = name;
        public List<string> Lines { get; } = lines;
    }

    /// <summary>Заголовок карточки правки: по id пакета — он уникален, повторный рендер того же
    /// пакета даёт то же имя.</summary>
    internal static string CardTitle(WikiPackage package) => "Правка человека " + package.Id[..8];

    /// <summary>ref карточки правки: ссылка на пакет, написавший её.</summary>
    internal static string CardRef(WikiPackage package) => WikiGate.OriginHuman + ":" + package.Id;

    internal static string CardPath(WikiPackage package) =>
        WikiSections.Folder(package.Section)! + WikiSourceCapture.SourceDirName + "/" + CardTitle(package) + ".md";

    /// <summary>Снимок написанного человеком: тексты утверждений и секции «Заметки». Пустой —
    /// карточки нет (убрали утверждение, поправили шапку, вернули свою формулировку).</summary>
    internal static string Snapshot(WikiPackage package) =>
        package.Action == WikiPackage.ActionRestore
            ? ""
            : string.Join('\n', package.Operations
                .Where(o => o.Op is WikiPackage.OpAppend or WikiPackage.OpEdit or WikiPackage.OpNotes)
                .Select(o => o.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t)));

    internal static Result Render(IReadOnlyList<WikiGate.Draft> state, WikiPackage package)
    {
        try
        {
            return RenderCore(state, package);
        }
        catch (Exception)
        {
            return Result.Fail("внутренняя ошибка рендера правки человека");
        }
    }

    /// <summary>Карточка, на которую ссылается формулировка в «Истории»: её ref для пакета restore.
    /// Только карточка origin: human — вернуть можно лишь то, что написал человек.</summary>
    internal static (string? CardRef, string? Error) ResolveRestoreCard(
        IReadOnlyList<WikiGate.Draft> state, WikiPage page, string restoreText)
    {
        var history = page.Sections.FirstOrDefault(s => s.Name == HistorySection);
        var line = history?.Lines.FirstOrDefault(l => WikiHumanEditPlanner.Normalize(l) == restoreText);
        if (line is null)
            return (null, "в «Истории» нет такой формулировки");
        var refs = FootnoteRefRx.Matches(line);
        if (refs.Count == 0 || !int.TryParse(refs[^1].Groups[1].Value, out var k) || k < 1 || k > page.Footnotes.Count)
            return (null, "у формулировки в «Истории» нет сноски на карточку");
        var title = WikiGate.Norm(page.Footnotes[k - 1].Target.Text);
        foreach (var draft in state)
        {
            var card = WikiPage.Parse(draft.Content).Model;
            if (card is null || card.Frontmatter.Type != WikiFrontmatter.TypeSource || WikiGate.Norm(card.Title) != title)
                continue;
            return card.Frontmatter.Origin == WikiGate.OriginHuman && card.Frontmatter.Ref is { Length: > 0 } cardRef
                ? (cardRef, null)
                : (null, "вернуть можно только формулировку человека: эта сноска ведёт не на карточку правки");
        }
        return (null, "карточка источника этой формулировки не найдена");
    }

    private static Result RenderCore(IReadOnlyList<WikiGate.Draft> state, WikiPackage package)
    {
        var pages = package.Operations.Select(o => o.Page).Distinct(StringComparer.Ordinal).ToList();
        if (pages.Count != 1)
            return Result.Fail("пакет human правит одну страницу");
        var path = pages[0];
        WikiGate.Draft? existing = state.Any(d => d.Path == path) ? state.First(d => d.Path == path) : null;

        WikiPage model;
        var ops = (IReadOnlyList<WikiPackage.Operation>)package.Operations;
        if (package.Action == WikiPackage.ActionRestore)
        {
            if (existing is null)
                return Result.Fail($"страницы «{path}» больше нет: правка опиралась на неё", stale: true);
            var parsedRestore = WikiPage.Parse(existing.Value.Content).Model;
            if (parsedRestore is null)
                return Result.Fail($"страница «{path}» не разбирается — править неразобранную нельзя");
            return RenderRestore(state, path, parsedRestore, package.Operations[0]);
        }
        if (ops[0].Op == WikiPackage.OpCreate)
        {
            if (existing is not null)
                return Result.Fail($"страница «{path}» уже есть: создавать нечего", stale: true);
            var created = NewPage(path, ops[0]);
            if (created.Error is not null)
                return Result.Fail(created.Error);
            model = created.Page!;
            ops = [.. ops.Skip(1)];
        }
        else
        {
            if (existing is null)
                return Result.Fail($"страницы «{path}» больше нет: правка опиралась на неё", stale: true);
            var parsed = WikiPage.Parse(existing.Value.Content).Model;
            if (parsed is null)
                return Result.Fail($"страница «{path}» не разбирается — править неразобранную нельзя");
            model = parsed;
        }

        var cardTitle = CardTitle(package);
        var sections = model.Sections.Select(s => new Sec(s.Name, s.Lines.ToList())).ToList();
        var footnotes = model.Footnotes.ToList();
        var fm = model.Frontmatter;

        foreach (var op in ops)
        {
            var failure = ApplyOp(op, sections, footnotes, ref fm, cardTitle);
            if (failure is not null)
                return failure;
        }

        var page = new WikiPage
        {
            Title = model.Title,
            Frontmatter = fm,
            Sections = sections.Select(s => new WikiPage.Section { Name = s.Name, Lines = s.Lines }).ToList(),
            Footnotes = footnotes,
        };
        var drafts = new List<WikiGate.Draft>();
        var snapshot = Snapshot(package);
        if (snapshot.Length > 0)
            drafts.Add(new WikiGate.Draft(CardPath(package), Card(package, snapshot)));
        drafts.Add(new WikiGate.Draft(path, page.Render()));
        return new Result(drafts, null, false);
    }

    // Операция над разобранной страницей; null — применена, иначе — отказ.
    private static Result? ApplyOp(
        WikiPackage.Operation op, List<Sec> sections, List<WikiPage.WikiFootnote> footnotes,
        ref WikiFrontmatter fm, string cardTitle)
    {
        if (op.Op == WikiPackage.OpHeader)
        {
            var header = SetHeader(fm, op.Target ?? "", op.Text ?? "");
            if (header is null)
                return Result.Fail($"поле шапки «{op.Target}» человек не правит");
            fm = header;
            return null;
        }
        if (op.Op == WikiPackage.OpNotes)
        {
            SetNotes(sections, op.Text ?? "");
            return null;
        }
        if (op.Op is not (WikiPackage.OpAppend or WikiPackage.OpEdit or WikiPackage.OpSupersede))
            return Result.Fail($"операция «{op.Op}» человеку недоступна");

        var section = sections.FirstOrDefault(s => s.Name == op.PageSection);
        if (section is null || section.Name == WikiPage.NotesSectionName)
            return Result.Fail($"на странице нет секции «{op.PageSection}» для утверждений");
        var history = sections.FirstOrDefault(s => s.Name == HistorySection);

        if (op.Op == WikiPackage.OpAppend)
        {
            section.Lines.Add(WithFootnote(op.Text!, footnotes, cardTitle));
            return null;
        }

        var index = section.Lines.FindIndex(l => WikiHumanEditPlanner.Normalize(l) == op.Target);
        if (index < 0)
            return Result.Fail($"утверждения «{op.Target}» в секции «{section.Name}» больше нет", stale: true);
        var old = section.Lines[index];

        if (op.Op == WikiPackage.OpEdit)
        {
            section.Lines[index] = WithFootnote(op.Text!, footnotes, cardTitle);
            // Прежняя формулировка — дословно, со своими сносками, в «Историю»; в самой «Истории»
            // и на странице без неё правка идёт на месте.
            if (history is not null && section != history)
                history.Lines.Add(old);
            return null;
        }

        // supersede «снято человеком»: из «Истории» не убирают, а страницу без неё не прячут.
        if (section == history)
            return Result.Fail("утверждения «Истории» не убираются: физического удаления нет");
        if (history is null)
            return Result.Fail("у страницы нет секции «История»: убрать утверждение некуда");
        section.Lines.RemoveAt(index);
        history.Lines.Add(old);
        return null;
    }

    // «Вернуть мою»: формулировка из «Истории» встаёт на место действующей строки, действующая
    // уходит в «Историю». Сноски те же — ни новой сноски, ни новой карточки.
    private static Result RenderRestore(
        IReadOnlyList<WikiGate.Draft> state, string path, WikiPage model, WikiPackage.Operation op)
    {
        var (_, error) = ResolveRestoreCard(state, model, op.Text ?? "");
        if (error is not null)
            return Result.Fail(error);

        var sections = model.Sections.Select(s => new Sec(s.Name, s.Lines.ToList())).ToList();
        var history = sections.First(s => s.Name == HistorySection);
        var restoreIndex = history.Lines.FindIndex(l => WikiHumanEditPlanner.Normalize(l) == op.Text);

        var active = sections
            .Where(s => s.Name != HistorySection && s.Name != WikiPage.NotesSectionName
                && WikiPage.AssertionSectionNames.Contains(s.Name))
            .Select(s => (Section: s, Index: s.Lines.FindIndex(l => WikiHumanEditPlanner.Normalize(l) == op.Target)))
            .FirstOrDefault(x => x.Index >= 0);
        if (active.Section is null)
            return Result.Fail($"действующего утверждения «{op.Target}» на странице больше нет", stale: true);

        var restored = history.Lines[restoreIndex];
        var replaced = active.Section.Lines[active.Index];
        active.Section.Lines[active.Index] = restored;
        history.Lines.RemoveAt(restoreIndex);
        history.Lines.Add(replaced);

        var page = new WikiPage
        {
            Title = model.Title,
            Frontmatter = model.Frontmatter,
            Sections = sections.Select(s => new WikiPage.Section { Name = s.Name, Lines = s.Lines }).ToList(),
            Footnotes = model.Footnotes,
        };
        return new Result([new WikiGate.Draft(path, page.Render())], null, false);
    }

    private static string WithFootnote(string text, List<WikiPage.WikiFootnote> footnotes, string cardTitle)
    {
        footnotes.Add(new WikiPage.WikiFootnote(
            new WikiPage.Target(true, cardTitle),
            text.Length <= WikiPage.MaxQuoteLength ? text : text[..WikiPage.MaxQuoteLength]));
        return $"{text} [^s{footnotes.Count}]";
    }

    private static (WikiPage? Page, string? Error) NewPage(string path, WikiPackage.Operation op)
    {
        var title = op.Text ?? "";
        var type = op.Target ?? WikiFrontmatter.TypeTopic;
        if (!WikiFrontmatter.TypeValues.Contains(type) || type == WikiFrontmatter.TypeSource)
            return (null, $"тип страницы «{type}» человек не заводит");
        var file = path.Split('/')[^1];
        if (!file.EndsWith(".md", StringComparison.Ordinal) || file[..^3] != title)
            return (null, $"заголовок страницы «{title}» должен совпадать с именем файла «{file}»");
        var names = type == WikiFrontmatter.TypeChronicle ? WikiPage.ChronicleSectionNames : WikiPage.SectionNames;
        return (new WikiPage
        {
            Title = title,
            Frontmatter = new WikiFrontmatter { Type = type },
            Sections = names.Select(n => new WikiPage.Section { Name = n, Lines = new List<string>() }).ToList(),
        }, null);
    }

    private static string Card(WikiPackage package, string snapshot) =>
        new WikiFrontmatter
        {
            Type = WikiFrontmatter.TypeSource,
            Origin = WikiGate.OriginHuman,
            Ref = CardRef(package),
            Hash = WikiManifest.Sha256Hex(snapshot),
            Captured = package.CreatedAt.ToString("O"),
        }.Render() + "\n\n# " + CardTitle(package) + "\n";

    private static WikiFrontmatter? SetHeader(WikiFrontmatter fm, string field, string value)
    {
        static IReadOnlyList<string> List(string v) =>
            v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var summary = fm.Summary;
        var aliases = fm.Aliases;
        var owns = fm.Owns;
        switch (field)
        {
            case "summary": summary = value.Length == 0 ? null : value; break;
            case "aliases": aliases = List(value); break;
            case "owns": owns = List(value); break;
            default: return null;
        }
        // as_of и written — поля кода: правка summary/aliases/owns их не стирает (S2-1h).
        return new WikiFrontmatter
        {
            Type = fm.Type, Kind = fm.Kind, Summary = summary, AsOf = fm.AsOf, Aliases = aliases, Owns = owns,
            Sources = fm.Sources, Updated = fm.Updated, Status = fm.Status,
            Origin = fm.Origin, Ref = fm.Ref, Hash = fm.Hash, Captured = fm.Captured, Signal = fm.Signal,
            Written = fm.Written,
        };
    }

    // «Заметки» — последняя секция; пустой текст убирает её (человек стёр секцию целиком).
    private static void SetNotes(List<Sec> sections, string text)
    {
        sections.RemoveAll(s => s.Name == WikiPage.NotesSectionName);
        var lines = text.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count > 0)
            sections.Add(new Sec(WikiPage.NotesSectionName, lines));
    }
}
