using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Spend;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Скорость ответа облачных ходов: сессионная (токены ÷ время ответов модели).
// Проверяем агрегат SpendAnswerSpeedService: формулу, порог медианы, исключения
// (локальный провайдер, источник без токенов, ход без времени API) и сортировку.
public class SpendAnswerSpeedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spend-speed-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SpendStore NewStore() => new(_dir, detailDays: 30);

    // Резолвер моделей: по умолчанию модель из записи, локальный провайдер — «local-qwen»
    private static Mock<IModelResolver> NewResolver()
    {
        var llm = new Mock<IModelResolver>();
        llm.Setup(l => l.ResolveModelOrDefault(It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns((string? model, string? provider) => model ?? $"{provider}-default");
        llm.Setup(l => l.IsLocal(It.IsAny<string?>())).Returns(false);
        llm.Setup(l => l.IsLocal("local-qwen")).Returns(true);
        return llm;
    }

    private static SpendRecord Rec(DateTime ts, string provider = "claude", string? model = "opus",
        string source = SpendSources.ChatTurn, long output = 100, long? apiMs = 1000) => new()
        {
            Timestamp = ts,
            OwnerId = "u1",
            Provider = provider,
            Model = model,
            Source = source,
            InputTokens = 10,
            OutputTokens = output,
            DurationMs = apiMs ?? 0,
            DurationApiMs = apiMs,
        };

    private static SpendAnswerSpeedDto Speed(SpendStore store, Mock<IModelResolver> llm,
        DateOnly? from = null, DateOnly? to = null)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return new SpendAnswerSpeedService(store, llm.Object)
            .AnswerSpeed(from ?? today.AddDays(-1), to ?? today);
    }

    [Fact]
    public void EndToEndTps_СчитаетсяПоФормуле_СуммаТокеновНаСуммуВремени()
    {
        var store = NewStore();
        var llm = NewResolver();
        var ts = DateTime.UtcNow;
        store.Record(Rec(ts, output: 100, apiMs: 1000));
        store.Record(Rec(ts, output: 200, apiMs: 2000));

        var row = Assert.Single(Speed(store, llm).Models);

        // (100 + 200) токенов ÷ (1000 + 2000) мс × 1000 = 100 ток/с
        row.EndToEndTps.Should().Be(100);
        row.OutputTokens.Should().Be(300);
        row.ApiMs.Should().Be(3000);
        row.Requests.Should().Be(2);
        row.Provider.Should().Be("claude");
        row.Model.Should().Be("opus");
    }

    [Fact]
    public void Медиана_БерётТолькоЗаписиСТокенамиНеМеньшеПорога()
    {
        var store = NewStore();
        var llm = NewResolver();
        var ts = DateTime.UtcNow;
        // Короткий ответ (10 токенов) занижает медиану — его в медиану не берём
        store.Record(Rec(ts, output: 10, apiMs: 1000));   // 10 ток/с
        store.Record(Rec(ts, output: 32, apiMs: 1000));   // 32 ток/с
        store.Record(Rec(ts, output: 64, apiMs: 1000));   // 64 ток/с

        var row = Assert.Single(Speed(store, llm).Models);

        // Только записи ≥ 32 токенов: [32, 64] → среднее центральных = 48
        row.MedianTps.Should().Be(48);
        // В end-to-end короткий ответ входит — это сумма токенов, а не медиана
        row.Requests.Should().Be(3);
    }

    [Fact]
    public void Медиана_НетЗаписейВышеПорога_Null()
    {
        var store = NewStore();
        var llm = NewResolver();
        store.Record(Rec(DateTime.UtcNow, output: 10, apiMs: 1000));

        var row = Assert.Single(Speed(store, llm).Models);

        row.MedianTps.Should().BeNull();
        row.EndToEndTps.Should().Be(10);
    }

    [Fact]
    public void ЛокальныйПровайдер_Исключён()
    {
        var store = NewStore();
        var llm = NewResolver();
        var ts = DateTime.UtcNow;
        store.Record(Rec(ts, provider: "local-qwen", output: 100, apiMs: 1000));
        store.Record(Rec(ts, provider: "claude", output: 100, apiMs: 1000));

        var models = Speed(store, llm).Models;

        models.Should().ContainSingle();
        models[0].Provider.Should().Be("claude");
    }

    [Fact]
    public void ИсточникБезТокенов_Исключён()
    {
        var store = NewStore();
        var llm = NewResolver();
        var ts = DateTime.UtcNow;
        store.Record(Rec(ts, source: SpendSources.Fal, output: 100, apiMs: 1000));
        store.Record(Rec(ts, source: SpendSources.ChatTurn, output: 100, apiMs: 1000));

        var models = Speed(store, llm).Models;

        models.Should().ContainSingle();
        models[0].Requests.Should().Be(1);
    }

    [Fact]
    public void ЗаписьБезВремениApi_НеУчтена()
    {
        var store = NewStore();
        var llm = NewResolver();
        var ts = DateTime.UtcNow;
        store.Record(Rec(ts, output: 100, apiMs: null));
        store.Record(Rec(ts, output: 100, apiMs: 1000));

        var row = Assert.Single(Speed(store, llm).Models);

        row.Requests.Should().Be(1);
        row.ApiMs.Should().Be(1000);
    }

    [Fact]
    public void МодельРазрешаетсяЧерезРезолв_ПустаяМодельПолучаетДефолт()
    {
        var store = NewStore();
        var llm = NewResolver();
        store.Record(Rec(DateTime.UtcNow, model: null, output: 100, apiMs: 1000));

        var row = Assert.Single(Speed(store, llm).Models);

        row.Model.Should().Be("claude-default");
    }

    [Fact]
    public void Сортировка_ПоСкоростиПоУбыванию()
    {
        var store = NewStore();
        var llm = NewResolver();
        var ts = DateTime.UtcNow;
        store.Record(Rec(ts, model: "slow", output: 100, apiMs: 1000));    // 100 ток/с
        store.Record(Rec(ts, model: "fast", output: 400, apiMs: 1000));    // 400 ток/с

        var models = Speed(store, llm).Models;

        models.Should().HaveCount(2);
        models[0].Model.Should().Be("fast");
        models[1].Model.Should().Be("slow");
    }

    [Fact]
    public void НетДанных_ПустойСписок()
    {
        var store = NewStore();
        var llm = NewResolver();

        var dto = Speed(store, llm);

        dto.Models.Should().BeEmpty();
        dto.MinTokensForMedian.Should().Be(32);
    }
}
