using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Статус заготовки-ассистента при ручной правке (план 2.8): PersonasController.Update снимает
// AssistantPersonaId, ТОЛЬКО когда меняется что-то характер-релевантное (Name/Role/Greeting/
// слот контракта). Посторонние поля (цвет, модель, привязки) статус не трогают — карточка-
// приглашение не должна гаснуть от косметики.
[Trait("Category", "Host")]
public class AssistantStatusTests : IClassFixture<AssistantStatusTests.Hosts>
{
    // Хост на КЛАСС, а не на тест. Статус заготовки (DefaultPersonaId == AssistantPersonaId)
    // живёт в общем UserStore: тесты «меняет имя/слот» снимают его через продукт, тесты
    // «не снимает» ждут нетронутым — конструктор класса выставляет дефолт нетронутой заготовкой
    // перед каждым тестом (саму заготовку заводит стартовый проход провижна хоста, Program.cs).
    // Имя в тесте смены уникальное: правка тем же именем, что уже стоит на заготовке, —
    // не изменение (IsCharacterRelevantChange сравнивает значения), и статус не снялся бы.
    public sealed class Hosts : TestWebApplicationFactory;

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AssistantStatusTests(Hosts hosts)
    {
        _factory = hosts;
        _client = _factory.CreateAuthenticatedClient();
        var users = _factory.Services.GetRequiredService<UserStore>();
        var ownerId = users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        users.SetAssistantPersona(ownerId, users.GetById(ownerId)!.DefaultPersonaId);
    }

    private async Task<JsonElement> MeAsync() => JsonSerializer.Deserialize<JsonElement>(
        await (await _client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync());

    // Заготовку заводит стартовый проход провижна (Program.cs): Default == Assistant, живая.
    private async Task<string> AssistantIdAsync() =>
        (await MeAsync()).GetProperty("defaultPersonaId").GetString()!;

    [Fact]
    public async Task Update_МеняетИмя_СнимаетСтатусЗаготовки()
    {
        var assistantId = await AssistantIdAsync();

        // Имя уникальное: после теста «из онбординг-сессии» на заготовке уже стоит «Марина»,
        // повтор того же имени — не изменение, и статус не снялся бы
        var response = await _client.PutAsJsonAsync($"/api/personas/{assistantId}",
            new { name = $"Марина-{Guid.NewGuid():N}" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var me = await MeAsync();
        me.GetProperty("needsOnboarding").GetBoolean().Should().BeFalse(
            "смена имени — характер-релевантное изменение, статус заготовки снят");
    }

    [Fact]
    public async Task Update_МеняетСлотХарактера_СнимаетСтатусЗаготовки()
    {
        var assistantId = await AssistantIdAsync();

        var response = await _client.PutAsJsonAsync($"/api/personas/{assistantId}",
            new { contract = new { character = "Ты дружелюбный помощник." } });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await MeAsync()).GetProperty("needsOnboarding").GetBoolean().Should().BeFalse(
            "правка слота контракта — характер-релевантное изменение");
    }

    [Fact]
    public async Task Update_МеняетТолькоЦвет_НеСнимаетСтатусЗаготовки()
    {
        var assistantId = await AssistantIdAsync();

        var response = await _client.PutAsJsonAsync($"/api/personas/{assistantId}", new { color = "blue" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await MeAsync()).GetProperty("needsOnboarding").GetBoolean().Should().BeTrue(
            "цвет — косметика, приглашение к знакомству не должно гаснуть");
    }

    [Fact]
    public async Task Update_ТаЖеРольБезИзменений_НеСнимаетСтатусЗаготовки()
    {
        var assistantId = await AssistantIdAsync();
        var before = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync($"/api/personas/{assistantId}")).Content.ReadAsStringAsync());
        var sameRole = before.GetProperty("role").GetString();

        var response = await _client.PutAsJsonAsync($"/api/personas/{assistantId}", new { role = sameRole });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await MeAsync()).GetProperty("needsOnboarding").GetBoolean().Should().BeTrue(
            "значение роли не изменилось — статус заготовки остаётся");
    }

    // Правка ИЗ САМОГО ЗНАКОМСТВА статус не снимает. Интервью зовёт personas_update на шаге
    // доработки — задолго до финального personas_set_default. Сняв маркер там, мы ломали бы
    // три вещи разом: предохранитель Create переставал бы держать (модель могла создать вторую
    // персону), apply-transcript отвечал бы 404 ровно в сценарии «модель не довела интервью до
    // финала», а возврат к чату знакомства деградировал бы промпт к «создай ассистента» поверх
    // уже настроенного. Снимает статус только человек из раздела «Персоны».
    [Fact]
    public async Task Update_ИзОнбордингСессии_НеСнимаетСтатусЗаготовки()
    {
        var assistantId = await AssistantIdAsync();
        var sessionId = JsonSerializer.Deserialize<JsonElement>(
                await (await _client.PostAsync("/api/onboarding/user/start", null)).Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/personas/{assistantId}");
        request.Headers.Add("X-Caller-Session-Id", sessionId);
        request.Content = JsonContent.Create(new { name = "Марина", role = "Личный помощник" });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var me = await MeAsync();
        me.GetProperty("needsOnboarding").GetBoolean().Should().BeTrue(
            "интервью ещё не дошло до финала — заготовка остаётся заготовкой, иначе предохранитель " +
            "перестанет держать, а apply-transcript ответит 404");
        me.GetProperty("defaultPersonaId").GetString().Should().Be(assistantId,
            "дефолт не меняется от доработки заготовки внутри знакомства");
    }
}
