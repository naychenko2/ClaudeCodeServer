using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Tests.Helpers;

namespace ClaudeHomeServer.Tests.Services;

// S2b-2 (ADR-035, решения 2, 4, 6, 10): Хранитель — единственный писатель pages/.
// Дрейф: перед записью байты файла на диске сверяются с манифестом раздела; расхождение —
// статус Drift и отказ писать (внешняя правка не перезаписывается никогда), снимает его
// только adopt. Манифест Invalid — отказ, а не «пустая карта». Блокировки — по паре
// (owner, section), две берутся в каноническом порядке ключей.
//
// Вызов WithSectionsLockedAsync идёт синхронно до первого ОЖИДАЕМОГО ожидания: если
// свободный семафор отдаётся сразу, продолжение не отдаётся управлению. Поэтому к строке,
// следующей за вызовом, состояние блоков определено: это и делает тест порядка блоков
// детерминистичным (без этой опоры «встречные операции» были бы тестом на везение).
public class WikiKeeperTests : IDisposable
{
    private const string User = "u1";
    private readonly string _dir;
    private readonly NotesService _notes;
    private readonly RecordingEventLog _events = new();
    private readonly WikiKeeper _sut;

    public WikiKeeperTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_keeper_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(config, users, new AppSettingsService(config));
        // Флаг llm-wiki включён: «граф видит страницу сразу» (решение 4, шаг 6) — свойство
        // графа с включённым флагом; без него источник wiki не отдаётся вовсе (хвост ревью S2e,
        // проверка — в NotesWikiSourceTests).
        _notes = new NotesService(projects, config, NullLogger<NotesService>.Instance, _events, new WikiFlagOn());
        _sut = new WikiKeeper(_notes, config, _events);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string PageFull(string pageKey) => Path.Combine(WikiPaths.Pages(_dir, User), pageKey);
    // WikiPaths.Keeper уже указывает на keeper/ — второй «keeper» в пути быть не должно.
    private string ManifestFile(string sectionKey) =>
        Path.Combine(WikiPaths.Keeper(_dir, User), "manifest", WikiManifest.SafeFileName(sectionKey) + ".json");
    private WikiManifestReadResult Manifest(string sectionKey) =>
        WikiManifest.TryRead(WikiPaths.Root(_dir, User), sectionKey);

    // Ожидаемый хэш считается независимо от проверяемого кода: тест через Sha256OfBytes
    // класса не отличил бы неверный хэш.
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private List<JsonElement> LogLines()
    {
        var path = Path.Combine(WikiPaths.Keeper(_dir, User), "log.jsonl");
        File.Exists(path).Should().BeTrue("журнал Хранителя должен быть записан");
        return [.. File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l)).Select(d => d.RootElement)];
    }

    // ─── 1. «Завести вики» ───────────────────────────────────────────────────

    [Fact]
    public async Task ЗавестиВики_СоздаётСхемуИКаталогиKeeper_ПовторныйВызовПравленнуюСхемуНеМеняет()
    {
        await _sut.EnsureInitializedAsync(User);

        var schema = PageFull(WikiPaths.SchemaFileName);
        File.ReadAllText(schema).Should().StartWith("# Схема вики");
        foreach (var dir in new[] { "manifest", "packages", "preimages" })
            Directory.Exists(Path.Combine(WikiPaths.Keeper(_dir, User), dir)).Should().BeTrue();

        // Человек правил схему (операция schema, решение 6): повторный «Завести» не
        // возвращает файл к шаблону — ни одного изменения.
        File.WriteAllText(schema, "# Схема вики\n\nправка человека.", new UTF8Encoding(false));
        var afterHumanEdit = Sha(File.ReadAllBytes(schema));

        await _sut.EnsureInitializedAsync(User);

        Sha(File.ReadAllBytes(schema)).Should().Be(afterHumanEdit);
        // Разделы при этом не заведены: их заводит первая применённая операция (решение 1).
        Directory.Exists(Path.Combine(WikiPaths.Pages(_dir, User), "Проекты")).Should().BeFalse();
    }

    [Fact]
    public async Task ДваПараллельныхЗавестиВики_ОбаБезИсключения_СхемаЦела_ПовторНеМеняетЕё()
    {
        // Проба MAJOR ревью S2b-2: без уникального temp и без блокировки инициализации
        // вторая «Завести» толкается с первой в одном temp-имени — её Move падает, а уборка
        // сносит temp победителя. Оба вызова стартаются от одного барьера (без Task.Delay):
        // с фиксом второй ждёт блокировку и проходит по «файл уже есть» — всегда зелено;
        // с мутацией (постоянное имя temp + снятая блокировка) хотя бы один бросает — красный.
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () => { await barrier.Task; await _sut.EnsureInitializedAsync(User); });
        var second = Task.Run(async () => { await barrier.Task; await _sut.EnsureInitializedAsync(User); });
        barrier.SetResult();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10)); // ни один не бросил

        var schema = PageFull(WikiPaths.SchemaFileName);
        File.Exists(schema).Should().BeTrue("гонка не должна оставить вики незаведённой");
        File.ReadAllText(schema).Should().StartWith("# Схема вики");
        var schemaHash = Sha(File.ReadAllBytes(schema));

        // Ровно одна запись шаблона: повторный вызов не меняет байт.
        await _sut.EnsureInitializedAsync(User);
        Sha(File.ReadAllBytes(schema)).Should().Be(schemaHash);

        // Ни temp-мусора вокруг цели — в любом исходе (тот же инвариант записи).
        Directory.EnumerateFiles(Path.GetDirectoryName(schema)!).Where(f => f.EndsWith(".tmp"))
            .Should().BeEmpty("атомарная запись не оставляет *.tmp");
    }

    // ─── 2. Запись: манифест и граф ──────────────────────────────────────────

    [Fact]
    public async Task Запись_МанифестРавенХэшуБайтФайлаНаДиске_ИГрафВидитСтраницуСразу()
    {
        await _sut.EnsureInitializedAsync(User);
        const string key = "Проекты/p1/Знание.md";
        const string content = "# Знание\n\nПервая версия.";

        // Кэш графа тёплый: без инвалидации страница появилась бы только по истечении TTL.
        _notes.GetSummaries(User, null, null).Select(n => n.Title).Should().NotContain("Знание");

        var res = await _sut.WritePageAsync(User, "projects/p1", key, content);
        res.Status.Should().Be(WikiOpStatus.Applied);

        var bytes = File.ReadAllBytes(PageFull(key));
        Manifest("projects/p1").State.Should().Be(WikiManifestState.Valid);
        Manifest("projects/p1").Manifest[key].Should().Be(Sha(bytes),
            "в манифест кладётся sha256 байтов файла, прочитанных с диска");

        _notes.GetSummaries(User, null, null).Select(n => n.Title).Should().Contain("Знание",
            "после записи Хранителя граф обязан видеть страницу сразу (решение 4, шаг 6)");
    }

    // ─── 3. Дрейф и adopt (решение 2, решение 6) ─────────────────────────────

    [Fact]
    public async Task ПравкаМимоХранителя_Дрифт_ФайлЦел_ЗатемAdoptСнимаетДрифт_ССтрокойAdoptВЖурнале()
    {
        await _sut.EnsureInitializedAsync(User);
        const string key = "Проекты/p1/Знание.md";
        var full = PageFull(key);

        (await _sut.WritePageAsync(User, "projects/p1", key, "# Знание\n\nХранитель.")).Status
            .Should().Be(WikiOpStatus.Applied);

        // Внешняя правка мимо Хранителя: человек или Obsidian.
        File.WriteAllText(full, "# Знание\n\nРучная правка человека.", new UTF8Encoding(false));

        var res = await _sut.WritePageAsync(User, "projects/p1", key, "# Знание\n\nПопытка Хранителя.");
        res.Status.Should().Be(WikiOpStatus.Drift);
        res.Reason.Should().NotBeNullOrWhiteSpace("причина — в результате вызова");
        File.ReadAllText(full).Should().Be("# Знание\n\nРучная правка человека.",
            "внешняя правка не перезаписывается никогда (решение 2)");
        // Манифест не тронут: «не применил» не должно выглядеть как «применил».
        Manifest("projects/p1").Manifest[key].Should().NotBe(Sha(File.ReadAllBytes(full)));

        // «Принять как есть»: файл не трогается, дрейф снимается, журнал получает строку.
        var adopted = await _sut.AdoptAsync(User, "projects/p1", key);
        adopted.Status.Should().Be(WikiOpStatus.Applied);
        File.ReadAllText(full).Should().Be("# Знание\n\nРучная правка человека.");
        Manifest("projects/p1").Manifest[key].Should().Be(Sha(File.ReadAllBytes(full)));

        var line = LogLines().Single(l => l.GetProperty("op").GetString() == "adopt");
        line.GetProperty("actor").GetString().Should().Be("user", "adopt — действие человека (решение 6)");
        line.GetProperty("section").GetString().Should().Be("projects/p1");
        line.GetProperty("pages")[0].GetString().Should().Be(key);

        // Дрейфа больше нет: обычная запись проходит.
        (await _sut.WritePageAsync(User, "projects/p1", key, "# Знание\n\nНовая версия.")).Status
            .Should().Be(WikiOpStatus.Applied);
    }

    [Fact]
    public async Task ТаЖеСтрокаДругимиБайтами_Дрифт_СверяютсяБайтыФайлаАНеСтрока()
    {
        await _sut.EnsureInitializedAsync(User);
        const string key = "Общее/X.md";
        const string content = "# X\n\nТот же текст.";
        var full = PageFull(key);

        (await _sut.WritePageAsync(User, "common", key, content)).Status.Should().Be(WikiOpStatus.Applied);

        // Та же строка, другие байты: внешний редактор дописал BOM. Хэш строки не увидел бы
        // разницы — хэш байтов видит (манифест, baseHashes и сверка — одна форма).
        byte[] bomBytes = [.. new byte[] { 0xEF, 0xBB, 0xBF }, ..Encoding.UTF8.GetBytes(content)];
        File.WriteAllBytes(full, bomBytes);

        var res = await _sut.WritePageAsync(User, "common", key, content);
        res.Status.Should().Be(WikiOpStatus.Drift);
        File.ReadAllBytes(full).Should().Equal(bomBytes);
    }

    [Fact]
    public async Task УдалённыйВнешнеФайл_Дрифт_АНеМолчаливоеСозданиеЗаново()
    {
        await _sut.EnsureInitializedAsync(User);
        const string key = "Общее/X.md";
        (await _sut.WritePageAsync(User, "common", key, "# X\n\nПервое.")).Status
            .Should().Be(WikiOpStatus.Applied);

        File.Delete(PageFull(key)); // удаление мимо Хранителя

        var res = await _sut.WritePageAsync(User, "common", key, "# X\n\nВторое.");
        res.Status.Should().Be(WikiOpStatus.Drift,
            "файл, удалённый внешне, не должен «восстановиться» молча: решение — за человеком");
        File.Exists(PageFull(key)).Should().BeFalse();
    }

    // ─── 4. Манифест Invalid: отказ, а не пустая карта (ревью S2b-1, M1) ─────

    [Fact]
    public async Task МанифестInvalid_ЗаписьОтказана_ФайлИМанифестНеТронуты()
    {
        await _sut.EnsureInitializedAsync(User);
        const string key = "Общее/X.md";
        (await _sut.WritePageAsync(User, "common", key, "# X\n\nПервое.")).Status
            .Should().Be(WikiOpStatus.Applied);

        File.WriteAllText(ManifestFile("common"), "{ мусор"); // манифест «пришёл с диска» испорченным

        var res = await _sut.WritePageAsync(User, "common", key, "# X\n\nВторое.");
        res.Status.Should().Be(WikiOpStatus.Refused);
        res.Reason.Should().NotBeNullOrWhiteSpace("причина отказа — в результате вызова");
        File.ReadAllText(PageFull(key)).Should().Be("# X\n\nПервое.");
        File.ReadAllText(ManifestFile("common")).Should().Be("{ мусор",
            "отказ не должен молча стирать нечитаемые записи битого манифеста");

        (await _sut.AdoptAsync(User, "common", key)).Status.Should().Be(WikiOpStatus.Refused);
    }

    // ─── 5. Контракт пары (раздел, страница) ─────────────────────────────────

    [Fact]
    public async Task СтраницаЧужогоРаздела_Отказ_ФайлНеЗаписан()
    {
        await _sut.EnsureInitializedAsync(User);

        var res = await _sut.WritePageAsync(User, "projects/p1", "Общее/X.md", "# X\n\nтекст.");

        res.Status.Should().Be(WikiOpStatus.Refused);
        File.Exists(PageFull("Общее/X.md")).Should().BeFalse(
            "иначе запись шла бы под блокировкой одного раздела, а файл относился к другому");
    }

    [Fact]
    public async Task ПутьСлужебногоФайла_ЗаписьОтказана_ФайлИМанифестНеТронуты()
    {
        // Хвост ревью S2b-3, Н2: последний сегмент на «_» (_index/_log/_health/_schema) —
        // служебный файл раздела, не страница. Без отказа запись по такому пути молча
        // перезаписала бы проекцию (её регенерирует сам Хранитель) или признак заведённости.
        await _sut.EnsureInitializedAsync(User);
        var projection = "Проекты/p1/_index.md";
        Directory.CreateDirectory(Path.GetDirectoryName(PageFull(projection))!);
        File.WriteAllText(PageFull(projection), "# индекс человека\n", new UTF8Encoding(false));

        var res = await _sut.WritePageAsync(User, "projects/p1", projection, "# подменённый индекс");

        res.Status.Should().Be(WikiOpStatus.Refused, "служебный файл раздела не страница");
        File.ReadAllText(PageFull(projection)).Should().Be("# индекс человека\n",
            "отказ не трогает файл");
        Manifest("projects/p1").Manifest.ContainsKey(projection).Should().BeFalse(
            "проекции не входят в манифест и после отказа");
        // Тот же отказ для adopt: принимать служебный файл в манифест нечего
        (await _sut.AdoptAsync(User, "projects/p1", projection)).Status.Should().Be(WikiOpStatus.Refused);
        // Обычная страница того же раздела проходит — отказ не всеобщий
        (await _sut.WritePageAsync(User, "projects/p1", "Проекты/p1/X.md", "# X\n\nтекст."))
            .Status.Should().Be(WikiOpStatus.Applied);
    }

    // ─── 6. Блокировки: разделы независимы, две — по каноническому порядку ───

    [Fact]
    public async Task ОперацияНаРазделеB_Завершается_ПокаБлокировкаРазделаAУдерживается()
    {
        await _sut.EnsureInitializedAsync(User);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holdingA = _sut.WithSectionsLockedAsync(User, ["projects/a"], async () =>
        {
            started.SetResult();
            await gate.Task;
            return true;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); // блокировка A реально удержана

        var res = await _sut.WritePageAsync(User, "projects/b", "Проекты/b/X.md", "# X\n\nтекст.")
            .WaitAsync(TimeSpan.FromSeconds(5));

        gate.SetResult();
        await holdingA;

        res.Status.Should().Be(WikiOpStatus.Applied,
            "разделы независимы: ночной разбор одного не держит ручное применение в другом (решение 10)");
    }

    [Fact]
    public async Task ВстречныеОперацииНаПаруРазделов_БерутБлокировкиПоКлючу_АНеПоАргументам()
    {
        await _sut.EnsureInitializedAsync(User);
        var gateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holdingA = _sut.WithSectionsLockedAsync(User, ["projects/a"], async () =>
        {
            startedA.SetResult();
            await gateA.Task;
            return true;
        });
        await startedA.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Операция с аргументами [B, A]. Порядок — по строке ключа, поэтому она ждёт A и
        // НЕ держит B. Мутация «порядок по аргументам»: она берёт B и ждёт A — следующая
        // запись в B не проходит, и тест красный по таймауту.
        var cross = _sut.WithSectionsLockedAsync(User, ["projects/b", "projects/a"], () => Task.FromResult(true));

        var res = await _sut.WritePageAsync(User, "projects/b", "Проекты/b/X.md", "# X\n\nтекст.")
            .WaitAsync(TimeSpan.FromSeconds(5));
        cross.IsCompleted.Should().BeFalse("встречная операция обязана ждать A, а не держать B");

        gateA.SetResult();
        await cross.WaitAsync(TimeSpan.FromSeconds(5));
        await holdingA;

        res.Status.Should().Be(WikiOpStatus.Applied);
    }

    [Fact]
    public async Task AdoptЖдётБлокировкуРаздела_ПокаЕёДержитЗапись_МанифестДоЖданияНеТронут()
    {
        // MAJOR-тест ревью S2b-2: adopt берёт ту же блокировку раздела, что и запись (решение 10).
        // Пока блок держится (здесь — внешним удержанием, тем же механизмом, что у записи),
        // adopt не выполняется и манифест не переписывает. Мутация «снять блокировку adopt»
        // (AdoptAsync без WithSectionsLockedAsync): adopt проходит сразу, IsCompleted истинно
        // и манифест обновлён под держимым блоком — тест красный.
        await _sut.EnsureInitializedAsync(User);
        const string key = "Проекты/p1/Знание.md";
        var full = PageFull(key);
        (await _sut.WritePageAsync(User, "projects/p1", key, "# Знание\n\nХранитель.")).Status
            .Should().Be(WikiOpStatus.Applied);

        // Дрейф, который призван закрыть adopt: внешняя правка, манифест от неё отличается.
        File.WriteAllText(full, "# Знание\n\nРучная правка.", new UTF8Encoding(false));
        var diskHash = Sha(File.ReadAllBytes(full));
        var manifestBefore = Manifest("projects/p1").Manifest[key];
        diskHash.Should().NotBe(manifestBefore);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = _sut.WithSectionsLockedAsync(User, ["projects/p1"], async () =>
        {
            started.SetResult();
            await gate.Task;
            return true;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); // блокировка раздела реально удержана

        var adopting = _sut.AdoptAsync(User, "projects/p1", key);
        adopting.IsCompleted.Should().BeFalse("adopt обязан ждать блокировку раздела, как запись");
        Manifest("projects/p1").Manifest[key].Should().Be(manifestBefore,
            "пока блок держится, adopt не переписывает манифест");

        gate.SetResult();
        var res = await adopting.WaitAsync(TimeSpan.FromSeconds(5));
        await holding;

        res.Status.Should().Be(WikiOpStatus.Applied);
        Manifest("projects/p1").Manifest[key].Should().Be(diskHash,
            "после ожидания adopt закрывает дрейф, как и без конкурентности");
    }

    // ─── 7. Событие журнала: честный автор, только раздел проекта ────────────

    [Fact]
    public async Task СобытиеWikiChanged_ЧестныйАвтор_ТолькоДляРазделаПроекта()
    {
        await _sut.EnsureInitializedAsync(User);
        const string key = "Проекты/p1/Знание.md";

        (await _sut.WritePageAsync(User, "projects/p1", key, "# Знание\n\nтекст.")).Status
            .Should().Be(WikiOpStatus.Applied);
        (await _sut.WritePageAsync(User, "common", "Общее/X.md", "# X\n\nтекст.")).Status
            .Should().Be(WikiOpStatus.Applied);
        await _sut.AdoptAsync(User, "projects/p1", key);

        // Ровно два события: запись Хранителем («keeper») и принятие человеком («user»).
        // «Общее» и «Личное» — без события: у них нет проекта-адресата (тот же порядок,
        // что у личных заметок в NotesService).
        _events.Appended.Select(e => (e.ProjectId, e.Type, e.Actor)).Should().Equal(
            ("p1", ProjectEventTypes.WikiChanged, "keeper"),
            ("p1", ProjectEventTypes.WikiChanged, "user"));
    }

    // Записанные вызовы IProjectEventLogService — тот же приём, что RecordingLog в
    // NotesWikiSourceTests: контракт проверяется без SQLite.
    private sealed class RecordingEventLog : IProjectEventLogService
    {
        public List<ProjectEvent> Appended { get; } = [];

        public ProjectEvent? Append(string projectId, string ownerId, string type, string actor,
            string summary, string? entityRef = null)
        {
            var e = new ProjectEvent { ProjectId = projectId, OwnerId = ownerId, Type = type, Actor = actor, Summary = summary, EntityRef = entityRef };
            Appended.Add(e);
            return e;
        }
    }

    // llm-wiki включён: тесты этого файла — о заведённой вике, чьи страницы обязаны быть
    // в графе сразу после записи Хранителя.
    private sealed class WikiFlagOn : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }
}
