using System.Linq;
using System.Text;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

// Наблюдатель SSE-потока: живые фикстуры vLLM (числа сверены grep-ом с файлами),
// перекрытие input из message_delta, chunking (1/7 байт), CacheHitPerc=null без
// cache_read_input_tokens, мусор не роняет.
public class SseUsageObserverTests
{
    // Фейковые часы: каждый GetUtcNow() +1 с (детерминированный TTFT/GenDuration).
    private sealed class AdvancingTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now += TimeSpan.FromSeconds(1);
    }

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LocalStats", name));

    // (1) Фикстура 1: input из message_delta (660, НЕ 6484 из message_start), output=4,
    //     cache_read=0 (поле есть, значение 0), cache_creation=5824 → CacheHitPerc = 0/(660+0+5824) = 0.
    //     Мутация: не перекрывать input из message_delta (брать из message_start) → InputTokens=6484, тест краснеет.
    [Fact]
    public void Fixture1_InputFromMessageDelta_CacheReadZero()
    {
        var obs = new SseUsageObserver(new AdvancingTime());
        obs.Feed(ReadFixture("vllm-messages-stream.sse"));
        var r = obs.Complete();

        r.InputTokens.Should().Be(660, "input из message_delta (660), не из message_start (6484)");
        r.OutputTokens.Should().Be(4);
        r.CacheReadTokens.Should().Be(0, "cache_read_input_tokens=0 (поле отдано)");
        r.CacheCreationTokens.Should().Be(5824);
        r.CacheHitPerc.Should().Be(0, "cache_read=0 → доля 0");
    }

    // (2) Фикстура 2 (cached): cache_read=5824, cache_creation=0 →
    //     CacheHitPerc = 5824/(660+5824+0) = 5824/6484 ≈ 0.8982.
    [Fact]
    public void Fixture2_Cached_HitPercFromCacheRead()
    {
        var obs = new SseUsageObserver(new AdvancingTime());
        obs.Feed(ReadFixture("vllm-messages-stream-cached.sse"));
        var r = obs.Complete();

        r.InputTokens.Should().Be(660);
        r.OutputTokens.Should().Be(4);
        r.CacheReadTokens.Should().Be(5824);
        r.CacheCreationTokens.Should().Be(0);
        // 5824 / (660 + 5824 + 0) = 5824 / 6484.
        r.CacheHitPerc.Should().BeApproximately(5824.0 / 6484.0, 1e-9);
    }

    // (3) Chunking: поток, разрезанный на куски по 1 байту, даёт тот же итог, что целый.
    //     Кусок может прийти посреди строки — наблюдатель буферизует незавершённую строку.
    [Fact]
    public void Chunking_OneByteAtATime_SameResult()
    {
        var stream = ReadFixture("vllm-messages-stream-cached.sse");

        var whole = new SseUsageObserver(new AdvancingTime());
        whole.Feed(stream);
        var rWhole = whole.Complete();

        var chunked = new SseUsageObserver(new AdvancingTime());
        foreach (var c in stream)
            chunked.Feed(c.ToString());
        var rChunked = chunked.Complete();

        rChunked.InputTokens.Should().Be(rWhole.InputTokens);
        rChunked.OutputTokens.Should().Be(rWhole.OutputTokens);
        rChunked.CacheReadTokens.Should().Be(rWhole.CacheReadTokens);
        rChunked.CacheCreationTokens.Should().Be(rWhole.CacheCreationTokens);
        rChunked.CacheHitPerc.Should().Be(rWhole.CacheHitPerc);
    }

    // (4) Chunking по 7 байт (не по границе строки/JSON) — тоже тот же итог.
    [Fact]
    public void Chunking_SevenBytes_SameResult()
    {
        var stream = ReadFixture("vllm-messages-stream.sse");

        var whole = new SseUsageObserver(new AdvancingTime());
        whole.Feed(stream);
        var rWhole = whole.Complete();

        var chunked = new SseUsageObserver(new AdvancingTime());
        for (var i = 0; i < stream.Length; i += 7)
            chunked.Feed(stream.Substring(i, Math.Min(7, stream.Length - i)));
        var rChunked = chunked.Complete();

        rChunked.InputTokens.Should().Be(rWhole.InputTokens);
        rChunked.OutputTokens.Should().Be(rWhole.OutputTokens);
        rChunked.CacheReadTokens.Should().Be(rWhole.CacheReadTokens);
        rChunked.CacheHitPerc.Should().Be(rWhole.CacheHitPerc);
    }

    // (5) Поток БЕЗ cache_read_input_tokens (поле не отдано) → CacheHitPerc == null («—» на экране).
    [Fact]
    public void NoCacheReadField_CacheHitPercNull()
    {
        var obs = new SseUsageObserver(new AdvancingTime());
        obs.Feed(
            "event: message_start\n" +
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":100,\"output_tokens\":0}}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"x\"}}\n\n" +
            "event: message_delta\n" +
            "data: {\"type\":\"message_delta\",\"usage\":{\"input_tokens\":100,\"output_tokens\":5}}\n\n");
        var r = obs.Complete();

        r.InputTokens.Should().Be(100);
        r.OutputTokens.Should().Be(5);
        r.CacheReadTokens.Should().BeNull("поле cache_read_input_tokens не отдано");
        r.CacheHitPerc.Should().BeNull("нет cache_read → доля null (на экране «—»)");
    }

    // (6) Поля времени: Ttft и GenDuration — конкретные числа (AdvancingTime: +1 с на вызов).
    //     Последовательность: message_start (1×GetUtcNow) → 3×content_block_delta (3×GetUtcNow)
    //     → message_delta (0×GetUtcNow). Итого 4 вызова GetUtcNow:
    //       requestStart = t+1, firstDelta = t+2, lastDelta = t+4.
    //     Ttft = 1 с, GenDuration = 2 с, GenTps = 10/2 = 5.0.
    //     Мутация: убрать один content_block_delta → GenDuration = 1 с, тест краснеет.
    [Fact]
    public void RequestUsage_TimeFields_AreMeaningful()
    {
        var obs = new SseUsageObserver(new AdvancingTime());
        obs.Feed(
            "event: message_start\n" +
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":100}}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"a\"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"b\"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"c\"}}\n\n" +
            "event: message_delta\n" +
            "data: {\"type\":\"message_delta\",\"usage\":{\"input_tokens\":100,\"output_tokens\":10}}\n\n");
        var r = obs.Complete();

        r.Ttft.Should().Be(TimeSpan.FromSeconds(1), "TTFT: старт → первая дельта = 1 с");
        r.GenDuration.Should().Be(TimeSpan.FromSeconds(2), "GenDuration: последняя − первая дельта = 2 с");
        r.GenTps.Should().Be(5.0, "GenTps = 10 токенов / 2 с = 5.0");
        r.OutputTokens.Should().Be(10);
    }

    // (7) Потолок буфера _line: одна «строка» > 64 КБ → наблюдатель отравлен,
    //     Complete отдаёт нулевой итог (все поля null).
    //     Мутация: убрать проверку _line.Length > MaxLineLength → строка обрабатывается,
    //     message_start проходит, InputTokens = 5 (не null) → тест краснеет.
    [Fact]
    public void RequestUsage_LineBufferOverflow_Poisoned()
    {
        var obs = new SseUsageObserver(new AdvancingTime());
        // Комментарий SSE (65537 символов > 64 КБ) + валидный message_start в одном куске.
        // С потолком: _line.Length > MaxLineLength → отравлен, всё null.
        // Без потолка: комментарий игнорируется, message_start обрабатывается → InputTokens = 5.
        var chunk = ": " + new string('p', 65537) + "\n" +
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":5}}}\n\n";
        obs.Feed(chunk);
        var r = obs.Complete();

        r.InputTokens.Should().BeNull("потолок _line → отравлен, все поля null");
        r.OutputTokens.Should().BeNull();
        r.Ttft.Should().BeNull();
        r.GenDuration.Should().BeNull();
    }

    // (8) Мусор не роняет: [DONE], event: без data:, JSON-обрыв, CRLF, комментарии —
    //     наблюдатель остаётся в рабочем состоянии и отдаёт корректный итог.
    [Fact]
    public void Garbage_DoesNotThrow()
    {
        var obs = new SseUsageObserver(new AdvancingTime());
        var garbage =
            "event: message_start\r\n" +                       // CRLF
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":50}}}\r\n\r\n" +
            ": comment line\r\n\r\n" +                          // SSE-комментарий
            "event: some_event\r\n\r\n" +                       // event: без data:
            "data: {\"type\":\"content_block_delta\"\r\n\r\n" + // JSON-обрыв (незакрытый объект)
            "data: [DONE]\r\n\r\n" +                            // [DONE]
            "event: content_block_delta\r\n" +
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"y\"}}\r\n\r\n" +
            "event: message_delta\r\n" +
            "data: {\"type\":\"message_delta\",\"usage\":{\"input_tokens\":50,\"output_tokens\":3}}\r\n\r\n";
        obs.Feed(garbage);
        var r = obs.Complete();

        r.InputTokens.Should().Be(50);
        r.OutputTokens.Should().Be(3);
    }

    // (9) Потоковое UTF-8: многобайтовый символ (кириллица) разрезан на границе кусков по
    //      1 байту — декодированный ввод тот же, что у целого (Decoder держит хвост символа).
    //      Мутация: заменить Decoder на Encoding.GetString по кускам → символ рвётся, тест краснеет.
    [Fact]
    public void MultibyteSymbol_SplitAcrossOneByteChunks_SameDecodedStream()
    {
        var stream = ReadFixture("vllm-messages-stream.sse"); // содержит кириллицу («Пари…»)
        var bytes = Encoding.UTF8.GetBytes(stream);

        var whole = new SseUsageObserver(new AdvancingTime());
        whole.Feed(stream);

        var chunked = new SseUsageObserver(new AdvancingTime());
        for (var i = 0; i < bytes.Length; i++)
            chunked.Feed(bytes.AsSpan(i, 1));

        // Декодированный ввод идентичен: многобайтовые символы не рвутся на границе 1-байтовых кусков.
        chunked.DecodedInput.ToString().Should().Be(whole.DecodedInput.ToString(),
            "потоковое UTF-8 держит хвост символа между кусками");
    }

    // (10) Потолок накопителя data: (MaxDataLength = 256 КБ): событие с НЕСКОЛЬКИМИ data:-
    //      строками (каждая < 64 КБ line-cap), суммой > 256 КБ → наблюдатель «отравлен».
    //      Склеенный data: — валидный JSON (разрез по границам whitespace: запятые массива),
    //      поэтому БЕЗ потолка он разбирается (InputTokens=5), С потолком — null.
    //      Мутация: убрать проверку _data.Length > MaxDataLength → JSON разбирается, тест краснеет.
    [Fact]
    public void RequestUsage_DataBufferOverflow_Poisoned()
    {
        var obs = new SseUsageObserver(new AdvancingTime());
        // Валидный JSON-объект с длинным числовым массивом, разрезанный на data:-строки
        // по запятым (whitespace): склейка с \n остаётся валидным JSON. Сумма > 256 КБ,
        // каждая строка < 64 КБ. Кормим ПОСТРОЧНО: иначе _line-потолок (64 КБ) сработал бы
        // на одном большом Feed раньше data:-потолка.
        var row = string.Join(",", Enumerable.Repeat("1", 15000)); // ~30 КБ на строку
        var lines = new List<string>
        {
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":5},\"arr\":[",
        };
        for (var i = 0; i < 9; i++)
            lines.Add("data: " + row + (i < 8 ? "," : ""));
        lines.Add("data: ]}}");
        lines.Add(""); // граница события
        foreach (var line in lines)
            obs.Feed(line + "\n");
        var r = obs.Complete();

        r.InputTokens.Should().BeNull("потолок data: (256 КБ) → отравлен, все поля null");
        r.OutputTokens.Should().BeNull();
        r.Ttft.Should().BeNull();
        r.GenDuration.Should().BeNull();
    }
}
