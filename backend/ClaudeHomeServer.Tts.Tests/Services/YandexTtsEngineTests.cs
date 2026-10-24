using System.Net;
using System.Text;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.Tts;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Шов ITtsEngine для модуля «Звук»: синтез через YandexTtsService, отказы на кривой вход до
// запроса к Яндексу и регистрация в подсистеме Tts
public class YandexTtsEngineTests
{
    private sealed class StubHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        public readonly List<string> Bodies = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return respond(Interlocked.Increment(ref Calls));
        }
    }

    private sealed class StubHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static IConfiguration Config(bool configured, bool liveTts = false)
    {
        var values = configured
            ? new Dictionary<string, string?>
            {
                ["Yandex:SpeechKit:ApiKey"] = "key",
                ["Yandex:SpeechKit:FolderId"] = "folder",
                ["Yandex:SpeechKit:RubPerRequest"] = "0.1626",
            }
            : [];
        if (liveTts) values[TtsVoiceCatalog.LiveTtsEnabledKey] = "true";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static YandexTtsEngine Make(StubHandler handler, bool configured = true, bool liveTts = false)
    {
        var config = Config(configured, liveTts);
        return new(new YandexTtsService(new StubHttpFactory(handler), config,
            NullLogger<YandexTtsService>.Instance), config);
    }

    private static HttpResponseMessage AudioOk() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"result\":{\"audioChunk\":{\"data\":\"" + Convert.ToBase64String(new byte[] { 1, 2, 3 }) + "\"}}}",
            Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task Synthesize_ЧерезШов_ОтдаётMp3ИЦенуИПередаётГолосРольСкорость()
    {
        var handler = new StubHandler(_ => AudioOk());
        ITtsEngine engine = Make(handler);

        var res = await engine.SynthesizeAsync("Привет, мир.", "marina", "whisper", 1.5, CancellationToken.None);

        res.Error.Should().BeNull();
        res.Audio.Should().Equal(1, 2, 3);
        res.Rub.Should().BeApproximately(0.1626, 1e-6);
        handler.Calls.Should().Be(1);
        handler.Bodies[0].Should().Contain("\"voice\":\"marina\"")
            .And.Contain("\"role\":\"whisper\"").And.Contain("\"speed\":1.5");
    }

    [Fact]
    public async Task Synthesize_АлиасГолоса_УходитКаноническимИменем()
    {
        var handler = new StubHandler(_ => AudioOk());
        var res = await Make(handler).SynthesizeAsync("Привет.", "madirus", null, 1.0, CancellationToken.None);

        res.Error.Should().BeNull();
        handler.Bodies[0].Should().Contain("\"voice\":\"madi_ru\"").And.NotContain("role");
    }

    [Fact]
    public async Task Synthesize_ТекстНаПределе3000_Проходит_АДлиннее_Отказ()
    {
        var handler = new StubHandler(_ => AudioOk());
        var engine = Make(handler);
        engine.MaxChars.Should().Be(3000);

        var atLimit = await engine.SynthesizeAsync(new string('а', 3000), "zahar", null, 1.0, CancellationToken.None);
        atLimit.Error.Should().BeNull();
        var callsAtLimit = handler.Calls;

        var over = await engine.SynthesizeAsync(new string('а', 3001), "zahar", null, 1.0, CancellationToken.None);
        over.Audio.Should().BeNull();
        over.Error.Should().Contain("3000");
        over.Rub.Should().Be(0);
        handler.Calls.Should().Be(callsAtLimit, "лишний текст отвергается до запроса к Яндексу");
    }

    [Fact]
    public async Task Synthesize_НеизвестныйГолос_ОтказБезЗапроса()
    {
        var handler = new StubHandler(_ => AudioOk());
        var res = await Make(handler).SynthesizeAsync("Привет.", "alice", null, 1.0, CancellationToken.None);

        res.Audio.Should().BeNull();
        res.Error.Should().Contain("alice");
        handler.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("zahar", "evil")]      // роль есть у других голосов, но не у этого
    [InlineData("filipp", "good")]     // голос без ролей вовсе
    [InlineData("jane", "angry")]      // роли нет ни у кого
    public async Task Synthesize_ЧужаяГолосуРоль_ОтказБезЗапроса(string voice, string role)
    {
        var handler = new StubHandler(_ => AudioOk());
        var res = await Make(handler).SynthesizeAsync("Привет.", voice, role, 1.0, CancellationToken.None);

        res.Audio.Should().BeNull();
        res.Error.Should().Contain(role);
        handler.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(3.5)]
    [InlineData(double.NaN)]
    public async Task Synthesize_СкоростьВнеПределов_Отказ(double speed)
    {
        var handler = new StubHandler(_ => AudioOk());
        var res = await Make(handler).SynthesizeAsync("Привет.", "zahar", null, speed, CancellationToken.None);

        res.Error.Should().NotBeNull();
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Synthesize_ЯндексОтказал_ОшибкаСПричиной()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom", Encoding.UTF8, "text/plain"),
        });
        var res = await Make(handler).SynthesizeAsync("Привет.", "zahar", null, 1.0, CancellationToken.None);

        res.Audio.Should().BeNull();
        res.Error.Should().NotBeNullOrWhiteSpace();
        res.Rub.Should().Be(0);
    }

    [Fact]
    public async Task НеНастроен_ConfiguredFalse_ИСинтезОтказБезИсключения()
    {
        var handler = new StubHandler(_ => AudioOk());
        var engine = Make(handler, configured: false);

        engine.Configured.Should().BeFalse();
        var res = await engine.SynthesizeAsync("Привет.", "zahar", null, 1.0, CancellationToken.None);
        res.Error.Should().NotBeNull();
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public void Каталог_19ГолосовИПятьАмплуа()
    {
        var engine = Make(new StubHandler(_ => AudioOk()));

        engine.Voices.Select(v => v.Voice).Should().BeEquivalentTo(
            "alena", "marina", "jane", "omazh", "dasha", "julia", "lera", "masha",
            "saule_ru", "zamira_ru", "zhanar_ru", "yulduz_ru",
            "zahar", "filipp", "ermil", "madi_ru", "alexander", "kirill", "anton");
        engine.Roles.Should().BeEquivalentTo("good", "evil", "friendly", "whisper", "strict");
        engine.MinSpeed.Should().Be(0.1);
        engine.MaxSpeed.Should().Be(3.0);
    }

    // --- LiveTTS через шов: путь модуля «Звук» собирает VoiceChoice в обход VoiceResolver ---

    [Fact]
    public async Task LiveTts_СофияСоСкоростьюБезРоли_ЕстьModel_НетSpeed_НетRole()
    {
        // Правило модели живёт в BuildPayload: шов передаёт скорость как есть, а в теле её
        // быть не должно — LiveTTS ответил бы 400 на любой speed
        var handler = new StubHandler(_ => AudioOk());
        var res = await Make(handler, liveTts: true)
            .SynthesizeAsync("Привет.", "sofia", null, 1.2, CancellationToken.None);

        res.Error.Should().BeNull();
        res.Rub.Should().BeApproximately(0.25, 1e-6, "цена LiveTTS — своя, по модели запроса");
        handler.Bodies[0].Should().Contain("\"model\":\"livetts\"")
            .And.Contain("\"voice\":\"sofia\"")
            .And.NotContain("speed")
            .And.NotContain("role");
    }

    [Fact]
    public void LiveTts_ТумблерВыключен_ГолосовLiveTtsВШвеНет()
    {
        var off = Make(new StubHandler(_ => AudioOk()));
        off.Voices.Should().NotContain(v => TtsVoiceCatalog.ModelFor(v.Voice) == TtsVoiceCatalog.LiveTts);
        off.Roles.Should().NotContain("casual");

        var on = Make(new StubHandler(_ => AudioOk()), liveTts: true);
        on.Voices.Select(v => v.Voice).Should().Contain(["sofia", "vera", "irina", "denis", "sergey", "vasily"]);
        on.Roles.Should().Contain(["casual", "support", "sales", "formal", "narrator"]);
    }

    [Fact]
    public async Task LiveTts_ТумблерВыключен_СинтезГолосомLiveTts_ОтказБезЗапроса()
    {
        var handler = new StubHandler(_ => AudioOk());
        var res = await Make(handler).SynthesizeAsync("Привет.", "sofia", null, 1.0, CancellationToken.None);

        res.Audio.Should().BeNull();
        res.Error.Should().Contain("sofia");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public void Подсистема_РегистрируетШов_БезКлючаConfiguredFalse()
    {
        var config = Config(configured: false);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(config);
        new TtsSubsystem().Register(services, config);

        using var sp = services.BuildServiceProvider();
        var engine = sp.GetService<ITtsEngine>();
        engine.Should().BeOfType<YandexTtsEngine>();
        engine!.Configured.Should().BeFalse();
    }
}
