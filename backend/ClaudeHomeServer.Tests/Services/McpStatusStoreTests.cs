using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Последний известный статус MCP-серверов: наблюдение из system/init каждого хода плюс
// разовая проба. Врать этот стор не имеет права — по нему человек решает, чинить ли запись.
public class McpStatusStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccs-mcp-status-" + Guid.NewGuid().ToString("N")[..8]);

    private McpStatusStore NewStore()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
        }).Build();
        return new McpStatusStore(config);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка best-effort */ }
    }

    [Theory]
    [InlineData("connected", McpServerStatuses.Connected)]
    [InlineData("failed", McpServerStatuses.Failed)]
    // CLI пишет «нужен вход» по-разному — наружу обязано выходить одно слово
    [InlineData("needs auth", McpServerStatuses.NeedsAuth)]
    [InlineData("needs_auth", McpServerStatuses.NeedsAuth)]
    [InlineData("", McpServerStatuses.Unknown)]
    [InlineData("что-то новое", McpServerStatuses.Unknown)]
    public void СтатусыИзInit_Нормализуются(string raw, string expected)
    {
        var store = NewStore();

        store.RecordFromInit("owner1", "chat1", [new McpServerInfo("weather", raw)]);

        store.Get("owner1", "weather")!.Status.Should().Be(expected);
    }

    [Fact]
    public void НаблюдениеИзInit_ХранитИсточникИЧат()
    {
        var store = NewStore();

        store.RecordFromInit("owner1", "chat1", [new McpServerInfo("tasks", "connected")]);

        var entry = store.Get("owner1", "tasks")!;
        entry.Source.Should().Be(McpObservationSource.Init);
        entry.SessionId.Should().Be("chat1");
        entry.Error.Should().BeNull();
    }

    [Fact]
    public void ПробаПерекрываетНаблюдениеХода()
    {
        var store = NewStore();
        store.RecordFromInit("owner1", "chat1", [new McpServerInfo("weather", "connected")]);

        store.RecordProbe("owner1", "weather", McpServerStatuses.NeedsAuth, "Сервер требует авторизации");

        var entry = store.Get("owner1", "weather")!;
        entry.Status.Should().Be(McpServerStatuses.NeedsAuth);
        entry.Source.Should().Be(McpObservationSource.Probe);
        entry.Error.Should().Be("Сервер требует авторизации");
        // Чат прежнего наблюдения не должен «прилипнуть» к пробе — она идёт вне хода
        entry.SessionId.Should().BeNull();
    }

    [Fact]
    public void НаблюдениеПереживаетПерезапуск()
    {
        var store = NewStore();
        store.RecordProbe("owner1", "weather", McpServerStatuses.Connected, null);

        NewStore().Get("owner1", "weather")!.Status.Should().Be(McpServerStatuses.Connected);
    }

    [Fact]
    public void НаблюденияРазныхВладельцев_НеПересекаются()
    {
        var store = NewStore();

        store.RecordFromInit("owner1", "chat1", [new McpServerInfo("weather", "connected")]);
        store.RecordFromInit("owner2", "chat2", [new McpServerInfo("weather", "failed")]);

        store.Get("owner1", "weather")!.Status.Should().Be(McpServerStatuses.Connected);
        store.Get("owner2", "weather")!.Status.Should().Be(McpServerStatuses.Failed);
    }

    [Fact]
    public void УдалениеСервера_УноситНаблюдение()
    {
        var store = NewStore();
        store.RecordFromInit("owner1", "chat1", [new McpServerInfo("weather", "connected")]);

        store.Remove("owner1", "weather");

        store.Get("owner1", "weather").Should().BeNull();
        // И после перезапуска тоже: иначе наблюдение вернулось бы к новой одноимённой записи
        NewStore().Get("owner1", "weather").Should().BeNull();
    }

    [Fact]
    public void СтатусыВстроенныхСерверов_ПриезжаютВместеСРеестровыми()
    {
        var store = NewStore();

        // init перечисляет ВСЕ серверы хода — фильтра по реестру у стора нет
        store.RecordFromInit("owner1", "chat1", [
            new McpServerInfo("tasks", "connected"),
            new McpServerInfo("notes", "connected"),
            new McpServerInfo("weather", "failed"),
        ]);

        store.GetByOwner("owner1").Should().HaveCount(3);
    }
}
