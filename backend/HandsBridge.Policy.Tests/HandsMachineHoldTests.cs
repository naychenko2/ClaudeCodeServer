using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Замок машины в мосте (ADR-016 §7): держится на время действий руками в пределах хода. Два
/// <see cref="HandsMachineHold"/> над одним замком — два моста двух чатов на одной машине.
/// </summary>
public class HandsMachineHoldTests
{
    private static (HandsMachineHold A, HandsMachineHold B) TwoBridges()
    {
        var machine = new InProcessHandsMachineMutex();
        return (new HandsMachineHold(machine), new HandsMachineHold(machine));
    }

    [Fact]
    public void Подключённые_руки_без_действий_замок_не_занимают_и_оба_хода_стартуют()
    {
        var (a, b) = TwoBridges();

        // Чтение дерева окна и список окон машину не трогают
        Assert.True(a.TryHold(HandsTools.UiSnapshot));
        Assert.True(a.TryHold(HandsTools.WindowManagement, "list"));

        Assert.False(a.Held);
        Assert.False(b.Held);
        Assert.True(b.TryHold(HandsTools.UiClick), "первое действие другого хода берёт свободный замок");
    }

    [Fact]
    public void Первое_действие_берёт_замок_действие_другого_хода_получает_отказ()
    {
        var (a, b) = TwoBridges();

        Assert.True(a.TryHold(HandsTools.UiClick));
        Assert.True(a.Held);
        Assert.True(a.TryHold(HandsTools.UiType), "свой ход действует дальше под тем же замком");

        Assert.False(b.TryHold(HandsTools.App));
        Assert.False(b.TryHold(HandsTools.WindowManagement, "activate"));
        Assert.False(b.Held);
    }

    [Fact]
    public void Конец_хода_отпускает_замок_и_следующий_ход_берёт_его_снова()
    {
        var (a, b) = TwoBridges();
        Assert.True(a.TryHold(HandsTools.UiClick));

        Assert.True(a.Release());
        Assert.False(a.Release(), "повторный конец хода отпускать нечего");

        Assert.True(b.TryHold(HandsTools.BrowserClick), "после конца хода руки свободны для другого чата");
        b.Release();
        Assert.True(a.TryHold(HandsTools.UiType), "следующий ход того же чата берёт замок заново");
    }

    [Theory]
    [InlineData(HandsTools.ScreenshotControl, "capture")]
    [InlineData(HandsTools.BrowserScreenshot, null)]
    public void Снимок_замок_не_берёт_и_при_занятых_руках_проходит(string tool, string? action)
    {
        var (a, b) = TwoBridges();

        Assert.True(a.TryHold(tool, action));
        Assert.False(a.Held, "снимок не трогает ввод");

        Assert.True(b.TryHold(HandsTools.UiClick));
        Assert.True(a.TryHold(tool, action), "снимок проходит, пока руками действует другой ход");
    }

    [Theory]
    [InlineData(HandsTools.BrowserSnapshot)]
    [InlineData(HandsTools.BrowserQuery)]
    [InlineData(HandsTools.BrowserWait)]
    public void Браузерное_чтение_замок_не_берёт_и_при_занятых_руках_проходит(string tool)
    {
        var (a, b) = TwoBridges();

        Assert.True(a.TryHold(tool));
        Assert.False(a.Held, "чтение через CDP не трогает фокус и ввод");

        Assert.True(b.TryHold(HandsTools.UiClick));
        Assert.True(a.TryHold(tool), "чтение проходит, пока руками действует другой ход");
    }

    [Theory]
    [InlineData(HandsTools.BrowserNavigate)]
    [InlineData(HandsTools.BrowserTabs)]
    [InlineData(HandsTools.BrowserClick)]
    [InlineData(HandsTools.BrowserType)]
    [InlineData(HandsTools.BrowserEvaluate)]
    public void Браузерное_действие_берёт_замок_и_при_занятых_руках_получает_отказ(string tool)
    {
        var (a, b) = TwoBridges();

        Assert.True(a.TryHold(tool));
        Assert.True(a.Held);

        Assert.False(b.TryHold(tool), "действие другого хода при занятых руках");
    }

    [Fact]
    public void Именованный_мьютекс_один_на_машину_и_отпускается_из_любого_потока()
    {
        var name = UniqueName();
        using var first = new NamedHandsMachineMutex(name);
        using var second = new NamedHandsMachineMutex(name);
        var a = new HandsMachineHold(first);
        var b = new HandsMachineHold(second);

        // Берём и отпускаем из разных потоков: привязку мьютекса к потоку держит поток-владелец
        Assert.True(Task.Run(() => a.TryHold(HandsTools.UiClick)).Result);
        Assert.False(Task.Run(() => b.TryHold(HandsTools.UiClick)).Result);
        Assert.True(Task.Run(a.Release).Result);
        Assert.True(Task.Run(() => b.TryHold(HandsTools.UiClick)).Result);
    }

    [Fact]
    public void Смерть_владельца_не_оставляет_замок_занятым()
    {
        var name = UniqueName();
        using var next = new NamedHandsMachineMutex(name);
        var hold = new HandsMachineHold(next);

        // Владелец берёт замок и умирает, не отпустив его, — как мост, погашенный KillTree
        var taken = new ManualResetEventSlim();
        var die = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            var mutex = new Mutex(false, name);
            mutex.WaitOne();
            taken.Set();
            die.Wait();
        });
        owner.Start();
        taken.Wait();

        Assert.False(hold.TryHold(HandsTools.UiClick), "пока владелец жив, руки заняты");

        die.Set();
        owner.Join();

        Assert.True(hold.TryHold(HandsTools.UiClick), "брошенный замок достаётся следующему");
        Assert.True(hold.Held);
    }

    private static string UniqueName() => @"Local\AiHome.Hands.Test." + Guid.NewGuid().ToString("N");
}
