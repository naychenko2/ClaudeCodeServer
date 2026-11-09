using ClaudeHomeServer.Services.Images.LocalMedia;
using Xunit;

namespace ClaudeHomeServer.Images.Tests;

// Чистая функция расчёта старта i-й ждущей: остаток идущей + Σ eta впереди;
// null в любом звене цепочки → null для этой и всех последующих
public class ComfyQueueEtaTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Нормальная_цепочка_суммирует_остаток_идущей_и_eta_впереди()
    {
        // Идущая: eta 100с, стартовала 40с назад → остаток 60с
        var running = new ComfyQueueEta.Running(EtaSeconds: 100, StartedAt: Now.AddSeconds(-40));
        // Ждущие: 30с, 20с, 10с
        var queue = new[]
        {
            new ComfyQueueEta.Waiting(EtaSeconds: 30),
            new ComfyQueueEta.Waiting(EtaSeconds: 20),
            new ComfyQueueEta.Waiting(EtaSeconds: 10),
        };

        // i=0: 60 (остаток идущей)
        Assert.Equal(60, ComfyQueueEta.StartInSeconds(running, Now, queue, 0));
        // i=1: 60 + 30 = 90
        Assert.Equal(90, ComfyQueueEta.StartInSeconds(running, Now, queue, 1));
        // i=2: 60 + 30 + 20 = 110
        Assert.Equal(110, ComfyQueueEta.StartInSeconds(running, Now, queue, 2));
    }

    [Fact]
    public void Null_в_середине_обнуляет_эту_и_все_последующие()
    {
        var running = new ComfyQueueEta.Running(EtaSeconds: 100, StartedAt: Now.AddSeconds(-40));
        var queue = new[]
        {
            new ComfyQueueEta.Waiting(EtaSeconds: 30),
            new ComfyQueueEta.Waiting(EtaSeconds: null), // null здесь
            new ComfyQueueEta.Waiting(EtaSeconds: 10),
        };

        // i=0: 60 (впереди никого)
        Assert.Equal(60, ComfyQueueEta.StartInSeconds(running, Now, queue, 0));
        // i=1: впереди queue[0] (eta=30, не null) → 60 + 30 = 90 (собственная null не влияет на свой старт)
        Assert.Equal(90, ComfyQueueEta.StartInSeconds(running, Now, queue, 1));
        // i=2: впереди queue[1] (eta=null) → null
        Assert.Null(ComfyQueueEta.StartInSeconds(running, Now, queue, 2));
    }

    [Fact]
    public void Перерасход_идущей_даёт_остаток_ноль()
    {
        // Идущая: eta 30с, но стартовала 50с назад → перерасход, остаток 0
        var running = new ComfyQueueEta.Running(EtaSeconds: 30, StartedAt: Now.AddSeconds(-50));
        var queue = new[]
        {
            new ComfyQueueEta.Waiting(EtaSeconds: 20),
            new ComfyQueueEta.Waiting(EtaSeconds: 10),
        };

        // i=0: 0 (перерасход)
        Assert.Equal(0, ComfyQueueEta.StartInSeconds(running, Now, queue, 0));
        // i=1: 0 + 20 = 20
        Assert.Equal(20, ComfyQueueEta.StartInSeconds(running, Now, queue, 1));
    }

    [Fact]
    public void Пустая_очередь_без_идущей_даёт_ноль()
    {
        var queue = Array.Empty<ComfyQueueEta.Waiting>();

        // Без идущей и без ждущих → 0
        Assert.Equal(0, ComfyQueueEta.StartInSeconds(null, Now, queue, 0));
    }

    [Fact]
    public void Без_идущей_только_сумма_впереди()
    {
        var queue = new[]
        {
            new ComfyQueueEta.Waiting(EtaSeconds: 30),
            new ComfyQueueEta.Waiting(EtaSeconds: 20),
            new ComfyQueueEta.Waiting(EtaSeconds: 10),
        };

        // i=0: 0 (впереди никого)
        Assert.Equal(0, ComfyQueueEta.StartInSeconds(null, Now, queue, 0));
        // i=1: 30
        Assert.Equal(30, ComfyQueueEta.StartInSeconds(null, Now, queue, 1));
        // i=2: 30 + 20 = 50
        Assert.Equal(50, ComfyQueueEta.StartInSeconds(null, Now, queue, 2));
    }

    // startedAt позже now (гонка часов): elapsed = 0, остаток = eta, а не eta + |elapsed|
    [Fact]
    public void StartedAt_ВБудущем_Ожидание_Равно_Eta()
    {
        var running = new ComfyQueueEta.Running(EtaSeconds: 100, StartedAt: Now.AddSeconds(10));
        var queue = new[]
        {
            new ComfyQueueEta.Waiting(EtaSeconds: 20),
        };

        // i=0: elapsed=0 (startedAt в будущем), остаток = eta = 100
        Assert.Equal(100, ComfyQueueEta.StartInSeconds(running, Now, queue, 0));
        // i=1: 100 + 20 = 120
        Assert.Equal(120, ComfyQueueEta.StartInSeconds(running, Now, queue, 1));
    }

    [Fact]
    public void Идущая_с_null_eta_даёт_null_для_всех()
    {
        var running = new ComfyQueueEta.Running(EtaSeconds: null, StartedAt: Now.AddSeconds(-40));
        var queue = new[]
        {
            new ComfyQueueEta.Waiting(EtaSeconds: 30),
        };

        // Идущая без eta → null для i=0
        Assert.Null(ComfyQueueEta.StartInSeconds(running, Now, queue, 0));
    }
}
