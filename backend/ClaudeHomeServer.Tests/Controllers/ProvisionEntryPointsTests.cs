using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Точки вызова DefaultAssistantProvisioner.EnsureAsync: рубеж создания чата проверен отдельно
// (ChatCreationPersonaGateTests); здесь — стартовый проход Program.cs для существующих
// пользователей и провижн нового пользователя (UsersController.Create). Сама логика EnsureAsync
// (идемпотентность, гонка, профиль) покрыта DefaultAssistantProvisionerTests. Своя фабрика НА
// КАЖДЫЙ тест (не IClassFixture): счётчик персон общего UserStore иначе тёк бы между фактами
// класса и делал бы результат зависимым от порядка выполнения.
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

    // Стартовый проход Program.cs — единственная точка провижна существующим пользователям:
    // к первому же запросу дефолт есть, он же заготовка, и он ровно один.
    [Fact]
    public async Task СтартовыйПроход_ДаётСуществующемуПользователюРовноОднуЗаготовку()
    {
        var me = await MeAsync();
        me.GetProperty("defaultPersonaId").ValueKind.Should().Be(JsonValueKind.String);
        me.GetProperty("needsOnboarding").GetBoolean().Should().BeTrue(
            "дефолт — нетронутая заготовка, знакомство ещё не пройдено");

        (await CountGlobalPersonasAsync()).Should().Be(1,
            "стартовый проход идемпотентен — второй заготовки не появляется");
    }

    // Новый пользователь получает заготовку прямо при заведении (UsersController.Create),
    // без ожидания следующего старта сервера. Проверяем через стор: чужие персоны по HTTP
    // не видны, а DTO пользователя персональных полей онбординга не содержит вовсе.
    [Fact]
    public async Task НовыйПользователь_ПолучаетЗаготовкуПриЗаведении()
    {
        var username = "guard_user_" + Guid.NewGuid().ToString("N")[..8];
        var response = await _client.PostAsJsonAsync("/api/users", new
        {
            username, password = "password12345", role = "user",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var users = _factory.Services.GetRequiredService<UserStore>();
        var personas = _factory.Services.GetRequiredService<PersonaManager>();
        var created = users.FindByUsername(username);
        created.Should().NotBeNull();
        created!.DefaultPersonaId.Should().NotBeNull("новичок сразу получает ассистента");
        created.AssistantPersonaId.Should().Be(created.DefaultPersonaId,
            "созданный дефолт — заготовка: оба поля совпадают, пока её не тронули");
        personas.Get(created.DefaultPersonaId!, created.Id).Should().NotBeNull(
            "дефолт резолвится в живую персону");
    }
}
