using ClaudeHomeServer.Services;
using FluentAssertions;

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
}
