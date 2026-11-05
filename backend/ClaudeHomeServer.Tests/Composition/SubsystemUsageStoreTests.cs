using System.Text.Json;
using ClaudeHomeServer.Services.Composition;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Composition;

// Стор счётчика использования подсистем (S1a): кольцо из 24 часовых корзин на ключ,
// TTL корзины при повороте, TTL 30 дней при Load, ограниченный набор ключей,
// конкурентность Hit (лок по ключу) и Save (семафор на путь файла).
//
// Модельное время фиксировано (T0 ровно на границе часа): стор не читает wall-clock,
// все границы окна проверяются на точных значениях.
public class SubsystemUsageStoreTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero); // ровно на границе часового слота

    private static SubsystemUsageStore Store(params string[] keys) => new(keys);

    private static int Last24h(SubsystemUsageStore s, string key, DateTimeOffset now) =>
        s.Snapshot(now).Single(e => e.Key == key).Last24h;

    // --- Hit / Snapshot -------------------------------------------------------------

    [Fact]
    public void Hit_CountsEveryAcceptedCall_KeepsLastUsedAt()
    {
        var s = Store("notes");
        s.Hit("notes", T0);
        s.Hit("notes", T0 + TimeSpan.FromMinutes(5));
        s.Hit("notes", T0 + TimeSpan.FromMinutes(10));

        var snap = s.Snapshot(T0 + TimeSpan.FromMinutes(10));

        snap.Should().HaveCount(1);
        snap[0].Key.Should().Be("notes");
        snap[0].LastUsedAt.Should().Be(T0 + TimeSpan.FromMinutes(10));
        snap[0].Last24h.Should().Be(3);
    }

    // Ключ вне набора и пустой ключ — молча игнор: счётчик не валидатор вызывающего
    // (память ограничена по построению: ключи только из конструктора).
    [Fact]
    public void Hit_ForeignOrEmptyKey_IsIgnoredSilently()
    {
        var s = Store("notes");

        var act = () =>
        {
            s.Hit("video", T0);
            s.Hit("", T0);
            s.Hit("   ", T0);
            s.Hit(null!, T0);
        };
        act.Should().NotThrow();

        s.Snapshot(T0).Should().BeEmpty("чужой и пустой ключ не должны порождать запись");
    }

    // Пустой/пробельный ключ в НАБОРЕ — ошибка конфигурации (тот же контракт, что
    // у SubsystemRegistration.AddSubsystems), а не тихий пропуск.
    [Fact]
    public void Ctor_BlankKeyInSet_Throws()
    {
        var act = () => Store("notes", " ");
        act.Should().Throw<ArgumentException>().WithParameterName("keys");
    }

    // Порядок снимка — по ключу, алфавит (OrdinalIgnoreCase), независимо от порядка Hit.
    [Fact]
    public void Snapshot_SortedByKey_CaseInsensitiveAlphabet()
    {
        var s = Store("notes", "Video", "tasks");
        s.Hit("Video", T0);
        s.Hit("tasks", T0);
        s.Hit("notes", T0);

        s.Snapshot(T0).Select(e => e.Key).Should().Equal("notes", "tasks", "Video");
    }

    // Вызов в другом регистре относится к тому же ключу; в снимке и файле — каноническое
    // написание из набора (иначе один ключ дал бы две строки файла).
    [Fact]
    public void Hit_DifferentCase_SameCanonicalKey()
    {
        var s = Store("notes");
        s.Hit("NOTES", T0);
        s.Hit("Notes", T0 + TimeSpan.FromMinutes(1));

        var snap = s.Snapshot(T0 + TimeSpan.FromMinutes(1));
        snap.Should().HaveCount(1);
        snap[0].Key.Should().Be("notes");
        snap[0].Last24h.Should().Be(2);
    }

    // --- окно 24 ч и поворот кольца --------------------------------------------------

    // Границы окна [H(now)−23 … H(now)] по часовым корзинам: ровно 23 ч 59 мин назад —
    // внутри; ровно 24 часа назад — уже нет (это 25-я корзина). Ровно на границе
    // начала окна (23 ч назад, старт корзины) — внутри: интервал half-open по началу слота.
    [Fact]
    public void RingWindow_HitsAtExactBounds()
    {
        var s = Store("notes");
        s.Hit("notes", T0);                          // 24 ч назад от точки проверки
        s.Hit("notes", T0 + TimeSpan.FromHours(1));  // 23 ч назад — ровно на границе начала окна
        s.Hit("notes", T0 + TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59)); // 1 мин назад

        var now = T0 + TimeSpan.FromHours(24);
        var snap = s.Snapshot(now);

        snap.Single().LastUsedAt.Should().Be(T0 + TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        snap.Single().Last24h.Should().Be(2,
            "обращение ровно за 24 часа попадает в 25-ю корзину и из окна выпадает");
    }

    // Пустая точка проверки: счётчик не «оживает» сам собой — без новых Hit окно пустеет.
    [Fact]
    public void Snapshot_AfterWindowExpires_ReportsZeroButKeepsLastUsedAt()
    {
        var s = Store("notes");
        s.Hit("notes", T0);

        Last24h(s, "notes", T0 + TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59))
            .Should().Be(1, "последняя минута 24-го часа ещё в окне");

        var snap = s.Snapshot(T0 + TimeSpan.FromHours(24));
        snap.Single().Last24h.Should().Be(0);
        snap.Single().LastUsedAt.Should().Be(T0, "время последнего обращения остаётся");
    }

    // Поворот вперёд обнуляет корзины старше кольца: счёт 30-часовой давности не должен
    // доехать до следующего окна.
    [Fact]
    public void Rotation_ZerosBucketsOlderThanTheRing()
    {
        var s = Store("notes");
        s.Hit("notes", T0);
        s.Hit("notes", T0 + TimeSpan.FromHours(30));

        Last24h(s, "notes", T0 + TimeSpan.FromHours(30)).Should().Be(1);

        // Дальше ещё на сутки: и второй счёт выпадает, кольцо не копит историю.
        Last24h(s, "notes", T0 + TimeSpan.FromHours(54)).Should().Be(0);
    }

    // Отдельные ключи не мешают друг другу: счёт каждого — своё кольцо.
    [Fact]
    public void MultipleKeys_HaveIndependentRings()
    {
        var s = Store("notes", "video");
        s.Hit("notes", T0);
        s.Hit("video", T0 + TimeSpan.FromHours(2));
        s.Hit("video", T0 + TimeSpan.FromHours(2));

        var now = T0 + TimeSpan.FromHours(2);
        Last24h(s, "notes", now).Should().Be(1);
        Last24h(s, "video", now).Should().Be(2);
    }

    // --- Load / Save ---------------------------------------------------------------

    private static string UsageJson(params (string Key, DateTimeOffset At)[] entries) =>
        JsonSerializer.Serialize(new
        {
            version = 1,
            entries = entries.Select(e => new
            {
                key = e.Key,
                lastUsedAt = e.At.ToString("O"),
                counts = Enumerable.Repeat(1, 24).ToArray(),
            }).ToArray(),
        });

    private sealed class TempDir : IDisposable
    {
        private readonly string _dir =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "subsys-usage-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(_dir);
        public string File(string name) => System.IO.Path.Combine(_dir, name);
        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* временное — не ценность */ }
        }
    }

    // TTL при загрузке: старше 30 дней — выброс; ровно 30 дней — не «старше», остаётся.
    [Fact]
    public void Load_DropsKeysOlderThanThirtyDays_KeepsExactBoundary()
    {
        using var tmp = new TempDir();
        var file = tmp.File("subsystem-usage.json");
        File.WriteAllText(file, UsageJson(
            ("notes", T0 - TimeSpan.FromDays(29)),
            ("video", T0 - TimeSpan.FromDays(31)),
            ("tasks", T0 - TimeSpan.FromDays(30))));

        var s = Store("notes", "video", "tasks");
        s.Load(file, T0);

        var keys = s.Snapshot(T0).Select(e => e.Key);
        keys.Should().Equal(new[] { "notes", "tasks" },
            "ровно 30 дней — не «старше 30 дней»; 31 день — выброс");
    }

    // Чужой ключ в файле — молча игнор (файл мог остаться от другого набора подсистем).
    [Fact]
    public void Load_IgnoresForeignKeysFromFile()
    {
        using var tmp = new TempDir();
        var file = tmp.File("subsystem-usage.json");
        File.WriteAllText(file, UsageJson(("notes", T0), ("telemetry", T0)));

        var s = Store("notes");
        s.Load(file, T0);

        s.Snapshot(T0).Select(e => e.Key).Should().Equal("notes");
    }

    // Отказ по контракту: отсутствующий, пустой и битый файл — пустой стор, без исключения.
    [Fact]
    public void Load_MissingEmptyOrBrokenFile_GivesEmptyStoreWithoutThrow()
    {
        using var tmp = new TempDir();

        var variants = new[]
        {
            tmp.File("absent.json"),
            tmp.File("empty.json"),   // 0 байт
            tmp.File("broken.json"),  // не JSON
            tmp.File("half.json"),    // JSON, но не нашей формы
        };
        File.WriteAllText(variants[1], "");
        File.WriteAllText(variants[2], "{ not json ][");
        File.WriteAllText(variants[3], """{"version":1,"entries":[{"key":"notes"}]}""");

        foreach (var path in variants)
        {
            var s = Store("notes");
            var act = () => s.Load(path, T0);
            act.Should().NotThrow($"сбой файла не должен ронять старт процесса: {path}");
            s.Snapshot(T0).Should().BeEmpty($"битый или отсутствующий файл даёт пустой стор: {path}");
        }
    }

    // Снимок → файл → новый стор: кольцо и время последнего обращения восстанавливаются.
    [Fact]
    public void SaveThenLoad_RoundTripsRingAndLastUsedAt()
    {
        using var tmp = new TempDir();
        var file = tmp.File("subsystem-usage.json");

        var s = Store("notes", "video");
        s.Hit("notes", T0);
        s.Hit("notes", T0 + TimeSpan.FromMinutes(3));
        s.Hit("video", T0 + TimeSpan.FromMinutes(1));
        s.Save(file);

        var s2 = Store("notes", "video");
        s2.Load(file, T0 + TimeSpan.FromMinutes(3));

        var snap = s2.Snapshot(T0 + TimeSpan.FromMinutes(3));
        snap.Select(e => e.Key).Should().Equal("notes", "video");
        snap[0].Last24h.Should().Be(2);
        snap[0].LastUsedAt.Should().Be(T0 + TimeSpan.FromMinutes(3));
        snap[1].Last24h.Should().Be(1);
    }

    // Источник правды — память: то, что уже набрано в памяти, более старым файлом не перетирается.
    [Fact]
    public void Load_DoesNotOverwriteExistingMemory()
    {
        using var tmp = new TempDir();
        var file = tmp.File("subsystem-usage.json");
        File.WriteAllText(file, UsageJson(("notes", T0 - TimeSpan.FromHours(2))));

        var s = Store("notes");
        s.Hit("notes", T0);
        s.Load(file, T0);

        var snap = s.Snapshot(T0);
        snap.Single().LastUsedAt.Should().Be(T0);
        snap.Single().Last24h.Should().Be(1, "файл не добавляет счёт, которого не было в памяти");
    }

    // Два Save одновременно: семафор на путь + temp-файл с уникальным именем — файл остаётся
    // читаемым снимком (не перемешанными половинками двух записей).
    [Fact]
    public async Task Save_ConcurrentTwoSaves_LeavesValidFile()
    {
        using var tmp = new TempDir();
        var file = tmp.File("subsystem-usage.json");

        var s = Store("notes");
        for (var i = 0; i < 200; i++) s.Hit("notes", T0 + TimeSpan.FromMinutes(i % 60));

        var left = Task.Run(() => s.Save(file));
        var right = Task.Run(() => s.Save(file));
        await Task.WhenAll(left, right);

        var s2 = Store("notes");
        var act = () => s2.Load(file, T0);
        act.Should().NotThrow();
        s2.Snapshot(T0).Single().Last24h.Should().Be(200);
    }

    // --- конкурентность -------------------------------------------------------------

    // Лок по ключу, не общий на стор: два потока × 10 000 Hit одного ключа дают ровно 20 000.
    [Fact]
    public async Task Hit_Parallel_NoLostCounts()
    {
        var s = Store("notes");

        var left = Task.Run(() => { for (var i = 0; i < 10_000; i++) s.Hit("notes", T0); });
        var right = Task.Run(() => { for (var i = 0; i < 10_000; i++) s.Hit("notes", T0); });
        await Task.WhenAll(left, right);

        Last24h(s, "notes", T0).Should().Be(20_000);
    }

    // --- бюджет и объём -------------------------------------------------------------

    // 50 ключей × 1 обращение в минуту × 7 суток модельного времени:
    // Last24h каждого = 1440 (24 ч × 60), файл < 16 КБ, точка проверки — внутри часа
    // последнего обращения (последний Hit ровно за минуту до now).
    [Fact]
    public void Size_50Keys_7Days_WindowIs24HoursAndFileUnder16Kb()
    {
        using var tmp = new TempDir();
        var file = tmp.File("subsystem-usage.json");

        var keys = Enumerable.Range(0, 50).Select(i => $"subsystem{i:D2}").ToArray();
        var s = Store(keys);

        var week = TimeSpan.FromDays(7);
        for (var t = T0; t < T0 + week; t += TimeSpan.FromMinutes(1))
            foreach (var k in keys)
                s.Hit(k, t);

        var now = T0 + week - TimeSpan.FromMinutes(1);
        var snap = s.Snapshot(now);
        snap.Should().HaveCount(50);
        snap.Should().OnlyContain(e => e.Last24h == 1_440,
            "окно = ровно 24 часа по часовым корзинам");

        s.Save(file);
        new FileInfo(file).Length.Should().BeLessThan(16 * 1024,
            "50 ключей × 24 int + 1 время — верхняя граница файла");
    }
}
