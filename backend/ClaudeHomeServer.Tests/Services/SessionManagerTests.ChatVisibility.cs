using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// S2d-7a: тумблер видимости чата (SessionManager.SetChatVisibility). Проверка «сообщений нет» и
// установка PrivateToUserId — атомарно под VisibilityLock, под которым первое сообщение чата
// «занимает» его (ClaimFirstMessage). Адресация событий читается в момент рассылки
// (ToChatAudience), поэтому исход гонки отправки и тумблера ровно один из двух.
public partial class SessionManagerTests
{
    private async Task<Session> SharedChatAsync(string suffix, string? createdBy = TestUserId, bool shared = true)
    {
        var (session, _) = await RunChatAsync("vis-" + suffix);
        session.CreatedByUserId = createdBy;
        if (shared)
            _projectManager.GetById(session.ProjectId!)!.Members =
                [new ProjectMember { UserId = Участник, Role = ProjectRole.Editor, AddedBy = TestUserId }];
        _broadcaster.Inner.Clear();
        return session;
    }

    private Lock VisibilityLockOf(string sessionId)
    {
        var entry = GetEntry(sessionId);
        return (Lock)entry.GetType().GetField("VisibilityLock")!.GetValue(entry)!;
    }

    private int ProjectEventsOf(string sessionId) =>
        _broadcaster.Inner.Project.Count(p => p.Message.SessionId == sessionId);

    [Fact]
    public async Task Видимость_АвторОбщегоПроекта_ПриватныйИОбратно_ExcludeFromDossiersОстаётсяTrue()
    {
        var session = await SharedChatAsync("toggle");

        var toPrivate = _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true);
        toPrivate.Outcome.Should().Be(ChatVisibilityOutcome.Ok);
        session.PrivateToUserId.Should().Be(TestUserId);
        session.ExcludeFromDossiers.Should().BeTrue("приватный чат не снимается в «Историю решений»");

        var toShared = _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: false);
        toShared.Outcome.Should().Be(ChatVisibilityOutcome.Ok);
        session.PrivateToUserId.Should().BeNull();
        session.ExcludeFromDossiers.Should().BeTrue("прошлое приватного периода не раскрываем");
    }

    [Fact]
    public async Task Видимость_НеАвтор_Отказ_ПриватностьНеСтавится()
    {
        var session = await SharedChatAsync("notauthor", createdBy: Участник);

        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome
            .Should().Be(ChatVisibilityOutcome.NotAuthor);
        session.PrivateToUserId.Should().BeNull();
        // Чат без зафиксированного автора (записи до появления поля) — тоже не чей-то
        session.CreatedByUserId = null;
        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome
            .Should().Be(ChatVisibilityOutcome.NotAuthor);
    }

    [Fact]
    public async Task Видимость_ЛичныйПроект_NotSharedProject()
    {
        var session = await SharedChatAsync("personal", shared: false);

        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome
            .Should().Be(ChatVisibilityOutcome.NotSharedProject);
        session.PrivateToUserId.Should().BeNull();
    }

    [Fact]
    public async Task Видимость_ЧатСоСообщением_HasMessages_ПослеОбщегоСнятияТожеHasMessages()
    {
        var session = await SharedChatAsync("hasmsg");

        await _sut.SendMessageAsync(session.Id, "первое", [], senderUserId: TestUserId);
        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome
            .Should().Be(ChatVisibilityOutcome.HasMessages);
        session.PrivateToUserId.Should().BeNull();

        // false всегда можно, а вернуть приватность после сообщений — уже нельзя
        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: false).Outcome.Should().Be(ChatVisibilityOutcome.Ok);
        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome
            .Should().Be(ChatVisibilityOutcome.HasMessages);
    }

    [Fact]
    public async Task Видимость_ПриватныйЧатССообщением_ПовторныйTrue_Идемпотентен()
    {
        var session = await SharedChatAsync("idem");
        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome.Should().Be(ChatVisibilityOutcome.Ok);
        await _sut.SendMessageAsync(session.Id, "в приватном", [], senderUserId: TestUserId);

        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome
            .Should().Be(ChatVisibilityOutcome.Ok, "уже приватный чат остаётся приватным, а не получает 409");
        session.PrivateToUserId.Should().Be(TestUserId);
    }

    // Гонка, порядок «тумблер первым» (детерминированно): пока тумблер держит замок видимости,
    // отправка не может пройти отметку первого сообщения — значит оба пути под ОДНИМ замком.
    // Сообщение уходит уже по приватной адресации: группе проекта — ничего, автору — событие.
    [Fact]
    public async Task ГонкаТумблерПервым_ОтправкаЖдётЗамок_СообщениеНеУходитВГруппуПроекта()
    {
        var session = await SharedChatAsync("race-toggle");
        var lk = VisibilityLockOf(session.Id);
        Task send;
        ChatVisibilityOutcome toggled;

        lock (lk)
        {
            send = Task.Run(() => _sut.SendMessageAsync(session.Id, "из соседней вкладки", [], senderUserId: TestUserId));
            Thread.Sleep(400);
            send.IsCompleted.Should().BeFalse("отправка обязана ждать тот же замок, под которым ставится приватность");
            ProjectEventsOf(session.Id).Should().Be(0);
            toggled = _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome;
        }
        await send.WaitAsync(TimeSpan.FromSeconds(10));

        toggled.Should().Be(ChatVisibilityOutcome.Ok);
        ProjectEventsOf(session.Id).Should().Be(0, "приватный чат: группе проекта события не адресуются");
        _broadcaster.Inner.Owner.Should().Contain(o => o.OwnerId == TestUserId && o.Message.SessionId == session.Id,
            "автор получает события своего приватного чата");
    }

    // Гонка, порядок «отправка первой»: сообщение уже принято (замок видимости отпущен после
    // отметки) — тумблер обязан отказать, приватность не ставится.
    [Fact]
    public async Task ГонкаОтправкаПервой_Тумблер409_ПриватностьНеСтавится()
    {
        var session = await SharedChatAsync("race-send");

        await _sut.SendMessageAsync(session.Id, "сначала сообщение", [], senderUserId: TestUserId);

        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome
            .Should().Be(ChatVisibilityOutcome.HasMessages);
        session.PrivateToUserId.Should().BeNull();
        ProjectEventsOf(session.Id).Should().BeGreaterThan(0, "общий чат: событие хода ушло группе проекта");
    }

    // Параллельные send и visibility{private:true}: исход ровно один из двух —
    // либо тумблер 409 (и сообщение вышло группе проекта), либо приватность встала и в группу
    // проекта по этому чату не ушло ничего.
    [Fact]
    public async Task ГонкаПараллельно_ИсходРовноОдин_БезЧастичногоСостояния()
    {
        for (var i = 0; i < 25; i++)
        {
            var session = await SharedChatAsync("race-par-" + i);
            var id = session.Id;
            using var go = new ManualResetEventSlim(false);

            var send = Task.Run(async () => { go.Wait(); await _sut.SendMessageAsync(id, "гонка " + i, [], senderUserId: TestUserId); });
            var toggle = Task.Run(() => { go.Wait(); return _sut.SetChatVisibility(id, TestUserId, makePrivate: true).Outcome; });
            go.Set();
            await send.WaitAsync(TimeSpan.FromSeconds(10));
            var outcome = await toggle.WaitAsync(TimeSpan.FromSeconds(10));

            if (outcome == ChatVisibilityOutcome.Ok)
            {
                session.PrivateToUserId.Should().Be(TestUserId, $"итерация {i}");
                ProjectEventsOf(id).Should().Be(0, $"итерация {i}: приватный чат с сообщением, разосланным в группу проекта");
            }
            else
            {
                outcome.Should().Be(ChatVisibilityOutcome.HasMessages, $"итерация {i}");
                session.PrivateToUserId.Should().BeNull($"итерация {i}");
            }
        }
    }
}
