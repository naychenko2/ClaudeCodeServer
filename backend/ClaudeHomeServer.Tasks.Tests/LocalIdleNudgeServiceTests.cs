using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static ClaudeHomeServer.Tasks.Tests.LocalExecutorFlowKit;

namespace ClaudeHomeServer.Tasks.Tests;

// Серверный пинок при простое слотов локального движка. Мутации (вывод — в отчёте):
//  • убрать проверку IdleMinutes — тест «раньше срока» красный;
//  • убрать кулдаун — тест «кулдаун» красный;
//  • не проверять Hold — тест «пауза фичи» красный; не проверять статус ключевого чата — «ключевой чат занят» красный;
//  • не проверять незапущенные шаги — тест «нет шагов» красный;
//  • считать свободным при очереди к движку — тест «очередь» красный;
//  • вернуть учёт по персонам (занятая персона выпадает) — тест «персона занята другим шагом» красный;
//  • убрать break после карточки — тест «две фичи» красный.
public class LocalIdleNudgeServiceTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
    private const string Tail = "Одного исполнителя можно запускать на несколько шагов параллельно. ";
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
        _kit.Roster.Slots = new LocalEngineSlots(6, 4, 0);
        var epic = _kit.NewFeature("X");
        _kit.NewStep(epic, "kira", "S2");
        _kit.NewStep(epic, "kira", "R-3", role: TaskRoles.Rework);
        return epic;
    }

    [Fact]
    public async Task СлотыСвободныДесятьМинут_ЕстьШаги_КарточкаСоСлотамиИВсемиИсполнителями()
    {
        var epic = Arrange();
        _kit.Roster.Locals.Add(new LocalExecutor("denis", "Денис"));
        _kit.NewStep(epic, "denis", "S5");
        var sut = Sut();

        await sut.TickAsync(T0);
        _kit.Sender.Sent.Should().BeEmpty("простой ещё не накопился");
        await sut.TickAsync(T0.AddMinutes(10));

        var card = _kit.Sender.Sent.Should().ContainSingle().Subject;
        card.SessionId.Should().Be(KeyChat);
        card.Text.Should().Be("Свободно слотов локальной модели: 2 из 6, простаивают 10 мин. " +
            "В фиче есть незапущенные шаги локальных исполнителей — Денис: S5; Кира: R-3, S2. " + Tail +
            "Сверь «После:» и запусти готовые, до 2; готовых нет — ответь одной строкой, почему.");
    }

    // Главное отличие от учёта по персонам: персона, уже ведущая шаг, не мешает позвать её на другой
    [Fact]
    public async Task ПерсонаЗанятаДругимШагом_ЕёНезапущенныеШагиВКарточке()
    {
        var epic = Arrange();
        _kit.NewStep(epic, "kira", "S1", running: true);
        var sut = Sut();

        await sut.TickAsync(T0);
        await sut.TickAsync(T0.AddMinutes(10));

        _kit.Sender.Sent.Should().ContainSingle().Which.Text.Should().Contain("Кира: R-3, S2");
    }

    [Fact]
    public async Task СвободноРаньшеСрока_НеПинаем()
    {
        Arrange();
        var sut = Sut();

        await sut.TickAsync(T0);
        await sut.TickAsync(T0.AddMinutes(9).AddSeconds(59));

        _kit.Sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Кулдаун_Фичи_НеЧащеРазаВПолчаса()
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
    public async Task ВсеСлотыЗаняты_ПростойОтсчитываетсяОтОсвобождения()
    {
        Arrange();
        _kit.Roster.Slots = new LocalEngineSlots(6, 6, 0);
        var sut = Sut();
        await sut.TickAsync(T0);
        await sut.TickAsync(T0.AddMinutes(20));
        _kit.Sender.Sent.Should().BeEmpty("свободных слотов нет — не простаивает");

        _kit.Roster.Slots = new LocalEngineSlots(6, 5, 0);
        await sut.TickAsync(T0.AddMinutes(21));
        _kit.Sender.Sent.Should().BeEmpty("слот освободился только что");
        await sut.TickAsync(T0.AddMinutes(31));
        _kit.Sender.Sent.Should().ContainSingle().Which.Text.Should().StartWith("Свободно слотов локальной модели: 1 из 6, простаивают 10 мин.");
    }

    // Ход ждёт допуска — место на движке уже разобрано, даже если счётчик занятых его не догнал
    [Fact]
    public async Task ОчередьКДвижку_НеПинаем()
    {
        Arrange();
        _kit.Roster.Slots = new LocalEngineSlots(6, 4, 1);
        var sut = Sut();

        await sut.TickAsync(T0);
        await sut.TickAsync(T0.AddMinutes(15));

        _kit.Sender.Sent.Should().BeEmpty();
    }

    // Слоты общие для фич: две карточки в одном тике раздали бы одни и те же слоты дважды
    [Fact]
    public async Task ДвеФичи_ОднаКарточкаЗаТик_ВтораяВСледующем()
    {
        Arrange();
        _kit.Sessions.Add("chat-key-y", SessionStatus.Active);
        var y = _kit.Tasks.Create(null, Owner, new CreateTaskRequest("Y", Role: TaskRoles.Feature));
        y.ExecutorLaunchedBySessionId = "chat-key-y";
        _kit.NewStep(y, "kira", "S7");
        var sut = Sut();

        await sut.TickAsync(T0);
        await sut.TickAsync(T0.AddMinutes(10));
        _kit.Sender.Sent.Should().HaveCount(1);
        await sut.TickAsync(T0.AddMinutes(11));

        _kit.Sender.Sent.Select(s => s.SessionId).Should().BeEquivalentTo([KeyChat, "chat-key-y"]);
    }

    [Fact]
    public async Task ШагиНеЛокальныхПерсон_ВКарточкуНеПопадают()
    {
        var epic = Arrange();
        _kit.NewStep(epic, "cloud-gleb", "S9");
        var sut = Sut();

        await sut.TickAsync(T0);
        await sut.TickAsync(T0.AddMinutes(10));

        _kit.Sender.Sent.Should().ContainSingle().Which.Text.Should().NotContain("S9");
    }

    // Ростер опустел (провайдер выключен): отсчёт простоя теряется, после возврата идёт заново,
    // а не от первого наблюдения часы назад
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
        _kit.Sender.Sent.Should().ContainSingle().Which.Text.Should().Contain("простаивают 10 мин");
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
