using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static ClaudeHomeServer.Tasks.Tests.LocalExecutorFlowKit;

namespace ClaudeHomeServer.Tasks.Tests;

// Серверный пинок при простое локального исполнителя (часть C). Мутации (вывод — в отчёте):
//  • убрать проверку IdleMinutes — тест «раньше срока» красный;
//  • убрать кулдаун — тест «кулдаун» красный;
//  • не проверять Hold — тест «пауза фичи» красный; не проверять статус ключевого чата — «ключевой чат занят» красный;
//  • не проверять незапущенные шаги — тест «нет шагов» красный.
public class LocalIdleNudgeServiceTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
    private readonly LocalExecutorFlowKit _kit = new();
    private Dictionary<string, string?> _cfg = new();

    public void Dispose() => _kit.Dispose();

    private LocalIdleNudgeService Sut() => new(_kit.Tasks, new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider(),
        new ConfigurationBuilder().AddInMemoryCollection(_cfg).Build(), NullLogger<LocalIdleNudgeService>.Instance)
    {
        Roster = _kit.Roster,
        Sender = _kit.Sender,
        Sessions = _kit.Sessions,
    };

    private TaskItem Arrange()
    {
        _kit.Roster.Locals.Add(new LocalExecutor("kira", "Кира"));
        var epic = _kit.NewFeature("X");
        _kit.NewStep(epic, "kira", "S2");
        _kit.NewStep(epic, "kira", "R-3", role: TaskRoles.Rework);
        return epic;
    }

    [Fact]
    public async Task СвободнаДесятьМинут_ЕстьШаги_КарточкаВКлючевойЧат()
    {
        Arrange();
        var sut = Sut();

        await sut.TickAsync(T0);
        _kit.Sender.Sent.Should().BeEmpty("простой ещё не накопился");
        await sut.TickAsync(T0.AddMinutes(10));

        var card = _kit.Sender.Sent.Should().ContainSingle().Subject;
        card.SessionId.Should().Be(KeyChat);
        card.Text.Should().Be("Свободен локальный исполнитель: Кира без работы 10 мин. " +
            "В фиче есть его незапущенные шаги: R-3, S2. " +
            "Сверь «После:» и запусти готовые; готовых нет — ответь одной строкой, почему.");
    }

    [Fact]
    public async Task СвободнаРаньшеСрока_НеПинаем()
    {
        Arrange();
        var sut = Sut();

        await sut.TickAsync(T0);
        await sut.TickAsync(T0.AddMinutes(9).AddSeconds(59));

        _kit.Sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Кулдаун_ПарыФичаПерсона_НеЧащеРазаВПолчаса()
    {
        Arrange();
        var sut = Sut();
        await sut.TickAsync(T0);
        await sut.TickAsync(T0.AddMinutes(10));

        await sut.TickAsync(T0.AddMinutes(39));
        _kit.Sender.Sent.Should().HaveCount(1, "кулдаун 30 мин ещё не вышел");
        await sut.TickAsync(T0.AddMinutes(40));
        _kit.Sender.Sent.Should().HaveCount(2, "ровно через 30 мин от прошлой карточки пинок разрешён");
    }

    [Fact]
    public async Task НеДоставлено_КулдаунНеЗапускается()
    {
        Arrange();
        _kit.Sender.Succeed = false;
        var sut = Sut();
        await sut.TickAsync(T0);
        await sut.TickAsync(T0.AddMinutes(10));

        _kit.Sender.Succeed = true;
        await sut.TickAsync(T0.AddMinutes(11));

        _kit.Sender.Sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task ПерсонаЗанята_ПростойОтсчитываетсяОтОсвобождения()
    {
        var epic = Arrange();
        var running = _kit.NewStep(epic, "kira", "S1", running: true);
        var sut = Sut();
        await sut.TickAsync(T0);
        await sut.TickAsync(T0.AddMinutes(20));
        _kit.Sender.Sent.Should().BeEmpty("занята — не простаивает");

        _kit.Sessions.GetById(running.LinkedSessionId!)!.Status = SessionStatus.Active;
        await sut.TickAsync(T0.AddMinutes(21));
        _kit.Sender.Sent.Should().BeEmpty("освободилась только что");
        await sut.TickAsync(T0.AddMinutes(31));
        _kit.Sender.Sent.Should().HaveCount(1);
    }

    // Персона выпала из обхода (реестр пуст — провайдер выключен): отсчёт простоя теряется,
    // после возврата он идёт заново, а не от первого наблюдения часы назад
    [Fact]
    public async Task ВыпалаИзОбхода_ОтсчётПростояЗаново()
    {
        Arrange();
        var sut = Sut();
        await sut.TickAsync(T0);
        var kira = _kit.Roster.Locals.Single();
        _kit.Roster.Locals.Clear();
        await sut.TickAsync(T0.AddMinutes(30));

        _kit.Roster.Locals.Add(kira);
        await sut.TickAsync(T0.AddMinutes(31));
        _kit.Sender.Sent.Should().BeEmpty("простой начался заново в 31 мин");
        await sut.TickAsync(T0.AddMinutes(41));
        _kit.Sender.Sent.Should().ContainSingle().Which.Text.Should().Contain("без работы 10 мин");
    }

    [Fact]
    public async Task НетНезапущенныхШагов_НеПинаем()
    {
        var epic = _kit.NewFeature("X");
        _kit.Roster.Locals.Add(new LocalExecutor("kira", "Кира"));
        var launched = _kit.NewStep(epic, "kira", "S1");
        launched.LinkedSessionId = "уже-запущен";
        var sut = Sut();

        await sut.TickAsync(T0);
        await sut.TickAsync(T0.AddMinutes(15));

        _kit.Sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task ПаузаФичи_НеПинаем()
    {
        var epic = Arrange();
        epic.Hold = new FeatureHold(FeatureHoldReasons.Manual, T0, null, true);
        var sut = Sut();

        await sut.TickAsync(T0);
        await sut.TickAsync(T0.AddMinutes(15));

        _kit.Sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task КлючевойЧатВедётХод_НеПинаем()
    {
        Arrange();
        _kit.Sessions.GetById(KeyChat)!.Status = SessionStatus.Working;
        var sut = Sut();

        await sut.TickAsync(T0);
        await sut.TickAsync(T0.AddMinutes(15));

        _kit.Sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task КлючевогоЧатаНет_НеПинаем()
    {
        Arrange();
        var sut = Sut();
        // справочник без KeyChat: ключевой чат удалён
        sut.Sessions = new LocalExecutorFlowKit.FakeSessions();

        await sut.TickAsync(T0);
        await sut.TickAsync(T0.AddMinutes(15));

        _kit.Sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task ФичаЗакрыта_НеПинаем()
    {
        Arrange().Status = TaskItemStatus.Done;
        var sut = Sut();

        await sut.TickAsync(T0);
        await sut.TickAsync(T0.AddMinutes(15));

        _kit.Sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task ЛокальныхПерсонНет_НеПинаем()
    {
        var epic = _kit.NewFeature("X");
        _kit.NewStep(epic, "kira", "S1");
        var sut = Sut();

        await sut.TickAsync(T0);
        await sut.TickAsync(T0.AddMinutes(15));

        _kit.Sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Выключатель_Enabled_False_НеПинаем()
    {
        Arrange();
        _cfg["Orchestration:LocalIdleNudge:Enabled"] = "false";
        var sut = Sut();

        await sut.TickAsync(T0);
        await sut.TickAsync(T0.AddMinutes(15));

        _kit.Sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Настройки_ИзКонфига_ПорогИКулдаун()
    {
        Arrange();
        _cfg["Orchestration:LocalIdleNudge:IdleMinutes"] = "2";
        _cfg["Orchestration:LocalIdleNudge:CooldownMinutes"] = "5";
        var sut = Sut();

        await sut.TickAsync(T0);
        await sut.TickAsync(T0.AddMinutes(2));
        _kit.Sender.Sent.Should().HaveCount(1);
        await sut.TickAsync(T0.AddMinutes(6));
        _kit.Sender.Sent.Should().HaveCount(1);
        await sut.TickAsync(T0.AddMinutes(7));
        _kit.Sender.Sent.Should().HaveCount(2);
    }
}
