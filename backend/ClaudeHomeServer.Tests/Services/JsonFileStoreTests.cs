using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Tests.Services;

// Атомарная запись JSON-сторов: устойчивость к транзиторной блокировке целевого файла
// (антивирус/индексатор на Windows) и к параллельным Save по одному пути.
public class JsonFileStoreTests : IDisposable
{
    private readonly string _dir;

    public JsonFileStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "jsonstore_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Save_ЦелевойФайлВременноЗаблокирован_ДожидаетсяИПишет()
    {
        var path = Path.Combine(_dir, "store.json");
        JsonFileStore.Save(path, new List<string> { "старое" });

        // Держим целевой файл эксклюзивно — как это делает антивирус на доли секунды.
        // Освобождаем на ВЫДЕЛЕННОМ потоке: пул под нагрузкой полного прогона может
        // задержать Task.Run на сотни мс и сделать тест тайминг-зависимым.
        var handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = new Thread(() => { Thread.Sleep(50); handle.Dispose(); }) { IsBackground = true };
        release.Start();

        var act = () => JsonFileStore.Save(path, new List<string> { "новое" });

        act.Should().NotThrow();
        release.Join();
        JsonFileStore.Load<List<string>>(path).Should().BeEquivalentTo(["новое"]);
    }

    [Fact]
    public void Save_ПараллельныеЗаписиПоОдномуПути_БезОшибок()
    {
        var path = Path.Combine(_dir, "concurrent.json");

        var act = () => Parallel.For(0, 32, i => JsonFileStore.Save(path, new List<string> { $"v{i}" }));

        act.Should().NotThrow();
        JsonFileStore.Load<List<string>>(path).Should().HaveCount(1);
        // Временные файлы за собой не оставляем
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
    }

    // 19.09.2026: 22 записи с "TaskExecution": null при non-nullable bool унесли все 866
    // сессий в .corrupt-бэкап. Битая запись обязана стоить только себя.
    [Fact]
    public void Load_БитаяЗаписьВСписке_ПоднимаетОстальныеИНеУноситФайл()
    {
        var path = Path.Combine(_dir, "sessions.json");
        File.WriteAllText(path, """
            [
              {"Id":"a","Flag":true},
              {"Id":"b","Flag":null},
              {"Id":"c","Flag":false}
            ]
            """);
        var logger = new CollectingLogger();

        var loaded = JsonFileStore.Load<List<Record>>(path, logger: logger);

        loaded.Should().NotBeNull();
        loaded!.Select(r => r.Id).Should().BeEquivalentTo(["a", "c"]);
        Directory.GetFiles(_dir, "*.corrupt-*.bak").Should().BeEmpty();
        File.Exists(path).Should().BeTrue();
        logger.Warnings.Should().Contain(m => m.Contains("#1"));
        logger.Warnings.Should().Contain(m => m.Contains("пропущено битых 1"));
    }

    [Fact]
    public void Load_БитаяЗаписьВСловаре_ПоднимаетОстальные()
    {
        var path = Path.Combine(_dir, "memory.json");
        File.WriteAllText(path, """
            {"первый":{"Id":"a","Flag":true},"второй":{"Id":"b","Flag":null}}
            """);
        var logger = new CollectingLogger();

        var loaded = JsonFileStore.Load<Dictionary<string, Record>>(path, logger: logger);

        loaded.Should().NotBeNull();
        loaded!.Keys.Should().BeEquivalentTo(["первый"]);
        Directory.GetFiles(_dir, "*.corrupt-*.bak").Should().BeEmpty();
        logger.Warnings.Should().Contain(m => m.Contains("«второй»"));
    }

    // Файл из NUL-байтов (аварийное выключение) — поэлементно не разложить, но рядом
    // лежит прошлый снимок: пустое состояние тут было бы потерей данных на ровном месте.
    [Fact]
    public void Load_ФайлИзНулей_ПоднимаетСостояниеИзБэкапа()
    {
        var path = Path.Combine(_dir, "sessions.json");
        File.WriteAllText($"{path}.corrupt-20260919-051307.bak", """[{"Id":"из бэкапа","Flag":true}]""");
        File.WriteAllBytes(path, new byte[64]);

        var loaded = JsonFileStore.Load<List<Record>>(path);

        loaded.Should().NotBeNull();
        loaded!.Select(r => r.Id).Should().BeEquivalentTo(["из бэкапа"]);
        // Нечитаемый файл при этом убран с дороги, а бэкап, из которого поднялись, остался
        File.Exists(path).Should().BeFalse();
        File.Exists($"{path}.corrupt-20260919-051307.bak").Should().BeTrue();
    }

    [Fact]
    public void Load_НеКоллекционныйТипСБитымПолем_ПрежнееПоведение()
    {
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, """{"Id":"a","Flag":null}""");

        var loaded = JsonFileStore.Load<Record>(path);

        loaded.Should().BeNull();
        File.Exists(path).Should().BeFalse();
        Directory.GetFiles(_dir, "state.json.corrupt-*.bak").Should().HaveCount(1);
    }

    [Fact]
    public void Load_НиФайлаНиБэкапа_ОтдаётПустоеСостояниеИАлертит()
    {
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllBytes(path, new byte[64]);
        var alerts = new List<JsonFileStore.DataLossAlert>();
        JsonFileStore.DataLossSink = alerts.Add;
        try
        {
            var loaded = JsonFileStore.Load<List<Record>>(path);

            loaded.Should().BeNull();
            alerts.Should().ContainSingle(a => a.Path == path);
        }
        finally
        {
            JsonFileStore.DataLossSink = null;
        }
    }

    // Алерт на старте случается ДО того, как композиция подписала доставку, — копится и
    // уходит в момент подписки. Без этого главный сценарий (чтение сторов при старте)
    // остался бы незамеченным ровно как 19.09.
    [Fact]
    public void DataLossSink_ПодпискаПослеСобытия_ПолучаетНакопленное()
    {
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllBytes(path, new byte[64]);
        // Сброс подписчика заодно очищает накопленное — стартуем с пустого буфера
        JsonFileStore.DataLossSink = null;
        JsonFileStore.Load<List<Record>>(path);

        var alerts = new List<JsonFileStore.DataLossAlert>();
        try
        {
            JsonFileStore.DataLossSink = alerts.Add;

            alerts.Should().ContainSingle(a => a.Path == path);
        }
        finally
        {
            JsonFileStore.DataLossSink = null;
        }
    }

    private sealed class Record
    {
        public string Id { get; set; } = "";
        public bool Flag { get; set; }
    }

    private sealed class CollectingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
