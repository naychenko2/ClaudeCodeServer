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
        { "Цель|подпись", null, "заголовок с подписью: цель — всё внутри скобок, как у гейта" },
        { "Цель#якорь", null, "заголовок с якорем: то же" },
        { "Цель|", null, "пустая подпись (хвост 15)" },
        { "Псевдоним", null, "алиас страницы: резолв по заголовку, алиасы не участвуют" },
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

    private static void AssertAgreement(string linkerBody, string? expectedPath, string form)
    {
        var drafts = Pages.Select(p => new WikiGate.Draft(p.Path, Content(p.Title, p.Aliases, "Содержимое без упоминаний.")))
            .Append(new WikiGate.Draft(Linker, Content("Источник", [], linkerBody)))
            .ToList();
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
