using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Images;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeHomeServer.Tests.Controllers;

// Драйвер-заглушки хоста: генерация картинок всегда работает. Ключ glif и отсутствие
// request_id — догоняющий опрос цены fal не начинается, в учёте остаётся ровно одна
// запись на каждый вызов.
internal sealed class AlwaysImagesGenerator : IImageGenerator
{
    public string Key => "glif";
    public string DisplayName => "Тестовый генератор";
    public bool Enabled => true;
    public IReadOnlyList<ImageModelInfo> Models => [];

    public Task<ImageGenerationResult> GenerateManyAsync(
        string prompt, int count, string? model, CancellationToken ct = default) =>
        Task.FromResult(new ImageGenerationResult(
            Enumerable.Range(1, count).Select(_ => new GeneratedImage([1, 2, 3], "image/png")).ToList(), null));
}

// Черновик персоны для страховки «Применить итоги разговора» — тот же контракт, что в
// OnboardingApplyTranscriptTests; прочие действия остаются «ничего не подошло» ([]),
// как в стандартном стабе фабрики
internal sealed class DraftForQuickCreateRunner : ICheapTextRunner
{
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

    private static string AnswerFor(string actionKey) =>
        actionKey == LocalActionCatalog.PersonaQuickCreate ? DraftJson : "[]";

    public bool UsesLocal(string actionKey) => false;
    public bool HasFreeRoute(string actionKey) => false;
    public bool HasRoute(string actionKey) => true;
    public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";
    public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
        string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        => Task.FromResult(AnswerFor(actionKey));
    public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
        object? jsonFormat = null, CancellationToken ct = default)
        => Task.FromResult<string?>(AnswerFor(actionKey));
    public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
        string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
        int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
        => throw new NotSupportedException();
}

internal sealed class CapturingSpend : ISpendCollector
{
    private readonly List<SpendRecord> _records = [];
    public IReadOnlyList<SpendRecord> Records { get { lock (_records) return [.. _records]; } }
    public void Record(SpendRecord record) { lock (_records) _records.Add(record); }
}

// Хост, где генерация картинок работает, а весь учёт расхода пишется в наблюдаемый
// коллектор: видно, на кого и с каким инициатором роутер записал каждую генерацию
public class AvatarSpendFactory : TestWebApplicationFactory
{
    // Наблюдаемый коллектор доступен тестам этого же сборочника (internal)
    internal CapturingSpend Spend { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IImageGenerator>();
            services.AddSingleton<IImageGenerator, AlwaysImagesGenerator>();
            services.RemoveAll<ISpendCollector>();
            services.AddSingleton<ISpendCollector>(Spend);
            services.RemoveAll<ICheapTextRunner>();
            services.AddSingleton<ICheapTextRunner, DraftForQuickCreateRunner>();
        });
    }
}

// Учёт трат генерации аватаров по точкам входа (S-SPEND-2c, находка ревью R-S-SPEND-2):
// инициатор записи совпадает с инициатором вызова — та же операция из UI даёт Human,
// из чата через MCP-тулсет — Agent; и каждая успешная генерация видна в учёте владельца.
[Trait("Category", "Host")]
public class AvatarSpendPathsTests : IClassFixture<AvatarSpendFactory>
{
    private readonly AvatarSpendFactory _factory;
    private readonly HttpClient _client;

    public AvatarSpendPathsTests(AvatarSpendFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private string OwnerId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private PersonaManager Personas => _factory.Services.GetRequiredService<PersonaManager>();

    // Первая запись каждого вызова генерации (число вариантов, суммы ещё нет) — по ней
    // видно адресата и инициатора траты
    private List<SpendRecord> ImageRecords() =>
        _factory.Spend.Records.Where(r => r.Label == ImageGenerationService.SpendLabelPendingAmount).ToList();

    private Persona NewPersona() =>
        Personas.Create(OwnerId, "А" + Guid.NewGuid().ToString("N")[..5], null, null, null, null, null,
            PersonaScope.Global, null, null, null, false);

    // Сессия-вызыватель — как в PersonasToolsetZoneTests: проект + его сессия, вызов от лица
    // этого чата (X-Caller-Session-Id) даёт широкую рамку хода
    private async Task<string> SessionAsync()
    {
        var project = await _client.PostAsJsonAsync("/api/projects", new { name = $"asp-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var session = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> CallToolsetAsync(string sessionId, string tool, object args)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/mcp/personas/{sessionId}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args },
            }), System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Caller-Session-Id", sessionId);
        var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var result = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        isError.Should().BeFalse(result.GetProperty("content")[0].GetProperty("text").GetString());
        return result;
    }

    private async Task<JsonElement> MeAsync() => JsonSerializer.Deserialize<JsonElement>(
        await (await _client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync())!;

    private async Task EnsureHomeConfiguredAsync()
    {
        var homes = Path.Combine(_factory.TempDir, "homes");
        Directory.CreateDirectory(homes);
        (await _client.PutAsJsonAsync("/api/settings", new { defaultProjectsPath = homes }))
            .EnsureSuccessStatusCode();
    }

    // Та же операция из UI: человек просит варианты аватара — трата на него и с ним же
    [Fact]
    public async Task АватарИзUI_ИнициаторЧеловека()
    {
        var persona = NewPersona();
        var before = ImageRecords().Count;

        var response = await _client.PostAsJsonAsync($"/api/personas/{persona.Id}/avatar/generate",
            new { prompt = "рыжий кот", count = 1 });
        response.EnsureSuccessStatusCode();

        var added = ImageRecords().Skip(before).ToList();
        added.Should().ContainSingle();
        added[0].OwnerId.Should().Be(OwnerId);
        added[0].Initiator.Should().Be(SpendInitiators.Human, "вызов пришёл из формы (REST)");
    }

    // Тот же аватар из чата (personas_generate_avatar): трата того же владельца, но
    // инициатор — агент
    [Fact]
    public async Task АватарИзЧата_ИнициаторАгента()
    {
        var persona = NewPersona();
        var sessionId = await SessionAsync();
        var before = ImageRecords().Count;

        await CallToolsetAsync(sessionId, "personas_generate_avatar", new { id = persona.Id, prompt = "рыжий кот" });

        var added = ImageRecords().Skip(before).ToList();
        added.Should().ContainSingle();
        added[0].OwnerId.Should().Be(OwnerId);
        added[0].Initiator.Should().Be(SpendInitiators.Agent,
            "personas_generate_avatar из чата — агентский вызов");
    }

    // Персона из чата (personas_create с AutoAvatar): аватар генерирует агент, и его
    // инициатор доходит до записи траты
    [Fact]
    public async Task ПерсонаИзЧата_АвтоАватарСИнициаторомАгента()
    {
        var sessionId = await SessionAsync();
        var before = ImageRecords().Count;

        await CallToolsetAsync(sessionId, "personas_create",
            new { name = "ИзЧата" + Guid.NewGuid().ToString("N")[..4], scope = "global" });

        var added = ImageRecords().Skip(before).ToList();
        added.Should().ContainSingle("AutoAvatar у персона из чата всегда true — генерация была");
        added[0].OwnerId.Should().Be(OwnerId);
        added[0].Initiator.Should().Be(SpendInitiators.Agent);
    }

    // Страховка «Применить итоги разговора»: аватар заготовки генерирует сервер по действию
    // человека — трата обязана быть в учёте этого владельца (раньше её не было вовсе)
    [Fact]
    public async Task ИтогиЗнакомства_ТратаАватараНаВладельца()
    {
        await EnsureHomeConfiguredAsync();
        // Заготовку завёл стартовый проход провижна: DefaultPersonaId == AssistantPersonaId
        (await MeAsync()).GetProperty("defaultPersonaId").ValueKind.Should().Be(JsonValueKind.String);
        (await _client.PostAsync("/api/onboarding/user/start", null)).EnsureSuccessStatusCode();
        var before = ImageRecords().Count;

        var response = await _client.PostAsync("/api/onboarding/user/apply-transcript", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var added = ImageRecords().Skip(before).ToList();
        added.Should().ContainSingle("аватар онбординга оплачен инстансом — трата видна в учёте");
        added[0].OwnerId.Should().Be(OwnerId);
        added[0].Initiator.Should().Be(SpendInitiators.Human, "итог знакомства применяет человек из UI");
    }
}
