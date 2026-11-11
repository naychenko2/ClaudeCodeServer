using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// FS-E3a: на шине событий хода появляется факт «человек ответил в чате, сообщение принято как
// ход» (turn/human-message-accepted). По нему подписчик FS-E3b снимает ожидание человека в
// задачах. Инвариант: событие — только про человеческий ход; агентские (agentDepth > 0),
// авто- и директивные подачи его не публикуют.
//
// Отрицательные проверки не вакуумны: каждый из них сверяет, что ход ВСЕМОСТОЯЛСЯ (мока
// процесса позвали на подачу в CLI) и при этом события нет — иначе «ноль событий» значал бы
// просто «ничего не произошло».
public sealed class HumanMessageAcceptedTests : SessionManagerTestBase
{
    // Шина того же контейнера, что резолвит фасад и ядро приёма (синглтон DI): читаем её там
    // же, куда смотрит издатель, а не собираем свою.
    private readonly ITurnEventBus _bus;
    private readonly List<HumanMessageAccepted> _events = new();
    private readonly object _gate = new();

    public HumanMessageAcceptedTests()
    {
        _bus = (ITurnEventBus)typeof(SessionManager)
            .GetField("_turnEvents", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(_sut)!;
        _bus.OnNotification<HumanMessageAccepted>(e =>
        {
            lock (_gate) _events.Add(e);
            return Task.CompletedTask;
        }, "HumanMessageAcceptedTests");
    }

    private List<HumanMessageAccepted> Events()
    {
        lock (_gate) return _events.ToList();
    }

    // Публикация fire-and-forget (контракт Notification): снимок берём с коротким ожиданием,
    // чтобы проверка не зависела от того, каким по счёту дойдёт до списка мой подписчик.
    private async Task<List<HumanMessageAccepted>> WaitForEventsAsync(int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = Events();
            if (snapshot.Count >= count) return snapshot;
            await Task.Delay(20);
        }
        return Events();
    }

    // Свободный чат с адаптером, который считает поданные ходы так же, как живой ClaudeSession
    // (SubmitTurn увеличивает номер на самой подаче): по этому номеру видно, что событие несёт
    // номер того хода, который эта подача начнёт.
    private async Task<(Session Session, Mock<ILlmSessionAdapter> Adapter)> MkCountingChatAsync(string suffix)
    {
        var session = await MkFreeSessionWithStubAsync(suffix);
        var entry = GetEntry(session.Id);
        long submitted = 0;
        var adapter = new Mock<ILlmSessionAdapter>();
        adapter.SetupGet(a => a.Info).Returns(session);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(true);
        adapter.SetupGet(a => a.OrchestrationActive).Returns(false);
        adapter.SetupGet(a => a.SubmittedTurnSeq).Returns(() => submitted);
        adapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<int>(), It.IsAny<bool>()))
            .Callback(() => submitted++)
            .Returns(Task.CompletedTask);
        SetProcess(entry, adapter.Object);
        return (session, adapter);
    }

    // Ход всё же состоялся: подача дошла до процесса (мока), а не утонула в отказе/очереди.
    private static void AssertTurnWasSent(Mock<ILlmSessionAdapter> adapter) =>
        adapter.Invocations.Should()
            .Contain(i => i.Method.Name == nameof(ILlmSessionAdapter.SendMessageAsync),
                "ход должен состояться: иначе «события нет» значит лишь «ничего не произошло»");

    [Fact]
    public async Task Ход_человека_даёт_ровно_одно_событие_с_ключами_из_хода()
    {
        var (session, adapter) = await MkCountingChatAsync("human");

        await _sut.SendMessageAsync(session.Id, "что делать с фичей?", []);

        var events = await WaitForEventsAsync(1);
        events.Should().HaveCount(1, "на один ход человека — ровно одна публикация");
        var e = events[0];
        e.Turn.SessionId.Should().Be(session.Id);
        // Ключи — из хода: владелец резолвится по сессии, а не по «текущему пользователю»
        e.Turn.OwnerId.Should().Be(TestUserId);
        e.Turn.ProjectId.Should().Be(session.ProjectId);
        e.Turn.AgentDepth.Should().Be(0);
        // Номер хода — тот, который начнёт эта подача: до подачи адаптер отдал 0 ходов
        e.Turn.TurnSeq.Should().Be(1);
        AssertTurnWasSent(adapter);
    }

    [Fact]
    public async Task Автоход_события_не_даёт()
    {
        var (session, adapter) = await MkCountingChatAsync("auto");

        await _sut.SendMessageAsync(session.Id, "авто-ход цикла", [], auto: true);

        AssertTurnWasSent(adapter);
        Events().Should().BeEmpty();
    }

    [Fact]
    public async Task Системная_директива_события_не_даёт()
    {
        var (session, adapter) = await MkCountingChatAsync("directive");

        await _sut.SendMessageAsync(session.Id, "[СИСТЕМНАЯ ДИРЕКТИВА — продолжай]", [], systemDirective: true);

        AssertTurnWasSent(adapter);
        Events().Should().BeEmpty();
    }

    [Fact]
    public async Task Агентский_ход_события_не_даёт()
    {
        var (session, adapter) = await MkCountingChatAsync("agent");

        // chats_send: сообщение другой сессии в этот чат — ход делегированный, не ответ человека
        await _sut.SendMessageAsync(session.Id, "доклад исполнителя", [], agentDepth: 1);

        AssertTurnWasSent(adapter);
        Events().Should().BeEmpty();
    }

    [Fact]
    public async Task Реактивный_ход_без_tasks_execute_события_не_даёт()
    {
        var (session, adapter) = await MkCountingChatAsync("suppress");

        await _sut.SendMessageAsync(session.Id, "реакция на доклад", [], suppressTasksExecute: true);

        AssertTurnWasSent(adapter);
        Events().Should().BeEmpty();
    }

    [Fact]
    public async Task Два_сообщения_человека_дают_два_события_с_разными_номерами_хода()
    {
        var (session, _) = await MkCountingChatAsync("two");

        await _sut.SendMessageAsync(session.Id, "первое", []);
        var first = await WaitForEventsAsync(1);
        first.Should().HaveCount(1);
        // Чат снова свободен (первый ход разобран) — второе сообщение идёт своей подачей
        session.Status = SessionStatus.Active;
        await _sut.SendMessageAsync(session.Id, "второе", []);

        var events = await WaitForEventsAsync(2);
        events.Should().HaveCount(2, "общего состояния нет: каждое сообщение — своё событие");
        events.Select(e => e.Turn.SessionId).Should().OnlyContain(s => s == session.Id);
        events.Select(e => e.Turn.TurnSeq).Distinct().Should().HaveCount(2,
            "два сообщения — два разных номера хода, общего состояния между ними нет");
    }

    [Fact]
    public async Task Бросающий_подписчик_не_роняет_приём_хода()
    {
        var (session, adapter) = await MkCountingChatAsync("thrower");
        _bus.OnNotification<HumanMessageAccepted>(_ => throw new InvalidOperationException("подписчик упал"),
            "HumanMessageAcceptedTests.Бросающий");

        var act = async () => await _sut.SendMessageAsync(session.Id, "человек ответил", []);

        await act.Should().NotThrowAsync("падение подписчика гасит шина, ход не падает");
        AssertTurnWasSent(adapter);
    }

    // --- REST-канал: POST /sessions/{id}/messages → SendMessageAndWaitAsync (FS-E3 доработка) ---
    // Ответ человека через REST идёт мимо SendDirectAsync; без своего издателя ожидание человека
    // в задаче не снималось (дефект QA 46a4b6ff). Человек — глубина 0, без персоны, с id пользователя.

    [Fact]
    public async Task REST_ответ_человека_в_свободном_чате_даёт_событие()
    {
        var (session, adapter) = await MkCountingChatAsync("rest-free");

        await _sut.SendMessageAndWaitAsync(session.Id, "Беру срок: неделя.", TimeSpan.Zero,
            senderUserId: TestUserId);

        AssertTurnWasSent(adapter);
        var events = await WaitForEventsAsync(1);
        events.Should().HaveCount(1);
        events[0].Turn.SessionId.Should().Be(session.Id);
        events[0].Turn.OwnerId.Should().Be(TestUserId);
        events[0].Turn.AgentDepth.Should().Be(0);
    }

    [Fact]
    public async Task REST_ответ_человека_в_занятом_чате_даёт_событие_при_постановке_в_очередь()
    {
        var session = await MkBusySessionAsync("rest-busy", SessionStatus.Working);

        var result = await _sut.SendMessageAndWaitAsync(session.Id, "Беру срок: неделя.", TimeSpan.Zero,
            senderUserId: TestUserId);

        result.Should().BeOfType<SendAndWaitResult.Queued>();
        var events = await WaitForEventsAsync(1);
        events.Should().HaveCount(1, "ответ принят в очередь — ожидание человека снимается сразу, не по концу хода");
        events[0].Turn.SessionId.Should().Be(session.Id);
    }

    [Fact]
    public async Task REST_дубль_в_очереди_второго_события_не_даёт()
    {
        var session = await MkBusySessionAsync("rest-dup", SessionStatus.Working);

        await _sut.SendMessageAndWaitAsync(session.Id, "повтор", TimeSpan.Zero, senderUserId: TestUserId);
        var again = await _sut.SendMessageAndWaitAsync(session.Id, "повтор", TimeSpan.Zero, senderUserId: TestUserId);

        again.Should().BeOfType<SendAndWaitResult.Queued>().Which.Duplicate.Should().BeTrue();
        await Task.Delay(100);
        Events().Should().HaveCount(1);
    }

    [Fact]
    public async Task REST_сообщение_агента_события_не_даёт()
    {
        var (session, adapter) = await MkCountingChatAsync("rest-agent");

        // chats_send: глубина ≥ 1 — делегированный ход, даже с id человека-актора
        await _sut.SendMessageAndWaitAsync(session.Id, "доклад", TimeSpan.Zero, agentDepth: 1,
            senderUserId: TestUserId);

        AssertTurnWasSent(adapter);
        Events().Should().BeEmpty();
    }

    [Fact]
    public async Task REST_сообщение_без_пользователя_события_не_даёт()
    {
        var (session, adapter) = await MkCountingChatAsync("rest-anon");

        // будильник сторожа: ни вызывателя, ни пользователя
        await _sut.SendMessageAndWaitAsync(session.Id, "будильник", TimeSpan.Zero);

        AssertTurnWasSent(adapter);
        Events().Should().BeEmpty();
    }

    // Директивная подача с id пользователя — ровно так её шлёт ChatContinueService.RequestHandoffAsync
    // (systemDirective: true, actorUserId: sender) — не ответ человека: ожидание задачи не снимает
    // (доработка 2 FS-E3b). Мутация «убрать systemDirective из PublishRestHumanMessage» краснит оба.
    [Fact]
    public async Task REST_директивная_подача_с_id_пользователя_в_свободном_чате_события_не_даёт()
    {
        var (session, adapter) = await MkCountingChatAsync("rest-directive-free");

        await _sut.SendMessageAndWaitAsync(session.Id, "[передача чата]", TimeSpan.Zero,
            systemDirective: true, senderUserId: TestUserId);

        AssertTurnWasSent(adapter);
        await Task.Delay(100);
        Events().Should().BeEmpty();
    }

    [Fact]
    public async Task REST_директивная_подача_с_id_пользователя_в_занятом_чате_события_не_даёт()
    {
        var session = await MkBusySessionAsync("rest-directive-busy", SessionStatus.Working);

        var result = await _sut.SendMessageAndWaitAsync(session.Id, "[передача чата]", TimeSpan.Zero,
            preempt: false, systemDirective: true, senderUserId: TestUserId);

        result.Should().BeOfType<SendAndWaitResult.Queued>("подача встала в очередь — иначе «события нет» ничего не значит");
        await Task.Delay(100);
        Events().Should().BeEmpty();
    }

    // --- Путь гонки (TurnIntake.cs:724-731): вторая подача проиграла гонку за ход ---
    // Код ветки гонки идентичен покрытому пути очереди, но это другое состояние: чат по статусу
    // свободен, а ожидатель хода первой подачи уже выставлен. В тестовой сборке окно «свободный
    // чат → смена статуса» у первой подачи целиком синхронное (зафиксировано в
    // SessionManagerQueueTests.cs:731), поэтому честный барьер двух Task.Run не гарантирует, что
    // вторая успеет пройти проверку занятости до Working, — она могла бы уйти в покрытый путь
    // очереди. Детерминизм даёт шов этого файла (тест «Два_сообщения…»: статус возвращается в
    // Active): он фиксирует ровно то состояние, в которое попадает проигравшая гонка подача.
    // Мутация «убрать PublishRestHumanMessage в ветке гонки» краснит этот тест.
    [Fact]
    public async Task ГонкаДвухПодач_КаждаяДаётРовноОдноСобытие()
    {
        var (session, adapter) = await MkCountingChatAsync("race-both");

        // Первая подача выиграла гонку: ход начат и не завершён (мока не отдаёт result —
        // TurnWaiter жив, как в живой гонке), событие — по прямому пути.
        var first = await _sut.SendMessageAndWaitAsync(session.Id, "первая", TimeSpan.Zero,
            senderUserId: TestUserId);
        first.Should().BeOfType<SendAndWaitResult.Running>();
        (await WaitForEventsAsync(1)).Should().HaveCount(1);

        // Вторая подача — состояние проигравшей гонки: чат свободен по статусу, но TurnWaiter
        // первой жив. Она проходит гейт занятости и не доходит до старта хода: встаёт в очередь
        // без прерывания чужого хода (ветка гонки, TurnIntake.cs:724-731).
        session.Status = SessionStatus.Active;
        var second = await _sut.SendMessageAndWaitAsync(session.Id, "вторая", TimeSpan.Zero,
            senderUserId: TestUserId);

        second.Should().BeOfType<SendAndWaitResult.Queued>();
        adapter.Verify(a => a.Interrupt(), Times.Never(), "ветка гонки не прерывает начатый чужой ход");
        var events = await WaitForEventsAsync(2);
        events.Should().HaveCount(2, "по одному событию на подачу: победитель по прямому пути, проигравший по ветке гонки");
        events.Select(e => e.Turn.TurnSeq).Distinct().Should().HaveCount(2,
            "второе событие — второй подачи, а не повтор первого");
        AssertTurnWasSent(adapter);
    }
}
