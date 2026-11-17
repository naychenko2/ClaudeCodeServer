using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Q1-S6a (2Б): чистая проекция «Связанные» (WikiRelated) по снимку страниц раздела и цепочки
// вверх. Фикстуры — готовые тексты страниц: проверяется чистая функция, без диска и без модели.
// Одна фикстура — ровно одна группа нужного вида и ни одной лишней: иначе фикстура не
// различает группы.
public class WikiRelatedTests
{
    private const string P1 = "Проекты/p1/Порядок-работ.md";
    private const string Card = "Проекты/p1/Источники/Источник-работ.md";

    // ─── c1: каждая из пяти групп собирается на фикстуре ───────────────────────

    [Fact]
    public void ГруппаSource_КарточкаИзСноскиЕсть_СноскаНеНаКарточкуНет()
    {
        var f = new Fixture()
            .Target(footnotes: ["Источник работ", "Порядок встреч"])
            .Page(Card, "Источник работ", type: WikiFrontmatter.TypeSource)
            .Page("Проекты/p1/Порядок-встреч.md", "Порядок встреч");

        var groups = f.Build();

        Group(groups, WikiRelated.KindSource).Items.Should().Equal(new[] { new WikiRelatedItem(Card, "Источник работ", "source") });
        groups.Should().HaveCount(1, "ссылка на не-карточку — не источник: группы для неё нет");
    }

    [Fact]
    public void ГруппаChronicle_ТотЖеЧатИсточникИСсылкаНаЕгоХронику()
    {
        var chronicle = "Проекты/p1/Хроники/chat-1.md";
        var f = new Fixture()
            .Target(footnotes: ["Источник чата"])
            .Page(Card, "Источник чата", type: WikiFrontmatter.TypeSource,
                origin: "session", @ref: "session:chat-1#m1-5")
            .Page(chronicle, "Источник чата 2026-11-01", type: WikiFrontmatter.TypeChronicle);

        Group(f.Build(), WikiRelated.KindChronicle).Items
            .Should().Equal(new[] { new WikiRelatedItem(chronicle, "Источник чата 2026-11-01", "chronicle") });
    }

    [Fact]
    public void ГруппаChronicle_КарточкаНеЧатИсточник_ХроникиНет()
    {
        var f = new Fixture()
            .Target(footnotes: ["Источник файла"])
            .Page(Card, "Источник файла", type: WikiFrontmatter.TypeSource, origin: "file")
            .Page("Проекты/p1/Хроники/chat-1.md", "Хроника чата", type: WikiFrontmatter.TypeChronicle);

        f.Build().Should().OnlyContain(g => g.Kind == WikiRelated.KindSource);
    }

    [Fact]
    public void ГруппаMentions_УпоминаютЭтуСтраницуБезСсылки()
    {
        var other = "Проекты/p1/Порядок-встреч.md";
        var f = new Fixture()
            .Target()
            .Page(other, "Порядок встреч", text: "Обсудили Порядок работ и записали решение.");

        var groups = f.Build();
        Group(groups, WikiRelated.KindMentions).Items.Should().Equal(new[] { new WikiRelatedItem(other, "Порядок встреч", "topic") });
        groups.Should().HaveCount(1);
    }

    [Fact]
    public void ГруппаMentions_ЗаголовокКакЧастьСлова_НеУпоминание()
    {
        var f = new Fixture()
            .Target(title: "Порядок")
            .Page("Проекты/p1/Порядок-встреч.md", "Встречи", text: "Это был лишь подпорядок общих правил.");

        f.Build().Should().BeEmpty("«подпорядок» — не слово «порядок»: граница слова — часть матчера");
    }

    [Fact]
    public void ГруппаMentions_УпоминаниеВнутриКода_НеУпоминание()
    {
        var f = new Fixture()
            .Target()
            .Page("Проекты/p1/Порядок-встреч.md", "Порядок встреч", text: "Пример синтаксиса: `Порядок работ` — не упоминание.");

        f.Build().Should().BeEmpty("тот же матчер и та же маска «вне кода», что у lint");
    }

    [Fact]
    public void ГруппаMentionedHere_УпомянутыЗдесьБезСсылки()
    {
        var other = "Проекты/p1/Порядок-встреч.md";
        var f = new Fixture()
            .Target(text: "См. также Порядок встреч — детали там.")
            .Page(other, "Порядок встреч");

        var groups = f.Build();
        Group(groups, WikiRelated.KindMentionedHere).Items.Should().Equal(new[] { new WikiRelatedItem(other, "Порядок встреч", "topic") });
        groups.Should().HaveCount(1);
    }

    [Fact]
    public void ГруппаBacklinks_СсылкаУказываетСюда()
    {
        var other = "Проекты/p1/Порядок-встреч.md";
        var f = new Fixture()
            .Target()
            .Page(other, "Порядок встреч", text: "См. [[Порядок работ]].");

        var groups = f.Build();
        Group(groups, WikiRelated.KindBacklinks).Items.Should().Equal(new[] { new WikiRelatedItem(other, "Порядок встреч", "topic") });
        groups.Should().HaveCount(1);
    }

    [Fact]
    public void ГруппаBacklinks_СсылкаСПрефиксомРазделаРезолвится()
    {
        var f = new Fixture()
            .Target()
            .Page("Проекты/p1/Порядок-встреч.md", "Порядок встреч", text: "См. [[Проекты/p1/Порядок работ]].");

        Group(f.Build(), WikiRelated.KindBacklinks).Items.Should().HaveCount(1);
    }

    // ─── правила групп ──────────────────────────────────────────────────────────

    [Fact]
    public void ПотолокГруппыВосемь_ДевятыйНеПопадает()
    {
        var f = new Fixture().Target();
        for (var i = 1; i <= 9; i++)
            f.Page($"Проекты/p1/Фикстура-{i}.md", $"Фикстура {i}", text: "Упомянут Порядок работ.");

        var items = Group(f.Build(), WikiRelated.KindMentions).Items;
        items.Should().HaveCount(WikiRelated.GroupLimit, "«Связанные» — блок страницы, не каталог");
        items.Select(i => i.PageKey).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void ДубльМеждуГруппамиОстаётсяТолькоВСтаршей_ВзаимноеУпоминание()
    {
        var f = new Fixture()
            .Target(text: "См. также Порядок встреч — детали там.")
            .Page("Проекты/p1/Порядок-встреч.md", "Порядок встреч", text: "Обсудили Порядок работ и записали решение.");

        var groups = f.Build();
        Group(groups, WikiRelated.KindMentions).Items.Should().HaveCount(1);
        groups.Should().NotContain(g => g.Kind == WikiRelated.KindMentionedHere,
            "тот же pageKey в двух группах — нет: нижняя группа уступает старшей");
    }

    [Fact]
    public void ДубльМеждуГруппамиОстаётсяТолькоВСтаршей_ХроникаСсылаетсяСюда()
    {
        var chronicle = "Проекты/p1/Хроники/chat-1.md";
        var f = new Fixture()
            .Target(footnotes: ["Источник чата"])
            .Page(Card, "Источник чата", type: WikiFrontmatter.TypeSource,
                origin: "session", @ref: "session:chat-1#m1-5")
            .Page(chronicle, "Хроника чата", type: WikiFrontmatter.TypeChronicle, text: "Работали над [[Порядок работ]].");

        var groups = f.Build();
        Group(groups, WikiRelated.KindChronicle).Items.Should().HaveCount(1);
        groups.Should().NotContain(g => g.Kind == WikiRelated.KindBacklinks,
            "хроника уже в своей группе — в backlinks её дубля нет");
    }

    [Fact]
    public void КарточкаИсточникаИПроекцияИсключеныИзУпоминаний()
    {
        var f = new Fixture()
            .Target(text: "Вспоминается Источник работ — но ссылки нет.")
            .Page(Card, "Источник работ", type: WikiFrontmatter.TypeSource)
            .Page("Проекты/p1/_index.md", "Указатель", text: "Здесь список: Порядок работ.");

        f.Build().Should().BeEmpty(
            "карточка источника имеет свою группу (её здесь нет), а _* генерирует код — не страница");
    }

    [Fact]
    public void СтраницаЧужогоБоковогоРазделаНеПопадает_СвояСфераИОбщееПопадают()
    {
        var side = "Проекты/p2/Порядок-встреч.md";
        var sphere = "Сферы/s7/Порядок-встреч-2.md";
        var common = "Общее/Порядок-встреч-3.md";
        var f = new Fixture()
            .Target()
            .Page(side, "Порядок встреч", text: "Обсудили Порядок работ.")
            .Page(sphere, "Порядок встреч 2", text: "Обсудили Порядок работ.")
            .Page(common, "Порядок встреч 3", text: "Обсудили Порядок работ.");

        var items = Group(f.Build(), WikiRelated.KindMentions).Items;
        items.Select(i => i.PageKey).Should().Equal([common, sphere],
            "видимость — свой раздел плюс цепочка вверх (тот же allowed, что у гейта): боковой проект выпадает");
    }

    [Fact]
    public void ПустыеГруппыНеОтдаются_ПустойСнимокПуст()
    {
        var f = new Fixture()
            .Target()
            .Page("Проекты/p1/Порядок-встреч.md", "Порядок встреч");
        f.Build().Should().BeEmpty();

        var empty = new Fixture();
        empty.Target();
        empty.Build().Should().BeEmpty();
    }

    [Fact]
    public void НеразобраннаяЦельИПроекцияЦелью_Пусто()
    {
        var f = new Fixture();
        f.Target();
        var target = f.TargetDraft;
        WikiRelated.Build(new WikiGate.Draft(P1, "не страница вовсе"), [target]).Should().BeEmpty();
        WikiRelated.Build(new WikiGate.Draft("Проекты/p1/_index.md", target.Content), [target]).Should().BeEmpty();
    }

    [Fact]
    public void Детерминизм_ОдинСоставВОбратномПорядкеВхода_ТотЖеОтвет()
    {
        var a = new Fixture()
            .Target(text: "См. также Порядок встреч.")
            .Page("Проекты/p1/Порядок-встреч.md", "Порядок встреч", text: "Обсудили Порядок работ.")
            .Page("Проекты/p1/Порядок-собраний.md", "Порядок собраний", text: "См. [[Порядок работ]].");
        var b = new Fixture();
        b.TargetDraft = a.TargetDraft;
        b.Candidates.AddRange(a.Candidates.AsEnumerable().Reverse());

        var first = a.Build();
        var second = b.Build();

        // Сравнение — по JSON: record с полем-списком равенства по содержанию не даёт
        // (та же ловушка, что в WikiLintTests), а сравнивать надо именно порядок и состав.
        Json(first).Should().Be(Json(second), "порядок групп и элементов — часть контракта");
        first.Should().HaveCount(2, "иначе сравнение порядков ничего не проверяет");
    }

    // ─── c3: JSON-контракт Q1-S7 ────────────────────────────────────────────────

    [Fact]
    public void СериализацияWebНастройками_РовноКлючиKindItemsPageKeyTitleType()
    {
        var f = new Fixture()
            .Target()
            .Page("Проекты/p1/Порядок-встреч.md", "Порядок встреч", text: "Обсудили Порядок работ.");

        Json(f.Build()).Should().Be(
            """[{"kind":"mentions","items":[{"pageKey":"Проекты/p1/Порядок-встреч.md","title":"Порядок встреч","type":"topic"}]}]""");
    }

    // ─── фикстура ───────────────────────────────────────────────────────────────

    private static WikiRelatedGroup Group(IReadOnlyList<WikiRelatedGroup> groups, string kind)
    {
        groups.Should().ContainSingle(g => g.Kind == kind, $"ожидалась ровно одна группа {kind}");
        return groups.First(g => g.Kind == kind);
    }

    // Те же web-настройки, что даёт ASP.NET Core: camelCase и неэкранированная юникод.
    private static readonly JsonSerializerOptions WebJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Json(IReadOnlyList<WikiRelatedGroup> groups) => JsonSerializer.Serialize(groups, WebJson);

    private sealed class Fixture
    {
        public WikiGate.Draft TargetDraft { get; set; } = default;
        public List<WikiGate.Draft> Candidates { get; } = [];

        // Страница по умолчанию: валидная, без снок и без упоминаний других страниц — фон,
        // который сам по себе не даёт ни одной группы.
        public Fixture Page(
            string path,
            string title,
            string type = WikiFrontmatter.TypeTopic,
            string[]? footnotes = null,
            string? origin = null,
            string? @ref = null,
            string text = "Содержимое страницы без упоминаний других.")
        {
            Candidates.Add(new WikiGate.Draft(path, Content(title, type, footnotes, origin, @ref, text)));
            return this;
        }

        public Fixture Target(
            string title = "Порядок работ",
            string[]? footnotes = null,
            string text = "Содержимое страницы без упоминаний других.")
        {
            TargetDraft = new WikiGate.Draft(P1, Content(title, WikiFrontmatter.TypeTopic, footnotes, null, null, text));
            return this;
        }

        // Снимок — как его даёт вызывающий: страницы раздела вместе с самой целью
        // (свою страницу Build исключает сам).
        public IReadOnlyList<WikiRelatedGroup> Build() => WikiRelated.Build(TargetDraft, [TargetDraft, .. Candidates]);

        // Форма страницы полная (требование WikiPage.Parse): шапка, заголовок, канонические
        // секции — текст идёт в первой; у карточки секций нет вовсе.
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
