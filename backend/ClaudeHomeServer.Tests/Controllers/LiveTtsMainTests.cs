using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Tts;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// LiveTTS в Main: тумблер Yandex:SpeechKit:LiveTts:Enabled решает, видна ли модель в списке
// голосов, в прослушке, в валидации голоса персоны и в описании MCP voice. Хоста два — с
// выключенным и включённым тумблером: это свойство КОНФИГУРАЦИИ инстанса, а не хода.
public sealed class LiveTtsMainTests : IDisposable
{
    private readonly List<TestWebApplicationFactory> _factories = [];

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }

    // Перехват запросов к SpeechKit: тело каждого запроса и ответ с аудио
    private sealed class SpeechKitStub : HttpMessageHandler
    {
        public readonly List<string> Bodies = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"result\":{\"audioChunk\":{\"data\":\"" + Convert.ToBase64String(new byte[] { 1, 2, 3 }) + "\"}}}",
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class SpendSink : ISpendCollector
    {
        public readonly List<SpendRecord> Records = [];
        public void Record(SpendRecord record) { lock (Records) Records.Add(record); }
    }

    private (TestWebApplicationFactory Factory, HttpClient Client) Host(bool liveTts,
        SpeechKitStub? speechKit = null, SpendSink? spend = null)
    {
        var factory = new TestWebApplicationFactory
        {
            ExtraServices = s =>
            {
                if (speechKit is not null)
                    s.AddHttpClient(YandexTtsService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => speechKit);
                if (spend is not null) s.AddSingleton<ISpendCollector>(spend);
            },
        };
        if (liveTts) factory.ExtraConfig[TtsVoiceCatalog.LiveTtsEnabledKey] = "true";
        if (speechKit is not null)
        {
            factory.ExtraConfig["Yandex:SpeechKit:ApiKey"] = "key";
            factory.ExtraConfig["Yandex:SpeechKit:FolderId"] = "folder";
        }
        _factories.Add(factory);
        return (factory, factory.CreateAuthenticatedClient());
    }

    // --- GET /api/tts/voices ---

    [Fact]
    public async Task Voices_ОтдаётModelИSpeedSupported_LiveTtsТолькоПриВключённомТумблере()
    {
        var (_, off) = Host(liveTts: false);
        var (_, on) = Host(liveTts: true);

        var offVoices = await VoicesAsync(off);
        var onVoices = await VoicesAsync(on);

        offVoices.Select(v => v.GetProperty("voice").GetString()).Should().NotContain("sofia");
        var sofia = onVoices.Single(v => v.GetProperty("voice").GetString() == "sofia");
        sofia.GetProperty("model").GetString().Should().Be("livetts");
        sofia.GetProperty("speedSupported").GetBoolean().Should().BeFalse();
        var zahar = onVoices.Single(v => v.GetProperty("voice").GetString() == "zahar");
        zahar.GetProperty("model").GetString().Should().Be("general");
        zahar.GetProperty("speedSupported").GetBoolean().Should().BeTrue();
    }

    private static async Task<List<JsonElement>> VoicesAsync(HttpClient client)
    {
        var body = await client.GetFromJsonAsync<JsonElement>("/api/tts/voices");
        return [.. body.GetProperty("voices").EnumerateArray()];
    }

    // --- POST /api/tts: прослушка и учёт трат ---

    [Fact]
    public async Task Прослушка_ТумблерВыключен_ГолосLiveTts400()
    {
        var (_, client) = Host(liveTts: false);

        var resp = await client.PostAsJsonAsync("/api/tts", new { text = "Привет", voice = "sofia" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Прослушка_СофияСТемпом_200_ТемпНеУходит_ТратаПоМоделиLiveTts()
    {
        var speechKit = new SpeechKitStub();
        var spend = new SpendSink();
        var (_, client) = Host(liveTts: true, speechKit, spend);

        // Старая вкладка шлёт темп всегда: 400 увёл бы её на голос браузера
        var resp = await client.PostAsJsonAsync("/api/tts", new { text = "Привет", voice = "sofia", speed = 1.2 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        speechKit.Bodies.Should().ContainSingle().Which.Should().Contain("\"model\":\"livetts\"").And.NotContain("speed");
        var record = spend.Records.Should().ContainSingle().Subject;
        record.Model.Should().Be("yandex-livetts");
        record.Label.Should().Be("sofia");
        record.CostRub.Should().BeApproximately(0.25, 1e-6);
    }

    [Fact]
    public async Task Прослушка_General_ТратаПрежнейФормы()
    {
        var speechKit = new SpeechKitStub();
        var spend = new SpendSink();
        var (_, client) = Host(liveTts: true, speechKit, spend);

        var resp = await client.PostAsJsonAsync("/api/tts", new { text = "Привет", voice = "zahar", speed = 1.2 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        speechKit.Bodies.Single().Should().Contain("\"speed\":1.2").And.NotContain("model");
        var record = spend.Records.Should().ContainSingle().Subject;
        record.Model.Should().Be("zahar", "у general поле Model исторически хранит голос");
        record.Label.Should().Be("zahar");
    }

    // --- MCP personas: описание voice и валидация голоса ---

    private static async Task<string> ProjectChatAsync(HttpClient client)
    {
        var project = await client.PostAsJsonAsync("/api/projects", new { name = $"lt-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var session = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> RpcAsync(HttpClient client, string sessionId, string method, object? @params)
    {
        var resp = await client.PostAsJsonAsync($"/mcp/personas/{sessionId}",
            new { jsonrpc = "2.0", id = 1, method, @params });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
    }

    private static async Task<string> VoiceDescriptionAsync(HttpClient client)
    {
        var sessionId = await ProjectChatAsync(client);
        var list = await RpcAsync(client, sessionId, "tools/list", new { });
        var create = list.GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "personas_create");
        return create.GetProperty("inputSchema").GetProperty("properties").GetProperty("voice")
            .GetProperty("description").GetString()!;
    }

    [Fact]
    public async Task ToolsList_ОписаниеVoice_ЗависитОтКонфигаИнстанса()
    {
        var (_, off) = Host(liveTts: false);
        var (_, on) = Host(liveTts: true);

        var offText = await VoiceDescriptionAsync(off);
        var onText = await VoiceDescriptionAsync(on);

        offText.Should().NotContain("sofia").And.NotContain("livetts").And.Contain("masha");
        onText.Should().Contain("sofia (livetts: casual/support)").And.Contain("дороже и без темпа");
    }

    [Fact]
    public async Task Update_Sofia_ПоТумблеру()
    {
        var (onFactory, on) = Host(liveTts: true);
        var (offFactory, off) = Host(liveTts: false);

        async Task<(JsonElement Result, Persona Target, PersonaManager Personas)> UpdateAsync(
            TestWebApplicationFactory factory, HttpClient client, object voiceArgs)
        {
            var personas = factory.Services.GetRequiredService<PersonaManager>();
            var owner = factory.Services.GetRequiredService<UserStore>()
                .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
            var target = personas.Create(owner, "П" + Guid.NewGuid().ToString("N")[..5], null, null, null, null, null,
                PersonaScope.Global, null, null, null, false);
            var sessionId = await ProjectChatAsync(client);
            var args = JsonSerializer.SerializeToNode(voiceArgs)!.AsObject();
            args["id"] = target.Id;
            var result = await RpcAsync(client, sessionId, "tools/call", new { name = "personas_update", arguments = args });
            return (result, personas.Get(target.Id, owner)!, personas);
        }

        static bool IsError(JsonElement r) => r.TryGetProperty("isError", out var e) && e.GetBoolean();
        static string TextOf(JsonElement r) => r.GetProperty("content")[0].GetProperty("text").GetString()!;

        var accepted = await UpdateAsync(onFactory, on, new { voice = "sofia", voiceRole = "casual" });
        IsError(accepted.Result).Should().BeFalse(TextOf(accepted.Result));
        accepted.Target.Voice!.Voice.Should().Be("sofia");
        accepted.Target.Voice.Role.Should().Be("casual");

        var badRole = await UpdateAsync(onFactory, on, new { voice = "sofia", voiceRole = "good" });
        IsError(badRole.Result).Should().BeTrue();
        TextOf(badRole.Result).Should().Contain("casual").And.Contain("support");

        var disabled = await UpdateAsync(offFactory, off, new { voice = "sofia" });
        IsError(disabled.Result).Should().BeTrue();
        TextOf(disabled.Result).Should().Contain("недоступен на этом сервере");
        disabled.Target.Voice.Should().BeNull("отказ не трогает персону");
    }
}
