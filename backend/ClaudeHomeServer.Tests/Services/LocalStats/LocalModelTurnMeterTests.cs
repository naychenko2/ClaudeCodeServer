using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Services.Gateway;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>
/// Счётчик хода локальной модели (флаг local-model-stats, шаг 17): фазы, суммирование
/// запросов хода, чистка по SessionDeleted, дроссель живых событий (≤1 Гц) и игнор
/// бесхозных запросов. Состояние — по сессии: параллельные чаты не смешиваются.
/// </summary>
public sealed class LocalModelTurnMeterTests
{
    // Фейковые часы: дроссель живых событий проверяем на них (без реального Task.Delay).
    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    // Фейковый reader: управляем Waiting (сколько прогонов ждут GPU).
    private sealed class FakeReader : ILocalModelStatsReader
    {
        public int Waiting;
        public LocalModelStatsCurrent Current => new() { Waiting = Waiting };
        public IReadOnlyList<LocalModelStatsPoint> GetHistory(TimeSpan window, long? sinceUnixMs = null) => [];
        public LocalModelStatsAverage GetAverageSpeed(TimeSpan window) => new();
    }

    // Фейковый sink: ловим OnLive (фаза, tps, waiting) и OnTurn (итог).
    private sealed class SpySink : ILocalModelLiveSink
    {
        public readonly List<(string Session, string Phase, double? Tps, int? Waiting)> Live = [];
        public readonly List<(string Session, LocalModelTurnSummary Summary)> Turns = [];
        public void OnLive(string sessionId, string phase, double? genTps, int? waitingTotal)
            => Live.Add((sessionId, phase, genTps, waitingTotal));
        public void OnTurn(string sessionId, LocalModelTurnSummary summary)
            => Turns.Add((sessionId, summary));
    }

    private readonly TurnEventBus _bus = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero));
    private readonly FakeReader _reader = new();
    private readonly SpySink _sink = new();
    private readonly MutableOptionsMonitor<LocalModelStatsOptions> _options =
        new(new LocalModelStatsOptions { Enabled = true });
    private readonly TurnTokenService _tokens;
    private readonly LocalModelTurnMeter _meter;

    public LocalModelTurnMeterTests()
    {
        _tokens = new TurnTokenService(_bus, _time);
        _meter = new LocalModelTurnMeter(_tokens, _options, _time, _reader, _sink);
        _meter.Subscribe(_bus);
    }

    // Токен хода для сессии: turnId → (sessionId, ownerId).
    private string Issue(string sessionId)
    {
        var issued = _tokens.Issue("owner", sessionId, "device");
        return issued.Grant.TurnId;
    }

    // Уникальный id запроса (как выдаёт шлюз): каждый HTTP-запрос — свой requestId.
    private int _reqCounter;
    private string ReqId() => $"req-{++_reqCounter}";

    private static RequestUsage Usage(int outTokens, int? ttftMs = null, TimeSpan? gen = null) => new()
    {
        OutputTokens = outTokens,
        InputTokens = 100,
        Ttft = ttftMs is null ? null : TimeSpan.FromMilliseconds(ttftMs.Value),
        GenDuration = gen,
    };

    // (1) Два параллельных запроса из двух разных сессий не смешиваются.
    [Fact]
    public async Task ПараллельныеСессии_НеСмешиваются()
    {
        var tA = Issue("chat-A");
        var tB = Issue("chat-B");
        var rA = ReqId();
        var rB = ReqId();

        _meter.OnRequestStarted(rA, tA);
        _meter.OnRequestStarted(rB, tB);
        _meter.OnRequestCompleted(rA, tA, Usage(10));
        _meter.OnRequestCompleted(rB, tB, Usage(20));

        await _bus.PublishAsync(new TurnCompleted(new TurnContext("chat-A", "owner", 1, 0), "success"));
        await _bus.PublishAsync(new TurnCompleted(new TurnContext("chat-B", "owner", 1, 0), "success"));

        _sink.Turns.Should().HaveCount(2);
        var a = _sink.Turns.First(t => t.Session == "chat-A").Summary;
        var b = _sink.Turns.First(t => t.Session == "chat-B").Summary;
        a.OutTokens.Should().Be(10, "сумма запросов chat-A");
        b.OutTokens.Should().Be(20, "сумма запросов chat-B");
    }

    // (2) Ход из нескольких запросов суммируется.
    [Fact]
    public async Task ХодИзНесколькихЗапросов_Суммируется()
    {
        var t = Issue("chat-A");
        var r1 = ReqId();
        var r2 = ReqId();

        // Два запроса одного хода (один turnId, разные requestId): оба завершаются, потом ход.
        _meter.OnRequestStarted(r1, t);
        _meter.OnRequestCompleted(r1, t, Usage(10, ttftMs: 50));
        _meter.OnRequestStarted(r2, t);
        _meter.OnRequestCompleted(r2, t, Usage(15, ttftMs: 30));

        await _bus.PublishAsync(new TurnCompleted(new TurnContext("chat-A", "owner", 1, 0), "success"));

        var s = _sink.Turns.Single(x => x.Session == "chat-A").Summary;
        s.OutTokens.Should().Be(25, "10 + 15");
        // Оба запроса стартовали одновременно (t=0): ttft первого по времени старта (50),
        // а не минимальный (30) — формулировка «минимального» неверна.
        s.TtftMs.Should().Be(50, "TTFT запроса, начавшегося раньше (оба t=0 → первый по порядку)");
    }

    // (2b) Второй запрос быстрее первого (меньший ttft) — итог берёт ttft ПЕРВОГО
    //      (ранее стартовавшего), а не минимальный. Два разных turnId одной сессии.
    //      Мутация: вернуть Math.Min → тест краснеет (берёт 30, а не 50).
    [Fact]
    public async Task ВторойЗапросБыстрее_ИтогБерётTtftПервого()
    {
        var t1 = Issue("chat-A");
        var t2 = Issue("chat-A");
        var r1 = ReqId();
        var r2 = ReqId();

        // Первый запрос стартует раньше (t=0), второй — позже (t=1с).
        _meter.OnRequestStarted(r1, t1);
        _time.Advance(TimeSpan.FromSeconds(1));
        _meter.OnRequestStarted(r2, t2);

        // Первый (ранний старт) медленнее: ttft=50. Второй (поздний старт) быстрее: ttft=30.
        _meter.OnRequestCompleted(r1, t1, Usage(10, ttftMs: 50));
        _meter.OnRequestCompleted(r2, t2, Usage(15, ttftMs: 30));

        await _bus.PublishAsync(new TurnCompleted(new TurnContext("chat-A", "owner", 1, 0), "success"));

        var s = _sink.Turns.Single(x => x.Session == "chat-A").Summary;
        // Минимальный ttft = 30 (второй), но он стартовал позже → итог берёт ttft первого (50).
        s.TtftMs.Should().Be(50, "ttft запроса с ранним стартом, не минимальный");
    }

    // (2c) QueueMsApprox: МИЛЛИСЕКУНДЫ, округлённые вверх до целой секунды (ceil(ms/1000)*1000):
    //      1 мс → 1000, 1000 мс → 1000, 1001 мс → 2000, 0 → 0.
    //      Мутация: убрать округление (просто (int)ms) → тест краснеет.
    [Theory]
    [InlineData(1, 1000)]
    [InlineData(1000, 1000)]
    [InlineData(1001, 2000)]
    [InlineData(0, 0)]
    public async Task QueueMsApprox_ОкруглениеВверхДоСекунды(int queueMs, int expected)
    {
        var t = Issue("chat-A");
        var r = ReqId();
        _reader.Waiting = 1; // фаза queued: QueuedStartAt фиксируется
        _meter.OnRequestStarted(r, t);
        if (queueMs > 0)
            _time.Advance(TimeSpan.FromMilliseconds(queueMs));
        // Запрос так и не получил первый токен: всё время от старта (queued) уходит в QueueMs.
        _meter.OnRequestCompleted(r, t, Usage(10)); // QueueMs += (now − QueuedStartAt) = queueMs

        await _bus.PublishAsync(new TurnCompleted(new TurnContext("chat-A", "owner", 1, 0), "success"));

        var s = _sink.Turns.Single(x => x.Session == "chat-A").Summary;
        s.QueueMsApprox.Should().Be(expected, $"округление вверх до секунды: {queueMs} мс → {expected}");
    }

    // (3) SessionDeleted чистит состояние сессии.
    [Fact]
    public async Task SessionDeleted_ЧиститСостояние()
    {
        var t = Issue("chat-A");
        var r = ReqId();
        _meter.OnRequestStarted(r, t);
        _meter.OnRequestCompleted(r, t, Usage(10));

        await _bus.PublishAsync(new SessionDeleted(new TurnContext("chat-A", "owner", 0, 0, null)));

        // После чистки новый ход той же сессии не видит старых токенов.
        var t2 = Issue("chat-A");
        var r2 = ReqId();
        _meter.OnRequestStarted(r2, t2);
        _meter.OnRequestCompleted(r2, t2, Usage(5));
        await _bus.PublishAsync(new TurnCompleted(new TurnContext("chat-A", "owner", 2, 0), "success"));

        var last = _sink.Turns.Last(x => x.Session == "chat-A").Summary;
        last.OutTokens.Should().Be(5, "после SessionDeleted накопитель чист: только новый ход");
    }

    // (4) Живые события не чаще 1 Гц на фейковых часах.
    [Fact]
    public void ЖивыеСобытия_НеЧаще1Гц()
    {
        var t = Issue("chat-A");
        var r1 = ReqId();
        var r2 = ReqId();
        var r3 = ReqId();
        var r4 = ReqId();

        _meter.OnRequestStarted(r1, t); // live #1 (t=0)
        _time.Advance(TimeSpan.FromMilliseconds(500));
        _meter.OnFirstToken(r1, t);      // live #2? (t=0.5) — <1с, дроссель
        _time.Advance(TimeSpan.FromMilliseconds(500));
        var t2 = Issue("chat-A");
        _meter.OnRequestStarted(r2, t2); // live #3? (t=1.0) — ≥1с, проходит
        _time.Advance(TimeSpan.FromMilliseconds(500));
        var t3 = Issue("chat-A");
        _meter.OnFirstToken(r3, t3);      // (t=1.5) — <1с от #3, дроссель

        // На сессию chat-A: live #1 (t=0) и #3 (t=1.0) — ровно 2, не 4.
        _sink.Live.Where(l => l.Session == "chat-A").Should().HaveCount(2, "дроссель 1 Гц");
    }

    // (5) Фазы queued/prefill/generating. Часы +1с между фазами, чтобы дроссель 1 Гц
    //     живых событий не сдержал смену фазы.
    [Fact]
    public void Фазы_QueuedPrefillGenerating()
    {
        var t = Issue("chat-A");
        var r1 = ReqId();

        // Ждущих нет → prefill.
        _reader.Waiting = 0;
        _meter.OnRequestStarted(r1, t);
        _sink.Live.Last().Phase.Should().Be("prefill");

        // Ждущие есть → queued.
        _time.Advance(TimeSpan.FromSeconds(1));
        _reader.Waiting = 3;
        var t2 = Issue("chat-A");
        var r2 = ReqId();
        _meter.OnRequestStarted(r2, t2);
        _sink.Live.Last().Phase.Should().Be("queued");

        // Первый токен → generating.
        _time.Advance(TimeSpan.FromSeconds(1));
        _meter.OnFirstToken(r2, t2);
        _sink.Live.Last().Phase.Should().Be("generating");
    }

    // (6) Запрос без токена (бесхозный) игнорируется.
    [Fact]
    public async Task БесхозныйЗапрос_Игнорируется()
    {
        // turnId, которого нет в TurnTokenService (токен не выдан/отозван).
        var r = ReqId();
        _meter.OnRequestStarted(r, "нет-такого-хода");
        _meter.OnFirstToken(r, "нет-такого-хода");
        _meter.OnRequestCompleted(r, "нет-такого-хода", Usage(42));

        await _bus.PublishAsync(new TurnCompleted(new TurnContext("chat-ghost", "owner", 1, 0), "success"));

        _sink.Turns.Should().BeEmpty("бесхозный запрос не привязан к сессии, итога нет");
    }

    // (7) ActiveChats: только чаты с запросом в работе; OwnerId — из токена хода.
    [Fact]
    public void ActiveChats_ТолькоСЗапросомВРаботе()
    {
        var tA = Issue("chat-A");
        var tB = Issue("chat-B");
        var rA = ReqId();
        var rB = ReqId();
        _meter.OnRequestStarted(rA, tA);
        _meter.OnRequestStarted(rB, tB);

        var active = _meter.ActiveChats();
        active.Should().HaveCount(2, "оба чата имеют запрос в работе");
        active.Should().OnlyContain(c => c.OwnerId == "owner", "OwnerId — из токена хода (grant)");
        active.Select(c => c.SessionId).Should().BeEquivalentTo(new[] { "chat-A", "chat-B" });

        // Запрос chat-B завершён (ход ещё нет) — чат больше не «в работе».
        _meter.OnRequestCompleted(rB, tB, Usage(7));
        active = _meter.ActiveChats();
        active.Should().HaveCount(1, "завершённый запрос не держит чат в списке");
        active.Single().SessionId.Should().Be("chat-A");
    }

    // (8) GetLastTurn: null до первого хода, итог после TurnCompleted.
    [Fact]
    public async Task GetLastTurn_ИтогПослеХода()
    {
        _meter.GetLastTurn("chat-A").Should().BeNull("ходов не было");

        var t = Issue("chat-A");
        var r = ReqId();
        _meter.OnRequestStarted(r, t);
        _meter.OnRequestCompleted(r, t, Usage(30, ttftMs: 120));
        await _bus.PublishAsync(new TurnCompleted(new TurnContext("chat-A", "owner", 1, 0), "success"));

        var last = _meter.GetLastTurn("chat-A");
        last.Should().NotBeNull("ход завершён — итог доступен");
        last!.OutTokens.Should().Be(30);
        last.TtftMs.Should().Be(120);
    }

    // (9) Тумблер выключен (Enabled=false): итог хода не шлём (OnTurn) и не храним
    //     (LastSummary → GetLastTurn null). Мутация: убрать проверку Enabled в OnTurnCompleted
    //     → OnTurn сработает и тест краснеет.
    [Fact]
    public async Task ТумблерВыключен_ИтогНеШлётся_ИНеХранится()
    {
        _options.CurrentValue.Enabled = false;
        var t = Issue("chat-A");
        var r = ReqId();
        _meter.OnRequestStarted(r, t);
        _meter.OnRequestCompleted(r, t, Usage(30, ttftMs: 120));

        await _bus.PublishAsync(new TurnCompleted(new TurnContext("chat-A", "owner", 1, 0), "success"));

        _sink.Turns.Should().BeEmpty("Enabled=false — итог хода не отправлен");
        _meter.GetLastTurn("chat-A").Should().BeNull("Enabled=false — итог не сохранён");
    }

    // (M4-1) Два параллельных запроса одного хода (один turnId, разные requestId) не
    //      затирают друг друга: оба завершаются, сумма токенов = 10 + 15 = 25.
    //      Мутация: вернуть ключ InWork на turnId → второй запрос затирает первый,
    //      OnRequestCompleted первого не находит запись → OutTokens = 15 (не 25) → тест краснеет.
    [Fact]
    public async Task M4_ПараллельныеЗапросыОдногоХода_НеЗатирают()
    {
        var t = Issue("chat-A");
        var r1 = ReqId();
        var r2 = ReqId();

        // Оба запроса стартовали, оба в работе.
        _meter.OnRequestStarted(r1, t);
        _meter.OnRequestStarted(r2, t);

        // ActiveChats: чат в работе (оба запроса в InWork).
        _meter.ActiveChats().Should().HaveCount(1, "чат с двумя параллельными запросами в работе");

        // Оба завершаются: сумма = 25.
        _meter.OnRequestCompleted(r1, t, Usage(10));
        _meter.OnRequestCompleted(r2, t, Usage(15));

        await _bus.PublishAsync(new TurnCompleted(new TurnContext("chat-A", "owner", 1, 0), "success"));

        var s = _sink.Turns.Single(x => x.Session == "chat-A").Summary;
        s.OutTokens.Should().Be(25, "10 + 15: параллельные запросы не затирают друг друга");
    }

    // (M4-2) Обрыв первого запроса хода (кнопка «Стоп»): OnRequestCompleted не вызван,
    //      но TurnCompleted приходит → InWork очищается, фантом не остаётся.
    //      Мутация: вернуть ранний выход в OnTurnCompleted (не чистить InWork) →
    //      ActiveChats после TurnCompleted покажет чат → тест краснеет.
    [Fact]
    public async Task M4_ОбрывЗапроса_ФантомНеОстаётся()
    {
        var t = Issue("chat-A");
        var r1 = ReqId();
        var r2 = ReqId();

        // Первый запрос стартовал, второй — тоже. Первый оборвался (OnRequestCompleted нет).
        _meter.OnRequestStarted(r1, t);
        _meter.OnRequestStarted(r2, t);

        // Второй завершается, первый — оборвался.
        _meter.OnRequestCompleted(r2, t, Usage(5));

        // Ход завершён (TurnCompleted): InWork должен очиститься, включая оборванный r1.
        await _bus.PublishAsync(new TurnCompleted(new TurnContext("chat-A", "owner", 1, 0), "success"));

        // Фантом: r1 остался в InWork → чат «в работе» без активности.
        _meter.ActiveChats().Should().BeEmpty("после TurnCompleted InWork чист: фантом не остался");
    }

    // (M4-3) Таймаут простоя: чат с запросом в работе, но без активности > порога
    //      не висит в панели. Prefill (нет первого токена) — порог 20 мин (Minor 1).
    //      Мутация: убрать проверку порога в ActiveChats → чат показывается → тест краснеет.
    [Fact]
    public void M4_ТаймаутПростоя_ЧатНеВиситВПанели()
    {
        var t = Issue("chat-A");
        var r = ReqId();
        _meter.OnRequestStarted(r, t); // prefill (Waiting=0), нет первого токена

        // Prefill: порог 20 мин. Активность 21 мин назад → таймаут истёк.
        _time.Advance(TimeSpan.FromMinutes(21));

        _meter.ActiveChats().Should().BeEmpty("prefill без активности > 20 мин — чат не в панели");
    }

    // (M4-4) Завершение хода (TurnCompleted) очищает InWork даже если запросы ещё в работе
    //      (ранний выход, ошибка). Мутация: убрать InWork.Clear() из OnTurnCompleted →
    //      ActiveChats покажет чат → тест краснеет.
    [Fact]
    public async Task M4_ЗавершениеХода_ОчищаетInWork()
    {
        var t = Issue("chat-A");
        var r = ReqId();
        _meter.OnRequestStarted(r, t);

        // Запрос ещё в работе (OnRequestCompleted не вызван), но ход завершён.
        await _bus.PublishAsync(new TurnCompleted(new TurnContext("chat-A", "owner", 1, 0), "success"));

        _meter.ActiveChats().Should().BeEmpty("TurnCompleted очистил InWork: запрос не висит");
    }

    // (Minor 1) Запрос в prefill 10 минут (нет первого токена) остаётся в списке:
    // порог простоя для prefill/queued — 20 мин, не 5. Мутация: вернуть PrefillIdleTimeout
    // в 5 мин → 10 мин > 5 → чат пропадает → тест краснеет.
    [Fact]
    public void Minor1_Prefill10Минут_ЧатОстаётсяВСписке()
    {
        var t = Issue("chat-A");
        var r = ReqId();
        _reader.Waiting = 0; // prefill, не queued
        _meter.OnRequestStarted(r, t);

        // 10 минут простоя (нет первого токена, нет OnTokenCount).
        _time.Advance(TimeSpan.FromMinutes(10));

        _meter.ActiveChats().Should().HaveCount(1, "prefill 10 мин < порога 20 мин — чат в списке");
    }

    // (Minor 1) Оборванный запрос в prefill без активности исчезает после порога (20 мин):
    // фантом (обрыв без TurnCompleted) всё равно чистится. Мутация: убрать проверку порога
    // для prefill (всегда 5 мин) → 21 мин > 5 → чат пропадает раньше, но тест на 21 мин всё
    // равно зелёный; мутация «убрать проверку вообще» → чат остаётся → тест краснеет.
    [Fact]
    public void Minor1_ОбрывPrefill_ИсчезаетПослеПорога()
    {
        var t = Issue("chat-A");
        var r = ReqId();
        _reader.Waiting = 0; // prefill
        _meter.OnRequestStarted(r, t);

        // 21 минута простоя (нет первого токена) → порог 20 мин истёк.
        _time.Advance(TimeSpan.FromMinutes(21));

        _meter.ActiveChats().Should().BeEmpty("prefill 21 мин > порога 20 мин — фантом чистится");
    }

    // (M5-1) Живая скорость: токены растут → genTps > 0 в OnLive.
    //      Мутация: убрать OnTokenCount из шлюза (не передавать счётчик) →
    //      TokenWindow пуст → genTps = null → тест краснеет.
    [Fact]
    public void M5_СкоростьРастётПоПриращению()
    {
        var t = Issue("chat-A");
        var r = ReqId();
        _meter.OnRequestStarted(r, t);
        _meter.OnFirstToken(r, t);

        // 10 токенов за 2 секунды → 5 ток/с.
        for (var i = 1; i <= 10; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(200));
            _meter.OnTokenCount(r, t, i);
        }

        // Последний OnLive: genTps > 0 (скорость из окна).
        var last = _sink.Live.Last(l => l.Session == "chat-A");
        last.Tps.Should().NotBeNull("есть приращение → скорость не null");
        last.Tps!.Should().BeGreaterThan(0, "скорость > 0 при росте токенов");
    }

    // (M5-2) Окно скользит: точки старше 3 с удаляются.
    //      Мутация: убрать prune (RemoveAt) → окно растёт без bound →
    //      скорость считается по всему окну (11 токенов / 7.8 с ≈ 1.41), не по 3 с (1 / 1 = 1.0).
    //      Допуск 0.2 (окно [0.8;1.2]) отделяет 1.41 от 1.0: с грубым 0.5 ([0.5;1.5]) мутация
    //      оставалась зелёной. Тест: после паузы 4 с + 2 новых токена за 1 с → скорость = 1.0.
    [Fact]
    public void M5_ОкноСкользит_СтарыеТочкиУдаляются()
    {
        var t = Issue("chat-A");
        var r = ReqId();
        _meter.OnRequestStarted(r, t);
        _meter.OnFirstToken(r, t);

        // 10 токенов за 2 с.
        for (var i = 1; i <= 10; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(200));
            _meter.OnTokenCount(r, t, i);
        }
        // Пауза 4 с: окно 3 с → все 10 точек 2-секундного окна ушли.
        _time.Advance(TimeSpan.FromSeconds(4));
        // 2 новых токена с интервалом 1 с (троттлинг 1 Гц: оба проходят).
        _time.Advance(TimeSpan.FromSeconds(1));
        _meter.OnTokenCount(r, t, 11);
        _time.Advance(TimeSpan.FromSeconds(1));
        _meter.OnTokenCount(r, t, 12);

        // Скорость из окна: 1 токен / 1 с = 1 ток/с (не 10/6 ≈ 1.67 как без prune).
        var last = _sink.Live.Last(l => l.Session == "chat-A");
        last.Tps.Should().NotBeNull("2 точки в окне → скорость вычислена");
        last.Tps!.Should().BeApproximately(1.0, 0.2, "окно 3 с: только новые токены, не старые (1.41 без prune вне допуска)");
    }

    // (M5-3) Троттлинг: ≤1 OnLive/с. Мутация: убрать проверку LiveThrottle →
    //      каждый OnTokenCount шлёт OnLive → больше 1/с → тест краснеет.
    [Fact]
    public void M5_Троттлинг_НеЧаще1Гц()
    {
        var t = Issue("chat-A");
        var r = ReqId();
        _meter.OnRequestStarted(r, t);
        _meter.OnFirstToken(r, t);

        var liveBefore = _sink.Live.Count;
        // 5 токенов за 0.5 с (все < 1 с от предыдущего live):
        // троттлинг должен сдержать — не больше 1 нового OnLive.
        for (var i = 1; i <= 5; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            _meter.OnTokenCount(r, t, i);
        }
        var newLives = _sink.Live.Count - liveBefore;
        newLives.Should().BeLessOrEqualTo(1, "троттлинг 1 Гц: не больше 1 OnLive/с");
    }

    // (M5-4) Нет 0: без приращения (1 токен, потом пауза) genTps = null, не 0.
    //      Мутация: вернуть 0 вместо null → тест краснеет.
    [Fact]
    public void M5_НетПриращения_GenTpsNull()
    {
        var t = Issue("chat-A");
        var r = ReqId();
        _meter.OnRequestStarted(r, t);
        _meter.OnFirstToken(r, t);

        // Один токен, потом пауза 4 с (окно 3 с: точка ушла).
        _time.Advance(TimeSpan.FromMilliseconds(200));
        _meter.OnTokenCount(r, t, 1);
        _time.Advance(TimeSpan.FromSeconds(4));
        // Окно пусто (все точки старше 3 с): ComputeLiveGenTps → null.
        // Но OnTokenCount не вызывался после паузы, значит OnLive не шлётся.
        // Проверяем через прямой вызов: если бы окно было [1 точка], скорость = null.
        // Эмулируем: вызываем OnTokenCount с тем же счётчиком (нет приращения).
        _meter.OnTokenCount(r, t, 1); // тот же count → dTokens = 0 → null

        var last = _sink.Live.Last(l => l.Session == "chat-A");
        last.Tps.Should().BeNull("нет приращения → genTps = null, не 0");
    }

    // (Minor 2) Два параллельных запроса, один завершается — скорость не проваливается в null.
    // Мутация: вернуть расчёт total как сумму по InWork (без SessionTotalTokens) →
    // после завершения r1 total падает (20 → 11); пик (20) — ПЕРВАЯ точка окна 3с →
    // dTokens = 11 − 20 < 0 → genTps = null → тест краснеет.
    // Сценарий: 10 токенов за 2с (пик 20, t=2.0), пауза 3с, r1 завершается,
    // r2 даёт 1 токен (t=5.0). Окно [2.0;5.0]: пик = первая точка.
    [Fact]
    public void Minor2_ПараллельныеЗапросы_ЗавершениеНеРоняетСкорость()
    {
        var t = Issue("chat-A");
        var r1 = ReqId();
        var r2 = ReqId();

        // Оба запроса стартовали и получили первый токен.
        _meter.OnRequestStarted(r1, t);
        _meter.OnRequestStarted(r2, t);
        _meter.OnFirstToken(r1, t);
        _meter.OnFirstToken(r2, t);

        // Оба генерируют: r1 — 10, r2 — 10 (по 200 мс, 2 с). Пик: t=2.0, total=20.
        for (var i = 1; i <= 10; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(200));
            _meter.OnTokenCount(r1, t, i);
            _meter.OnTokenCount(r2, t, i);
        }

        // r1 завершается (t=2.0): его вклад (10) замораживается в SessionTotalTokens.
        _meter.OnRequestCompleted(r1, t, Usage(10));

        // Пауза 3 с (t=2.0…5.0): r2 не даёт токенов.
        _time.Advance(TimeSpan.FromSeconds(3));

        // r2 даёт 1 новый токен (t=5.0, count=11).
        _meter.OnTokenCount(r2, t, 11);

        // Окно [2.0;5.0]: 2 точки.
        // Монотонный: (2.0, 20), (5.0, 21) → dTokens=1 > 0 → speed ≈ 0.33.
        // Мутация: (2.0, 20), (5.0, 11) → dTokens=−9 < 0 → null.
        var last = _sink.Live.Last(l => l.Session == "chat-A");
        last.Tps.Should().NotBeNull("завершение параллельного запроса не роняет скорость в null");
        last.Tps!.Should().BeGreaterThan(0, "скорость > 0: монотонный счётчик, приращение по r2");
    }

    // (M5-5) Итог хода — по точному usage, не по живой скорости.
    //      Мутация: BuildSummary берёт genTps из TokenWindow → значение отличается → тест краснеет.
    [Fact]
    public async Task M5_ИтогХода_ПоТочномуUsage()
    {
        var t = Issue("chat-A");
        var r = ReqId();
        _meter.OnRequestStarted(r, t);
        _meter.OnFirstToken(r, t);

        // 10 токенов за 2 с (живая скорость ≈ 5 ток/с).
        for (var i = 1; i <= 10; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(200));
            _meter.OnTokenCount(r, t, i);
        }
        // Завершение: точный usage = 10 токенов, GenDuration = 2 с → GenTps = 5.0.
        _meter.OnRequestCompleted(r, t, Usage(10, gen: TimeSpan.FromSeconds(2)));

        await _bus.PublishAsync(new TurnCompleted(new TurnContext("chat-A", "owner", 1, 0), "success"));

        var s = _sink.Turns.Single(x => x.Session == "chat-A").Summary;
        s.GenTps.Should().Be(5.0, "итог = 10 токенов / 2 с = 5.0 (точный usage)");
    }
}
