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

public class SessionManagerWorkLoopTests : SessionManagerTestBase
{
    [Fact]
    public async Task ContinueWorkLoop_ЛимитИтераций_ЯвноеСообщениеИСниманиеЦикла()
    {
        // Имитируем возврат из waiting: Phase=waiting руками, после return ContinueWorkLoopAsync
        // инкрементирует Iteration, проверяет MaxIterations и стопит. До правки цикл жёг
        // итерации на КАЖДОМ ходе — Phase=working+Iteration=2 хватало. Теперь инкремент
        // привязан к возврату, поэтому нужен явный Phase=waiting, иначе счётчик не сдвинется.
        var session = await MkBusySessionAsync("loop-limit", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var loop = _sut.GetById(session.Id)!.WorkLoop!;
        loop.MaxIterations = 3;
        loop.Iteration = 2; // следующий возврат из waiting (++ → 3) упрётся в лимит
        loop.Phase = "waiting";
        var entry = GetEntry(session.Id);
        SetLoopTurnInFlight(entry, true);

        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForConditionAsync(() => _sut.GetById(session.Id)!.WorkLoop is null, TimeSpan.FromSeconds(2));

        _sut.GetById(session.Id)!.WorkLoop.Should().BeNull();
        var msg = Sent<WorkLoopStoppedMessage>().Should().ContainSingle().Subject;
        msg.Reason.Should().Be("limit");
        msg.Text.Should().Contain("3 ходов");
    }

    [Fact]
    public async Task ContinueWorkLoop_ОшибкаХода_ЯвноеСообщениеИСниманиеЦикла()
    {
        var session = await MkBusySessionAsync("loop-error", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        SetLoopTurnInFlight(entry, true);

        // ErrorMessage выставляет LoopTurnFailed=true (SessionManager.OnMessageAsync) —
        // именно эта ветка ContinueWorkLoopAsync тестируется
        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ErrorMessage("подписка недоступна"), TestRunId);
        await WaitForConditionAsync(() => _sut.GetById(session.Id)!.WorkLoop is null, TimeSpan.FromSeconds(2));

        _sut.GetById(session.Id)!.WorkLoop.Should().BeNull();
        var msg = Sent<WorkLoopStoppedMessage>().Should().ContainSingle().Subject;
        msg.Reason.Should().Be("error");
        msg.Text.Should().Be("Цикл остановлен: ход завершился ошибкой.");
    }

    [Fact]
    public async Task ContinueWorkLoop_НайденПромис_ПереходитВVerifying_БезСообщенияОстановки()
    {
        // Переход в verifying — штатный шаг цикла (не остановка с неясным исходом), поэтому
        // WorkLoopStoppedMessage тут не шлётся, а WorkLoop остаётся активным
        var session = await MkBusySessionAsync("loop-verify", SessionStatus.Working);
        session.Name = "есть имя";
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        SetLoopTurnInFlight(entry, true);

        var loop = _sut.GetById(session.Id)!.WorkLoop!;
        lock (entry.GetType().GetField("LoopTurnLock")!.GetValue(entry)!)
        {
            var buf = (System.Text.StringBuilder)entry.GetType().GetField("LoopTurnText")!.GetValue(entry)!;
            buf.Append($"готово <promise>{loop.Promise}</promise>");
        }

        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForConditionAsync(() => _sut.GetById(session.Id)?.WorkLoop?.Phase == "verifying",
            TimeSpan.FromSeconds(2));

        _sut.GetById(session.Id)!.WorkLoop.Should().NotBeNull();
        _sut.GetById(session.Id)!.WorkLoop!.Phase.Should().Be("verifying");
        Sent<WorkLoopStoppedMessage>().Should().BeEmpty();
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));
        adapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("ВЕРИФИКАЦИЯ")), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once());
    }

    [Fact]
    public async Task ОбрывФоновогоСабагента_ПаспортBgDoneПослеВыходаПроцесса_УходитДобивание()
    {
        // Сценарий ревью: bg_done → паспорт отложен на перепроверку → координатор дописал ход →
        // result (статус Active) → процесс CLI вышел → Dispose ватчера отдаёт паспорт с bg_done.
        // В этот момент entry.Process — живой адаптер (он переживает выход CLI), ход не в полёте,
        // и NoteTruncatedBgAgent обязан дойти до директивы, а не ограничиться пометкой
        var session = await MkBusySessionAsync("bg-cut-after-exit", SessionStatus.Active);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        SubagentSink(session.Id)(CutBgPassport(session.Id));

        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(5));
        adapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("обрывок") && t.Contains("1/2")), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once());
        _subagentRuns.Latest("a-cut")!.NudgeAttempts.Should().Be(1);
    }

    [Fact]
    public async Task ОбрывФоновогоСабагента_ХодВПолёте_ТолькоПометка_БезВторогоХода()
    {
        var session = await MkBusySessionAsync("bg-cut-in-flight", SessionStatus.Working);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        SubagentSink(session.Id)(CutBgPassport(session.Id));

        // Ветка «ход идёт» выходит до планирования отправки — проверка без ожидания честная
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Never());
        entry.GetType().GetField("TruncatedBgNote")!.GetValue(entry).Should().NotBeNull();
        entry.GetType().GetField("TruncatedSubagent")!.GetValue(entry).Should().NotBeNull();
    }

    [Fact]
    public async Task ОбрывСинхронногоСабагента_ЦиклДоГотовоАктивен_ОставляетПометкуВместоМолчания()
    {
        // Инцидент 25.08.2026 (чат «Зависание чатов практики»): синхронный Task вернул координатору
        // обрывок вместо отчёта, добивание уступило активному циклу «до готово» — и отметка просто
        // гасилась. Обрыв не оставлял следа ВООБЩЕ: ни директивы, ни предупреждения.
        var session = await MkBusySessionAsync("cut-loop-note", SessionStatus.Working);
        session.Name = "есть имя";
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        // finishedBy=tool_result — синхронный Task: NoteTruncatedBgAgent не зовётся,
        // отметка ждёт result хода координатора
        SubagentSink(session.Id)(CutBgPassport(session.Id, finishedBy: "tool_result"));
        entry.GetType().GetField("TruncatedSubagent")!.GetValue(entry).Should().NotBeNull();

        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);

        entry.GetType().GetField("TruncatedSubagent")!.GetValue(entry).Should().BeNull();
        ((ClaudeHomeServer.Services.Llm.SubagentRunPassport?)entry.GetType()
            .GetField("TruncatedBgNote")!.GetValue(entry))!.AgentId.Should().Be("a-cut");
        _subagentRuns.Latest("a-cut")!.NudgeAttempts.Should().Be(0,
            "добивание уступает циклу — у него свой протокол продолжения");
    }

    [Fact]
    public async Task ОбрывСинхронногоСабагента_ЦиклПоднимаетСледующийХод_ПометкаУезжаетСНим()
    {
        // Сквозной путь: пометка обязана лечь ДО того, как цикл поднимет следующий ход,
        // иначе опоздает ровно на ход (а на последней итерации потеряется совсем) — ради
        // этого блок разбора обрыва стоит перед разбором цикла в OnMessageAsync
        var session = await MkBusySessionAsync("cut-loop-carry", SessionStatus.Working);
        session.Name = "есть имя";
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        SetLoopTurnInFlight(entry, true);

        SubagentSink(session.Id)(CutBgPassport(session.Id, finishedBy: "tool_result"));
        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);

        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(5));
        adapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("обрывок") && t.Contains("НЕ итог")),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Once());
    }

    [Fact]
    public async Task ДобиваниеСабагента_ОбрывОпровергнутПокаПланировалось_ДирективаНеУходит()
    {
        var session = await MkBusySessionAsync("bg-cut-late-final", SessionStatus.Active);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        var cut = CutBgPassport(session.Id);
        // Пока добивание планировалось, ватчер прислал опровержение: последний паспорт штатный
        _subagentRuns.Record(cut with { Truncated = false, LastStopReason = "end_turn" });

        var nudge = (Task)typeof(SessionManager).GetMethod("NudgeTruncatedSubagentAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_sut, [session.Id, cut, 1])!;
        await nudge;

        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Never());
        _subagentRuns.Latest("a-cut")!.NudgeAttempts.Should().Be(0);
    }

    [Fact]
    public async Task ОбрывФоновогоСабагента_ОпровергнутШтатнымОтчётом_ПометкиСняты()
    {
        var session = await MkBusySessionAsync("bg-cut-refuted", SessionStatus.Working);
        var entry = GetEntry(session.Id);
        SetProcess(entry, StubAdapter(entry).Object);
        var sink = SubagentSink(session.Id);

        sink(CutBgPassport(session.Id, agentId: "a-other"));
        sink(CutBgPassport(session.Id, agentId: "a-cut"));
        // Штатный отчёт ЧУЖОГО агента пометку a-cut не трогает
        sink(CutBgPassport(session.Id, agentId: "a-other", truncated: false));
        ((ClaudeHomeServer.Services.Llm.SubagentRunPassport?)entry.GetType()
            .GetField("TruncatedBgNote")!.GetValue(entry))!.AgentId.Should().Be("a-cut");
        ((ClaudeHomeServer.Services.Llm.SubagentRunPassport?)entry.GetType()
            .GetField("TruncatedSubagent")!.GetValue(entry))!.AgentId.Should().Be("a-cut");

        // Финал a-cut доехал до транскрипта позже сигнала — ватчер прислал опровержение:
        // обе пометки сняты, ложная директива по result и префикс координатору не уйдут
        sink(CutBgPassport(session.Id, agentId: "a-cut", truncated: false));
        entry.GetType().GetField("TruncatedBgNote")!.GetValue(entry).Should().BeNull();
        entry.GetType().GetField("TruncatedSubagent")!.GetValue(entry).Should().BeNull();
    }

    [Fact]
    public async Task SetWorkLoop_РучнойСтоп_ШлётЯвноеСообщение()
    {
        var session = await MkBusySessionAsync("loop-manual-stop", SessionStatus.Active);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);

        await _sut.SetWorkLoopAsync(session.Id, enabled: false, userId: TestUserId, manual: true);

        var msg = Sent<WorkLoopStoppedMessage>().Should().ContainSingle().Subject;
        msg.Reason.Should().Be("manual");
        msg.Text.Should().Be("Цикл остановлен вами. Текущий ход продолжает работу.");
    }

    [Fact]
    public async Task SetWorkLoop_Включение_НеШлётСообщениеОстановки()
    {
        var session = await MkBusySessionAsync("loop-manual-on", SessionStatus.Active);

        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId, manual: true);

        Sent<WorkLoopStoppedMessage>().Should().BeEmpty("это включение, а не остановка");
    }

    // Major 3: ContinueWorkLoopAsync взводит LoopTurnInFlight под PendingLock до отправки
    // директивы продолжения. Пользовательское сообщение, пришедшее в момент отправки директивы
    // (гонка «гейт пройден → user → drain»), НЕ должно пускать второй ход в тот же процесс —
    // оно ждёт конца continuation-итерации как следующая итерация.
    [Fact]
    public async Task ContinueWorkLoop_ВзводитМаркерДоОтправки_ОчередьНеДублируетХод()
    {
        var session = await MkBusySessionAsync("loop-race-gate", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        var continuationSent = 0;
        adapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()))
            .Callback<string, IReadOnlyList<string>, int, bool, string?>((t, _, _, _, _) =>
            {
                if (!t.Contains("СИСТЕМНАЯ ДИРЕКТИВА — ЦИКЛ")) return;
                continuationSent++;
                // В момент вызова адаптера (директива отправляется) маркер уже взведён —
                // имитируем гонку: пользователь кладёт сообщение. Drain по нему уступить должен.
                EnqueueUser(entry, "опоздавшая вводная");
            })
            .Returns(Task.CompletedTask);
        SetProcess(entry, adapter.Object);
        SetLoopTurnInFlight(entry, true); // ход-итерация в полёте

        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));

        continuationSent.Should().Be(1, "директива продолжения отправлена ровно один раз");
        GetLoopTurnInFlight(entry).Should().BeTrue("continuation-итерация помечена в полёте");
        // Опоздавшее сообщение НЕ доставлено вторым ходом параллельно директиве — ждёт в очереди.
        adapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("опоздавшая вводная")), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Never());
        _sut.GetPending(session.Id).Should().Contain(m => m.Text.Contains("опоздавшая вводная"),
            "сообщение осталось в очереди до конца continuation-итерации");
    }

    // Minor 5: выключение цикла разбирает агентские сообщения, скопившиеся за время цикла
    // (они ждали конца ВСЕГО цикла), — не дожидаясь следующего пользовательского хода.
    [Fact]
    public async Task SetWorkLoop_Выключение_РазбираетАгентскиеСообщенияИзОчереди()
    {
        var session = await MkBusySessionAsync("loop-drain-on-disable", SessionStatus.Active);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        await InvokeEnqueuePendingAsync(session.Id, entry, "доклад из цикла");
        _sut.GetPending(session.Id).Should().ContainSingle("агентское сообщение скопилось за цикл");

        await _sut.SetWorkLoopAsync(session.Id, enabled: false, userId: TestUserId, manual: true);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));

        adapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("доклад из цикла")), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once(),
            "Minor 5: агентское сообщение доставляется при выключении цикла");
        _sut.GetPending(session.Id).Should().BeEmpty();
    }

    // Minor 6: drain при активном цикле, свободном маркере и пустой user-очереди (напр. ход
    // прерван, сообщение из очереди удалено до exited) — планирует ContinueWorkLoopAsync,
    // иначе цикл висел бы «активным» без движения.
    [Fact]
    public async Task Drain_АктивныйЦиклПустаяОчередь_ПродолжаетЦиклДирективой()
    {
        var session = await MkBusySessionAsync("loop-drain-empty", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        // Прерванный ход: exited этого прогона разберёт очередь, но сообщения в ней нет (удалили),
        // а маркер свободен (interrupt сбросил) — без Minor 6 цикл завис бы.
        entry.GetType().GetField("DrainOnExitedRun")!.SetValue(entry, TestRunId);
        SetLoopTurnInFlight(entry, false);

        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry), new ExitedMessage(), TestRunId);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));

        adapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("СИСТЕМНАЯ ДИРЕКТИВА — ЦИКЛ")), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once(),
            "Minor 6: drain продолжает цикл директивой при пустой user-очереди");
    }

    [Fact]
    public async Task ContinueWorkLoop_ЕстьЖиваяДелегированнаяЗадача_УходитВWaiting()
    {
        // Координатор запустил исполнителя и ждёт доклада: цикл НЕ жжёт итерации, а уходит
        // в фазу waiting. LoopTurnInFlight обязан быть снят под PendingLock — иначе drain
        // вечно уступает на гейте, и доклад не доедет НИКОГДА. ГРАБЛЯ, отдельный тест.
        var session = await MkBusySessionAsync("loop-waiting-1", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        WithLiveDelegated();
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        SetLoopTurnInFlight(entry, true); // ход-итерация в полёте
        var loopBefore = _sut.GetById(session.Id)!.WorkLoop!;
        var iterBefore = loopBefore.Iteration;
        var phaseBefore = loopBefore.Phase;

        try
        {
            await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
                new ResultMessage("success", 10, 1, null, null), TestRunId);
            // Даём фоновой ContinueWorkLoopAsync дойти до ухода в waiting
            await WaitForConditionAsync(() => _sut.GetById(session.Id)?.WorkLoop?.Phase == "waiting",
                TimeSpan.FromSeconds(2));

            var loop = _sut.GetById(session.Id)!.WorkLoop!;
            loop.Phase.Should().Be("waiting", "фаза переключается на ожидание исполнителя");
            loop.Iteration.Should().Be(iterBefore, "итерация не выросла в фазе waiting");
            GetLoopTurnInFlight(entry).Should().BeFalse("LoopTurnInFlight снят — иначе drain вечно уступает");
            // Директива продолжения НЕ ушла — её заменил уход в ожидание
            adapter.Verify(a => a.SendMessageAsync(
                It.Is<string>(t => t.Contains("СИСТЕМНАЯ ДИРЕКТИВА — ЦИКЛ")),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never());
            phaseBefore.Should().Be("working", "санити: до теста фаза была working");
        }
        finally
        {
            _liveDelegated = false;
            _sut.HasLiveDelegatedTasks = null;
        }
    }

    [Fact]
    public async Task ContinueWorkLoop_ДелегатНеЗадан_ЧистыйWorking_БезВозвратаТратитИтерацию()
    {
        // Без делегата HasLiveDelegatedTasks (тесты без DI к TaskManager) ждать нечего —
        // цикл НЕ уходит в waiting, значит нет возврата. Но счётчик всё равно растёт:
        // инкремент живёт в двух точках — блок возврата waiting→working (здесь не сработает)
        // и конец ContinueWorkLoopAsync (сработает на обычном ходе). Смысл теста: проверить,
        // что счётчик не привязан жёстко к ожиданию — обычная работа тоже его крутит.
        var session = await MkBusySessionAsync("loop-waiting-nodelegate", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        _sut.HasLiveDelegatedTasks.Should().BeNull("санити: делегат не задан");
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        SetLoopTurnInFlight(entry, true);
        var iterBefore = _sut.GetById(session.Id)!.WorkLoop!.Iteration;

        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));

        _sut.GetById(session.Id)!.WorkLoop.Should().NotBeNull("цикл продолжается");
        _sut.GetById(session.Id)!.WorkLoop!.Phase.Should().Be("working");
        _sut.GetById(session.Id)!.WorkLoop!.Iteration.Should().Be(iterBefore + 1,
            "чистый working-ход без возврата — инкремент в конце ContinueWorkLoopAsync");
    }

    [Fact]
    public async Task ContinueWorkLoop_ПриходДоклада_ВозвращаетWorkingИПродолжаетЦикл()
    {
        // Сквозной путь: координатор ушёл в waiting → пришёл Report (TaskExecutionService.
        // ReportToDelegatorAsync) → drain подхватил его, доставил ход-реакцию → по result
        // ContinueWorkLoopAsync переключает фазу обратно в working и шлёт директиву.
        var session = await MkBusySessionAsync("loop-waiting-resume", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        WithLiveDelegated();
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        SetLoopTurnInFlight(entry, true);

        try
        {
            // Шаг 1: result хода-итерации → ContinueWorkLoopAsync уходит в waiting
            await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
                new ResultMessage("success", 10, 1, null, null), TestRunId);
            await WaitForConditionAsync(() => _sut.GetById(session.Id)?.WorkLoop?.Phase == "waiting",
                TimeSpan.FromSeconds(2));
            var loopAfterWaiting = _sut.GetById(session.Id)!.WorkLoop!;
            var iterInWaiting = loopAfterWaiting.Iteration;
            // Эмитируем «доклад пришёл»: выключаем делегат (задача закрыта), и кладём Report
            // в очередь через SendOrEnqueueAsync (тот же канал, что у TaskExecutionService).
            _liveDelegated = false;
            // Чат между итерациями (Active) — SendOrEnqueueAsync идёт прямой отправкой
            session.Status = SessionStatus.Active;
            ClearSent();
            await _sut.SendOrEnqueueAsync(session.Id, "[доклад] исполнитель закончил",
                silent: true, suppressTasksExecute: true,
                staffNote: "доклад", kind: SessionManager.PendingKind.Report);
            // Доклад ушёл в ленту, реакция поставщика поднимает следующий ход-реакцию.
            // Ход-реакция идёт через SendMessageAsync(auto=true) — адаптер получит её.
            await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));
            // Эмулируем result хода-реакции: триггерит ContinueWorkLoopAsync,
            // который должен вернуть фазу в working и поднять следующую итерацию.
            SetLoopTurnInFlight(entry, true);
            await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
                new ResultMessage("success", 10, 1, null, null), TestRunId);
            // Ждём именно директиву продолжения — иначе гонка: Phase переключается в начале
            // ContinueWorkLoopAsync, а Iteration++ — после; ранний assert увидит «working, 0».
            await WaitForConditionAsync(() => adapter.Invocations.Any(i =>
                    i.Method.Name == nameof(ILlmSessionAdapter.SendMessageAsync)
                    && i.Arguments.Count > 0
                    && i.Arguments[0] is string s
                    && s.Contains("СИСТЕМНАЯ ДИРЕКТИВА — ЦИКЛ")),
                TimeSpan.FromSeconds(2));
            var loopResumed = _sut.GetById(session.Id)!.WorkLoop!;
            loopResumed.Phase.Should().Be("working", "возврат из ожидания");
            loopResumed.Iteration.Should().BeGreaterThan(iterInWaiting, "итерация продвинулась");
        }
        finally
        {
            _liveDelegated = false;
            _sut.HasLiveDelegatedTasks = null;
        }
    }

    [Fact]
    public async Task SendOrEnqueue_ДокладВСвободныйЧатПриАктивномЦикле_ДоставляетсяСразу()
    {
        // Между итерациями (статус Active) координаторский цикл «до готово» ещё активен.
        // Доклад (Report) должен идти СРАЗУ, а не ждать конца ВСЕГО цикла — иначе
        // цикл жжёт итерации, не видя доклада. Это и есть «сломанный замок» из задачи.
        var session = await MkBusySessionAsync("loop-report-direct", SessionStatus.Active);
        session.Name = "есть имя";
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        ClearSent();

        // Чат между итерациями — SendOrEnqueueAsync идёт прямой отправкой.
        var deferred = await _sut.SendOrEnqueueAsync(session.Id, "реакция на доклад",
            silent: true, suppressTasksExecute: true,
            staffNote: "Доклад по задаче передан постановщику",
            kind: SessionManager.PendingKind.Report);

        deferred.Should().BeFalse("чат свободен — отправка прямая, не в очередь");
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));
        adapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("реакция на доклад")), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once());
    }

    [Fact]
    public async Task SendOrEnqueue_ОбычноеАгентскоеВСвободныйЧатПриАктивномЦикле_ТожеСразу()
    {
        // Контраст: посторонний агент при свободном чате (между итерациями) идёт сразу,
        // как и раньше — гейт «WorkLoop is null || kind is User or Report» пропускает.
        // Сюда важно положить «обычное» — чат НЕ в waiting, делегат пуст, цикл просто работает.
        var session = await MkBusySessionAsync("loop-agent-direct", SessionStatus.Active);
        session.Name = "есть имя";
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        ClearSent();

        var deferred = await _sut.SendOrEnqueueAsync(session.Id, "посторонний агент пишет",
            silent: true, suppressTasksExecute: true, kind: SessionManager.PendingKind.Agent);

        deferred.Should().BeFalse();
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));
        adapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("посторонний агент пишет")), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once());
    }

    [Fact]
    public async Task ContinueWorkLoop_ВозвратИзWaitingВНачалеПродолжаетЦикл()
    {
        // Упрощённый сценарий без имитации SendOrEnqueue: имитируем возврат через прямой
        // вызов ContinueWorkLoopAsync после того, как фаза уже была переключена в waiting.
        // Цикл сам на старте переключает фазу обратно в working и шлёт директиву.
        var session = await MkBusySessionAsync("loop-waiting-resume-2", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        // Принудительно ставим фазу waiting (имитация уже отработавшего ухода)
        _sut.GetById(session.Id)!.WorkLoop!.Phase = "waiting";
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        SetLoopTurnInFlight(entry, true);
        var iterBefore = _sut.GetById(session.Id)!.WorkLoop!.Iteration;

        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));

        var loop = _sut.GetById(session.Id)!.WorkLoop!;
        loop.Phase.Should().Be("working", "вход в ContinueWorkLoopAsync возвращает фазу в working");
        loop.Iteration.Should().BeGreaterThan(iterBefore, "итерация продвинулась");
    }

    // --- Детектор маркера <waiting>: симметрично блокеру. Покрывает парсер в изоляции,
    // без поднятия сессии и адаптера — это «чистый» юнит, как у блокера (последний покрыт
    // ровно так же в общем тесте TryExtractBlockedMarker_* — если он добавлен позже).
    [Theory]
    [InlineData("жду ответа\n<waiting>ответа chats_send</waiting>\n", "ответа chats_send")]
    [InlineData("просто текст без маркера", null)]
    [InlineData("внутри ```\n<waiting>в коде</waiting>\n``` не считается", null)]
    [InlineData("инлайн `<waiting>в инлайне</waiting>` тоже нет", null)]
    [InlineData("<waiting></waiting>", "EMPTY_OK")] // пустой тег — валиден, reason=null
    [InlineData("<WAITING>верхний регистр не считается</WAITING>", null)] // регистр точный
    [InlineData("<waiting лишний пробел>нет</waiting>", null)] // симметрия с блокером
    public void TryExtractWaitingMarker_РазличныеТексты(string text, string? expectedReason)
    {
        var ok = SessionManager.TryExtractWaitingMarker(text, out var reason);
        if (expectedReason is null)
            ok.Should().BeFalse($"маркер не должен опознаться: {text}");
        else if (expectedReason == "EMPTY_OK")
        {
            // Пустой тег валиден: true + reason == null (симметрия с блокером).
            ok.Should().BeTrue();
            reason.Should().BeNull("пустой тег — валидный waiting без уточнения причины");
        }
        else
        {
            ok.Should().BeTrue();
            reason.Should().Be(expectedReason);
        }
    }

    // Обрезка до 300 символов — отдельным кейсом, чтобы не возиться с подсчётом длины в теории.
    // Внутри маркера кладём 400 'a', после схлопывания \s+ — те же 400 (пробелов нет),
    // обрезка до 300, начало — "аааа…".
    [Fact]
    public void TryExtractWaitingMarker_ОбрезкаДо300Символов()
    {
        var text = "<waiting>" + new string('а', 400) + "</waiting>";
        var ok = SessionManager.TryExtractWaitingMarker(text, out var reason);
        ok.Should().BeTrue();
        reason.Should().NotBeNull().And.HaveLength(300);
        reason!.Should().Be(new string('а', 300));
    }

    [Fact]
    public async Task ContinueWorkLoop_НайденWaitingМаркер_УходитВWaiting()
    {
        // Координатор сам сказал «жду» через маркер `<waiting>` — цикл уходит в фазу waiting
        // с сохранённой причиной, без расхода итерации, снимая LoopTurnInFlight (иначе drain
        // вечно уступает). Причина и момент входа сохраняются — тик ожидания ими будет жить.
        var session = await MkBusySessionAsync("loop-waiting-marker", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        SetLoopTurnInFlight(entry, true);
        var iterBefore = _sut.GetById(session.Id)!.WorkLoop!.Iteration;

        lock (entry.GetType().GetField("LoopTurnLock")!.GetValue(entry)!)
        {
            var buf = (System.Text.StringBuilder)entry.GetType().GetField("LoopTurnText")!.GetValue(entry)!;
            buf.Append("жду ответа <waiting>ответа chats_send с сессии X</waiting>");
        }

        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForConditionAsync(() => _sut.GetById(session.Id)?.WorkLoop?.Phase == "waiting",
            TimeSpan.FromSeconds(2));

        var loop = _sut.GetById(session.Id)!.WorkLoop!;
        loop.Phase.Should().Be("waiting");
        loop.Iteration.Should().Be(iterBefore, "итерация не растёт в фазе waiting");
        loop.WaitingReason.Should().Be("ответа chats_send с сессии X",
            "причина из маркера сохраняется — она нужна тикам и логу");
        loop.WaitingSince.Should().NotBeNull("момент входа в фазу нужен тикам и персистентности");
        loop.WaitingTicks.Should().Be(0, "на входе счётчик стартует с нуля");
        GetLoopTurnInFlight(entry).Should().BeFalse("LoopTurnInFlight снят — иначе drain вечно уступает");
        adapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("СИСТЕМНАЯ ДИРЕКТИВА — ЦИКЛ")),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never(),
            "директива продолжения не должна уйти — её заменил уход в ожидание");
    }

    [Fact]
    public async Task ContinueWorkLoop_WaitingИPromise_VerifyingВыигрывает()
    {
        // ПОРЯДОК ВАЖЕН (см. комментарий в ContinueWorkLoopAsync): вывести оба — противоречие,
        // и верификационный ход разрешит его дешевле, чем лишний круг ожидания.
        var session = await MkBusySessionAsync("loop-waiting-promise", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        SetLoopTurnInFlight(entry, true);

        var loop = _sut.GetById(session.Id)!.WorkLoop!;
        lock (entry.GetType().GetField("LoopTurnLock")!.GetValue(entry)!)
        {
            var buf = (System.Text.StringBuilder)entry.GetType().GetField("LoopTurnText")!.GetValue(entry)!;
            buf.Append($"готово <promise>{loop.Promise}</promise> <waiting>заодно</waiting>");
        }

        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForConditionAsync(() => _sut.GetById(session.Id)?.WorkLoop?.Phase == "verifying",
            TimeSpan.FromSeconds(2));

        var after = _sut.GetById(session.Id)!.WorkLoop!;
        after.Phase.Should().Be("verifying", "промис проверяется раньше маркера waiting");
        after.WaitingReason.Should().BeNull("при ожидании не зашли — промис выиграл");
    }

    [Fact]
    public async Task ContinueWorkLoop_WaitingИBlocked_BlockedВыигрывает()
    {
        // Симметрично: блокер раньше промиса, блокер раньше waiting. Противоречие
        // («жду» + «не могу без человека») — стоп, человек разберёт.
        var session = await MkBusySessionAsync("loop-waiting-blocked", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        SetLoopTurnInFlight(entry, true);

        lock (entry.GetType().GetField("LoopTurnLock")!.GetValue(entry)!)
        {
            var buf = (System.Text.StringBuilder)entry.GetType().GetField("LoopTurnText")!.GetValue(entry)!;
            buf.Append("<blocked>нужен человек</blocked> <waiting>вдобавок жду</waiting>");
        }

        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForConditionAsync(() => _sut.GetById(session.Id)!.WorkLoop is null,
            TimeSpan.FromSeconds(2));

        _sut.GetById(session.Id)!.WorkLoop.Should().BeNull("блокер стопит цикл раньше waiting");
        var msg = Sent<WorkLoopStoppedMessage>().Should().ContainSingle().Subject;
        msg.Reason.Should().Be("blocked");
        msg.Text.Should().Contain("нужен человек");
    }

    [Fact]
    public async Task TickWaitingLoops_СвободныйЧат_ШлётДирективуИРаститСчётчик()
    {
        // Тик — это фоновая системная директива-тик, отправленная координатору; Iteration
        // НЕ растёт (тик не тратит итерации), WaitingTicks — растёт. Чат между итерациями
        // (Active), без живого прогона, без LoopTurnInFlight — тик проходит.
        var session = await MkBusySessionAsync("loop-tick-send", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        // StubAdapter по умолчанию HasLiveTurn=true (изображает идущий ход) — для тика
        // нужен СВОБОДНЫЙ чат. Сбрасываем явно, иначе HasLiveTurnProcess заблокирует тик.
        adapter.SetupGet(a => a.HasLiveTurn).Returns(false);
        SetProcess(entry, adapter.Object);
        // Вручную переводим фазу в waiting по маркеру (имитация уже отработавшего ухода)
        var loop0 = _sut.GetById(session.Id)!.WorkLoop!;
        loop0.Phase = "waiting";
        loop0.WaitingReason = "ответа с чужой сессии";
        loop0.WaitingSince = DateTime.UtcNow;
        loop0.WaitingTicks = 0;
        // Чат между итерациями (не Working/Waiting), чтобы тик прошёл
        session.Status = SessionStatus.Active;
        SetLoopTurnInFlight(entry, false);
        SetWaitingTickInterval(TimeSpan.Zero);
        SetMaxWaitingTicks(5);
        BackdateWaitingSince(session.Id, TimeSpan.FromMinutes(10));

        ClearSent();
        await _sut.TickWaitingLoopsAsync();
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));

        var loop = _sut.GetById(session.Id)!.WorkLoop!;
        loop.Iteration.Should().Be(loop0.Iteration, "тик НЕ тратит итерацию");
        loop.WaitingTicks.Should().Be(1, "первый тик увеличивает счётчик на 1");
        loop.WaitingReason.Should().Be("ответа с чужой сессии", "причина сохраняется между тиками");
        adapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("ТИК ОЖИДАНИЯ") && t.Contains("1/5")),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Once());
    }

    [Fact]
    public async Task TickWaitingLoops_ПотолокТиков_СтопСWaitingTimeout()
    {
        // На потолке WaitingTicks >= MaxWaitingTicks цикл встаёт с reason="waiting_timeout"
        // и причиной из маркера в тексте уведомления. Без тика чат бы висел вечно.
        var session = await MkBusySessionAsync("loop-tick-timeout", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(false); // свободный чат — иначе тик не пройдёт
        SetProcess(entry, adapter.Object);
        var loop0 = _sut.GetById(session.Id)!.WorkLoop!;
        loop0.Phase = "waiting";
        loop0.WaitingReason = "ответа с чужой сессии";
        loop0.WaitingSince = DateTime.UtcNow;
        loop0.WaitingTicks = 4; // один шаг до потолка (после ++ → 5 ≥ 5 → стоп)
        session.Status = SessionStatus.Active;
        SetLoopTurnInFlight(entry, false);
        SetWaitingTickInterval(TimeSpan.Zero);
        SetMaxWaitingTicks(5);
        BackdateWaitingSince(session.Id, TimeSpan.FromMinutes(10));

        await _sut.TickWaitingLoopsAsync();
        await WaitForConditionAsync(() => _sut.GetById(session.Id)!.WorkLoop is null,
            TimeSpan.FromSeconds(2));

        _sut.GetById(session.Id)!.WorkLoop.Should().BeNull("на потолке тиков цикл остановлен");
        var msg = Sent<WorkLoopStoppedMessage>().Should().ContainSingle().Subject;
        msg.Reason.Should().Be("waiting_timeout");
        msg.Text.Should().Contain("ответа с чужой сессии", "причина маркера едет в текст уведомления");
    }

    [Fact]
    public async Task ContinueWorkLoop_НетМаркераВWaiting_ВозвратВWorkingИОбнуление()
    {
        // Возврат из waiting (например, по result хода-тика, где модель НЕ вывела
        // `<waiting>` снова): фаза становится working, счётчик тиков и причина обнуляются.
        // Это симметрия с ContinueWorkLoop_ВозвратИзWaitingВНачалеПродолжаетЦикл, но с
        // проверкой сброса полей тиков.
        var session = await MkBusySessionAsync("loop-tick-resume", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        var loop0 = _sut.GetById(session.Id)!.WorkLoop!;
        loop0.Phase = "waiting";
        loop0.WaitingReason = "ответа";
        loop0.WaitingSince = DateTime.UtcNow;
        loop0.WaitingTicks = 3;
        var iterBefore = loop0.Iteration;

        // Ход-тик без маркера waiting (модель молчит) — триггерит ContinueWorkLoopAsync
        SetLoopTurnInFlight(entry, true);
        lock (entry.GetType().GetField("LoopTurnLock")!.GetValue(entry)!)
        {
            var buf = (System.Text.StringBuilder)entry.GetType().GetField("LoopTurnText")!.GetValue(entry)!;
            buf.Clear(); // пустой ответ: ни маркера, ни промиса, ни блокера
        }

        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));

        var loop = _sut.GetById(session.Id)!.WorkLoop!;
        loop.Phase.Should().Be("working", "нет маркера → возврат в working");
        loop.WaitingTicks.Should().Be(0, "счётчик обнулён на возврате");
        loop.WaitingReason.Should().BeNull("причина обнулена на возврате");
        loop.WaitingSince.Should().BeNull("момент входа обнулён на возврате");
        loop.Iteration.Should().BeGreaterThan(iterBefore, "итерация продвинулась — это уже не тик");
    }

    [Fact]
    public async Task TickWaitingLoops_ОжиданиеПоЖивойЗадаче_НеТикает()
    {
        // Регресс: ожидание по HasLiveDelegatedTasks (причина WaitingReason=null) — НЕ
        // тикается. Доклад придёт сам через Report; смерть исполнителя ловит алерт молчания.
        // Тик там не нужен и был бы вреден: разбудил бы координатора посреди чужой задачи.
        var session = await MkBusySessionAsync("loop-tick-delegated", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        // Чат свободен (иначе тик не пройдёт по другой причине), но ожидание по задаче
        // всё равно должно игнорироваться — WaitingReason=null. Это и проверяем.
        adapter.SetupGet(a => a.HasLiveTurn).Returns(false);
        SetProcess(entry, adapter.Object);

        var loop0 = _sut.GetById(session.Id)!.WorkLoop!;
        loop0.Phase = "waiting";
        loop0.WaitingReason = null; // ожидание по живой задаче, не по маркеру
        loop0.WaitingSince = DateTime.UtcNow;
        loop0.WaitingTicks = 0;
        session.Status = SessionStatus.Active;
        SetLoopTurnInFlight(entry, false);
        SetWaitingTickInterval(TimeSpan.Zero);
        BackdateWaitingSince(session.Id, TimeSpan.FromMinutes(10));

        ClearSent();
        await _sut.TickWaitingLoopsAsync();
        // Даём фоновым задачам отработать, чтобы SendMessageAsync (если бы он был) доехал
        await Task.Delay(200);

        var loop = _sut.GetById(session.Id)!.WorkLoop!;
        loop.WaitingTicks.Should().Be(0, "ожидание по задаче не тикается — счётчик не растёт");
        adapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("ТИК ОЖИДАНИЯ")),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never(),
            "директива-тик не должна уйти — ожидание по задаче");
    }

    [Fact]
    public async Task DrainNextPending_ДокладВОчередиПриАктивномЦикле_Подхватывается()
    {
        // Симметрия гейта разбора очереди: при активном цикле Report подхватывается
        // (а не только User), иначе доклад пролежит в очереди до конца ВСЕГО цикла.
        // Чат в Working (идёт ход-итерация) — доклад ставится через EnqueuePendingAsync.
        var session = await MkBusySessionAsync("loop-drain-report", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        SetLoopTurnInFlight(entry, true);

        // Эмулируем постановку Report через тот же EnqueuePendingAsync, что зовёт SendOrEnqueue.
        // Внутри гейт на 4082 пропустит только User/Report при активном цикле, и dispatchNow=false
        // (статус Working) — сообщение встаёт в очередь, drain подхватит его по result хода.
        var method = typeof(SessionManager).GetMethod("EnqueuePendingAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var enq = (Task)method.Invoke(_sut, new object?[]
        {
            session.Id, entry, "реакция постановщика",
            null, "task-report", 0, true, true, null, SessionManager.PendingKind.Report,
            null, null, null
        })!;
        await enq;

        _sut.GetPending(session.Id).Should().ContainSingle()
            .Which.Kind.Should().Be(SessionManager.PendingKind.Report);

        // По result хода-итерации ContinueWorkLoopAsync триггерится; он УСТУПАЕТ, если
        // в очереди лежит Report (тот же гейт, что и для User) — иначе директива обгонит
        // лежащий доклад. Снимаем LoopTurnInFlight и смотрим: по result drain подхватит Report.
        // В тесте мы НЕ дожидаемся конца хода-реакции — достаточно увидеть, что в очереди
        // ничего не осталось (drain изъял Report).
        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        // Ждём, пока drain унесёт Report
        await WaitForConditionAsync(() => _sut.GetPending(session.Id).Count == 0,
            TimeSpan.FromSeconds(2));
        _sut.GetPending(session.Id).Should().BeEmpty("доклад подхвачен drain'ом, не ждёт конца цикла");
    }

    [Fact]
    public async Task DrainNextPending_ОбычноеАгентскоеВОчередиПриАктивномЦикле_ЖдётКонцаЦикла()
    {
        // Контраст: посторонний агент (kind=Agent) по-прежнему ждёт конца ВСЕГО цикла —
        // решение владельца 2026-09-01: посторонний агент не сбивает координатора.
        var session = await MkBusySessionAsync("loop-drain-agent", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        SetLoopTurnInFlight(entry, true);

        var method = typeof(SessionManager).GetMethod("EnqueuePendingAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var enq = (Task)method.Invoke(_sut, new object?[]
        {
            session.Id, entry, "постороннее сообщение",
            null, "external-agent", 0, true, false, null, SessionManager.PendingKind.Agent,
            null, null, null
        })!;
        await enq;

        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        // Очередь не разобрана (LoopTurnInFlight+нет User/Report в очереди → ContinueWorkLoopAsync
        // шлёт директиву, по result директивы — drain, а до этого момента обычное Agent висит)
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));
        _sut.GetPending(session.Id).Should().ContainSingle("обычное агентское ждёт конца ВСЕГО цикла")
            .Which.Kind.Should().Be(SessionManager.PendingKind.Agent);
    }

    // --- Гард B4: автопилот и «Командная реализация» не сочетаются в одном чате ---

    [Fact]
    public async Task SetWorkLoop_ПриАктивнойКомандаРеализации_Отказ()
    {
        var session = await MakeStabForModeAsync("mode-conflict-1");
        await _sut.SetTeamImplementAsync(session.Id, enabled: true, userId: TestUserId);

        var act = () => _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);

        (await act.Should().ThrowAsync<SessionModeConflictException>())
            .WithMessage("*Командной реализации*");
        _sut.GetById(session.Id)!.WorkLoop.Should().BeNull();
    }

    [Fact]
    public async Task SetTeamImplement_ПриАктивномАвтопилоте_Отказ()
    {
        var session = await MakeStabForModeAsync("mode-conflict-2");
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);

        var act = () => _sut.SetTeamImplementAsync(session.Id, enabled: true, userId: TestUserId);

        (await act.Should().ThrowAsync<SessionModeConflictException>())
            .WithMessage("*Автопилот*");
        _sut.GetById(session.Id)!.TeamImplement.Should().BeNull();
    }

    [Fact]
    public async Task SetTeamImplement_БезАктивногоАвтопилота_РаботаетКакРаньше()
    {
        var session = await MakeStabForModeAsync("mode-noconflict");

        var updated = await _sut.SetTeamImplementAsync(session.Id, enabled: true, userId: TestUserId);

        updated!.TeamImplement.Should().NotBeNull();
    }

    [Fact]
    public async Task SendMessageAndWait_ЧатШтаба_НеПрерывает()
    {
        // Штаб «Командной реализации» агентским сообщением не рушится — прежняя очередь
        var (session, _, _) = await MakeTeamStabAsync("agent-stab-preempt");
        session.Status = SessionStatus.Working;
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        var result = await _sut.SendMessageAndWaitAsync(session.Id, "доклад штабу", TimeSpan.Zero);

        result.Should().BeOfType<SendAndWaitResult.Queued>();
        adapter.Verify(a => a.Interrupt(), Times.Never());
        _sut.GetPending(session.Id).Should().ContainSingle();
    }

    [Fact]
    public async Task Interrupt_Ручной_ЗамороженнаяОчередьНеРазбираетсяПоExited()
    {
        // Регресс «Стоп»: ручное прерывание замораживает очередь, и exited убитого хода
        // НЕ доставляет отложенное — даже если перед этим постановка взводила разбор по exited
        var session = await MkBusySessionAsync("stop-exited", SessionStatus.Working);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        await _sut.SendMessageAndWaitAsync(session.Id, "не доставлять", TimeSpan.Zero);

        _sut.Interrupt(session.Id);
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ExitedMessage(), TestRunId);

        await Task.Delay(150); // drain не должен запуститься
        _sut.GetPending(session.Id).Should().ContainSingle()
            .Which.Text.Should().Be("не доставлять");
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Never());
    }

    [Fact]
    public async Task PreemptForPending_ЗанятыйШтаб_ПрерываетХодИЧиститМаркерыУбитого()
    {
        // Ход штаба убит ради очереди — result по нему не придёт, а с ним не придёт и разбор
        // конца хода, потребляющий буфер маркеров. Оставленный маркер склеился бы с текстом
        // следующего хода и применился задним числом: фантомная эскалация и сдвиг стадии
        // («волна-призрак»). Сама отправка ход штаба не рвёт — рвёт только явный перебой.
        var (session, _, _) = await MakeTeamStabAsync("stab-preempt-user");
        session.Status = SessionStatus.Working;
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        // Координатор успел написать маркер — он копится в буфере хода штаба
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new TextDeltaMessage("<<<ЭСКАЛАЦИЯ: расхождение с планом>>>"), TestRunId);
        GetTeamTurnText(entry).Should().NotBeEmpty("предусловие: буфер хода штаба непуст");

        var outcome = await _sut.SendMessageAsync(session.Id, "как дела?", []);

        outcome.Should().Be(SessionManager.SendUserOutcome.Queued);
        adapter.Verify(a => a.Interrupt(), Times.Never()); // отправка ход штаба не рушит
        GetTeamTurnText(entry).Should().NotBeEmpty("ход жив — буфер копится дальше");

        _sut.PreemptForPending(session.Id).Should().BeTrue();

        adapter.Verify(a => a.Interrupt(), Times.Once());
        GetTeamTurnText(entry).Should().BeEmpty("маркеры убитого хода не доживают до следующего");
    }

    [Fact]
    public async Task Exited_ЧужогоПрогона_НеРазбираетОчередьПрерванногоХода()
    {
        // exited доживающего прогона опаздывает до ~30 мин: не привязанный к прогону разбор
        // увёл бы сообщение в умирающий от interrupt адаптер — из видимой очереди изъято,
        // в семафоре адаптера потеряно
        var session = await MkBusySessionAsync("late-exited", SessionStatus.Working);
        session.Name = "есть имя";
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object, runId: 7);
        await _sut.SendMessageAndWaitAsync(session.Id, "доклад", TimeSpan.Zero); // enqueue + interrupt

        // Поздний exited СТАРОГО прогона
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ExitedMessage(), runId: 6);

        await Task.Delay(150); // разбор не должен запуститься
        _sut.GetPending(session.Id).Should().ContainSingle().Which.Text.Should().Be("доклад");
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Never());

        // exited прерванного прогона — доставка идёт
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ExitedMessage(), runId: 7);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));
        _sut.GetPending(session.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task Exited_ПослеШтатногоResult_НеДренитОчередьВторойРаз()
    {
        // Прерванный ход всё же успел закончиться штатно: очередь разобрал result, метка
        // разбора по exited погашена. Поздний exited того же прогона не должен выпускать
        // следующее сообщение параллельно уже идущему ходу.
        var session = await MkBusySessionAsync("result-then-exited", SessionStatus.Working);
        session.Name = "есть имя";
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        await _sut.SendMessageAndWaitAsync(session.Id, "первое", TimeSpan.Zero);
        await _sut.SendMessageAndWaitAsync(session.Id, "второе", TimeSpan.Zero);

        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2)); // result выпустил «первое»

        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ExitedMessage(), TestRunId);

        await Task.Delay(150); // второй разбор не должен запуститься
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once());
        _sut.GetPending(session.Id).Should().ContainSingle().Which.Text.Should().Be("второе");
    }

    // Регресс: приёмка «один лимит». У SessionWorkLoop больше НЕТ полей квоты запусков.
    // Старые записи data/sessions.json содержали executionsStarted/maxExecutions — System.Text.Json
    // по умолчанию молча игнорирует лишние поля, новая схема должна прочитать штатно.
    [Fact]
    public void WorkLoop_КвотыЗапусковБольшеНет_СтараяЗаписьДесериализуется()
    {
        var legacy = @"{
            ""Promise"": ""ГОТОВО"",
            ""Iteration"": 4,
            ""MaxIterations"": 20,
            ""Phase"": ""waiting"",
            ""ExecutionsStarted"": 17,
            ""MaxExecutions"": 20,
            ""WaitingTicks"": 0
        }";
        var opts = new System.Text.Json.JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<SessionWorkLoop>(legacy, opts);

        roundTripped.Should().NotBeNull();
        roundTripped!.Iteration.Should().Be(4, "значение из JSON сохранено");
        roundTripped.MaxIterations.Should().Be(20);
        roundTripped.Phase.Should().Be("waiting");
        roundTripped.Promise.Should().Be("ГОТОВО");
        // Лишние поля JSON с ExecutionsStarted/MaxExecutions теперь тихо игнорируются —
        // System.Text.Json по умолчанию не падает на unknown fields, поэтому старая запись
        // sessions.json читается новым кодом без миграции.
    }

    // Регресс: запуск задачи из чата с включённым циклом на ОБЫЧНОМ ходу — разрешён (без
    // отдельной квоты цикла). Лавину возвратов держит Iteration в ContinueWorkLoopAsync.
    [Fact]
    public async Task ГейтЗапуска_ОбычныйХодВЧатеСЦиклом_РазрешёнБезСписания()
    {
        var session = await MakeWorkLoopChatAsync("human-turn");
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.CurrentTurnSuppressTasksExecute).Returns(false);
        SetProcess(entry, adapter.Object);
        var context = MakeMcpCallContext(session.Id);

        ExecuteFilter().OnActionExecuting(context);

        context.Result.Should().BeNull("чистый рабочий ход координатора — без квоты и без запрета");
    }

    // Регресс: запуск задачи из чата с включённым циклом на ходу ДОКЛАДА исполнителя —
    // разрешён: ход-реакция — точка, где координатор принимает результат и запускает
    // следующего исполнителя.
    [Fact]
    public async Task ГейтЗапуска_ХодДокладаВЧатеСЦиклом_Разрешён()
    {
        var session = await MakeWorkLoopChatAsync("report-turn");
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.CurrentTurnSuppressTasksExecute).Returns(true);
        SetProcess(entry, adapter.Object);
        var context = MakeMcpCallContext(session.Id);

        ExecuteFilter().OnActionExecuting(context);

        context.Result.Should().BeNull(
            "активный цикл «до готово»: ход-реакция на доклад — единственный момент, где координатор "
            + "ставит следующего исполнителя; лавину возвратов держит Iteration, не запрет");
    }

    // Ход доклада вне цикла тоже запускает: запрет снят решением владельца (мешал ставить
    // следующую задачу), цикл «доклад → запуск → доклад» останавливает «Стоп».
    [Fact]
    public async Task ГейтЗапуска_ХодДокладаВнеЦикла_Разрешён()
    {
        var dir = MkProjectDir("wl-report-plain");
        var project = _projectManager.Create("WL-RP", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.CurrentTurnSuppressTasksExecute).Returns(true);
        SetProcess(entry, adapter.Object);
        var context = MakeMcpCallContext(session.Id);

        ExecuteFilter().OnActionExecuting(context);

        context.Result.Should().BeNull("ход-реакция на доклад запуск задачи больше не запрещает");
    }

    // Регресс: ход ГЛУБЖЕ допустимой глубины запуска (2) в чате с циклом — запрет.
    // Цикл «до готово» разрешает реакционный ход на доклад, но не ход, ушедший за потолок
    // глубины: иначе автономный цикл бы запускал исполнителей по цепочке делегирования.
    [Fact]
    public async Task ГейтЗапуска_ДелегированныйХодВЧатеСЦиклом_ОтказКакРаньше()
    {
        var session = await MakeWorkLoopChatAsync("delegated");
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.CurrentTurnAgentDepth).Returns(2);
        SetProcess(entry, adapter.Object);
        var context = MakeMcpCallContext(session.Id);

        ExecuteFilter().OnActionExecuting(context);

        var result = context.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.ObjectResult>().Subject;
        result.StatusCode.Should().Be(403, "цепочка делегирования не идёт дальше независимо от режима");
    }

    // Ядро новой механики: возврат из waiting в working инкрементирует Iteration РОВНО ОДИН РАЗ.
    // Тест от обратного к имитации drain → ContinueWorkLoopAsync: ставим Phase=waiting руками,
    // эмулируем result хода-реакции и ждём директиву продолжения. Подробный сценарий — в
    // ContinueWorkLoop_ПриходДоклада_ВозвращаетWorkingИПродолжаетЦикл; здесь — короткий контракт.
    [Fact]
    public async Task ContinueWorkLoop_ВозвратИзWaiting_ИнкрементируетНаОдин()
    {
        var session = await MkBusySessionAsync("loop-return-iter", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        _sut.GetById(session.Id)!.WorkLoop!.Phase = "waiting";
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        SetLoopTurnInFlight(entry, true);
        var iterBefore = _sut.GetById(session.Id)!.WorkLoop!.Iteration;

        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));

        var loop = _sut.GetById(session.Id)!.WorkLoop!;
        loop.Phase.Should().Be("working", "ожидание сменилось работой");
        loop.Iteration.Should().Be(iterBefore + 1,
            "ровно ОДИН инкремент на ВОЗВРАТ из ожидания — НЕ на каждом ходе цикла");
    }

    // Контракт: серия «запуск исполнителя → доклад → продолжение → запуск → доклад …»
    // упирается в MaxIterations и останавливает цикл с reason="limit". Счётчик считает
    // ВСЕ ходы цикла (рабочие + возвраты из ожидания), текст об этом — единый.
    [Fact]
    public async Task ContinueWorkLoop_СерияВозвратовУпираетсяВЛимит_СтопСReasonLimit()
    {
        var session = await MkBusySessionAsync("loop-exhaust-limit", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var loop = _sut.GetById(session.Id)!.WorkLoop!;
        loop.MaxIterations = 2;
        loop.Iteration = 1; // следующий возврат (++ → 2) упрётся в лимит
        loop.Phase = "waiting";
        var entry = GetEntry(session.Id);
        SetLoopTurnInFlight(entry, true);

        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForConditionAsync(() => _sut.GetById(session.Id)!.WorkLoop is null, TimeSpan.FromSeconds(2));

        _sut.GetById(session.Id)!.WorkLoop.Should().BeNull();
        var msg = Sent<WorkLoopStoppedMessage>().Should().ContainSingle().Subject;
        msg.Reason.Should().Be("limit");
        msg.Text.Should().Contain("2 ходов");
    }

    // Контракт: чистый working-ход (без prior waiting) Iteration тратит. Это закрывает
    // регрессию 0c7d43ff — цикл без делегирования должен останавливаться по лимиту,
    // а не крутиться вечно с Iteration=0. Раньше (до регрессии) этот контракт держался
    // инкрементом в конце ContinueWorkLoopAsync; сейчас — там же, плюс точка на возврате.
    [Fact]
    public async Task ContinueWorkLoop_ЧистыйWorkingХод_ТратитИтерацию()
    {
        var session = await MkBusySessionAsync("loop-no-return", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        // По умолчанию Phase=working, HasLiveDelegatedTasks=null — никакого возврата из ожидания
        var iterBefore = _sut.GetById(session.Id)!.WorkLoop!.Iteration;
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        SetLoopTurnInFlight(entry, true);

        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));

        var loop = _sut.GetById(session.Id)!.WorkLoop!;
        loop.Should().NotBeNull("цикл продолжается — нет блокера/промиса/waiting");
        loop.Iteration.Should().Be(iterBefore + 1,
            "чистый working-ход тратит итерацию — иначе цикл без делегирования не остановится");
    }

    // Ключевой регресс-тест: серия чистых working-ходов БЕЗ ухода в ожидание упирается
    // в MaxIterations и останавливает цикл с reason="limit". Именно отсутствие такого
    // теста и пропустило регрессию 0c7d43ff — цикл без делегирования крутился вечно.
    [Fact]
    public async Task ContinueWorkLoop_СерияЧистыхWorkingХодовУпираетсяВЛимит_СтопСReasonLimit()
    {
        var session = await MkBusySessionAsync("loop-pure-working-limit", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var loop = _sut.GetById(session.Id)!.WorkLoop!;
        loop.MaxIterations = 2; // потолок низкий — прогон короткий, а смысл тот же
        var entry = GetEntry(session.Id);
        // HasLiveDelegatedTasks не задан — цикл НЕ уходит в waiting, значит каждый result —
        // это чистый working-ход, который должен тратить итерацию в конце метода.
        _sut.HasLiveDelegatedTasks.Should().BeNull("санити: делегат не задан");

        // Первый ход: Iteration 0 → 1, цикл живёт, директива уходит.
        SetLoopTurnInFlight(entry, true);
        var adapter1 = StubAdapter(entry);
        SetProcess(entry, adapter1.Object);
        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForSendAsync(adapter1, TimeSpan.FromSeconds(2));
        _sut.GetById(session.Id)!.WorkLoop!.Iteration.Should().Be(1,
            "первый чистый working-ход тратит итерацию");

        // Второй ход: Iteration 1 → 2, упёрлись в лимит → стоп reason="limit".
        SetLoopTurnInFlight(entry, true);
        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForConditionAsync(() => _sut.GetById(session.Id)!.WorkLoop is null,
            TimeSpan.FromSeconds(2));

        _sut.GetById(session.Id)!.WorkLoop.Should().BeNull(
            "чистые working-ходы без делегирования тоже обязаны упереться в лимит");
        var msg = Sent<WorkLoopStoppedMessage>().Should().ContainSingle().Subject;
        msg.Reason.Should().Be("limit");
        msg.Text.Should().Contain("2 ходов");
    }

    // Смешанный сценарий: обычный рабочий ход + возврат из ожидания суммарно дают +2.
    // Подтверждает, что ОБЕ точки инкремента живы одновременно и считают независимо.
    [Fact]
    public async Task ContinueWorkLoop_СмешанныйСценарий_РабочийХодПлюсВозврат_ДаютДва()
    {
        var session = await MkBusySessionAsync("loop-mixed", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var iterBefore = _sut.GetById(session.Id)!.WorkLoop!.Iteration;

        // Шаг 1: чистый working-ход (Phase=working, делегат не задан) — инкремент в конце метода.
        SetLoopTurnInFlight(entry, true);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));
        _sut.GetById(session.Id)!.WorkLoop!.Iteration.Should().Be(iterBefore + 1,
            "чистый working-ход даёт +1");

        // Шаг 2: возврат из ожидания (Phase=waiting руками) — инкремент в блоке waiting→working.
        _sut.GetById(session.Id)!.WorkLoop!.Phase = "waiting";
        SetLoopTurnInFlight(entry, true);
        var adapter2 = StubAdapter(entry);
        SetProcess(entry, adapter2.Object);
        await InvokeOnMessageAsync(session.Id, GetAccumulator(entry),
            new ResultMessage("success", 10, 1, null, null), TestRunId);
        await WaitForSendAsync(adapter2, TimeSpan.FromSeconds(2));

        var loop = _sut.GetById(session.Id)!.WorkLoop!;
        loop.Phase.Should().Be("working", "возврат из ожидания перевёл фазу");
        loop.Iteration.Should().Be(iterBefore + 2,
            "рабочий ход + возврат из ожидания суммарно дают +2");
    }

    // Фаза ожидания сама по себе (тики, пока доклад не пришёл) итерацию НЕ тратит.
    // Проверяем через прямой путь TickWaitingLoopsAsync: цикл в waiting по маркеру
    // (WaitingReason != null — НЕ живая делегированная задача), один тик, и убеждаемся,
    // что Iteration не сдвинулся. Используем существующие помощники SetWaitingTickInterval
    // и SetMaxWaitingTicks (тесту выше — TickWaitingLoops_СвободныйЧат_*), плюс
    // BackdateWaitingSince — тик ждёт ≥ интервала с WaitingSince.
    [Fact]
    public async Task ContinueWorkLoop_ФазаОжиданияБезДоклада_ИтерациюНеТратит()
    {
        var session = await MkBusySessionAsync("loop-waiting-ticks", SessionStatus.Working);
        await _sut.SetWorkLoopAsync(session.Id, enabled: true, userId: TestUserId);
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(false); // свободный чат — иначе тик не пройдёт
        SetProcess(entry, adapter.Object);

        var loop0 = _sut.GetById(session.Id)!.WorkLoop!;
        var iterBefore = loop0.Iteration;
        loop0.Phase = "waiting";
        loop0.WaitingReason = "тестовый доклад";
        loop0.WaitingTicks = 0;
        session.Status = SessionStatus.Active;
        SetLoopTurnInFlight(entry, false);
        SetWaitingTickInterval(TimeSpan.Zero);
        SetMaxWaitingTicks(5); // потолок большой — тест не про стоп, а про счёт
        BackdateWaitingSince(session.Id, TimeSpan.FromMinutes(10));

        ClearSent();
        await _sut.TickWaitingLoopsAsync();
        await WaitForSendAsync(adapter, TimeSpan.FromSeconds(2));

        var after = _sut.GetById(session.Id)!.WorkLoop!;
        after.Iteration.Should().Be(iterBefore,
            "тики фазы ожидания итерацию не тратят — бесплатны по счёту");
        after.WaitingTicks.Should().Be(1, "счётчик тиков ожидания растёт независимо от Iteration");
        after.Phase.Should().Be("waiting",
            "после первого тика цикл ещё в ожидании (потолок тиков не достигнут)");
    }

    // Minor (ревью work-loop): невалидное значение лимита в конфиге (≤0 / не число) не должно
    // молча отрубать цикл — сваливаемся в дефолт. LoopLimitOrDefault internal — покрыт напрямую.
    [Theory]
    [InlineData(null, 20)]
    [InlineData("", 20)]
    [InlineData("0", 20)]
    [InlineData("-5", 20)]
    [InlineData("abc", 20)]
    [InlineData("1", 1)]
    [InlineData("5", 5)]
    [InlineData("100", 100)]
    public void LoopLimitOrDefault_НевалидноеИлиНоль_Дефолт_ПоложительноеКакЕсть(string? raw, int expected)
    {
        SessionManager.LoopLimitOrDefault(raw).Should().Be(expected);
    }

    [Fact]
    public void LoopLimitOrDefault_СвойДефолтУважается()
    {
        SessionManager.LoopLimitOrDefault("0", 7).Should().Be(7);
        SessionManager.LoopLimitOrDefault("3", 7).Should().Be(3);
    }

    // Э7-фикс (находка Веры Major №2): координатор до публикации плана вызывал
    // tasks_run_executor напрямую — стадия оставалась planning, карточки плана не было,
    // а единственное согласование (карточка плана) обходилось целиком. Бюджет честно
    // считал расход, но самого гейта по стадии не было — квота разрешала запуск.
    [Fact]
    public async Task КвотаЗапуска_ДоПубликацииПлана_Отказ()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-before-plan");
        // MakeTeamStabAsync только включает режим — стадия по умолчанию interview,
        // план ещё не публиковался (см. SessionTeamImplement.Stage)

        var (verdict, reason) = _sut.TryConsumeTeamImplementRun(session.Id, TestUserId);

        verdict.Should().Be(SessionManager.TeamRunQuota.Exhausted,
            "план не подтверждён — единственное согласование ещё не пройдено");
        reason.Should().Contain("не подтверждён");
        _sut.GetById(session.Id)!.TeamImplement!.Budget.RunsUsed.Should().Be(0, "отказ ничего не расходует");
    }

    [Fact]
    public async Task ГейтЗапуска_ДоПубликацииПлана_ОтказЧерезФильтр()
    {
        // Тот же сценарий на реальной точке входа: mcp__tasks__tasks_run_executor → /execute
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-before-plan-filter");
        var context = MakeMcpCallContext(session.Id);

        ExecuteFilter().OnActionExecuting(context);

        var result = context.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.ObjectResult>().Subject;
        result.StatusCode.Should().Be(403);
    }

    // REST-путь запуска (атрибут [DenyOnDelegatedTurn] на TasksController.Execute) обязан
    // держать ту же допустимую глубину, что http-тулсет: один литерал, иначе пути разъедутся
    // (REST разрешил бы запуск на глубине, где http-тулсет ещё держит запрет, и наоборот).
    [Fact]
    public void ГейтЗапуска_Атрибут_ДопустимаяГлубина_СовпадаетСКонстантой()
    {
        ExecuteFilter().AllowedDepth.Should().Be(McpEndpoints.RunExecutorAllowedDepth,
            "REST-путь и http-тулсет запуска читают одну и ту же допустимую глубину");
    }

    [Fact]
    public async Task КвотаЗапуска_ПослеОстановкиЧеловеком_Отказ()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-stop");
        await _sut.StopTeamImplementAsync(session.Id, TestUserId);

        var (verdict, reason) = _sut.TryConsumeTeamImplementRun(session.Id, TestUserId);

        verdict.Should().Be(SessionManager.TeamRunQuota.Exhausted);
        reason.Should().Contain("остановлена");
    }

    [Fact]
    public async Task Бюджет_РасширяетсяТолькоОтветомЧеловекаПоКарточке()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-budget-add");
        var budget = _sut.GetById(session.Id)!.TeamImplement!.Budget;
        var maxTasksBefore = budget.MaxTasks;
        budget.RunsUsed = budget.MaxRuns;
        // Исчерпание бюджета случается на ходу итерации — волна уже стартовала
        _sut.GetById(session.Id)!.TeamImplement!.WaveNumber = 1;

        // Действие агента (гейт запуска) потолки не двигает — оно их только читает
        _sut.TryConsumeTeamImplementRun(session.Id, TestUserId);
        budget.MaxRuns.Should().Be(budget.RunsUsed, "агент не может добавить себе бюджет");

        // Путь человека: карточка исчерпания + кнопка «Добавить бюджет» из хаба
        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.BudgetExhausted,
            Title = "Бюджет итерации израсходован",
            Actions = TeamEscalationActions.For(TeamEscalationKind.BudgetExhausted),
        };
        await ((ITeamHistoryStore)_sut).PublishTeamEscalationAsync(session.Id, escalation);
        var ok = await _sut.RespondTeamEscalationAsync(session.Id, escalation.Id, "addBudget",
            userId: TestUserId);

        ok.Should().BeTrue();
        var after = _sut.GetById(session.Id)!.TeamImplement!;
        after.Budget.MaxTasks.Should().BeGreaterThan(maxTasksBefore);
        after.Budget.MaxRuns.Should().BeGreaterThan(after.Budget.RunsUsed, "работа может продолжиться");
        after.Stage.Should().Be(TeamImplementStage.Wave, "практика вернулась в работу");
    }

    [Fact]
    public async Task КарточкаОстановки_ПубликуетсяВЛентуИСтавитСтадиюОжидания()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-escalation");

        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.Blocker,
            Title = "Исполнитель застрял: нет доступа к БД",
            Details = "Не могу подключиться к тестовой базе",
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.Blocker),
        };
        await ((ITeamHistoryStore)_sut).PublishTeamEscalationAsync(session.Id, escalation);

        var card = Sent<TeamEscalationMessage>().Single();
        card.Type.Should().Be("team_escalation");
        card.Kind.Should().Be("blocker");
        card.Resolved.Should().BeFalse();
        card.Actions.Select(a => a.Id).Should().Contain(["answer", "reassign", "drop"]);
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "молчаливых остановок в режиме не бывает");
    }

    // Дыра покрытия (волна 3): смена координатора не переписывает автора уже опубликованных
    // карточек — «всё, что штаб говорит человеку, идёт от лица координатора НА МОМЕНТ публикации»
    [Fact]
    public async Task ЭскалацияПослеСменыКоординатора_СтараяКарточкаСохраняетИсходногоАвтора()
    {
        var (session, backend, _) = await MakeTeamStabAsync("ti-author-change");
        var originalCoordinatorId = session.PersonaId!;
        var oldCard = new TeamEscalation
        {
            Kind = TeamEscalationKind.Blocker,
            Title = "Первая остановка",
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.Blocker),
        };
        await ((ITeamHistoryStore)_sut).PublishTeamEscalationAsync(session.Id, oldCard);

        await _sut.SetTeamImplementAsync(session.Id, enabled: true, coordinatorPersonaId: backend.Id,
            userId: TestUserId);
        var newCard = new TeamEscalation
        {
            Kind = TeamEscalationKind.Blocker,
            Title = "Вторая остановка",
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.Blocker),
        };
        await ((ITeamHistoryStore)_sut).PublishTeamEscalationAsync(session.Id, newCard);

        var cards = Sent<TeamEscalationMessage>().ToList();
        cards.Single(c => c.Title == "Первая остановка").PersonaId.Should().Be(originalCoordinatorId,
            "старая карточка не должна переписываться на нового координатора задним числом");
        cards.Single(c => c.Title == "Вторая остановка").PersonaId.Should().Be(backend.Id,
            "новая карточка идёт от лица уже смененного координатора");
    }

    [Fact]
    public async Task КарточкаОстановки_ОтветЧеловека_ГаситКарточкуИВозвращаетВРаботу()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-escalation-answer");
        // Блокер приходит от исполнителя идущей волны
        _sut.GetById(session.Id)!.TeamImplement!.WaveNumber = 1;
        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.Blocker,
            Title = "Исполнитель застрял: нет доступа",
            Actions = TeamEscalationActions.For(TeamEscalationKind.Blocker),
        };
        await ((ITeamHistoryStore)_sut).PublishTeamEscalationAsync(session.Id, escalation);

        var ok = await _sut.RespondTeamEscalationAsync(session.Id, escalation.Id, "answer",
            "доступ выдал, продолжай", TestUserId);

        ok.Should().BeTrue();
        var last = Sent<TeamEscalationMessage>().Last();
        last.Resolved.Should().BeTrue();
        last.ChosenActionId.Should().Be("answer");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Wave);
        // Служебный ход координатору — с подписью плашки механики вместо пузыря «Автоматически»
        Sent<UserMessageMessage>().Last().StaffNote
            .Should().Be("Ответ на карточку передан координатору");
    }

    // Прод 2026-07-31: ответ на карточку «Координатор не понял вводную» (кнопок нет — ответ
    // полем, actionId=null) ставил Stage=Wave при WaveNumber=0 и PlanCardId=null — «волна-
    // призрак»: статус врёт «идёт волна 0, дождись докладов», волн/задач/плана нет, сторож
    // не тикает (WaveStartedAt=null). Ответ до первой волны возвращает практику в стадию,
    // из которой пришла карточка.
    [Fact]
    public async Task КарточкаНеПонялВводную_ОтветЧеловека_ВозвращаетВИнтервьюАНеВПризрачнуюВолну()
    {
        var (session, _, _) = await MakeInterviewStabAsync("ti-stall-answer");
        await _sut.HandleTeamTurnEndAsync(session.Id, "Хорошо, посмотрю что тут можно сделать.", failed: false);
        var card = Sent<TeamEscalationMessage>().Last();
        card.Kind.Should().Be("productDecision");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision);

        var ok = await _sut.RespondTeamEscalationAsync(session.Id, card.EscalationId, null,
            "нужен экспорт именно в CSV, кнопка в тулбаре", TestUserId);

        ok.Should().BeTrue();
        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.WaveNumber.Should().Be(0);
        ti.Stage.Should().Be(TeamImplementStage.Interview,
            "до первой волны ответ на эскалацию возвращает практику в стадию карточки, " +
            "а не в «волну-призрак» без плана и сторожа");
    }

    // Дыра покрытия (волна 3): свободный текстовый ответ (без кнопки) доезжает до координатора
    // дословно — не теряется и не подменяется generic-текстом решения
    [Fact]
    public async Task RespondEscalation_СвободныйТекст_ДоезжаетДоКоординатораДословно()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-escalation-freetext");
        _sut.GetById(session.Id)!.TeamImplement!.WaveNumber = 1;
        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.Blocker,
            Title = "Исполнитель застрял: нет доступа",
            Actions = TeamEscalationActions.For(TeamEscalationKind.Blocker),
        };
        await ((ITeamHistoryStore)_sut).PublishTeamEscalationAsync(session.Id, escalation);

        var ok = await _sut.RespondTeamEscalationAsync(session.Id, escalation.Id, null,
            "доступ выдал вручную, продолжай без кнопки", TestUserId);

        ok.Should().BeTrue();
        Sent<UserMessageMessage>().Last().Text
            .Should().Contain("доступ выдал вручную, продолжай без кнопки");
    }

    [Theory]
    [InlineData(TeamEscalationKind.BudgetExhausted, "addBudget")]
    [InlineData(TeamEscalationKind.Stopped, "resume")]
    [InlineData(TeamEscalationKind.WaveGate, "runNext")]
    public async Task КарточкаОстановки_РешениеВозвращающееВРаботу_РаздаётВолну(
        TeamEscalationKind kind, string actionId)
    {
        // Худший отказ автономного режима — молчаливый тупик после явного действия человека:
        // кнопка нажата, а волна не стартует, WaveStartedAt пуст и сторож молчит
        var (session, plan, handed) = await MakeStabWithPlanAndStarterAsync("ti-resume-" + actionId);
        var escalation = new TeamEscalation
        {
            Kind = kind,
            Title = "Практика ждёт решения",
            Actions = TeamEscalationActions.For(kind),
        };
        await ((ITeamHistoryStore)_sut).PublishTeamEscalationAsync(session.Id, escalation);

        var ok = await _sut.RespondTeamEscalationAsync(session.Id, escalation.Id, actionId, userId: TestUserId);

        ok.Should().BeTrue();
        handed().Should().BeSameAs(plan, $"после «{actionId}» практика обязана поехать сама");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Wave);
    }

    [Fact]
    public async Task КарточкаОстановки_ЗавершитьИтерацию_ВолнуНеРаздаёт()
    {
        var (session, _, handed) = await MakeStabWithPlanAndStarterAsync("ti-finish-nowave");
        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.BudgetExhausted,
            Title = "Бюджет израсходован",
            Actions = TeamEscalationActions.For(TeamEscalationKind.BudgetExhausted),
        };
        await ((ITeamHistoryStore)_sut).PublishTeamEscalationAsync(session.Id, escalation);

        await _sut.RespondTeamEscalationAsync(session.Id, escalation.Id, "finish", userId: TestUserId);

        handed().Should().BeNull("«Завершить итерацию» новую работу не разворачивает");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Checking);
    }

    // Прод 2026-08-17 («мёртвая зона конвейера»): карточка PlanDeviation висела ПОСЛЕ
    // закрытия волны — авто-раздача следующей была подавлена, «Разрешить» в белый список
    // actionId не входил, и после ответа практика возвращалась в Wave без раздачи. Теперь
    // признак «пора звать раздачу» — состояние (ClosedWave == WaveNumber + нерозданные
    // под-задачи), а не кнопка.
    [Fact]
    public async Task RespondEscalation_AllowПослеЗакрытойВолны_РаздаётСледующуюВолну()
    {
        var (session, plan, handed) = await MakeStabWithPlanAndStarterAsync("ti-allow-deadzone");
        // Одна волна роздана и закрыта, вторая ждёт — форма мёртвой зоны инцидента
        ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
        {
            t.WaveNumber = 1; t.ClosedWave = 1; t.PlannedWaves = 2;
            return true;
        });
        var escalation = new TeamEscalation
        {
            Kind = TeamEscalationKind.PlanDeviation,
            Title = "Работа выходит за план",
            Actions = TeamEscalationActions.For(TeamEscalationKind.PlanDeviation),
        };
        await ((ITeamHistoryStore)_sut).PublishTeamEscalationAsync(session.Id, escalation);

        var ok = await _sut.RespondTeamEscalationAsync(session.Id, escalation.Id, "allow", userId: TestUserId);

        ok.Should().BeTrue();
        handed().Should().BeSameAs(plan,
            "«Разрешить» по карточке, висевшей после закрытия волны, обязан раздать следующую волну");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Wave);
    }

    // Тот же инцидент, второй вход в него: ответ обычным сообщением (у текста нет actionId,
    // белым списком он не покрывался вовсе) — раздача теперь решается тем же состоянием.
    [Fact]
    public async Task ТекстВОжиданииРешения_ПослеЗакрытойВолны_РаздаётСледующуюВолну()
    {
        var (session, plan, handed) = await MakeStabWithPlanAndStarterAsync("ti-text-deadzone");
        ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
        {
            t.WaveNumber = 1; t.ClosedWave = 1; t.PlannedWaves = 2;
            return true;
        });
        await ((ITeamHistoryStore)_sut).PublishTeamEscalationAsync(session.Id, new TeamEscalation
        {
            Kind = TeamEscalationKind.PlanDeviation,
            Title = "Работа выходит за план",
            Actions = TeamEscalationActions.For(TeamEscalationKind.PlanDeviation),
        });
        session.Status = SessionStatus.Working;

        await _sut.SendMessageAsync(session.Id, "разрешаю, делай как считаешь нужным", []);

        handed().Should().BeSameAs(plan,
            "текстовый ответ после закрытой волны тоже обязан запустить раздачу следующей");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Wave);
    }

    [Fact]
    public async Task КарточкаОстановки_ЧужойВладелец_НеОтвечает()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-escalation-alien");
        var escalation = new TeamEscalation { Kind = TeamEscalationKind.Blocker, Title = "Застрял" };
        await ((ITeamHistoryStore)_sut).PublishTeamEscalationAsync(session.Id, escalation);

        var ok = await _sut.RespondTeamEscalationAsync(session.Id, escalation.Id, "answer",
            userId: "another-user");

        ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("Всё плохо\n<escalate:deviation>нужен файл вне владения</escalate>",
        TeamEscalationKind.PlanDeviation, "нужен файл вне владения")]
    [InlineData("<escalate:check>падает 3 теста</escalate>", TeamEscalationKind.CheckFailed, "падает 3 теста")]
    [InlineData("<escalate:decision>CSV или XLSX?</escalate>", TeamEscalationKind.ProductDecision, "CSV или XLSX?")]
    // Модель по XML-привычке закрывает тег по имени (</escalate:check>) — принимаем оба варианта
    [InlineData("<escalate:check>падает 3 теста</escalate:check>", TeamEscalationKind.CheckFailed, "падает 3 теста")]
    [InlineData("<escalate:clarify>что именно неясно</escalate:clarify>", TeamEscalationKind.NeedsClarification, "что именно неясно")]
    public void МаркерЭскалации_РазбираетсяПоТипу(string text, TeamEscalationKind kind, string details)
    {
        var parsed = SessionManager.ParseEscalationMarker(text);

        parsed.Should().NotBeNull();
        parsed!.Value.Kind.Should().Be(kind);
        parsed.Value.Text.Should().Be(details);
    }

    [Fact]
    public void МаркерЭскалации_ВнутриКодБлока_НеСчитается()
    {
        // Модель часто цитирует протокол, прежде чем им пользоваться — цитата не остановка
        var text = "Протокол такой:\n```\n<escalate:check>пример</escalate>\n```\nработаю дальше";

        SessionManager.ParseEscalationMarker(text).Should().BeNull();
    }

    [Fact]
    public void МаркерЭскалации_БезМаркера_Null()
    {
        SessionManager.ParseEscalationMarker("обычный ответ координатора").Should().BeNull();
    }
}
