using System.Net;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Composition;

// Учёт обращений по ключу подсистемы (S1b): карта «сборка → ключ», мидлвар на живом хосте,
// REST-поля SubsystemInfo. Хост — один на класс.
[Trait("Category", "Host")]
public class SubsystemUsageAttributionTests(SubsystemUsageAttributionTests.Hosts hosts)
    : IClassFixture<SubsystemUsageAttributionTests.Hosts>
{
    public sealed class Hosts : TestWebApplicationFactory;

    private readonly Hosts _factory = hosts;

    private SubsystemUsageStore Store => _factory.Services.GetRequiredService<SubsystemUsageStore>();

    private static int HitsOf(SubsystemUsageStore store, string key) =>
        store.Snapshot(DateTimeOffset.UtcNow).FirstOrDefault(e => e.Key == key)?.Last24h ?? 0;

    [Fact]
    public async Task ВызовКонтроллераВертикали_СчитаетсяПоКлючуПодсистемы()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var before = HitsOf(Store, "notes");

        var resp = await client.GetAsync("/api/notes");
        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);

        HitsOf(Store, "notes").Should().Be(before + 1);
    }

    [Fact]
    public async Task ВызовБезТокена_НеСчитается()
    {
        using var anon = _factory.CreateClient();
        var before = HitsOf(Store, "notes");

        var resp = await anon.GetAsync("/api/notes");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        HitsOf(Store, "notes").Should().Be(before);
    }

    [Fact]
    public async Task РучкаЯдра_НеСчитаетсяНиЗаКакуюПодсистему()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var before = Store.Snapshot(DateTimeOffset.UtcNow).Sum(e => e.Last24h);

        // SubsystemsController лежит в Main — ядро; сам вызов не должен записаться никому
        (await client.GetAsync("/api/admin/subsystems")).StatusCode.Should().Be(HttpStatusCode.OK);

        Store.Snapshot(DateTimeOffset.UtcNow).Sum(e => e.Last24h).Should().Be(before);
    }

    [Fact]
    public async Task Админка_ОтдаётИспользование_ПоКлючуИНуллДляНеиспользованных()
    {
        using var client = _factory.CreateAuthenticatedClient();
        await client.GetAsync("/api/notes");

        var json = JsonNode.Parse(await client.GetStringAsync("/api/admin/subsystems"))!.AsArray();
        var notes = json.Single(n => n!["key"]!.GetValue<string>() == "notes")!;
        notes["usedLast24h"]!.GetValue<int>().Should().BeGreaterThan(0);
        notes["lastUsedAt"]!.GetValue<string>().Should().NotBeNullOrEmpty();

        var unused = json.First(n => n!["usedLast24h"] is null);
        unused!["lastUsedAt"].Should().BeNull();
    }

    [Fact]
    public void КартаСборок_ТулсетВертикалиАтрибутируетсяПоИмени_ЯдроНет()
    {
        var attribution = _factory.Services.GetRequiredService<SubsystemUsageAttribution>();
        var toolsets = _factory.Services.GetServices<IMcpToolset>().ToList();

        // Каждое имя, которое атрибутировалось, ведёт к ключу из наблюдаемого набора
        foreach (var t in toolsets)
        {
            var key = attribution.KeyOfMcp(t.Name);
            if (key is not null) attribution.ObservableKeys.Should().Contain(key);
        }
        attribution.KeyOfMcp("нет-такого-тулсета").Should().BeNull();
        attribution.KeyOf(typeof(SubsystemUsageAttributionTests)).Should().BeNull("сборка тестов не подсистема");
    }

    [Fact]
    public void Snapshot_БезИспользования_ПоляNull()
    {
        var store = new SubsystemStateStore();
        store.RecordActive(new StubSubsystem());
        var info = store.Snapshot(new ConfigurationBuilder().Build()).Single();
        info.LastUsedAt.Should().BeNull();
        info.UsedLast24h.Should().BeNull();
    }

    private sealed class StubSubsystem : IAppSubsystem
    {
        public string Key => "stub";
        public string Title => "Заглушка";
        public void Register(IServiceCollection services, IConfiguration config) { }
    }
}
