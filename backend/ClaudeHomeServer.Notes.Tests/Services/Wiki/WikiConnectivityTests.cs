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

    // ─── K1 / K2 (A6) ───────────────────────────────────────────────────────────────

    private const string TopicA = "Проекты/p1/Квантовая-телепортация-фотонов.md";
    private const string TopicB = "Проекты/p1/Телепортация-фотонов-квантовая.md";
    private const string TopicTitleA = "Квантовая телепортация фотонов";
    private const string TopicTitleB = "Телепортация фотонов квантовая запутанных";

    // Фон из 58 страниц без общих редких основ: N большой, df общих основ у двух страниц — 2,
    // вес основы ln(30) ≈ 3,4, три основы из обоих заголовков (×2) — сумма заведомо выше MinScore.
    private static Fixture WithBackground(Fixture f)
    {
        for (var i = 0; i < 58; i++)
            f.Page($"Проекты/p1/Фон-{i}.md", $"Пустая страница номер {i}");
        return f;
    }

    [Fact]
    public void K1_ТолькоТемаВместоСсылок_ЗасчитываетсяA_НоK0НеРастёт()
    {
        var f = WithBackground(new Fixture()
            .Page(TopicA, TopicTitleA)
            .Page(TopicB, TopicTitleB));

        var r = WikiConnectivity.Measure(f.Snapshot, [TopicA, TopicB]);

        r.K1.Should().Be(1.0, "общие редкие основы дают ребро «По теме» с обеих сторон");
        r.K0.Should().Be(0, "прежняя степень topic не считает: ни упоминаний, ни ссылок");
        r.K2.Should().Be(0);
    }

    [Fact]
    public void K1_ПараДелитКарточкуИсточника_РёбраНеДаёт()
    {
        // Инвариант «метрика не засчитывает связь, которую сама и создала»: общая карточка —
        // происхождение, а не связь темы с темой. Мутация: убрать Overlaps в Measure — красный.
        var card = "Проекты/p1/Источники/Источник-работ.md";
        var f = WithBackground(new Fixture()
            .Page(card, "Источник работ", type: WikiFrontmatter.TypeSource, origin: "session", @ref: "session:chat-1#m1-5")
            .Page(TopicA, TopicTitleA, footnotes: ["Источник работ"])
            .Page(TopicB, TopicTitleB, footnotes: ["Источник работ"]));

        var r = WikiConnectivity.Measure(f.Snapshot, [TopicA, TopicB]);

        r.K1.Should().Be(0, "обе страницы из одной карточки источника: ребро не засчитано");
    }

    [Fact]
    public void Measure_ИндексПоТемеОдинРазНаНаборAllowed_ПостроенийНеБольшеЧислаНаборов()
    {
        // Сторож цены. Мутация: передать в Build null вместо topicIndex — индекс строится на каждую
        // страницу, вызовов порядка N², тест красный. Два раздела проектов = два набора allowed
        // ({pN, common}); глобальный индекс (один на снимок) здесь запрещён отдельным тестом ниже.
        var f = WithBackground(new Fixture().Page(TopicA, TopicTitleA).Page(TopicB, TopicTitleB));
        f.Page("Проекты/p1/Ссылка.md", "Ссылающаяся страница", text: "См. [[Квантовая телепортация фотонов]].");
        for (var i = 0; i < 20; i++)
            f.Page($"Проекты/p2/Другое-{i}.md", $"Страница второго проекта номер {i}");
        var n = f.Snapshot.Count;
        n.Should().BeGreaterThanOrEqualTo(80);
        var counter = new WikiRelated.NormCounter();
        var cache = new WikiRelated.TopicIndexCache(f.Snapshot, counter);

        WikiConnectivity.MeasurePages(f.Snapshot, [.. f.Snapshot.Select(d => d.Path)], counter, cache);

        cache.Built.Should().Be(2, "два разных набора видимых разделов — два индекса, не по индексу на страницу");
        counter.WordsCalls.Should().BeLessThanOrEqualTo(4 * n,
            "индексы — по числу видимых страниц набора, заголовочные основы лениво; на страницу индекс не строится");
    }

    [Fact]
    public void K1_ИндексПоВидимымРазделам_ОбщаяОсноваЧастаяВРазделеНеДаётРебра()
    {
        // Основа редкая в корпусе (60 страниц, df = 2), но частая в своём разделе (p1: 6 страниц,
        // df = 2 > MaxDfShare·6): при показе «По теме» этих двух страниц нет. Измерение обязано
        // видеть то же. Мутация: вернуть глобальный индекс по снимку (индекс для всех страниц
        // из всего снимка) — K1 по полному снимку станет 1, тест красный.
        var f = new Fixture().Page(TopicA, TopicTitleA).Page(TopicB, TopicTitleB);
        for (var i = 0; i < 4; i++)
            f.Page($"Проекты/p1/Фон-{i}.md", $"Пустая страница номер {i}");
        for (var i = 0; i < 54; i++)
            f.Page($"Проекты/p2/Фон-{i}.md", $"Пустая страница номер {i}");
        var full = f.Snapshot;
        var p1Slice = (IReadOnlyList<WikiGate.Draft>)[.. full.Where(d => d.Path.StartsWith("Проекты/p1/", StringComparison.Ordinal))];
        p1Slice.Count.Should().Be(6);

        WikiRelated.Build(full.Single(d => d.Path == TopicA), p1Slice)
            .Should().NotContain(g => g.Kind == WikiRelated.KindTopic, "в срезе раздела основа частая — «По теме» нет");

        var byFull = WikiConnectivity.Measure(full, [TopicA, TopicB]);
        var bySlice = WikiConnectivity.Measure(p1Slice, [TopicA, TopicB]);

        bySlice.K1.Should().Be(0);
        byFull.K1.Should().Be(bySlice.K1, "индекс — по видимым разделам: чужой раздел не меняет df и N");
    }

    [Fact]
    public void K2_СсылкаНаСтраницуЗнанийДаёт_СсылкаНаКарточкуНет()
    {
        var card = "Проекты/p1/Источники/Источник-работ.md";
        var target = "Проекты/p1/Порядок-работ.md";
        var toKnowledge = "Проекты/p1/Ссылка-на-знание.md";
        var toCard = "Проекты/p1/Ссылка-на-карточку.md";
        var f = new Fixture()
            .Page(card, "Источник работ", type: WikiFrontmatter.TypeSource)
            .Page(target, "Порядок работ")
            .Page(toKnowledge, "Ссылка на знание", text: "См. [[Порядок работ]].")
            .Page(toCard, "Ссылка на карточку", text: "См. [[Источник работ]].");

        var r = WikiConnectivity.Measure(f.Snapshot, [toKnowledge, toCard, target]);

        r.K2.Should().BeApproximately(1.0 / 3, 1e-9, "[[…]] на знание — только у одной из трёх страниц");
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
