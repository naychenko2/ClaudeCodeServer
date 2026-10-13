using System.Security.Claims;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Auth;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Делегированность держится на уровне ПРОГОНА, а не хода: признак хода у адаптера гаснет с его
// концом, а фоновые агенты делегированного хода (Agent run_in_background, Workflow) живут после
// result и ходят в MCP с заголовком этого чата. Гейты видят их делегированными, пока они живы и
// пока ход не подаст человек; без фоновых делегированность действует ровно ход.
public partial class SessionManagerTests
{
    private const string ChatsSend = "Отправка сообщения в другой чат";
    private const string ReportUp = "Отчёт в вышестоящий чат";

    // Адаптер ведёт себя как ClaudeSession: признаки хода живут ровно ход, живость фоновых
    // агентов прогона задаёт тест
    private sealed class RunFake
    {
        public Mock<ILlmSessionAdapter> Adapter = null!;
        public int LastDepth = -1;
        public bool BgAlive;
        public int TurnDepth;
        public bool TurnSuppressed;
        // Ход встал в очередь адаптера и ещё не начат: свой признак адаптер не взвёл
        // (у ClaudeSession он ставится в RunTurnAsync, уже после SendMessageAsync)
        public bool DeferTurn;

        public void EndTurn() { TurnDepth = 0; TurnSuppressed = false; }
    }

    private RunFake DepthAdapter(object entry)
    {
        var fake = new RunFake();
        var adapter = StubAdapter(entry);
        adapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<int>(), It.IsAny<bool>()))
            .Callback((string _, IReadOnlyList<string>? _, int d, bool s) =>
            {
                fake.LastDepth = d;
                if (fake.DeferTurn) return;
                fake.TurnDepth = d;
                fake.TurnSuppressed = s;
                fake.LastDepth = d;
            })
            .Returns(Task.CompletedTask);
        adapter.SetupGet(a => a.CurrentTurnAgentDepth).Returns(() => fake.TurnDepth);
        adapter.SetupGet(a => a.CurrentTurnSuppressTasksExecute).Returns(() => fake.TurnSuppressed);
        adapter.SetupGet(a => a.HasLiveBgAgents).Returns(() => fake.BgAlive);
        fake.Adapter = adapter;
        SetProcess(entry, adapter.Object);
        return fake;
    }

    // Ход «закончился» в ядре: статус свободен, ожидатель прошлого хода снят — как после result
    private void FreeChat(Session session)
    {
        session.Status = SessionStatus.Active;
        GetEntry(session.Id).GetType().GetField("TurnWaiter")!.SetValue(GetEntry(session.Id), null);
    }

    private void EndTurn(Session session, RunFake fake)
    {
        fake.EndTurn();
        FreeChat(session);
    }

    private bool GateDenies(string sessionId, string action, bool alsoWhenExecutorSuppressed = false,
        bool allowOnStickyRun = false) =>
        !DelegatedTurnGate.Decide(_sut, TestUserId, sessionId, action, alsoWhenExecutorSuppressed,
            allowInTeamImplement: false, failOpenWhenUnknown: false, allowOnStickyRun: allowOnStickyRun).Allowed;

    // Все гейты, закрытые на делегированном прогоне, разом
    private async Task AssertDelegatedAsync(string sessionId, AdminByStoreHandler handler, bool denied, string because)
    {
        GateDenies(sessionId, ChatsSend).Should().Be(denied, "chats_send: " + because);
        GateDenies(sessionId, "Запуск исполнителя").Should().Be(denied, "исполнитель: " + because);
        GateDenies(sessionId, "Выкатка прода", alsoWhenExecutorSuppressed: true).Should().Be(denied, "выкатка: " + because);
        GateDenies(sessionId, "Удаление публичной базы", alsoWhenExecutorSuppressed: true).Should().Be(denied, "Dify: " + because);
        (await AdminCheckAsync(handler, sessionId)).Should().Be(!denied, "AdminByStore: " + because);
    }

    // Сервисный токен админа идёт в admin-ручку из чата callerSessionId
    private static async Task<bool> AdminCheckAsync(AdminByStoreHandler handler, string? callerSessionId)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, TestUserId),
            new Claim(JwtService.TokenKindClaim, JwtService.ServiceTokenKind),
        ], "test"));
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/deploy/start";
        if (callerSessionId is not null) http.Request.Headers[McpEndpoints.CallerSessionHeader] = callerSessionId;
        var context = new AuthorizationHandlerContext([new AdminByStoreRequirement()], principal, http);
        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private AdminByStoreHandler AdminHandler()
    {
        var users = new Mock<IUserStore>();
        users.Setup(u => u.GetById(TestUserId)).Returns(new User { Id = TestUserId, Username = TestUsername, Role = "admin" });
        return new AdminByStoreHandler(users.Object, new DelegatedTurnGateAdapter(_sut));
    }

    private async Task<(Session Session, RunFake Fake)> RunChatAsync(string suffix, SessionStatus status = SessionStatus.Active)
    {
        var session = await MkBusySessionAsync(suffix, status);
        session.Name = "есть имя";
        return (session, DepthAdapter(GetEntry(session.Id)));
    }

    [Fact]
    public async Task ДелегированныйХод_БезФоновых_ДействуетРовноХод()
    {
        var (session, fake) = await RunChatAsync("rd-nobg");
        var handler = AdminHandler();

        await _sut.SendMessageAndWaitAsync(session.Id, "из другого чата", TimeSpan.Zero, agentDepth: 1);
        await AssertDelegatedAsync(session.Id, handler, denied: true, "сам делегированный ход");
        GateDenies(session.Id, ReportUp, allowOnStickyRun: true).Should().BeTrue("внутри делегированного хода отчёт закрыт");

        EndTurn(session, fake);
        await AssertDelegatedAsync(session.Id, handler, denied: false, "после result без фоновых — как до B1");
    }

    // Ход подан, а адаптер ещё не взвёл свой признак: делегированный ход уже делегирован, и
    // фоновые агенты прошлого прогона не делают его «липкой» частью (отчёт наверх закрыт)
    [Fact]
    public async Task ДелегированныйХод_ДоСтартаВАдаптере_УжеДелегирован()
    {
        var (session, fake) = await RunChatAsync("rd-window");
        fake.DeferTurn = true;
        fake.BgAlive = true;

        await _sut.SendMessageAndWaitAsync(session.Id, "из другого чата", TimeSpan.Zero, agentDepth: 1);

        _sut.GetActiveTurnDelegation(session.Id, TestUserId)
            .Should().Be(new TurnDelegationState(1, false, RunOnly: false), "ход в полёте — делегированный сам");
        GateDenies(session.Id, ReportUp, allowOnStickyRun: true).Should().BeTrue("внутри делегированного хода отчёт закрыт");
    }

    [Fact]
    public async Task ФоновыйАгентПослеResult_ГейтыЗакрыты_ОтчётНаверхОткрыт_ЗавершениеФоновыхСнимает()
    {
        var (session, fake) = await RunChatAsync("rd-bg");
        var handler = AdminHandler();

        await _sut.SendMessageAndWaitAsync(session.Id, "из другого чата", TimeSpan.Zero, agentDepth: 1);
        fake.BgAlive = true; // ход запустил фонового агента
        EndTurn(session, fake);

        _sut.GetActiveTurnDelegation(session.Id, TestUserId)
            .Should().Be(new TurnDelegationState(1, false, RunOnly: true), "липкая часть прогона");
        await AssertDelegatedAsync(session.Id, handler, denied: true, "фоновый агент делегированного хода");
        GateDenies(session.Id, ReportUp, allowOnStickyRun: true).Should().BeFalse("отчёт наверх на липкой части разрешён");
        GateDenies(session.Id, ReportUp).Should().BeTrue("без allowOnStickyRun липкая часть закрывает и его");

        // Авто-ход глубины 0 при живых агентах делегированность не снимает
        await _sut.SendMessageAsync(session.Id, "автоматика", [], auto: true);
        await AssertDelegatedAsync(session.Id, handler, denied: true, "авто-ход при живых фоновых");
        GateDenies(session.Id, ReportUp, allowOnStickyRun: true).Should().BeFalse("на авто-ходе глубины 0 отчёт разрешён");
        EndTurn(session, fake);

        // Все фоновые завершились — признак снят
        fake.BgAlive = false;
        await AssertDelegatedAsync(session.Id, handler, denied: false, "фоновых не осталось");
        fake.BgAlive = true;
        GateDenies(session.Id, ChatsSend).Should().BeFalse("снятый признак сам не возвращается");
    }

    // Авто-ход глубины 0 (не человек) при живых фоновых агентах признак прогона не снимает —
    // ни на время хода, ни после его result
    [Fact]
    public async Task ФоновыйАгентЖив_АвтоХодГлубины0_ПризнакПрогонаДержится()
    {
        var (session, fake) = await RunChatAsync("rd-auto");
        var handler = AdminHandler();

        await _sut.SendMessageAndWaitAsync(session.Id, "из другого чата", TimeSpan.Zero, agentDepth: 1);
        fake.BgAlive = true;
        EndTurn(session, fake);

        await _sut.SendMessageAsync(session.Id, "автоматика", [], auto: true);
        fake.Adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), 0, false),
            Times.AtLeastOnce(), "авто-ход дошёл до адаптера с глубиной 0");
        EndTurn(session, fake);

        _sut.GetActiveTurnDelegation(session.Id, TestUserId)
            .Should().Be(new TurnDelegationState(1, false, RunOnly: true), "после авто-хода прогон всё ещё делегированный");
        await AssertDelegatedAsync(session.Id, handler, denied: true, "после авто-хода при живых фоновых");
    }

    [Fact]
    public async Task ФоновыйАгентЖив_ХодЧеловекаСнимает()
    {
        var (session, fake) = await RunChatAsync("rd-human");
        var handler = AdminHandler();

        await _sut.SendMessageAndWaitAsync(session.Id, "из другого чата", TimeSpan.Zero, agentDepth: 1);
        fake.BgAlive = true;
        EndTurn(session, fake);
        GateDenies(session.Id, ChatsSend).Should().BeTrue();

        await _sut.SendMessageAsync(session.Id, "дальше я сам", []);
        fake.Adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), 0, false),
            Times.AtLeastOnce(), "ход человека дошёл до адаптера");
        await AssertDelegatedAsync(session.Id, handler, denied: false, "ход человека, агенты ещё живы");
        EndTurn(session, fake);
        await AssertDelegatedAsync(session.Id, handler, denied: false, "после хода человека");
    }

    // Ход-реакция на доклад исполнителя — тоже не человек: admin-действия закрыты и после его
    // result, пока живы его фоновые агенты
    [Fact]
    public async Task ПрогонХодаДоклада_ФоновыйЖив_AdminДействияЗакрыты_ЗавершениеСнимает()
    {
        var (session, fake) = await RunChatAsync("rd-report");
        var handler = AdminHandler();

        await _sut.SendMessageAsync(session.Id, "доклад исполнителя", [], auto: true, suppressTasksExecute: true);
        fake.BgAlive = true;
        EndTurn(session, fake);
        GateDenies(session.Id, "Выкатка прода", alsoWhenExecutorSuppressed: true).Should().BeTrue();
        (await AdminCheckAsync(handler, session.Id)).Should().BeFalse();

        fake.BgAlive = false;
        GateDenies(session.Id, "Выкатка прода", alsoWhenExecutorSuppressed: true).Should().BeFalse();
        (await AdminCheckAsync(handler, session.Id)).Should().BeTrue();
    }

    // Живость фоновых узнать нечем (адаптера у чата нет) — признак держится (fail-closed)
    [Fact]
    public async Task ЖивостьНеизвестна_ПризнакДержится()
    {
        var (session, fake) = await RunChatAsync("rd-unknown");

        await _sut.SendMessageAndWaitAsync(session.Id, "из другого чата", TimeSpan.Zero, agentDepth: 1);
        EndTurn(session, fake);
        // Адаптер ушёл до первого вопроса гейта: спросить живость не у кого
        GetEntry(session.Id).GetType().GetField("Process")!.SetValue(GetEntry(session.Id), null);
        GateDenies(session.Id, ChatsSend).Should().BeTrue("без адаптера живость неизвестна — fail-closed");
    }

    // chats_send в занятый чат встаёт в очередь и уходит в работу её разбором: глубина
    // сообщения обязана доехать до хода — иначе ход из очереди выглядел бы ходом человека
    [Fact]
    public async Task ChatsSendИзОчереди_ХодДелегированный()
    {
        var (session, fake) = await RunChatAsync("rd-queue", SessionStatus.Working);

        (await _sut.SendMessageAndWaitAsync(session.Id, "из другого чата", TimeSpan.Zero, agentDepth: 1))
            .Should().BeOfType<SendAndWaitResult.Queued>();
        FreeChat(session);
        var drain = (Task)typeof(SessionManager).GetMethod("DrainNextPendingAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_sut, [session.Id])!;
        await drain;

        fake.LastDepth.Should().Be(1, "ход из очереди подан адаптеру с глубиной сообщения");
        fake.BgAlive = true;
        EndTurn(session, fake);
        GateDenies(session.Id, ChatsSend).Should().BeTrue("и его фоновые агенты после result — делегированные");
    }
}
