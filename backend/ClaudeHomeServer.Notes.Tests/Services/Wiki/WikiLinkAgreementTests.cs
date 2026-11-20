using System.Text;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Сторож M2 (хвост 41): два резолва одной ссылки [[…]] — WikiGate.ResolveLink и закрытый резолв
// WikiRelated.Build — обязаны соглашаться на каждой форме ссылки. Автосвязыватель станет
// третьим потребителем этих правил; без сторожа расхождение молча даёт ссылку, которую гейт
// считает живой, а «Связанные» — нет (или наоборот).
//
// Инвариант: страница, в которую резолвит ResolveLink, — единственная страница, у которой
// страница-автор ссылки попала в группу backlinks; нерезолвящаяся ссылка не даёт backlinks
// нигде. Чистые функции, без диска.
public class WikiLinkAgreementTests
{
    private const string Linker = "Проекты/p1/Источник.md";
    private const string LinkerSection = "projects/p1";

    private static readonly (string Path, string Title, string[] Aliases)[] Pages =
    [
        ("Проекты/p1/Цель.md", "Цель", []),
        ("Проекты/p1/Сосед.md", "Сосед", []),
        ("Проекты/p1/Алиасный.md", "Алиасный", ["Псевдоним"]),
        ("Общее/Общий.md", "Общий", []),
        ("Проекты/p2/Чужой.md", "Чужой", []),
    ];

    public static TheoryData<string, string?, string> Forms => new()
    {
        { "Цель", "Проекты/p1/Цель.md", "заголовок" },
        { "цель", "Проекты/p1/Цель.md", "заголовок, регистр" },
        // B0: цель — то, что до первого «|»; подпись в резолв не входит.
        { "Цель|подпись", "Проекты/p1/Цель.md", "заголовок с подписью: цель — до «|»" },
        { "Цель|", "Проекты/p1/Цель.md", "пустая подпись (хвост 15)" },
        { "Цель | подпись", "Проекты/p1/Цель.md", "пробелы вокруг цели обрезаны" },
        { "Проекты/p1/Цель|подпись", "Проекты/p1/Цель.md", "префикс своего раздела с подписью" },
        // R-B0: LinkTarget обрезает цель и по краям — префикс с краевыми пробелами резолвится.
        { " Проекты/p1/Цель ", "Проекты/p1/Цель.md", "префикс с краевыми пробелами: цель обрезана, Resolved" },
        { "Общее/Общий|подпись", "Общее/Общий.md", "префикс раздела вверх с подписью" },
        { "Общий |подпись", "Общее/Общий.md", "голая вверх по цепочке с подписью" },
        { "|подпись", null, "пустая цель: резолвить нечего" },
        { "Чужой|подпись", null, "голая ссылка вбок с подписью" },
        { "Проекты/p2/Чужой|подпись", null, "префикс бокового раздела с подписью" },
        { "Неизвестная|подпись", null, "несуществующая страница с подписью" },
        { "Цель#якорь|подпись", null, "якорь и подпись: якорь остаётся частью цели" },
        { "Цель#якорь", null, "заголовок с якорем: то же" },
        { "Псевдоним", "Проекты/p1/Алиасный.md", "алиас страницы: заголовок и алиасы — одно пространство имён" },
        { "псевдоним|подпись", "Проекты/p1/Алиасный.md", "алиас: регистр и подпись" },
        { "Проекты/p1/Псевдоним", "Проекты/p1/Алиасный.md", "алиас с префиксом своего раздела" },
        { "Проекты/p1/Сосед", "Проекты/p1/Сосед.md", "префикс своего раздела" },
        { "Общее/Общий", "Общее/Общий.md", "префикс раздела вверх по цепочке" },
        { "Общий", "Общее/Общий.md", "голая ссылка вверх по цепочке" },
        { "Чужой", null, "голая ссылка вбок" },
        { "Проекты/p2/Чужой", null, "префикс бокового раздела" },
        { "Общее/Призрак", null, "префикс допустимого раздела, страницы нет" },
        { "щ/тот", null, "неразобранный префикс" },
        { "Неизвестная", null, "неизвестная страница" },
    };

    [Theory]
    [MemberData(nameof(Forms))]
    public void РезолвГейтаИСвязанныхСогласны(string link, string? expectedPath, string form)
    {
        var body = $"- См. [[{link}]].";
        AssertAgreement(body, expectedPath, form);
    }

    [Fact]
    public void ССылкаВКодСпане_НеСсылкаДляОбоих()
    {
        // Ссылка в код-спане — не ссылка (маска «вне кода» у OutboundLinks): ни одной связи.
        AssertAgreement("- Пример `[[Проекты/p1/Сосед]]` в коде.", null, "код-спан");
    }

    // B0: цель берёт WikiPage.LinkTarget — одна на обе стороны, своей копии разбора «|» в
    // резолвах нет. Якорь «#» — часть цели, он не режется. R-B0: LinkTarget дополнительно
    // снимает пробелы по краям цели: ссылка с краевыми пробелами [[ Проект… ]] для префиксной
    // цели — Resolved, а не BadPrefix, как было до Trim.
    [Theory]
    [InlineData("Цель|подпись", "Resolved")]
    [InlineData(" Проекты/p1/Цель ", "Resolved")]
    [InlineData("Цель|", "Resolved")]
    [InlineData("Цель | подпись", "Resolved")]
    [InlineData("Проекты/p1/Цель|подпись", "Resolved")]
    [InlineData("Общее/Общий|подпись", "Resolved")]
    [InlineData("Псевдоним", "Resolved")]
    [InlineData("Цель#якорь", "NotFound")]
    [InlineData("Цель#якорь|подпись", "NotFound")]
    [InlineData("|подпись", "NotFound")]
    [InlineData("Неизвестная|подпись", "NotFound")]
    public void РезолвБерётЦельДоВертикали(string link, string expected)
    {
        var snapshot = Snapshot($"См. [[{link}]].");
        WikiGate.ResolveLink(link, LinkerSection, ["common"], snapshot).Outcome.ToString()
            .Should().Be(expected, $"{link}: цель — до первого «|», якорь остаётся в цели");
    }

    [Fact]
    public void СнятиеОставляетРезолвимуюСсылку_НеразрешимуюСнимаетВПодпись()
    {
        var claim = "См. [[Цель|подпись]] и [[Неизвестная|текст]] и [[ Проекты/p1/Цель ]].";
        var snapshot = Snapshot(claim);
        var stripped = WikiGate.StripUnresolvedLinks(claim, LinkerSection, ["common"], snapshot, out var removed);
        stripped.Should().Be("См. [[Цель|подпись]] и текст и [[ Проекты/p1/Цель ]].",
            "резолвимая ссылка остаётся как написана — байт-в-байт по внутренности скобок, "
            + "краевые пробелы не трогает (R-B0: третья резолвится, потому что цель для резолва "
            + "обрезает LinkTarget, а не StripUnresolvedLinks); неразрешимая — как прежде, "
            + "в алиас (PlainLinkText не тронут)");
        removed.Should().Be(1);
    }

    private static List<WikiGate.Draft> Drafts(string linkerBody) =>
        Pages.Select(p => new WikiGate.Draft(p.Path, Content(p.Title, p.Aliases, "Содержимое без упоминаний.")))
            .Append(new WikiGate.Draft(Linker, Content("Источник", [], linkerBody)))
            .ToList();

    private static List<(string Section, WikiPage Page, string Path)> Snapshot(string linkerBody) =>
        Drafts(linkerBody).Select(d => (WikiSections.Parse(d.Path)!, WikiPage.Parse(d.Content).Model!, d.Path)).ToList();

    private static void AssertAgreement(string linkerBody, string? expectedPath, string form)
    {
        var drafts = Drafts(linkerBody);
        var snapshot = drafts
            .Select(d => (WikiSections.Parse(d.Path)!, WikiPage.Parse(d.Content).Model!, d.Path))
            .ToList();
        var linkerPage = snapshot.First(s => s.Path == Linker).Item2;

        // Сторона гейта: исход по каждой ссылке страницы-автора (в код-спане их нет вовсе).
        var links = linkerPage.OutboundLinks();
        var resolvedSections = links
            .Select(l => WikiGate.ResolveLink(l.Text, LinkerSection, ["common"], snapshot))
            .Where(r => r.Outcome == WikiGate.LinkOutcome.Resolved)
            .Select(r => r.Section)
            .ToList();
        if (expectedPath is null)
            resolvedSections.Should().BeEmpty($"{form}: гейт не должен резолвить");
        else
            resolvedSections.Should().ContainSingle().Which.Should().Be(WikiSections.Parse(expectedPath), form);

        // Сторона «Связанных», взгляд цели: у какой страницы автор — обратная ссылка. Страница
        // видит только свой раздел и вверх, поэтому из «Общего» проект не виден: смотрим цели
        // того же раздела, что у автора.
        var backlinked = new List<string>();
        foreach (var target in drafts.Where(d => d.Path != Linker && WikiSections.Parse(d.Path) == LinkerSection))
        {
            var groups = WikiRelated.Build(target, drafts);
            if (groups.Any(g => g.Kind == WikiRelated.KindBacklinks && g.Items.Any(i => i.PageKey == Linker)))
                backlinked.Add(target.Path);
        }
        var expectedBack = expectedPath is not null && WikiSections.Parse(expectedPath) == LinkerSection ? [expectedPath] : Array.Empty<string>();
        backlinked.Should().Equal(expectedBack, form);

        // Взгляд автора (видит и вверх): страница, чей заголовок стоит в тексте автора словом,
        // попадает в «упомянуты здесь» ровно тогда, когда ссылка автора в неё НЕ резолвится.
        var lower = linkerBody.ToLowerInvariant();
        var mask = NotesService.CodeMask(lower);
        var mentionedHere = WikiRelated.Build(drafts.First(d => d.Path == Linker), drafts)
            .Where(g => g.Kind == WikiRelated.KindMentionedHere)
            .SelectMany(g => g.Items.Select(i => i.PageKey))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        var expectedMentioned = Pages
            .Where(p => WikiSections.Parse(p.Path) is LinkerSection or "common")
            .Where(p => WikiRelated.IsMention(lower, mask, p.Title) && p.Path != expectedPath)
            .Select(p => p.Path)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        mentionedHere.Should().Equal(expectedMentioned, form);
    }

    private static string Content(string title, string[] aliases, string body)
    {
        var sb = new StringBuilder("---\ntype: ").Append(WikiFrontmatter.TypeTopic)
            .Append("\naliases: [").Append(string.Join(", ", aliases)).Append("]\nowns: []\nsources: []\n---\n# ")
            .Append(title).Append('\n');
        for (var i = 0; i < WikiPage.SectionNames.Count; i++)
        {
            sb.Append("\n## ").Append(WikiPage.SectionNames[i]).Append('\n');
            if (i == 0)
                sb.Append(body).Append('\n');
        }
        return sb.ToString();
    }
}
