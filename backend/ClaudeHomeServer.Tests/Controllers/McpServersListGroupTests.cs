using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Регресс: Кира добавила на фронте фильтр вкладки «Доступ» по `server.group === 'integration'`,
// но бэкенд поле `group` в DTO записи реестра не отдавал — `grep -c "Group" McpServerDto.cs`
// давал 0, фронт получал undefined и фильтр не отсекал ничего. Группа ехала только из
// /api/mcp/servers/builtin, а вкладка «Доступ» работает со списком /api/mcp/servers, ключи
// которого в builtin исключены явно.
//
// Этот тест ловит именно дыру «поля в DTO нет»: интеграционная запись higgsfield в
// ответе List обязана иметь group=integration, ручная — group!=integration, и сам
// ключ group обязан присутствовать в JSON (не быть пропущенным null/undefined). Уберут
// поле из DTO — тест упадёт на ассертах ключа и значения.
public class McpServersListGroupTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();

    private static string UserIdOf(TestWebApplicationFactory factory) =>
        factory.Services.GetRequiredService<UserStore>().GetFirst()!.Id;

    [Fact]
    public async Task List_ИнтеграцияHiggsfield_ОтдаётГруппуIntegration()
    {
        // Запись реестра для ключа «higgsfield» заводится только через CreateBuiltIn
        // (McpRegistry.Create режет по ReservedKeys) — ровно как и в проде через
        // HiggsfieldOAuthService.ConnectAsync. Запрос через POST /api/mcp/servers отверг бы
        // ключ как занятый, и тест потерял бы проверяемое значение group.
        var ownerId = UserIdOf(factory);
        var registry = factory.Services.GetRequiredService<McpRegistry>();
        registry.CreateBuiltIn(ownerId, new McpServerRecord
        {
            Key = HiggsfieldOAuthService.Key,
            Label = HiggsfieldOAuthService.Label,
            Description = HiggsfieldOAuthService.Description,
            Transport = McpTransport.Http,
            Url = HiggsfieldOAuthService.Url,
            Auth = new McpAuthConfig { Kind = McpAuthKind.OAuth2 },
            Enabled = true,
        });

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/mcp/servers");
        var higgsfield = list.EnumerateArray()
            .Single(item => item.GetProperty("key").GetString() == HiggsfieldOAuthService.Key);

        // Поле group обязано быть в JSON. Если из DTO его уберут — TryGetProperty вернёт false,
        // и ассерт ниже укажет на причину (мутация «убрать поле из DTO» должна ронять тест)
        higgsfield.TryGetProperty("group", out var groupProp).Should().BeTrue(
            "поле group обязано отдаваться в /api/mcp/servers для записей реестра — иначе фильтр «Доступа» на фронте мёртв");
        groupProp.ValueKind.Should().Be(JsonValueKind.String,
            "group — это строка из McpBuiltinGroups, не null и не отсутствующее значение");
        groupProp.GetString().Should().Be(McpBuiltinGroups.Integration,
            "формат строки обязан совпадать с тем, что отдаёт /api/mcp/servers/builtin — иначе фронт получит несовместимые значения");
    }

    [Fact]
    public async Task List_РучнаяЗапись_ОтдаётГруппуExternal_ИНеIntegration()
    {
        // Ручная запись на свободном ключе: не ReservedKeys, не IntegrationKeys — должна
        // классифицироваться как external. ВАЖНО: group НЕ должен быть «integration»,
        // иначе фильтр «Доступа» уведёт ручные записи под интеграции — старая дыра
        // именно в том, что field отсутствовал (= undefined), и фильтр ничего не отсекал.
        var resp = await _client.PostAsJsonAsync("/api/mcp/servers", new Dictionary<string, object?>
        {
            ["key"] = "mcp-group-test-" + Guid.NewGuid().ToString("N")[..8],
            ["transport"] = "stdio",
            ["command"] = "node",
            ["args"] = new[] { "server.js" },
        });
        resp.EnsureSuccessStatusCode();

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/mcp/servers");
        var own = list.EnumerateArray()
            .Single(item => item.GetProperty("source").GetString() == "manual"
                && item.GetProperty("key").GetString()!.StartsWith("mcp-group-test-"));

        own.TryGetProperty("group", out var groupProp).Should().BeTrue(
            "поле group обязано быть и у ручных записей — фронт ожидает ключ в JSON всегда");
        groupProp.ValueKind.Should().Be(JsonValueKind.String);
        groupProp.GetString().Should().NotBe(McpBuiltinGroups.Integration,
            "ручная запись не должна классифицироваться как интеграция — иначе её каскад «включить в проекте / выдать персоне» уедет в одну кучу с Higgsfield");
        groupProp.GetString().Should().Be(McpBuiltinGroups.External,
            "незнакомый ключ реестра идёт в группу External — см. McpRegistry.BuiltinGroupOf");
    }

    [Fact]
    public async Task List_ГруппыСовпадают_СВстроенными()
    {
        // Источник правды для фронта — McpRegistry.BuiltinGroupOf: одна функция
        // обслуживает и /api/mcp/servers/builtin (McpServersController.Builtin), и
        // записи реестра (через McpServerMapper.ToDto). Расхождение форматов между
        // двумя источниками раскололо бы фильтр «Доступа» без шума — этот тест
        // проверяет, что форматы действительно одни и те же.
        var ownerId = UserIdOf(factory);
        var registry = factory.Services.GetRequiredService<McpRegistry>();
        registry.CreateBuiltIn(ownerId, new McpServerRecord
        {
            Key = "dify", // другой ключ из IntegrationKeys — для разнообразия
            Transport = McpTransport.Http,
            Url = "https://example.test/mcp",
            Auth = new McpAuthConfig { Kind = McpAuthKind.None },
            Enabled = true,
        });

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/mcp/servers");
        var dify = list.EnumerateArray()
            .Single(item => item.GetProperty("key").GetString() == "dify");
        dify.GetProperty("group").GetString().Should().Be(McpBuiltinGroups.Integration,
            "dify — это IntegrationKeys; формат должен совпадать с /api/mcp/servers/builtin");
    }
}