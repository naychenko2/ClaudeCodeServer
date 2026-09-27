using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

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

    // --- Волна 7: инстансные интеграции (Higgsfield). Глеб в ревью `4f061084` отметил
    // отсутствие тестов на новую поверхность: GetForServer/GetByOwnerForServers/GetByOwnerMerged.
    // Эти пять тестов — сторожа на инстансность: следующий человек не должен случайно
    // продублировать условие в контроллере или прочитать user-bag там, где жил ServiceOwnerId-bag.

    // Сторож 1: обычные серверы идут через user-bag, как и Get. Без этого теста любая
    // правка GetForServer может сломать «дефолтный» путь — никто не заметит.
    [Fact]
    public void GetForServer_ОбычныйСервер_ЧитаетИзUserBag()
    {
        var store = NewStore();
        store.RecordFromInit("owner1", "chat1",
            [new McpServerInfo("weather", "connected")]);

        var record = new McpServerRecord
        {
            Key = "weather",
            OwnerId = "owner1",
            Label = "Weather",
            Transport = McpTransport.Http,
            Url = "https://example.test/mcp",
        };

        var entry = store.GetForServer("owner1", record);

        entry.Should().BeEquivalentTo(store.Get("owner1", "weather"),
            "обычный сервер обязан читаться из user-bag — как и прежний Get");
    }

    // Сторож 2: точка склейки реально работает — для Higgsfield отдаём ServiceOwnerId-bag,
    // даже если в user-bag есть запись (старое «connected» от init не должно помешать
    // свежему «failed» от probe дойти до UI).
    [Fact]
    public void GetForServer_Higgsfield_ЧитаетИзServiceOwnerIdИгнорируяUserBag()
    {
        var store = NewStore();
        const string userId = "owner1";
        // Ложный «connected» в user-bag — init мог принести его до сбоя OAuth.
        store.RecordFromInit(userId, "chat1",
            [new McpServerInfo(HiggsfieldOAuthService.Key, "connected")]);
        // Истинный «failed» в ServiceOwnerId-bag — проба по кнопке.
        store.RecordProbe(HiggsfieldOAuthService.ServiceOwnerId, HiggsfieldOAuthService.Key,
            McpServerStatuses.Failed, "tools/list empty");

        var record = new McpServerRecord
        {
            Key = HiggsfieldOAuthService.Key,
            OwnerId = userId,
            Label = HiggsfieldOAuthService.Label,
            Transport = McpTransport.Http,
            Url = HiggsfieldOAuthService.Url,
        };

        var entry = store.GetForServer(userId, record);

        entry.Should().NotBeNull();
        entry!.Status.Should().Be(McpServerStatuses.Failed,
            "Higgsfield обязан читаться из ServiceOwnerId-bag, не из user-bag");
        entry.Error.Should().Be("tools/list empty",
            "диагностический текст берётся из инстансной записи, не из чужой");
    }

    // Сторож 3: при коллизии побеждает инстансный статус. Без этого теста следующая
    // правка может «упростить» GetByOwnerMerged до простого GetByOwner — и карточка
    // встроенных серверов начнёт показывать зелёный Higgsfield, пока реальный статус Failed.
    [Fact]
    public void GetByOwnerMerged_КоллизияСтатусов_ПриоритетУИнстансного()
    {
        var store = NewStore();
        const string userId = "owner1";
        store.RecordFromInit(userId, "chat1",
            [new McpServerInfo(HiggsfieldOAuthService.Key, "connected")]);
        store.RecordProbe(HiggsfieldOAuthService.ServiceOwnerId, HiggsfieldOAuthService.Key,
            McpServerStatuses.NeedsAuth, "401 Unauthorized");

        var merged = store.GetByOwnerMerged(userId);

        merged.Should().ContainKey(HiggsfieldOAuthService.Key);
        merged[HiggsfieldOAuthService.Key].Status.Should().Be(McpServerStatuses.NeedsAuth,
            "инстансный статус важнее user-bag — иначе UI скроет реальную проблему");
    }

    // Сторож 4: прежнее поведение GetByOwner не ломается, когда инстансных записей нет.
    // Без теста легко «оптимизировать» GetByOwnerMerged и пропустить возврат user-bag'а
    // для обычных записей вроде weather.
    [Fact]
    public void GetByOwnerMerged_НетИнстансныхЗаписей_РавенGetByOwner()
    {
        var store = NewStore();
        const string userId = "owner1";
        store.RecordFromInit(userId, "chat1", [
            new McpServerInfo("weather", "connected"),
            new McpServerInfo("tasks", "failed"),
        ]);
        // ServiceOwnerId-bag пуст — никаких записей о Higgsfield.

        var merged = store.GetByOwnerMerged(userId);
        var own = store.GetByOwner(userId);

        merged.Should().BeEquivalentTo(own,
            "без инстансных записей мердж обязан вернуть то же, что и GetByOwner");
        merged.Should().HaveCount(2);
    }

    // Сторож 5: пустой список записей — не падает. Контроллеры List и Builtin прогоняют
    // все записи реестра (а у чата без реестра их 0). Без теста первая регрессия
    // обнаружится только в проде.
    [Fact]
    public void GetByOwnerForServers_ПустойСписок_ВозвращаетПустойСловарь()
    {
        var store = NewStore();

        var result = store.GetByOwnerForServers("owner1", []);

        result.Should().NotBeNull();
        result.Should().BeEmpty("граничный случай — пустой вход даёт пустой выход без падений");
    }
}
