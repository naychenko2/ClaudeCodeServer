using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Persist стора local-actions: успешная запись реально попадает на диск, при ошибке записи
// Set откатывает in-memory и возвращает false (ловушка приёмки: раньше успех был молчаливым,
// настройка висела в памяти и терялась при рестарте).
public class LocalActionOverridesStoreTests
{
    private static IConfiguration Config(string dataPath) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = dataPath,
        }).Build();

    [Fact]
    public void Set_УспешнаяЗапись_ФайлНаДискеОбновился()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ccs_la_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var store = new LocalActionOverridesStore(Config(Path.Combine(tempDir, "projects.json")));

        store.Set(LocalActionCatalog.NotesTags, "claude").Should().BeTrue();

        var filePath = Path.Combine(tempDir, "local-actions.json");
        File.Exists(filePath).Should().BeTrue("успешный Set обязан писать на диск");
        using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
        doc.RootElement.TryGetProperty(LocalActionCatalog.NotesTags, out var v).Should().BeTrue();
        v.GetString().Should().Be("claude");

        // in-memory и на диске совпадают
        store.TryGet(LocalActionCatalog.NotesTags).Should().Be("claude");
    }

    [Fact]
    public void Set_ОшибкаЗаписи_ОткатИFalse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ccs_la_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        // Делаем «каталог» data файлом — Directory.CreateDirectory по этому пути упадёт (нельзя
        // создать каталог с именем существующего файла), кросс-платформенно.
        var blocker = Path.Combine(tempDir, "data");
        File.WriteAllText(blocker, "x");

        var store = new LocalActionOverridesStore(Config(Path.Combine(blocker, "projects.json")));

        // Запись не удалась → Set честно отдаёт false (раньше молчаливый true)
        store.Set(LocalActionCatalog.NotesTags, "claude").Should().BeFalse();
        // In-memory откат: значение НЕ видно применённым
        store.TryGet(LocalActionCatalog.NotesTags).Should().BeNull();
    }
}
