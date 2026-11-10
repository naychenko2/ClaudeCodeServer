using System.Globalization;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// R5 (ADR-039, решение 7): правило «страница спит». Статус вычисляется при чтении и никуда
// не пишется: StatusValues и файлы pages/ не тронуты — отклонено «dormant пятым значением
// status во frontmatter» (потребовало бы запись в pages/ от имени кода чтения). Исключение
// спящих из recall — R6, здесь только вычисление.
//
// Правило архива (ADR-039 §7): Window без сигнала чтения. Исключения — каждое достаточно
// одно: страница disputed (спор — дело человека, не архива); на странице правка человека;
// страница младше Window; учёт чтений начат меньше Window назад (базовой линии нет, всё
// выглядело бы непрочитанным). Дальше последний сигнал = max(последнее чтение, начало
// учёта): страницы без чтений судит начало учёта, до него учёта не было вовсе. Любой вид
// чтения, recall тоже, идёт с весом 1 наравне с сильными (ADR-039 «Открыто», п. 4 —
// упрощение до замера).
//
// Интервалы half-open: сигнал ровно Window назад — уже спит; страница ровно Window возрастом
// — уже не молода. «dormant» — только признак: удаления нет, любое чтение возвращает страницу.
public static class WikiDormancy
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    /// <summary>Спит ли эта страница по этим данным. fm — шапка страницы; humanEdited —
    /// признак правки человека (в DormantPages — тот же, по которому WikiProjections.
    /// PageAuthorship пишет авторство «human»); reads — запись страницы в сторе чтений
    /// (null — никто её не читал); trackingSince — начало учёта (Snapshot.Since, null —
    /// учёт не начат). Ничего не пишет и не трогает файлов.</summary>
    public static bool IsDormant(
        WikiFrontmatter fm,
        bool humanEdited,
        WikiReadsStore.PageReads? reads,
        DateTimeOffset? trackingSince,
        DateTimeOffset now)
    {
        if (fm.Status == WikiFrontmatter.StatusDisputed)
            return false;
        if (humanEdited)
            return false;
        if (trackingSince is null)
            return false;

        // «Страница младше Window» — возраст доказывает читаемая дата шапки. Даты нет или
        // она не читается — исключения по возрасту нет: страница, которая не может доказать
        // ни возраст, ни чтения, — ровно то, для чего архив. Разбор даты как у
        // WikiLintService.ParseUpdated; mtime-подстановки линтера здесь нет: правило
        // вычисляется по шапке и стору, а не по диску.
        if (TryUpdated(fm, out var updated) && now - updated < Window)
            return false;

        var last = trackingSince.Value;
        if (reads?.LastRead is { } lastRead && lastRead > last)
            last = lastRead;

        return now - last >= Window;
    }

    /// <summary>Ключи спящих страниц раздела (PageKey — путь под pages/, тот же синтаксис,
    /// что ключи стора R1). Читает только файлы одного раздела и данный снимок стора —
    /// не всю вики. Пустое множество: снимок невалиден (на испорченном сторе ничего не
    /// архивируется), ключ не является разделом, учёт не начат, раздела на диске нет —
    /// во всех этих случаях судить не по чему, и выдуманный архив хуже молчания.
    /// Неразобранные страницы и служебные проекции в обход не входят.</summary>
    public static IReadOnlySet<string> DormantPages(
        string root,
        string sectionKey,
        WikiReadsStore.Snapshot reads,
        DateTimeOffset now)
    {
        if (!reads.Valid)
            return EmptySet();
        var folder = WikiSections.Folder(sectionKey);
        if (folder is null)
            return EmptySet();
        if (reads.Since is not { } since)
            return EmptySet();

        var sectionDir = SafePath.JoinBelowRoot(root, "pages/" + folder);
        if (!Directory.Exists(sectionDir))
            return EmptySet();

        // Раздел читается один раз: страницы — для оценки, карточки из «Источники/» —
        // для признака авторства (тот же набор карточек и та же нормализация, чем
        // мерит WikiController.ReadAuthorship).
        var pages = new List<(string PageKey, WikiPage Page)>();
        var cardDrafts = new List<WikiGate.Draft>();
        try
        {
            foreach (var full in Directory.EnumerateFiles(sectionDir, "*.md", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(sectionDir, full).Replace('\\', '/');
                if (WikiPaths.IsProjection(rel))
                    continue; // _index/_log/_health — не страницы
                var key = folder + rel;
                string? text;
                try { text = WikiFileWriter.Read(root, "pages/" + key); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                if (text is null || WikiPage.Parse(text).Model is not { } page)
                    continue; // страницы нет или шапка не разобрана — судить не по чему
                pages.Add((key, page));
                if (rel.Split('/').SkipLast(1).Contains(WikiSourceCapture.SourceDirName))
                    cardDrafts.Add(new WikiGate.Draft(key, text));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return EmptySet();
        }

        var cards = WikiProjections.Cards(cardDrafts);
        var dormant = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, page) in pages)
        {
            // Признак правки человека — тот же, по которому PageAuthorship пишет авторство
            // «human»: все действующие сноски страницы ведут на карточки origin: human
            // («together» — смешанное авторство — этого признака не даёт). Карточка
            // источника исключением не снабжена: она страница под pages/ на общем правиле.
            var humanEdited = WikiProjections.PageAuthorship(
                page, t => cards.TryGetValue(WikiGate.Norm(t), out var c) ? c.Origin : null)
                == WikiProjections.AuthorshipHuman;
            reads.Pages.TryGetValue(key, out var pageReads);
            if (IsDormant(page.Frontmatter, humanEdited, pageReads, since, now))
                dormant.Add(key);
        }
        return dormant;
    }

    // updated из шапки: дата или null. Тот же разбор, что у WikiLintService.ParseUpdated:
    // строгий парсер проверяет форму поля, не календарь.
    private static bool TryUpdated(WikiFrontmatter fm, out DateTimeOffset updated) =>
        DateTimeOffset.TryParse(fm.Updated, CultureInfo.InvariantCulture, DateTimeStyles.None, out updated);

    private static HashSet<string> EmptySet() => new(StringComparer.Ordinal);
}
