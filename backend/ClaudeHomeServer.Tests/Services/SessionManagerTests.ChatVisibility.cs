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

        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome
            .Should().Be(ChatVisibilityOutcome.HasMessages, "после false вернуть приватность нельзя");
        session.PrivateToUserId.Should().BeNull();
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

    // Парная сторона: тумблер, в свою очередь, обязан ждать замок видимости — проверка «сообщений
    // нет» и установка приватности вне замка пропустили бы отправку, занявшую чат между ними.
    [Fact]
    public async Task ТумблерЖдётЗамокВидимости_ПроверкаСообщенийИУстановкаПодОдним()
    {
        var session = await SharedChatAsync("toggle-waits");
        var lk = VisibilityLockOf(session.Id);
        Task<ChatVisibilityOutcome> toggle;

        lock (lk)
        {
            toggle = Task.Run(() => _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome);
            Thread.Sleep(400);
            toggle.IsCompleted.Should().BeFalse("тумблер обязан ждать замок, под которым первое сообщение занимает чат");
            session.PrivateToUserId.Should().BeNull();
        }

        (await toggle.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(ChatVisibilityOutcome.Ok);
        session.PrivateToUserId.Should().Be(TestUserId);
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

    // S2d-7a доработка 1, пункт 1: перевод в приватный снимает чужие соединения с session-группы
    // (содержимое группы не фильтруется адресацией) и шлёт группе проекта событие без содержимого.
    [Fact]
    public async Task ВПриватный_ВышибаетЧужихИзSessionГруппы_АвторНеВышибается_ИшлётСобытие()
    {
        var session = await SharedChatAsync("evict");
        _broadcaster.EvictableConnections.Add("conn-участник");
        _sut.AddViewer(session.Id, "conn-участник");
        _sut.AddViewer(session.Id, "conn-автор");

        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome.Should().Be(ChatVisibilityOutcome.Ok);
        await _sut.EvictForeignViewersAsync(session.Id, TestUserId);

        var call = _broadcaster.EvictCalls.Should().ContainSingle().Subject;
        call.SessionId.Should().Be(session.Id);
        call.UserIds.Should().Contain(Участник).And.NotContain(TestUserId, "автора не вышибаем");
        _broadcaster.Inner.Project.Select(p => p.Message).OfType<Protocol.ChatVisibilityChangedMessage>()
            .Should().ContainSingle(m => m.SessionId == session.Id && m.Private);
        // реестр зрителей почищен по снятым соединениям: вышибленный не глушит уведомления автору
        _sut.HasViewers(session.Id).Should().BeTrue("автор остался зрителем");
        _sut.RemoveViewer(session.Id, "conn-автор");
        _sut.HasViewers(session.Id).Should().BeFalse("вышибленный участник из реестра убран");
    }

    // Пункт 2: карточка ленты (модуль/штаб) уходит мимо LastMessage и MessageCount, но чат пустым
    // уже не считается — 409, а отметка переживает рестарт (лежит в Session).
    [Fact]
    public async Task КарточкаЛентыЧерезAppendStored_ЧатЗанят_ТумблерОтказывает()
    {
        var session = await SharedChatAsync("card");

        await _sut.AppendModuleRecordAsync(session.Id,
            new Protocol.StoredModuleRecord { Module = "imageeditor", RecordType = "done", Fallback = "готово" });

        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome
            .Should().Be(ChatVisibilityOutcome.HasMessages);
        session.PrivateToUserId.Should().BeNull();
        session.MessageClaimed.Should().BeTrue("отметка в Session переживает рестарт процесса");
    }

    // Пункт 3: доставка отложенного сообщения после рестарта (DrainRestoredPendingAsync →
    // DeliverPendingAsync → SendDirectAsync) идёт мимо трёх входов с отметкой — теперь занимает чат сама.
    // Порядок «тумблер первым»: доставка ждёт замок видимости, уходит по приватной адресации.
    [Fact]
    public async Task ВосстановленнаяОчередь_ДоставкаЖдётЗамок_ТумблерПервым_ГруппеПроектаНичего()
    {
        var session = await SharedChatAsync("restored-queue");
        var entry = GetEntry(session.Id);
        var lk = VisibilityLockOf(session.Id);
        var queued = new SessionManager.QueuedMessage("q1", "после рестарта", null, null, 0, DateTime.UtcNow,
            Kind: SessionManager.PendingKind.User, SenderUserId: TestUserId);
        var deliver = typeof(SessionManager).GetMethod("DeliverPendingAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Task send;
        ChatVisibilityOutcome toggled;

        lock (lk)
        {
            send = Task.Run(() => (Task)deliver.Invoke(_sut, [session.Id, entry, queued])!);
            Thread.Sleep(400);
            send.IsCompleted.Should().BeFalse("доставка из очереди обязана ждать тот же замок видимости");
            toggled = _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome;
        }
        await send.WaitAsync(TimeSpan.FromSeconds(10));

        toggled.Should().Be(ChatVisibilityOutcome.Ok);
        ProjectEventsOf(session.Id).Should().Be(0, "приватный чат: группе проекта события не адресуются");
    }

    // Та же доставка первой: тумблер отказывает, а отметка стоит в Session.
    [Fact]
    public async Task ВосстановленнаяОчередь_ДоставкаПервой_Тумблер409()
    {
        var session = await SharedChatAsync("restored-queue-first");
        var entry = GetEntry(session.Id);
        var queued = new SessionManager.QueuedMessage("q2", "после рестарта", null, null, 0, DateTime.UtcNow,
            Kind: SessionManager.PendingKind.User, SenderUserId: TestUserId);
        var deliver = typeof(SessionManager).GetMethod("DeliverPendingAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        await (Task)deliver.Invoke(_sut, [session.Id, entry, queued])!;

        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome
            .Should().Be(ChatVisibilityOutcome.HasMessages);
        session.MessageClaimed.Should().BeTrue();
    }

    // S2d-6c: пока чат приватный, opt-out «Не сохранять решения» не выключается — иначе
    // паспорта приватного разговора попали бы в общую «Историю решений» проекта.
    [Fact]
    public async Task ПриватныйЧат_СнятьExcludeFromDossiers_Нельзя_ФлагОстаётсяTrue()
    {
        var session = await SharedChatAsync("optout-priv");
        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome
            .Should().Be(ChatVisibilityOutcome.Ok);

        _sut.TrySetExcludeFromDossiers(session.Id, false).Should().BeFalse("приватный чат не выходит в «Историю решений»");
        session.ExcludeFromDossiers.Should().BeTrue("отказ не меняет флаг");

        // true принимается и в приватном: opt-out остаётся включённым
        _sut.TrySetExcludeFromDossiers(session.Id, true).Should().BeTrue();
        session.ExcludeFromDossiers.Should().BeTrue();
    }

    // Пустой PrivateToUserId (старые записи без автора) — тоже приватность: false не принимается,
    // флаг остаётся как был.
    [Fact]
    public async Task ПриватныйЧат_ПустойPrivateToUserId_ТожеОтказ()
    {
        var session = await SharedChatAsync("optout-empty");
        session.PrivateToUserId = "";

        _sut.TrySetExcludeFromDossiers(session.Id, false).Should().BeFalse("пустая строка — тоже приватный чат");
        session.ExcludeFromDossiers.Should().BeFalse("отказ не меняет флаг");
    }

    // Общий чат: автор сам решает. После приватного периода (флаг true по тумблеру) снятие
    // приватности остаётся возможным, и только тогда автор может выключить флаг — явное согласие.
    [Fact]
    public async Task ОбщийЧат_СнятьExcludeFromDossiers_Можно_ИПослеСнятияПриватности()
    {
        var session = await SharedChatAsync("optout-shared");

        _sut.TrySetExcludeFromDossiers(session.Id, true).Should().BeTrue();
        _sut.TrySetExcludeFromDossiers(session.Id, false).Should().BeTrue("общий чат: opt-out выключается");
        session.ExcludeFromDossiers.Should().BeFalse();

        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome
            .Should().Be(ChatVisibilityOutcome.Ok);
        _sut.TrySetExcludeFromDossiers(session.Id, false).Should().BeFalse("пока приватный — нельзя");
        session.ExcludeFromDossiers.Should().BeTrue();

        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: false).Outcome
            .Should().Be(ChatVisibilityOutcome.Ok);
        _sut.TrySetExcludeFromDossiers(session.Id, false).Should().BeTrue("после снятия — явное согласие автора");
        session.ExcludeFromDossiers.Should().BeFalse();
    }

    // Гонка S2d-6c: тумблер «в приватный» и снятие флага идут параллельно под одним VisibilityLock.
    // Инвариант после каждой пары: PrivateToUserId != null ⇒ ExcludeFromDossiers == true.
    // Чтение признака приватности вне замка дало бы быстрое «не приватный» в запись false после
    // тумблера — приватный чат с выключенным флагом. Каждой итерации возвращается свежее
    // состояние пустого неприватного чата (сообщений у него никогда не было — путь HasMessages
    // не задействуется).
    [Fact]
    public async Task ГонкаПриватностьИСнятиеФлага_ИнвариантВоВсех500Парах()
    {
        var session = await SharedChatAsync("race-optout");
        for (var i = 0; i < 500; i++)
        {
            session.PrivateToUserId = null;
            session.ExcludeFromDossiers = false;
            using var go = new ManualResetEventSlim(false);

            var toggle = Task.Run(() => { go.Wait(); return _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome; });
            var optOut = Task.Run(() => { go.Wait(); return _sut.TrySetExcludeFromDossiers(session.Id, false); });
            go.Set();
            var toggled = await toggle.WaitAsync(TimeSpan.FromSeconds(10));
            var accepted = await optOut.WaitAsync(TimeSpan.FromSeconds(10));

            toggled.Should().Be(ChatVisibilityOutcome.Ok, $"итерация {i}: пустой чат становится приватным");
            if (session.PrivateToUserId is not null)
                session.ExcludeFromDossiers.Should().BeTrue($"итерация {i}: приватный чат с выключенным флагом");
            if (!accepted)
                session.PrivateToUserId.Should().NotBeNullOrEmpty($"итерация {i}: отказ возможен только на приватном");
        }
    }
    // Гонка S2d-7b-3 (доработка 1): тумблер «в приватный» ‖ запись состава. Проверка приватности и
    // запись Participants — под одним VisibilityLock, как проверка IsTeamOrGroup в тумблере.
    // Инвариант после каждой пары: НЕ (приватный И есть состав). Исход один из двух: либо чат
    // приватный (состав отклонён PrivateChatModeException), либо общий со составом (тумблер TeamOrGroup).
    [Fact]
    public async Task ГонкаПриватностьИСостав_ПриватныйЧатБезУчастников_Во200Парах()
    {
        var session = await SharedChatAsync("race-participants");
        var a = MkPersona("Гонка-А", null, session.ProjectId!);
        var b = MkPersona("Гонка-Б", null, session.ProjectId!);
        for (var i = 0; i < 200; i++)
        {
            session.PrivateToUserId = null;
            session.ExcludeFromDossiers = false;
            session.Participants = null;
            using var go = new ManualResetEventSlim(false);

            var toggle = Task.Run(() => { go.Wait(); return _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome; });
            var setSet = Task.Run(() =>
            {
                go.Wait();
                try { _sut.SetParticipants(session.Id, TestUserId, [a.Id, b.Id]); return true; }
                catch (PrivateChatModeException) { return false; }
            });
            go.Set();
            await toggle.WaitAsync(TimeSpan.FromSeconds(10));
            await setSet.WaitAsync(TimeSpan.FromSeconds(10));

            (session.PrivateToUserId is not null && session.Participants is { Count: > 0 })
                .Should().BeFalse($"итерация {i}: приватный чат с составом персон");
        }
    }

    // То же для штаба: тумблер ‖ SetTeamImplementAsync(enabled). Запись TeamImplement идёт
    // через шов ядра под VisibilityLock; приватный чат TeamImplement не получает.
    [Fact]
    public async Task ГонкаПриватностьИШтаб_ПриватныйЧатБезTeamImplement_Во200Парах()
    {
        var session = await SharedChatAsync("race-team");
        var coordinator = MkPersona("Координатор-гонка", null, session.ProjectId!);
        var executor = MkPersona("Исполнитель-гонка", null, session.ProjectId!);
        for (var i = 0; i < 200; i++)
        {
            session.PrivateToUserId = null;
            session.ExcludeFromDossiers = false;
            session.TeamImplement = null;
            using var go = new ManualResetEventSlim(false);

            var toggle = Task.Run(() => { go.Wait(); return _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome; });
            var enable = Task.Run(async () =>
            {
                go.Wait();
                try { await _sut.SetTeamImplementAsync(session.Id, true, coordinatorPersonaId: coordinator.Id,
                    executorPersonaIds: [executor.Id], userId: TestUserId); return true; }
                catch (PrivateChatModeException) { return false; }
            });
            go.Set();
            await toggle.WaitAsync(TimeSpan.FromSeconds(10));
            await enable.WaitAsync(TimeSpan.FromSeconds(10));

            (session.PrivateToUserId is not null && session.TeamImplement is not null)
                .Should().BeFalse($"итерация {i}: приватный чат стал штабом");
        }
    }
}
