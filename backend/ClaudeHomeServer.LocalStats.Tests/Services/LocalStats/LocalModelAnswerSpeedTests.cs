using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>
/// Сборщик скорости ответа (фича «Скорость ответа», docs/features/llm-answer-speed-spec.md):
/// сквозная скорость как Σ токенов ÷ Σ полного времени запросов, медиана скорости с порогом
/// токенов, медиана и p90 времени до первого токена, окна 5m/1h (half-open по моменту
/// завершения), старение открытых запросов, «нет данных» = null, а не 0.
/// </summary>
public sealed class LocalModelAnswerSpeedTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-20T12:00:00+00:00");

    private const string Main = LocalModelStatsPoller.MainTrackName;
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    // Часы с ручным ходом: окна и пороги меряются на них, без реального ожидания.
    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private static (LocalModelAnswerSpeed C, ManualTime Time) New()
    {
        var time = new ManualTime(T0);
        return (new LocalModelAnswerSpeed(time), time);
    }

    // Полный цикл одного запроса: старт → (первый токен, если был) → завершение.
    // Все времена — смещение от T0; tokens null — «движок не отдал usage».
    private static void Run(LocalModelAnswerSpeed c, string id, TimeSpan start, TimeSpan? firstToken,
        TimeSpan end, long? tokens, string track = Main)
    {
        c.OnRequestStarted(id, track, T0 + start);
        if (firstToken is not null) c.OnFirstToken(id, T0 + firstToken.Value);
        c.OnRequestCompleted(id, tokens, T0 + end);
    }

    private static AnswerSpeedWindow W(AnswerSpeedSnapshot s, string name) =>
        s.Windows.Single(w => w.Window == name);

    // (1) Сквозная скорость — сумма, а не среднее скоростей: 300 токенов / 30 с = 10.
    //     Мутация: брать среднее скоростей ((100/10 + 200/20)/2 = 7.5) — тест краснеет.
    [Fact]
    public void СквознаяСкорость_СуммаТокеновНаСуммуВремени()
    {
        var (c, time) = New();
        Run(c, "r1", TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), 100);
        Run(c, "r2", TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(61), TimeSpan.FromSeconds(80), 200);
        time.Advance(TimeSpan.FromSeconds(90));

        var snap = c.SnapshotOf(Main);
        var w = W(snap, "5m");
        w.Requests.Should().Be(2);
        w.EndToEndTps.Should().BeApproximately(10.0, 1e-9);
        // То же кольцо в большем окне: те же два запроса.
        W(snap, "1h").Requests.Should().Be(2);
        W(snap, "1h").EndToEndTps.Should().BeApproximately(10.0, 1e-9);
    }

    // (2) Движок не отдал usage: запрос считается в requests окна, но не входит в Σ скорости.
    //     Ни одного запроса с токенами — сквозная скорость null, а не 0.
    [Fact]
    public void НетТокенов_СквознаяСкоростьNull_ЗапросУчитан()
    {
        var (c, time) = New();
        Run(c, "r1", TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), null);
        time.Advance(TimeSpan.FromSeconds(30));

        var snap = c.SnapshotOf(Main);
        var w = W(snap, "5m");
        w.Requests.Should().Be(1, "запрос завершён в окне — он есть в числе запросов");
        w.EndToEndTps.Should().BeNull("токенов нет — скорости нет, и нуля вместо неё быть не должно");
        w.MedianTps.Should().BeNull();
        snap.MinTokensForMedian.Should().Be(32);
    }

    // (3) Порог медианы: запрос короче 32 выходных токенов в медиану скорости не входит
    //     (короткий ответ — prefill, а не генерация), но в Σ сквозной скорости он входит.
    //     Мутация: снять порог (условие t < MinTokensForMedian) — медиана по трём запросам
    //     (2, 10, 10) = 10.0, тест краснеет.
    [Fact]
    public void МедианаСПорогомТокенов_КороткийОтветНеВходит_НоВСуммеЕсть()
    {
        var (c, time) = New();
        Run(c, "long", TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), 100); // 10 ток/с
        Run(c, "mid", TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20), 40);   // 2 ток/с
        Run(c, "short", TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 10);  // ниже порога
        time.Advance(TimeSpan.FromSeconds(30));

        var snap = c.SnapshotOf(Main);
        W(snap, "5m").Requests.Should().Be(3);
        W(snap, "5m").MedianTps.Should().BeApproximately(6.0, 1e-9, "медиана по двум запросам от порога: (2 + 10) / 2");
        W(snap, "5m").EndToEndTps.Should().BeApproximately(150.0 / 31.0, 1e-9, "в сквозную сумму короткий запрос входит");
    }

    // (4) Медиана: при чётном n — среднее двух средних, при нечётном — середина. Пусто — null.
    [Theory]
        [InlineData(new double[] { 5 }, 5.0)]
        [InlineData(new double[] { 5, 1 }, 3.0)]
        [InlineData(new double[] { 5, 1, 3 }, 3.0)]
        [InlineData(new double[] { 5, 1, 3, 2 }, 2.5)]
    public void Медиана_СредниеДвухСреднихПриЧётномN(double[] values, double expected) =>
        LocalModelAnswerSpeed.Median([.. values]).Should().BeApproximately(expected, 1e-9);

    // (5) p90 — ближайший ранг ceil(0.9·n)−1 в отсортированном ряду (n=1 → единственный).
    //     Мутация: floor вместо ceil → при n=4 индекс 2 (а не 3) → краснеет.
    [Theory]
        [InlineData(new double[] { 7 }, 7.0)]
        [InlineData(new double[] { 1, 2, 3, 4 }, 4.0)]
        [InlineData(new double[] { 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 }, 9.0)]
        [InlineData(new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, 10.0)]
    public void P90_БлижайшийРанг(double[] values, double expected) =>
        LocalModelAnswerSpeed.P90([.. values]).Should().BeApproximately(expected, 1e-9);

    // (6) TTFT: медиана и p90 только по запросам с первым токеном; без первого токена — null.
    [Fact]
    public void Ttft_ТолькоПоЗапросамСПервымТокеном()
    {
        var (c, time) = New();
        Run(c, "quiet", TimeSpan.Zero, null, TimeSpan.FromSeconds(10), 100); // первого токена не было
        Run(c, "a", TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), 100);
        Run(c, "b", TimeSpan.Zero, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(10), 100);
        time.Advance(TimeSpan.FromSeconds(30));

        var w = W(c.SnapshotOf(Main), "5m");
        w.MedianTtftSec.Should().BeApproximately(4.0, 1e-9);
        w.P90TtftSec.Should().BeApproximately(6.0, 1e-9);

        var (quiet, quietTime) = New();
        Run(quiet, "quiet", TimeSpan.Zero, null, TimeSpan.FromSeconds(10), 100);
        quietTime.Advance(TimeSpan.FromSeconds(30));
        var wq = W(quiet.SnapshotOf(Main), "5m");
        wq.MedianTtftSec.Should().BeNull();
        wq.P90TtftSec.Should().BeNull();
    }

    // (7) Граница окна half-open: ровно на now−5м — входит; на такт раньше — только в 1ч;
    //     старше часа — нигде (и вычищается из кольца).
    [Fact]
    public void ГраницаОкна_РовноНаГраницеВходит_РаньшеНет()
    {
        var (c, time) = New();
        // «Сейчас» — T0+2ч; границы окон: 5м → T0+1ч55м, 1ч → T0+1ч.
        Run(c, "at5m", TimeSpan.FromMinutes(110), TimeSpan.FromMinutes(111), TimeSpan.FromMinutes(115), 100);
        Run(c, "before5m", TimeSpan.FromMinutes(110), TimeSpan.FromMinutes(111), TimeSpan.FromMinutes(115) - Tick, 100);
        Run(c, "olderThanHour", TimeSpan.FromMinutes(50), TimeSpan.FromMinutes(51), TimeSpan.FromHours(1) - Tick, 100);
        time.Advance(TimeSpan.FromHours(2));

        var snap = c.SnapshotOf(Main);
        W(snap, "5m").Requests.Should().Be(1, "запрос ровно на границе окна — в окне");
        W(snap, "1h").Requests.Should().Be(2, "запрос на такт раньше границы — только в часовом окне");
    }

    // (8) Пустая дорожка: окна есть, метрики null, requests 0 — «нет данных» не читается как «ноль».
    [Fact]
    public void ПустаяДорожка_МетрикиNull_ЗапросовНоль()
    {
        var (c, _) = New();
        var snap = c.SnapshotOf(Main);

        snap.Windows.Select(w => w.Window).Should().Equal("5m", "1h");
        foreach (var w in snap.Windows)
        {
            w.Requests.Should().Be(0);
            w.EndToEndTps.Should().BeNull();
            w.MedianTps.Should().BeNull();
            w.MedianTtftSec.Should().BeNull();
            w.P90TtftSec.Should().BeNull();
        }
        snap.MinTokensForMedian.Should().Be(32);
    }

    // (9) Дорожки раздельны: запрос именованного движка не попадает в main и наоборот.
    [Fact]
    public void ДорожкиРаздельны()
    {
        var (c, time) = New();
        Run(c, "main-req", TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), 100, Main);
        Run(c, "gemma-req", TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20), 200, "gemma");
        time.Advance(TimeSpan.FromSeconds(30));

        W(c.SnapshotOf(Main), "5m").Requests.Should().Be(1);
        W(c.SnapshotOf(Main), "5m").EndToEndTps.Should().BeApproximately(10.0, 1e-9);
        W(c.SnapshotOf("gemma"), "5m").Requests.Should().Be(1);
        W(c.SnapshotOf("gemma"), "5m").EndToEndTps.Should().BeApproximately(10.0, 1e-9, "200 токенов / 20 с");
        c.SnapshotOf("net-tagoy-dvizhok").Windows.All(w => w.Requests == 0).Should().BeTrue();
    }

    // (10) Открытый запрос без завершения старше часа мёртв: его «завершение» метрикой не
    //      становится — иначе в Σ попала бы двухчасовая длительность.
    [Fact]
    public void ОткрытыйЗапросСтареет_ЗавершениеПослеЧасаНеМетрика()
    {
        var (c, time) = New();
        c.OnRequestStarted("r1", Main, T0);
        time.Advance(TimeSpan.FromHours(2));
        c.OnRequestCompleted("r1", 500, T0 + TimeSpan.FromHours(2));

        var w = W(c.SnapshotOf(Main), "1h");
        w.Requests.Should().Be(0);
        w.EndToEndTps.Should().BeNull();
    }

    // (11) Запрос с нулевой длительностью (старт = конец) не входит в знаменатели, но считается
    //      в requests: деления на ноль в агрегате нет.
    [Fact]
    public void НулеваяДлительность_НеВСуммахНоВУчётеЗапросов()
    {
        var (c, time) = New();
        Run(c, "zero", TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 100);
        time.Advance(TimeSpan.FromMinutes(1));

        var w = W(c.SnapshotOf(Main), "5m");
        w.Requests.Should().Be(1);
        w.EndToEndTps.Should().BeNull();
        w.MedianTps.Should().BeNull();
        w.MedianTtftSec.Should().BeApproximately(0.0, 1e-9, "первый токен в тот же момент — TTFT 0, это данные");
    }
}
