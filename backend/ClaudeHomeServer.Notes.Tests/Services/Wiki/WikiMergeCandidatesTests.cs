using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// C4: подбор пар merge — чистая функция на снимке страниц. Снимки тестов «мелкой» ветки —
// 24 страницы (22 фоновых с уникальными словами), чтобы 5 общих редких основ давали
// 5·ln(24/2) ≈ 12,4 ≥ MinScore = 11 (та же арифметика, что у замеров WikiTopicScoreTests).
public class WikiMergeCandidatesTests
{
    private static readonly string[] Gems = ["аметист", "бирюза", "гранат", "хризолит", "яшма"];

    private static string GemText => string.Join(" ", Gems);

    // Пять уникальных основ для одной пары (5·ln(N/2) ≥ 11 при N≥23): слова «ке»+i+«мщгцх»+«а» —
    // их основа («ке»+i+буква) уникальна для пары (i, буква) и не пересекается с чужими;
    // df = 2 (только эта страница и её B).
    private static string UniqueWords(int i) =>
        string.Join(" ", "мщгцх".Select(m => "ке" + (char)('а' + i) + m + "а"));

    // filler — нейтральные слова без основ тестовых пересечений («описан»/«порода» частые
    // или не общие); длина — чтобы сводка вышла за 400 символов и страница не была «мелкой».
    private static string FillText => string.Join(" ",
        Enumerable.Range(1, 25).Select(i => $"подробное описание породы номер {i}"));

    // Длинная сводка: ≥ 400 символов — страница не «мелкая».
    private static string LongText => GemText + " " + FillText;

    /// <summary>Страница раздела «Общее»: сводка — в шапке (та же проекция, что у каталога),
    /// утверждения — строки «Сводки» (без сносок — авторство остаётся «ai»); опции — тип,
    /// сноска на карточку (human-строка), секция «Заметки».</summary>
    private static WikiGate.Draft Page(string path, string title, string summary, int statements = 0,
        string type = "topic", string? humanCard = null, bool notes = false)
    {
        var text = "---\ntype: " + type + "\nsummary: " + summary + "\n---\n\n# " + title + "\n\n## Сводка\n";
        for (var i = 0; i < statements; i++)
            text += "- Факт " + (i + 1) + (humanCard is null ? "." : ".[^s1]") + "\n";
        text += "\n## Хронология\n\n## Противоречия\n\n## История\n";
        if (notes)
            text += "\n## Заметки\nМоя приписка\n";
        if (humanCard is not null)
            text += "\n[^s1]: [[" + humanCard + "]] — «цитата человека для теста»\n";
        return new WikiGate.Draft(path, text);
    }

    private static WikiGate.Draft Card(string path, string title, string origin) =>
        new(path, "---\ntype: source\norigin: " + origin + "\nref: x:1\nhash: h\ncaptured: 2026-11-01\n" +
            "---\n\n# " + title + "\n");

    /// <summary>Фон до N=24: заголовок «Фоновая{i}» (одна общая основа «фонов» — под MinCommon,
    /// TitleSubset между фоновыми никогда), сводка с уникальным словом — чужих пересечений нет.</summary>
    private static IEnumerable<WikiGate.Draft> Background(int count) =>
        Enumerable.Range(1, count).Select(i =>
            Page($"Общее/Фон{i}.md", $"Фоновая{i}", $"фоновое описание уникальное{i}"));

    private static List<(string A, string B)> Pick(
        IEnumerable<WikiGate.Draft> pages,
        IEnumerable<string>? open = null,
        IEnumerable<string>? quarantined = null,
        int maxTotal = 10,
        int maxPerSection = 3) =>
        WikiMergeCandidates.Pick([.. pages],
            new HashSet<string>(open ?? [], StringComparer.Ordinal),
            new HashSet<string>(quarantined ?? [], StringComparer.Ordinal),
            maxTotal, maxPerSection);

    // Снимок «мелкой» ветки: A — короткая (1 утверждение, сводка 34 символа), B — длинная
    // (не мелкая), 22 фоновых. 5 общих редких основ → 12,4 ≥ 11.
    private static List<WikiGate.Draft> SmallSnapshot(
        string aPath = "Общее/Камни.md", string bPath = "Общее/Породы.md", string aType = "topic") =>
        [.. Background(22),
            Page(aPath, "Камни", GemText, 1, aType),
            Page(bPath, "Породы", LongText, 2)];

    // ─── c1: ветка «мелкая страница» ────────────────────────────────────────────

    [Fact]
    public void Мелкая_ПохожаяBВТомЖеРазделе_РовноОднаПара()
    {
        var pairs = Pick(SmallSnapshot());

        pairs.Should().Equal(new[] { ("Общее/Камни.md", "Общее/Породы.md") },
            "5·ln(24/2) ≈ 12,4 ≥ MinScore; B — ближайшая и единственная допустимая");
    }

    [Fact]
    public void МелкаяСоСтрокойЧеловека_ПарНет()
    {
        var pages = SmallSnapshot();
        var a = pages.Single(p => p.Path == "Общее/Камни.md");
        pages[pages.FindIndex(p => p.Path == a.Path)] =
            Page(a.Path, "Камни", GemText, 1, humanCard: "Моя заметка");
        // Карточка human добавлена в снимок — порога она не меняет (её сводки нет).
        pages.Add(Card("Общее/Источники/Моя заметка.md", "Моя заметка", "human"));

        Pick(pages).Should().BeEmpty("предусловие A: все строки автора ai — human-сноска отсекает");
    }

    [Fact]
    public void МелкаяВОткрытомПакете_ПарНет()
    {
        Pick(SmallSnapshot(), open: ["Общее/Камни.md"]).Should().BeEmpty();
    }

    [Fact]
    public void ПохожаяBTДругомРазделе_ПарНет()
    {
        Pick(SmallSnapshot(bPath: "Личное/Породы.md")).Should().BeEmpty(
            "пары — только внутри одного раздела, цепочка вверх не берётся");
    }

    [Fact]
    public void МелкаяТипаState_ПарНет()
    {
        Pick(SmallSnapshot(aType: WikiFrontmatter.TypeState)).Should().BeEmpty(
            "состояние живёт своими правилами — не «мелкая»");
    }

    [Fact]
    public void МелкаяСДвумяУтверждениями_ПарНет()
    {
        var pages = SmallSnapshot();
        pages[pages.FindIndex(p => p.Path == "Общее/Камни.md")] =
            Page("Общее/Камни.md", "Камни", GemText, 2);

        Pick(pages).Should().BeEmpty("не больше одного утверждения");
    }

    [Fact]
    public void МелкаяСЗаметкамиЧеловека_ПарНет()
    {
        var pages = SmallSnapshot();
        pages[pages.FindIndex(p => p.Path == "Общее/Камни.md")] =
            Page("Общее/Камни.md", "Камни", GemText, 1, notes: true);

        Pick(pages).Should().BeEmpty("секция «Заметки» человека — не трогать");
    }

    [Fact]
    public void МелкаяНижеMinScore_ПарНет()
    {
        // Две общие редкие основы: 2·ln(24/2) ≈ 5,0 < 11 — та же граница, что у порога оценки.
        List<WikiGate.Draft> pages = [.. Background(22),
            Page("Общее/Камни.md", "Камни", "аметист бирюза", 1),
            Page("Общее/Породы.md", "Породы", "аметист бирюза " + FillText, 2)];

        Pick(pages).Should().BeEmpty("ниже MinScore — не «по теме»");
    }

    [Fact]
    public void МелкаяBВКарантине_ПараЕсть_AВКарантине_ПарНет()
    {
        Pick(SmallSnapshot(), quarantined: ["Общее/Породы.md"])
            .Should().Equal(new[] { ("Общее/Камни.md", "Общее/Породы.md") },
                "карантин — предусловие только A: B остаётся как есть");
        Pick(SmallSnapshot(), quarantined: ["Общее/Камни.md"]).Should().BeEmpty();
    }

    [Fact]
    public void МелкаяBВОткрытомПакете_ПарНет()
    {
        Pick(SmallSnapshot(), open: ["Общее/Породы.md"]).Should().BeEmpty(
            "A и B — не в открытом пакете: другой пакет меняет состояние любой из них");
    }

    // ─── c2: ветка «дубль» ──────────────────────────────────────────────────────

    private static List<WikiGate.Draft> DuplicateSnapshot() =>
        [.. Background(22),
            Page("Общее/Каталог Исходники.md", "Каталог Исходники", LongText, 2),
            Page("Общее/Содержимое каталога Исходники.md", "Содержимое каталога Исходники", LongText, 3)];

    [Fact]
    public void Дубль_РовноОднаПара_A_МеньшеПоУтверждениям()
    {
        var pairs = Pick(DuplicateSnapshot());

        pairs.Should().Equal(new[] { ("Общее/Каталог Исходники.md", "Общее/Содержимое каталога Исходники.md") },
            "TitleSubset: {катал, исход} ⊆ {содер, катал, исход}; A — 2 утверждения против 3; " +
            "обе не «мелкие» (сводка ≥ 400) — второй пары нет");
    }

    [Fact]
    public void ДубльРавныеУтверждения_A_ПутьБольшеПоOrdinal()
    {
        var pairs = Pick([.. Background(22),
            Page("Общее/Каталог Исходники.md", "Каталог Исходники", LongText, 2),
            Page("Общее/Содержимое каталога Исходники.md", "Содержимое каталога Исходники", LongText, 2)]);

        pairs.Should().Equal(new[] { ("Общее/Содержимое каталога Исходники.md", "Общее/Каталог Исходники.md") },
            "при равном числе утверждений A — чей путь больше по Ordinal");
    }

    [Fact]
    public void ДубльВСоседнемРазделе_ПарНет()
    {
        Pick([.. Background(22),
            Page("Общее/Каталог Исходники.md", "Каталог Исходники", LongText, 2),
            Page("Личное/Содержимое каталога Исходники.md", "Содержимое каталога Исходники", LongText, 3)])
            .Should().BeEmpty();
    }

    [Fact]
    public void ДубльПриоритетНадМелкой_ОднаПараНаA()
    {
        // «Каталог Исходники» — и дубль, и мелкая (1 утверждение, короткая сводка):
        // пара одна, и это дубль; «мелкая» ветка её не удваивает.
        var pairs = Pick([.. Background(22),
            Page("Общее/Каталог Исходники.md", "Каталог Исходники", GemText, 1),
            Page("Общее/Содержимое каталога Исходники.md", "Содержимое каталога Исходники", LongText, 3)]);

        pairs.Should().Equal(new[] { ("Общее/Каталог Исходники.md", "Общее/Содержимое каталога Исходники.md") });
    }

    // ─── c3: лимиты ─────────────────────────────────────────────────────────────

    [Fact]
    public void ДвенадцатьМелкихВОдномРазделе_ТриПары()
    {
        // Каждая мелкая — со своими уникальными основами (общие на 12 страниц были бы
        // «частыми» и отсекались MaxDfShare); B несёт все наборы и остаётся одной.
        var smalls = Enumerable.Range(1, 12).Select(i =>
            Page($"Общее/К{i}.md", $"К{i}", UniqueWords(i), 1));
        var bText = string.Join(" ", Enumerable.Range(1, 12).Select(UniqueWords)) + " " + FillText;

        var pairs = Pick([.. Background(10), .. smalls, Page("Общее/Породы.md", "Породы", bText, 2)],
            maxPerSection: 3);

        pairs.Should().HaveCount(3, "потолок раздела");
        pairs.Select(p => p.A).Should().Equal(["Общее/К1.md", "Общее/К10.md", "Общее/К11.md"],
            "порядок — Ordinal пути A");
        pairs.Select(p => p.B).Should().OnlyContain(p => p == "Общее/Породы.md");
    }

    [Fact]
    public void ПятьРазделовПоТриПары_ДесятьВсего()
    {
        // Пять настоящих разделов (ключи common < personal < projects/*): в каждом —
        // своя группа «Камни*» (3 мелкие) и своя «Породы» (похожая, не мелкая).
        // Разделы — непересекающиеся по видимости: common и personal не смотрят в projects/*,
        // поэтому «Породы» нужна своему разделу, а не всему снимку.
        var folders = new[] { "Общее", "Личное", "Проекты/p1", "Проекты/p2", "Проекты/p3" };
        var pages = new List<WikiGate.Draft>();
        for (var f = 0; f < folders.Length; f++)
        {
            // индекры уникальны на весь снимок: один и тот же набор в пяти разделах поднял бы
            // df до 5 и уронил бы оценку под MinScore.
            var idx = Enumerable.Range(f * 3 + 1, 3);
            foreach (var i in idx)
                pages.Add(Page($"{folders[f]}/К{i}.md", $"К{i}", UniqueWords(i), 1));
            pages.Add(Page($"{folders[f]}/Породы.md", "Породы",
                string.Join(" ", idx.Select(UniqueWords)) + " " + FillText, 2));
        }

        var pairs = Pick([.. Background(10), .. pages]);

        pairs.Should().HaveCount(10, "потолок общего: 15 потенциальных, выходит 10");
        pairs.Select(p => p.A).Take(4).Should().Equal(
            ["Общее/К1.md", "Общее/К2.md", "Общее/К3.md", "Личное/К4.md"],
            "разделы по Ordinal (common, personal, projects/*), внутри — путь A");
    }

    // ─── c4: детерминированность ────────────────────────────────────────────────

    [Fact]
    public void ТотЖеВходВПеремешанномПорядке_ТотЖеСписокПар()
    {
        var one = Pick(DuplicateSnapshot());
        var two = Pick(DuplicateSnapshot().OrderBy(_ => Guid.NewGuid()));
        var three = Pick(DuplicateSnapshot().OrderBy(_ => Guid.NewGuid()));

        two.Should().Equal(one);
        three.Should().Equal(one);

        var smallOne = Pick(SmallSnapshot());
        var smallTwo = Pick(SmallSnapshot().OrderBy(_ => Guid.NewGuid()));
        smallTwo.Should().Equal(smallOne);
    }

    // ─── границы и служебные ────────────────────────────────────────────────────

    [Fact]
    public void НеразобраннаяСтраницаИПроекции_НеКандидаты()
    {
        var pairs = Pick([.. SmallSnapshot(),
            new WikiGate.Draft("Общее/Сломано.md", "не страница вовсе"),
            new WikiGate.Draft("Общее/_index.md", "---\ntype: topic\n---\n\n# _index\n")]);

        pairs.Should().Equal(new[] { ("Общее/Камни.md", "Общее/Породы.md") });
    }

    [Fact]
    public void ПотолкиНенулевые_ПустойВход_Пусто()
    {
        Pick([]).Should().BeEmpty();
        Pick(SmallSnapshot(), maxTotal: 0).Should().BeEmpty();
        Pick(SmallSnapshot(), maxPerSection: 0).Should().BeEmpty();
    }
}
