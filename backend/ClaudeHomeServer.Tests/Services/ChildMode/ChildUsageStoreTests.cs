using System.Text.Json;
using ClaudeHomeServer.Services.Backup;
using ClaudeHomeServer.Services.ChildMode;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services.ChildMode;

/// <summary>
/// Журнал присутствия ребёнка (KM-B5b, разрез §3): data/child-usage/{ownerId}.json —
/// минуты по дням (yyyy-MM-dd местной даты в таймзоне вызывающего) и пространствам.
///
/// Что фиксируют тесты (критерии KM-B5b):
/// 1. разные минуты одного дня считаются по числу минут; дубль той же минуты (другая
///    секунда, повторный heartbeat) ничего не добавляет;
/// 2. «сегодня» — МЕСТНЫЙ день: 23:59 и 00:01 в одной таймзоне попадают в разные дни;
/// 3. хранятся последние 60 дней: старше — срезаются при записи, свежий день не страдает
///    от поздной записи старой минуты;
/// 4. файл на владельца и на пространство: чужие минуты и чужие пространства не видны;
/// 5. гонка: две вкладки одного ребёнка (один файл) не теряют записей друг друга и не
///    добавляют дубль; разные владельцы не мешают друг другу (20 параллельных);
/// 6. битый JSON: файл уходит в .corrupt-*.bak, журнал продолжается пустым, исключения
///    наружу нет;
/// 7. новое хранилище в data/ едет в бэкап само, служебный tmp — нет; корень — из
///    резолвера путей данных (DataPath).
/// Платформонезависимо: Path.GetTempPath()+Path.Combine, таймзоны фиксированные
/// (CreateCustomZone / Utc), без задержек и без зависимости от tzdata машины.
/// Мутация (критерий c2): снять lock (LockFor(path)) в ChildUsageStore.RecordMinute —
/// краснеет RecordMinute_ParallelSameOwnerDifferentMinutes_AllCounted (20 записей → 3).
/// RecordMinute_ParallelSameMinute_CountsOnce от этой мутации не краснеет и не может:
/// дубль той же минуты возвращается по TryAddMinute ДО всякой записи файла, то есть путь
/// дубля не имеет окна гонки вовсе. Защиту read-modify-write держит тест с разными минутами.
/// </summary>
public sealed class ChildUsageStoreTests : IDisposable
{
    private readonly string _tempDir;

    public ChildUsageStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ccs-child-usage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* временная папка */ }
        GC.SuppressFinalize(this);
    }

    private string Root => Path.Combine(_tempDir, ChildUsageStore.DirName);

    private ChildUsageStore Store() => new(Root);

    private string FileFor(string ownerId) => Path.Combine(Root, ownerId + ".json");

    // Дни считаются по таймзоне, которую передаёт вызывающий: фиксированная UTC+3 (без
    // зависимости от tzdata) и UTC — этого хватает, чтобы проверить пересчёт.
    private static readonly TimeZoneInfo Tz3 =
        TimeZoneInfo.CreateCustomTimeZone("test-utc+3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");

    private static readonly DateTimeOffset NoonUtc = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ТриРазныеМинуты_СчитаютсяТремя()
    {
        var store = Store();

        store.RecordMinute("kid-a", "lessons", NoonUtc, TimeZoneInfo.Utc).Should().BeTrue();
        store.RecordMinute("kid-a", "lessons", NoonUtc.AddMinutes(1), TimeZoneInfo.Utc).Should().BeTrue();
        store.RecordMinute("kid-a", "lessons", NoonUtc.AddMinutes(2), TimeZoneInfo.Utc).Should().BeTrue();

        store.MinutesToday("kid-a", "lessons", NoonUtc.AddMinutes(30), TimeZoneInfo.Utc).Should().Be(3);
    }

    [Fact]
    public void ДубльТойЖеМинуты_НичегоНеДобавляет()
    {
        var store = Store();
        var first = NoonUtc;   // :00:00

        store.RecordMinute("kid-a", "lessons", first, TimeZoneInfo.Utc).Should().BeTrue();
        // Другая секунда той же минуты — тот же ключ записи (округление вниз до минуты).
        store.RecordMinute("kid-a", "lessons", first.AddSeconds(59), TimeZoneInfo.Utc).Should().BeFalse();

        store.MinutesToday("kid-a", "lessons", first, TimeZoneInfo.Utc).Should().Be(1);
    }

    [Fact]
    public void СекундаРовноНаГраницеМинуты_ОтноситсяКНовойМинуте()
    {
        var store = Store();
        var lastSecond = new DateTimeOffset(2026, 11, 17, 9, 5, 59, TimeSpan.Zero);
        var nextMinute = new DateTimeOffset(2026, 11, 17, 9, 6, 0, TimeSpan.Zero);

        store.RecordMinute("kid-a", "lessons", lastSecond, TimeZoneInfo.Utc);
        store.RecordMinute("kid-a", "lessons", nextMinute, TimeZoneInfo.Utc);

        // [9:05, 9:06) и [9:06, 9:07) — две разные минуты.
        store.MinutesToday("kid-a", "lessons", nextMinute, TimeZoneInfo.Utc).Should().Be(2);
    }

    [Fact]
    public void МинутаДоИПослеПолночи_ПопадаютВРазныеДни()
    {
        var store = Store();
        // 20:59Z = 23:59 в UTC+3 — ещё день 16.11; 21:01Z = 00:01 — уже день 17.11.
        var beforeMidnight = new DateTimeOffset(2026, 11, 16, 20, 59, 0, TimeSpan.Zero);
        var afterMidnight = new DateTimeOffset(2026, 11, 16, 21, 1, 0, TimeSpan.Zero);

        store.RecordMinute("kid-a", "lessons", beforeMidnight, Tz3);
        store.RecordMinute("kid-a", "lessons", afterMidnight, Tz3);

        // Каждый день держит свою минуту: «сегодня» считается по местному дню now.
        store.MinutesToday("kid-a", "lessons", beforeMidnight, Tz3).Should().Be(1, "23:59 — минута дня 16.11");
        store.MinutesToday("kid-a", "lessons", afterMidnight, Tz3).Should().Be(1, "00:01 — минута дня 17.11");

        // Ни одна из них не видна как «сегодня» другому дню.
        store.MinutesToday("kid-a", "lessons", beforeMidnight.AddDays(1), Tz3).Should().Be(1);
        store.MinutesToday("kid-a", "lessons", afterMidnight.AddDays(-1), Tz3).Should().Be(1);
    }

    [Fact]
    public void ЗаписьСтарше60Дней_СрезанаПриЗаписи()
    {
        var store = Store();

        store.RecordMinute("kid-a", "lessons", NoonUtc, TimeZoneInfo.Utc);
        // Границы окна: -59 входит (окно 09.19–17.11 = ровно 60 дней), -60 и -61 — за окном.
        store.RecordMinute("kid-a", "lessons", NoonUtc.AddDays(-59), TimeZoneInfo.Utc);
        store.MinutesToday("kid-a", "lessons", NoonUtc.AddDays(-59), TimeZoneInfo.Utc).Should().Be(1);

        store.RecordMinute("kid-a", "lessons", NoonUtc.AddDays(-60), TimeZoneInfo.Utc);
        store.RecordMinute("kid-a", "lessons", NoonUtc.AddDays(-61), TimeZoneInfo.Utc);

        store.MinutesToday("kid-a", "lessons", NoonUtc.AddDays(-60), TimeZoneInfo.Utc)
            .Should().Be(0, "день за пределами окна хранения срезан при записи");
        store.MinutesToday("kid-a", "lessons", NoonUtc.AddDays(-61), TimeZoneInfo.Utc).Should().Be(0);
        // Свежие дни срезка не задела: поздняя запись старой минуты их не срезает.
        store.MinutesToday("kid-a", "lessons", NoonUtc, TimeZoneInfo.Utc).Should().Be(1);
    }

    [Fact]
    public void ПустойЖурналИлиЧужоеПространство_Ноль()
    {
        var store = Store();

        // Нет файла вовсе.
        store.MinutesToday("kid-none", "lessons", NoonUtc, TimeZoneInfo.Utc).Should().Be(0);

        // Файл есть, но этого пространства и этого дня в нём нет.
        store.RecordMinute("kid-a", "lessons", NoonUtc, TimeZoneInfo.Utc);
        store.MinutesToday("kid-a", "studio", NoonUtc, TimeZoneInfo.Utc).Should().Be(0);
        store.MinutesToday("kid-a", "lessons", NoonUtc.AddDays(1), TimeZoneInfo.Utc).Should().Be(0);
    }

    [Fact]
    public void РазныеВладельцыИПространства_НеВидятДругДруга()
    {
        var store = Store();

        store.RecordMinute("kid-a", "lessons", NoonUtc, TimeZoneInfo.Utc);
        store.RecordMinute("kid-b", "lessons", NoonUtc, TimeZoneInfo.Utc);
        store.RecordMinute("kid-a", "studio", NoonUtc, TimeZoneInfo.Utc);

        store.MinutesToday("kid-a", "lessons", NoonUtc, TimeZoneInfo.Utc).Should().Be(1);
        store.MinutesToday("kid-b", "lessons", NoonUtc, TimeZoneInfo.Utc).Should().Be(1);
        store.MinutesToday("kid-a", "studio", NoonUtc, TimeZoneInfo.Utc).Should().Be(1);
        store.MinutesToday("kid-b", "studio", NoonUtc, TimeZoneInfo.Utc).Should().Be(0);

        // Файл — на владельца.
        File.Exists(FileFor("kid-a")).Should().BeTrue();
        File.Exists(FileFor("kid-b")).Should().BeTrue();
    }

    [Fact]
    public void НеВалидныйВход_ОтказНаВходе()
    {
        var store = Store();

        // Каждый плохой вход отсекается сам, по одному: пустой первый не глушит остальные.
        Action[] bad = [
            () => store.RecordMinute("", "lessons", NoonUtc, TimeZoneInfo.Utc),
            () => store.RecordMinute("kid/../a", "lessons", NoonUtc, TimeZoneInfo.Utc),
            () => store.RecordMinute("kid-a", "", NoonUtc, TimeZoneInfo.Utc),
            () => store.RecordMinute("kid-a", "lessons lessons", NoonUtc, TimeZoneInfo.Utc),
            () => store.RecordMinute("kid-a", new string('s', 65), NoonUtc, TimeZoneInfo.Utc),
            () => store.RecordMinute("kid-a", "lessons", NoonUtc, null!),
        ];
        foreach (var a in bad) a.Should().Throw<ArgumentException>();
        File.Exists(FileFor("kid-a")).Should().BeFalse("отказ на входе ничего не пишет");
    }

    // ======== Конкурентность ========

    /// <summary>
    /// Две вкладки шлют heartbeat в одну минуту: засчитана одна минута. Проверка держит и
    /// уже записанное состояние (3 минуты до гонок): дубли не должны ни добавить, ни потерять.
    /// От мутации «снять блокировку» этот тест не краснеет — путь дубля возвращается до записи
    /// файла; защиту read-modify-write доказывает ...DifferentMinutes_AllCounted.
    /// </summary>
    [Fact]
    public async Task RecordMinute_ParallelSameMinute_CountsOnce()
    {
        var store = Store();
        var m1 = NoonUtc;
        var m2 = NoonUtc.AddMinutes(1);
        var m3 = NoonUtc.AddMinutes(2);
        foreach (var t in new[] { m1, m2, m3 }) store.RecordMinute("kid-a", "lessons", t, TimeZoneInfo.Utc);

        // 20 параллельных дублей уже учтённой минуты m1.
        await RunParallel(20, _ => store.RecordMinute("kid-a", "lessons", m1, TimeZoneInfo.Utc));

        store.MinutesToday("kid-a", "lessons", NoonUtc.AddMinutes(30), TimeZoneInfo.Utc)
            .Should().Be(3, "дубли той же минуты ничего не добавили, раньше записанное цело");
        MinutesOf("kid-a", "2026-11-17", "lessons").Should().Equal(
            m1.Hour * 60 + m1.Minute, m2.Hour * 60 + m2.Minute, m3.Hour * 60 + m3.Minute);
    }

    /// <summary>Разные минуты одного ребёнка в 20 параллельных вызовах — все целы.</summary>
    [Fact]
    public async Task RecordMinute_ParallelSameOwnerDifferentMinutes_AllCounted()
    {
        var store = Store();

        await RunParallel(20, i => store.RecordMinute("kid-a", "lessons", NoonUtc.AddMinutes(i), TimeZoneInfo.Utc));

        // Все 20 минут — один день (12:00–12:19 UTC): ни одна не потеряна.
        store.MinutesToday("kid-a", "lessons", NoonUtc, TimeZoneInfo.Utc).Should().Be(20);
    }

    /// <summary>20 разных владельцев пишут одновременно: ни одной потерянной записи.</summary>
    [Fact]
    public async Task RecordMinute_ParallelDifferentOwners_NoLoss()
    {
        var store = Store();

        await RunParallel(20, i =>
        {
            var owner = $"kid-{i}";
            for (var m = 0; m < 4; m++) store.RecordMinute(owner, "lessons", NoonUtc.AddMinutes(m), TimeZoneInfo.Utc);
        });

        for (var i = 0; i < 20; i++)
            store.MinutesToday($"kid-{i}", "lessons", NoonUtc, TimeZoneInfo.Utc).Should().Be(4, $"потеря записей {i}");
    }

    // ======== Отказ: битый журнал ========

    [Fact]
    public void БитыйЖсон_ЖурналПродолжаетсяПустым_БезИсключения()
    {
        Directory.CreateDirectory(Root);
        var path = FileFor("kid-a");
        File.WriteAllText(path, "{ \"2026-11-17\": { \"lessons\": [1,");   // синтаксически битый

        var store = Store();
        var act = () => store.RecordMinute("kid-a", "lessons", NoonUtc, TimeZoneInfo.Utc);

        act.Should().NotThrow("потеря журнала присутствия не роняет запрос ребёнка");
        store.MinutesToday("kid-a", "lessons", NoonUtc, TimeZoneInfo.Utc).Should().Be(1);

        // Порча не стирается молча: исходник сохранён рядом снимком .corrupt-*.bak.
        Directory.GetFiles(Root, "kid-a.json.corrupt-*.bak").Should().HaveCount(1);
    }

    // ======== Бэкап и корень ========

    [Fact]
    public void ФайлЖурналаЕдетВБэкап_СлужебныйTmpНет()
    {
        var store = Store();
        store.RecordMinute("kid-a", "lessons", NoonUtc, TimeZoneInfo.Utc);

        // Новое хранилище в data/ не имеет строки-исключения в BackupPaths — оно едет само;
        // Формат существующих сторов не меняется, поэтому BackupSchema.Version остаётся прежним.
        BackupPaths.ShouldInclude($"{ChildUsageStore.DirName}/kid-a.json").Should().BeTrue();
        BackupPaths.ShouldInclude($"{ChildUsageStore.DirName}/kid-a.json.1234567890ab.tmp").Should().BeFalse();
        BackupPaths.ShouldInclude($"{ChildUsageStore.DirName}/kid-a.json.corrupt-20261117-120000.bak").Should().BeFalse();
    }

    [Fact]
    public void КореньЖурнала_ИзРезолвераПутейДанных()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        }).Build();

        ChildUsageStore.RootFromConfig(config).Should().Be(Path.Combine(_tempDir, ChildUsageStore.DirName));
    }

    // ======== Помощники ========

    // Минуты дня из самого файла: тест смотрит итоговое состояние, а не только ответ API.
    private List<int> MinutesOf(string ownerId, string day, string space)
    {
        var all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<int>>>>(
            File.ReadAllText(FileFor(ownerId)), ChildUsageStore.Json);
        return all is not null && all.TryGetValue(day, out var spaces) && spaces.TryGetValue(space, out var minutes)
            ? minutes
            : [];
    }

    // Старт всех задач — по одному барьеру: иначе «гонка» растянется по очереди потоков.
    private static async Task RunParallel(int count, Action<int> body)
    {
        using var gate = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, count)
            .Select(i => Task.Run(() => { gate.Wait(); body(i); }))
            .ToArray();
        gate.Set();
        // Страховка от зависания: условие теста — на результате, не на таймере.
        var all = Task.WhenAll(tasks);
        (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(60)))).Should().Be(all, "гонка не завершилась");
    }
}
