using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// J-P1a (ADR-039, решение 10): чистка ИИ-страниц Хранителем — предпросмотр, zip-архив до
// удаления, удаление, журнал op=purge, гашение открытых ИИ-пакетов, сброс курсоров и флаг
// бэклога. Инициатор — человек; на боевых данных исполнитель операцию не запускает.
//
// И6 «чистка не трогает человеческое» проверяется по строке на каждый класс предиката:
// ai, together, chronicle, карточка-сирота | карточка со ссылкой выжившей страницы, human,
// приёмыш (строка op=adopt в журнале), проекция «_». И7 «откат не воскрешает удалённое» —
// в WikiRevertTests (тот же путь: сверка хэша состояния применения).
//
// Окна ставятся дисковым состоянием и фиксированным TimeProvider, а не гонкой и не паузой.
public class WikiPurgeTests : IDisposable
{
    private const string User = "u1";

    // Фикстура из 8 файлов — ровно классы таблицы решения. «Общее» — что чистится,
    // «Личное» — что остаётся: раздел без удалённых страниц не порождает ни журнала, ни
    // перегенерации, и его файлы сверяются побайтно.
    private const string AiPage = "Общее/Ии-знание.md";
    private const string TogetherPage = "Общее/Смешанное.md";
    private const string ChroniclePage = "Общее/Хроники/с1.md";
    private const string CardAiPath = "Общее/Источники/Карточка-ии.md";
    private const string CardHumanPath = "Личное/Источники/Карточка-человека.md";
    private const string HumanPage = "Личное/Моя-страница.md";
    private const string AdoptPage = "Общее/Принятая-как-есть.md";
    private const string LogMd = "Личное/_log.md"; // проекция: не страница, не трогается никогда

    // Дата, заведомо не совпадающая с часами машины: иначе мутация «взять сегодня из
    // DateTime.Now вместо TimeProvider» была бы невидима (сегодня и есть сегодня).
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly FixedToday = DateOnly.FromDateTime(FixedNow.UtcDateTime);

    private static readonly string CardAiFile = $$"""
        ---
        type: source
        origin: session
        ref: session:s1#m1-9
        hash: 0123456789012301234567890123456789012345678901234567890123456789
        captured: 2026-10-27
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Карточка-ии
        """;

    private static readonly string CardHumanFile = $$"""
        ---
        type: source
        origin: human
        ref: notes/моя-заметка.md
        hash: 1111111111111111111111111111111111111111111111111111111111111111
        captured: 2026-10-27
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Карточка-человека
        """;

    private static string AiPageFile(string claim) => $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Ии-знание

        ## Сводка
        {{claim}} [^s1]

        ## Хронология

        ## Противоречия

        ## История

        [^s1]: [[Карточка-ии]] — «{{claim}}»
        """;

    private static string TogetherPageFile(string claim) => $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Смешанное

        ## Сводка
        Ии-часть: {{claim}} [^s1]
        Человеческая часть: перенести сборку на Linux [^s2]

        ## Хронология

        ## Противоречия

        ## История

        [^s1]: [[Карточка-ии]] — «{{claim}}»
        [^s2]: [[Карточка-человека]] — «перенести сборку на Linux»
        """;

    private static readonly string ChronicleFile = """
        ---
        type: chronicle
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Хроника чата с1

        ## О чём
        Разбор окна чата.

        ## Что решили
        Идти по плану.

        ## Что выяснили
        Ничего нового.

        ## Открытые вопросы
        —
        """;

    private static readonly string HumanPageFile = """
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Моя-страница

        ## Сводка
        перенести сборку на Linux [^s1]

        ## Хронология

        ## Противоречия

        ## История

        [^s1]: [[Карточка-человека]] — «перенести сборку на Linux»
        """;

    // Приёмыш: без сносок — по авторству он был бы «ai»; держит его строка op=adopt в
    // журнале — решение человека (И6).
    private static readonly string AdoptPageFile = """
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Принятая-как-есть

        ## Сводка
        Текст, который человек принял как есть.

        ## Хронология

        ## Противоречия

        ## История
        """;

    private static readonly string LogMdFile = """
        # Журнал раздела «Личное»

        - 2026-11-01T00:00:00Z · apply · keeper · Личное/Было.md
        """;

    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly NotesService _notes;
    private readonly RecordingEventLog _events = new();
    private readonly WikiKeeper _keeper;
    private readonly WikiApplyService _apply;

    public WikiPurgeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_purge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));
        _notes = new NotesService(_projects, _config, NullLogger<NotesService>.Instance, _events, flags: new WikiFlagOn());
        // «Сегодня» чистки — по TimeProvider Хранителя (ревью J-P1c): дата флага и причина
        // гашения пакетов детерминированы тестом, а не часами машины. Шаг на секунду с каждым
        // запросом — чтобы тест гонки мог чистить несколько раз без коллизии имени архива
        // (имя секундн; две чистки одной секунды в проде — не сценарий).
        _keeper = new WikiKeeper(_notes, _config, _events, new SteppingTime(FixedNow));
        _apply = new WikiApplyService(_notes, _keeper, new NoSpheres(), _config, _events);
        _keeper.EnsureInitializedAsync(User).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Время, шагающее на секунду с каждым запросом: тесту гонки нужны разные
    /// имена архивов на каждую итерацию (имя «purge-{ts}» секундн; две чистки одной секунды
    /// в проде — не сценарий, в тесте — обязателен).</summary>
    private sealed class SteppingTime(DateTimeOffset start) : TimeProvider
    {
        private int _ticks;
        public override DateTimeOffset GetUtcNow() => start.AddSeconds(_ticks++);
    }

    private sealed class WikiFlagOn : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }

    private sealed class NoSpheres : ISphereDirectory
    {
        public string? SphereOf(string ownerId, string projectId) => null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public bool Enabled(string ownerId) => false;
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }

    private sealed class RecordingEventLog : IProjectEventLogService
    {
        public ProjectEvent? Append(string projectId, string ownerId, string type, string actor,
            string summary, string? entityRef = null, string? actorUserId = null) => null;
    }

    private string Root => WikiPaths.Root(_dir, User);

    // ─── Фикстура ────────────────────────────────────────────────────────────────────────────

    private void BuildFixture()
    {
        WriteRaw(AiPage, AiPageFile("Контенер собран на второй машине"));
        WriteRaw(TogetherPage, TogetherPageFile("Контенер собран на второй машине"));
        WriteRaw(ChroniclePage, ChronicleFile);
        WriteRaw(CardAiPath, CardAiFile);
        WriteRaw(CardHumanPath, CardHumanFile);
        WriteRaw(HumanPage, HumanPageFile);
        WriteRaw(AdoptPage, AdoptPageFile);
        WriteRaw(LogMd, LogMdFile);

        // Строка adopt — решение человека: тест пишет её как состояние диска (тот же формат,
        // что у строки adopt Хранителя); проверяется предикат чистки, а не генерация строки.
        var line = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow,
            op = "adopt",
            section = "common",
            pages = new[] { AdoptPage },
            source = (string?)null,
            actor = "user",
            policy = (string?)null,
        });
        File.AppendAllText(Path.Combine(Root, "keeper", "log.jsonl"), line + "\n", new UTF8Encoding(false));
    }

    private void WriteRaw(string pageKey, string content)
    {
        var full = Path.Combine(WikiPaths.Pages(_dir, User), pageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
    }

    /// <summary>Та же фикстура, но записанная Хранителем: каждая страница — с записью манифеста.
    /// Нужна тесту согласованности «манифест ↔ диск»: файл, записанный мимо Хранителя, — дрейф
    /// по контракту, и обратное направление сверки на нём краснеет всегда, а не на гонке.</summary>
    private async Task BuildFixtureViaKeeper()
    {
        foreach (var (pageKey, content) in new[]
        {
            (CardAiPath, CardAiFile), (CardHumanPath, CardHumanFile),
            (AiPage, AiPageFile("Контенер собран на второй машине")),
            (TogetherPage, TogetherPageFile("Контенер собран на второй машине")),
            (ChroniclePage, ChronicleFile), (HumanPage, HumanPageFile), (AdoptPage, AdoptPageFile),
        })
        {
            var section = WikiSections.Parse(pageKey)!;
            var r = await _keeper.WritePageAsync(User, section, pageKey, content);
            r.Status.Should().Be(WikiOpStatus.Applied, $"{pageKey}: {r.Reason}");
        }
        WriteRaw(LogMd, LogMdFile);
        var line = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow,
            op = "adopt",
            section = "common",
            pages = new[] { AdoptPage },
            source = (string?)null,
            actor = "user",
            policy = (string?)null,
        });
        File.AppendAllText(Path.Combine(Root, "keeper", "log.jsonl"), line + "\n", new UTF8Encoding(false));
    }

    // Снимок всего дерева вики — путь → sha256 байтов: «на диске не тронуто ничего»
    // проверяется по файлам, а не по признаку «ничего не написали».
    private Dictionary<string, string> SnapshotTree()
    {
        var snap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            snap[Path.GetRelativePath(Root, f).Replace('\\', '/')] = Sha(File.ReadAllBytes(f));
        return snap;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private List<string> LogLines()
    {
        var p = Path.Combine(Root, "keeper", "log.jsonl");
        return File.Exists(p) ? File.ReadAllLines(p).Where(l => l.Length > 0).ToList() : [];
    }

    private static readonly string[] Doomed = [AiPage, TogetherPage, ChroniclePage, CardAiPath];
    private static readonly string[] Kept = [CardHumanPath, HumanPage, AdoptPage, LogMd];

    // ─── c1. Предикат по классам: dryRun — ровно ИИ-классы; запуск — они удалены, прочие целы ───

    [Fact]
    public async Task Чистка_ПредикатПоКлассам_DryRunРовноИиСтраницыХроникаИСирота_ЗапускУдаляетИхПрочиеПобайтноЦелы()
    {
        // Мутация «не трогать together» (убрать together из предиката): together не в Items →
        // BeEquivalentTo краснеет. Мутация «без приёмышей» (убрать проверку adopt): приёмыш
        // в Items → краснеет. Мутация «ссылки до удаления» (не исключать doomed из чтения
        // ссылок): карточка-ии не сирота → BeEquivalentTo краснеет.
        BuildFixture();
        var before = SnapshotTree();

        var preview = await _keeper.PurgeAiPagesAsync(User, dryRun: true);

        preview.Ok.Should().BeTrue(preview.Reason);
        preview.DryRun.Should().BeTrue();
        preview.Items.Select(i => i.PageKey).Should().BeEquivalentTo(Doomed,
            "ровно ИИ-страница, смешанная, хроника и карточка-сирота — ничего человеческого");
        preview.Items.Single(i => i.PageKey == AiPage).Class.Should().Be(WikiProjections.AuthorshipAi);
        preview.Items.Single(i => i.PageKey == TogetherPage).Class.Should().Be(WikiProjections.AuthorshipTogether);
        preview.Items.Single(i => i.PageKey == ChroniclePage).Class.Should().Be(WikiFrontmatter.TypeChronicle);
        preview.Items.Single(i => i.PageKey == CardAiPath).Class.Should().Be(WikiFrontmatter.TypeSource);
        preview.CountsBySection["common"].Should().Equal(new Dictionary<string, int>
        {
            [WikiProjections.AuthorshipAi] = 1,
            [WikiProjections.AuthorshipTogether] = 1,
            [WikiFrontmatter.TypeChronicle] = 1,
            [WikiFrontmatter.TypeSource] = 1,
        }, "числа по разделам и классам — то, что показывает предпросмотр");
        preview.ArchivePath.Should().BeNull("предпросмотр не создаёт архив");

        var res = await _keeper.PurgeAiPagesAsync(User, dryRun: false);
        res.Ok.Should().BeTrue(res.Reason);

        foreach (var page in Doomed)
            File.Exists(Path.Combine(WikiPaths.Pages(_dir, User), page)).Should().BeFalse($"«{page}» — класс чистки, файл удалён");
        foreach (var page in Kept)
            Sha(File.ReadAllBytes(Path.Combine(WikiPaths.Pages(_dir, User), page)))
                .Should().Be(before["pages/" + page], $"«{page}» побайтно цел: человеческое и служебное чистка не трогает (И6)");

        // Журнал: строка op=purge, actor=user, список путей затронутых страниц (раздел «Общее»;
        // «Личное» не тронуто — строки по нему нет).
        var purgeLine = LogLines().Single(l => l.Contains("\"op\":\"purge\""));
        purgeLine.Should().Contain("\"actor\":\"user\"");
        foreach (var page in Doomed) purgeLine.Should().Contain(page);
        purgeLine.Should().NotContain(HumanPage);
        LogLines().Count(l => l.Contains("\"op\":\"purge\"")).Should().Be(1, "по одной строке на затронутый раздел");

        // _log.md затронутого раздела — производная журнала: строка чистки в нём (порядок:
        // журнал до проекций, тот же, что у adopt/revert).
        File.ReadAllText(Path.Combine(WikiPaths.Pages(_dir, User), "Общее/_log.md")).Should().Contain("purge");
    }

    // ─── c2. dryRun: ни одного байта на диске, архива нет ───────────────────────────────────

    [Fact]
    public async Task ЧисткаDryRun_ХешиВсегоДереваВикиРавны_ArхивногоКаталогаНет()
    {
        // Мутация «dryRun пишет журнал» (не возвращаться до записи log.jsonl): хэш log.jsonl
        // расходится → Equal краснеет. Мутация «dryRun делает архив» → BeFalse краснеет.
        BuildFixture();
        // Доводка ленивого backfill — существующий механизм входа в замок раздела, не чистка:
        // без неё первый же вход оставил бы маркеры основ, и тест мерил бы их, а не dryRun.
        await _keeper.WithSectionsLockedAsync(User, ["common", "personal"], () => Task.FromResult(0));
        var before = SnapshotTree();

        var res = await _keeper.PurgeAiPagesAsync(User, dryRun: true);

        res.Ok.Should().BeTrue(res.Reason);
        res.Items.Should().HaveCount(4);
        SnapshotTree().Should().Equal(before, "предпросмотр: на диск не написал ни байта");
        Directory.Exists(Path.Combine(Root, "keeper", "archive")).Should().BeFalse("архива при dryRun нет");
    }

    // ─── c3. Архив до удаления и содержит всё дерево; отказ архива — 0 файлов, код, без исключения ───

    [Fact]
    public async Task ЧисткаArхивСозданДоУдаленияИСодержитВсеФайлы_КаталогArхиваВArхивеНет()
    {
        // Мутация «удалять до архива» (переставить цикл удаления перед архивом): записей
        // удалённых страниц в zip нет → Contain краснеет.
        BuildFixture();
        var res = await _keeper.PurgeAiPagesAsync(User, dryRun: false);
        res.Ok.Should().BeTrue(res.Reason);
        res.ArchivePath.Should().NotBeNull();

        var zipPath = Path.Combine(Root, res.ArchivePath!);
        File.Exists(zipPath).Should().BeTrue();
        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        foreach (var page in Doomed.Concat(Kept))
            names.Should().Contain("pages/" + page, "архив — снимок ДО удаления: все восемь файлов в нём");
        names.Should().Contain("pages/" + WikiPaths.SchemaFileName);
        names.Should().NotContain(n => n.StartsWith("keeper/archive/"), "свой каталог архивов в архив не входит");
    }

    [Fact]
    public async Task ЧисткаОтказArхива_УдаленоНольФайловИсходКодом_ИсключенияНаружуНет()
    {
        // Мутация «удалять до архива»: при несозданном архиве файлы уже удалены →
        // «побайтно равны исходным» краснеет — именно это и проверяет критерий.
        BuildFixture();
        // Доводка ленивого backfill (тот же приём, что в тесте dryRun): маркеры основ — след
        // первого входа в замок раздела, а не след чистки; без доводки сравнение меряло бы их.
        await _keeper.WithSectionsLockedAsync(User, ["common", "personal"], () => Task.FromResult(0));
        var before = SnapshotTree();
        // Симуляция отказа: имя архива занято файлом → CreateNew не создаёт (тот же путь,
        // что у кода: tsFixed — по фиксированному TimeProvider Хранителя).
        var tsFixed = FixedNow.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var blocked = Path.Combine(Root, "keeper", "archive");
        Directory.CreateDirectory(blocked);
        File.WriteAllText(Path.Combine(blocked, $"purge-{tsFixed}.zip"), "занято", new UTF8Encoding(false));

        var res = await _keeper.PurgeAiPagesAsync(User, dryRun: false);

        res.Ok.Should().BeFalse();
        res.Code.Should().Be(WikiKeeper.PurgeCodeArchive);
        res.Reason.Should().Contain("архив");
        res.Items.Should().BeEmpty("отказ — состава в исходе нет: операция не сделалась");
        var after = SnapshotTree();
        foreach (var (path, hash) in before)
            after.Should().ContainKey(path, $"«{path}» — при отказе архива файл не удалён");
        after.Where(kv => !kv.Key.StartsWith("keeper/archive"))
            .Should().Equal(before.Where(kv => !kv.Key.StartsWith("keeper/archive")),
                "ничего не удалено и не записано: страницы побайтно равны исходным");
        LogLines().Should().NotContain(l => l.Contains("\"op\":\"purge\""), "несостоявшаяся чистка журнал не пишет");
        // Перезапуск разбора не звался: ни флага, ни гашения пакетов при отказе.
        WikiBacklogFlag.IsActive(Root, FixedToday).Should().BeFalse();
    }

    // ─── R-P1a M1. Принятая как есть карточка переживает чистку: сиротский тест её не тронет ──

    [Fact]
    public async Task Чистка_ПринятаяКакЕстьКарточкаНеВСоставеИОстаетсяНаДиске_СиротскийТестЕеПропускает()
    {
        // Мутация «вернуть continue для source до проверки adopt» (исходный порядок и без
        // guard'а в сиротском тесте): карточка уходит в состав → «NotContain» и «файл на
        // месте» краснеют. Мутация «убрать adopted из сиротского теста» (один guard): тот
        // же исход — сиротство без adopt-защиты.
        BuildFixture();
        // Человек принимает карточку источника как есть: строка op=adopt по пути карточки —
        // тот же формат журнала, что у строки страницы. На карточку до чистки ссылалась только
        // удаляемая ИИ-страница: без приёмыша она стала бы сиротой.
        var line = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow,
            op = "adopt",
            section = "common",
            pages = new[] { CardAiPath },
            source = (string?)null,
            actor = "user",
            policy = (string?)null,
        });
        File.AppendAllText(Path.Combine(Root, "keeper", "log.jsonl"), line + "\n", new UTF8Encoding(false));

        var preview = await _keeper.PurgeAiPagesAsync(User, dryRun: true);
        preview.Ok.Should().BeTrue(preview.Reason);
        preview.Items.Select(i => i.PageKey).Should().NotContain(CardAiPath,
            "карточка принята как есть: решение человека сильнее сиротства (И6)");

        var res = await _keeper.PurgeAiPagesAsync(User, dryRun: false);
        res.Ok.Should().BeTrue(res.Reason);
        res.Items.Select(i => i.PageKey).Should().NotContain(CardAiPath);

        // Остальной состав — как был: защита карточки не меняет прочие классы.
        res.Items.Select(i => i.PageKey).Should().BeEquivalentTo(Doomed.Except([CardAiPath]));

        var cardFull = Path.Combine(WikiPaths.Pages(_dir, User), CardAiPath);
        File.Exists(cardFull).Should().BeTrue("принятая карточка переживает чистку");
        foreach (var page in Doomed.Except([CardAiPath]))
            File.Exists(Path.Combine(WikiPaths.Pages(_dir, User), page)).Should().BeFalse($"«{page}» — класс чистки, файл удалён");

        // Журнал: карточки в строке чистки нет — её не удаляли.
        var purgeLine = LogLines().Single(l => l.Contains("\"op\":\"purge\""));
        purgeLine.Should().NotContain(CardAiPath, "несостоявшееся удаление не записывается в журнал");
    }

    // ─── R-P1a M2. Частичный сбой удаления: манифест согласован с диском, отказ — с фактическим составом ──

    [Fact]
    public async Task ЧисткаЧастичныйСбойУдаления_ИсходКодомБезИсключения_МанифестСогласенСДиском_ВОтказеФактическиУдалённые()
    {
        // Мутация «убрать починку манифеста» (возвращать отказ без RollbackTo по удалённым):
        // записи удалённых файлов остаются в манифеста → «записи манифеста без файла — нет»
        // краснеет. Мутация «молчать о фактическом составе» (возвращать PurgeFail с пустым
        // Items): «Items равны фактически удалённым» краснеет.
        await BuildFixtureViaKeeper(); // манифест с записями — сверка «манифест ↔ диск» без них не меряется

        // Последняя по сортировке (Ordinal) страница состава — «Общее/Хроники/с1.md»: на её
        // удалении процесс и прерывается, первые три (по порядку) успевают удалиться.
        // Блокировка — состоянием диска, без шва в коде: на Windows файл read-only
        // (File.Delete отказывает по атрибуту), на Unix — каталог-родитель без права записи
        // (unlink по EACCES). Тот же приём, что у отказа архива: недоступный файл.
        var blockedFull = Path.Combine(WikiPaths.Pages(_dir, User), ChroniclePage);
        var blockedDir = Path.GetDirectoryName(blockedFull)!;
        if (OperatingSystem.IsWindows()) File.SetAttributes(blockedFull, FileAttributes.ReadOnly);
        else File.SetUnixFileMode(blockedDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        WikiPurgeResult res;
        try
        {
            res = await _keeper.PurgeAiPagesAsync(User, dryRun: false);
        }
        finally
        {
            // Права возвращаем сразу: и assertions, и Dispose (уборка дерева) не должны
            // спотыкаться о заблокированный каталог.
            if (OperatingSystem.IsWindows()) File.SetAttributes(blockedFull, FileAttributes.Normal);
            else File.SetUnixFileMode(blockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        res.Ok.Should().BeFalse();
        res.Code.Should().Be(WikiKeeper.PurgeCodeIo);
        res.Reason.Should().Contain("прервано");

        // Фактический состав в отказе: ровно удалённые (первые по сортировке), без блокатора.
        var actuallyDeleted = new[] { AiPage, CardAiPath, TogetherPage };
        res.Items.Select(i => i.PageKey).Should().BeEquivalentTo(actuallyDeleted,
            "отказ называет фактическое удаление, а не задуманный состав");
        res.CountsBySection["common"].Should().Equal(new Dictionary<string, int>
        {
            [WikiProjections.AuthorshipAi] = 1,
            [WikiProjections.AuthorshipTogether] = 1,
            [WikiFrontmatter.TypeSource] = 1,
        });
        res.ArchivePath.Should().NotBeNull("архив создан до удаления — он на диске");
        File.Exists(Path.Combine(Root, res.ArchivePath!)).Should().BeTrue();

        foreach (var page in actuallyDeleted)
            File.Exists(Path.Combine(WikiPaths.Pages(_dir, User), page)).Should().BeFalse($"«{page}» удалён до сбоя");
        File.Exists(blockedFull).Should().BeTrue("сбой — на последней по сортировке: она цела");

        // Манифест согласован с диском в обе стороны — инвариант «после любого исхода»:
        // ни записи без файла, ни файла без записи (проекции — вне манифеста по контракту).
        var manifest = WikiManifest.TryRead(Root, "common");
        manifest.State.Should().Be(WikiManifestState.Valid);
        var pagesDir = WikiPaths.Pages(_dir, User);
        foreach (var (page, hash) in manifest.Manifest)
        {
            var full = Path.Combine(pagesDir, page);
            File.Exists(full).Should().BeTrue($"запись манифеста «{page}» без файла — починка не сделана");
            Sha(File.ReadAllBytes(full)).Should().Be(hash, $"хэш манифеста «{page}» не по диску");
        }
        var diskFiles = Directory.EnumerateFiles(Path.Combine(pagesDir, "Общее"), "*.md", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(pagesDir, f).Replace('\\', '/'))
            .Where(p => !WikiPaths.IsProjection(p))
            .ToHashSet();
        var manifestKeys = manifest.Manifest.Keys.ToHashSet();
        foreach (var rel in diskFiles)
            manifestKeys.Should().Contain(rel, $"файл «{rel}» без записи манифеста");

        // Полчистки — не чистка: журнал и перезапуск разбора за ней не стоят.
        LogLines().Should().NotContain(l => l.Contains("\"op\":\"purge\""), "наполовину сделанная операция журнал не пишет");
        WikiBacklogFlag.IsActive(Root, FixedToday).Should().BeFalse();
    }

    // ─── c5. Гашение открытых ИИ-пакетов, применённые и human не тронуты; курсоры и флаг ────

    private static WikiPackage.Operation Op(string op, string page, string? text,
        string decision = WikiPackage.DecisionAuto, string? reason = null) => new()
    {
        Op = op,
        Page = page,
        Text = text,
        Target = op == WikiPackage.OpAppend ? "что-то" : null,
        Class = WikiPackage.ClassKey("session", op),
        Decision = decision,
        Reason = reason,
    };

    private string NewPackage(string status, string author = WikiPackage.AuthorAi)
    {
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Author = author,
            Section = "common",
            Sources = [new WikiPackage.SourceRef("session", "chat-1")],
            Operations = [Op(WikiPackage.OpAppend, "Общее/Заявка.md", "Контенер собран на второй машине",
                decision: WikiPackage.DecisionInbox, reason: "спорный класс")],
            Status = WikiPackage.StatusChecking,
        };
        if (author == WikiPackage.AuthorHuman)
        {
            // Пакет человека рождается сразу в accepted (CreateHuman).
            var human = pkg with { Status = WikiPackage.StatusAccepted, Action = WikiPackage.ActionEdit };
            WikiPackageStore.CreateHuman(Root, human).Ok.Should().BeTrue();
            return human.Id;
        }
        WikiPackageStore.Create(Root, pkg).Ok.Should().BeTrue();
        // Доведение статуса — только допустимыми переходами таблицы.
        var chain = new List<string> { WikiPackage.StatusProposed };
        if (status is WikiPackage.StatusAccepted or WikiPackage.StatusApplying or WikiPackage.StatusApplied)
            chain.Add(WikiPackage.StatusAccepted);
        if (status is WikiPackage.StatusApplying or WikiPackage.StatusApplied)
            chain.Add(WikiPackage.StatusApplying);
        if (status == WikiPackage.StatusApplied)
            chain.Add(WikiPackage.StatusApplied);
        foreach (var step in chain)
            WikiPackageStore.Transition(Root, pkg.Id, step).Ok.Should().BeTrue(step);
        return pkg.Id;
    }

    private (string Status, string? Reason) ReadStatus(string id)
    {
        var read = WikiPackageStore.TryRead(Root, id);
        read.State.Should().Be(WikiPackageFileState.Valid);
        return (read.Package!.Status, read.Package!.StatusReason);
    }

    [Fact]
    public async Task Чистка_ОткрытыеAiПакетыStaleСПриЧинной_ПрименённыйИHumanНеТронуты_КурсорыНуль_ФлагНа14Дней()
    {
        // Мутация «не гасить accepted» (убрать accepted из IsStaleableByPurge): StaledPackages=1
        // и статус accepted → краснеют. Мутация «гасить применённый» (добавить applied):
        // «applied unchanged» краснеет. Мутация «флаг без TimeProvider» (DateTime.Now вместо
        // _time): IsActive(FixedToday+14) краснеет (дата не та).
        BuildFixture();
        var proposed = NewPackage(WikiPackage.StatusProposed);
        var accepted = NewPackage(WikiPackage.StatusAccepted);
        var appliedId = NewPackage(WikiPackage.StatusApplied);
        var humanId = NewPackage(WikiPackage.StatusAccepted, author: WikiPackage.AuthorHuman);

        // Запись стора разбора с ненулевым курсором — то, что чистка обязана обнулить (J-P1b).
        var triage = WikiChatTriageStore.Upsert(Root, new WikiChatTriageEntry(
            "s1", 7, "hash-7", "core", "окно", 1, DateTimeOffset.UtcNow, Score: 42));
        triage.Ok.Should().BeTrue();

        var res = await _keeper.PurgeAiPagesAsync(User, dryRun: false);
        res.Ok.Should().BeTrue(res.Reason);

        res.StaledPackages.Should().Be(2, "proposed и accepted автора ai — открытые и неприменённые");
        ReadStatus(proposed).Status.Should().Be(WikiPackage.StatusStale);
        ReadStatus(proposed).Reason.Should().Contain("чистка вики");
        ReadStatus(accepted).Status.Should().Be(WikiPackage.StatusStale);
        ReadStatus(accepted).Reason.Should().Contain("чистка вики");

        // Применённый не трогается: его откат — кнопка человека, не чистка (п.7).
        ReadStatus(appliedId).Status.Should().Be(WikiPackage.StatusApplied);
        // Пакет автора human — решение человека: чистка его не гасит.
        ReadStatus(humanId).Status.Should().Be(WikiPackage.StatusAccepted);

        // Сброс курсоров: запись на месте, поля разбора обнулены, балл и ступени целы.
        var entries = WikiChatTriageStore.Get(Root).Entries;
        var entry = entries.Single(e => e.SessionId == "s1");
        entry.Cursor.Should().Be(0);
        entry.CursorHash.Should().BeNull();
        entry.NothingNewStreak.Should().Be(0);
        entry.Score.Should().Be(42, "пересчёт не пересчитывается");
        res.TriageResetCount.Should().Be(1);

        // Флаг бэклога: сегодня+14 — последний день флага (граница включительно), +15 — нет.
        WikiBacklogFlag.IsActive(Root, FixedToday).Should().BeTrue();
        WikiBacklogFlag.IsActive(Root, FixedToday.AddDays(WikiKeeper.PurgeBacklogDays)).Should().BeTrue();
        WikiBacklogFlag.IsActive(Root, FixedToday.AddDays(WikiKeeper.PurgeBacklogDays + 1)).Should().BeFalse();
    }

    [Fact]
    public async Task ЧисткаDryRun_ПерезапускНеЗывается_ПакетыКурсорыИФлагКакБыли()
    {
        // Мутация «перезапуск и при dryRun» (вызвать Stale/Reset/Set до возврата предпросмотра):
        // статус proposed, курсор 7 и IsActive краснеют.
        BuildFixture();
        var proposed = NewPackage(WikiPackage.StatusProposed);
        var triage = WikiChatTriageStore.Upsert(Root, new WikiChatTriageEntry(
            "s1", 7, "hash-7", "core", "окно", 1, DateTimeOffset.UtcNow));

        var res = await _keeper.PurgeAiPagesAsync(User, dryRun: true);

        res.Ok.Should().BeTrue(res.Reason);
        res.Items.Should().HaveCount(4);
        ReadStatus(proposed).Status.Should().Be(WikiPackage.StatusProposed);
        WikiChatTriageStore.Get(Root).Entries.Single().Cursor.Should().Be(7);
        WikiBacklogFlag.IsActive(Root, FixedToday).Should().BeFalse();
    }

    // ─── Конкурентность: чистка ‖ применение в тот же раздел ────────────────────────────────

    [Fact]
    public async Task ЧисткаГонкаСПрименениемТогоЖераздела_БезИсключенийМанифестСогласенСДиском()
    {
        // 20 итераций гонки: ровно один из двух порядков допустим (apply ждёт все замки
        // чистки), ни один не даёт исключения и ни один не оставляет манифест не по диску.
        // Без замка это окно держится только вероятностью пересечения записи и чтения —
        // поэтому тест меряет инвариант на живой конкурентности, а не на одной гарантированной
        // мутации; детерминированные мутации чистки — в тестах предиката и архива.
        await BuildFixtureViaKeeper();
        for (var i = 0; i < 20; i++)
        {
            // Предмет чистки этой итерации — ИИ-страница, записанная мимо Хранителя (сноска на
            // несуществующую карточку — авторство «ai»): иначе после первой итерации чистить
            // было бы нечего и гонка превращалась бы в односторонний прогон.
            WriteRaw($"Общее/Чужое-{i}.md", AiPageFile($"Контенер собран на второй машине {i}"));
            // Пакет на новый create-файл: ни base-сверки, ни пересечения с файлами фикстуры.
            var pageKey = $"Общее/Гонка-{i}.md";
            var pkg = new WikiPackage
            {
                Id = WikiPackageStore.NewId(),
                Section = "common",
                Sources = [new WikiPackage.SourceRef("session", "chat-1")],
                Operations = [Op(WikiPackage.OpCreate, pageKey, "Контенер собран на второй машине")],
                Status = WikiPackage.StatusChecking,
            };
            WikiPackageStore.Create(Root, pkg).Ok.Should().BeTrue();
            foreach (var step in new[] { WikiPackage.StatusProposed, WikiPackage.StatusAccepted })
                WikiPackageStore.Transition(Root, pkg.Id, step).Ok.Should().BeTrue(step);
            var onDisk = WikiPackageStore.TryRead(Root, pkg.Id).Package!;

            // apply — на живой карточке, которую чистка не сносит никогда (на неё ссылается
            // человеческая страница): иначе после первой итерации применять было бы не с чем
            // и гонка превращалась бы в односторонний прогон.
            var applyTask = Task.Run(() => _apply.ApplyAsync(User, onDisk, new WikiApplyService.ApplyContext("Карточка-человека", "Контенер собран на второй машине")));
            var purgeTask = Task.Run(() => _keeper.PurgeAiPagesAsync(User, dryRun: false));
            var apply = await applyTask.WaitAsync(TimeSpan.FromSeconds(30));
            var purge = await purgeTask.WaitAsync(TimeSpan.FromSeconds(30));

            // Исключения наружу нет ни у одного из участников гонки; исход apply — применение
            // или отказ статусом (пакет погашен чисткой до заявки) — оба законны.
            purge.Ok.Should().BeTrue($"итерация {i}: {purge.Reason}");

            // Манифест согласован с диском: каждой записи — файл с тем же хэшем, каждого
            // файла раздела — запись с тем же хэшем (проекции — вне манифеста по контракту).
            var manifest = WikiManifest.TryRead(Root, "common");
            manifest.State.Should().Be(WikiManifestState.Valid);
            // Пути — относительно pages/, в той же форме, что ключи манифеста.
            var pagesDir = WikiPaths.Pages(_dir, User);
            var diskFiles = Directory.EnumerateFiles(Path.Combine(pagesDir, "Общее"), "*.md", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(pagesDir, f).Replace('\\', '/'))
                .Where(p => !WikiPaths.IsProjection(p))
                .ToHashSet();
            foreach (var (page, hash) in manifest.Manifest)
            {
                var full = Path.Combine(WikiPaths.Pages(_dir, User), page);
                File.Exists(full).Should().BeTrue($"итерация {i}: запись манифеста «{page}» без файла");
                Sha(File.ReadAllBytes(full)).Should().Be(hash, $"итерация {i}: хэш манифеста «{page}» не по диску");
            }
            var manifestKeys = manifest.Manifest.Keys.ToHashSet();
            foreach (var rel in diskFiles)
                manifestKeys.Should().Contain(rel, $"итерация {i}: файл «{rel}» без записи манифеста");
        }
    }

    // ─── Пустая вики и отсутствующая вики: нулевой исход без архива и без исключения ────────

    [Fact]
    public async Task ЧисткаПустойИлиНезаведённойVики_НулевойУспехБезArхиваБезИсключения()
    {
        BuildFixture();
        foreach (var page in Doomed.Concat(Kept))
            File.Delete(Path.Combine(WikiPaths.Pages(_dir, User), page)); // страниц нет, keeper/ остаётся: чистка не трогает историю

        var empty = await _keeper.PurgeAiPagesAsync(User, dryRun: false);
        empty.Ok.Should().BeTrue();
        empty.Items.Should().BeEmpty();
        empty.ArchivePath.Should().BeNull("нечего архивировать, когда нечего удалять");
        WikiBacklogFlag.IsActive(Root, FixedToday).Should().BeFalse("пустая чистка — не чистка: бэклога она не поднимает");

        var other = Path.Combine(Path.GetTempPath(), "wiki_purge_none_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            var none = await _keeper.PurgeAiPagesAsync("u9", dryRun: false);
            none.Ok.Should().BeTrue();
            none.Items.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }
}
