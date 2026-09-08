using System.Net;
using System.Text;
using ClaudeHomeServer.Services.Tts;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Синтез речи Yandex SpeechKit: конфигурированность, нарезка текста под лимит v3
// (249 символов) и разбор потокового ответа
public class YandexTtsServiceTests
{
    private static YandexTtsService Make(Dictionary<string, string?>? values = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build();
        return new YandexTtsService(Mock.Of<IHttpClientFactory>(), config,
            NullLogger<YandexTtsService>.Instance);
    }

    [Fact]
    public void IsConfigured_БезКлючаИлиFolderId_False()
    {
        Make().IsConfigured.Should().BeFalse();
        Make(new() { ["Yandex:SpeechKit:ApiKey"] = "key" }).IsConfigured
            .Should().BeFalse("без folderId запрос к SpeechKit не собрать");
        Make(new() { ["Yandex:SpeechKit:FolderId"] = "folder" }).IsConfigured.Should().BeFalse();
        Make(new()
        {
            ["Yandex:SpeechKit:ApiKey"] = "key",
            ["Yandex:SpeechKit:FolderId"] = "folder",
        }).IsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task Synthesize_БезКонфига_НиАудио_НиОплаченныхЗапросов()
    {
        var res = await Make().SynthesizeAsync("Привет", new VoiceChoice("zahar", null, 1.0));
        res.Audio.Should().BeNull();
        // Ни одного запроса не ушло — значит и счёт выставлять не за что
        res.BilledRequests.Should().Be(0);
        res.Rub.Should().Be(0);
    }

    // --- SplitForSynthesis: нарезка под лимит 249 символов на запрос (v3) ---

    [Fact]
    public void Split_КороткийТекст_ОднимКуском()
    {
        YandexTtsService.SplitForSynthesis("Привет. Как дела?", 5000)
            .Should().ContainSingle().Which.Should().Be("Привет. Как дела?");
    }

    [Fact]
    public void Split_ПустойТекст_БезКусков()
    {
        YandexTtsService.SplitForSynthesis("   ", 5000).Should().BeEmpty();
        YandexTtsService.SplitForSynthesis("", 5000).Should().BeEmpty();
    }

    [Fact]
    public void Split_ДлинныйТекст_РежетсяПоГраницамПредложений()
    {
        // Три предложения по ~40 символов, лимит 100: первые два влезают вместе, третье отдельно
        var s1 = "Первое предложение ровно про погоду дня.";
        var s2 = "Второе предложение о планах на вечер.";
        var s3 = "Третье предложение завершает рассказ.";
        var chunks = YandexTtsService.SplitForSynthesis($"{s1} {s2} {s3}", 100);

        chunks.Should().HaveCount(2);
        chunks[0].Should().Be($"{s1} {s2}");
        chunks[1].Should().Be(s3);
        chunks.Should().OnlyContain(c => c.Length <= 100);
    }

    [Fact]
    public void Split_СловоДлиннееЛимита_РежетсяЖёстко()
    {
        var monster = new string('а', 250); // одно «слово» без пробелов и знаков препинания
        var chunks = YandexTtsService.SplitForSynthesis(monster, 100);

        chunks.Should().HaveCount(3);
        chunks.Should().OnlyContain(c => c.Length <= 100);
        string.Concat(chunks).Should().Be(monster, "текст не должен теряться при жёсткой нарезке");
    }

    [Fact]
    public void Split_ДлинноеПредложение_РежетсяПоГраницеСлова()
    {
        // Предложение без точек длиннее лимита: на лимите 249 это обычный случай (модель
        // пишет длинными периодами), и рез посреди слова слышно сразу
        var text = string.Join(" ", Enumerable.Repeat("словечко", 40)); // 359 символов
        var chunks = YandexTtsService.SplitForSynthesis(text, 100);

        chunks.Should().OnlyContain(c => c.Length <= 100);
        chunks.Should().OnlyContain(c => c.Split(' ').All(w => w == "словечко"),
            "слова не должны рваться пополам");
        string.Join(" ", chunks).Should().Be(text, "текст не должен теряться");
    }

    [Fact]
    public void Split_ТекстБольшеЛимита_НичегоНеТеряет()
    {
        var text = string.Join(" ", Enumerable.Range(0, 300).Select(i => $"Предложение номер {i}."));
        text.Length.Should().BeGreaterThan(YandexTtsService.MaxCharsPerRequest);

        var chunks = YandexTtsService.SplitForSynthesis(text, YandexTtsService.MaxCharsPerRequest);

        chunks.Should().HaveCountGreaterThan(1);
        chunks.Should().OnlyContain(c => c.Length <= YandexTtsService.MaxCharsPerRequest);
        // Склейка кусков с точностью до пробелов равна исходнику
        string.Join(" ", chunks).Should().Be(text);
    }

    // --- ExtractAudio: разбор потокового ответа v3 ---
    //
    // Ответ приходит строками JSON, аудио лежит в result.audioChunk.data (base64).
    // Принцип разбора: любая неожиданность — отказ ЦЕЛИКОМ, потому что половина фразы
    // в ушах хуже честного фолбэка на голос браузера.

    private static string Chunk(params byte[] bytes) =>
        "{\"result\":{\"audioChunk\":{\"data\":\"" + Convert.ToBase64String(bytes) + "\"}}}";

    [Fact]
    public void Extract_ОднаСтрока_ОтдаётБайтыАудио()
    {
        YandexTtsService.ExtractAudio(Chunk(1, 2, 3)).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Extract_НесколькоСтрок_СклеиваетПоПорядку()
    {
        var body = $"{Chunk(1, 2)}\n{Chunk(3)}\n{Chunk(4, 5)}";
        YandexTtsService.ExtractAudio(body).Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public void Extract_ОшибкаВнутриОтвета_Null()
    {
        // Статус 200, а в теле отказ: без этой ветки получили бы «полкуска озвучки»
        const string body = """{"error":{"grpcCode":3,"message":"Too long text"}}""";
        YandexTtsService.ExtractAudio(body).Should().BeNull();
    }

    [Fact]
    public void Extract_ОборванныйПоток_Null()
    {
        var body = $"{Chunk(1, 2)}\n{{\"result\":{{\"audioChu";
        YandexTtsService.ExtractAudio(body).Should().BeNull("оборванный поток — это отказ, а не половина фразы");
    }

    [Fact]
    public void Extract_ПустоеИлиБезАудио_Null()
    {
        YandexTtsService.ExtractAudio("").Should().BeNull();
        YandexTtsService.ExtractAudio("   ").Should().BeNull();
        YandexTtsService.ExtractAudio("""{"result":{}}""").Should().BeNull();
    }

    // --- учёт оплаченных запросов: SpeechKit тарифицируется ЗА ЗАПРОС ---

    private sealed class StubHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(Interlocked.Increment(ref Calls)));
    }

    private sealed class StubHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static YandexTtsService MakeLive(StubHandler handler, double? rubPerRequest = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Yandex:SpeechKit:ApiKey"] = "key",
            ["Yandex:SpeechKit:FolderId"] = "folder",
        };
        if (rubPerRequest is { } r) values["Yandex:SpeechKit:RubPerRequest"] = r.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new YandexTtsService(new StubHttpFactory(handler), config,
            NullLogger<YandexTtsService>.Instance);
    }

    private static HttpResponseMessage AudioOk() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(Chunk(1, 2, 3), Encoding.UTF8, "application/json"),
    };

    // Текст на два запроса: два предложения, каждое длиннее половины лимита в 249 символов
    private static string TwoChunkText() =>
        new string('а', 200) + ". " + new string('б', 200) + ".";

    [Fact]
    public async Task Synthesize_ВсеКускиУспешны_СчитаетКаждыйЗапрос()
    {
        var handler = new StubHandler(_ => AudioOk());
        var res = await MakeLive(handler, rubPerRequest: 0.1626)
            .SynthesizeAsync(TwoChunkText(), new VoiceChoice("zahar", null, 1.0));

        handler.Calls.Should().Be(2);
        res.Audio.Should().NotBeNull();
        res.BilledRequests.Should().Be(2);
        res.Rub.Should().BeApproximately(0.3252, 1e-6);
    }

    [Fact]
    public async Task Synthesize_ОбрывНаВторомКуске_ПервыйЗапросВсёРавноОплачен()
    {
        // Яндекс уже принял первый запрос и выставит за него счёт — озвучка провалилась,
        // а деньги списаны. Промолчать здесь значит занижать расход.
        var handler = new StubHandler(call => call == 1
            ? AudioOk()
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom", Encoding.UTF8, "text/plain"),
            });

        var res = await MakeLive(handler, rubPerRequest: 0.1626)
            .SynthesizeAsync(TwoChunkText(), new VoiceChoice("zahar", null, 1.0));

        res.Audio.Should().BeNull("половина фразы хуже честного фолбэка на голос браузера");
        res.BilledRequests.Should().Be(1);
        res.Rub.Should().BeApproximately(0.1626, 1e-6);
    }

    [Fact]
    public async Task Synthesize_ОтказНаПервомЖеЗапросе_НичегоНеОплачено()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("no role", Encoding.UTF8, "text/plain"),
        });

        var res = await MakeLive(handler).SynthesizeAsync("Привет.", new VoiceChoice("zahar", null, 1.0));

        res.BilledRequests.Should().Be(0, "отказ Яндекс не тарифицирует");
        res.Rub.Should().Be(0);
    }

    [Fact]
    public async Task Synthesize_ОтветБезАудио_ЗапросВсёРавноОплачен()
    {
        // 200 с ошибкой в теле: аудио нет, но запрос принят — значит и посчитан
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"error":{"grpcCode":3,"message":"Too long text"}}""",
                Encoding.UTF8, "application/json"),
        });

        var res = await MakeLive(handler, rubPerRequest: 0.1626)
            .SynthesizeAsync("Привет.", new VoiceChoice("zahar", null, 1.0));

        res.Audio.Should().BeNull();
        res.BilledRequests.Should().Be(1);
    }
}
