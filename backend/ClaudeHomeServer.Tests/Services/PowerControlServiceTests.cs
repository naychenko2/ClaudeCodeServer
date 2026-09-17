using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Power;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Services;

// Отсрочка и отмена команды питания. Настоящая машина здесь не участвует: платформенный код
// спрятан за IPowerActions ровно ради этой границы (тесты гоняются на linux-раннере CI, и
// выполнять команду выключения в прогоне тестов было бы, мягко говоря, неудачно).
//
// Проверяем то, что иначе стоит дорого: отмена обязана реально снимать команду (иначе кнопка
// «Отменить» врёт, а машина всё равно гаснет), выключенная конфигом фича не должна планировать
// ничего, а второй запрос поверх запланированного — отбиваться, а не заводить вторую очередь.
public class PowerControlServiceTests
{
    private sealed class FakePowerActions : IPowerActions
    {
        private readonly TaskCompletionSource<PowerAction> _executed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Available { get; init; } = true;
        public Task<PowerAction> Executed => _executed.Task;

        public bool Execute(PowerAction action)
        {
            _executed.TrySetResult(action);
            return true;
        }
    }

    private static PowerControlService Make(FakePowerActions actions, bool enabled = true, int delay = 60)
        => new(
            Options.Create(new PowerControlOptions { Enabled = enabled, DelaySeconds = delay }),
            actions,
            NullLogger<PowerControlService>.Instance);

    [Fact]
    public void Schedule_ФичаВыключена_Отказ()
    {
        var service = Make(new FakePowerActions(), enabled: false);

        var result = service.Schedule(PowerAction.Shutdown, "admin");

        result.Ok.Should().BeFalse();
        service.Pending.Should().BeNull();
    }

    [Fact]
    public void Schedule_ПлатформаНеУмеет_Отказ()
    {
        var service = Make(new FakePowerActions { Available = false });

        service.Schedule(PowerAction.Sleep, "admin").Ok.Should().BeFalse();
    }

    [Fact]
    public void Schedule_ПоверхЗапланированного_Отказ()
    {
        var service = Make(new FakePowerActions());
        service.Schedule(PowerAction.Shutdown, "admin").Ok.Should().BeTrue();

        var second = service.Schedule(PowerAction.Restart, "admin");

        second.Ok.Should().BeFalse();
        // Запланированным осталось ПЕРВОЕ действие: второй запрос ничего не подменяет
        service.Pending!.Action.Should().Be(PowerAction.Shutdown);
    }

    [Fact]
    public async Task Cancel_СнимаетКомандуДоСрабатывания()
    {
        var actions = new FakePowerActions();
        var service = Make(actions, delay: 60);
        service.Schedule(PowerAction.Shutdown, "admin");

        service.Cancel("admin").Should().BeTrue();

        service.Pending.Should().BeNull();
        // Отсрочка минута — если бы отмена не сработала, команда всё равно ещё не ушла бы,
        // поэтому проверяем не только отсутствие вызова, но и то, что планировщик пуст.
        var finished = await Task.WhenAny(actions.Executed, Task.Delay(200));
        finished.Should().NotBe(actions.Executed);
    }

    [Fact]
    public void Cancel_НечегоОтменять_ВозвращаетFalse()
        => Make(new FakePowerActions()).Cancel("admin").Should().BeFalse();

    [Fact]
    public async Task Schedule_БезОтсрочки_ВыполняетДействие()
    {
        var actions = new FakePowerActions();
        var service = Make(actions, delay: 0);

        service.Schedule(PowerAction.Restart, "admin").Ok.Should().BeTrue();

        // Ждём СОБЫТИЕ, а не таймер: раннер CI слабее рабочей машины (см. соглашения проекта)
        var finished = await Task.WhenAny(actions.Executed, Task.Delay(TimeSpan.FromSeconds(10)));
        finished.Should().Be(actions.Executed);
        (await actions.Executed).Should().Be(PowerAction.Restart);
        // Состояние снимается перед командой — иначе проснувшаяся после сна машина показывала
        // бы «запланировано» от прошлой жизни
        service.Pending.Should().BeNull();
    }
}
