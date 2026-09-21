using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Потолок одновременных тяжёлых запусков: занятые слоты ставят следующего в очередь,
// освобождение пускает его дальше, лёгкие запуски и выключенный потолок проходят насквозь.
// Ожидание проверяется СОБЫТИЕМ (Task.WhenAny с таймаутом), а не паузой: CI гоняет тесты
// на голодном ubuntu-latest, где Task.Delay даёт флейки.
public class BuildConcurrencyGateTests
{
    private static readonly TimeSpan Beat = TimeSpan.FromSeconds(5);

    private static ProcessSpec Heavy => new() { FileName = "dotnet", Args = ["build"], Heavy = true };
    private static ProcessSpec Light => new() { FileName = "git", Args = ["status"] };

    // Задача не завершилась к контрольному сроку — ждём заведомо дольше, чем «мгновенно»
    private static async Task<bool> CompletedAsync(Task task) =>
        await Task.WhenAny(task, Task.Delay(Beat)) == task;

    [Fact]
    public async Task ПревышениеПотолка_СтавитВОчередь_ОсвобождениеПускаетСледующего()
    {
        var gate = new BuildConcurrencyGate(2);

        var first = await gate.AcquireAsync(Heavy);
        var second = await gate.AcquireAsync(Heavy);
        gate.Available.Should().Be(0);

        var third = gate.AcquireAsync(Heavy);
        (await CompletedAsync(third)).Should().BeFalse("оба слота заняты — третий ждёт");

        first.Dispose();

        (await CompletedAsync(third)).Should().BeTrue("освобождённый слот достался очереди");
        (await third).Dispose();
        second.Dispose();
        gate.Available.Should().Be(2);
    }

    [Fact]
    public async Task ПотолокНоль_СквознойПроход()
    {
        var gate = new BuildConcurrencyGate(0);

        // Тяжёлых запусков заведомо больше любого разумного потолка — ни один не ждёт.
        // Через CompletedAsync, а не голым await: сломанный сквозной проход обязан дать
        // КРАСНЫЙ, а не вечное ожидание, в котором CI отваливается по таймауту джобы
        var all = Task.WhenAll(Enumerable.Range(0, 20).Select(_ => gate.AcquireAsync(Heavy)));

        (await CompletedAsync(all)).Should().BeTrue("потолка нет — ждать нечего");
        gate.Limit.Should().Be(0);
        var slots = await all;
        slots.Should().HaveCount(20);
        foreach (var s in slots) s.Dispose();
    }

    [Fact]
    public async Task ЛёгкийЗапуск_ПотолокНеТрогает()
    {
        var gate = new BuildConcurrencyGate(1);
        using var busy = await gate.AcquireAsync(Heavy);
        gate.Available.Should().Be(0);

        // Сотни git status в очереди за сборкой парализовали бы git-бар
        var light = gate.AcquireAsync(Light);

        (await CompletedAsync(light)).Should().BeTrue("git не тяжёлый — слот ему не нужен");
        gate.TryAcquire(Light).Should().NotBeNull("занятый потолок лёгкий запуск не отказывает");
        gate.Available.Should().Be(0, "лёгкие запуски слотов не занимают");
    }

    [Fact]
    public async Task TryAcquire_БезСлота_ОтдаётNull_АНеЖдёт()
    {
        var gate = new BuildConcurrencyGate(1);
        var busy = await gate.AcquireAsync(Heavy);

        gate.TryAcquire(Heavy).Should().BeNull();

        busy.Dispose();
        var free = gate.TryAcquire(Heavy);
        free.Should().NotBeNull("слот освободился");
        free!.Dispose();
    }

    [Fact]
    public async Task ПовторныйDispose_НеПоднимаетСчётчикВышеПотолка()
    {
        var gate = new BuildConcurrencyGate(1);
        var slot = await gate.AcquireAsync(Heavy);

        slot.Dispose();
        slot.Dispose();

        gate.Available.Should().Be(1, "слот возвращается ровно один раз");
    }

    [Fact]
    public void Отмена_СнимаетОжидающего()
    {
        var gate = new BuildConcurrencyGate(1);
        using var busy = gate.TryAcquire(Heavy)!;
        using var cts = new CancellationTokenSource();

        var waiting = gate.AcquireAsync(Heavy, cts.Token);
        cts.Cancel();

        waiting.Invoking(t => t.GetAwaiter().GetResult()).Should().Throw<OperationCanceledException>();
        gate.Available.Should().Be(0, "отменённое ожидание чужой слот не отпускает");
    }

    [Theory]
    [InlineData(null, BuildConcurrencyGate.DefaultLimit)]  // ключа нет — разумный дефолт
    [InlineData("3", 3)]
    [InlineData("0", 0)]                                    // явный ноль — откат без пересборки
    [InlineData("-1", 0)]                                   // мусорное значение = без ограничения
    public void ПотолокЧитаетсяИзКонфига(string? value, int expected)
    {
        var values = new Dictionary<string, string?>();
        if (value is not null) values["Execution:Isolation:MaxConcurrentBuilds"] = value;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        BuildConcurrencyGate.FromConfig(config).Limit.Should().Be(expected);
    }

    [Fact]
    public void ПризнакТяжести_БерётсяИзМеткиSpec_АНеИзИмениКоманды()
    {
        BuildConcurrencyGate.IsHeavy(Heavy).Should().BeTrue();
        BuildConcurrencyGate.IsHeavy(Light).Should().BeFalse();
        // Имя dotnet само по себе тяжести не означает: `dotnet --version` и dev-сервер проекта
        BuildConcurrencyGate.IsHeavy(new ProcessSpec { FileName = "dotnet", Args = ["--version"] })
            .Should().BeFalse();
        // Прогрев worktree — единственный тяжёлый запуск продукта на сегодня
        BuildConcurrencyGate.IsHeavy(WorktreeBuildWarmup.BuildSpec("/tmp/wt")).Should().BeTrue();
    }
}
