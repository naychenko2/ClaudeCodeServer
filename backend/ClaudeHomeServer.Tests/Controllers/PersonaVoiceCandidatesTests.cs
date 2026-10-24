using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Кнопка «Подобрать под персону»: три обычных голоса плюс отдельный живой (LiveTTS),
// «Подобрать ещё» без уже показанных. Автоподбор при создании при этом живого голоса
// не подставляет никогда — он по-прежнему ходит через PickVoiceAsync.
public class PersonaVoiceCandidatesTests : IDisposable
{
    private readonly VoiceCheap _cheap = new();
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PersonaVoiceCandidatesTests()
    {
        _factory = new TestWebApplicationFactory
        {
            ExtraServices = s => s.AddSingleton<ICheapTextRunner>(_cheap),
        };
        _client = _factory.CreateAuthenticatedClient();
    }

    public void Dispose() => _factory.Dispose();

    // Заглушка one-shot: на подбор голоса — управляемый ответ, промпт запоминается
    private sealed class VoiceCheap : ICheapTextRunner
    {
        public string VoiceAnswer { get; set; } = "";
        public bool VoiceFails { get; set; }
        public string? LastPrompt;
        public int VoiceCalls;

        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            if (actionKey != LocalActionCatalog.PersonaVoice) return Task.FromResult("[]");
            Interlocked.Increment(ref VoiceCalls);
            LastPrompt = prompt;
            return VoiceFails
                ? Task.FromException<string>(new InvalidOperationException("модель недоступна"))
                : Task.FromResult(VoiceAnswer);
        }
        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>("[]");
        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static readonly string[] GeneralVoices = PersonaGeneralVoices();
    private static readonly string[] LiveVoices = ["sofia", "vera", "irina", "denis", "sergey", "vasily"];

    private static string[] PersonaGeneralVoices() =>
        ClaudeHomeServer.Services.Tts.TtsVoiceCatalog.All
            .Where(v => v.Model == ClaudeHomeServer.Services.Tts.TtsVoiceCatalog.General)
            .Select(v => v.Voice).ToArray();

    private async Task<HttpResponseMessage> CandidatesAsync(params string[] exclude) =>
        await _client.PostAsJsonAsync("/api/personas/ai/voice/candidates",
            new { name = "Сергей", role = "Руководитель", exclude });

    // --- Эндпоинт ---

    [Fact]
    public async Task Подбор_ТриОбычных_И_ОдинЖивой()
    {
        _cheap.VoiceAnswer = """
            kirill strict | чёткий тон руководителя
            zahar good | глубокий и уверенный
            alexander | собранный, деловой
            sergey support | живой, поддерживающий
            """;

        var resp = await CandidatesAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var candidates = body.GetProperty("candidates").EnumerateArray().ToList();
        candidates.Select(c => c.GetProperty("voice").GetString())
            .Should().Equal("kirill", "zahar", "alexander");
        candidates[0].GetProperty("role").GetString().Should().Be("strict");
        candidates[0].GetProperty("why").GetString().Should().Be("чёткий тон руководителя");
        candidates[2].GetProperty("role").ValueKind.Should().Be(JsonValueKind.Null);

        var live = body.GetProperty("live");
        live.GetProperty("voice").GetString().Should().Be("sergey");
        live.GetProperty("role").GetString().Should().Be("support");
        body.GetProperty("exhausted").GetBoolean().Should().BeFalse();

        // В промпте есть обе группы: обычные и живые — раздельно
        _cheap.LastPrompt.Should().Contain("Обычные голоса").And.Contain("Живые голоса LiveTTS")
            .And.Contain("masha ·").And.Contain("sofia ·");
    }

    [Fact]
    public async Task ПодобратьЕщё_ПоказанныеНеПредлагаются_НиВПромпте_НиВОтвете()
    {
        // Модель упрямо вернула показанного kirill — разбор обязан его выкинуть
        _cheap.VoiceAnswer = """
            kirill strict | снова он
            masha strict | строгая
            julia | сдержанная
            denis formal | живой
            """;

        var resp = await CandidatesAsync("kirill", "zahar", "alexander", "sergey");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("voice").GetString())
            .Should().Equal("masha", "julia");
        body.GetProperty("live").GetProperty("voice").GetString().Should().Be("denis");

        foreach (var shown in new[] { "kirill", "zahar", "alexander", "sergey" })
            _cheap.LastPrompt.Should().NotContain($"{shown} ·", "показанный голос модели не предлагаем");
        _cheap.LastPrompt.Should().Contain("masha ·");
    }

    [Fact]
    public async Task ВсеОбычныеПоказаны_Исчерпано_ЖивыеЕщёПредлагаются()
    {
        _cheap.VoiceAnswer = "irina narrator | дикторская";

        var resp = await CandidatesAsync(GeneralVoices);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("candidates").GetArrayLength().Should().Be(0);
        body.GetProperty("exhausted").GetBoolean().Should().BeTrue();
        body.GetProperty("live").GetProperty("voice").GetString().Should().Be("irina");
    }

    [Fact]
    public async Task ВсёПоказано_МодельНеЗовём_Исчерпано()
    {
        var resp = await CandidatesAsync([.. GeneralVoices, .. LiveVoices]);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("candidates").GetArrayLength().Should().Be(0);
        body.GetProperty("live").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("exhausted").GetBoolean().Should().BeTrue();
        _cheap.VoiceCalls.Should().Be(0, "запрос к модели стоит денег, а ответ известен заранее");
    }

    [Fact]
    public async Task МодельОтказалась_ПустойСписок200_Мусор502_Сбой502()
    {
        _cheap.VoiceAnswer = "none";
        var none = await CandidatesAsync();
        none.StatusCode.Should().Be(HttpStatusCode.OK);
        (await none.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("candidates")
            .GetArrayLength().Should().Be(0);

        _cheap.VoiceAnswer = "какой-то текст без голосов";
        (await CandidatesAsync()).StatusCode.Should().Be(HttpStatusCode.BadGateway);

        _cheap.VoiceFails = true;
        (await CandidatesAsync()).StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    // --- Автоподбор при создании: живой голос не подставляется никогда ---

    [Fact]
    public async Task СозданиеБезГолоса_МодельНазвалаТолькоЖивой_ПерсонаБезГолоса()
    {
        _cheap.VoiceAnswer = "sofia casual";

        var resp = await _client.PostAsJsonAsync("/api/personas",
            new { name = "Живая" + Guid.NewGuid().ToString("N")[..4] });
        resp.EnsureSuccessStatusCode();
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        _cheap.VoiceCalls.Should().Be(1);
        _cheap.LastPrompt.Should().NotContain("sofia ·").And.NotContain("Живые голоса");
        var owner = _factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        _factory.Services.GetRequiredService<PersonaManager>().Get(id, owner)!.Voice.Should().BeNull();
    }

    // --- Разбор ответа ---

    [Fact]
    public void Разбор_ИмяПерсоныВПояснении_НеСтановитсяГолосом()
    {
        // vera — живой голос и одновременно персона проекта: из пояснения он не берётся
        var (candidates, live) = PersonasCrudService.ParseCandidates("masha friendly | как у Vera из команды");

        candidates.Should().ContainSingle().Which.Should().Be(
            new PersonasCrudService.VoiceCandidate("masha", "friendly", "как у Vera из команды"));
        live.Should().BeNull();
    }

    [Fact]
    public void Разбор_БольшеТрёхОбычных_Повторы_ЧужоеАмплуа_ВторойЖивой()
    {
        var (candidates, live) = PersonasCrudService.ParseCandidates("""
            1. kirill strict | раз
            2. kirill good | повтор
            3. filipp strict | без амплуа
            4. madirus | алиас
            5. masha | лишний
            live: vasily sales | первый живой
            live: denis | второй живой
            """);

        candidates.Select(c => (c.Voice, c.Role)).Should().Equal(
            ("kirill", "strict"), ("filipp", (string?)null), ("madi_ru", (string?)null));
        live!.Voice.Should().Be("vasily");
        live.Role.Should().Be("sales");
    }
}
