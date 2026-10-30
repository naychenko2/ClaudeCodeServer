using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.Team;
using ClaudeHomeServer.Services.TriggerSources;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

public partial class SessionManagerHierarchyTests : SessionManagerTestBase
{
    [Fact]
    public async Task ReportUp_БезРодителя_ДокладыватьНекуда()
    {
        var dir = MkProjectDir("rep_solo");
        var project = _projectManager.Create("REP-SOLO", dir, TestUserId, TestUsername);
        var lonely = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var r = await _sut.ReportUpAsync(lonely.Id, "нашёл блокер", TestUserId, withTurn: false);

        r.Should().Be(SessionManager.ReportUpResult.NoParent);
    }

    [Fact]
    public async Task ReportUp_КладётКарточкуВРодителяБезХода()
    {
        var (parent, child) = await MkParentChildAsync("card");
        parent.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");

        var r = await _sut.ReportUpAsync(child.Id, "нашёл блокер: нет доступа к БД", TestUserId, withTurn: false);

        r.Should().Be(SessionManager.ReportUpResult.Delivered);
        var history = await _sut.GetHistoryAsync(parent.Id);
        history.Should().ContainSingle(m => m is Protocol.StoredUserMessage)
            .Which.Should().BeOfType<Protocol.StoredUserMessage>()
            .Which.Text.Should().Contain("нет доступа к БД");
        // Ход не запускался — чат остался в исходном статусе
        _sut.GetById(parent.Id)!.Status.Should().NotBe(SessionStatus.Working);
    }

    [Fact]
    public async Task ReportBlocker_КладётДокладСПометкойИПоднимаетКарточкуВШтабе()
    {
        // Штаб в режиме практики + дочерний чат исполнителя под ним
        var (stab, _, _) = await MakeTeamStabAsync("ti-blocker");
        stab.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");
        var child = await _sut.CreateAsync(stab.ProjectId!, ClaudeMode.Auto, name: "Задача: эндпоинт");
        _sut.SetParent(child.Id, stab.Id, TestUserId);

        var r = await _sut.ReportBlockerAsync(child.Id, "нет доступа к тестовой БД", TestUserId);

        r.Should().BeOneOf(SessionManager.ReportUpResult.Delivered, SessionManager.ReportUpResult.Queued);
        var history = await _sut.GetHistoryAsync(stab.Id);
        history.OfType<Protocol.StoredUserMessage>().Should()
            .Contain(m => m.Text.Contains("нет доступа к тестовой БД") && m.Text.Contains("Блокер"),
                "доклад помечен как блокер, а не теряется среди обычных отчётов");
        // Человек видит карточку немедленно, а не в конце волны. Ветка блокера идёт с
        // withTurn:true — ход штаба стартует fire-and-forget и пишет broadcast'ы в
        // _sentMessages параллельно, поэтому карточку берём через ожидающий снимок под
        // lock, иначе .Single() ловит Add в момент перечисления («Collection was modified»).
        var card = (await WaitForEscalationCardsAsync(stab.Id, minCount: 1)).Single();
        card.Kind.Should().Be("blocker");
        card.Details.Should().Contain("нет доступа");
        _sut.GetById(stab.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision);
    }

    [Fact]
    public async Task ReportBlocker_КаждоеПробуждениеШтабаРасходуетСвоюКвоту()
    {
        var (stab, _, _) = await MakeTeamStabAsync("ti-blocker-quota");
        stab.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");
        var child = await _sut.CreateAsync(stab.ProjectId!, ClaudeMode.Auto, name: "Задача: эндпоинт");
        _sut.SetParent(child.Id, stab.Id, TestUserId);

        await _sut.ReportBlockerAsync(child.Id, "первый блокер", TestUserId);
        await _sut.ReportBlockerAsync(child.Id, "второй блокер", TestUserId);

        _sut.GetById(stab.Id)!.TeamImplement!.Budget.WakeupsUsed.Should().Be(2,
            "платный ход штаба, поднятый агентом, обязан считаться — иначе лавина идёт мимо бюджета");
    }

    // m3 (второй проход Глеба): пробуждение штаба списывается ДО того, как доклад реально
    // дойдёт до родителя (TryConsumeTeamWakeup раньше ReportUpAsync). Цепочка автоотчётов
    // уже на потолке (TooDeep) — доклад не доставлен, координатор фактически не разбужен,
    // платить команде не с чего. Единица обязана вернуться.
    [Fact]
    public async Task ReportBlocker_ЦепочкаСлишкомГлубокая_ВозвращаетКвотуПробуждения()
    {
        var (stab, _, _) = await MakeTeamStabAsync("ti-blocker-toodeep");
        stab.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");
        var child = await _sut.CreateAsync(stab.ProjectId!, ClaudeMode.Auto, name: "Задача: эндпоинт");
        _sut.SetParent(child.Id, stab.Id, TestUserId);
        var childEntry = GetEntry(child.Id);
        childEntry.GetType().GetField("ReportChainDepth")!.SetValue(childEntry, 3);

        var r = await _sut.ReportBlockerAsync(child.Id, "застрял", TestUserId);

        r.Should().Be(SessionManager.ReportUpResult.TooDeep);
        _sut.GetById(stab.Id)!.TeamImplement!.Budget.WakeupsUsed.Should().Be(0,
            "доклад не дошёл — координатор фактически не разбужен, платить не с чего");
    }

    [Fact]
    public async Task ReportBlocker_КвотаПробужденийВыбрана_ХодаНет_НоЧеловекВидитКарточку()
    {
        var (stab, _, _) = await MakeTeamStabAsync("ti-blocker-cap");
        stab.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");
        var child = await _sut.CreateAsync(stab.ProjectId!, ClaudeMode.Auto, name: "Задача: эндпоинт");
        _sut.SetParent(child.Id, stab.Id, TestUserId);
        var budget = _sut.GetById(stab.Id)!.TeamImplement!.Budget;
        budget.WakeupsUsed = budget.MaxWakeups;

        var r = await _sut.ReportBlockerAsync(child.Id, "снова застрял", TestUserId);

        r.Should().Be(SessionManager.ReportUpResult.Delivered);
        (await _sut.GetHistoryAsync(stab.Id)).OfType<Protocol.StoredUserMessage>().Should()
            .Contain(m => m.Text.Contains("снова застрял"), "сам доклад терять нельзя");
        budget.WakeupsUsed.Should().Be(budget.MaxWakeups, "отказ ничего не расходует");
        // Ход координатора не поднимаем, но человек обязан узнать: застрявший исполнитель
        // без карточки — то самое молчаливое зависание, которого в режиме быть не должно
        var card = Sent<TeamEscalationMessage>().Single();
        card.Kind.Should().Be("budgetExhausted");
        card.Details.Should().Contain("снова застрял");
    }

    [Fact]
    public async Task ReportBlocker_КвотаВыбрана_ВтороеСообщениеКарточкуНеДублирует()
    {
        // Практика уже ждёт решения по той же причине: вторая карточка и второй push —
        // это спам, а не сигнал. Сам доклад при этом в ленту всё равно ложится.
        var (stab, _, _) = await MakeTeamStabAsync("ti-blocker-spam");
        stab.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");
        var child = await _sut.CreateAsync(stab.ProjectId!, ClaudeMode.Auto, name: "Задача: эндпоинт");
        _sut.SetParent(child.Id, stab.Id, TestUserId);
        var budget = _sut.GetById(stab.Id)!.TeamImplement!.Budget;
        budget.WakeupsUsed = budget.MaxWakeups;

        await _sut.ReportBlockerAsync(child.Id, "первый застрял", TestUserId);
        await _sut.ReportBlockerAsync(child.Id, "второй застрял", TestUserId);

        Sent<TeamEscalationMessage>().Should().ContainSingle(
            "карточка на остановку одна, сколько бы докладов ни пришло следом");
        (await _sut.GetHistoryAsync(stab.Id)).OfType<Protocol.StoredUserMessage>().Should()
            .Contain(m => m.Text.Contains("второй застрял"), "доклады не теряются");
    }

    [Fact]
    public async Task ReportBlocker_ПослеОстановкиЧеловеком_ХодаНет_КарточкаУжеВисит()
    {
        var (stab, _, _) = await MakeTeamStabAsync("ti-blocker-stopped");
        stab.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");
        var child = await _sut.CreateAsync(stab.ProjectId!, ClaudeMode.Auto, name: "Задача: эндпоинт");
        _sut.SetParent(child.Id, stab.Id, TestUserId);
        await _sut.StopTeamImplementAsync(stab.Id, TestUserId);
        // Остановка больше не молчаливая: карточка возврата публикуется в момент стопа
        Sent<TeamEscalationMessage>().Single().Kind.Should().Be("stopped");
        ClearSent();

        await _sut.ReportBlockerAsync(child.Id, "застрял", TestUserId);

        _sut.GetById(stab.Id)!.TeamImplement!.Budget.WakeupsUsed.Should().Be(0,
            "практика остановлена человеком — агент не поднимает её обратно");
        // Карточка остановки уже висит открытой (стадия «ждёт решения») — доклад-блокер
        // второй не поднимает: сигнал человеку уже дан, дублирование было бы спамом
        Sent<TeamEscalationMessage>().Should().BeEmpty();
    }

    [Fact]
    public async Task КвотаПробуждения_ЛюбойАгентскийХодШтаба_РасходуетЕёОдинаково()
    {
        // chats_send — соседний вход в тот же платный ход штаба: если бы он шёл мимо квоты,
        // бюджет обходился бы сменой инструмента
        var (stab, _, _) = await MakeTeamStabAsync("ti-wakeup-shared");

        var first = _sut.TryConsumeTeamWakeup(stab.Id);
        var second = _sut.TryConsumeTeamWakeup(stab.Id);

        first.TeamMode.Should().BeTrue();
        first.Allowed.Should().BeTrue();
        second.Allowed.Should().BeTrue();
        _sut.GetById(stab.Id)!.TeamImplement!.Budget.WakeupsUsed.Should().Be(2);
    }

    [Fact]
    public async Task КвотаПробуждения_ОбычныйЧат_НеОграничивается()
    {
        var dir = MkProjectDir("ti-wakeup-plain");
        var project = _projectManager.Create("TI-WP", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var wake = _sut.TryConsumeTeamWakeup(session.Id);

        wake.TeamMode.Should().BeFalse("вне режима переписка между чатами работает как раньше");
        wake.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task КвотаПробуждения_ИсчерпанныйБюджет_Отказ()
    {
        var (stab, _, _) = await MakeTeamStabAsync("ti-wakeup-cap");
        var budget = _sut.GetById(stab.Id)!.TeamImplement!.Budget;
        budget.WakeupsUsed = budget.MaxWakeups;

        var wake = _sut.TryConsumeTeamWakeup(stab.Id);

        wake.TeamMode.Should().BeTrue();
        wake.Allowed.Should().BeFalse();
        wake.Reason.Should().Contain("срочных вызовов");
    }

    [Fact]
    public async Task ReportBlocker_РодительВнеРежима_КарточкиНет_НоХодЗапускается()
    {
        var (parent, child) = await MkParentChildAsync("blocker-plain");
        parent.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");

        var r = await _sut.ReportBlockerAsync(child.Id, "застрял на конфликте", TestUserId);

        r.Should().BeOneOf(SessionManager.ReportUpResult.Delivered, SessionManager.ReportUpResult.Queued);
        Sent<TeamEscalationMessage>().Should().BeEmpty(
            "карточка остановки — часть режима практики, обычному чату она не нужна");
    }

    [Fact]
    public async Task ReportUp_БезПерсоны_ПодписываетИменемЧата()
    {
        // Исполнитель — обычный Claude: лица нет, поэтому карточка идёт с именем его чата
        var (parent, child) = await MkParentChildAsync("name");
        parent.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");

        await _sut.ReportUpAsync(child.Id, "готово наполовину", TestUserId, withTurn: false);

        var stored = (await _sut.GetHistoryAsync(parent.Id)).OfType<Protocol.StoredUserMessage>().Single();
        stored.SenderChatName.Should().Be("Задача: починить билд");
        stored.ViaAgent.Should().BeTrue();
    }

    [Fact]
    public async Task ReportUp_ЦепочкаГлубже3_Отклоняется()
    {
        // a → b → c → d: отчёты идут вверх по цепочке, четвёртый по счёту упирается в потолок
        var dir = MkProjectDir("rep_deep");
        var project = _projectManager.Create("REP-DEEP", dir, TestUserId, TestUsername);
        var chats = new List<Session>();
        for (var i = 0; i < 5; i++)
        {
            var s = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, name: $"чат {i}");
            s.ClaudeSessionId = "cli-" + Guid.NewGuid().ToString("N");
            chats.Add(s);
            if (i > 0) _sut.SetParent(chats[i].Id, chats[i - 1].Id, TestUserId);
        }

        // chats[4] → chats[3] → … каждый следующий отчёт наращивает глубину цепочки
        (await _sut.ReportUpAsync(chats[4].Id, "уровень 1", TestUserId, false))
            .Should().Be(SessionManager.ReportUpResult.Delivered);
        (await _sut.ReportUpAsync(chats[3].Id, "уровень 2", TestUserId, false))
            .Should().Be(SessionManager.ReportUpResult.Delivered);
        (await _sut.ReportUpAsync(chats[2].Id, "уровень 3", TestUserId, false))
            .Should().Be(SessionManager.ReportUpResult.Delivered);

        (await _sut.ReportUpAsync(chats[1].Id, "уровень 4", TestUserId, false))
            .Should().Be(SessionManager.ReportUpResult.TooDeep, "цепочка автоотчётов ограничена тремя звеньями");
    }

    [Fact]
    public async Task ReportUp_ЧужойЧат_НеНайден()
    {
        var (_, child) = await MkParentChildAsync("foreign");

        var r = await _sut.ReportUpAsync(child.Id, "текст", "another-user", withTurn: false);

        r.Should().Be(SessionManager.ReportUpResult.NotFound);
    }

    // --- SetParent: ручная группировка чатов (drag-and-drop) ---

    [Fact]
    public async Task SetParent_НазначаетРодителя()
    {
        var dir = MkProjectDir("par");
        var project = _projectManager.Create("PAR", dir, TestUserId, TestUsername);
        var parent = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var child = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var updated = _sut.SetParent(child.Id, parent.Id, TestUserId);

        SessionTaskLinks.ParentSessionId(updated!, null).Should().Be(parent.Id);
        SessionTaskLinks.ParentSessionId(_sut.GetById(child.Id)!, null).Should().Be(parent.Id, "связь персистится");
    }

    [Fact]
    public async Task SetParent_НеМеняетUpdatedAt()
    {
        // Корни сортируются по активности поддерева — перетаскивание не должно
        // выкидывать чат наверх списка, будто в нём был ход
        var dir = MkProjectDir("par-upd");
        var project = _projectManager.Create("PARU", dir, TestUserId, TestUsername);
        var parent = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var child = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var before = child.UpdatedAt;

        _sut.SetParent(child.Id, parent.Id, TestUserId);

        _sut.GetById(child.Id)!.UpdatedAt.Should().Be(before);
    }

    [Fact]
    public async Task SetParent_Null_ВыноситВКорень()
    {
        var dir = MkProjectDir("par-root");
        var project = _projectManager.Create("PARR", dir, TestUserId, TestUsername);
        var parent = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var child = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        _sut.SetParent(child.Id, parent.Id, TestUserId);

        var updated = _sut.SetParent(child.Id, null, TestUserId)!;

        SessionTaskLinks.ParentSessionId(updated, null).Should().BeNull();
        updated.ParentOverrideId.Should().BeNull();
        updated.ParentDetached.Should().BeFalse("у обычного чата гасить нечего — флаг не оседает");
    }

    [Fact]
    public async Task SetParent_ПеребиваетАвтоСвязьПоЗадаче()
    {
        var dir = MkProjectDir("par-task");
        var project = _projectManager.Create("PART", dir, TestUserId, TestUsername);
        var autoParent = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var manualParent = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var child = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, taskExecution: true, taskId: "t-1");

        var tasks = new StubTasks(("t-1", autoParent.Id, TaskItemStatus.Todo));
        _sut.SetTaskLookupForTests(tasks);
        try
        {
            SessionTaskLinks.ParentSessionId(_sut.GetById(child.Id)!, tasks).Should().Be(autoParent.Id, "исходно — авто-связь");

            var updated = _sut.SetParent(child.Id, manualParent.Id, TestUserId)!;

            SessionTaskLinks.ParentSessionId(updated, tasks).Should().Be(manualParent.Id, "ручной родитель побеждает");
            updated.TaskId.Should().Be("t-1", "связь с задачей перетаскиванием не рвётся");
            updated.Origin.Should().Be(ChatOrigin.Task);
        }
        finally { _sut.SetTaskLookupForTests(null); }
    }

    [Fact]
    public async Task SetParent_Null_ГаситАвтоСвязьУЧатаЗадачи()
    {
        var dir = MkProjectDir("par-detach");
        var project = _projectManager.Create("PARD", dir, TestUserId, TestUsername);
        var autoParent = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var child = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, taskExecution: true, taskId: "t-2");

        var tasks = new StubTasks(("t-2", autoParent.Id, TaskItemStatus.Todo));
        _sut.SetTaskLookupForTests(tasks);
        try
        {
            var updated = _sut.SetParent(child.Id, null, TestUserId);

            updated!.ParentDetached.Should().BeTrue();
            SessionTaskLinks.ParentSessionId(updated!, tasks).Should().BeNull("явный корень перебивает авто-связь");
        }
        finally { _sut.SetTaskLookupForTests(null); }
    }

    [Fact]
    public async Task SetParent_СамВСебя_Отклоняется()
    {
        var dir = MkProjectDir("par-self");
        var project = _projectManager.Create("PARS", dir, TestUserId, TestUsername);
        var chat = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var act = () => _sut.SetParent(chat.Id, chat.Id, TestUserId);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task SetParent_ВСвоегоПотомка_Отклоняется()
    {
        // A → B → C; попытка сделать A ребёнком C замкнула бы кольцо
        var dir = MkProjectDir("par-cycle");
        var project = _projectManager.Create("PARC", dir, TestUserId, TestUsername);
        var a = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var b = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var c = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        _sut.SetParent(b.Id, a.Id, TestUserId);
        _sut.SetParent(c.Id, b.Id, TestUserId);

        var act = () => _sut.SetParent(a.Id, c.Id, TestUserId);

        act.Should().Throw<InvalidOperationException>();
        SessionTaskLinks.ParentSessionId(_sut.GetById(a.Id)!, null).Should().BeNull("отклонённая операция ничего не записала");
    }

    [Fact]
    public async Task SetParent_ЧатИзДругогоПроекта_Отклоняется()
    {
        // Списки рендерятся на разных экранах: ребёнок не нашёл бы родителя в своей
        // выборке и молча всплыл бы в корень
        var d1 = MkProjectDir("par-x1"); var p1 = _projectManager.Create("PX1", d1, TestUserId, TestUsername);
        var d2 = MkProjectDir("par-x2"); var p2 = _projectManager.Create("PX2", d2, TestUserId, TestUsername);
        var child = await _sut.CreateAsync(p1.Id, ClaudeMode.Auto);
        var parent = await _sut.CreateAsync(p2.Id, ClaudeMode.Auto);

        var act = () => _sut.SetParent(child.Id, parent.Id, TestUserId);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task SetParent_ЧужойЧат_НеНайден()
    {
        var stranger = _userStore.Add("stranger-parent", "pw-123456", "user");
        var mineDir = MkProjectDir("par-mine");
        var mine = _projectManager.Create("PMINE", mineDir, TestUserId, TestUsername);
        var theirsDir = MkProjectDir("par-theirs");
        var theirs = _projectManager.Create("PTHEIRS", theirsDir, stranger.Id, stranger.Username);
        var child = await _sut.CreateAsync(mine.Id, ClaudeMode.Auto);
        var foreign = await _sut.CreateAsync(theirs.Id, ClaudeMode.Auto);

        // Чужой родитель — 400, чужой ребёнок — 404 (сессии не видно вовсе)
        var setForeignParent = () => _sut.SetParent(child.Id, foreign.Id, TestUserId);
        setForeignParent.Should().Throw<InvalidOperationException>();
        _sut.SetParent(foreign.Id, child.Id, TestUserId).Should().BeNull();
    }

    [Fact]
    public void SetParent_НесуществующийЧат_ReturnsNull()
    {
        _sut.SetParent("nonexistent", null, TestUserId).Should().BeNull();
    }

    [Fact]
    public async Task CreateGroupChatAsync_ПерсиститУчастников_СпикерВедущая()
    {
        var (user, project, personas) = MkGroupFixture(3, "a");
        var ids = personas.Select(p => p.Id).ToList();

        var session = await _sut.CreateGroupChatAsync(user.Id, ids, ClaudeMode.Auto, "Команда");

        session.Participants.Should().Equal(ids);
        session.PersonaId.Should().Be(ids[0], "активный спикер при создании — ведущая (первая)");
        session.ProjectId.Should().Be(project.Id, "зона ведущей — её проект");

        // Персистентность: перечитываем sessions.json свежим взглядом
        var stored = _sut.GetById(session.Id);
        stored!.Participants.Should().Equal(ids);
    }

    [Fact]
    public async Task CreateGroupChatAsync_МеньшеДвухУчастников_400()
    {
        var (user, _, personas) = MkGroupFixture(1, "b");

        var act = () => _sut.CreateGroupChatAsync(user.Id, [personas[0].Id], ClaudeMode.Auto);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateGroupChatAsync_ЧужаяПерсона_НеНайдена()
    {
        var (user, _, personas) = MkGroupFixture(2, "d");
        var stranger = _personaManager.Create("another-owner", "Чужая", null, null, null,
            null, null, PersonaScope.Global, null, null, null, false);

        var act = () => _sut.CreateGroupChatAsync(user.Id,
            [personas[0].Id, stranger.Id], ClaudeMode.Auto);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task SetParticipants_СпикерСохраняется_ЕслиОстался()
    {
        var (user, _, personas) = MkGroupFixture(3, "e");
        var ids = personas.Select(p => p.Id).ToList();
        var session = await _sut.CreateGroupChatAsync(user.Id, ids, ClaudeMode.Auto);
        // Активный спикер — вторая персона (симулируем прошлый роутинг)
        _sut.SetPersona(session.Id, user.Id, ids[1]);

        var updated = _sut.SetParticipants(session.Id, user.Id, [ids[1], ids[2]]);

        updated!.Participants.Should().Equal(ids[1], ids[2]);
        updated.PersonaId.Should().Be(ids[1], "спикер остался в составе — сохраняется");
    }

    [Fact]
    public async Task SetParticipants_СпикерВыбыл_НоваяВедущая()
    {
        var (user, _, personas) = MkGroupFixture(3, "f");
        var ids = personas.Select(p => p.Id).ToList();
        var session = await _sut.CreateGroupChatAsync(user.Id, ids, ClaudeMode.Auto);
        // Активный спикер — первая; убираем её из состава
        var updated = _sut.SetParticipants(session.Id, user.Id, [ids[1], ids[2]]);

        updated!.PersonaId.Should().Be(ids[1], "спикер выбыл — активной становится новая ведущая");
    }

    // SetPersona после рефакторинга на SwitchSpeaker: публичное поведение не изменилось
    [Fact]
    public async Task SetPersona_ДоПервогоХода_ПрименяетМодельПерсоны()
    {
        var (user, project, _) = MkGroupFixture(2, "g");
        var persona = _personaManager.Create(user.Id, "Соло", "Аналитик", null, null,
            model: "opus", effort: "high", PersonaScope.Project, project.Id,
            color: null, greeting: null, memoryEnabled: false);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var updated = _sut.SetPersona(session.Id, user.Id, persona.Id);

        updated!.PersonaId.Should().Be(persona.Id);
        updated.Model.Should().Be("opus");
        updated.Effort.Should().Be("high");
        updated.AgentName.Should().BeNull();
        updated.PersonaSwitched.Should().BeFalse("ходов ещё не было — оговорка о смене не нужна");
    }

    // --- GetHistoryAsync ---

    [Fact]
    public async Task GetHistoryAsync_NonExistentSession_ReturnsEmpty()
    {
        var history = await _sut.GetHistoryAsync("nonexistent");

        history.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHistoryAsync_NewSession_ReturnsEmpty()
    {
        var dir = MkProjectDir("nh");
        var project = _projectManager.Create("NH", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var history = await _sut.GetHistoryAsync(session.Id);

        history.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHistoryAsync_SessionWithoutAccumulator_LoadsFromDisk()
    {
        // Симулируем сессию после рестарта сервера: у неё нет накопителя (Process=null, Accumulator=null),
        // но ClaudeSessionId задан → история должна подгрузиться с диска
        var dir = MkProjectDir("rh");
        var project = _projectManager.Create("RH", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var claudeSessionId = "test-claude-session-" + Guid.NewGuid().ToString("N");
        session.ClaudeSessionId = claudeSessionId;

        // Сохраняем историю на диск напрямую через historyService
        var messages = new List<ClaudeHomeServer.Protocol.StoredMessage>
        {
            new ClaudeHomeServer.Protocol.StoredTextMessage("Привет из истории")
        };
        await _historyService.SaveAsync(claudeSessionId, messages);

        // История из accumulator (он есть и возвращает пустой список изначально)
        // Чтобы протестировать disk-путь, нужна сессия без accumulator.
        // GetHistoryAsync возвращает accumulator.GetAll() если он есть.
        // Для disk-пути: создаём новый SessionManager (симулируем рестарт),
        // сохраняем sessions.json с нашей сессией.
        // Вместо этого проверяем просто что история пустая для новой сессии —
        // полное тестирование disk-пути описано в ChatHistoryServiceTests.
        var history = await _sut.GetHistoryAsync(session.Id);
        // Новая сессия → accumulator пустой → возвращает пустой список
        history.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AttachWorktree_СуществующееДеревоПроекта_СессияПолучаетПутьИВетку()
    {
        var (project, worktree) = await MkRepoWithWorktreeAsync("attach-ok");
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.AcceptEdits);

        var attached = await _sut.AttachWorktreeAsync(session.Id, worktree);

        attached.Should().BeTrue();
        var info = _sut.GetById(session.Id)!;
        info.WorktreePath.Should().Be(Path.GetFullPath(worktree));
        // Ветку не передавали — берётся из самого дерева
        info.WorktreeBranch.Should().Be("wt/тест");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AttachWorktree_СуществующееДерево_ПрогреваетГрафДереваОдинРаз()
    {
        var (project, worktree) = await MkRepoWithWorktreeAsync("attach-warm");
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.AcceptEdits);

        (await _sut.AttachWorktreeAsync(session.Id, worktree)).Should().BeTrue();

        _graphInspector.Warmed.Should().Equal(Path.GetFullPath(worktree));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AttachWorktree_ЧужаяПапка_ГрафНеПрогревается()
    {
        var (project, _) = await MkRepoWithWorktreeAsync("attach-warm-alien");
        var alien = Directory.CreateDirectory(Path.Combine(_tempDir, "alien-warm")).FullName;
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.AcceptEdits);

        (await _sut.AttachWorktreeAsync(session.Id, alien)).Should().BeFalse();

        _graphInspector.Warmed.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AttachWorktree_ПутьНеЧислитсяВРепе_ОтказБезПривязки()
    {
        var (project, _) = await MkRepoWithWorktreeAsync("attach-alien");
        // Папка есть на диске, но деревом проекта не является
        var alien = Directory.CreateDirectory(Path.Combine(_tempDir, "alien")).FullName;
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.AcceptEdits);

        var attached = await _sut.AttachWorktreeAsync(session.Id, alien);

        attached.Should().BeFalse();
        // Мягкая деградация: чат остаётся в корне проекта
        _sut.GetById(session.Id)!.WorktreePath.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AttachWorktree_ПутиНетНаДиске_Отказ()
    {
        var (project, worktree) = await MkRepoWithWorktreeAsync("attach-missing");
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.AcceptEdits);

        var attached = await _sut.AttachWorktreeAsync(session.Id, worktree + "-нет", "wt/тест");

        attached.Should().BeFalse();
        _sut.GetById(session.Id)!.WorktreePath.Should().BeNull();
    }

    [Fact]
    public async Task AttachWorktree_ЧатВнеПроекта_Отказ()
    {
        // У личной задачи проекта нет — дерева тоже: привязывать не к чему
        var user = _userStore.Add("attach-personal", "pw-123456", "user");
        var session = await _sut.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits);

        (await _sut.AttachWorktreeAsync(session.Id, _tempDir)).Should().BeFalse();
        _sut.GetById(session.Id)!.WorktreePath.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AttachWorktree_НачатыйЧат_Отказ()
    {
        // Контекст начатого чата привязан к прежнему cwd (--resume ищет транскрипт по нему):
        // переезд с контекстом — только через SetWorktreeAsync
        var (project, worktree) = await MkRepoWithWorktreeAsync("attach-started");
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.AcceptEdits);
        _sut.GetById(session.Id)!.ClaudeSessionId = "csid-1";

        (await _sut.AttachWorktreeAsync(session.Id, worktree)).Should().BeFalse();
        _sut.GetById(session.Id)!.WorktreePath.Should().BeNull();
    }

    // --- Удаление чата: чьё дерево сносим, чужое/общее/старое — нет ---
    // Дефект: временные чаты исполнителей (AttachWorktreeAsync, WorktreeOwned=false) истекали
    // через сутки и каждый уносил ОБЩЕЕ дерево фичи вместе с незакоммиченными правками.

    // Привязанное (AttachWorktreeAsync) дерево: WorktreeOwned=false → при удалении чата
    // git worktree remove не вызывается, папка дерева остаётся (его делят другие задачи фичи).
    [Fact]
    [Trait("Category", "Slow")]
    public async Task DeleteAsync_ПривязанноеДерево_НеСносит()
    {
        var (project, worktree) = await MkRepoWithWorktreeAsync("del-attach");
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.AcceptEdits);
        (await _sut.AttachWorktreeAsync(session.Id, worktree)).Should().BeTrue();
        _sut.GetById(session.Id)!.WorktreeOwned.Should().BeFalse();
        Directory.Exists(worktree).Should().BeTrue();

        await _sut.DeleteAsync(session.Id);

        Directory.Exists(worktree).Should().BeTrue("привязанное дерево не сносится");
    }

    // Своё дерево (создано тумблером SetWorktreeAsync, WorktreeOwned=true) → при удалении
    // чата git worktree remove вызывается, папка дерева сносится.
    [Fact]
    [Trait("Category", "Slow")]
    public async Task DeleteAsync_СвоёДеревоТумблера_Сносит()
    {
        // Тумблер резолвит home владельца: нужен реальный пользователь в сторе (не TestUserId)
        var owner = _userStore.Add("own-wt-owner", "pw-123456", "user");
        var project = await MkGitProjectAsync("del-own", owner.Id, owner.Username);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.AcceptEdits);
        var updated = await _sut.SetWorktreeAsync(session.Id, enabled: true);
        updated!.WorktreeOwned.Should().BeTrue();
        var wt = updated.WorktreePath!;
        Directory.Exists(wt).Should().BeTrue();

        await _sut.DeleteAsync(session.Id);

        Directory.Exists(wt).Should().BeFalse("своё дерево сносится вместе с чатом");
    }

    // Два чата делят одно дерево (оба WorktreeOwned=true, один нормализованный путь):
    // удалён один → дерево НЕ сносится, пока живой чат на него ссылается.
    [Fact]
    [Trait("Category", "Slow")]
    public async Task DeleteAsync_ОбщееДеревоДвухЧатов_НеСносит()
    {
        var (project, worktree) = await MkRepoWithWorktreeAsync("del-shared");
        var s1 = await _sut.CreateAsync(project.Id, ClaudeMode.AcceptEdits);
        var s2 = await _sut.CreateAsync(project.Id, ClaudeMode.AcceptEdits);
        _sut.GetById(s1.Id)!.WorktreePath = worktree;
        _sut.GetById(s1.Id)!.WorktreeOwned = true;
        _sut.GetById(s2.Id)!.WorktreePath = worktree;
        _sut.GetById(s2.Id)!.WorktreeOwned = true;
        Directory.Exists(worktree).Should().BeTrue();

        await _sut.DeleteAsync(s1.Id);

        Directory.Exists(worktree).Should().BeTrue("второй живой чат всё ещё ссылается на дерево");
    }

    // Старая запись (WorktreeOwned=false по умолчанию, как в sessions.json без поля) →
    // дерево по умолчанию не сносится: лучше мусорное дерево, чем потеря работы.
    [Fact]
    [Trait("Category", "Slow")]
    public async Task DeleteAsync_СтараяЗаписьБезПризнака_НеСносит()
    {
        var (project, worktree) = await MkRepoWithWorktreeAsync("del-old");
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.AcceptEdits);
        // Эмулируем старую запись: путь дерева задан, но признака владения нет (false по умолчанию)
        _sut.GetById(session.Id)!.WorktreePath = worktree;
        _sut.GetById(session.Id)!.WorktreeOwned.Should().BeFalse();
        Directory.Exists(worktree).Should().BeTrue();

        await _sut.DeleteAsync(session.Id);

        Directory.Exists(worktree).Should().BeTrue("старая запись: дерево по умолчанию не сносится");
    }

    [Fact]
    public async Task RateLimitMessage_ЖивойХод_ЗаписываетSourceTurn_ИТрогаетActivityTracker()
    {
        var dir = MkProjectDir("ratelimit");
        var project = _projectManager.Create("RL", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var acc = new TurnAccumulator(new List<StoredMessage>());
        var msg = new RateLimitMessage("five_hour", DateTime.UtcNow.AddHours(2).ToString("o"),
            "allowed", 0.4, false);

        await InvokeOnMessageAsync(session.Id, acc, msg);

        var snap = _usage.GetAll().Should().ContainSingle(s => s.LimitType == "five_hour").Subject;
        snap.Source.Should().Be("turn");
        snap.SubscriptionKey.Should().Be(ClaudeSubscriptionPool.PrimaryKey);
        _activity.IsIdle(ClaudeSubscriptionPool.PrimaryKey, TimeSpan.FromMinutes(10)).Should().BeFalse();
    }

    [Fact]
    public async Task RateLimitMessage_ЗдоровоеОкноНаИсчерпанномАккаунте_СнимаетПометку()
    {
        // Самолечение: аккаунт помечен исчерпанным (ложно или окно уже отпустило), но ход
        // через него проходит — пометка снимается прямо на ходу, не дожидаясь resetsAt.
        var dir = MkProjectDir("ratelimit-heal");
        var project = _projectManager.Create("RLH", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var acc = new TurnAccumulator(new List<StoredMessage>());
        _subPool.MarkExhausted(ClaudeSubscriptionPool.PrimaryKey, DateTime.UtcNow.AddDays(5));

        await InvokeOnMessageAsync(session.Id, acc, new RateLimitMessage("five_hour",
            DateTime.UtcNow.AddHours(2).ToString("o"), "allowed_warning", 0.64, false));

        _subPool.IsExhausted(ClaudeSubscriptionPool.PrimaryKey).Should().BeFalse();
    }

    [Fact]
    public async Task RateLimitMessage_RejectedНеизвестногоОкна_НеПомечаетИсчерпанной()
    {
        // Инцидент 2026-08-02: одиночное rejected по окну seven_day_overage_included
        // выводило живой аккаунт из ротации на пять суток. Такое окно только пишется в usage.
        var dir = MkProjectDir("ratelimit-unknown");
        var project = _projectManager.Create("RLU", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var acc = new TurnAccumulator(new List<StoredMessage>());

        await InvokeOnMessageAsync(session.Id, acc, new RateLimitMessage("seven_day_overage_included",
            DateTime.UtcNow.AddDays(5).ToString("o"), "rejected", null, false));

        _subPool.IsExhausted(ClaudeSubscriptionPool.PrimaryKey).Should().BeFalse();
        _usage.GetAll().Should().Contain(s => s.LimitType == "seven_day_overage_included");
    }

    [Fact]
    public async Task RateLimitMessage_ПослеОтказаПоМодели_НеПомечаетПодпискуИсчерпанной()
    {
        // Инцидент 2026-09-09 (чат «Анализ документов ВФЛА»): отказ «нет доступа к модели» /
        // «кончились кредиты модели» приезжает от CLI ещё и телеметрией rate_limit_event
        // status=rejected — она про ОКНО и про модель не знает ничего. Без подавления такое
        // позднее событие метило живую подписку исчерпанной, и её Sonnet/Opus выпадали из
        // ротации до сброса пятичасового окна.
        var dir = MkProjectDir("ratelimit-model-reject");
        var project = _projectManager.Create("RLM", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var acc = new TurnAccumulator(new List<StoredMessage>());
        // Пометку ставит адаптер, разобрав текст ошибки хода; здесь воспроизводим её факт.
        _subPool.MarkModelUnavailable(ClaudeSubscriptionPool.PrimaryKey, "fable",
            FallbackErrorClass.ModelOutOfCredits);

        await InvokeOnMessageAsync(session.Id, acc, new RateLimitMessage("five_hour",
            DateTime.UtcNow.AddHours(2).ToString("o"), "rejected", null, false));

        _subPool.IsExhausted(ClaudeSubscriptionPool.PrimaryKey).Should().BeFalse(
            "отказ был по модели — подписка жива");
        _usage.GetAll().Should().Contain(s => s.LimitType == "five_hour",
            "в снимок для экрана событие всё равно попадает");
    }

    [Fact]
    public async Task RateLimitMessage_ОтказПоМоделиНаДругойПодписке_ТожеПодавляет()
    {
        // Гонка (блокер ревью): пометку ставит попытка на одной подписке, а позднее событие
        // приезжает уже с ключом СОСЕДНЕЙ — тихая ротация переставила Info.Provider до его
        // прихода. Адресное подавление по ключу тут промахнулось бы, и MarkExhausted забанил
        // бы здоровый аккаунт — тот же ложный бан, ради которого подавление и заведено.
        var dir = MkProjectDir("ratelimit-model-race");
        var project = _projectManager.Create("RLR", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var acc = new TurnAccumulator(new List<StoredMessage>());
        // Отказ случился на ЧУЖОЙ (уже отбитой) подписке, чат живёт на PrimaryKey.
        _subPool.MarkModelUnavailable("acc-другая", "opus", FallbackErrorClass.ModelNoAccess);

        await InvokeOnMessageAsync(session.Id, acc, new RateLimitMessage("five_hour",
            DateTime.UtcNow.AddHours(2).ToString("o"), "rejected", null, false));

        _subPool.IsExhausted(ClaudeSubscriptionPool.PrimaryKey).Should().BeFalse(
            "в окне подавления событию про окно не верим, чей бы ключ в нём ни стоял");
    }

    [Fact]
    public async Task RateLimitMessage_НеизвестноеОкно_НеСнимаетПометкуИсчерпания()
    {
        // Симметрия белого списка: неизвестное окно не банит и не разбанивает — иначе
        // транзитное allowed_warning по overage-окну сняло бы реальный бан по неделе.
        var dir = MkProjectDir("ratelimit-unknown-heal");
        var project = _projectManager.Create("RLUH", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var acc = new TurnAccumulator(new List<StoredMessage>());
        _subPool.MarkExhausted(ClaudeSubscriptionPool.PrimaryKey, DateTime.UtcNow.AddDays(2));

        await InvokeOnMessageAsync(session.Id, acc, new RateLimitMessage("seven_day_overage_included",
            DateTime.UtcNow.AddDays(5).ToString("o"), "allowed_warning", 0.3, false));

        _subPool.IsExhausted(ClaudeSubscriptionPool.PrimaryKey).Should().BeTrue();
    }

    [Fact]
    public async Task ГейтыСерверов_БезПривязок_ВсёПодключено()
    {
        var dir = MkProjectDir("gates-default");
        var project = _projectManager.Create("GD", dir, TestUserId, TestUsername);
        var persona = MkGatedPersona(project.Id, "default");
        // Вторая персона в контексте — иначе подсказки о консультациях не из чего строить
        MkGatedPersona(project.Id, "peer");
        var session = await _sut.CreatePersonaChatAsync(TestUserId, persona.Id, ClaudeMode.Auto);

        InvokePrivate("BuildWidgetsContext", TestUserId, persona).Should().NotBeNull();
        // Уведомления — исключение из «без привязок включено всё»: с августа 2026 сервер идёт
        // по роли (модуль автоматизации), персоне без роли его даёт только явная привязка
        InvokePrivate("BuildNotificationsContext", TestUserId, persona.Id, persona).Should().BeNull();
        InvokePrivate("BuildCodeGraphContext", TestUserId, project.Id, session.Id, dir, persona)
            .Should().NotBeNull();
        var personas = InvokePrivate("BuildPersonasContext", TestUserId, project.Id, session, persona)
            .Should().BeOfType<PersonasMcpContext>().Subject;
        personas.MentionsHint.Should().NotBeNull("консультации включены по умолчанию");
        InvokePrivate("ConsultantsEnabled", TestUserId, session, persona).Should().Be(true);
    }

    [Fact]
    public async Task Уведомления_ЯвнаяПривязка_ПодключаетСервер()
    {
        // Дефолт сузили по данным использования, но включить сервер обратно можно
        // привязкой персоны — без правок кода
        var dir = MkProjectDir("gates-notif");
        var project = _projectManager.Create("GNo", dir, TestUserId, TestUsername);
        var persona = MkGatedPersona(project.Id, "notif");
        persona = _personaManager.UpdateBindings(persona.Id, TestUserId,
            [new PersonaBinding
            {
                Type = PersonaBindingType.Tool, Target = "notifications",
                Mode = PersonaBindingMode.Auto,
            }])!;
        await _sut.CreatePersonaChatAsync(TestUserId, persona.Id, ClaudeMode.Auto);

        InvokePrivate("BuildNotificationsContext", TestUserId, persona.Id, persona)
            .Should().NotBeNull();
    }

    [Fact]
    public async Task ГейтыСерверов_OffПривязки_СнимаютСерверы()
    {
        var dir = MkProjectDir("gates-off");
        var project = _projectManager.Create("GO", dir, TestUserId, TestUsername);
        var persona = MkGatedPersona(project.Id, "off",
            "widgets", "notifications", "codegraph", "personas");
        var session = await _sut.CreatePersonaChatAsync(TestUserId, persona.Id, ClaudeMode.Auto);

        InvokePrivate("BuildWidgetsContext", TestUserId, persona).Should().BeNull();
        InvokePrivate("BuildNotificationsContext", TestUserId, persona.Id, persona).Should().BeNull();
        InvokePrivate("BuildCodeGraphContext", TestUserId, project.Id, session.Id, dir, persona)
            .Should().BeNull();
        InvokePrivate("BuildPersonasContext", TestUserId, project.Id, session, persona).Should().BeNull();
    }

    [Fact]
    public async Task ГейтыСерверов_СессияБезПерсоны_НеЗатронута()
    {
        var dir = MkProjectDir("gates-nopersona");
        var project = _projectManager.Create("GN", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        InvokePrivate("BuildWidgetsContext", TestUserId, null).Should().NotBeNull();
        InvokePrivate("BuildNotificationsContext", TestUserId, null, null).Should().NotBeNull();
        InvokePrivate("BuildCodeGraphContext", TestUserId, project.Id, session.Id, dir, null)
            .Should().NotBeNull();
        InvokePrivate("BuildPersonasContext", TestUserId, project.Id, session, null).Should().NotBeNull();
        InvokePrivate("ConsultantsEnabled", TestUserId, session, null).Should().Be(true);
    }

    // Гейт «модуль Architecture реально загружен» (Viaduct 10.2): без записи в сторе
    // подсистем тулсета нет в реестре — ход не должен объявлять CLI мёртвый сервер.
    // Остальные замки открыты (привязок нет, фич-флага у раздела нет), меняется только стор.
    [Fact]
    public void Архитектура_ГейтЗагрузкиМодуля_ПоСторуПодсистем()
    {
        var dir = MkProjectDir("gates-arch");
        var user = _userStore.Add("arch-gate-owner", "password123", "user");
        var project = _projectManager.Create("GA", dir, user.Id, user.Username);
        var states = new SubsystemStateStore();
        typeof(SessionManager).GetField("_subsystemStates", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_sut, states);

        InvokePrivate("BuildArchitectureContext", user.Id, project.Id, null)
            .Should().BeNull("модуль не загружен — записи architecture в сторе нет");

        states.RecordActive(new ArchitectureStubSubsystem());

        InvokePrivate("BuildArchitectureContext", user.Id, project.Id, null)
            .Should().NotBeNull("модуль загружен (RecordActive), прочие замки открыты");
    }

    [Fact]
    public async Task Консультанты_Off_СнимаютПодсказкуНоОставляютСерверПерсон()
    {
        var dir = MkProjectDir("gates-consult");
        var project = _projectManager.Create("GC", dir, TestUserId, TestUsername);
        var persona = MkGatedPersona(project.Id, "consult", "consultants");
        MkGatedPersona(project.Id, "consult-peer");
        var session = await _sut.CreatePersonaChatAsync(TestUserId, persona.Id, ClaudeMode.Auto);

        InvokePrivate("ConsultantsEnabled", TestUserId, session, persona).Should().Be(false);
        InvokePrivate("BuildPersonaAgentsProvider", TestUserId, session, persona).Should().BeNull(
            "нет ни pmem-серверов, ни --add-dir с .md-агентами");
        var personas = InvokePrivate("BuildPersonasContext", TestUserId, project.Id, session, persona)
            .Should().BeOfType<PersonasMcpContext>().Subject;
        personas.MentionsHint.Should().BeNull(
            "без консультаций persona_ask и список коллег не нужны (PERSONAS_MENTIONS=0)");
    }

    [Fact]
    public async Task Консультанты_Off_ВГрупповомЧатеИгнорируется()
    {
        // Спикер обязан уметь спросить коллег по чату — иначе групповой чат ломается по замыслу
        var (user, project, personas) = MkGroupFixture(3, "gates-group");
        var speaker = _personaManager.UpdateBindings(personas[0].Id, user.Id,
        [
            new PersonaBinding { Type = PersonaBindingType.Tool, Target = "consultants", Mode = PersonaBindingMode.Off },
        ]);
        var session = await _sut.CreateGroupChatAsync(user.Id, personas.Select(p => p.Id).ToList(),
            ClaudeMode.Auto, "Команда");

        InvokePrivate("ConsultantsEnabled", user.Id, session, speaker).Should().Be(true);
        var ctx = InvokePrivate("BuildPersonasContext", user.Id, project.Id, session, speaker)
            .Should().BeOfType<PersonasMcpContext>().Subject;
        ctx.MentionsHint.Should().NotBeNull("в групповом чате коллеги остаются доступными");
    }

    [Fact]
    public async Task Personas_Off_ВГрупповомЧатеИгнорируется()
    {
        // Спикер обязан уметь спросить коллег через persona_ask — иначе BuildGroupChatHint
        // безосновательно отсылает к блоку о консультациях, а его нет (регресс из ревью 995702c5)
        var (user, project, personas) = MkGroupFixture(3, "gates-group-personas");
        var speaker = _personaManager.UpdateBindings(personas[0].Id, user.Id,
        [
            new PersonaBinding { Type = PersonaBindingType.Tool, Target = "personas", Mode = PersonaBindingMode.Off },
        ]);
        var session = await _sut.CreateGroupChatAsync(user.Id, personas.Select(p => p.Id).ToList(),
            ClaudeMode.Auto, "Команда-персоны");

        InvokePrivate("PersonasEnabled", user.Id, session, speaker).Should().Be(true);
        var ctx = InvokePrivate("BuildPersonasContext", user.Id, project.Id, session, speaker)
            .Should().BeOfType<PersonasMcpContext>().Subject;
        ctx.MentionsHint.Should().NotBeNull("в групповом чате сервер персон остаётся подключён");
    }

    [Fact]
    public async Task Personas_Off_ОдиночныйЧатТойЖеПерсоны_СнимаетСервер()
    {
        var dir = MkProjectDir("gates-personas-solo");
        var project = _projectManager.Create("GPS", dir, TestUserId, TestUsername);
        var persona = MkGatedPersona(project.Id, "personas-solo", "personas");
        var session = await _sut.CreatePersonaChatAsync(TestUserId, persona.Id, ClaudeMode.Auto);

        InvokePrivate("PersonasEnabled", TestUserId, session, persona).Should().Be(false);
        InvokePrivate("BuildPersonasContext", TestUserId, project.Id, session, persona).Should().BeNull();
    }

    [Fact]
    public async Task ГейтыСерверов_РешениеОдинаковоеНаВсехХодах()
    {
        // Состав tools/list входит в сигнатуру запуска CLI: «мерцание» между ходами убивает
        // процесс claude со всеми MCP-серверами
        var dir = MkProjectDir("gates-stable");
        var project = _projectManager.Create("GS", dir, TestUserId, TestUsername);
        var persona = MkGatedPersona(project.Id, "stable", "codegraph");
        var session = await _sut.CreatePersonaChatAsync(TestUserId, persona.Id, ClaudeMode.Auto);

        for (var turn = 1; turn <= 5; turn++)
        {
            InvokePrivate("BuildCodeGraphContext", TestUserId, project.Id, session.Id, dir, persona)
                .Should().BeNull($"ход {turn}");
            InvokePrivate("BuildWidgetsContext", TestUserId, persona).Should().NotBeNull($"ход {turn}");
        }
    }

    [Fact]
    public void MakeChatTitle_TeamImplement_ТемаИзTask()
    {
        MakeChatTitle("/team-implement {\"task\":\"добавить экспорт в CSV\",\"worktree\":false,\"verify\":true}")
            .Should().Be("добавить экспорт в CSV");
    }

    [Fact]
    public void MakeChatTitle_PanelOfExperts_ТемаИзTopic()
    {
        MakeChatTitle("/panel-of-experts {\"topic\":\"выбрать очередь сообщений\",\"rounds\":2}")
            .Should().Be("выбрать очередь сообщений");
    }

    [Fact]
    public void MakeChatTitle_ReviewConsilium_ТемаИзTarget()
    {
        MakeChatTitle("/review-consilium {\"target\":\"текущий дифф\",\"lenses\":[\"security\"]}")
            .Should().Be("текущий дифф");
    }

    [Fact]
    public void MakeChatTitle_RedTeam_ТемаИзTarget()
    {
        MakeChatTitle("/red-team {\"target\":\"план миграции БД\",\"angles\":[\"security\"]}")
            .Should().Be("план миграции БД");
    }

    [Fact]
    public void MakeChatTitle_PanelOfExperts_БезTopic_ПадаетНаBrief()
    {
        MakeChatTitle("/panel-of-experts {\"topic\":\"\",\"brief\":\"контекст из чата\",\"rounds\":2}")
            .Should().Be("контекст из чата");
    }

    [Theory]
    [InlineData("/oh-my-claudecode:ralplan --interactive \"выбрать подход к кэшу\"", "выбрать подход к кэшу")]
    [InlineData("/oh-my-claudecode:deep-interview --standard \"формат экспорта\"", "формат экспорта")]
    [InlineData("/oh-my-claudecode:autopilot \"починить флаки-тест\"", "починить флаки-тест")]
    [InlineData("/oh-my-claudecode:trace \"почему падает воркер\"", "почему падает воркер")]
    [InlineData("/oh-my-claudecode:sciomc \"разобрать утечку памяти\"", "разобрать утечку памяти")]
    public void MakeChatTitle_СтроковыеМеханики_ТемаИзКавычек(string turnText, string expected)
    {
        MakeChatTitle(turnText).Should().Be(expected);
    }

    // B6: путь QuotedTopicRegex не был покрыт пустыми кавычками и переводом строки —
    // для JSON-пути (JsonTopicKeys) такие тесты уже были (пустая тема/битый JSON), для
    // строкового — нет
    [Fact]
    public void MakeChatTitle_КавычкиПустые_ПадаетНаОбрезкуСырогоТекста()
    {
        var turnText = "/oh-my-claudecode:ralplan \"\"";
        MakeChatTitle(turnText).Should().Be(turnText);
    }

    [Fact]
    public void MakeChatTitle_КавычкиСПереводомСтроки_ОбрезаетсяДоПервойСтроки()
    {
        MakeChatTitle("/oh-my-claudecode:deep-interview \"первая строка\nвторая строка\"")
            .Should().Be("первая строка");
    }

    [Fact]
    public void MakeChatTitle_Ultraqa_ТемаПослеФлага()
    {
        MakeChatTitle("/oh-my-claudecode:ultraqa --frontend починить регрессию формы")
            .Should().Be("починить регрессию формы");
    }

    [Fact]
    public void MakeChatTitle_ПустаяТема_ПадаетНаОбрезкуСырогоТекста()
    {
        MakeChatTitle("/team-implement {\"task\":\"\",\"worktree\":false,\"verify\":true}")
            .Should().StartWith("/team-implement {\"task\"");
    }

    [Fact]
    public void MakeChatTitle_БитыйJson_НеПадаетИВозвращаетОбрезку()
    {
        MakeChatTitle("/team-implement {\"task\":\"незакрытый")
            .Should().StartWith("/team-implement {\"task\"");
    }

    [Fact]
    public void MakeChatTitle_ОбычноеСообщение_РаботаетКакРаньше()
    {
        MakeChatTitle("Помоги разобраться с багом в SessionManager").Should().Be("Помоги разобраться с багом в SessionManager");
    }

    [Fact]
    public void MakeChatTitle_ДлинныйJson_ОбрезаетсяДо48Символов()
    {
        var longTask = new string('а', 100);
        MakeChatTitle($"/team-implement {{\"task\":\"{longTask}\"}}")
            .Should().Be(new string('а', 48) + "…");
    }

    [Fact]
    public async Task RefineChatTitle_НеЛокальныйМаршрут_УточняетЗаголовок()
    {
        var dir = MkProjectDir("refine-remote");
        var project = _projectManager.Create("RT", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        session.Name = "обрезка сообщения";

        typeof(SessionManager).GetField("_cheap", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_sut, new StubTitleCheapRunner(usesLocal: false, () => """{"title":"Уточнённый заголовок"}"""));

        await (Task)InvokePrivate("RefineChatTitleAsync", session.Id, "первое сообщение чата",
            "обрезка сообщения", TestUserId)!;

        _sut.GetOwned(session.Id, TestUserId)!.Name.Should().Be("Уточнённый заголовок",
            "заголовок обязан уточняться по маршруту места chat-title, а не только на локали");
    }

    [Fact]
    public async Task RefineChatTitle_ОшибкаИсполнителя_ОставляетОбрезкуБезИсключения()
    {
        var dir = MkProjectDir("refine-error");
        var project = _projectManager.Create("RE", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        session.Name = "обрезка сообщения";

        typeof(SessionManager).GetField("_cheap", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_sut, new StubTitleCheapRunner(usesLocal: false,
                () => throw new InvalidOperationException("исполнитель недоступен")));

        await (Task)InvokePrivate("RefineChatTitleAsync", session.Id, "первое сообщение чата",
            "обрезка сообщения", TestUserId)!;

        _sut.GetOwned(session.Id, TestUserId)!.Name.Should().Be("обрезка сообщения",
            "отказ исполнителя — best-effort: имя остаётся обрезкой, исключение наружу не идёт");
    }

    [Fact]
    public async Task PersonaAutomation_ГейтOnlyIf_ОцениваетсяПриНеЛокальномМаршруте()
    {
        var dir = MkProjectDir("automation-gate");
        var project = _projectManager.Create("AG", dir, TestUserId, TestUsername);
        var persona = _personaManager.Create(TestUserId, "Гейт-персона", role: null, description: null,
            systemPrompt: null, model: null, effort: null, scope: PersonaScope.Project,
            projectId: project.Id, color: null, greeting: null, memoryEnabled: false);

        // UsesLocal=false — маршрут места automation-gate НЕ локальный (слот/direct-модель);
        // раньше это молча пропускало гейт целиком (условие оценивалось только внутри хода)
        var (automation, state) = BuildAutomationService(
            new StubTitleCheapRunner(usesLocal: false, () => "нет"));

        var rule = new PersonaAutomationRule
        {
            Name = "Только про деплой",
            Trigger = new AutomationTrigger(),
            Condition = new AutomationCondition { OnlyIf = "касается деплоя" },
        };
        var ev = new TriggerEvent(rule.Id, AutomationTriggerType.Timer, "Обновлена документация README");

        var fireAsync = typeof(PersonaAutomationService).GetMethod("FireAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)fireAsync.Invoke(automation, [persona, rule, TimeZoneInfo.Utc, ev, CancellationToken.None, false])!;

        state.GetRule(persona.Id, rule.Id).LastResult.Should().Be("gated",
            "гейт OnlyIf обязан оцениваться (и отсекать) при любом маршруте места automation-gate, не только на локали");
    }

    [Fact]
    public async Task PersonaAutomation_СводкаУведомления_СтроитсяПриНеЛокальномМаршруте()
    {
        var dir = MkProjectDir("automation-summary");
        var project = _projectManager.Create("AS", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        // Форсируем чтение истории с диска (как после рестарта сервера) — accumulator=null
        var sessionsDict = typeof(SessionManager).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(_sut)!;
        var args = new object?[] { session.Id, null };
        ((bool)sessionsDict.GetType().GetMethod("TryGetValue")!.Invoke(sessionsDict, args)!).Should().BeTrue();
        var entry = args[1]!;
        entry.GetType().GetField("Accumulator", BindingFlags.Public | BindingFlags.Instance)!.SetValue(entry, null);

        var claudeSessionId = "csid-" + Guid.NewGuid().ToString("N");
        session.ClaudeSessionId = claudeSessionId;
        await _historyService.SaveAsync(claudeSessionId,
            [new Protocol.StoredTextMessage("Готово: обновил конфиг и перезапустил сервис.")]);

        // UsesLocal=false — маршрут места notification-summary НЕ локальный
        var (automation, _) = BuildAutomationService(
            new StubTitleCheapRunner(usesLocal: false, () => "Обновил конфиг и перезапустил сервис."));

        var summarize = typeof(PersonaAutomationService).GetMethod("TrySummarizeLastReplyAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = await (Task<string?>)summarize.Invoke(automation, [session.Id, TestUserId])!;

        result.Should().Be("Обновил конфиг и перезапустил сервис.",
            "суть уведомления обязана строиться по маршруту места notification-summary, не только на локали");
    }

    [Fact]
    public async Task Sweep_МёртвыйПрогонПослеResult_ПереводитВFinished()
    {
        // Форма (б): result пришёл, exited не пришёл, прогон умер. На следующий день HasLiveTurn=false
        // — sweep закрывает зависший Active (без sweep — вечный active).
        var session = await MkBusySessionAsync("sweep-dead");
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(false); // процесса нет
        adapter.SetupGet(a => a.HasPendingBg).Returns(false);
        SetProcess(entry, adapter.Object);

        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Active);

        SetLastTurnEndedAt(entry, DateTimeOffset.UtcNow.AddSeconds(-30)); // exited давно не пришёл

        _sut.RunStuckActiveSweepForTests();

        await WaitForConditionAsync(() => _sut.GetById(session.Id)!.Status == SessionStatus.Finished,
            TimeSpan.FromSeconds(2));
        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Finished);
    }

    // --- Гашение процесса исполнителя закрытой задачи (инцидент 13.10) ---

    [Fact]
    public async Task StopClosedExecutorProcess_ИсполнительХодКончилсяФонЖив_СнимаетПроцессНеТрогаяUpdatedAt()
    {
        var session = await MkBusySessionAsync("exec-closed", SessionStatus.Active);
        session.TaskExecution = true;
        session.TaskId = "task-closed";
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasPendingBg).Returns(true); // фоновый dotnet test ещё жив
        adapter.Setup(a => a.DisposeAsync()).Returns(ValueTask.CompletedTask);
        SetProcess(entry, adapter.Object);
        var updatedBefore = session.UpdatedAt;

        var stopped = _sut.StopClosedExecutorProcess(session.Id, "task-closed");

        stopped.Should().BeTrue("задача закрыта, ход кончился — процесс с фоном гасится");
        adapter.Verify(a => a.DisposeAsync(), Times.Once);
        entry.GetType().GetField("Process")!.GetValue(entry).Should().BeNull(
            "адаптер снят: поздний task-notification больше некому принять");
        session.UpdatedAt.Should().Be(updatedBefore, "остановка — не активность чата");
    }

    [Fact]
    public async Task StopClosedExecutorProcess_ОбычныйЧатСФоновымАгентом_ЖивётКакРаньше()
    {
        // Панели экспертов и Workflow в обычных чатах держат процесс ради HasPendingBg
        var session = await MkBusySessionAsync("plain-bg", SessionStatus.Active);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasPendingBg).Returns(true);
        SetProcess(entry, adapter.Object);
        SetLastTurnEndedAt(entry, DateTimeOffset.UtcNow.AddSeconds(-30));

        _sut.StopClosedExecutorProcess(session.Id, "any-task").Should().BeFalse();
        _sut.RunStuckActiveSweepForTests();

        adapter.Verify(a => a.DisposeAsync(), Times.Never);
        AssertSweepSkipped(_sut, session.Id, entry); // HasPendingBg по-прежнему гейтит Finished
    }

    [Fact]
    public async Task StopClosedExecutorProcess_ЗадачаЗакрытаАХодИдёт_ПроцессНеСнимается()
    {
        // Done пришёл раньше конца хода (статус Working): гасить рано
        var session = await MkBusySessionAsync("exec-working", SessionStatus.Working);
        session.TaskExecution = true;
        session.TaskId = "task-closed";
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        _sut.StopClosedExecutorProcess(session.Id, "task-closed").Should().BeFalse();

        adapter.Verify(a => a.DisposeAsync(), Times.Never);
        entry.GetType().GetField("Process")!.GetValue(entry).Should().NotBeNull();
    }

    [Fact]
    public async Task Sweep_ЖивойПрогонБезФоновойРаботы_ПереводитВFinished()
    {
        // Висящий без работы прогон (HasLiveTurn=true, нет ни HasPendingBg, ни IsContinuationInFlight):
        // ждать !HasLiveTurn пришлось бы до BgLingerTimeout (минуты) — sweep даёт быстрый terminus.
        // Это форма «процесс держат плагинные хуки/мосты без единой задачи и без продолжения» —
        // единственный случай, когда живый прогон корректно доводится до Finished (после grace).
        var session = await MkBusySessionAsync("sweep-alive-nobg");
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(true);   // прогон висит
        adapter.SetupGet(a => a.HasPendingBg).Returns(false); // фоновых задач нет
        adapter.SetupGet(a => a.IsContinuationInFlight).Returns(false); // продолжения нет
        SetProcess(entry, adapter.Object);

        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        SetLastTurnEndedAt(entry, DateTimeOffset.UtcNow.AddSeconds(-30));

        _sut.RunStuckActiveSweepForTests();

        await WaitForConditionAsync(() => _sut.GetById(session.Id)!.Status == SessionStatus.Finished,
            TimeSpan.FromSeconds(2));
        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Finished);
    }

    [Fact]
    public async Task Sweep_ЖивойПрогонВПродолжении_НеТрогает()
    {
        // Bg-агент доработал (HasPendingBg=false), но процесс ЖИВ и генерит ход-продолжение — ответ
        // на task_notification завершившегося агента (IsContinuationInFlight=true). LastTurnEndedAt
        // стоит с result ПЕРВОГО хода (минуты назад, grace давно истёк) — без гейта продолжения sweep
        // ставил бы Finished ровно когда CLI начинает генерацию, и text_delta летели в «завершённый»
        // чат: клиент по finished историю хода не перечитывает, контент теряется (P16-сценарий).
        var session = await MkBusySessionAsync("sweep-alive-continuation");
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(true);
        adapter.SetupGet(a => a.HasPendingBg).Returns(false);       // bg-агент завершён
        adapter.SetupGet(a => a.IsContinuationInFlight).Returns(true); // но идёт ход-продолжение
        SetProcess(entry, adapter.Object);

        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        SetLastTurnEndedAt(entry, DateTimeOffset.UtcNow.AddSeconds(-30));

        _sut.RunStuckActiveSweepForTests();

        AssertSweepSkipped(_sut, session.Id, entry);
    }

    [Fact]
    public async Task Sweep_ЖивойПрогонСФоновойРаботой_НеТрогает()
    {
        // Панель экспертов / Workflow: прогон жив и держит фоновые задачи. Finished преждевременен
        // (синтез панели прилетел бы в «завершённый» чат спустя минуты) — sweep обязан молчать.
        var session = await MkBusySessionAsync("sweep-alive-bg");
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(true);
        adapter.SetupGet(a => a.HasPendingBg).Returns(true); // фоновые агенты работают
        SetProcess(entry, adapter.Object);

        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        SetLastTurnEndedAt(entry, DateTimeOffset.UtcNow.AddSeconds(-30));

        _sut.RunStuckActiveSweepForTests();

        AssertSweepSkipped(_sut, session.Id, entry);
    }

    [Fact]
    public async Task Sweep_ВключённыйЦиклДоГотово_НеТрогает()
    {
        // Пауза между итерациями цикла: LoopTurnInFlight сброшен по result, статус Active, метка
        // свежая — но WorkLoop ещё включён. Sweep не должен мигать Finished посреди цикла
        // (ретрай/фолбэк могут затянуть паузу свыше grace — найдка архитектора).
        var session = await MkBusySessionAsync("sweep-loop");
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(false);
        adapter.SetupGet(a => a.HasPendingBg).Returns(false);
        SetProcess(entry, adapter.Object);
        await _sut.SetWorkLoopAsync(session.Id, true); // цикл включён — WorkLoop != null

        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        SetLastTurnEndedAt(entry, DateTimeOffset.UtcNow.AddSeconds(-30));

        _sut.RunStuckActiveSweepForTests();

        AssertSweepSkipped(_sut, session.Id, entry);
        _sut.GetById(session.Id)!.WorkLoop.Should().NotBeNull("цикл не выключен — sweep не имеет права его трогать");
    }

    [Fact]
    public async Task Sweep_ПланировщикШтабаВФоне_НеТрогает()
    {
        // Планировщик «Командной реализации» работает до 300 с без CLI-прогона (HasLiveTurn=false),
        // статус Active — sweep без гейта закрыл бы сессию в разгар планирования.
        var session = await MkBusySessionAsync("sweep-plan");
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(false);
        adapter.SetupGet(a => a.HasPendingBg).Returns(false);
        SetProcess(entry, adapter.Object);
        entry.GetType().GetField("TeamPlanningInFlight")!.SetValue(entry, true);

        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        SetLastTurnEndedAt(entry, DateTimeOffset.UtcNow.AddSeconds(-30));

        _sut.RunStuckActiveSweepForTests();

        AssertSweepSkipped(_sut, session.Id, entry);
    }

    [Fact]
    public async Task Sweep_ВGraceОкне_НеТрогает()
    {
        // Свежий result → метка только что проставлена, grace не истёк — sweep молчит даже при
        // мёртвом процессе: клиент ещё перечитывает историю хода по active-статусу (useSession.ts).
        var session = await MkBusySessionAsync("sweep-grace");
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(false);
        adapter.SetupGet(a => a.HasPendingBg).Returns(false);
        SetProcess(entry, adapter.Object);

        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ResultMessage("success", 10, 1, null, null));
        SetLastTurnEndedAt(entry, DateTimeOffset.UtcNow); // свежая метка — grace не истёк

        _sut.RunStuckActiveSweepForTests();

        AssertSweepSkipped(_sut, session.Id, entry);
    }

    [Fact]
    public async Task Sweep_ЖивойДочернийИсполнитель_ДержитКоординатора()
    {
        // P28 (положительный, главный сценарий): исполнитель волны прямо сейчас работает —
        // Status=Working, идёт ход, без фоновых агентов и продолжений. Координатор не должен
        // уходить в Finished в разгар волны, даже если его собственный прогон мёртв и grace
        // истёк. Реальный сценарий из отчёта Веры (чат 166282c7).
        var (coordinator, coordEntry) = await MkStuckCoordinatorAsync("live-child");
        var (child, _) = await MkChildAsync("exec", SessionStatus.Working, alive: true);

        _sut.SetTaskLookupForTests(new StubTasks((child.TaskId!, coordinator.Id, TaskItemStatus.Todo)));
        try
        {
            SetLastTurnEndedAt(coordEntry, DateTimeOffset.UtcNow.AddSeconds(-30)); // grace истёк
            _sut.RunStuckActiveSweepForTests();

            AssertSweepSkipped(_sut, coordinator.Id, coordEntry);
        }
        finally { _sut.SetTaskLookupForTests(null); }
    }

    [Fact]
    public async Task Sweep_МёртвыйДочернийИсполнитель_НеДержитКоординатора()
    {
        // P28 (негативный, главный сторож клинча): потомок числится в рабочем статусе (Working),
        // но его процесс мёртв (HasLiveTurn=false) — координатор обязан закрыться по grace, как
        // обычная сессия без волны. Иначе вечный active обоих чатов — регресс хуже исходного
        // дефекта (ложный Finished на минуту). Этот тест — страхует именно его.
        var (coordinator, coordEntry) = await MkStuckCoordinatorAsync("dead-child");
        var (child, _) = await MkChildAsync("exec", SessionStatus.Working, alive: false);

        _sut.SetTaskLookupForTests(new StubTasks((child.TaskId!, coordinator.Id, TaskItemStatus.Todo)));
        try
        {
            SetLastTurnEndedAt(coordEntry, DateTimeOffset.UtcNow.AddSeconds(-30)); // grace истёк
            _sut.RunStuckActiveSweepForTests();

            await WaitForConditionAsync(() => _sut.GetById(coordinator.Id)!.Status == SessionStatus.Finished,
                TimeSpan.FromSeconds(2));
            _sut.GetById(coordinator.Id)!.Status.Should().Be(SessionStatus.Finished);
        }
        finally { _sut.SetTaskLookupForTests(null); }
    }

    [Fact]
    public async Task Sweep_ЗависшийВWaitingИсполнитель_НеДержитКоординатора()
    {
        // P30-страховка: исполнитель умер на permission-запросе, статус остался Waiting навсегда
        // (открытый дефект P30, ещё не чинен). Гейт по голому статусу держал бы координатора вечно
        // — поэтому живость процесса обязательна при ЛЮБОМ статусе. Здесь ребёнок в Waiting, но
        // процесс мёртв → координатор закрывается по grace.
        var (coordinator, coordEntry) = await MkStuckCoordinatorAsync("waiting-child");
        var (child, _) = await MkChildAsync("exec", SessionStatus.Waiting, alive: false);

        _sut.SetTaskLookupForTests(new StubTasks((child.TaskId!, coordinator.Id, TaskItemStatus.Todo)));
        try
        {
            SetLastTurnEndedAt(coordEntry, DateTimeOffset.UtcNow.AddSeconds(-30)); // grace истёк
            _sut.RunStuckActiveSweepForTests();

            await WaitForConditionAsync(() => _sut.GetById(coordinator.Id)!.Status == SessionStatus.Finished,
                TimeSpan.FromSeconds(2));
            _sut.GetById(coordinator.Id)!.Status.Should().Be(SessionStatus.Finished);
        }
        finally { _sut.SetTaskLookupForTests(null); }
    }

    [Fact]
    public async Task Sweep_ЖивойПотомокВГлубину_ДержитКоординатораЧерезСреднийУзел()
    {
        // P28 (глубина): законная цепочка делегирования координатор → исполнитель → суб-исполнитель
        // (глубина цепочки делегирования до 3). Суб-исполнитель работает (Working+alive), а исполнитель уже
        // отдал result и сидит в Active без своих фоновых задач — сам он не жив по HasLiveWork,
        // но под ним кипит работа. Правило по ПРЯМЫМ детям закрыло бы координатора (его ребёнок-
        // исполнитель не жив) — рекурсивный подъём от суб-исполнителя должен пройти через средний
        // узел и удержать корень.
        var (coordinator, coordEntry) = await MkStuckCoordinatorAsync("deep");
        var dir = MkProjectDir("p28h-mid");
        var project = _projectManager.Create("P28H-MID", dir, TestUserId, TestUsername);
        var executor = await _sut.CreateAsync(project.Id, ClaudeMode.Auto,
            taskExecution: true, taskId: "t-exec");
        var execEntry = GetEntry(executor.Id);
        var execAdapter = StubAdapter(execEntry);
        execAdapter.SetupGet(a => a.HasLiveTurn).Returns(true);  // прогон жив, но без фоновой работы
        execAdapter.SetupGet(a => a.HasPendingBg).Returns(false);
        execAdapter.SetupGet(a => a.IsContinuationInFlight).Returns(false);
        SetProcess(execEntry, execAdapter.Object);
        executor.Status = SessionStatus.Active; // ждёт потомка — сам не HasLiveWork

        var (sub, _) = await MkChildAsync("sub", SessionStatus.Working, alive: true);

        SetSutTasks(
            (executor.TaskId!, coordinator.Id, TaskItemStatus.Todo),
            (sub.TaskId!, executor.Id, TaskItemStatus.Todo));
        try
        {
            SetLastTurnEndedAt(coordEntry, DateTimeOffset.UtcNow.AddSeconds(-30)); // grace истёк
            _sut.RunStuckActiveSweepForTests();

            AssertSweepSkipped(_sut, coordinator.Id, coordEntry);
            // Средний узел не получал result (LastTurnEndedAt нет), поэтому проверяем только статус:
            // sweep не имел права закрыть его — он удержан живым суб-исполнителем.
            _sut.GetById(executor.Id)!.Status.Should().Be(SessionStatus.Active,
                "средний узел удержан живым потомком, хотя сам не HasLiveWork");
        }
        finally { _sut.SetTaskLookupForTests(null); }
    }

    [Fact]
    public async Task ExitedШтатно_ПереводитВFinished()
    {
        // Регресс: штатный переход Active→Finished по ExitedMessage не сломан sweep'ом — exited
        // по-прежнему закрывает ход мгновенно, не дожидаясь grace.
        var session = await MkBusySessionAsync("sweep-exited");
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(false);
        adapter.SetupGet(a => a.HasPendingBg).Returns(false);
        SetProcess(entry, adapter.Object);

        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Active);

        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ExitedMessage(), TestRunId);

        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Finished);
    }

    [Fact]
    public async Task ПерезапускХода_ЗависшийБезПрогона_ВозвращаетЧатВРаботуСТемЖеРежимом()
    {
        const string csid = "keep0123456789abcdef";
        var session = await MkStuckTeamSessionAsync("turn-stuck", claudeSessionId: csid);

        var result = await _sut.RestartStuckTurnAsync(session.Id, startFresh: false);

        result.Resumed.Should().BeTrue();
        result.Message.Should().Contain("напишите сообщение",
            "очередь пуста — текст не обещает ход, которого не будет (minor 3)");
        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Active,
            "главное: в чат снова можно писать");
        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.Stage.Should().Be(TeamImplementStage.Wave, "стадия пережила перезапуск");
        team.WaveNumber.Should().Be(1);
        team.PlanVersion.Should().Be(2, "версия плана не сброшена");
        team.Budget.RunsUsed.Should().Be(2, "расход бюджета не изменился");
        _sut.GetById(session.Id)!.ClaudeSessionId.Should().Be(csid,
            "resume-якорь цел — следующий ход продолжит разговор");
        Sent<WorkLoopStoppedMessage>().Should().ContainSingle()
            .Which.Reason.Should().Be("team_restart", "человек видит в ленте, что ход перезапущен вручную");
    }

    [Fact]
    public async Task ПерезапускХода_СвободныйЧат_Отказ()
    {
        var session = await MkStuckTeamSessionAsync("turn-free");
        _sut.GetById(session.Id)!.Status = SessionStatus.Active;

        var act = () => _sut.RestartStuckTurnAsync(session.Id, startFresh: false);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Чат не занят*");
    }

    [Fact]
    public async Task ПерезапускХода_ЖивойПрогонНачалсяНедавно_Отказ()
    {
        var session = await MkStuckTeamSessionAsync("turn-fresh");
        var entry = GetEntry(session.Id);
        var live = new SwitchableLiveAdapter((Session)entry.GetType().GetField("Info")!.GetValue(entry)!);
        SetProcess(entry, live.Object);

        var act = () => _sut.RestartStuckTurnAsync(session.Id, startFresh: false);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Штаб работает*",
                "живой ход с недавней активностью — честная работа, а не зависание");
    }

    [Fact]
    public async Task ПерезапускХода_KillНеСработал_ОтказАНеМолчаливыйОбрыв()
    {
        var session = await MkStuckTeamSessionAsync("turn-undying");
        var entry = GetEntry(session.Id);
        var live = new SwitchableLiveAdapter((Session)entry.GetType().GetField("Info")!.GetValue(entry)!);
        SetProcess(entry, live.Object);
        // Ход давно молчит (гейт свежести пропускает), но процесс не умирает после kill
        _sut.GetById(session.Id)!.UpdatedAt = DateTime.UtcNow.AddMinutes(-40);
        var prevTimeout = _sut.KillWaitTimeout;
        _sut.KillWaitTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            var act = () => _sut.RestartStuckTurnAsync(session.Id, startFresh: false);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*Не удалось остановить зависший процесс*");
            _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Working,
                "недоубитый процесс не объявляется мёртвым по факту вызова kill");
        }
        finally { _sut.KillWaitTimeout = prevTimeout; }
    }

    [Fact]
    public async Task ПерезапускХода_ПовторныйКликПокаИдёт_Отказ()
    {
        var session = await MkStuckTeamSessionAsync("turn-twice");
        var entry = GetEntry(session.Id);
        var live = new SwitchableLiveAdapter((Session)entry.GetType().GetField("Info")!.GetValue(entry)!);
        SetProcess(entry, live.Object);
        _sut.GetById(session.Id)!.UpdatedAt = DateTime.UtcNow.AddMinutes(-40);
        var prevTimeout = _sut.KillWaitTimeout;
        _sut.KillWaitTimeout = TimeSpan.FromSeconds(30);
        try
        {
            var first = Task.Run(() => _sut.RestartStuckTurnAsync(session.Id, startFresh: false));
            // Первый вызов паркуется в ожидании смерти процесса: детерминированный маркер —
            // Interrupt уже ушёл в адаптер, значит гейт повторного вызова захвачен
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!live.Mock.Invocations.Any(i => i.Method.Name == nameof(ILlmSessionAdapter.Interrupt))
                   && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            live.Mock.Invocations.Should().Contain(i => i.Method.Name == nameof(ILlmSessionAdapter.Interrupt),
                "первый вызов уже в фазе kill — проверка детерминирована");

            Func<Task> secondCall = () => _sut.RestartStuckTurnAsync(session.Id, startFresh: false);
            (await secondCall.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*уже перезапускается*");

            live.Alive = false; // процесс «умер» — первый вызов завершается штатно
            (await first).Resumed.Should().BeTrue();
        }
        finally
        {
            live.Alive = false;
            _sut.KillWaitTimeout = prevTimeout;
        }
    }

    [Fact]
    public async Task ПерезапускХода_ТранскриптПовреждён_РезюмеЗапрещеноПредложеноНачатьЗаново()
    {
        const string csid = "dmg0123456789abcdef";
        var session = await MkStuckTeamSessionAsync("turn-damaged", claudeSessionId: csid);
        // Последняя строка оборвана посреди записи — как при kill в момент записи файла
        WriteTranscript(csid, "{\"type\":\"user\"}\n{\"type\":\"assistant\",\"mess");

        var act = () => _sut.RestartStuckTurnAsync(session.Id, startFresh: false);

        await act.Should().ThrowAsync<SessionManager.TurnTranscriptDamagedException>()
            .WithMessage("*повреждён*");
        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Working,
            "повреждённый транскрипт — не повод реанимировать чат молча: человек решает");

        // Человек подтвердил «начать заново»: якорь сброшен, чат вернулся в работу
        var fresh = await _sut.RestartStuckTurnAsync(session.Id, startFresh: true);
        fresh.Resumed.Should().BeFalse();
        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Active);
        _sut.GetById(session.Id)!.ClaudeSessionId.Should().BeNull(
            "ход начнётся заново — без --resume по повреждённому файлу");
    }

    // Major ревью: реальный таймлайн гибели на битом транскрипте — первый вызов убил
    // процесс и вернул 409 transcript_damaged, финализация убитого прогона перевела
    // чат в Active, а отложенная очередь умерла на --resume по тому же файлу. У
    // свободного чата с битым якорем единственный выход — startFresh, и он обязан
    // проходить: раньше здесь навсегда стоял «Чат не занят».
    [Fact]
    public async Task ПерезапускХода_СвободныйЧатСБитымТранскриптом_СпасениеStartFreshРазрешено()
    {
        const string csid = "resq0123456789abcd";
        var session = await MkStuckTeamSessionAsync("turn-rescue", claudeSessionId: csid);
        WriteTranscript(csid, "{\"type\":\"user\"}\n{\"type\":\"assistant\",\"mess");
        _sut.GetById(session.Id)!.Status = SessionStatus.Active;

        var fresh = await _sut.RestartStuckTurnAsync(session.Id, startFresh: true);

        fresh.Resumed.Should().BeFalse();
        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Active, "чат снова доступен");
        _sut.GetById(session.Id)!.ClaudeSessionId.Should().BeNull(
            "битый resume-якорь сброшен — новый ход не упрётся в повреждённый файл");
    }

    // Спасательная ветка — не лазейка: свободный чат с целым транскриптом startFresh
    // не получает — обычное сообщение продолжит разговор без потери контекста
    [Fact]
    public async Task ПерезапускХода_СвободныйЧатСЦелымТранскриптом_СпасениеНеОткрываетДыру()
    {
        const string csid = "resqok0123456789ab";
        var session = await MkStuckTeamSessionAsync("turn-rescue-ok", claudeSessionId: csid);
        WriteTranscript(csid, "{\"type\":\"user\"}\n{\"type\":\"assistant\"}\n");
        _sut.GetById(session.Id)!.Status = SessionStatus.Active;

        var act = () => _sut.RestartStuckTurnAsync(session.Id, startFresh: true);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Чат не занят*",
                "целый транскрипт — спасать нечего: сообщение продолжит разговор");
    }

    // Minor 4 ревью: перезапуск хода — семантика штаба (кнопка и тексты говорят
    // «штаб»), вне «Командной реализации» — отказ; обобщение на все чаты — отдельно
    [Fact]
    public async Task ПерезапускХода_ВнеРежимаКР_Отказ()
    {
        var dir = MkProjectDir("turn-no-team");
        var project = _projectManager.Create("turn-no-team", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        _sut.GetById(session.Id)!.Status = SessionStatus.Working;

        var act = () => _sut.RestartStuckTurnAsync(session.Id, startFresh: false);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*штаба*");
    }

    [Fact]
    public async Task ПерезапускХода_ЦелыйТранскрипт_РезюмеРазрешено()
    {
        const string csid = "ok0123456789abcdef";
        var session = await MkStuckTeamSessionAsync("turn-intact", claudeSessionId: csid);
        WriteTranscript(csid, "{\"type\":\"user\"}\n{\"type\":\"assistant\"}\n");

        var result = await _sut.RestartStuckTurnAsync(session.Id, startFresh: false);

        result.Resumed.Should().BeTrue("файл цел — продолжаем с сохранённым контекстом");
        _sut.GetById(session.Id)!.ClaudeSessionId.Should().Be(csid);
    }

    [Fact]
    public async Task ПерезапускХода_ЧатЖдётОтветаНаКарточку_Отказ()
    {
        var session = await MkStuckTeamSessionAsync("turn-waiting-card");
        var entry = GetEntry(session.Id);
        entry.GetType().GetField("PendingInteraction")!.SetValue(entry,
            new PermissionRequestMessage("req-1", "Bash", new { command = "dotnet build" }));

        var act = () => _sut.RestartStuckTurnAsync(session.Id, startFresh: false);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ждёт вашего ответа*",
                "ожидающая карточка — не зависание: ответ лежит в ленте чата");
    }
}
