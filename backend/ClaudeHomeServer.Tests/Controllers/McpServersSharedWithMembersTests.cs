using ClaudeHomeServer.Services.Access;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// S2a-7: тумблер «для участников» (PUT /api/mcp/servers/{id}/shared).
/// По умолчанию выключен; меняет только владелец записи — участнику общего проекта 403
/// (запись реальна: это сервер хозяина проекта, он может доехать и до его хода),
/// постороннему 404 (в его реестре такой записи нет). Правка самой записи пометку не сбрасывает.
/// Старый mcp-servers.json без поля читается без миграции: SharedWithMembers=false.
/// </summary>
[Trait("Category", "Host")]
public class McpServersSharedWithMembersTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _owner = factory.CreateAuthenticatedClient();

    private static string IdOf(JsonElement e) => e.GetProperty("id").GetString()!;

    private static string NewKey(string what) => $"{what}-{Guid.NewGuid().ToString("N")[..8]}";

    private async Task<string> CreateServerAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/mcp/servers", new
        {
            key = NewKey("s2a7-srv"),
            transport = "stdio",
            command = "node",
            args = new[] { "server.js" },
        });
        resp.EnsureSuccessStatusCode();
        return IdOf(JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()));
    }

    private async Task<JsonElement> GetServerAsync(string id)
    {
        var resp = await _owner.GetAsync($"/api/mcp/servers/{id}");
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private async Task<JsonElement> MarkAsync(HttpClient client, string id, bool shared)
    {
        var resp = await client.PutAsJsonAsync($"/api/mcp/servers/{id}/shared", new { sharedWithMembers = shared });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    /// <summary>Общий проект testuser с участником seconduser; возвращает id проекта.</summary>
    private async Task<string> AddSharedProjectWithSecondUser()
    {
        var resp = await _owner.PostAsJsonAsync("/api/projects", new { name = "Общий-" + Guid.NewGuid().ToString("N")[..8] });
        resp.EnsureSuccessStatusCode();
        var projectId = IdOf(JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()));
        var secondId = factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;
        factory.Services.GetRequiredService<ProjectManager>().SetMembers(projectId,
            [new ProjectMember { UserId = secondId, Role = ProjectRole.Editor }]);
        return projectId;
    }

    // --- Дефолт и сам тумблер ---

    [Fact]
    public async Task НоваяЗапись_ПометкаВыключена_ТумблерПереключаетсяВОбеСтороны()
    {
        var id = await CreateServerAsync(_owner);
        (await GetServerAsync(id)).GetProperty("sharedWithMembers").GetBoolean()
            .Should().BeFalse("по умолчанию сервер общего проекта не касается");

        (await MarkAsync(_owner, id, true)).GetProperty("sharedWithMembers").GetBoolean()
            .Should().BeTrue("PUT отвечает новым состоянием");
        (await GetServerAsync(id)).GetProperty("sharedWithMembers").GetBoolean()
            .Should().BeTrue("состояние переживает перечитывание");

        (await MarkAsync(_owner, id, false)).GetProperty("sharedWithMembers").GetBoolean()
            .Should().BeFalse("и снимается тем же тумблером");
    }

    // --- Права: только владелец ---

    [Fact]
    public async Task УчастникуОбщегоПроекта_403()
    {
        var id = await CreateServerAsync(_owner);
        await AddSharedProjectWithSecondUser();

        var second = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var resp = await second.PutAsJsonAsync($"/api/mcp/servers/{id}/shared", new { sharedWithMembers = true });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "участник видит чужой сервер хозяина проекта (тот может доехать до его хода), но править его не может");
    }

    [Fact]
    public async Task Постороннему_404_КакЛюбаяЧужаяЗапись()
    {
        var second = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var id = await CreateServerAsync(second); // запись seconduser, у testuser с ним общих проектов нет

        var resp = await _owner.PutAsJsonAsync($"/api/mcp/servers/{id}/shared", new { sharedWithMembers = true });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "в реестре вызывающего такой записи нет — она для него недостижима, как и любая чужая");
    }

    // --- Пометка переживает обычную правку записи (форма правки её не содержит) ---

    [Fact]
    public async Task ПравкаЗаписи_ПометкуНеСбрасывает()
    {
        var id = await CreateServerAsync(_owner);
        await MarkAsync(_owner, id, true);

        // PUT полной записи без поля в запросе — то, что делает форма правки
        var resp = await _owner.PutAsJsonAsync($"/api/mcp/servers/{id}", new
        {
            label = "переименованный",
            transport = "stdio",
            command = "node2",
        });
        resp.EnsureSuccessStatusCode();
        (await GetServerAsync(id)).GetProperty("sharedWithMembers").GetBoolean()
            .Should().BeTrue("правка настроек сервера не имеет права молча снимать разрешение (та же грабля, что чинили с AllowOutsideProjects)");
    }
}

/// <summary>
/// «Существующие записи → false, миграции данных нет»: старый mcp-servers.json без поля
/// грузится как выключенная пометка. Пишем файл напрямую, минуя Create — так же, как его
/// пишет версия до S2a-7 (и так же, как его вернёт бэкап).
/// </summary>
public class McpRegistrySharedWithMembersLoadTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(),
        "mcp_shared_load_" + Guid.NewGuid().ToString("N"));
    private const string OwnerId = "owner-old";

    public McpRegistrySharedWithMembersLoadTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void СтарыйФайлБезПоля_ЧитаетсяКакВыключеннаяПометка()
    {
        File.WriteAllText(Path.Combine(_tempDir, "mcp-servers.json"), """
            {
              "owner-old": [
                { "id": "r1", "ownerId": "owner-old", "key": "srv", "label": "S",
                  "command": "node", "enabled": true }
              ]
            }
            """);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();
        var registry = new McpRegistry(config, new McpSecretStore(config));

        var loaded = registry.GetByOwner(OwnerId);
        loaded.Should().ContainSingle("запись из старого файла должна загрузиться");
        loaded[0].SharedWithMembers.Should().BeFalse(
            "отсутствие поля в JSON значит выключенная пометка: миграции данных S2a-7 не заводил");

        // И по этому состоянию общий проект (Narrow) её не отдаёт — чистое условие,
        // без подняния сессии
        ClaudeHomeServer.Services.Mcp.McpDelivery.ShouldDeliver(loaded[0], ["srv"],
                isProjectChat: true, personaGranted: true, readOnly: false,
                ClaudeHomeServer.Services.Access.TurnFrameKind.Narrow)
            .Should().BeFalse("старая запись в общем проекте не доезжает, пока её не пометили");
    }
}
