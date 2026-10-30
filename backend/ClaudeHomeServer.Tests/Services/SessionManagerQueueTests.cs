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

public class SessionManagerQueueTests : SessionManagerTestBase
{
    [Theory]
    [InlineData(SessionStatus.Working)]
    [InlineData(SessionStatus.Waiting)]
    public async Task SendMessageAndWait_ЗанятаяСессия_СообщениеВОчередьАНеОтказ(SessionStatus status)
    {
        // Раньше сюда прилетал Busy → 409, и сообщение агента терялось
        var session = await MkBusySessionAsync("busy" + status, status);

        var result = await _sut.SendMessageAndWaitAsync(session.Id, "привет из другого чата",
            TimeSpan.FromSeconds(5));

        result.Should().BeOfType<SendAndWaitResult.Queued>()
            .Which.Position.Should().Be(1);
        _sut.GetPending(session.Id).Should().ContainSingle()
            .Which.Text.Should().Be("привет из другого чата");
    }

    [Fact]
    public async Task SendMessageAndWait_ОдинаковыйТекстОтТогоЖеОтправителя_НеДублируется()
    {
        // Прежний контракт chats_send советовал ретраить при отказе — наивный агент
        // насыпал бы очередь копиями одного сообщения
        var session = await MkBusySessionAsync("dup");
        await _sut.SendMessageAndWaitAsync(session.Id, "повтор", TimeSpan.Zero, senderPersonaId: "p-1");

        var again = await _sut.SendMessageAndWaitAsync(session.Id, "повтор", TimeSpan.Zero, senderPersonaId: "p-1");

        again.Should().BeOfType<SendAndWaitResult.Queued>().Which.Duplicate.Should().BeTrue();
        _sut.GetPending(session.Id).Should().HaveCount(1);
    }

    [Fact]
    public async Task SendMessageAndWait_ТотЖеТекстОтДругогоОтправителя_ЭтоРазныеСообщения()
    {
        var session = await MkBusySessionAsync("dup2");
        await _sut.SendMessageAndWaitAsync(session.Id, "готово?", TimeSpan.Zero, senderPersonaId: "p-1");

        await _sut.SendMessageAndWaitAsync(session.Id, "готово?", TimeSpan.Zero, senderPersonaId: "p-2");

        _sut.GetPending(session.Id).Should().HaveCount(2);
    }

    [Fact]
    public async Task SendMessageAndWait_ПереполненнаяОчередь_Отклоняет()
    {
        var session = await MkBusySessionAsync("full");
        for (var i = 0; i < 10; i++)
            await _sut.SendMessageAndWaitAsync(session.Id, $"сообщение {i}", TimeSpan.Zero);

        var overflow = await _sut.SendMessageAndWaitAsync(session.Id, "лишнее", TimeSpan.Zero);

        overflow.Should().BeOfType<SendAndWaitResult.QueueFull>().Which.Limit.Should().Be(10);
        _sut.GetPending(session.Id).Should().HaveCount(10, "лишнее не должно вытеснять принятое");
    }

    [Fact]
    public async Task SendMessageAndWait_ОчередьХранитИсточникИОтправителя()
    {
        var session = await MkBusySessionAsync("meta");

        await _sut.SendMessageAndWaitAsync(session.Id, "из соседнего проекта", TimeSpan.Zero,
            senderPersonaId: "p-9", senderOrigin: "Проект Альфа");

        var queued = _sut.GetPending(session.Id).Should().ContainSingle().Subject;
        queued.SenderPersonaId.Should().Be("p-9");
        queued.SenderOrigin.Should().Be("Проект Альфа");
        queued.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CancelPending_СнимаетСообщениеИзОчереди()
    {
        var session = await MkBusySessionAsync("cancel");
        await _sut.SendMessageAndWaitAsync(session.Id, "первое", TimeSpan.Zero);
        await _sut.SendMessageAndWaitAsync(session.Id, "второе", TimeSpan.Zero);
        var first = _sut.GetPending(session.Id)[0];

        (await _sut.CancelPendingAsync(session.Id, first.Id)).Should().BeTrue();

        _sut.GetPending(session.Id).Should().ContainSingle().Which.Text.Should().Be("второе");
        (await _sut.CancelPendingAsync(session.Id, first.Id)).Should().BeFalse("повторная отмена — уже нечего снимать");
    }

    [Fact]
    public async Task Interrupt_ЗамораживаетОчередьНеЧистит()
    {
        // «Стоп» замораживает очередь: агентское сообщение остаётся ждать возобновления,
        // а не вычищается (как было раньше) — иначе сразу после прерывания хлынул бы ход
        var session = await MkBusySessionAsync("interrupt");
        SetProcess(GetEntry(session.Id), StubAdapter(GetEntry(session.Id)).Object);
        await _sut.SendMessageAndWaitAsync(session.Id, "не доставлять", TimeSpan.Zero);

        _sut.Interrupt(session.Id);

        _sut.GetPending(session.Id).Should().ContainSingle()
            .Which.Text.Should().Be("не доставлять");
    }

    [Fact]
    public async Task Interrupt_Человеком_ПишетОтметкуВИсторию()
    {
        var (session, adapter) = await MkRunningTurnAsync("stop-marker");

        _sut.Interrupt(session.Id);

        adapter.Verify(a => a.Interrupt(), Times.Once());
        var history = await _sut.GetHistoryAsync(session.Id);
        history.Select(m => m.GetType()).Should().Equal(
            typeof(StoredUserMessage), typeof(StoredTextMessage), typeof(StoredInterruptedMessage));
    }

    [Fact]
    public async Task InterruptTurn_Штабом_ОтметкуНеПишет()
    {
        // Штаб снимает исполнителя той же механикой, но это не «Стоп» человека
        var (session, adapter) = await MkRunningTurnAsync("staff-stop");

        ((ITeamTurnIntake)_sut).InterruptTurn(session.Id);

        adapter.Verify(a => a.Interrupt(), Times.Once());
        (await _sut.GetHistoryAsync(session.Id)).OfType<StoredInterruptedMessage>().Should().BeEmpty();
    }

    [Fact]
    public async Task ОбрывХодаБезСтопа_ОтметкуНеПишет()
    {
        // Падение процесса: ошибка и голый exited — отметки «остановлен пользователем» нет
        var (session, _) = await MkRunningTurnAsync("crash");
        var acc = GetAccumulator(GetEntry(session.Id));

        await InvokeOnMessageAsync(session.Id, acc, new ErrorMessage("процесс упал"), TestRunId);
        await InvokeOnMessageAsync(session.Id, acc, new ExitedMessage(), TestRunId);

        (await _sut.GetHistoryAsync(session.Id)).OfType<StoredInterruptedMessage>().Should().BeEmpty();
    }

    [Fact]
    public async Task Interrupt_СвободныйЧат_ОтметкуНеПишет()
    {
        var (session, _) = await MkRunningTurnAsync("idle-stop", SessionStatus.Active);

        _sut.Interrupt(session.Id);

        (await _sut.GetHistoryAsync(session.Id)).OfType<StoredInterruptedMessage>().Should().BeEmpty();
    }

    // «Стоп» в чате и «Стоп» в трее рук — один исход: прерывание адаптера (фолбэк на нём
    // запрещён), отметка в истории и пометка модели в начале следующего хода
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Стоп_ВЧатеИВТрееРук_ОдинИсход_ОтметкаИПометкаМодели(bool fromTray)
    {
        var (session, adapter) = await MkRunningTurnAsync("stop-note-" + fromTray);
        string? sent = null;
        adapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<int>(), It.IsAny<bool>()))
            .Callback<string, IReadOnlyList<string>?, int, bool, string?>((t, _, _, _, _) => sent = t)
            .Returns(Task.CompletedTask);

        if (fromTray) new ClaudeHomeServer.Services.Composition.HumanTurnStop(_sut).StoppedByHuman(session.Id);
        else _sut.Interrupt(session.Id);

        adapter.Verify(a => a.Interrupt(), Times.Once());
        (await _sut.GetHistoryAsync(session.Id)).OfType<StoredInterruptedMessage>().Should().ContainSingle();

        // Ход прерван — чат свободен, следующее сообщение человека уходит в CLI с пометкой
        session.Status = SessionStatus.Active;
        await _sut.SendMessageAsync(session.Id, "что дальше?", []);
        sent.Should().StartWith(SessionManager.UserStopNoteText).And.EndWith("что дальше?");

        // Пометка одноразовая
        session.Status = SessionStatus.Active;
        await _sut.SendMessageAsync(session.Id, "ещё", []);
        // В CLI уходит сырая реплика с маркером человека (шаг 1 сжатия), без обвязки
        sent.Should().Be(ClaudeHomeServer.Services.Llm.Claude.ClaudeSession.WithHumanTextMarker("ещё"));
    }

    [Fact]
    public async Task PreemptForPending_ПишетОтметкуВИсторию()
    {
        var (session, _) = await MkRunningTurnAsync("preempt-marker");
        await _sut.SendMessageAsync(session.Id, "срочное", []);

        _sut.PreemptForPending(session.Id).Should().BeTrue();

        (await _sut.GetHistoryAsync(session.Id)).OfType<StoredInterruptedMessage>().Should().ContainSingle();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendMessage_ПрерываетЖдущийХод_ОтметкаТолькоДляЧеловека(bool fromHub)
    {
        var (session, _) = await MkRunningTurnAsync("preempt-send-" + fromHub, SessionStatus.Waiting);

        var outcome = fromHub
            ? await _sut.SendMessageAsync(session.Id, "не спрашивай, делай", [], cause: SessionManager.DeliveryCause.User)
            // Отправка не из хаба (cause не User) — живая лента чужого клиента отметки не ставит
            : await _sut.SendMessageAsync(session.Id, "директива", []);

        outcome.Should().Be(SessionManager.SendUserOutcome.QueuedPreempted);
        (await _sut.GetHistoryAsync(session.Id)).OfType<StoredInterruptedMessage>()
            .Should().HaveCount(fromHub ? 1 : 0);
    }

    [Fact]
    public async Task RespondPermission_КарточкиУжеНет_НеТрогаетАдаптерИСтатус()
    {
        // Гонка: ватчдог оборвал зависший ход (карточку снял обработчик конца хода), а клик
        // пользователя долетел позже. Раньше такой ответ ставил Working на мёртвом процессе
        var session = await MkBusySessionAsync("stale-perm", SessionStatus.Error);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        _sut.RespondPermission(session.Id, "req-1", "allow");

        await Task.Delay(100);
        adapter.Verify(a => a.RespondPermission(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Error);
    }

    [Fact]
    public async Task AnswerQuestion_КарточкиУжеНет_НеТрогаетАдаптерИСтатус()
    {
        var session = await MkBusySessionAsync("stale-question", SessionStatus.Error);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        _sut.AnswerQuestion(session.Id, "tool-1", "{\"answers\":[]}");

        await Task.Delay(100);
        adapter.Verify(a => a.AnswerQuestion(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Error);
    }

    [Fact]
    public async Task RespondPlan_КарточкиУжеНет_НеТрогаетАдаптерИСтатус()
    {
        var session = await MkBusySessionAsync("stale-plan", SessionStatus.Error);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        _sut.RespondPlan(session.Id, "req-1", approve: true, feedback: null);

        await Task.Delay(100);
        adapter.Verify(a => a.RespondPlan(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>()), Times.Never());
        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Error);
    }

    [Fact]
    public async Task RespondPermission_КарточкаЖива_ОтветУходитАдаптеруИЧатВРаботу()
    {
        // Контроль гейта: обычный сценарий не задет
        var session = await MkBusySessionAsync("live-perm", SessionStatus.Waiting);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        SetPendingInteraction(entry, new PermissionRequestMessage("req-1", "Bash", new { }));

        _sut.RespondPermission(session.Id, "req-1", "allow");

        await WaitForConditionAsync(() => _sut.GetById(session.Id)!.Status == SessionStatus.Working,
            TimeSpan.FromSeconds(2));
        adapter.Verify(a => a.RespondPermission("req-1", "allow"), Times.Once());
        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Working);
    }

    [Fact]
    public async Task RespondPermission_КарточкаФоновогоАгентаПослеКонцаХода_ЧатСвободен()
    {
        // Дефект 8ebd5bec: ход отдал «агент запущен» и закончился (Active), Bash фонового агента
        // запросил разрешение — Waiting. Ответ ставил Working, а result хода-продолжения
        // ClaudeSession отбрасывает как «между ходами» — снять Working было некому, чат висел
        // в «Концентрируюсь…» до «Стопа». Вне хода ответ возвращает чат в свободный Active.
        var (session, adapter) = await MkRunningTurnAsync("bg-perm-after-turn", SessionStatus.Active);
        var entry = GetEntry(session.Id);
        var acc = GetAccumulator(entry);

        await InvokeOnMessageAsync(session.Id, acc,
            new PermissionRequestMessage("req-bg", "Bash", new { command = "sleep 60" }), TestRunId);
        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Waiting, "карточка ждёт человека");

        _sut.RespondPermission(session.Id, "req-bg", "allow");

        await WaitForConditionAsync(() => _sut.GetById(session.Id)!.Status != SessionStatus.Waiting,
            TimeSpan.FromSeconds(2));
        adapter.Verify(a => a.RespondPermission("req-bg", "allow"), Times.Once());
        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Active,
            "хода нет — result продолжения до SessionManager не дойдёт, Working стал бы вечным");
        GetLastTurnEndedAt(entry).Should().NotBeNull("sweep должен суметь довести чат до Finished");
    }

    [Fact]
    public async Task RespondPermission_КарточкаВнутриХода_ЧатОстаётсяВРаботе()
    {
        // Контроль: карточка посреди пользовательского хода — после ответа ход идёт дальше
        var (session, adapter) = await MkRunningTurnAsync("perm-in-turn");
        var acc = GetAccumulator(GetEntry(session.Id));

        await InvokeOnMessageAsync(session.Id, acc,
            new PermissionRequestMessage("req-1", "Bash", new { command = "dotnet build" }), TestRunId);
        _sut.RespondPermission(session.Id, "req-1", "allow");

        await WaitForConditionAsync(() => _sut.GetById(session.Id)!.Status != SessionStatus.Waiting,
            TimeSpan.FromSeconds(2));
        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Working);
    }

    // --- Ответ на карточку гасит её на ОСТАЛЬНЫХ устройствах чата ---
    // Отвечающий клиент гасит свою копию оптимистично, остальным нужно событие: без него
    // форма висела активной до перезагрузки страницы (перезагрузка истории не помогает —
    // ответ не добавляет элемент в ленту, и сверка по длине даёт «сервер не новее»).

    [Fact]
    public async Task RespondPermission_КарточкаЖива_РассылаетРешениеОстальнымУстройствам()
    {
        var session = await MkBusySessionAsync("bcast-perm", SessionStatus.Waiting);
        var entry = GetEntry(session.Id);
        SetProcess(entry, StubAdapter(entry).Object);
        SetPendingInteraction(entry, new PermissionRequestMessage("req-1", "Bash", new { }));
        ClearSent();

        _sut.RespondPermission(session.Id, "req-1", "allow_always");

        await WaitForConditionAsync(() => Sent<InteractionResolvedMessage>().Count > 0,
            TimeSpan.FromSeconds(2));
        var msg = Sent<InteractionResolvedMessage>().Single();
        msg.Kind.Should().Be("permission");
        msg.Id.Should().Be("req-1");
        msg.Decision.Should().Be("always");
        msg.SessionId.Should().Be(session.Id);
    }

    [Fact]
    public async Task AnswerQuestion_КарточкаЖива_РассылаетОтветОстальнымУстройствам()
    {
        var session = await MkBusySessionAsync("bcast-question", SessionStatus.Waiting);
        var entry = GetEntry(session.Id);
        SetProcess(entry, StubAdapter(entry).Object);
        SetPendingInteraction(entry, new AskQuestionMessage("tool-1", new { }));
        ClearSent();

        _sut.AnswerQuestion(session.Id, "tool-1", "{\"answers\":{\"Подход\":\"Первый\"}}");

        await WaitForConditionAsync(() => Sent<InteractionResolvedMessage>().Count > 0,
            TimeSpan.FromSeconds(2));
        var msg = Sent<InteractionResolvedMessage>().Single();
        msg.Kind.Should().Be("question");
        msg.Id.Should().Be("tool-1");
        msg.Answers.Should().NotBeNull();
    }

    [Fact]
    public async Task RespondPlan_КарточкаЖива_РассылаетРешениеОстальнымУстройствам()
    {
        var session = await MkBusySessionAsync("bcast-plan", SessionStatus.Waiting);
        var entry = GetEntry(session.Id);
        SetProcess(entry, StubAdapter(entry).Object);
        SetPendingInteraction(entry, new PlanReviewMessage("req-2", "план"));
        ClearSent();

        _sut.RespondPlan(session.Id, "req-2", approve: false, feedback: "доработать");

        await WaitForConditionAsync(() => Sent<InteractionResolvedMessage>().Count > 0,
            TimeSpan.FromSeconds(2));
        var msg = Sent<InteractionResolvedMessage>().Single();
        msg.Kind.Should().Be("plan");
        msg.Id.Should().Be("req-2");
        msg.Approved.Should().BeFalse();
        msg.Feedback.Should().Be("доработать");
    }

    [Fact]
    public async Task AnswerQuestion_КарточкиУжеНет_НичегоНеРассылает()
    {
        // Протухший ответ (ход уже оборван) не должен гасить чужие карточки
        var session = await MkBusySessionAsync("bcast-stale", SessionStatus.Error);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        ClearSent();

        _sut.AnswerQuestion(session.Id, "tool-1", "{\"answers\":[]}");

        await Task.Delay(100);
        // Ассерт на пустую рассылку сам по себе зеленел бы и на голодном раннере, не успевшем
        // выполнить фоновую отправку. Проверка адаптера детерминированна: гейт стоит раньше
        // обоих действий, и снятие гейта роняет тест независимо от таймингов CI.
        adapter.Verify(a => a.AnswerQuestion(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        Sent<InteractionResolvedMessage>().Should().BeEmpty();
    }

    [Fact]
    public async Task RespondPermission_AllowAlways_КладётИнструментНаСессиюИСохраняетСтор()
    {
        // До фикса список жил в ConcurrentDictionary адаптера и обнулялся рестартом сервера,
        // ленивым восстановлением чата и сменой собеседника — человек жал «всегда» заново
        var session = await MkBusySessionAsync("always-perm", SessionStatus.Waiting);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        SetPendingInteraction(entry, new PermissionRequestMessage("req-1", "Bash", new { }));

        _sut.RespondPermission(session.Id, "req-1", "allow_always");

        _sut.GetById(session.Id)!.AutoAllowTools.Should().BeEquivalentTo(["Bash"]);
        (await StoredAutoAllowAsync(session.Id)).Should().BeEquivalentTo(["Bash"],
            "решение обязано пережить рестарт сервера");
        // Само решение уходит адаптеру как есть — «allow» из allow_always делает ClaudeSession
        adapter.Verify(a => a.RespondPermission("req-1", "allow_always"), Times.Once());
    }

    [Fact]
    public async Task RespondPermission_AllowAlwaysПовторно_НеДублируетИнструмент()
    {
        var session = await MkBusySessionAsync("always-dup", SessionStatus.Waiting);
        var entry = GetEntry(session.Id);
        SetProcess(entry, StubAdapter(entry).Object);

        SetPendingInteraction(entry, new PermissionRequestMessage("req-1", "Bash", new { }));
        _sut.RespondPermission(session.Id, "req-1", "allow_always");
        SetPendingInteraction(entry, new PermissionRequestMessage("req-2", "bash", new { }));
        _sut.RespondPermission(session.Id, "req-2", "allow_always");

        _sut.GetById(session.Id)!.AutoAllowTools.Should().BeEquivalentTo(["Bash"],
            "сравнение имён — OrdinalIgnoreCase");
    }

    [Fact]
    public async Task RespondPermission_AllowAlwaysПоЧужойКарточке_НичегоНеЗапоминает()
    {
        // У одного хода карточек может быть несколько (параллельные tool_use). Запомнить
        // «всегда» по чужой карточке значило бы выдать бессрочные права не тому инструменту
        var session = await MkBusySessionAsync("always-mismatch", SessionStatus.Waiting);
        var entry = GetEntry(session.Id);
        SetProcess(entry, StubAdapter(entry).Object);
        SetPendingInteraction(entry, new PermissionRequestMessage("req-2", "Write", new { }));

        _sut.RespondPermission(session.Id, "req-1", "allow_always");

        _sut.GetById(session.Id)!.AutoAllowTools.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveAutoAllowTool_УбираетИнструментИСохраняетСтор()
    {
        var session = await MkBusySessionAsync("always-remove", SessionStatus.Waiting);
        var entry = GetEntry(session.Id);
        SetProcess(entry, StubAdapter(entry).Object);
        SetPendingInteraction(entry, new PermissionRequestMessage("req-1", "Bash", new { }));
        _sut.RespondPermission(session.Id, "req-1", "allow_always");
        var updatedBefore = _sut.GetById(session.Id)!.UpdatedAt;

        // Регистр иной — снятие всё равно обязано сработать
        var updated = _sut.RemoveAutoAllowTool(session.Id, "bash");

        updated!.AutoAllowTools.Should().BeEmpty();
        (await StoredAutoAllowAsync(session.Id)).Should().BeEmpty();
        updated.UpdatedAt.Should().Be(updatedBefore, "смена настройки чата — не активность");
        _sut.RemoveAutoAllowTool(session.Id, "bash").Should().NotBeNull("снятие идемпотентно");
        _sut.RemoveAutoAllowTool("no-such-session", "Bash").Should().BeNull();
    }

    [Fact]
    public async Task RemapModels_ПерепиcываетТолькоМоделиИзКарты()
    {
        var stale = await MkSessionWithModelAsync("stale", "glm-5.2[1m]");
        var air = await MkSessionWithModelAsync("air", "glm-4.5-air");
        var preset = await MkSessionWithModelAsync("preset", "preset:" + Guid.NewGuid());
        var unknown = await MkSessionWithModelAsync("unknown", "glm-5-turbo");
        var empty = await MkSessionWithModelAsync("empty", null);
        var presetBefore = preset.Model;

        _sut.RemapModels(GlmModelAliasMigration.Map).Should().Be(2);

        stale.Model.Should().Be("glm-5.3[1m]");
        air.Model.Should().Be("glm-4.7");
        preset.Model.Should().Be(presetBefore, "ссылка на пресет — не id модели");
        unknown.Model.Should().Be("glm-5-turbo", "незнакомая модель остаётся как была");
        empty.Model.Should().BeNull();
        (await File.ReadAllTextAsync(Path.Combine(_tempDir, "sessions.json")))
            .Should().Contain("glm-5.3[1m]").And.NotContain("glm-5.2[1m]");
    }

    [Fact]
    public async Task МиграцияGlm_ПереписываетЧатыСнимаетКопиюИСтавитМаркер()
    {
        var session = await MkSessionWithModelAsync("hosted", "glm-5.2[1m]");
        // Стор на диске должен существовать до миграции — иначе копию снимать не с чего
        _sut.SetVoiceMode(session.Id, false);

        await NewGlmMigration().StartAsync(CancellationToken.None);

        session.Model.Should().Be("glm-5.3[1m]");
        File.Exists(Path.Combine(_tempDir, ".glm-model-aliases-migrated")).Should().BeTrue();
        Directory.GetFiles(_tempDir, "sessions.json.bak-*").Should().ContainSingle(
            "боевой стор перезаписывается только после снимка");
    }

    [Fact]
    public async Task МиграцияGlm_ПовторныйСтарт_НичегоНеТрогает()
    {
        await NewGlmMigration().StartAsync(CancellationToken.None);
        var session = await MkSessionWithModelAsync("second-run", "glm-5.2[1m]");

        await NewGlmMigration().StartAsync(CancellationToken.None);

        session.Model.Should().Be("glm-5.2[1m]",
            "маркер уже стоит — повторный проход не перебивает выбор пользователя");
    }

    [Fact]
    public async Task МиграцияСемейств_СводитПрибитыеВерсииИСтавитМаркер()
    {
        var opus48 = await MkSessionWithModelAsync("opus48", "claude-opus-4-8");
        var fable5 = await MkSessionWithModelAsync("fable5", "claude-fable-5[1m]");
        var fable51 = await MkSessionWithModelAsync("fable51", "claude-fable-5-1[1m]");
        var opus1m = await MkSessionWithModelAsync("opus1m", "opus[1m]");
        var fable1m = await MkSessionWithModelAsync("fable1m", "fable[1m]");
        var glm = await MkSessionWithModelAsync("glm", "glm-5.2");
        var def = await MkSessionWithModelAsync("def", "default");
        var empty = await MkSessionWithModelAsync("empty", null);
        var persona = _personaManager.Create(TestUserId, "Модельная", null, null, null,
            model: null, effort: null, PersonaScope.Global, projectId: null, color: null,
            greeting: null, memoryEnabled: false);
        persona.Model = "claude-opus-5";
        persona.TierStrong = "claude-fable-5-1[1m]";
        _sut.SetVoiceMode(opus48.Id, false); // стор на диске — чтобы было с чего снять копию

        await NewFamilyMigration().StartAsync(CancellationToken.None);

        opus48.Model.Should().Be("opus");
        fable5.Model.Should().Be("fable");
        fable51.Model.Should().Be("fable");
        opus1m.Model.Should().Be("opus");
        fable1m.Model.Should().Be("fable");
        glm.Model.Should().Be("glm-5.2", "модель стороннего провайдера не трогаем");
        def.Model.Should().Be("default");
        empty.Model.Should().BeNull();
        _personaManager.Get(persona.Id, TestUserId)!.Model.Should().Be("opus");
        _personaManager.Get(persona.Id, TestUserId)!.TierStrong.Should().Be("fable");
        File.Exists(Path.Combine(_tempDir, ClaudeModelFamilyMigration.MarkerFileName)).Should().BeTrue();
        Directory.GetFiles(_tempDir, "sessions.json.bak-*").Should().ContainSingle();
        (await File.ReadAllTextAsync(Path.Combine(_tempDir, "sessions.json")))
            .Should().NotContain("claude-opus-4-8").And.NotContain("[1m]");

        // Идемпотентность: повторное сведение ничего не меняет
        var migration = NewFamilyMigration();
        _sut.RemapModels(migration.Canonical).Should().Be(0);
        _personaManager.RemapModels(migration.Canonical).Should().Be(0);
    }

    [Fact]
    public async Task МиграцияСемейств_ПовторныйСтарт_НичегоНеТрогает()
    {
        await NewFamilyMigration().StartAsync(CancellationToken.None);
        var session = await MkSessionWithModelAsync("second-run", "claude-opus-4-8");

        await NewFamilyMigration().StartAsync(CancellationToken.None);

        session.Model.Should().Be("claude-opus-4-8", "маркер уже стоит — второй проход не идёт");
    }

    [Fact]
    public async Task ЗаписьМодели_РоднойClaudeСводитсяКСемейству()
    {
        var session = await MkSessionWithModelAsync("write", null);

        var updated = await _sut.UpdateAsync(session.Id, TestUserId, null, "claude-fable-5-1[1m]", null);

        updated!.Model.Should().Be("fable");
        _personaManager.Create(TestUserId, "Пин", null, null, null, model: "opus[1m]", effort: null,
            PersonaScope.Global, projectId: null, color: null, greeting: null, memoryEnabled: false)
            .Model.Should().Be("opus");
    }

    [Fact]
    public async Task Interrupt_ЗанятыйЧатБезЖивогоПрогона_РеанимируетЧат()
    {
        // Working без хода терминален: сообщения уходят в Pending, разбор которой ждёт конца
        // хода, а хода уже нет. «Стоп» вместо no-op сбрасывает чат в рабочее состояние
        var session = await MkBusySessionAsync("revive", SessionStatus.Working);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(false); // ход умер, адаптер осиротел
        SetProcess(entry, adapter.Object);
        SetPendingInteraction(entry, new PermissionRequestMessage("req-1", "Bash", new { }));

        _sut.Interrupt(session.Id);

        await WaitForConditionAsync(() => _sut.GetById(session.Id)!.Status == SessionStatus.Active,
            TimeSpan.FromSeconds(2));
        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Active);
        GetProcess(entry).Should().BeNull("отравленный адаптер выброшен — следующий ход поднимет свежий");
        _sut.GetPendingInteraction(session.Id).Should().BeNull();
        adapter.Verify(a => a.DisposeAsync(), Times.Once());
        var notice = _sentMessages.OfType<WorkLoopStoppedMessage>().Should().ContainSingle().Subject;
        notice.Reason.Should().Be("stuck_reset");
        notice.Text.Should().Be("Зависший ход сброшен — чат снова доступен.");
        (await _sut.GetHistoryAsync(session.Id)).OfType<StoredWorkLoopStoppedMessage>()
            .Should().ContainSingle("карточка сброса переживает перезагрузку страницы");
    }

    [Fact]
    public async Task Interrupt_ЗанятыйЧатБезЖивогоПрогона_ОчередьНеЗамораживает()
    {
        // Заморозка снимается концом хода, которого не будет — отложенное застряло бы навсегда
        var session = await MkBusySessionAsync("revive-queue", SessionStatus.Working);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(false);
        SetProcess(entry, adapter.Object);
        await _sut.SendMessageAndWaitAsync(session.Id, "от агента", TimeSpan.Zero);

        _sut.Interrupt(session.Id);

        await WaitForConditionAsync(() => _sut.GetById(session.Id)!.Status == SessionStatus.Active,
            TimeSpan.FromSeconds(2));
        entry.GetType().GetField("QueueFrozen")!.GetValue(entry).Should().Be(false);
        _sut.GetPending(session.Id).Should().ContainSingle().Which.Text.Should().Be("от агента");
    }

    [Fact]
    public async Task Interrupt_ХодВОчередиБезПрогона_АдаптерНеВыбрасывается()
    {
        // Прогон появляется только после старта процесса CLI (это секунды), и «Стоп»,
        // нажатый сразу после отправки, попадал ровно в это окно: чат объявлялся зависшим,
        // адаптер выбрасывался из-под живого хода, и тот падал ObjectDisposedException'ом
        // прямо в ленту («Cannot access a disposed object: SemaphoreSlim», 2026-08-15)
        var session = await MkBusySessionAsync("queued-turn", SessionStatus.Working);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(false);   // прогон ещё не создан
        adapter.SetupGet(a => a.HasQueuedTurn).Returns(true);  // но ход уже принят и поднимает CLI
        SetProcess(entry, adapter.Object);

        _sut.Interrupt(session.Id);

        await Task.Delay(100);
        adapter.Verify(a => a.Interrupt(), Times.Once());
        adapter.Verify(a => a.DisposeAsync(), Times.Never());
        GetProcess(entry).Should().NotBeNull("ход в полёте — адаптер убирать нельзя");
        _sentMessages.OfType<WorkLoopStoppedMessage>().Should().BeEmpty("это не реанимация зависшего чата");
    }

    [Fact]
    public async Task Interrupt_ЖивойПрогон_ПрежнееПоведение()
    {
        // Контроль: у честно занятого чата «Стоп» по-прежнему бьёт по адаптеру, статус
        // синхронно не трогает (его сменит exited убитого хода) и реанимации не устраивает
        var session = await MkBusySessionAsync("revive-control", SessionStatus.Working);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry); // HasLiveTurn = true
        SetProcess(entry, adapter.Object);

        _sut.Interrupt(session.Id);

        await Task.Delay(100);
        adapter.Verify(a => a.Interrupt(), Times.Once());
        adapter.Verify(a => a.DisposeAsync(), Times.Never());
        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Working);
        GetProcess(entry).Should().NotBeNull();
        _sentMessages.OfType<WorkLoopStoppedMessage>().Should().BeEmpty();
    }

    [Fact]
    public void GetPending_НесуществующаяСессия_Пусто()
    {
        _sut.GetPending("nonexistent").Should().BeEmpty();
    }

    [Fact]
    public async Task SendOrEnqueue_ЗанятыйЧат_ОткладываетСлужебныйХодИНеПоказываетПризрак()
    {
        // Доклад исполнителя (модель Z): его текст уже лежит в ленте гостевой репликой,
        // поэтому ход-реакция откладывается молча — призрак дублировал бы служебный промпт
        var session = await MkBusySessionAsync("silent");

        var deferred = await _sut.SendOrEnqueueAsync(session.Id, "отреагируй на отчёт",
            senderPersonaId: "delegator-1", silent: true, suppressTasksExecute: true);

        deferred.Should().BeTrue();
        var queued = _sut.GetPending(session.Id).Should().ContainSingle().Subject;
        queued.Silent.Should().BeTrue();
        queued.SuppressTasksExecute.Should().BeTrue("признак хода-реакции едет с отложенным ходом");
        _sut.GetVisiblePending(session.Id).Should().BeEmpty("служебный ход призраком не показываем");
    }

    [Fact]
    public async Task GetVisiblePending_ОбычноеСообщение_Видно()
    {
        var session = await MkBusySessionAsync("visible");

        await _sut.SendMessageAndWaitAsync(session.Id, "привет", TimeSpan.Zero, senderOrigin: "Проект Бета");

        var visible = _sut.GetVisiblePending(session.Id).Should().ContainSingle().Subject;
        visible.Text.Should().Be("привет");
        visible.SenderOrigin.Should().Be("Проект Бета");
    }

    // --- «Честная очередь»: пользовательские сообщения в занятый чат ---

    [Theory]
    [InlineData(SessionStatus.Working)]
    [InlineData(SessionStatus.Waiting)]
    public async Task SendMessage_User_ЗанятыйЧат_СтавитВВидимуюОчередь(SessionStatus status)
    {
        // Раньше сообщение пользователя молча писалось в stdin занятого CLI — теперь
        // встаёт в видимую серверную очередь с вложениями и режимом (FIFO)
        var session = await MkBusySessionAsync("uq" + status, status);

        var outcome = await _sut.SendMessageAsync(session.Id, "подожди меня", ["a.txt", "b.txt"], mode: "plan");

        // Working — ход доживает сам (Queued); Waiting — он ждёт человека и не закончится
        // никогда, поэтому прерывается ради доставки (QueuedPreempted)
        outcome.Should().Be(status == SessionStatus.Waiting
            ? SessionManager.SendUserOutcome.QueuedPreempted
            : SessionManager.SendUserOutcome.Queued);
        var queued = _sut.GetPending(session.Id).Should().ContainSingle().Subject;
        queued.Text.Should().Be("подожди меня");
        queued.Kind.Should().Be(SessionManager.PendingKind.User);
        queued.AttachedPaths.Should().BeEquivalentTo(["a.txt", "b.txt"]);
        queued.Mode.Should().Be("plan");
        // В видимом снимке пользовательское тоже отражается (карточка-призрак)
        _sut.GetVisiblePending(session.Id).Should().ContainSingle();
    }

    [Fact]
    public async Task SendMessage_User_ДубликатыДопускаются()
    {
        // Человек может осознанно слать повторы («продолжи», «ещё раз») — дедуп только
        // для агентских, пользовательские не дедупятся
        var session = await MkBusySessionAsync("uqdup");

        await _sut.SendMessageAsync(session.Id, "ещё раз", []);
        await _sut.SendMessageAsync(session.Id, "ещё раз", []);

        _sut.GetPending(session.Id).Should().HaveCount(2);
    }

    // --- Синхронизация устройств: live-баллон ручного ввода ---

    [Fact]
    public async Task SendMessage_User_РучнойВвод_РассылаетсяВсемКромеОтправителя()
    {
        // Фикс синхронизации устройств: реплика, отправленная с устройства А, раньше не
        // приезжала устройству Б live (только из истории по F5) — сервер не рассылал
        // user_message для ручного ввода, потому что отправитель рисует баллон оптимистично.
        // Теперь реплика идёт в session-группу с исключением соединения-отправителя:
        // у А дубля нет, Б видит сообщение сразу.
        var session = await MkFreeSessionWithStubAsync("live-except");
        ClearSent();

        await _sut.SendMessageAsync(session.Id, "привет с ноута", [], senderConnectionId: "conn-A");

        var except = _broadcaster.Inner.SessionExcept.Should().ContainSingle().Subject;
        except.SessionId.Should().Be(session.Id);
        except.ExceptConnectionId.Should().Be("conn-A");
        except.Message.Should().BeOfType<UserMessageMessage>().Which.Text.Should().Be("привет с ноута");
        // В общий session-канал дубль не падал — эха отправителю нет
        _broadcaster.Inner.Session.Select(t => t.Message).OfType<UserMessageMessage>().Should().BeEmpty();
    }

    [Fact]
    public async Task SendMessage_User_БезСоединения_РассылаетсяВсем()
    {
        // REST/сервисные вызовы (connectionId нет): оптимистичного баллона ни у кого нет —
        // реплика уходит всем соединениям session-группы, исключать некого
        var session = await MkFreeSessionWithStubAsync("live-rest");
        ClearSent();

        await _sut.SendMessageAsync(session.Id, "из REST", []);

        _broadcaster.Inner.Session.Select(t => t.Message).OfType<UserMessageMessage>().Should().ContainSingle()
            .Which.Text.Should().Be("из REST");
        _broadcaster.Inner.SessionExcept.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelPending_СнимаетПользовательскоеСообщение()
    {
        var session = await MkBusySessionAsync("uqcancel");
        await _sut.SendMessageAsync(session.Id, "первое", []);
        await _sut.SendMessageAsync(session.Id, "второе", []);
        var first = _sut.GetPending(session.Id)[0];

        (await _sut.CancelPendingAsync(session.Id, first.Id)).Should().BeTrue();

        _sut.GetPending(session.Id).Should().ContainSingle().Which.Text.Should().Be("второе");
    }

    [Fact]
    public async Task Interrupt_ИзымаетПоследнееПользовательское_АгентскиеОстаются()
    {
        // «Стоп» замораживает очередь и возвращает в композер ПОСЛЕДНЕЕ пользовательское:
        // оно изымается, агентские и более ранние пользовательские остаются ждать возобновления
        var session = await MkBusySessionAsync("uqfreeze");
        SetProcess(GetEntry(session.Id), StubAdapter(GetEntry(session.Id)).Object);
        await _sut.SendMessageAndWaitAsync(session.Id, "от агента", TimeSpan.Zero);       // agent
        await _sut.SendMessageAsync(session.Id, "user-1", []);                              // user
        await _sut.SendMessageAsync(session.Id, "user-последнее", []);                      // user (последнее)

        _sut.Interrupt(session.Id);

        var pending = _sut.GetPending(session.Id);
        pending.Should().HaveCount(2); // agent + user-1 (последнее user изъято)
        pending.Select(p => p.Text).Should().BeEquivalentTo(["от агента", "user-1"]);
        pending.Should().NotContain(p => p.Text == "user-последнее");
    }

    [Fact]
    public async Task Превью_ОбычныйХод_ИсходныйТекстБезОбвязкиХода()
    {
        // В CLI ход уходит с обвязкой (BuildCliTurnText — здесь пометка об обрыве фонового
        // агента), а в карточке чата обязано остаться то, что написал человек
        var (session, entry, sentText) = await MkPreviewChatAsync("prev-plain");
        entry.GetType().GetField("TruncatedBgNote")!.SetValue(entry, PreviewBgRun());

        await _sut.SendMessageAsync(session.Id, "почини тесты", []);

        session.LastMessage.Should().Be("почини тесты");
        sentText()!.Should().EndWith("почини тесты").And.Contain("обрывок",
            "обвязка хода в CLI уехала — и именно её не должно быть в превью");
    }

    [Fact]
    public async Task Превью_СистемнаяДиректива_ОстаётсяПоследнееСообщениеЧеловека()
    {
        // Директива («[СИСТЕМНАЯ ДИРЕКТИВА — ОБРЫВ САБАГЕНТА 1/2] …») человеку в списке
        // чатов не адресована: превью держит его последнюю реплику
        var (session, _, sentText) = await MkPreviewChatAsync("prev-directive");
        await _sut.SendMessageAsync(session.Id, "почини тесты", []);

        await _sut.SendMessageAsync(session.Id, "[СИСТЕМНАЯ ДИРЕКТИВА — ОБРЫВ САБАГЕНТА 1/2] Сабагент «kostya» замолчал",
            [], systemDirective: true);

        session.LastMessage.Should().Be("почини тесты");
        sentText()!.Should().Contain("СИСТЕМНАЯ ДИРЕКТИВА", "в CLI директива уходит как обычный ход");
    }

    [Fact]
    public async Task Превью_ДлинноеСообщение_ОбрезаетсяСоМноготочием()
    {
        var (session, _, _) = await MkPreviewChatAsync("prev-long");
        var text = new string('я', 150);

        await _sut.SendMessageAsync(session.Id, text, []);

        session.LastMessage.Should().Be(new string('я', 100) + "…");
    }

    [Fact]
    public async Task Превью_ХодАгента_СообщениемАгентаБезОбвязки()
    {
        // chats_send (SendMessageAndWaitAsync): превью агентскому ходу ставит SessionManager —
        // адаптер LastMessage больше не пишет, без записи здесь карточка чата вовсе не видела
        // бы сообщения агента. Обвязки хода нет и в CLI: BuildCliTurnText агентскому пути не клеится
        var (session, _, sentText) = await MkPreviewChatAsync("prev-agent");

        var result = await _sut.SendMessageAndWaitAsync(session.Id, "доклад из соседнего чата", TimeSpan.Zero);

        result.Should().BeOfType<SendAndWaitResult.Running>("таймаут нулевой — ход не ждём");
        session.LastMessage.Should().Be("доклад из соседнего чата");
        sentText().Should().Be("доклад из соседнего чата", "агентскому ходу обвязка не клеится");
    }

    // --- Очередь входящих сообщений в занятом чате (enqueue, прерывание — точечное) ---

    [Fact]
    public async Task SendMessage_User_ЗанятыйЧат_НеПрерываетХод_ИДоставляетПоResult()
    {
        // Отправка ≠ остановка (поведение claude CLI): сообщение пользователя встаёт в
        // очередь и ЖДЁТ штатного конца хода. Kill посреди хода выбрасывал бы сделанную
        // работу и оплаченные токены, а в транскрипте оставлял tool_use без tool_result.
        var session = await MkBusySessionAsync("preempt", SessionStatus.Working);
        session.Name = "есть имя"; // иначе фоновый уточнятор заголовка полезет в локальную модель
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        var outcome = await _sut.SendMessageAsync(session.Id, "срочное", []);

        outcome.Should().Be(SessionManager.SendUserOutcome.Queued);
        adapter.Verify(a => a.Interrupt(), Times.Never());
        _sut.GetPending(session.Id).Should().ContainSingle().Which.Text.Should().Be("срочное");

        // Ход дожил до конца сам — очередь разбирает штатный result
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));

        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once());
        _sut.GetPending(session.Id).Should().BeEmpty("по концу хода очередь разобрана");
    }

    [Fact]
    public async Task SendMessage_User_ЧатЖдётЧеловека_ПрерываетХод_ИДоставляетПоExited()
    {
        // Waiting — ход стоит на запросе разрешения/вопросе и сам не закончится никогда:
        // текст вместо ответа на диалог означает, что отвечать на него не будут. Здесь
        // прерывание обязательно, иначе сообщение висело бы в очереди вечно. Убитый процесс
        // result не шлёт — очередь разбирается по exited ТОГО ЖЕ прогона (DrainOnExitedRun).
        var session = await MkBusySessionAsync("preempt-waiting", SessionStatus.Waiting);
        session.Name = "есть имя";
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        var outcome = await _sut.SendMessageAsync(session.Id, "не спрашивай, делай", []);

        // Клиенту важно отличить это от аварии: убитый ход пришлёт голый exited
        outcome.Should().Be(SessionManager.SendUserOutcome.QueuedPreempted);
        adapter.Verify(a => a.Interrupt(), Times.Once());

        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ExitedMessage(), TestRunId);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));

        _sut.GetPending(session.Id).Should().BeEmpty("прерывание доставляет сообщение немедленно");
    }

    [Fact]
    public async Task PreemptForPending_ПрерываетХодИДоставляетЖдущееПоExited()
    {
        // Явный перебой (кнопка «Прервать и отправить» на карточке очереди): в отличие от
        // «Стоп» очередь НЕ морозится — прерывание здесь и есть требование доставить сейчас
        var session = await MkBusySessionAsync("manual-preempt", SessionStatus.Working);
        session.Name = "есть имя";
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        await _sut.SendMessageAsync(session.Id, "срочное", []);
        // Предусловие: сама отправка ход не рвёт
        adapter.Verify(a => a.Interrupt(), Times.Never());

        _sut.PreemptForPending(session.Id).Should().BeTrue();

        adapter.Verify(a => a.Interrupt(), Times.Once());
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ExitedMessage(), TestRunId);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));
        _sut.GetPending(session.Id).Should().BeEmpty("очередь разобрана по exited убитого хода");
    }

    // --- Непрочитанность: доводка статуса не воскрешает точку у прочитанного чата ---

    [Fact]
    public async Task Exited_ПослеПрочтения_НеДелаетЧатНепрочитанным()
    {
        // Инцидент 06.09.2026: чат открыли (LastReadAt догнал UpdatedAt), ушли из него, а
        // спустя секунды пришла доводка exited — безусловный UpdatedAt снова обгонял отметку
        // прочтения, и серая точка на кнопках проекта и стены возвращалась сама собой
        var session = await MkBusySessionAsync("unread-exited", SessionStatus.Active);
        session.UpdatedAt = DateTime.UtcNow.AddMinutes(-1);
        session.LastReadAt = session.UpdatedAt;
        var updatedAt0 = session.UpdatedAt;

        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ExitedMessage(), TestRunId);

        session.Status.Should().Be(SessionStatus.Finished, "статус доводится как раньше");
        session.UpdatedAt.Should().Be(updatedAt0, "за exited нового содержимого нет");
        session.LastReadAt.Should().Be(session.UpdatedAt, "чат остаётся прочитанным");
    }

    [Fact]
    public async Task Result_ДвигаетUpdatedAt_ОтветЭтоНовоеСодержимое()
    {
        // Контроль пары к предыдущему: ответ хода обязан помечать чат непрочитанным —
        // иначе на другом устройстве ход прошёл бы вовсе незаметно
        var session = await MkBusySessionAsync("unread-result", SessionStatus.Working);
        session.UpdatedAt = DateTime.UtcNow.AddMinutes(-1);
        session.LastReadAt = session.UpdatedAt;
        var updatedAt0 = session.UpdatedAt;

        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ResultMessage("success", 10, 1, null, null), TestRunId);

        session.Status.Should().Be(SessionStatus.Active);
        session.UpdatedAt.Should().BeAfter(updatedAt0);
    }

    [Fact]
    public async Task Exited_БезResult_ПриНепустойОчереди_РазбираетЕё()
    {
        // Прогон умер без result (смерть процесса, ватчдог), а сообщение стоит в очереди.
        // Пока каждая отправка убивала ход, дыру закрывала метка DrainOnExitedRun; теперь
        // отправка ход не трогает, и без разбора по exited сообщение висело бы призраком
        // при простаивающем чате — забрать его смогла бы только следующая отправка.
        var session = await MkBusySessionAsync("dead-run-drain", SessionStatus.Working);
        session.Name = "есть имя";
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        await _sut.SendMessageAsync(session.Id, "жду доставки", []);
        adapter.Verify(a => a.Interrupt(), Times.Never());

        // Боевой порядок: ClaudeSession обнуляет прогон ДО отправки exited (HasLiveTurn=false),
        // но обёртка FallbackLlmSessionAdapter отдаёт придержанный exited вниз из SettleAsync и
        // снимает оркестрацию лишь в finally — то есть ПОСЛЕ нас. Стаб это воспроизводит: гейт
        // разбора обязан смотреть на живой прогон, а не на занятость адаптера целиком.
        adapter.SetupGet(a => a.HasLiveTurn).Returns(false);
        adapter.SetupGet(a => a.OrchestrationActive).Returns(true);
        // Процесс умер сам: exited без предшествующего result и без метки разбора
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ExitedMessage(), TestRunId);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));

        _sut.GetPending(session.Id).Should().BeEmpty("очередь разобрана по смерти прогона");
    }

    [Fact]
    public async Task Стоп_ЗатемОтправка_ДоставляетПоExitedУбитогоХода()
    {
        // Ходовой сценарий: «Стоп» → сразу отправить исправленный текст, пока exited убитого
        // хода ещё в пути. Статус до exited остаётся Working, метку разбора «Стоп» не ставит —
        // без разбора по смерти прогона сообщение застряло бы навсегда.
        var session = await MkBusySessionAsync("stop-then-send", SessionStatus.Working);
        session.Name = "есть имя";
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        _sut.Interrupt(session.Id);                                  // «Стоп»: очередь заморожена
        await _sut.SendMessageAsync(session.Id, "давай иначе", []);  // возобновление: разморозка + enqueue

        // Прогон, убитый «Стопом», мёртв, а оркестрация фолбэка ещё не снята — тот же боевой
        // порядок, что и в тесте выше (её снимает finally уже после доставки exited вниз)
        adapter.SetupGet(a => a.HasLiveTurn).Returns(false);
        adapter.SetupGet(a => a.OrchestrationActive).Returns(true);
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ExitedMessage(), TestRunId);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));

        _sut.GetPending(session.Id).Should().BeEmpty("сообщение ушло в работу, а не зависло");
    }

    [Fact]
    public async Task Exited_ЗамороженнаяОчередь_НеРазбираетсяРазборомМёртвогоПрогона()
    {
        // Инвариант «Стоп»: заморозку снимает только возобновление пользователем. Новый
        // разбор по смерти прогона не должен его обходить, иначе сразу после «Стоп» хлынул
        // бы ход из очереди — ровно то, от чего заморозка и защищает.
        var session = await MkBusySessionAsync("frozen-dead-run", SessionStatus.Working);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        await _sut.SendMessageAndWaitAsync(session.Id, "агентский доклад", TimeSpan.Zero);
        _sut.Interrupt(session.Id); // «Стоп» морозит очередь

        adapter.SetupGet(a => a.HasLiveTurn).Returns(false); // прогон убит «Стопом»
        adapter.SetupGet(a => a.OrchestrationActive).Returns(true);
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ExitedMessage(), TestRunId);
        await Task.Delay(150);

        _sut.GetPending(session.Id).Should().ContainSingle("замороженная очередь ждёт возобновления");
    }

    [Fact]
    public async Task PreemptForPending_ЧатБезЖивогоПрогона_Отказывает()
    {
        // Чат числится занятым, а прогона нет (ход убил ватчдог/сбой, статус остался): убивать
        // нечего, exited не придёт. Ответить «прервал» значило бы соврать клиенту — он нарисует
        // «Прервано», а сообщение так и не уйдёт. Такой чат реанимирует «Стоп».
        var session = await MkBusySessionAsync("preempt-stuck", SessionStatus.Working);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(false);
        SetProcess(entry, adapter.Object);
        await _sut.SendMessageAsync(session.Id, "срочное", []);

        _sut.PreemptForPending(session.Id).Should().BeFalse();

        adapter.Verify(a => a.Interrupt(), Times.Never());
        _sut.GetPending(session.Id).Should().ContainSingle("сообщение осталось ждать");
    }

    [Fact]
    public async Task PreemptForPending_ВоВремяКомпакции_Отказывает()
    {
        // Компакция — обычный ход, но обёртка фолбэка его не оркеструет и exited убитой
        // компакции ГЛОТАЕТ: до SessionManager не дошло бы ничего, чат остался бы в вечном
        // Working с застрявшей очередью. Прерывать её нельзя — отказываем честно.
        var session = await MkBusySessionAsync("preempt-compact", SessionStatus.Active);
        session.ClaudeSessionId = "csid-compact"; // иначе сворачивать нечего и CompactAsync выйдет сразу
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        await _sut.CompactAsync(session.Id);
        await _sut.SendMessageAsync(session.Id, "во время сжатия", []);

        _sut.PreemptForPending(session.Id).Should().BeFalse();
        adapter.Verify(a => a.Interrupt(), Times.Never());

        // Компакция дошла до конца сама — очередь разбирается штатным путём, по её result
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));

        _sut.GetPending(session.Id).Should().BeEmpty("очередь разобрана по result компакции");
    }

    [Fact]
    public async Task PreemptForPending_БезОчередиИлиБезХода_НичегоНеПрерывает()
    {
        // Холостой перебой убил бы чужой только что стартовавший ход, ничего не доставив
        var session = await MkBusySessionAsync("manual-preempt-noop", SessionStatus.Working);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        _sut.PreemptForPending(session.Id).Should().BeFalse("очередь пуста — доставлять нечего");

        await _sut.SendMessageAsync(session.Id, "срочное", []);
        session.Status = SessionStatus.Active; // ход кончился сам, пока пользователь целился в кнопку
        _sut.PreemptForPending(session.Id).Should().BeFalse("ход уже не идёт — прерывать нечего");
        adapter.Verify(a => a.Interrupt(), Times.Never());
    }

    [Fact]
    public async Task SendMessage_User_ВЦиклеДоГотово_НеСнимаетЦиклИПрерываетХод()
    {
        // Сообщение пользователя в чат с активным циклом НЕ снимает цикл (решение владельца
        // 2026-08-08): ход прерывается, сообщение встаёт в очередь, цикл остаётся активным.
        // WorkLoopMessage(active:false) не рассылается — цикл не выключен.
        var session = await MkBusySessionAsync("preempt-loop", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        ClearSent();

        var outcome = await _sut.SendMessageAsync(session.Id, "вмешаться в цикл", []);

        outcome.Should().Be(SessionManager.SendUserOutcome.QueuedPreempted);
        _sut.GetById(session.Id)!.WorkLoop.Should().NotBeNull("сообщение пользователя не снимает цикл");
        Sent<WorkLoopMessage>().Should().NotContain(m => !m.Active,
            "выключение цикла не рассылается — цикл остаётся активным");
        adapter.Verify(a => a.Interrupt(), Times.Once());
        _sut.GetPending(session.Id).Should().ContainSingle()
            .Which.Kind.Should().Be(SessionManager.PendingKind.User);
    }

    [Fact]
    public async Task SendMessage_User_ЦиклМеждуИтерациями_НеСнимаетЦиклИДоставляетИтерациюСПротоколом()
    {
        // Между итерациями цикла чат на мгновение свободен: прерывать нечего (Interrupt не
        // зовётся), но цикл НЕ снимается — сообщение продолжает его следующей итерацией:
        // dispatchNow форсирует разбор, и доставленный ход несёт протокол цикла (WorkLoopTurn).
        var session = await MkBusySessionAsync("preempt-idle-loop", SessionStatus.Active);
        session.Name = "есть имя";
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        var outcome = await _sut.SendMessageAsync(session.Id, "вмешаться между итерациями", []);

        outcome.Should().Be(SessionManager.SendUserOutcome.Queued);
        _sut.GetById(session.Id)!.WorkLoop.Should().NotBeNull("цикл продолжается");
        adapter.Verify(a => a.Interrupt(), Times.Never());
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));
        adapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("вмешаться между итерациями")
                            && t.Contains("[ЦИКЛ «ДО ГОТОВО»]")),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Once());
        _sut.GetPending(session.Id).Should().BeEmpty();
    }

    // Дыра в очереди (решение владельца 17.10): chats_send в ЗАНЯТЫЙ чат кладёт QueuedMessage
    // с правильной AgentDepth, но DeliverPendingAsync для агентского сообщения звал
    // SendMessageAsync(…, auto: true, …) БЕЗ глубины — ход стартует с глубиной 0, и потолок
    // глубины запуска (allowedDepth = 1) становится фиктивным: глубина 1, доставленная из
    // очереди, проходила гейт как 0 (живой инцидент 17.10: второе сообщение Александра
    // Софье пришло через очередь, и запуск прошёл, хотя первое, прямое, получило отказ).
    // Регрессия: агентское сообщение глубины 1, доставленное из очереди, даёт ход с глубиной 1.
    [Fact]
    public async Task Очередь_ДоставкаАгентскогоГлубины1_ХодСГлубиной1()
    {
        var session = await MkBusySessionAsync("queue-depth", SessionStatus.Working);
        var entry = GetEntry(session.Id);

        // Мок-адаптер, фиксирующий глубину, которую ход реально получил (аргумент SendMessageAsync)
        var adapter = new Mock<ILlmSessionAdapter>();
        adapter.SetupGet(a => a.Info).Returns(session);
        int? capturedDepth = null;
        adapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string>()))
            .Callback<string, IReadOnlyList<string>?, int, bool, string?>(
                (_, _, depth, _, _) => capturedDepth = depth)
            .Returns(Task.CompletedTask);
        SetProcess(entry, adapter.Object);

        // Агентское сообщение глубины 1 — как chats_send из другого чата
        var next = new SessionManager.QueuedMessage(Guid.NewGuid().ToString("N"), "сообщение глубины 1",
            null, null, 1, DateTime.UtcNow, Kind: SessionManager.PendingKind.Agent);

        var method = typeof(SessionManager).GetMethod("DeliverPendingAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(_sut, [session.Id, entry, next])!;
        await task;

        capturedDepth.Should().Be(1,
            "доставка из очереди донесла глубину хода — потолок глубины запуска не фиктивный");
    }

    [Fact]
    public async Task ContinueWorkLoop_ПользовательскоеСообщениеВОчереди_НеШлётДирективуПродолжения()
    {
        // Без двойной отправки (п.4): при user-pending в очереди директива продолжения НЕ шлётся —
        // продолжением служит само сообщение пользователя. Ход-итерация завершается result,
        // в очереди лежит вводная пользователя.
        var session = await MkBusySessionAsync("loop-pending-race", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        SetLoopTurnInFlight(entry, true); // ход-итерация в полёте
        EnqueueUser(entry, "доп. вводная человека");

        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));

        // Доставка из очереди пошла (сообщение пользователя как следующая итерация)…
        adapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("доп. вводная человека")), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once());
        // …а системной директивы продолжения НЕ было — иначе два хода подряд в один процесс
        adapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("СИСТЕМНАЯ ДИРЕКТИВА — ЦИКЛ")), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Never());
        _sut.GetById(session.Id)!.WorkLoop.Should().NotBeNull("цикл продолжается на сообщении пользователя");
    }

    [Fact]
    public async Task SetWorkLoop_СтопВоВремяХодаСАгентскимВОчереди_НеЗапускаетВторойХод()
    {
        // Major: ручной стоп цикла приходит ВО ВРЕМЯ хода (Status=Working). В очереди лежит
        // агентское сообщение (ждало конца цикла). Без гейта статуса SetWorkLoopAsync завёл бы
        // drain → голова очереди ушла бы вторым ходом в ЖИВОЙ процесс (SendMessageAsync(auto)
        // минует гейт занятости). Гейт: drain запускается только когда чат уже свободен.
        var session = await MkBusySessionAsync("loop-stop-busy", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        SetLoopTurnInFlight(entry, true); // ход-итерация цикла в полёте
        EnqueueAgent(entry, "доклад из параллельного чата");

        await _sut.SetWorkLoopAsync(session.Id, enabled: false, userId: TestUserId, manual: true);

        // Drain запускается в фоне; подставной адаптер синхронен, поэтому, будь гейт снят,
        // SendMessageAsync прилетел бы за доли секунды. Ждём окно и проверяем, что вызова НЕ было.
        await WaitForSendAsync(adapter, TimeSpan.FromMilliseconds(500));
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Never(),
            "стоп цикла во время хода не должен пускать второй ход в живой процесс");
        _sut.GetPending(session.Id).Should().ContainSingle(
            "агентское сообщение остаётся ждать штатного drain по result текущего хода");

        // Позитивный контроль (Minor 3): негативная проверка выше — по таймауту, без неё не видно,
        // что сообщение не потерялось, а дождётся своего часа. Доигрываем result текущего хода:
        // статус падает в Active, drain разбирает очередь — накопленное агентское доставляется.
        // Этим же закрывается, что доставка накопленного по result не сломана правками Minor 1–2.
        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));

        adapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("доклад из параллельного чата")), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once(),
            "по result текущего хода накопленное агентское доставляется");
        _sut.GetPending(session.Id).Should().BeEmpty("очередь разобрана после result");
    }

    [Fact]
    public async Task DrainNextPending_ПараллельныеВызовыВне_ОтправляютТолькоОдно()
    {
        // Minor 1: два параллельных drain (выключение цикла из SetWorkLoopAsync + штатный
        // из OnMessageAsync по result) без сериализации вытащили бы по сообщению каждый → два
        // хода. DrainInFlight сериализует разбор вне цикла: второй drain уступает, сообщение
        // ждёт result первого. Чтобы смоделировать реальную параллельность (на ThreadPool),
        // задерживаем отправку первого drain на gate — он висит со взведённым DrainInFlight.
        var session = await MkBusySessionAsync("drain-dedup", SessionStatus.Active);
        session.Name = "есть имя";
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        EnqueueAgent(entry, "доклад-1");
        EnqueueAgent(entry, "доклад-2");

        var sendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>())).Returns(() => sendGate.Task);

        var drain = typeof(SessionManager).GetMethod("DrainNextPendingAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var t1 = Task.Run(async () => await (Task)drain.Invoke(_sut, [session.Id])!);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(1)); // t1 дошёл до отправки, DrainInFlight=true

        var t2 = Task.Run(async () => await (Task)drain.Invoke(_sut, [session.Id])!);
        await Task.WhenAny(t2, Task.Delay(TimeSpan.FromSeconds(1))); // t2 уступил по DrainInFlight

        sendGate.SetResult(); // отпустить первый drain — finally погасит DrainInFlight
        await t1;

        // Отправлено ровно одно — второе сообщение осталось ждать своего хода.
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once());
        _sut.GetPending(session.Id).Should().ContainSingle("второе сообщение осталось в очереди");
    }

    [Fact]
    public async Task Interrupt_ЧатВЦиклеДоГотово_СнимаетЦикл()
    {
        // «Стоп» — явный путь выключения цикла (п.5): прерывает ход и снимает цикл синхронно,
        // поведение прежнее. WorkLoopMessage(active:false) рассылается.
        var session = await MkBusySessionAsync("loop-stop", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        ClearSent();

        _sut.Interrupt(session.Id);

        _sut.GetById(session.Id)!.WorkLoop.Should().BeNull("«Стоп» снимает цикл");
        Sent<WorkLoopMessage>().Should().ContainSingle(m => !m.Active);
    }

    [Fact]
    public async Task SendMessageAndWait_ОбычныйЗанятыйЧат_ПрерываетХод()
    {
        // Агентское сообщение (chats_send) в обычный занятый чат тоже прерывает ход —
        // доклад доставится сразу, а не после многоминутного хода
        var session = await MkBusySessionAsync("agent-preempt", SessionStatus.Working);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        var result = await _sut.SendMessageAndWaitAsync(session.Id, "срочный доклад", TimeSpan.Zero);

        result.Should().BeOfType<SendAndWaitResult.Queued>();
        adapter.Verify(a => a.Interrupt(), Times.Once());
        _sut.GetPending(session.Id).Should().ContainSingle();
    }

    [Fact]
    public async Task SendMessageAndWait_ЧатВЦиклеДоГотово_НеПрерывает()
    {
        // Доклады персон не рушат цикл «до готово»: сообщение ждёт конца всего цикла
        var session = await MkBusySessionAsync("agent-loop", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        var result = await _sut.SendMessageAndWaitAsync(session.Id, "доклад в цикл", TimeSpan.Zero);

        result.Should().BeOfType<SendAndWaitResult.Queued>();
        adapter.Verify(a => a.Interrupt(), Times.Never());
        _sut.GetById(session.Id)!.WorkLoop.Should().NotBeNull("агентское сообщение не трогает цикл");
        _sut.GetPending(session.Id).Should().ContainSingle();
    }

    [Fact]
    public async Task SendMessageAndWait_PreemptFalse_ОбычныйЗанятыйЧат_НеПрерываетИДоставляетПоResult()
    {
        // preempt=false: агент не рубит чужой рабочий ход (Kill убил бы фоновых сабагентов
        // внутри него) — сообщение ждёт штатного result и уходит в ЖИВОЙ процесс
        var session = await MkBusySessionAsync("agent-nopreempt", SessionStatus.Working);
        session.Name = "есть имя";
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        var result = await _sut.SendMessageAndWaitAsync(session.Id, "доклад без прерывания",
            TimeSpan.Zero, preempt: false);

        result.Should().BeOfType<SendAndWaitResult.Queued>();
        adapter.Verify(a => a.Interrupt(), Times.Never());
        _sut.GetPending(session.Id).Should().ContainSingle();

        // Штатный конец хода разбирает очередь: сообщение доставлено, очередь пуста
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));
        _sut.GetPending(session.Id).Should().BeEmpty();
        // «Доклад» — агентское (chats_send): drain доставляет как auto: true — не-человеческий ход, маркера не получает (ShouldMarkHumanText: auto → false).
        adapter.Verify(a => a.SendMessageAsync("доклад без прерывания", It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once());
    }

    [Fact]
    public async Task SendMessageAndWait_PreemptFalse_ОжидающийЧат_ПрерываетХод()
    {
        // Waiting прерывается и при preempt=false: ход на карточке разрешения сам не
        // закончится — «не прерывать» означало бы вечное ожидание (как у пользователя)
        var session = await MkBusySessionAsync("agent-nopreempt-waiting", SessionStatus.Waiting);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        var result = await _sut.SendMessageAndWaitAsync(session.Id, "ответ вместо человека",
            TimeSpan.Zero, preempt: false);

        result.Should().BeOfType<SendAndWaitResult.Queued>();
        adapter.Verify(a => a.Interrupt(), Times.Once());
        _sut.GetPending(session.Id).Should().ContainSingle();
    }

    [Fact]
    public async Task SendMessageAndWait_PreemptTrueЯвно_ОбычныйЗанятыйЧат_ПрерываетХод()
    {
        // Явный preempt=true ведёт себя как дефолт (обратная совместимость контракта)
        var session = await MkBusySessionAsync("agent-preempt-true", SessionStatus.Working);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        var result = await _sut.SendMessageAndWaitAsync(session.Id, "срочный доклад",
            TimeSpan.Zero, preempt: true);

        result.Should().BeOfType<SendAndWaitResult.Queued>();
        adapter.Verify(a => a.Interrupt(), Times.Once());
        _sut.GetPending(session.Id).Should().ContainSingle();
    }

    // --- Гонка TOCTOU очереди: автодоставка при постановке в момент завершения хода ---
    //
    // SendMessageAsync/SendMessageAndWaitAsync читают Info.Status БЕЗ лока, а EnqueuePendingAsync
    // делает Add ПОД PendingLock. Между ними окно: ход завершился (ResultMessage → OnMessageAsync →
    // DrainNextPendingAsync отработал по ЕЩЁ ПУСТОЙ очереди, статус упал в Active), и новое сообщение
    // встаёт в очередь при СВОБОДНОМ чате — триггер автодоступы (по result) уже стрелял. Фикс: в
    // EnqueuePendingAsync после Add, под локом, проверить переход очереди 0→1 при свободном статусе
    // и форсировать DrainNextPendingAsync (идемпотентен). Проверяем сам инвариант white-box'ом:
    // приватный EnqueuePendingAsync + заглушка адаптера (чтобы доставка не запускала claude.exe).

    [Fact]
    public async Task EnqueuePending_СвободныйЧат_ФорсируетДоставкуИОчищаетОчередь()
    {
        // Симуляция окна result: ход «только что» завершился, статус Active, очередь пуста.
        // Без фикса сообщение зависло бы в очереди до следующего действия пользователя.
        var session = await MkBusySessionAsync("race", SessionStatus.Active);
        session.Name = "есть имя"; // иначе фоновый уточнятор заголовка полезет в локальную модель
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        // Детерминированный сигнал доставки: адаптер зажигает TCS в момент вызова. Poll по
        // очереди (как и по invocations) здесь не годится — DrainNextPendingAsync изымает
        // сообщение (RemoveAt → очередь пуста) ДО вызова adapter.SendMessageAsync, а под
        // нагрузкой между RemoveAt и вызовом адаптера проходит значимое время. TCS ловит
        // сам факт доставки без гонок и таймаутных окон.
        var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()))
            .Callback(() => delivered.TrySetResult(true))
            .Returns(Task.CompletedTask);

        var result = await InvokeEnqueuePendingAsync(session.Id, entry, "зависшее сообщение");

        result.Should().BeOfType<SendAndWaitResult.Queued>().Which.Position.Should().Be(1);
        // DrainNextPendingAsync работает в fire-and-forget Task.Run: очередь пустеет
        // (RemoveAt) ДО фактической доставки (Process.SendMessageAsync). Ждём не пустоту
        // очереди, а сам вызов адаптера — иначе Verify ловит гонку.
        await WaitForAdapterCallAsync(adapter, TimeSpan.FromSeconds(2));

        _sut.GetPending(session.Id).Should().BeEmpty(
            "при свободном чате форсированный drain разбирает очередь сам, без зависания");
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.AtLeastOnce());
    }

    [Fact]
    public async Task EnqueuePending_ЗанятыйЧат_НеФорсируетДоставку()
    {
        // Регрессия: при Working/Waiting доставка не форсируется — сообщение ждёт конца хода
        // (result разберёт очередь). Иначе нормальная очередь дёргала бы процесс на каждом Add.
        var session = await MkBusySessionAsync("race-busy", SessionStatus.Working);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        var result = await InvokeEnqueuePendingAsync(session.Id, entry, "ждёт хода");

        result.Should().BeOfType<SendAndWaitResult.Queued>();
        await Task.Delay(150); // drain не запущен — за это время ничего не должно поменяться
        _sut.GetPending(session.Id).Should().ContainSingle().Which.Text.Should().Be("ждёт хода");
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Never());
    }

    [Fact]
    public async Task EnqueuePending_ЗамороженнаяОчередь_НеФорсируетДоставку()
    {
        // Регрессия: «Стоп» заморозил очередь (QueueFrozen). Даже при свободном чате
        // авто-доставка запрещена — возобновляет только новое пользовательское сообщение.
        var session = await MkBusySessionAsync("race-frozen", SessionStatus.Active);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        SetQueueFrozen(entry, true);

        var result = await InvokeEnqueuePendingAsync(session.Id, entry, "в заморозке");

        result.Should().BeOfType<SendAndWaitResult.Queued>();
        await Task.Delay(150);
        _sut.GetPending(session.Id).Should().ContainSingle().Which.Text.Should().Be("в заморозке");
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Never());
    }

    [Fact]
    public async Task Pending_РестартСервера_ОчередьВосстановленаВТомЖеПорядке()
    {
        var session = await MkBusySessionAsync("persist-rt");
        session.Name = "есть имя";

        await _sut.SendOrEnqueueAsync(session.Id, "доклад исполнителя", senderPersonaId: "exec-1",
            silent: true, suppressTasksExecute: true, staffNote: "служебная заметка",
            kind: SessionManager.PendingKind.Report);
        await _sut.SendMessageAsync(session.Id, "вопрос человека", []);

        File.Exists(PendingFilePath(session.Id)).Should().BeTrue("очередь записана на диск при постановке");

        var restarted = BuildSecondManager();
        var restored = restarted.GetPending(session.Id);

        restored.Select(m => m.Text).Should().Equal("доклад исполнителя", "вопрос человека");
        restored[0].Kind.Should().Be(SessionManager.PendingKind.Report);
        restored[0].StaffNote.Should().Be("служебная заметка");
        restored[0].Silent.Should().BeTrue();
        restored[0].SuppressTasksExecute.Should().BeTrue();
        restored[0].SenderPersonaId.Should().Be("exec-1");
        restored[1].Kind.Should().Be(SessionManager.PendingKind.User);
    }

    [Fact]
    public async Task Pending_ОчередьРазобрана_ФайлУдалён()
    {
        var session = await MkBusySessionAsync("persist-empty");
        await _sut.SendOrEnqueueAsync(session.Id, "доклад", kind: SessionManager.PendingKind.Report);
        File.Exists(PendingFilePath(session.Id)).Should().BeTrue();

        var entry = GetEntry(session.Id);
        var pending = (System.Collections.IList)entry.GetType().GetField("Pending")!.GetValue(entry)!;
        pending.RemoveAt(0);

        File.Exists(PendingFilePath(session.Id)).Should().BeFalse("пустая очередь — файла нет");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Pending_АвтодренажНаСтарте_ЧатБезХодаПолучаетСообщение()
    {
        var session = await MkBusySessionAsync("persist-drain");
        await _sut.SendOrEnqueueAsync(session.Id, "доклад до рестарта", kind: SessionManager.PendingKind.Report);

        var restarted = BuildSecondManager();
        var entry = GetEntryOf(restarted, session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        await restarted.DrainRestoredPendingAsync();
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(3));

        adapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("доклад до рестарта")), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once());
        restarted.GetPending(session.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task Pending_ЗамороженнаяОчередь_ПослеРестартаНеРазбираетсяАвтоматически()
    {
        var session = await MkBusySessionAsync("persist-frozen");
        var entry = GetEntry(session.Id);
        SetProcess(entry, StubAdapter(entry).Object);
        await _sut.SendOrEnqueueAsync(session.Id, "ждёт возобновления", kind: SessionManager.PendingKind.Report);

        _sut.Interrupt(session.Id); // «Стоп» замораживает очередь
        await WaitForComposerRestoresAsync(1);

        var restarted = BuildSecondManager();
        var restartedEntry = GetEntryOf(restarted, session.Id);
        restartedEntry.GetType().GetField("QueueFrozen")!.GetValue(restartedEntry).Should().Be(true);
        var adapter = StubAdapter(restartedEntry);
        SetProcess(restartedEntry, adapter.Object);

        await restarted.DrainRestoredPendingAsync();
        await Task.Delay(200);

        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Never());
        restarted.GetPending(session.Id).Should().ContainSingle().Which.Text.Should().Be("ждёт возобновления");
    }

    [Fact]
    public async Task Pending_СтаршеСуток_НеОживляетсяАВИсторииПлашкаНеДоставлено()
    {
        var session = await MkBusySessionAsync("persist-expired");
        var old = new SessionManager.QueuedMessage(Guid.NewGuid().ToString("N"), "недельной давности", null, null,
            0, DateTime.UtcNow - TimeSpan.FromDays(7), Kind: SessionManager.PendingKind.Report);
        _historyService.SavePending(session.Id, new PendingQueueFile(false, [old]));

        var restarted = BuildSecondManager();
        var entry = GetEntryOf(restarted, session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        restarted.GetPending(session.Id).Should().BeEmpty("просроченное не оживляем");
        await restarted.DrainRestoredPendingAsync();

        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Never());
        File.Exists(PendingFilePath(session.Id)).Should().BeFalse();
        var history = await _historyService.LoadAsync(session.Id);
        history.OfType<StoredWorkLoopStoppedMessage>()
            .Should().ContainSingle(m => m.Reason == "pending_expired" && m.Text.Contains("Не доставлено"));
    }

    // --- FreezePending и CurrentTurnSnapshot (фикс бага композера) ---

    [Fact]
    public async Task FreezePending_AfterDelivery_ГаситCurrentTurnSnapshot()
    {
        // Симулируем прерванный пользовательский ход: задаём CurrentTurnSnapshot напрямую
        var session = await MkBusySessionAsync("freeze", SessionStatus.Working);
        var entry = GetEntry(session.Id);
        SetProcess(entry, StubAdapter(entry).Object); // ход идёт — иначе «Стоп» уходит в реанимацию

        SetCurrentTurnSnapshot(entry, "текст прерванного хода", ["file.txt"], "plan");

        _sut.Interrupt(session.Id);
        var restores = await WaitForComposerRestoresAsync(1);

        restores.Should().ContainSingle()
            .Which.Text.Should().Be("текст прерванного хода");

        // Повторный Interrupt не должен resurrect старый текст
        _sut.Interrupt(session.Id);
        restores = await WaitForComposerRestoresAsync(2);

        restores.Last().Text.Should().BeNull("snapshot должен быть погашен после доставки restore");
    }

    [Fact]
    public async Task FreezePending_ПовторныйВызовПриПогашенномSnapshot_НеResurrectТекст()
    {
        var session = await MkBusySessionAsync("freeze2", SessionStatus.Working);
        var entry = GetEntry(session.Id);
        SetProcess(entry, StubAdapter(entry).Object); // ход идёт — иначе «Стоп» уходит в реанимацию

        SetCurrentTurnSnapshot(entry, "старый текст", ["old.txt"], "auto");

        _sut.Interrupt(session.Id);
        var restores = await WaitForComposerRestoresAsync(1);
        restores.Should().ContainSingle().Which.Text.Should().Be("старый текст");

        _sut.Interrupt(session.Id);
        restores = await WaitForComposerRestoresAsync(2);

        restores.Last().Text.Should().BeNull("повторный FreezePending не должен resurrect текст");
    }

    [Fact]
    public async Task FreezePending_БезSnapshot_ВозвращаетRestoreСNullТекстом()
    {
        var session = await MkBusySessionAsync("freeze3", SessionStatus.Working);
        var entry = GetEntry(session.Id);
        SetProcess(entry, StubAdapter(entry).Object); // ход идёт — иначе «Стоп» уходит в реанимацию

        _sut.Interrupt(session.Id);
        var restores = await WaitForComposerRestoresAsync(1);

        restores.Should().ContainSingle()
            .Which.Text.Should().BeNull("прерван авто/агентский ход — восстанавливать нечего");
    }

    [Fact]
    public async Task Pipeline_МедленныйКлиент_ОбработчикВозвращаетсяБыстро_ПорядокСохранён()
    {
        var session = AddPipelineSession();
        var acc = new TurnAccumulator(new List<StoredMessage>());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _broadcaster.Gate = _ => release.Task;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var feed = Task.Run(async () =>
        {
            await _sut.OnMessageQueuedAsync(session.Id, acc, new TextDeltaMessage("a"));
            await _sut.OnMessageQueuedAsync(session.Id, acc, new TextDeltaMessage("b"));
            await _sut.OnMessageQueuedAsync(session.Id, acc, new TextDeltaMessage("c"));
        });
        await feed.WaitAsync(TimeSpan.FromSeconds(5)); // при синхронной рассылке повисло бы на гейте
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        Sent<TextDeltaMessage>().Should().BeEmpty("клиент ещё не принял ни одного кадра");

        release.SetResult();
        await _sut.DrainPipelineAsync(session.Id);

        Sent<TextDeltaMessage>().Select(m => m.Text).Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task Pipeline_МедленныйНаблюдатель_ResultДоходитПоследним_ПослеВсехПредыдущих()
    {
        var session = AddPipelineSession(executor: true);
        var acc = new TurnAccumulator(new List<StoredMessage>());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<string>();
        _sut.OnSessionMessage += async (_, msg) =>
        {
            await release.Task;
            lock (seen) seen.Add(msg.GetType().Name);
        };

        await _sut.OnMessageQueuedAsync(session.Id, acc, new TextDeltaMessage("x"))
            .WaitAsync(TimeSpan.FromSeconds(5));
        await _sut.OnMessageQueuedAsync(session.Id, acc, new ToolUseMessage("t1", "Read", JsonDocument.Parse("{}").RootElement))
            .WaitAsync(TimeSpan.FromSeconds(5));
        await _sut.OnMessageQueuedAsync(session.Id, acc, new ResultMessage("success", 1, 1, null, null))
            .WaitAsync(TimeSpan.FromSeconds(5));
        lock (seen) seen.Should().BeEmpty("наблюдатель ещё не отпущен");

        release.SetResult();
        await _sut.DrainPipelineAsync(session.Id);

        lock (seen) seen.Should().Equal(nameof(TextDeltaMessage), nameof(ToolUseMessage), nameof(ResultMessage));
    }

    [Fact]
    public async Task Pipeline_УдалениеСессии_НеБлокируетВызывающего_ДренажВФонеДоводитResult()
    {
        var session = AddPipelineSession(executor: true);
        var acc = new TurnAccumulator(new List<StoredMessage>());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<string>();
        _sut.OnSessionMessage += async (_, msg) =>
        {
            await release.Task;
            lock (seen) seen.Add(msg.GetType().Name);
        };
        await _sut.OnMessageQueuedAsync(session.Id, acc, new TextDeltaMessage("x"));
        await _sut.OnMessageQueuedAsync(session.Id, acc, new ResultMessage("success", 1, 1, null, null));

        // Удаление не держит HTTP-путь на дренаже: возвращается, пока наблюдатель ещё держит Result
        await _sut.DeleteAsync(session.Id).WaitAsync(TimeSpan.FromSeconds(5));
        lock (seen) seen.Should().BeEmpty("наблюдатель ещё не отпущен");

        release.SetResult();
        // Дренаж идёт в фоне и доводит Result до наблюдателя
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (seen) if (seen.Count == 2) break;
            await Task.Delay(20);
        }

        lock (seen) seen.Should().Equal(nameof(TextDeltaMessage), nameof(ResultMessage));
    }

    [Fact]
    public async Task Pipeline_Переполнение_ДельтыСклеиваются_ТекстНеТеряется_ToolUseИResultВПорядке()
    {
        var session = AddPipelineSession();
        var acc = new TurnAccumulator(new List<StoredMessage>());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _broadcaster.Gate = _ =>
        {
            started.TrySetResult();
            return release.Task;
        };

        // Первая дельта повисает у «клиента», остальные заполняют очередь и сверх ёмкости сливаются
        await _sut.OnMessageQueuedAsync(session.Id, acc, new TextDeltaMessage("0"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Дельты не блокируют писателя: при полной очереди лишние сливаются. Потолок ожидания
        // превращает «зависло на back-pressure» (слив сломан) в красный тест, а не в повисший прогон
        await Task.Run(async () =>
        {
            for (var i = 1; i < 5000; i++)
                await _sut.OnMessageQueuedAsync(session.Id, acc, new TextDeltaMessage(i.ToString() + ","));
        }).WaitAsync(TimeSpan.FromSeconds(5));

        // Редкие кадры не сливаются и писателя не блокируют: кладутся даже в «полную» очередь
        await Task.Run(async () =>
        {
            await _sut.OnMessageQueuedAsync(session.Id, acc, new ToolUseMessage("t1", "Read", JsonDocument.Parse("{}").RootElement));
            await _sut.OnMessageQueuedAsync(session.Id, acc, new ResultMessage("success", 1, 1, null, null));
        }).WaitAsync(TimeSpan.FromSeconds(5));

        release.SetResult();
        await _sut.DrainPipelineAsync(session.Id);

        // Ни одного символа не потеряно: слитые дельты склеены в том же порядке
        var expected = "0" + string.Concat(Enumerable.Range(1, 4999).Select(i => i.ToString() + ","));
        string.Concat(Sent<TextDeltaMessage>().Select(m => m.Text)).Should().Be(expected);
        _sut.PipelineForTest(session.Id)!.Coalesced.Should().BeGreaterThan(0, "дельты сверх ёмкости слиты, а не выброшены");
        var all = Sent<ServerMessage>();
        var toolIdx = all.FindIndex(m => m is ToolUseMessage);
        var resultIdx = all.FindIndex(m => m is ResultMessage);
        toolIdx.Should().BeGreaterThan(-1, "ToolUse не теряется");
        resultIdx.Should().BeGreaterThan(toolIdx, "Result дошёл после ToolUse");
        all.FindLastIndex(m => m is TextDeltaMessage).Should().BeLessThan(toolIdx);
    }

    [Fact]
    public async Task Pipeline_ОдинПотребитель_ПорядокНеЛомаетсяЗадержкойПервогоКадра()
    {
        var session = AddPipelineSession();
        var acc = new TurnAccumulator(new List<StoredMessage>());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _broadcaster.Gate = async msg =>
        {
            if (msg is TextDeltaMessage { Text: "1" }) await release.Task;
            else second.TrySetResult();
        };

        await _sut.OnMessageQueuedAsync(session.Id, acc, new TextDeltaMessage("1"));
        await _sut.OnMessageQueuedAsync(session.Id, acc, new TextDeltaMessage("2"));
        await _sut.OnMessageQueuedAsync(session.Id, acc, new TextDeltaMessage("3"));
        // Второй потребитель (если бы он был) разобрал бы «2» не дожидаясь «1»
        await Task.WhenAny(second.Task, Task.Delay(300));
        second.Task.IsCompleted.Should().BeFalse("второй потребитель не разбирает «2» мимо зависшего «1»");
        release.SetResult();
        await _sut.DrainPipelineAsync(session.Id);

        Sent<TextDeltaMessage>().Select(m => m.Text).Should().Equal("1", "2", "3");
    }

    [Fact]
    public async Task Pipeline_НаблюдательКладётКадрыВТуЖеСессию_ТупикаНет()
    {
        var session = AddPipelineSession(executor: true);
        var acc = new TurnAccumulator(new List<StoredMessage>());
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int Count = 2500;
        _sut.OnSessionMessage += async (_, msg) =>
        {
            if (msg is not ResultMessage) return;
            // Из потока потребителя кладём в очередь той же сессии больше ёмкости несливаемых кадров
            for (var i = 0; i < Count; i++)
                await _sut.OnMessageQueuedAsync(session.Id, acc, new ToolUseMessage("t" + i, "Read", JsonDocument.Parse("{}").RootElement));
            finished.TrySetResult();
        };

        await _sut.OnMessageQueuedAsync(session.Id, acc, new ResultMessage("success", 1, 1, null, null));

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await _sut.DrainPipelineAsync(session.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Sent<ToolUseMessage>().Should().HaveCount(Count, "несливаемые кадры не теряются");
    }
}
