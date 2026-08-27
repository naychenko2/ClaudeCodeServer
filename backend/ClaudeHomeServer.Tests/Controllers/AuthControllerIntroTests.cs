using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// GET /api/auth/me — блокеры плана onboarding-optional (§2.5, принцип 3): чтение НИКОГДА
// не провижнит персону, needsOnboarding — точная формула, осиротевший дефолт
// резолвится в null на чтении. Отдельный класс (не расширение AuthControllerTests) — тесты
// мутируют UserStore напрямую, состояние per-факт не должно течь в обычные auth-тесты.
// Дефолт/заготовку каждый тест выставляет сам — порядок фактов внутри класса не
// гарантирован (общая фабрика на класс, как в ChatCreationPersonaGateTests).
public class AuthControllerIntroTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AuthControllerIntroTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private async Task<JsonElement> MeAsync() => JsonSerializer.Deserialize<JsonElement>(
        await (await _client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync());

    private async Task<int> CountGlobalPersonasAsync() =>
        JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync("/api/personas?scope=global")).Content.ReadAsStringAsync())
            .GetArrayLength();

    // Принцип 3 плана (§2.5): GET /me — чистое чтение, провижн вызывается ТОЛЬКО на точках
    // записи. Дефолт и заготовку снимаем напрямую в UserStore — через HTTP их не обнулить.
    [Fact]
    public async Task Me_БезДефолта_НеСоздаётПерсонуДажеПриПовторныхЗапросах()
    {
        var userId = (await MeAsync()).GetProperty("userId").GetString()!;
        var users = _factory.Services.GetRequiredService<UserStore>();
        users.SetDefaultPersona(userId, null);
        users.SetAssistantPersona(userId, null);

        var before = await CountGlobalPersonasAsync();
        for (var i = 0; i < 3; i++)
            (await _client.GetAsync("/api/auth/me")).EnsureSuccessStatusCode();
        var after = await CountGlobalPersonasAsync();

        after.Should().Be(before, "GET /me не должен провижнить персону — мутации только на точках записи");
    }

    // Полный проход формулы needsOnboarding = !IntroCompletedAt && Default==Assistant
    // && резолв в живую персону — по одному слагаемому, чтобы падение любого гасило метку.
    [Fact]
    public async Task Me_needsOnboarding_КаждоеСлагаемоеФормулыОбязательно()
    {
        var userId = (await MeAsync()).GetProperty("userId").GetString()!;
        var users = _factory.Services.GetRequiredService<UserStore>();
        var personas = _factory.Services.GetRequiredService<PersonaManager>();

        var persona = personas.Create(userId, "Тест-формула", role: null, description: null, systemPrompt: null,
            model: null, effort: null, scope: PersonaScope.Global, projectId: null, color: "orange",
            greeting: null, memoryEnabled: true);
        users.SetDefaultPersona(userId, persona.Id);
        users.SetAssistantPersona(userId, persona.Id);
        users.SetIntroCompleted(userId, null);

        // Все слагаемые сошлись — метка горит
        (await MeAsync()).GetProperty("needsOnboarding").GetBoolean().Should().BeTrue();

        // Знакомство пройдено — гаснет
        users.SetIntroCompleted(userId, DateTime.UtcNow);
        (await MeAsync()).GetProperty("needsOnboarding").GetBoolean().Should().BeFalse("знакомство уже пройдено");
        users.SetIntroCompleted(userId, null);

        // Заготовка «тронута» (AssistantPersonaId сброшен вручную) — гаснет
        users.SetAssistantPersona(userId, null);
        (await MeAsync()).GetProperty("needsOnboarding").GetBoolean().Should().BeFalse(
            "AssistantPersonaId != DefaultPersonaId — заготовка уже не заготовка");
        users.SetAssistantPersona(userId, persona.Id);
    }

    // Резолв дефолта в живой объект (не просто «поле не null»): дефолт указывает на
    // удалённую персону → AuthController.Me() нормализует на чтении в null, а needsOnboarding
    // не зажигается — иначе мёртвый id и зажигал бы метку навсегда, и запирал бы её (создать
    // нельзя — персона фигурирует как «уже есть», но её нет).
    [Fact]
    public async Task Me_ОсиротевшийДефолт_РезолвитсяВNull_НеЗажигаетМетку()
    {
        var userId = (await MeAsync()).GetProperty("userId").GetString()!;
        var users = _factory.Services.GetRequiredService<UserStore>();
        users.SetIntroCompleted(userId, null);
        users.SetDefaultPersona(userId, "dead-persona-id-does-not-exist");
        users.SetAssistantPersona(userId, "dead-persona-id-does-not-exist");

        var me = await MeAsync();
        me.GetProperty("defaultPersonaId").ValueKind.Should().Be(JsonValueKind.Null,
            "мёртвый DefaultPersonaId нормализуется в null на чтении");
        me.GetProperty("needsOnboarding").GetBoolean().Should().BeFalse(
            "резолв в живую персону — обязательное слагаемое, не просто DefaultPersonaId != null");
    }
}
