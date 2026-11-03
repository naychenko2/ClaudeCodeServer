using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Tests.Helpers;

namespace ClaudeHomeServer.Tests.Services;

// S2b-3 (ADR-035, решения 4 пункт 6 и 6): проекции раздела — _index.md (каталог страниц
// раздела) и _log.md (последние 200 строк журнала этого раздела, новые сверху) —
// перегенерируются ЦЕЛИКОМ Хранителем после каждой записи и после adopt, под той же
// блокировкой раздела. Проекции — производные файлы: в манифест не входят, дрейфом не
// считаются, в каталог другого раздела не попадают, а обрыв строки журнала не роняет
// перегенерацию.
public class WikiProjectionsTests : IDisposable
{
    private const string User = "u1";
    private readonly string _dir;
    private readonly WikiKeeper _sut;

    public WikiProjectionsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_projections_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(config, users, new AppSettingsService(config));
        var notes = new NotesService(projects, config, NullLogger<NotesService>.Instance);
        _sut = new WikiKeeper(notes, config);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Root() => WikiPaths.Root(_dir, User);
    private string ProjectionPath(string folder, string fileName) => Path.Combine(WikiPaths.Pages(_dir, User), folder, fileName);

    private static string[] EntryLines(string path) =>
        [.. File.ReadAllLines(path, Encoding.UTF8).Where(l => l.StartsWith("- ", StringComparison.Ordinal))];

    // Страница с разберёмой шапкой: без summary — строка каталога без описания.
    private static string Page(string title, string type, string? summary = null) =>
        $"""
        ---
        type: {type}
        {(summary is null ? "" : $"summary: {summary}\n")}aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {title}

        ## Сводка
        ## Хронология
        ## Противоречия
        ## История
        """;

    // ─── 1. _index.md: строки только страниц своего раздела ──────────────────

    [Fact]
    public async Task ИндексРазделаСодержитЕгоСтраницы_СтрокЧужихРазделовНикаких()
    {
        await _sut.EnsureInitializedAsync(User);
        const string ownKey = "Проекты/p1/Знание.md";
        const string ownNoSummary = "Проекты/p1/Другое.md";

        // Чужие разделы — раньше своих: иная проба различала бы «обход своего раздела» и
        // «обход всех разделов на момент записи» (мутация M1 прошла бы незамеченной).
        (await _sut.WritePageAsync(User, "common", "Общее/Чужое.md", Page("Чужое", "topic", "из общего раздела")))
            .Status.Should().Be(WikiOpStatus.Applied);
        (await _sut.WritePageAsync(User, "projects/p2", "Проекты/p2/ЧужойПроект.md", Page("ЧужойПроект", "topic")))
            .Status.Should().Be(WikiOpStatus.Applied);
        (await _sut.WritePageAsync(User, "projects/p1", ownKey, Page("Знание", "topic", "Что известно о p1")))
            .Status.Should().Be(WikiOpStatus.Applied);
        (await _sut.WritePageAsync(User, "projects/p1", ownNoSummary, Page("Другое", "concept")))
            .Status.Should().Be(WikiOpStatus.Applied);

        var entries = EntryLines(ProjectionPath("Проекты/p1", WikiProjections.IndexFileName));

        // Ровно по строке на каждую страницу раздела — и ни одной чужой. Мутация «индекс по
        // всем разделам» (обход pages/ целиком) даёт здесь 4 строки и чужие имена — красный.
        entries.Should().HaveCount(2);
        entries.Should().ContainSingle(
            e => e.Contains("[[Знание]]") && e.Contains("topic") && e.Contains("Что известно о p1"),
            "строка каталога несёт имя, type и описание из шапки");
        entries.Should().ContainSingle(e => e.Contains("[[Другое]]") && e.Contains("concept"));
        string.Join("\n", entries).Should().NotContain("Чужое").And.NotContain("ЧужойПроект");
        // Проекции — не страницы: сами себя каталог не перечисляет.
        string.Join("\n", entries).Should().NotContain("_index").And.NotContain("_log");
    }

    // ─── 2. _log.md: последние 200 записей своего раздела, новые сверху ───────

    [Fact]
    public async Task ЖурналРаздела_205Записей_Ровно200ПерваяСамаяНовая_ЧужогоРазделаНикаких()
    {
        await _sut.EnsureInitializedAsync(User);
        var root = Root();

        // Порядок строк в log.jsonl — порядок дописывания (append), он и есть хронология.
        for (var i = 1; i <= 205; i++)
            WikiManifest.AppendLog(root, "write", "projects/p1", [$"Проекты/p1/П{i:000}"], null, "keeper");
        for (var i = 1; i <= 5; i++)
            WikiManifest.AppendLog(root, "write", "common", [$"Общее/Ч{i:000}"], null, "keeper");

        WikiProjections.RegenerateSection(root, "projects/p1");

        var entries = EntryLines(ProjectionPath("Проекты/p1", WikiProjections.LogFileName));
        entries.Should().HaveCount(WikiProjections.LogLinesLimit, "витрина журнала — окно, а не весь стор");
        entries[0].Should().Contain("П205", "новые сверху");
        entries[^1].Should().Contain("П006", "за окном остаётся самая старая из показанных");
        string.Join("\n", entries).Should().NotContain("П005", "записи за пределами окна не переносятся");
        string.Join("\n", entries).Should().NotContain("Ч0", "журнал общий на владельца — строки чужого раздела сюда не попадают");
    }

    // ─── 3. Проекции не в манифесте и не дают дрейфа ─────────────────────────

    [Fact]
    public async Task ПроекцииНеВМанифесте_ИхЗаписьНеСчитаетсяДрейфомРаздела()
    {
        await _sut.EnsureInitializedAsync(User);
        const string key = "Проекты/p1/Знание.md";

        (await _sut.WritePageAsync(User, "projects/p1", key, Page("Знание", "topic")))
            .Status.Should().Be(WikiOpStatus.Applied);

        File.Exists(ProjectionPath("Проекты/p1", WikiProjections.IndexFileName)).Should().BeTrue();
        File.Exists(ProjectionPath("Проекты/p1", WikiProjections.LogFileName)).Should().BeTrue();

        var manifest = WikiManifest.TryRead(Root(), "projects/p1");
        manifest.State.Should().Be(WikiManifestState.Valid);
        manifest.Manifest.Keys.Should().Contain(key);
        manifest.Manifest.Keys.Should().NotContain("Проекты/p1/_index.md").And.NotContain("Проекты/p1/_log.md",
            "проекции — производные файлы, их нет ни в манифесте, ни в сверке");

        // Та же страница вторично: диск изменился только её каталогом — это не дрейф.
        (await _sut.WritePageAsync(User, "projects/p1", key, Page("Знание", "topic", "вторая версия")))
            .Status.Should().Be(WikiOpStatus.Applied);
    }

    // ─── 4. Хранитель обновляет проекции после записи и после adopt ──────────

    [Fact]
    public async Task ПослеЗаписиИндексВидитСтраницу_ПослеAdoptЕёСтрокаВЖурналеРаздела()
    {
        await _sut.EnsureInitializedAsync(User);
        const string key = "Проекты/p1/Знание.md";

        (await _sut.WritePageAsync(User, "projects/p1", key, Page("Знание", "topic")))
            .Status.Should().Be(WikiOpStatus.Applied);
        EntryLines(ProjectionPath("Проекты/p1", WikiProjections.IndexFileName))
            .Should().ContainSingle(e => e.Contains("[[Знание]]"));

        // Дрейф и его закрытие: adopt дописывает строку журнала — _log.md обязан её показать.
        File.WriteAllText(Path.Combine(WikiPaths.Pages(_dir, User), key), "правка человека", new UTF8Encoding(false));
        (await _sut.AdoptAsync(User, "projects/p1", key)).Status.Should().Be(WikiOpStatus.Applied);

        var log = EntryLines(ProjectionPath("Проекты/p1", WikiProjections.LogFileName));
        log.Should().ContainSingle(e => e.Contains("adopt") && e.Contains("user") && e.Contains(key));
        // Другой раздел своей витрины не получил: чужая строка в его _log.md не появляется.
        File.Exists(ProjectionPath("Общее", WikiProjections.LogFileName)).Should().BeFalse();
    }

    // ─── 5. Обрыв/битый JSON строки журнала пропускается, не роняет её ───────

    [Fact]
    public void БитыеСтрокиЖурналаПропускаются_ПерегенерацияНеПадает()
    {
        var root = Root();
        Directory.CreateDirectory(WikiPaths.Keeper(_dir, User));
        var logPath = Path.Combine(WikiPaths.Keeper(_dir, User), "log.jsonl");

        // Строки дописываются мимо Хранителя: хвост мог оборваться на середине записи.
        // Перевод строки — отдельной "\n": в raw-литерале «\n» — два символа, не перевод.
        File.AppendAllText(logPath, """{"ts":"2026-10-28T10:00:00+00:00","op":"write","section":"projects/p1","pages":["Проекты/p1/A.md"],"source":null,"actor":"keeper"}""" + "\n");
        File.AppendAllText(logPath, "мусор { не json\n");
        File.AppendAllText(logPath, """{"ts":"2026-10-28T10:02:00+00:00","op":"adopt","section":"projects/p1","pages":[],"source":null,"actor":"user"}""" + "\n");
        // Оборванный хвост — последним: без перевода он склеил бы с собой следующую запись.
        File.AppendAllText(logPath, "{\"ts\":\"2026-10-28T10:03:00+00:00\",\"op\":\"w");

        var act = () => WikiProjections.RegenerateSection(root, "projects/p1");
        act.Should().NotThrow("витрина не роняет перегенерацию оборванной строкой стора");

        var entries = EntryLines(ProjectionPath("Проекты/p1", WikiProjections.LogFileName));
        entries.Should().HaveCount(2);
        entries[0].Should().Contain("adopt");
        entries[1].Should().Contain("Проекты/p1/A.md");
        string.Join("\n", entries).Should().NotContain("мусор");
    }

    // —— B1 (ADR-038): признак авторства страницы по сноскам ——

    private static string Card(string title, string origin) =>
        $"---\ntype: source\nsummary: к\norigin: {origin}\nref: x:1\nhash: h\ncaptured: 2026-11-02\nstatus: active\n---\n\n# {title}\n";

    private static string PageWith(string title, params string[] cards)
    {
        var swodka = string.Join("\n", cards.Select((c, i) => $"- Утверждение {i + 1}.[^s{i + 1}]"));
        var foots = string.Join("\n", cards.Select((c, i) => $"[^s{i + 1}]: [[{c}]] — «цитата номер {i + 1} длинная»"));
        return $"---\ntype: entity\nsummary: с\nstatus: active\n---\n\n# {title}\n\n## Сводка\n{swodka}\n\n## Хронология\n\n## Противоречия\n\n## История\n\n{foots}\n";
    }

    [Fact]
    public void Авторство_ИИВместеЧеловек_ПоСноскам_КарточкиНеСчитаются()
    {
        var state = new List<WikiGate.Draft>
        {
            new("Общее/Источники/А.md", Card("А", "session")),
            new("Общее/Источники/Я.md", Card("Я", "human")),
            new("Общее/Ии.md", PageWith("Ии", "А")),
            new("Общее/Вместе.md", PageWith("Вместе", "А", "Я")),
            new("Общее/Чел.md", PageWith("Чел", "Я")),
            new("Общее/Пусто.md", PageWith("Пусто")),
        };
        var res = WikiProjections.Authorship(state);
        res["Общее/Ии.md"].Should().Be(WikiProjections.AuthorshipAi);
        res["Общее/Вместе.md"].Should().Be(WikiProjections.AuthorshipTogether);
        res["Общее/Чел.md"].Should().Be(WikiProjections.AuthorshipHuman);
        res["Общее/Пусто.md"].Should().Be(WikiProjections.AuthorshipAi);
        res.Keys.Should().NotContain(k => k.Contains("Источники"));
    }

    [Fact]
    public void Авторство_СноскаТолькоВИстории_НеСчитается()
    {
        // Мутация «считать и «Историю»»: страница с human-сноской только в «Истории» станет
        // together (Ии2) / human (Ист) — оба Be краснеют.
        string History(string title, string actual, string past) =>
            $"---\ntype: entity\nsummary: с\nstatus: active\n---\n\n# {title}\n\n## Сводка\n- Действующее.[^s1]\n\n## Хронология\n\n## Противоречия\n\n## История\n- Снято.[^s2]\n\n[^s1]: [[{actual}]] — «цитата номер один длинная»\n[^s2]: [[{past}]] — «цитата номер два длинная»\n";
        var state = new List<WikiGate.Draft>
        {
            new("Общее/Источники/А.md", Card("А", "session")),
            new("Общее/Источники/Я.md", Card("Я", "human")),
            new("Общее/Ии2.md", History("Ии2", "А", "Я")),
            new("Общее/Чел2.md", History("Чел2", "Я", "А")),
        };
        var res = WikiProjections.Authorship(state);
        res["Общее/Ии2.md"].Should().Be(WikiProjections.AuthorshipAi);
        res["Общее/Чел2.md"].Should().Be(WikiProjections.AuthorshipHuman, "ИИ-сноска в «Истории» не делает страницу «вместе»");
    }

    [Fact]
    public void Footnotes_CardPage_ЭтоPageKeyКарточки_ДляНеНайденной_Null()
    {
        const string card = "---\ntype: source\nsummary: к\norigin: session\nref: x:1\nhash: h\ncaptured: 2026-11-02\nstatus: active\n---\n\n# Моя заметка\n";
        const string body = "---\ntype: entity\nsummary: с\nstatus: active\n---\n\n# А\n\n## Сводка\n- Факт.[^s1] Ещё.[^s2]\n\n## Хронология\n\n## Противоречия\n\n## История\n\n[^s1]: [[Моя заметка]] — «цитата номер один»\n[^s2]: [[Нет карточки]] — «цитата номер два»\n";
        var cards = WikiProjections.Cards([new WikiGate.Draft("Общее/Источники/Моя заметка.md", card)]);
        var foots = WikiProjections.Footnotes(WikiPage.Parse(body).Model!, cards);
        foots[0].Card.Should().Be("Моя заметка", "card остаётся заголовком");
        foots[0].CardPage.Should().Be("Общее/Источники/Моя заметка.md");
        foots[1].CardPage.Should().BeNull("карточки нет");
    }
}
