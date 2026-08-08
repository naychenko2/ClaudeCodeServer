using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Статус заготовки-ассистента при ручной правке (план 2.8): PersonasController.Update снимает
// AssistantPersonaId, ТОЛЬКО когда меняется что-то характер-релевантное (Name/Role/Greeting/
// слот контракта). Посторонние поля (цвет, модель, привязки) статус не трогают — карточка-
// приглашение не должна гаснуть от косметики. Своя фабрика НА КАЖДЫЙ тест (не IClassFixture):
// AssistantPersonaId/DefaultPersonaId живут в общем UserStore синглтона, и общая фабрика на
// класс сделала бы факты зависимыми от порядка выполнения (как и в OnboardingApplyTranscriptTests).
public class AssistantStatusTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public AssistantStatusTests() => _client = _factory.CreateAuthenticatedClient();

    public void Dispose() => _factory.Dispose();

    private async Task SetFlagAsync(bool enabled) =>
        (await _client.PutAsJsonAsync(
            $"/api/feature-flags/{FeatureFlagKeys.DefaultPersonasOnboarding}", new { enabled }))
        .EnsureSuccessStatusCode();

    private async Task<JsonElement> MeAsync() => JsonSerializer.Deserialize<JsonElement>(
        await (await _client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync());

    // Включение флага провижнит заготовку (хук 2.2): Default == Assistant, живая.
    private async Task<string> ProvisionAssistantAsync()
    {
        await SetFlagAsync(true);
        return (await MeAsync()).GetProperty("defaultPersonaId").GetString()!;
    }

    [Fact]
    public async Task Update_МеняетИмя_СнимаетСтатусЗаготовки()
    {
        var assistantId = await ProvisionAssistantAsync();

        var response = await _client.PutAsJsonAsync($"/api/personas/{assistantId}", new { name = "Марина" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var me = await MeAsync();
        me.GetProperty("needsOnboarding").GetBoolean().Should().BeFalse(
            "смена имени — характер-релевантное изменение, статус заготовки снят");
    }

    [Fact]
    public async Task Update_МеняетСлотХарактера_СнимаетСтатусЗаготовки()
    {
        var assistantId = await ProvisionAssistantAsync();

        var response = await _client.PutAsJsonAsync($"/api/personas/{assistantId}",
            new { contract = new { character = "Ты дружелюбный помощник." } });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await MeAsync()).GetProperty("needsOnboarding").GetBoolean().Should().BeFalse(
            "правка слота контракта — характер-релевантное изменение");
    }

    [Fact]
    public async Task Update_МеняетТолькоЦвет_НеСнимаетСтатусЗаготовки()
    {
        var assistantId = await ProvisionAssistantAsync();

        var response = await _client.PutAsJsonAsync($"/api/personas/{assistantId}", new { color = "blue" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await MeAsync()).GetProperty("needsOnboarding").GetBoolean().Should().BeTrue(
            "цвет — косметика, приглашение к знакомству не должно гаснуть");
    }

    [Fact]
    public async Task Update_ТаЖеРольБезИзменений_НеСнимаетСтатусЗаготовки()
    {
        var assistantId = await ProvisionAssistantAsync();
        var before = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync($"/api/personas/{assistantId}")).Content.ReadAsStringAsync());
        var sameRole = before.GetProperty("role").GetString();

        var response = await _client.PutAsJsonAsync($"/api/personas/{assistantId}", new { role = sameRole });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await MeAsync()).GetProperty("needsOnboarding").GetBoolean().Should().BeTrue(
            "значение роли не изменилось — статус заготовки остаётся");
    }
}
