using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Удаление заготовки-ассистента (план 2.7, решение §3в): AssistantPersonaId обнуляется ВСЕГДА
// при удалении заготовки; если заготовка к тому же дефолт владельца — удаление разрешено БЕЗ
// преемника (единственная глобальная персона нового пользователя иначе была бы неудаляемой),
// и дефолт обнуляется вместе с ней. Своя фабрика НА КАЖДЫЙ тест (не IClassFixture): иначе
// факт, переключивший личный дефолт на другую персону, ломает допущение «дефолт == заготовка»
// у соседнего факта в том же классе (общий UserStore синглтона, порядок не гарантирован).
public class AssistantDeletionTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public AssistantDeletionTests() => _client = _factory.CreateAuthenticatedClient();

    public void Dispose() => _factory.Dispose();

    private async Task SetFlagAsync(bool enabled) =>
        (await _client.PutAsJsonAsync(
            $"/api/feature-flags/{FeatureFlagKeys.DefaultPersonasOnboarding}", new { enabled }))
        .EnsureSuccessStatusCode();

    private async Task<JsonElement> MeAsync() => JsonSerializer.Deserialize<JsonElement>(
        await (await _client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync());

    private async Task<string> ProvisionAssistantAsync()
    {
        await SetFlagAsync(true);
        return (await MeAsync()).GetProperty("defaultPersonaId").GetString()!;
    }

    [Fact]
    public async Task Delete_ЗаготовкаОнаЖеДефолт_БезПреемника_204_ОбнуляетОбаПоля()
    {
        var assistantId = await ProvisionAssistantAsync();

        var response = await _client.DeleteAsync($"/api/personas/{assistantId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "единственная глобальная персона нового пользователя — заготовка, преемник не нужен");

        var me = await MeAsync();
        me.GetProperty("defaultPersonaId").ValueKind.Should().Be(JsonValueKind.Null);
        me.GetProperty("needsOnboarding").GetBoolean().Should().BeFalse(
            "заготовка удалена — AssistantPersonaId обнулён, метка не горит на мёртвом id");
        (await _client.GetAsync($"/api/personas/{assistantId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_ЗаготовкаНеДефолт_ОбнуляетТолькоAssistantPersonaId()
    {
        // Заготовка перестала быть дефолтом (личный дефолт переключили на другую персону),
        // но статус заготовки ещё висит — удаление снимает статус, дефолт не трогает.
        var assistantId = await ProvisionAssistantAsync();
        var otherId = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.PostAsJsonAsync("/api/personas", new { name = "Другая" }))
                .Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        (await _client.PostAsync($"/api/personas/{otherId}/make-default", null)).EnsureSuccessStatusCode();

        var response = await _client.DeleteAsync($"/api/personas/{assistantId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var me = await MeAsync();
        me.GetProperty("defaultPersonaId").GetString().Should().Be(otherId,
            "дефолт уже указывал на другую персону — удаление заготовки его не трогает");
    }
}
