using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
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
}
