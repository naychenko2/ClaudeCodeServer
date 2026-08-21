using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Страховка «Применить итоги разговора» онбординга (план 3.2): POST /api/onboarding/user/apply-transcript.
// Контракт: 404 — нет живой сессии или живой заготовки; 200 — заготовка обновлена, знакомство завершено.
// Свежая фабрика на каждый тест: состояние OnboardingSessionId/AssistantPersonaId живёт в
// общем UserStore синглтона, и тесты на нём зависели бы от порядка. Плюс своя ICheapTextRunner-заглушка,
// отдающая готовый черновик: боевой раннер погнал бы claude.exe/Ollama в интеграционном тесте.
public class OnboardingApplyTranscriptTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    // Готовый черновик персоны — модель «договорилась» до него, но до personas_set_default не дошла.
    private static readonly string DraftJson = JsonSerializer.Serialize(new
    {
        role = "Личный тренер",
        name = "Марина",
        description = "Помощник и тренер",
        @character = "Ты обаятельный помощник.",
        tone = "тепло и на равных",
        mustDo = new[] { "Отвечай по делу" },
        mustNot = new[] { "Не воды" },
        outputFormat = "Коротко",
        speechExamples = new[] { "Привет!" },
        greeting = "Здравствуйте!",
        color = "blue",
        avatarPrompt = "friendly woman 30s",
    });

    public OnboardingApplyTranscriptTests()
    {
        _factory = new TestWebApplicationFactory
        {
            ExtraServices = s => s.AddSingleton<ICheapTextRunner>(new StubCheapRunner(DraftJson))
        };
        _client = _factory.CreateAuthenticatedClient();
    }

    public void Dispose() => _factory.Dispose();

    private async Task EnsureHomeConfiguredAsync()
    {
        var homes = Path.Combine(_factory.TempDir, "homes");
        Directory.CreateDirectory(homes);
        (await _client.PutAsJsonAsync("/api/settings", new { defaultProjectsPath = homes }))
            .EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> MeAsync() => JsonSerializer.Deserialize<JsonElement>(
        await (await _client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync())!;

    // Нет живой сессии знакомства (OnboardingSessionId пуст) → 404.
    [Fact]
    public async Task БезСессии_404()
    {
        var response = await _client.PostAsync("/api/onboarding/user/apply-transcript", null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("сессии");
    }

    // Сессия есть, но живой заготовки нет (её удалили или статус уже снят правкой) → 404.
    [Fact]
    public async Task БезЗаготовки_404()
    {
        await EnsureHomeConfiguredAsync();
        (await _client.PostAsync("/api/onboarding/user/start", null)).EnsureSuccessStatusCode();
        // Статус заготовки снят: AssistantPersonaId пуст — применять итоги не к чему
        var userId = (await MeAsync()).GetProperty("userId").GetString()!;
        _factory.Services.GetRequiredService<UserStore>().SetAssistantPersona(userId, null);
        (await MeAsync()).GetProperty("onboardingSessionId").ValueKind.Should().Be(JsonValueKind.String);

        var response = await _client.PostAsync("/api/onboarding/user/apply-transcript", null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("заготовки");
    }

    // Сессия + живая заготовка: транскрипт → черновик → применение к заготовке → финализация.
    // Вторую персону НЕ плодим: та же заготовка получает имя/характер, знакомство завершается.
    [Fact]
    public async Task СоСессиейИЗаготовкой_ПрименяетЧерновикИЗавершает()
    {
        await EnsureHomeConfiguredAsync();
        // Заготовку завёл стартовый проход провижна: DefaultPersonaId == AssistantPersonaId.
        var assistantId = (await MeAsync()).GetProperty("defaultPersonaId").GetString()!;
        // Старт онбординга фиксирует OnboardingSessionId.
        (await _client.PostAsync("/api/onboarding/user/start", null)).EnsureSuccessStatusCode();

        var response = await _client.PostAsync("/api/onboarding/user/apply-transcript", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Заготовка обновлена на месте (id дефолта не изменился, имя — из черновика).
        var applied = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())!;
        applied.GetProperty("id").GetString().Should().Be(assistantId);
        applied.GetProperty("name").GetString().Should().Be("Марина");

        // Второй персоны не создано: единственная глобальная персона — бывшая заготовка.
        var list = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync("/api/personas?scope=global")).Content.ReadAsStringAsync())!;
        list.GetArrayLength().Should().Be(1, "применение итогов не должно плодить вторую персону");

        // Финализация: OnboardingSessionId очищен, needsOnboarding погас (IntroCompletedAt проставлен,
        // AssistantPersonaId обнулён → DefaultPersonaId != AssistantPersonaId).
        var me = await MeAsync();
        me.GetProperty("onboardingSessionId").ValueKind.Should().Be(JsonValueKind.Null);
        me.GetProperty("needsOnboarding").GetBoolean().Should().BeFalse();
        me.GetProperty("defaultPersonaId").GetString().Should().Be(assistantId,
            "apply-transcript не меняет дефолт — заготовка им уже была");
    }

    private sealed class StubCheapRunner(string answer) : ICheapTextRunner
    {
        public bool UsesLocal(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "claude";
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult(answer);
        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>(answer);
        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
