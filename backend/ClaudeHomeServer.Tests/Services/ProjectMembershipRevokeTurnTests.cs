using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// F2-O3 (финальное ревью «Общих проектов», пункт О3): отзыв участника гасит запущенное в его
/// приватных чатах. Сквозная цепочка: ProjectMembershipService.RemoveAsync → DropAsync →
/// SessionManager.StopChatsOfRevokedMember → ход прерван, цикл «до готово» снят, очередь
/// очищена (не заморожена). Область — ровно приватные чаты отозванного: общий чат и приватный
/// чат другого участника не тронуты.
/// </summary>
public class ProjectMembershipRevokeTurnTests : SessionManagerTestBase
{
    private const string MemberId = "member-1";
    private const string OtherMemberId = "member-2";

    /// <summary>
    /// Живой ProjectMembershipService на той же фикстуре: отказов по правам здесь нет (вызывает
    /// владелец), хаб — мок: выкидывание групп и событие К3 — предмет RevokedMemberHubTests,
    /// здесь DropAsync обязан пройти сквозь него без исключения.
    /// </summary>
    private ProjectMembershipService NewMembershipService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SharedProjects:Enabled"] = "true",
            })
            .Build();
        var gate = new SharedProjectsGate(config, new FeatureFlagService(_userStore));
        var client = new Mock<IClientProxy>();
        client.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(client.Object);
        var groups = new Mock<IGroupManager>();
        groups.Setup(g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);
        hub.SetupGet(h => h.Groups).Returns(groups.Object);
        return new ProjectMembershipService(_projectManager, _userStore, gate,
            new ClaudeHomeServer.Services.Git.GitService(TestLauncherFactory.Instance), _sut,
            new UserConnectionRegistry(), hub.Object, NullLogger<ProjectMembershipService>.Instance);
    }

    /// <summary>Общий проект с участником и его приватным чатом (тот же признак, что ставит
    /// SetChatVisibility: Session.PrivateToUserId).</summary>
    private async Task<(Project Project, Session PrivateChat)> SharedProjectWithPrivateChatOfMemberAsync(string suffix)
    {
        var dir = MkProjectDir("revoke_" + suffix);
        var project = _projectManager.Create("Общий-" + suffix, dir, TestUserId, TestUsername);
        _projectManager.SetMembers(project.Id,
            [new ProjectMember { UserId = MemberId, Role = ProjectRole.Editor, AddedBy = TestUserId }]);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        session.PrivateToUserId = MemberId;
        return (project, session);
    }

    [Fact]
    public async Task Отзыв_ГаситЖивойХодСнимаетЦиклИЧиститОчередьПриватногоЧата()
    {
        var (project, chat) = await SharedProjectWithPrivateChatOfMemberAsync("live");
        var entry = GetEntry(chat.Id);
        var adapter = StubAdapter(entry); // HasLiveTurn=true — «Стоп» видит живой ход, не зависший
        SetProcess(entry, adapter.Object);
        chat.Status = SessionStatus.Working;
        chat.WorkLoop = new SessionWorkLoop { Iteration = 2 };
        EnqueueUser(entry, "не доставлять");
        EnqueueAgent(entry, "доклад не доставлять");

        var res = await NewMembershipService().RemoveAsync(TestUserId, project.Id, MemberId);

        res.Ok.Should().BeTrue();
        // Ход останавливается: живой процесс прерван ровно один раз…
        adapter.Verify(a => a.Interrupt(), Times.Once());
        // …цикл «до готово» снят (иначе exited прерванного хода запустил бы новую итерацию,
        // которую никто не увидит: автор чата проекта больше не видит)
        ((Session)entry.GetType().GetField("Info")!.GetValue(entry)!).WorkLoop.Should().BeNull();
        // Очередь очищена и НЕ заморожена: заморозка ждёт возобновления человеком, а его здесь
        // больше нет; оставленные сообщения застряли бы в чате, который никто не откроет
        _sut.GetPending(chat.Id).Should().BeEmpty();
        ((bool)entry.GetType().GetField("QueueFrozen")!.GetValue(entry)!).Should().BeFalse();
    }

    [Fact]
    public async Task Отзыв_ЗависшийПриватныйЧатНеОстаётсяЗанятым()
    {
        // Статус Working без живого хода (ход убил ватчдог/сбой): без отзыва такой чат оставался
        // бы занятым навсегда, а его очередь ждала бы конца несуществующего хода.
        var (project, chat) = await SharedProjectWithPrivateChatOfMemberAsync("stuck");
        var entry = GetEntry(chat.Id);
        chat.Status = SessionStatus.Working;
        EnqueueUser(entry, "ждёт несуществующего хода");

        (await NewMembershipService().RemoveAsync(TestUserId, project.Id, MemberId)).Ok.Should().BeTrue();

        _sut.GetPending(chat.Id).Should().BeEmpty();
        await WaitForConditionAsync(
            () => _sut.GetById(chat.Id)!.Status != SessionStatus.Working, TimeSpan.FromSeconds(5));
        _sut.GetById(chat.Id)!.Status.Should().NotBe(SessionStatus.Working,
            "после отзыва зависший чат реанимируется: стоящий статус значит ход, который некому закончить");
    }

    [Fact]
    public async Task Отзыв_НеТрогаетОбщийЧатИПриватныйЧатДругогоУчастника()
    {
        // Область правки — ровно приватные чаты отозванного: ходы общего чата принадлежат проекту
        // и продолжаются для владельца; чат другого участника к отзыву не относится.
        var (project, _) = await SharedProjectWithPrivateChatOfMemberAsync("scope");
        var sharedChat = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var otherPrivate = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        otherPrivate.PrivateToUserId = OtherMemberId;
        EnqueueUser(GetEntry(sharedChat.Id), "общий чат живёт");
        EnqueueUser(GetEntry(otherPrivate.Id), "чужой приватный живёт");

        (await NewMembershipService().RemoveAsync(TestUserId, project.Id, MemberId)).Ok.Should().BeTrue();

        _sut.GetPending(sharedChat.Id).Should().ContainSingle();
        _sut.GetPending(otherPrivate.Id).Should().ContainSingle();
    }

    // ── доработка 2 (дельта-ревью 627bd324): гонка «отзыв против уже начатой доставки» ──

    /// <summary>
    /// Сообщение изымается из очереди под PendingLock, а доставляется вне замка: в окне между
    /// изъятием и стартом хода (здесь — на бродкасте очереди перед DeliverPendingAsync) приходит
    /// отзыв участника. StopChatsOfRevokedMember чистит очередь, но до уже изъятых сообщений не
    /// дотягивается. Перепроверка в DeliverPendingAsync гасит ход-сироту: в процесс отозванного
    /// не уходит ни одного нового хода, очередь остаётся пустой. Окно держит TaskCompletionSource.
    /// </summary>
    [Fact]
    public async Task ОтзывВОкнеМеждуИзятиемИСтартомНеПоднимаетХодСироту()
    {
        var (project, chat) = await SharedProjectWithPrivateChatOfMemberAsync("race");
        var entry = GetEntry(chat.Id);
        var adapter = StubAdapter(entry); // живой «previous»-процесс: без перепроверки ход пошёл бы в него
        SetProcess(entry, adapter.Object);
        EnqueueUser(entry, "изъято из очереди до отзыва");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Окно: доставка застревает на бродкасте очереди (сразу после изъятия, до DeliverPendingAsync).
        // Гейт ловит только PendingMessagesMessage — служебные бродкасты отзыва (composer_restore)
        // проходят, иначе отзыв, которого ждёт этот же тест, уткнулся бы в свой же гейт (deadlock).
        _broadcaster.Gate = async msg =>
        {
            if (msg is PendingMessagesMessage)
            {
                entered.TrySetResult();
                await release.Task;
            }
        };
        try
        {
            var drain = (Task)InvokePrivate("DrainNextPendingAsync", chat.Id)!;

            await WaitForConditionAsync(() => entered.Task.IsCompleted, TimeSpan.FromSeconds(5));
            entered.Task.IsCompleted.Should().BeTrue("drain не дошёл до окна между изъятием и стартом");

            // Отзыв — ровно в этом окне: сообщение уже вне очереди, ход ещё не начат.
            (await NewMembershipService().RemoveAsync(TestUserId, project.Id, MemberId)).Ok.Should().BeTrue();

            release.SetResult(); // отпускаем окно: доставка доходит до перепроверки
            await drain;

            // Ход-сирота не поднят: в процесс отозванного не ушло ни одного нового сообщения.
            adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<int>(), It.IsAny<bool>()), Times.Never(),
                "после отзыва стартует ход-сирота в приватном чате отозванного");
            _sut.GetPending(chat.Id).Should().BeEmpty("изъятое до отзыва сообщение не возвращается в очередь");
        }
        finally { _broadcaster.Gate = null; }
    }

    /// <summary>
    /// S3-7d: та же гонка, что в тесте выше, но в приватном чате активен цикл «до готово»:
    /// при активном цикле drain взводит маркер LoopTurnInFlight вместе с изъятием. Отзыв
    /// в окне между изятием и стартом идёт через StopChatsOfRevokedMember → InterruptCore,
    /// который снимает и цикл, и маркер — поэтому внутренний гард DeliverPendingAsync
    /// («при активном цикле гасим маркер») не срабатывает: до перепроверки доходит уже снятое
    /// состояние. Фиксируем это здесь — состояние на момент перепроверки и отсутствие
    /// хода-сироты; consistency-правка (снятие под PendingLock) поведение не меняет.
    /// </summary>
    [Fact]
    public async Task ОтзывВОкнеДоставки_АктивныйЦиклИМаркерСнятыСтопом_ХодСиротаНеПоднят()
    {
        var (project, chat) = await SharedProjectWithPrivateChatOfMemberAsync("race-loop");
        var entry = GetEntry(chat.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        chat.WorkLoop = new SessionWorkLoop { Iteration = 2 };
        EnqueueUser(entry, "изъято из очереди до отзыва");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _broadcaster.Gate = async msg =>
        {
            if (msg is PendingMessagesMessage)
            {
                entered.TrySetResult();
                await release.Task;
            }
        };
        try
        {
            var drain = (Task)InvokePrivate("DrainNextPendingAsync", chat.Id)!;

            await WaitForConditionAsync(() => entered.Task.IsCompleted, TimeSpan.FromSeconds(5));
            entered.Task.IsCompleted.Should().BeTrue("drain не дошёл до окна между изятием и стартом");
            GetLoopTurnInFlight(entry).Should().BeTrue(
                "санити: при активном цикле drain взводит маркер вместе с изъятием");

            // Отзыв — ровно в этом окне. Он снимает и цикл, и маркер (TurnProcessHost.InterruptCore):
            // к моменту перепроверки в DeliverPendingAsync гард «цикл активен» уже ложен —
            // ветка снятия маркера не выполняется, доходит только общий отказ.
            (await NewMembershipService().RemoveAsync(TestUserId, project.Id, MemberId)).Ok.Should().BeTrue();
            ((Session)entry.GetType().GetField("Info")!.GetValue(entry)!).WorkLoop.Should().BeNull(
                "отзыв снимает цикл синхронно, до продолжения доставки");
            GetLoopTurnInFlight(entry).Should().BeFalse(
                "отзыв гасит маркер через InterruptCore: гард при перепроверке не срабатывает");

            release.SetResult(); // отпускаем окно: доставка доходит до перепроверки
            await drain;

            // Ход-сирота не поднят: в процесс отозванного не ушло ни одного нового сообщения.
            adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<int>(), It.IsAny<bool>()), Times.Never(),
                "после отзыва стартует ход-сирота в приватном чате отозванного");
            _sut.GetPending(chat.Id).Should().BeEmpty();
        }
        finally { _broadcaster.Gate = null; }
    }

    /// <summary>
    /// TS-5: доставка ждала смерти прогона (показана фаза «queued»), а перепроверка отзыва
    /// отказала — ход не стартует, строка обязана сняться фазой «done».
    /// </summary>
    [Fact]
    public async Task ОтзывПослеОжиданияПрогона_ПослеQueuedШлётсяDone()
    {
        EnableAwaitPreviousExit(5);
        var (project, chat) = await SharedProjectWithPrivateChatOfMemberAsync("queued-done");
        var entry = GetEntry(chat.Id);
        var adapter = StubAdapter(entry);
        var alive = true;
        adapter.SetupGet(a => a.HasLiveTurn).Returns(() => Volatile.Read(ref alive));
        SetProcess(entry, adapter.Object);
        _broadcaster.Gate = msg =>
        {
            if (msg is TurnStartPhaseMessage { Phase: "queued" }) Volatile.Write(ref alive, false);
            return Task.CompletedTask;
        };
        try
        {
            (await NewMembershipService().RemoveAsync(TestUserId, project.Id, MemberId)).Ok.Should().BeTrue();
            var next = new QueuedMessage(Guid.NewGuid().ToString("N"), "после отзыва",
                null, null, 0, DateTime.UtcNow, Kind: PendingKind.User);
            var method = typeof(SessionManager).GetMethod("DeliverPendingAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var delivered = await (Task<bool>)method.Invoke(_sut, [chat.Id, entry, next])!;

            delivered.Should().BeFalse("ход не стартовал");
            Sent<TurnStartPhaseMessage>().Select(p => p.Phase).Should().Equal(["queued", "done"],
                "после показанной очереди выход без старта хода снимает строку");
        }
        finally { _broadcaster.Gate = null; }
    }

    /// <summary>
    /// Граница: отзыв одного участника не глушит доставку в приватный чат другого — он остаётся
    /// участником проекта, перепроверка его пропускает. Тот же ход событий, что в тесте гонки,
    /// но целевой чат принадлежит неотозванному: ход стартует, очередь разбирается.
    /// </summary>
    [Fact]
    public async Task ДоставкаВПриватныйЧатДругогоУчастникаПроходитПриОтзывеПервого()
    {
        var (project, _) = await SharedProjectWithPrivateChatOfMemberAsync("other");
        _projectManager.SetMembers(project.Id, [
            new ProjectMember { UserId = MemberId, Role = ProjectRole.Editor, AddedBy = TestUserId },
            new ProjectMember { UserId = OtherMemberId, Role = ProjectRole.Editor, AddedBy = TestUserId },
        ]);
        var otherChat = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        otherChat.PrivateToUserId = OtherMemberId; // автор — неотозванный участник
        var entry = GetEntry(otherChat.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        EnqueueUser(entry, "доставить в чат другого участника");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _broadcaster.Gate = async msg =>
        {
            if (msg is PendingMessagesMessage)
            {
                entered.TrySetResult();
                await release.Task;
            }
        };
        try
        {
            var drain = (Task)InvokePrivate("DrainNextPendingAsync", otherChat.Id)!;

            await WaitForConditionAsync(() => entered.Task.IsCompleted, TimeSpan.FromSeconds(5));
            entered.Task.IsCompleted.Should().BeTrue("drain не дошёл до окна");

            // В окне доставки в чат ДРУГОГО участника отзывается ПЕРВЫЙ — целевого чата это не касается.
            (await NewMembershipService().RemoveAsync(TestUserId, project.Id, MemberId)).Ok.Should().BeTrue();

            release.SetResult();
            await drain;

            // Доставка в чат неотозванного участника прошла: ход стартовал в его процесс.
            adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<int>(), It.IsAny<bool>()), Times.Once(),
                "перепроверка не должна глушить доставку в чат участника, который не отозван");
            _sut.GetPending(otherChat.Id).Should().BeEmpty();
        }
        finally { _broadcaster.Gate = null; }
    }
}
