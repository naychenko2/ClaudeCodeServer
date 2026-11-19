using System.Text;
using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// A5 (очередь 2, волна A): чистый замер связности (WikiConnectivity) на фикстурах — степень =
// |mentions| + |mentionedHere| + |backlinks| из «Связанных», source и chronicle — отдельным
// счётчиком, в степень не входят (формула замера Q1-QA4). Фикстуры — готовые тексты страниц,
// без диска и без модели (тот же Fixture, что в WikiRelatedTests).
public class WikiConnectivityTests
{
    // ─── c1: корпус из 4 страниц с заранее посчитанными связями ────────────────────

    [Fact]
    public void Measure_KnownCorpus()
    {
        var a = "Проекты/p1/Порядок-работ.md";
        var b = "Проекты/p1/Порядок-встреч.md";
        var card = "Проекты/p1/Источники/Источник-работ.md";
        var chronicle = "Проекты/p1/Хроники/chat-1.md";

        // a: сноска на карточку (её source) и упоминание заголовка b; b: упоминание заголовка a
        // словом, без ссылки; карточка — источник чата, у a есть его хроника; сноска a [[…]] —
        // OutboundLink → backlink карточки.
        var f = new Fixture()
            .Page(a, "Порядок работ", text: "См. также Порядок встреч — детали там.", footnotes: ["Источник работ"])
            .Page(b, "Порядок встреч", text: "Обсудили Порядок работ и записали решение.")
            .Page(card, "Источник работ", type: WikiFrontmatter.TypeSource, origin: "session", @ref: "session:chat-1#m1-5")
            .Page(chronicle, "Хроника чата 2026-11-01", type: WikiFrontmatter.TypeChronicle);

        var r = WikiConnectivity.Measure(f.Snapshot, [a, b, card, chronicle]);

        // Степени: a = 1 (упоминают её b; b в её же mentionedHere дедуплицирован в старшую
        // группу — не удваивает), b = 1 (упоминает её a), карточка = 1 (backlink a сноской),
        // хроника = 0. Сумма 3, среднее 0.75, доля со связью 3/4.
        // Группы source и chronicle страницы a (по 1) в сумму не вошли: вошли бы — среднее 1.25.
        r.Count.Should().Be(4);
        r.AvgDegree.Should().Be(0.75);
        r.LinkedShare.Should().Be(0.75, "со связью (степень ≥ 1) — три страницы из четырёх");
    }

    // ─── c2: степень несрезанная ────────────────────────────────────────────────────

    [Fact]
    public void Degree_NoCap()
    {
        var target = "Проекты/p1/Порядок-работ.md";
        var f = new Fixture().Page(target, "Порядок работ");
        for (var i = 1; i <= 10; i++)
            f.Page($"Проекты/p1/Фикстура-{i}.md", $"Фикстура {i}", text: "См. [[Порядок работ]].");

        var r = WikiConnectivity.Measure(f.Snapshot, [target]);

        r.Count.Should().Be(1);
        r.AvgDegree.Should().Be(10.0, "10 обратных ссылок не срезаются GroupLimit: Measure зовёт Build с limit = null");
        r.LinkedShare.Should().Be(1.0);
    }

    // ─── крайние случаи ─────────────────────────────────────────────────────────────

    [Fact]
    public void ПустойЗамерИПутьВнеСнимка_НулиБезДеленияНаНоль()
    {
        var a = "Проекты/p1/Порядок-работ.md";
        var f = new Fixture().Page(a, "Порядок работ");

        WikiConnectivity.Measure(f.Snapshot, []).Should().BeEquivalentTo(new WikiConnectivity.Result(0, 0, 0));
        WikiConnectivity.Measure(f.Snapshot, ["Проекты/p1/Нет-такой.md"])
            .Should().BeEquivalentTo(new WikiConnectivity.Result(0, 0, 0),
                "пути вне снимка не измеряются: метрика не догадывается о тексте, которого не видит");
    }

    // ─── фикстура (та же форма страницы, что в WikiRelatedTests) ────────────────────

    private sealed class Fixture
    {
        public List<WikiGate.Draft> Pages { get; } = [];

        public Fixture Page(
            string path,
            string title,
            string type = WikiFrontmatter.TypeTopic,
            string[]? footnotes = null,
            string? origin = null,
            string? @ref = null,
            string text = "Содержимое страницы без упоминаний других.")
        {
            Pages.Add(new WikiGate.Draft(path, Content(title, type, footnotes, origin, @ref, text)));
            return this;
        }

        /// <summary>Снимок — как его даёт вызывающий: каждая измеряемая страница есть в нём.</summary>
        public IReadOnlyList<WikiGate.Draft> Snapshot => [.. Pages];

        private static string Content(
            string title,
            string type,
            string[]? footnotes,
            string? origin,
            string? @ref,
            string text)
        {
            var sb = new StringBuilder("---\ntype: ").Append(type).Append('\n');
            if (type == WikiFrontmatter.TypeSource)
                sb.Append("origin: ").Append(origin ?? "file")
                    .Append("\nref: ").Append(@ref ?? "note:Идеи/Заметка.md")
                    .Append("\nhash: h1\ncaptured: 2026-11-01\n");
            sb.Append("aliases: []\nowns: []\nsources: []\n---\n# ").Append(title).Append('\n');
            if (type != WikiFrontmatter.TypeSource)
            {
                var sections = type == WikiFrontmatter.TypeChronicle
                    ? WikiPage.ChronicleSectionNames
                    : WikiPage.SectionNames;
                for (var i = 0; i < sections.Count; i++)
                {
                    sb.Append("\n## ").Append(sections[i]).Append('\n');
                    if (i == 0)
                        sb.Append(text).Append('\n');
                }
            }
            var ids = footnotes ?? [];
            for (var i = 0; i < ids.Length; i++)
                sb.Append("[^s").Append(i + 1).Append("]: [[").Append(ids[i]).Append("]] — «цитата источника»\n");
            return sb.ToString();
        }
    }
}
