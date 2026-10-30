using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Реестр актора хода (S2a-1): кто запустил идущий ход чата. Пишется в двух точках подачи,
// «липкий» на прогон, пока живы фоновые агенты; при смешении акторов выигрывает сужение.
public partial class SessionManagerTests
{
    private const string Участник = "u-participant";
    private const string ДругойУчастник = "u-participant-2";
    private const string ТретийУчастник = "u-participant-3";

    private void NoteActor(string sessionId, string? actor, bool fromHuman)
    {
        var entry = GetEntry(sessionId);
        typeof(SessionManager).GetMethod("NoteTurnActor",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_sut, [entry, actor, fromHuman]);
    }

    [Fact]
    public async Task ПодачаАгента_ЗаписываетАктора_ВходящийХодВиденДоКонцаХода()
    {
        var (session, fake) = await RunChatAsync("ta-submit");

        await _sut.SendMessageAndWaitAsync(session.Id, "из чата", TimeSpan.Zero, senderUserId: Участник);

        _sut.GetTurnActor(session.Id, TestUserId).Should().Be(Участник, "ход в полёте — актор подачи");

        EndTurn(session, fake);
        _sut.GetTurnActor(session.Id, TestUserId).Should().BeNull("без фоновых агентов актор после хода не живёт");
    }

    [Fact]
    public async Task ПодачаЧеловека_ЧерезSendMessage_ЗаписываетАктора()
    {
        var (session, fake) = await RunChatAsync("ta-human");
        fake.BgAlive = true;

        await _sut.SendMessageAsync(session.Id, "привет", [], senderUserId: Участник);

        _sut.GetTurnActor(session.Id, TestUserId).Should().Be(Участник);
    }

    [Fact]
    public async Task ВладелецЧужогоЧата_НеВидитАктора_ПосторонниймУказавшийЧужойЧат_Null()
    {
        var (session, _) = await RunChatAsync("ta-owner");
        await _sut.SendMessageAndWaitAsync(session.Id, "из чата", TimeSpan.Zero, senderUserId: Участник);

        _sut.GetTurnActor(session.Id, "чужой-владелец").Should().BeNull("владение — через GetOwned");
        _sut.GetTurnActor("нет-такого-чата", TestUserId).Should().BeNull();
    }

    [Fact]
    public async Task ФоновыйАгентЖив_АктороМУчастникаПослеХода_ЛипнетДоЗавершенияАгентов()
    {
        var (session, fake) = await RunChatAsync("ta-sticky");
        await _sut.SendMessageAndWaitAsync(session.Id, "из чата", TimeSpan.Zero, senderUserId: Участник);
        fake.BgAlive = true;
        EndTurn(session, fake);

        _sut.GetTurnActor(session.Id, TestUserId).Should().Be(Участник, "агенты живы — актор прогона держится");

        fake.BgAlive = false;
        _sut.GetTurnActor(session.Id, TestUserId).Should().BeNull("агентов не осталось — актор снят");
    }

    [Fact]
    public async Task СмешениеАкторов_ВыигрываетСужение_ПодачаАрендатораНеРасширяетПрогон()
    {
        var (session, fake) = await RunChatAsync("ta-narrow");
        await _sut.SendMessageAndWaitAsync(session.Id, "из чата", TimeSpan.Zero, senderUserId: Участник);
        fake.BgAlive = true;
        EndTurn(session, fake);

        // авто-ход арендатора поверх прогона участника: ход идёт от арендатора, но прогон остаётся узким
        await _sut.SendMessageAsync(session.Id, "автоматика", [], auto: true, senderUserId: TestUserId);
        _sut.GetTurnActor(session.Id, TestUserId).Should().Be(Участник,
            "в прогоне был не-арендатор, пока его агенты живы — сужение побеждает");
    }

    [Fact]
    public async Task ПодачаЧеловека_ПокаЖивыАгентыУчастника_НеРасширяетПрогон_ПотомНачинаетНовый()
    {
        var (session, fake) = await RunChatAsync("ta-reset");
        await _sut.SendMessageAndWaitAsync(session.Id, "из чата", TimeSpan.Zero, senderUserId: Участник);
        fake.BgAlive = true;
        EndTurn(session, fake);

        await _sut.SendMessageAsync(session.Id, "дальше я", [], senderUserId: TestUserId);
        _sut.GetTurnActor(session.Id, TestUserId).Should().Be(Участник,
            "агенты участника живы — человеческая подача арендатора прогон не расширяет (канал хаба)");

        await _sut.SendMessageAndWaitAsync(session.Id, "из чата REST", TimeSpan.Zero, senderUserId: TestUserId);
        _sut.GetTurnActor(session.Id, TestUserId).Should().Be(Участник, "REST-канал даёт тот же результат");

        fake.BgAlive = false;
        EndTurn(session, fake);
        await _sut.SendMessageAsync(session.Id, "теперь точно я", [], senderUserId: TestUserId);
        _sut.GetTurnActor(session.Id, TestUserId).Should().Be(TestUserId,
            "агентов не осталось — подача человека начинает новую ответственность");
    }

    [Fact]
    public async Task ВнутреннийХодБезИнициатора_НаследуетАктораПрогона_ИначеНеизвестен()
    {
        var (session, fake) = await RunChatAsync("ta-internal");
        await _sut.SendMessageAndWaitAsync(session.Id, "из чата", TimeSpan.Zero, senderUserId: Участник);
        fake.BgAlive = true;
        EndTurn(session, fake);

        await _sut.SendMessageAsync(session.Id, "nudge", [], auto: true);
        _sut.GetTurnActor(session.Id, TestUserId).Should().Be(Участник, "внутренний ход наследует RunActor");

        fake.BgAlive = false;
        EndTurn(session, fake);
        await _sut.SendMessageAsync(session.Id, "ещё один", [], auto: true);
        _sut.GetTurnActor(session.Id, TestUserId).Should().BeNull("наследовать нечего — актор неизвестен (Narrow)");
    }

    // Гонка «подача из очереди»: сообщение другого участника ждало в очереди, пока шёл ход первого.
    // По разбору очереди актором хода обязан стать отправитель сообщения из очереди, а не прежний.
    [Fact]
    public async Task ПодачаИзОчереди_АкторомСтановитсяОтправительСообщения()
    {
        var (session, fake) = await RunChatAsync("ta-queue", SessionStatus.Active);
        await _sut.SendMessageAsync(session.Id, "первый ход", [], senderUserId: Участник);
        session.Status = SessionStatus.Working; // ход первого участника в полёте
        _sut.GetTurnActor(session.Id, TestUserId).Should().Be(Участник);

        await _sut.SendMessageAsync(session.Id, "второй из очереди", [], senderUserId: ДругойУчастник);
        _sut.GetPending(session.Id).Should().ContainSingle()
            .Which.SenderUserId.Should().Be(ДругойУчастник, "очередь хранит автора сообщения");
        _sut.GetTurnActor(session.Id, TestUserId).Should().Be(Участник, "идущий ход остаётся за первым");

        EndTurn(session, fake);
        await (Task)typeof(SessionManager).GetMethod("DrainNextPendingAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_sut, [session.Id])!;

        _sut.GetTurnActor(session.Id, TestUserId).Should().Be(ДругойУчастник, "подача из очереди записала своего актора");
    }

    // Конкурентная запись: пока писатели гоняют подачи арендатора и участников, читатели (потоки
    // MCP-вызовов) никогда не увидят арендатора поверх прогона участника, а итог не расширяется.
    [Fact]
    public async Task КонкурентныеПодачи_АрендаторНеРасширяетПрогонУчастника()
    {
        var (session, fake) = await RunChatAsync("ta-race");
        fake.BgAlive = true;
        NoteActor(session.Id, Участник, fromHuman: false);

        var stop = false;
        var widened = 0;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
                if (_sut.GetTurnActor(session.Id, TestUserId) is { } a && a == TestUserId)
                    Interlocked.Increment(ref widened);
        })).ToArray();
        var writers = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 500; n++)
                NoteActor(session.Id, (i + n) % 3 == 0 ? TestUserId : i % 2 == 0 ? Участник : ДругойУчастник,
                    fromHuman: false);
        })).ToArray();
        await Task.WhenAll(writers);
        Volatile.Write(ref stop, true);
        await Task.WhenAll(readers);

        widened.Should().Be(0, "ни одно чтение не видело арендатора поверх прогона участника");
        _sut.GetTurnActor(session.Id, TestUserId).Should().NotBe(TestUserId).And.NotBeNull();
    }

    // Рамка: «чата нет / чужой» — Narrow (fail-closed), чат вне общего проекта — Wide
    [Fact]
    public async Task Рамка_НеизвестныйИЧужойЧат_Narrow_ЛичныйПроект_Wide()
    {
        var (session, _) = await RunChatAsync("ta-frame");

        _sut.GetTurnFrame(session.Id, TestUserId).Should().Be(TurnFrameKind.Wide, "личный проект — рамка широкая");
        _sut.GetTurnFrame(null, TestUserId).Should().Be(TurnFrameKind.Narrow, "вызов без заголовка");
        _sut.GetTurnFrame("нет-такого", TestUserId).Should().Be(TurnFrameKind.Narrow);
        _sut.GetTurnFrame(session.Id, "чужой-владелец").Should().Be(TurnFrameKind.Narrow, "чужой чат");
    }

    [Fact]
    public async Task Рамка_ОбщийПроект_УзкаяДляВсех_ДажеДляВладельца()
    {
        var (session, _) = await RunChatAsync("ta-frame-shared");
        var project = _projectManager.GetById(session.ProjectId!)!;
        project.Members = [new ProjectMember { UserId = Участник, Role = ProjectRole.Editor, AddedBy = TestUserId }];

        _sut.GetTurnFrame(session.Id, TestUserId).Should().Be(TurnFrameKind.Narrow);
        await _sut.SendMessageAndWaitAsync(session.Id, "из чата", TimeSpan.Zero, senderUserId: TestUserId);
        _sut.GetTurnFrame(session.Id, TestUserId).Should().Be(TurnFrameKind.Narrow, "решение 29.10");
    }
}

// S2a-10a: участник-редактор ведёт ход в чате общего проекта. Очередь под PendingLock хранит
// отправителя каждого сообщения; «автор хода» и «автор сообщения в очереди» — основание, по которому
// редактор останавливает свой ход и снимает своё сообщение, а чужое нет.
public partial class SessionManagerTests
{
    [Fact]
    public async Task ГонкаДвухУчастников_ВИдущийХод_ОбаВОчереди_КаждыйСоСвоимОтправителем()
    {
        var (session, _) = await RunChatAsync("ta-two-queue", SessionStatus.Active);
        await _sut.SendMessageAsync(session.Id, "ход первого", [], senderUserId: Участник);
        session.Status = SessionStatus.Working;

        var start = new TaskCompletionSource();
        var senders = new[] { Участник, ДругойУчастник };
        var sends = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await start.Task;
            await _sut.SendMessageAsync(session.Id, $"сообщение {i}", [], senderUserId: senders[i % 2]);
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(sends);

        var pending = _sut.GetPending(session.Id);
        pending.Should().HaveCount(8, "ни одно сообщение гонки не потеряно");
        pending.Count(p => p.SenderUserId == Участник).Should().Be(4);
        pending.Count(p => p.SenderUserId == ДругойУчастник).Should().Be(4);
        pending.All(p => p.Text.Length > 0 && (p.SenderUserId == senders[int.Parse(p.Text.Split(' ')[1]) % 2]))
            .Should().BeTrue("отправитель не перепутан между участниками");
        _sut.GetTurnActor(session.Id, TestUserId).Should().Be(Участник, "идущий ход остался за первым");
    }

    [Fact]
    public async Task АвторХода_ВладелецИАвторДа_ДругойУчастникИНеизвестныйНет()
    {
        var (session, _) = await RunChatAsync("ta-author");
        await _sut.SendMessageAndWaitAsync(session.Id, "из чата", TimeSpan.Zero, senderUserId: Участник);

        _sut.IsOwnerOrTurnAuthor(session.Id, TestUserId).Should().BeTrue("владелец управляет любым ходом");
        _sut.IsOwnerOrTurnAuthor(session.Id, Участник).Should().BeTrue("автор хода");
        _sut.IsOwnerOrTurnAuthor(session.Id, ДругойУчастник).Should().BeFalse("чужой ход не его");
        _sut.IsOwnerOrTurnAuthor("нет-такого", Участник).Should().BeFalse();
    }

    // Конкурентность (S2a-10b): две одновременные подачи от разных участников и попытка остановки
    // третьим. Авторство читается из реестра под тем же PendingLock, под которым пишется подача
    // (NoteTurnActor / GetTurnActor), — поэтому третий участник не становится автором чужого хода
    // ни в один момент гонки, а автор идущего хода и владелец остаются. Без Task.Delay: подачи
    // стартуют по одному сигналу TaskCompletionSource.
    [Fact]
    public async Task ГонкаДвухПодач_ОстановкаТретьимОтказана_АвторИВладелецПроходят()
    {
        var (session, _) = await RunChatAsync("ta-stop-race", SessionStatus.Active);
        await _sut.SendMessageAsync(session.Id, "ход первого", [], senderUserId: Участник);
        session.Status = SessionStatus.Working; // ход первого в полёте

        var stopReading = 0;
        var thirdWasAuthor = 0;
        var third = Task.Run(() =>
        {
            while (Volatile.Read(ref stopReading) == 0)
                if (_sut.IsOwnerOrTurnAuthor(session.Id, ТретийУчастник)) Interlocked.Increment(ref thirdWasAuthor);
        });

        var start = new TaskCompletionSource();
        var senders = new[] { Участник, ДругойУчастник };
        var sends = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await start.Task;
            await _sut.SendMessageAsync(session.Id, $"сообщение {i}", [], senderUserId: senders[i % 2]);
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(sends);
        Volatile.Write(ref stopReading, 1);
        await third;

        thirdWasAuthor.Should().Be(0, "третий участник ни в один момент гонки не был автором чужого хода");

        _sut.GetPending(session.Id).Should().HaveCount(8, "ни одна подача гонки не потеряна");
        _sut.GetTurnActor(session.Id, TestUserId).Should().Be(Участник, "идущий ход остался за его автором");
        _sut.IsOwnerOrTurnAuthor(session.Id, Участник).Should().BeTrue("автор идущего хода");
        _sut.IsOwnerOrTurnAuthor(session.Id, ДругойУчастник).Should().BeFalse(
            "сообщение в очереди не делает человека автором чужого хода");
        _sut.IsOwnerOrTurnAuthor(session.Id, ТретийУчастник).Should().BeFalse("ни подачи, ни членства — отказ");
        _sut.IsOwnerOrTurnAuthor(session.Id, TestUserId).Should().BeTrue("владелец останавливает любой ход");
    }

    [Fact]
    public async Task ОтправительВОчереди_ВидитсяПоИдентификаторуСообщения()
    {
        var (session, _) = await RunChatAsync("ta-pending-sender", SessionStatus.Active);
        await _sut.SendMessageAsync(session.Id, "ход", [], senderUserId: Участник);
        session.Status = SessionStatus.Working;
        await _sut.SendMessageAsync(session.Id, "от второго", [], senderUserId: ДругойУчастник);

        var queued = _sut.GetPending(session.Id).Single();
        _sut.PendingSentBy(session.Id, queued.Id).Should().Be(ДругойУчастник);
        _sut.PendingSentBy(session.Id, "нет-такого").Should().BeNull();
    }
}
