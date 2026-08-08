using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Точки вызова DefaultAssistantProvisioner.EnsureAsync (план §2.1–2.3): рубеж создания чата
// проверен отдельно (ChatCreationPersonaGateTests); здесь — хук включения флага
// (FeatureFlagsController.Set, план 2.2) и провижн нового пользователя (UsersController.Create,
// план 2.3). Стартовый проход Program.cs и сама логика EnsureAsync — тонкая обёртка над уже
// покрытым DefaultAssistantProvisionerTests (идемпотентность, гонка, профиль); отдельно не
// перепроверяются — независимой ветвления там нет. Своя фабрика НА КАЖДЫЙ тест (не
// IClassFixture): счётчик персон и состояние флага общего UserStore иначе текли бы
// между фактами класса и делали бы результат зависимым от порядка выполнения.
public class ProvisionEntryPointsTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public ProvisionEntryPointsTests() => _client = _factory.CreateAuthenticatedClient();

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> MeAsync() => JsonSerializer.Deserialize<JsonElement>(
        await (await _client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync());

    private async Task<int> CountGlobalPersonasAsync() =>
        JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync("/api/personas?scope=global")).Content.ReadAsStringAsync())
            .GetArrayLength();

    [Fact]
    public async Task ХукФлага_Включение_ПровижнитРовноОднуЗаготовку()
    {
        var before = await CountGlobalPersonasAsync();

        var response = await _client.PutAsJsonAsync(
            $"/api/feature-flags/{FeatureFlagKeys.DefaultPersonasOnboarding}", new { enabled = true });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var me = await MeAsync();
        me.GetProperty("defaultPersonaId").ValueKind.Should().Be(JsonValueKind.String);
        (await CountGlobalPersonasAsync()).Should().Be(before + 1,
            "включение флага провижнит ровно одну заготовку (план 2.2)");

        // Повторное включение (тумблер туда-обратно) не плодит вторую — EnsureAsync идемпотентен
        (await _client.PutAsJsonAsync(
            $"/api/feature-flags/{FeatureFlagKeys.DefaultPersonasOnboarding}", new { enabled = true }))
            .EnsureSuccessStatusCode();
        (await CountGlobalPersonasAsync()).Should().Be(before + 1);
    }

    [Fact]
    public async Task ХукФлага_Выключение_НеТрогаетУжеПровижнутуюПерсону()
    {
        (await _client.PutAsJsonAsync(
            $"/api/feature-flags/{FeatureFlagKeys.DefaultPersonasOnboarding}", new { enabled = true }))
            .EnsureSuccessStatusCode();
        var assistantId = (await MeAsync()).GetProperty("defaultPersonaId").GetString();

        var response = await _client.PutAsJsonAsync(
            $"/api/feature-flags/{FeatureFlagKeys.DefaultPersonasOnboarding}", new { enabled = false });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await _client.GetAsync($"/api/personas/{assistantId}")).StatusCode.Should().Be(HttpStatusCode.OK,
            "выключение флага не удаляет и не трогает уже созданную персону (план 3г)");
    }

    // Флаг default-personas-onboarding сегодня выключен по умолчанию в каталоге (dark launch) —
    // EnsureAsync у нового пользователя без per-user override оказывается no-op. Тест фиксирует
    // это поведение как регрессионную страховку: провижн нового пользователя не должен падать
    // и не должен создавать персону, пока флаг не станет дефолтно включённым.
    [Fact]
    public async Task НовыйПользователь_БезДефолтноВключённогоФлага_НеПолучаетПерсону()
    {
        var username = "guard_user_" + Guid.NewGuid().ToString("N")[..8];
        var response = await _client.PostAsJsonAsync("/api/users", new
        {
            username, password = "password12345", role = "user",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        created.TryGetProperty("defaultPersonaId", out var dp).Should().BeFalse(
            "DTO нового пользователя вообще не содержит персональных полей онбординга — они не публичные");
    }
}
