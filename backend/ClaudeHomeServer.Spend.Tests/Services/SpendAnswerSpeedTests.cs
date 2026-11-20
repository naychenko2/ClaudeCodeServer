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
            TurnApiMs = apiMs,
        };

    private static SpendAnswerSpeedDto Speed(SpendStore store, Mock<IModelResolver> llm,
        DateTime? from = null, DateTime? to = null)
    {
        var now = DateTime.UtcNow;
        return new SpendAnswerSpeedService(store, llm.Object)
            .AnswerSpeed(from ?? now.AddDays(-1), to ?? now);
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

    // Хвост ревью aa8af844, п.1: окно по времени, half-open [from, to). Запись ровно на начале
    // границы входит, ровно на конце — нет (её возьмёт следующий снимок). Ответ называет
    // фактическое окно в ISO UTC, а не «дату запроса».
    [Fact]
    public void ГраницаОкна_НачалоВходит_КонецНет_ОкноВОтветеISO()
    {
        var store = NewStore();
        var llm = NewResolver();
        var from = new DateTime(2026, 11, 19, 18, 0, 0, DateTimeKind.Utc);
        store.Record(Rec(from, output: 100, apiMs: 1000));               // начало — входит
        store.Record(Rec(from.AddHours(12), output: 100, apiMs: 1000)); // середина — входит
        store.Record(Rec(from.AddHours(24), output: 100, apiMs: 1000)); // конец [from, to) — нет

        var dto = new SpendAnswerSpeedService(store, llm.Object).AnswerSpeed(from, from.AddHours(24));

        var row = Assert.Single(dto.Models);
        row.Requests.Should().Be(2, "запись ровно на конце окна относится следующему снимку");
        row.ApiMs.Should().Be(2000);
        dto.From.Should().Be("2026-11-19T18:00:00Z");
        dto.To.Should().Be("2026-11-20T18:00:00Z");
    }

    // Хвост ревью aa8af844, п.3 + задача 654c083c: прода-строка 20.11 (до фикса приращения) —
    // несёт НАКОПИТЕЛЬНЫЙ счётчик процесса CLI под прежним именем durationApiMs (466637 → 513390 → …),
    // а не время хода. Знаменатель переехал под имя TurnApiMs: старое имя из модели снято, строка
    // читается штатно (запись остаётся в сторе), но поле null — и в скорость ход не идёт:
    // делить токены одного хода на время всей сессии нельзя.
    [Fact]
    public void СтараяСтрокаСНакопительнымПолем_НеЧитается_ВСкоростьНеИдёт()
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        Directory.CreateDirectory(_dir);
        // Форма прода-строки 20.11: то же сериализование стора (camelCase, null-поля опущены),
        // знаменатель — под старым именем и со значением накопительного счётчика.
        var legacy = "{\"timestamp\":\"" + DateTime.UtcNow.ToString("O") + "\",\"ownerId\":\"u1\","
            + "\"provider\":\"claude\",\"model\":\"opus\",\"source\":\"chat-turn\","
            + "\"inputTokens\":10,\"outputTokens\":100,\"durationMs\":1000,\"durationApiMs\":513390}";
        legacy.Should().Contain("durationApiMs", "фикстура — прода-строка до фикса приращения");
        File.WriteAllText(Path.Combine(_dir, $"turns-{date:yyyy-MM-dd}.jsonl"), legacy + Environment.NewLine);

        var store = new SpendStore(_dir, detailDays: 30);
        var rec = Assert.Single(store.DetailsBetween(date, date));
        rec.TurnApiMs.Should().BeNull();

        // В агрегат она не попадает: рядом запись нового формата — строка и метрики только про неё.
        store.Record(Rec(DateTime.UtcNow, output: 100, apiMs: 1000));
        var row = Assert.Single(Speed(store, NewResolver()).Models);
        row.Requests.Should().Be(1, "ход без поля-знаменателя в скорость не идёт");
        row.ApiMs.Should().Be(1000);
        row.OutputTokens.Should().Be(100);
    }

    // Хвост ревью aa8af844, п.4: бесплатный не-локальный поставщик (free-подписка через CLI)
    // остаётся в карточке облачных, но группа — по паре (поставщик, модель): строка
    // «qwen3-coder» от freellmapi и от minimax — две строки, а не одна усреднённая.
    [Fact]
    public void БесплатныйПоставщик_ВКарточке_НоГруппаПоПареПоставщикМодель()
    {
        var store = NewStore();
        var llm = NewResolver();
        var ts = DateTime.UtcNow;
        store.Record(Rec(ts, provider: "freellmapi", model: "qwen3-coder", source: SpendSources.Free,
            output: 100, apiMs: 1000));  // 100 ток/с — бесплатный
        store.Record(Rec(ts, provider: "minimax", model: "qwen3-coder", output: 400, apiMs: 1000)); // 400 ток/с — платный

        var models = Speed(store, NewResolver()).Models;

        models.Should().HaveCount(2, "один ключ группы — поставщик и модель, не модель");
        models[0].Provider.Should().Be("minimax");
        models[0].Model.Should().Be("qwen3-coder");
        models[0].EndToEndTps.Should().Be(400);
        models[1].Provider.Should().Be("freellmapi");
        models[1].Model.Should().Be("qwen3-coder");
        models[1].EndToEndTps.Should().Be(100);
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
