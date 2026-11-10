using ClaudeHomeServer.Services.Notes.Wiki;

namespace ClaudeHomeServer.Tests.Services;

// R5 (ADR-039, решение 7): правило «страница спит». Уровень: исключения правила (disputed,
// правка человека, возраст, начало учёта), сигнал чтения и его half-open граница, recall
// с весом 1, обход раздела (свои страницы, карточки «Источники/» для признака авторства,
// испорченный снимок — пустое множество). Ничего не пишется: тесты только читают — статус
// вычисляется, и отсутствие записи в том числе тем, что у класса нет вызовов писателя.
public class WikiDormancyTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T00:00:00+00:00");
    private const string Section = "common";

    private readonly string _dir;

    public WikiDormancyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_dormancy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ─── IsDormant: исключения правила ──────────────────────────────────────────

    [Fact]
    public void Disputed_НикогдаНеСпит()
    {
        // Всё остальное такое, что без исключения страница спала бы: сигнал 40 дней.
        var fm = Fm(status: WikiFrontmatter.StatusDisputed, updated: Iso(Now.AddDays(-60)));

        WikiDormancy.IsDormant(fm, false, Reads(Now.AddDays(-40)), Now.AddDays(-60), Now)
            .Should().BeFalse("спор — дело человека и Хранителя, не архива");
    }

    [Fact]
    public void СтраницаМладшеОкна_НеСпит_ДажеБезЧтений()
    {
        var fm = Fm(updated: Iso(Now.AddDays(-6)));

        // Без исключения по возрасту страница спала бы: учёт старый, чтений нет.
        WikiDormancy.IsDormant(fm, false, null, Now.AddDays(-60), Now)
            .Should().BeFalse("у молодой страницы ещё не было шанса быть прочитанной");
    }

    [Fact]
    public void УчётНеНачат_НиктоНеСпит()
    {
        WikiDormancy.IsDormant(Fm(updated: Iso(Now.AddDays(-60))), false, null, null, Now)
            .Should().BeFalse("без базовой линии каждая страница выглядела бы непрочитанной — архив закрыл бы всю вики");
    }

    // ─── сигнал чтения: half-open граница и вес путей ───────────────────────────

    [Fact]
    public void БезЧтений29Дней_ЕщёНеСпит()
    {
        var fm = Fm(updated: Iso(Now.AddDays(-60)));

        WikiDormancy.IsDormant(fm, false, Reads(Now.AddDays(-29)), Now.AddDays(-60), Now).Should().BeFalse();
    }

    [Fact]
    public void БезЧтенийРовноОкно_Спит()
    {
        var fm = Fm(updated: Iso(Now.AddDays(-60)));

        WikiDormancy.IsDormant(fm, false, Reads(Now.AddDays(-30)), Now.AddDays(-60), Now)
            .Should().BeTrue("интервал half-open: сигнал ровно Window назад — уже спит");
    }

    [Fact]
    public void СвежийRecall_НеСпит_Вес1()
    {
        var fm = Fm(updated: Iso(Now.AddDays(-60)));
        // recall двинул LastRead (так его пишет стор): в этой волне он в правиле с весом 1
        // наравне с сильными чтениями (ADR-039 «Открыто», п. 4 — упрощение до замера).
        var reads = new WikiReadsStore.PageReads(2, 0, 0, 0, Now.AddDays(-5), Now.AddDays(-5), null);

        WikiDormancy.IsDormant(fm, false, reads, Now.AddDays(-60), Now).Should().BeFalse();
    }

    [Fact]
    public void БезЧтенийСудитНачалоУчёта()
    {
        var fm = Fm(updated: Iso(Now.AddDays(-90)));

        // Страницы нет в сторе — никто её не читал: последний сигнал — начало учёта, 40 дней назад.
        WikiDormancy.IsDormant(fm, false, null, Now.AddDays(-40), Now).Should().BeTrue();
    }

    // ─── DormantPages: обход раздела ─────────────────────────────────────────────

    [Fact]
    public void НевалидныйСнимок_НичегоНеАрхивируется()
    {
        WritePage("Общее/Спит.md", PageBody("Спит", Iso(Now.AddDays(-90))));
        var broken = new WikiReadsStore.Snapshot(
            false, "файл учёта чтений не читается", Now.AddDays(-60),
            new Dictionary<string, WikiReadsStore.PageReads>());

        WikiDormancy.DormantPages(_dir, Section, broken, Now)
            .Should().BeEmpty("на испорченном сторе половина вики притворилась бы непрочитанной");
    }

    [Fact]
    public void УчётНеНачат_РазделНеАрхивируется()
    {
        WritePage("Общее/Спит.md", PageBody("Спит", Iso(Now.AddDays(-90))));
        // Валидный снимок без начала учёта — состояние до первой записи стора.
        var snap = new WikiReadsStore.Snapshot(true, null, null, new Dictionary<string, WikiReadsStore.PageReads>());

        WikiDormancy.DormantPages(_dir, Section, snap, Now).Should().BeEmpty("базовой линии нет — архив ещё не начался");
    }

    [Fact]
    public void DormantPages_ВозвращаетТолькоСпящиеСтраницыРаздела()
    {
        WritePage("Общее/Спит.md", PageBody("Спит", Iso(Now.AddDays(-90))));   // нет в сторе — судит начало учёта (60 д)
        WritePage("Общее/Спит2.md", PageBody("Спит2", Iso(Now.AddDays(-90)))); // прочтена 45 дней назад
        WritePage("Общее/Свежая.md", PageBody("Свежая", Iso(Now.AddDays(-90)))); // прочтена 5 дней назад
        WritePage("Общее/Молодая.md", PageBody("Молодая", Iso(Now.AddDays(-10)))); // младше Window

        var snap = Snap(
            ("Общее/Спит2.md", Reads(Now.AddDays(-45))),
            ("Общее/Свежая.md", Reads(Now.AddDays(-5))));

        WikiDormancy.DormantPages(_dir, Section, snap, Now)
            .Should().BeEquivalentTo(new[] { "Общее/Спит.md", "Общее/Спит2.md" });
    }

    [Fact]
    public void ПравкаЧеловека_СтраницуНеАрхивирует()
    {
        // Авторство «human»: единственная действующая сноска — на карточку origin: human.
        // Всё остальное такое, что без признака страница спала бы: сигнал 40 дней.
        WritePage("Общее/Человек.md", PageBody("Человек", Iso(Now.AddDays(-90)), "Моя заметка"));
        WritePage("Общее/Ии.md", PageBody("Ии", Iso(Now.AddDays(-90)))); // сноска без карточки — «ai»
        WritePage("Общее/Источники/Моя заметка.md", CardBody("Моя заметка", "human"));

        var snap = Snap(
            ("Общее/Человек.md", Reads(Now.AddDays(-40))),
            ("Общее/Ии.md", Reads(Now.AddDays(-40))),
            ("Общее/Источники/Моя заметка.md", Reads(Now.AddDays(-5)))); // свежая карточка сама не спит

        WikiDormancy.DormantPages(_dir, Section, snap, Now)
            .Should().BeEquivalentTo(new[] { "Общее/Ии.md" },
                "на странице с правкой человека архив не проходит; ai-страницу судит сигнал");
    }

    [Fact]
    public void АвторствоTogether_ФлагаПравкиНеДаёт_СпитПоСигналу()
    {
        // Признак правки — признак «human»-авторства PageAuthorship: все действующие
        // сноски на человеческие карточки. «together» (человеческая и ai-сноски вместе)
        // его не даёт — такая страница судится сигналом чтения.
        WritePage("Общее/Вместе.md", PageBody("Вместе", Iso(Now.AddDays(-90)), "Моя заметка", "Нет карточки"));
        WritePage("Общее/Источники/Моя заметка.md", CardBody("Моя заметка", "human"));

        var snap = Snap(
            ("Общее/Вместе.md", Reads(Now.AddDays(-40))),
            ("Общее/Источники/Моя заметка.md", Reads(Now.AddDays(-5))));

        WikiDormancy.DormantPages(_dir, Section, snap, Now)
            .Should().BeEquivalentTo(new[] { "Общее/Вместе.md" });
    }

    [Fact]
    public void КарточкаИсточникаОцениваетсяОбщимПравилом()
    {
        // Задача не даёт карточке исключения: она страница под pages/ на том же правиле;
        // авторства у неё нет (PageAuthorship для карточек не пишется), и правкой она не помечается.
        WritePage("Общее/Источники/Карточка.md", CardBody("Карточка", "session"));

        WikiDormancy.DormantPages(_dir, Section, Snap(), Now)
            .Should().BeEquivalentTo(new[] { "Общее/Источники/Карточка.md" }, "никто не читал карточку 40+ дней");
    }

    [Fact]
    public void DormantPages_ЧитаетТолькоСтраницыОдногоРаздела()
    {
        WritePage("Общее/Спит.md", PageBody("Спит", Iso(Now.AddDays(-90))));
        WritePage("Личное/Чужой.md", PageBody("Чужой", Iso(Now.AddDays(-90)))); // спящая страница чужого раздела

        var result = WikiDormancy.DormantPages(_dir, Section, Snap(), Now);

        result.Should().Contain("Общее/Спит.md");
        result.Should().NotContain("Личное/Чужой.md", "обход одного раздела, не всей вики (бюджет шага)");
    }

    [Fact]
    public void НеКлючРазделаИПустойРаздел_ПустоеМножество()
    {
        WritePage("Общее/Спит.md", PageBody("Спит", Iso(Now.AddDays(-90))));

        WikiDormancy.DormantPages(_dir, "нет-раздела", Snap(), Now).Should().BeEmpty("такого раздела нет — нечего архивировать");
        WikiDormancy.DormantPages(_dir, "personal", Snap(), Now).Should().BeEmpty("раздела на диске нет");
    }

    [Fact]
    public void НеразобраннаяШапкаИПроекции_НеОцениваются()
    {
        WritePage("Общее/Кривая.md", "это не страница вики");
        WritePage("Общее/_index.md", "# Каталог раздела\n");
        WritePage("Общее/Спит.md", PageBody("Спит", Iso(Now.AddDays(-90))));

        WikiDormancy.DormantPages(_dir, Section, Snap(), Now)
            .Should().BeEquivalentTo(new[] { "Общее/Спит.md" },
                "по неразобранной шапке судить не по чему; служебный файл — не страница");
    }

    // ─── построение состояния ───────────────────────────────────────────────────

    private static WikiFrontmatter Fm(string status = WikiFrontmatter.StatusActive, string? updated = null) =>
        new() { Type = WikiFrontmatter.TypeTopic, Status = status, Updated = updated };

    // Запись сильной читки: счётчик и его время согласованы, как их держит стор.
    private static WikiReadsStore.PageReads Reads(DateTimeOffset lastRead) =>
        new(0, 1, 0, 0, lastRead, null, lastRead);

    private static WikiReadsStore.Snapshot Snap(params (string Key, WikiReadsStore.PageReads Reads)[] pages)
    {
        var dict = new Dictionary<string, WikiReadsStore.PageReads>(StringComparer.Ordinal);
        foreach (var (key, reads) in pages)
            dict[key] = reads;
        // Учёт начат 60 дней назад: дальше любой раздел без чтений уже в окне архива.
        return new WikiReadsStore.Snapshot(true, null, Now.AddDays(-60), dict);
    }

    private static string Iso(DateTimeOffset d) => d.ToString("O");

    private void WritePage(string key, string text) => WikiFileWriter.Write(_dir, "pages/" + key, text);

    // Страницы теста — разбираемые целиком: все канонические секции, действующие сноски
    // (ссылки в «Сводке») на карточки из footnoteTargets; цели без карточек — «не-human».
    private static string PageBody(string title, string? updated = null, params string[] footnoteTargets)
    {
        if (footnoteTargets.Length == 0)
            footnoteTargets = ["Нет карточки"];
        var body = "---\ntype: topic\nsummary: тест\n";
        if (updated is not null)
            body += "updated: " + updated + "\n";
        body += "status: active\n---\n\n# " + title + "\n\n## Сводка\n";
        for (var i = 0; i < footnoteTargets.Length; i++)
            body += "- Факт " + (i + 1) + ".[^s" + (i + 1) + "]\n";
        body += "\n## Хронология\n\n## Противоречия\n\n## История\n\n";
        for (var i = 0; i < footnoteTargets.Length; i++)
            body += "[^s" + (i + 1) + "]: [[" + footnoteTargets[i] + "]] — «цитата номер " + (i + 1) + " для теста»\n";
        return body;
    }

    private static string CardBody(string title, string origin) =>
        "---\ntype: source\norigin: " + origin + "\nref: x:1\nhash: h\ncaptured: 2026-11-01\n---\n\n# " + title + "\n";
}
