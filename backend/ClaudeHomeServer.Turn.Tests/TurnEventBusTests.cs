using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Tests.Services;

// Юниты шины событий хода (этап 0 плана «Шина событий хода», ADR-013).
// Подписчиков в этом этапе нет — тесты только на саму шину: контракт Notification/Filter,
// обязательность next() у Filter, устойчивость Notification к исключениям.
public sealed class TurnEventBusTests
{
    private sealed record StubNotification(TurnContext Turn, string Tag) : ITurnNotification;

    private sealed record StubFilter(TurnContext Turn, List<string> Log) : ITurnFilter;

    private static TurnContext Ctx() => new(SessionId: "s1", OwnerId: "u1", TurnSeq: 1, AgentDepth: 0);

    [Fact]
    public async Task Filter_подписчики_идут_по_Order_и_передают_друг_другу()
    {
        var bus = new TurnEventBus();
        var log = new List<string>();
        bus.OnFilter<StubFilter>(20, (e, next) => { log.Add("b20-in"); return next(); });
        bus.OnFilter<StubFilter>(5, (e, next) => { log.Add("b5-in"); return next(); });
        bus.OnFilter<StubFilter>(10, (e, next) => { log.Add("b10-in"); return next(); });

        var evt = new StubFilter(Ctx(), log);
        await bus.ApplyAsync(evt);

        // Равных Order нет — стабильный порядок по Order.
        Assert.Equal(new[] { "b5-in", "b10-in", "b20-in" }, log);
    }

    [Fact]
    public async Task Filter_равные_Order_идут_в_порядке_подписки()
    {
        var bus = new TurnEventBus();
        var log = new List<string>();
        bus.OnFilter<StubFilter>(10, (e, next) => { log.Add("first"); return next(); });
        bus.OnFilter<StubFilter>(10, (e, next) => { log.Add("second"); return next(); });
        bus.OnFilter<StubFilter>(10, (e, next) => { log.Add("third"); return next(); });

        await bus.ApplyAsync(new StubFilter(Ctx(), log));

        Assert.Equal(new[] { "first", "second", "third" }, log);
    }

    [Fact]
    public async Task Filter_подписчик_не_позвал_next_бросает_внятную_ошибку()
    {
        var bus = new TurnEventBus();
        bus.OnFilter<StubFilter>(0, (e, next) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bus.ApplyAsync(new StubFilter(Ctx(), new List<string>())));

        Assert.Contains("Filter-подписчик", ex.Message);
        Assert.Contains("не позвал next()", ex.Message);
    }

    [Fact]
    public async Task Filter_подписчик_не_позвал_next_следующие_не_запускаются()
    {
        // Иначе молчаливый обрыв пропустил бы критичную правку промпта/модели.
        var bus = new TurnEventBus();
        var log = new List<string>();
        bus.OnFilter<StubFilter>(0, (e, next) => Task.CompletedTask);
        bus.OnFilter<StubFilter>(1, (e, next) => { log.Add("second-ran"); return next(); });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => bus.ApplyAsync(new StubFilter(Ctx(), log)));

        Assert.Empty(log);
    }

    [Fact]
    public async Task Filter_подписчик_многократно_звал_next_бросает_ошибку()
    {
        var bus = new TurnEventBus();
        var log = new List<string>();
        bus.OnFilter<StubFilter>(0, async (e, next) =>
        {
            await next();
            await next();
        });
        bus.OnFilter<StubFilter>(1, (e, next) => { log.Add("tail"); return next(); });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bus.ApplyAsync(new StubFilter(Ctx(), log)));

        Assert.Contains("больше одного раза", ex.Message);
    }

    [Fact]
    public async Task Filter_исключение_подписчика_роняет_ход()
    {
        var bus = new TurnEventBus();
        var log = new List<string>();
        bus.OnFilter<StubFilter>(0, (e, next) => throw new InvalidOperationException("boom"));
        bus.OnFilter<StubFilter>(1, (e, next) => { log.Add("tail"); return next(); });

        // Filter — в пути хода: честный сбой хода, а не глушение.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bus.ApplyAsync(new StubFilter(Ctx(), log)));
        Assert.Equal("boom", ex.Message);
        Assert.Empty(log);
    }

    [Fact]
    public async Task Notification_исключение_подписчика_не_всплывает_и_не_роняет_публикацию()
    {
        var bus = new TurnEventBus();
        var log = new List<string>();
        bus.OnNotification<StubNotification>((e) => { log.Add("first-before"); return Task.CompletedTask; });
        bus.OnNotification<StubNotification>((e) => throw new InvalidOperationException("notif-boom"));
        bus.OnNotification<StubNotification>((e) => { log.Add("second-after"); return Task.CompletedTask; });

        // Публикация НЕ бросает: ход не должен падать из-за сломанного наблюдателя.
        await bus.PublishAsync(new StubNotification(Ctx(), Tag: "x"));

        // Остальные подписчики выполнились — гашение исключения в одном месте
        // не должно прерывать рассылку остальным.
        Assert.Equal(new[] { "first-before", "second-after" }, log);
    }

    [Fact]
    public async Task Notification_без_подписчиков_не_бросает()
    {
        var bus = new TurnEventBus();
        await bus.PublishAsync(new StubNotification(Ctx(), Tag: "x"));
    }

    [Fact]
    public async Task Filter_без_подписчиков_не_бросает()
    {
        var bus = new TurnEventBus();
        await bus.ApplyAsync(new StubFilter(Ctx(), new List<string>()));
    }

    [Fact]
    public void Контракт_Notification_и_Filter_раздельный_по_типу_события()
    {
        // Один подписчик не может одновременно быть Notification и Filter —
        // вид события задаёт контракт отказа и шина на это полагается.
        Assert.False(typeof(ITurnNotification).IsAssignableFrom(typeof(ITurnFilter)));
        Assert.False(typeof(ITurnFilter).IsAssignableFrom(typeof(ITurnNotification)));
    }

    [Fact]
    public async Task Notification_публикация_без_подписчиков_не_бросает()
    {
        // Прозрачный сценарий, но стоит зафиксировать: первый же production-подписчик
        // не должен ловить «пустую шину» как ошибку — поведение должно быть как у пустого массива.
        var bus = new TurnEventBus();
        await bus.PublishAsync(new StubNotification(Ctx(), Tag: "x"));
    }

    [Fact]
    public async Task Filter_несколько_обработчиков_в_одном_next_счётчик_инкрементируется_по_вызовам()
    {
        // Защита от регрессии InvokeAsync: счётчик nextCalls инкрементируется на КАЖДЫЙ
        // вызов next(), даже если handler планирует его асинхронно в нескольких ветках.
        var bus = new TurnEventBus();
        var log = new List<string>();
        bus.OnFilter<StubFilter>(0, async (e, next) =>
        {
            log.Add("a-in");
            await next();
            log.Add("a-after-next");
        });
        bus.OnFilter<StubFilter>(1, async (e, next) =>
        {
            log.Add("b-in");
            await next();
            log.Add("b-after-next");
        });

        await bus.ApplyAsync(new StubFilter(Ctx(), log));

        // Вложенные вызовы next() вложенных обработчиков — нормальный порядок:
        // каждый дёрнул next() ровно один раз, итого один полный проход.
        Assert.Equal(new[] { "a-in", "b-in", "b-after-next", "a-after-next" }, log);
    }
}