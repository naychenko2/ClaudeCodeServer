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

public class SessionManagerTeamLoopTests : SessionManagerTestBase
{
    [Fact]
    public async Task КонецХода_ПослеПроверки_ПереводитРежимВОжиданиеВводной()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-idle");
        _sut.GetById(session.Id)!.TeamImplement!.Stage = TeamImplementStage.Checking;

        await _sut.HandleTeamTurnEndAsync(session.Id, "Итерация завершена: 5 задач, проверка пройдена", failed: false);

        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Should().NotBeNull("режим не выключается вместе с планом — выключает его только человек");
        ti.Stage.Should().Be(TeamImplementStage.Idle);
        Sent<TeamImplementMessage>().Last().Stage.Should().Be("idle",
            "стадия ожидания уходит на провод — по ней рисуется бейдж «ждёт задачу»");
    }

    [Fact]
    public async Task КонецХода_ПроверкаУпала_ЗовётЧеловекаКарточкойИНеЗависаетВПроверке()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-idle-fail");
        _sut.GetById(session.Id)!.TeamImplement!.Stage = TeamImplementStage.Checking;

        await _sut.HandleTeamTurnEndAsync(session.Id, "", failed: true);

        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Stage.Should().NotBe(TeamImplementStage.Idle,
            "упавший ход итог не подвёл — «итерация завершена» было бы враньём");
        ti.Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "и застревать в «проверке» навсегда нельзя — выход только через человека");
        var card = Sent<TeamEscalationMessage>().Last();
        card.Kind.Should().Be("checkFailed");
        card.Actions.Select(a => a.Id).Should().Contain(["keepFixing", "finishWithIssues"]);
    }

    // m4 (второй проход Глеба, e7aee793): «Чинить дальше» уводило стадию в Wave без
    // фактической раздачи волны — упавший следующий ход не давал checkFailed
    // (HandleTeamTurnEndAsync требует Stage == Checking), а сторож волн в Wave не смотрит.
    // Контур «любая остановка = карточка» был дырявым ровно там, где это больнее всего.
    [Fact]
    public async Task КарточкаПроверки_ЧинитьДальше_ОстаётсяВПроверкеИДаётКарточкуПриПовторномПровале()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-keepfixing");
        _sut.GetById(session.Id)!.TeamImplement!.Stage = TeamImplementStage.Checking;
        await _sut.HandleTeamTurnEndAsync(session.Id, "", failed: true);
        var firstCard = Sent<TeamEscalationMessage>().Last();
        firstCard.Kind.Should().Be("checkFailed");

        var ok = await _sut.RespondTeamEscalationAsync(session.Id, firstCard.EscalationId, "keepFixing",
            userId: TestUserId);

        ok.Should().BeTrue();
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Checking,
            "«Чинить дальше» чинит и перепроверяет сам координатор — раздачи волны здесь нет, " +
            "уводить стадию в Wave нельзя");

        // Следующий ход координатора тоже падает — контур «любая остановка = карточка» обязан
        // сработать снова, а не молчать
        await _sut.HandleTeamTurnEndAsync(session.Id, "", failed: true);

        var secondCard = Sent<TeamEscalationMessage>().Last();
        secondCard.Kind.Should().Be("checkFailed",
            "упавший ход после «Чинить дальше» обязан снова дать карточку, а не молчать");
    }

    // Э7-фикс (находка Веры Major №3): координатор в стадии planning закончил ход без
    // маркера работы (у слабых моделей маркер иногда теряется) — молчаливый тупик:
    // плана нет, карточки нет, бейдж «планирование» повис бы навсегда без единого следа.
    [Fact]
    public async Task КонецХода_PlanningБезМаркераИБезВолн_ЗовётЧеловекаКарточкойВместоМолчания()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-silent-stall");
        // Stage по умолчанию planning, WaveNumber == 0 — ни одна волна ещё не стартовала

        await _sut.HandleTeamTurnEndAsync(session.Id, "Хорошо, посмотрю что тут можно сделать.", failed: false);

        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "молчаливый тупик закрывается карточкой, а не бесконечным «планированием»");
        var card = Sent<TeamEscalationMessage>().Last();
        card.Kind.Should().Be("productDecision");
    }

    // Э8-фикс (ревью Глеба): тот же класс молчаливого тупика, что чинил Э7-фикс, но не был
    // портирован на новую стадию Interview — координатор, закончивший первый ход итерации
    // обычным текстом без <team:work> и без <escalate:...>, вешал бы практику на «интервью»
    // навсегда без единой карточки.
    [Fact]
    public async Task КонецХода_InterviewБезМаркераИБезВолн_ЗовётЧеловекаКарточкойВместоМолчания()
    {
        var (session, _, _) = await MakeInterviewStabAsync("ti-interview-silent-stall");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Interview);

        await _sut.HandleTeamTurnEndAsync(session.Id, "Хорошо, посмотрю что тут можно сделать.", failed: false);

        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "молчаливый тупик в интервью закрывается карточкой, а не бесконечным «интервью»");
        var card = Sent<TeamEscalationMessage>().Last();
        card.Kind.Should().Be("productDecision");
    }

    // Волна 6 (живая приёмка волны 5): ход, оборванный технически (рестарт сервера, упавший
    // процесс), раньше падал в ТУ ЖЕ ветку молчаливого тупика, что и координатор, реально
    // ответивший без маркера — карточка «Координатор не понял вводную» отправляла человека
    // переформулировать задачу, хотя причина не в ней. failed=true обязан давать честный текст
    // про обрыв хода, а не «не понял вводную».
    [Fact]
    public async Task КонецХода_ОборванТехническиВPlanning_ДаётЧестнуюКарточкуАНеНеПонялВводную()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-turn-interrupted");
        // Stage по умолчанию planning, WaveNumber == 0 — до маркера дело не дошло

        await _sut.HandleTeamTurnEndAsync(session.Id, "", failed: true);

        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Stage.Should().Be(TeamImplementStage.AwaitingDecision);
        var card = Sent<TeamEscalationMessage>().Last();
        card.Kind.Should().Be("productDecision");
        card.Title.Should().Be("Ход прервался");
        card.Title.Should().NotBe("Координатор не понял вводную",
            "обрыв хода — техническая причина, а не непонятая вводная");
        card.Details.Should().Contain("Повторить");
    }

    // Тот же честный текст обязан вытеснить и «Уточнения так и не пришли» — интервью,
    // прерванное технически посреди волны, это не тупик clarify-раунда.
    [Fact]
    public async Task КонецХода_ОборванТехническиВОжиданииУточнений_ДаётЧестнуюКарточку()
    {
        var (session, _, _) = await MakeInterviewStabAsync("ti-turn-interrupted-clarify");
        ((ITeamRunState)_sut).WithTeamState(session.Id, t => { t.WaveNumber = 1; return true; });

        await _sut.HandleTeamTurnEndAsync(session.Id, "", failed: true);

        var card = Sent<TeamEscalationMessage>().Last();
        card.Title.Should().Be("Ход прервался");
        card.Title.Should().NotBe("Уточнения так и не пришли");
    }

    // Прод 2026-08-04 (чат «Второй заход»): гард молчаливого тупика поднял «Координатор не
    // понял вводную» ПОСРЕДИ живого планирования. Ход закончился маркером работы, планировщик
    // строил план (потолок 300 с), и за это время в чате закончился ещё один ход — честный
    // ответ «ждём карточку плана» без маркера: Planning && WaveNumber == 0 совпало с условием
    // гарда, и тревога повисла красной, хотя через 28 секунд пришёл готовый план. Пока
    // планировщик жив, конец чужого хода без маркера — не тупик координатора.
    [Fact]
    public async Task КонецХода_БезМаркераПокаПланированиеЖиво_НеДаётЛожнойТревоги()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-planning-inflight");
        SetPlannerAnswer(backend, frontend);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _plannerStub.Gate = () => gate.Task;

        // Ход с маркером работы запускает планирование; не await — он жив, пока калитка закрыта
        var markerTurn = _sut.HandleTeamTurnEndAsync(session.Id,
            "<team:work>добавить экспорт в CSV</team>", failed: false);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (_plannerStub.Calls == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        _plannerStub.Calls.Should().Be(1, "маркер работы обязан запустить планировщика");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Planning);

        // Пока планирование живо, в чате заканывается ещё один ход — без маркера
        await _sut.HandleTeamTurnEndAsync(session.Id, "Ждём карточку плана команды.", failed: false);

        _sentMessages.OfType<TeamEscalationMessage>().Should().BeEmpty(
            "планировщик жив — стадия Planning && WaveNumber == 0 это работа, а не тупик");

        // Планирование завершается планом: человек получает карточку плана, тревоги не было
        gate.SetResult();
        await markerTurn;

        _sentMessages.OfType<TeamEscalationMessage>().Should().BeEmpty();
        _sentMessages.OfType<TeamPlanMessage>().Should().ContainSingle();
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Confirming);
    }

    // Парная страховка к тесту выше: тот же конец хода без маркера в Planning && WaveNumber == 0,
    // но планировщик НЕ жив — это настоящий молчаливый тупик, и гард обязан позвать человека.
    // Защита, ради которой гард делался (Э7-фикс), не теряется.
    [Fact]
    public async Task КонецХода_БезМаркераИПланировщикНеЖив_ПоПрежнемуДаётКарточку()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-planning-deadend");
        _sut.GetById(session.Id)!.TeamImplement!.Stage = TeamImplementStage.Planning;

        await _sut.HandleTeamTurnEndAsync(session.Id, "Хорошо, посмотрю что тут можно сделать.", failed: false);

        var card = _sentMessages.OfType<TeamEscalationMessage>().Should().ContainSingle()
            .Which;
        card.Title.Should().Be("Координатор не понял вводную");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision);
    }

    // Прод 2026-08-12 (P16): ход координатора, завершённый текстом без маркера, но оставивший
    // живой фоновый субагент (entry.Process.HasPendingBg) — НЕ молчаливый тупик: координатор
    // ждёт собственного результата, и следующий ход (пробуждение по task-notification) принесёт
    // маркер team:work. Гард обязан молчать, иначе карточка «не понял вводную» уводит стадию
    // в AwaitingDecision, и тогда штатный team:work уже не потребляется (StartTeamWorkAsync её
    // не принимает).
    [Fact]
    public async Task КонецХодаБезМаркера_ЖивойAsyncСубагент_НеДаётЛожнойТревоги()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-async-agent-inflight");
        _sut.GetById(session.Id)!.TeamImplement!.Stage = TeamImplementStage.Planning;
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasPendingBg).Returns(true); // координатор запустил async-агента
        SetProcess(entry, adapter.Object);

        await _sut.HandleTeamTurnEndAsync(session.Id, "Запустил разведку кода, жду результат.", failed: false);

        _sentMessages.OfType<TeamEscalationMessage>().Should().BeEmpty(
            "ход, оставивший живого async-субагента, — это работа, а не тупик координатора");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Planning,
            "стадия не ушла в AwaitingDecision по ложной тревоге");
    }

    // Задача b63fd8ea: тот же класс молчаливого тупика, что и P16, но дыра была не в условии,
    // а в его потолке длительности. Подавление через HasAsyncAgent висело бессрочно —
    // фоновый агент с heartbeat'ами мог не доводить координатора до маркера часами, и
    // BgLingerTimeout грейс тишины не лечил (это не потолок длительности). Должно быть
    // наоборот: async-агент живёт дольше своего окна подавления (10 мин) → гард поднимает
    // карточку молчаливого тупика и уводит стадию в AwaitingDecision, чтобы человек мог
    // вмешаться.
    [Fact]
    public async Task КонецХода_AsyncСубагентВиситДольшеПорога_КарточкаМолчаливогоТупикаПриходит()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-async-agent-stalled");
        _sut.GetById(session.Id)!.TeamImplement!.Stage = TeamImplementStage.Planning;
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasPendingBg).Returns(true); // координатор всё ещё ждёт агента
        SetProcess(entry, adapter.Object);
        // Метка подавления проставлена давно — окно 10 мин истекло. Без этого фикса гард
        // молчал бы бессрочно и интервью висело часами без карточки и push.
        SetAsyncAgentStallSince(entry, DateTime.UtcNow - TimeSpan.FromMinutes(11));

        await _sut.HandleTeamTurnEndAsync(session.Id, "Всё ещё жду результат.", failed: false);

        _sentMessages.OfType<TeamEscalationMessage>().Should().ContainSingle(
            "async-агент молчит дольше окна подавления — карточка молчаливого тупика обязана прийти");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "гард перевёл стадию, чтобы человек мог вмешаться");
    }

    // Регрессия P16 с новой логикой: подавление в окне порога (метка 5 минут назад) —
    // гард молчит. Это не дубль существующего теста P16 (там метки нет вообще — первый вызов):
    // здесь мы явно проверяем, что окно порога работает в ОБЕ стороны.
    [Fact]
    public async Task КонецХода_AsyncСубагентВиситВНачалеОкна_ГардМолчит()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-async-agent-fresh-stall");
        _sut.GetById(session.Id)!.TeamImplement!.Stage = TeamImplementStage.Planning;
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasPendingBg).Returns(true);
        SetProcess(entry, adapter.Object);
        // Подавление началось 5 минут назад — глубоко внутри окна 10 мин.
        SetAsyncAgentStallSince(entry, DateTime.UtcNow - TimeSpan.FromMinutes(5));

        await _sut.HandleTeamTurnEndAsync(session.Id, "Жду результат.", failed: false);

        _sentMessages.OfType<TeamEscalationMessage>().Should().BeEmpty(
            "подавление ещё в окне порога — гард обязан молчать, как при P16");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Planning,
            "стадия не ушла в AwaitingDecision по ложной тревоге");
    }

    // Волна 3 задачи b63fd8ea (Major): хук на BgAgentDoneMessage(Aborted=true). Агент умер
    // вместе с прогоном, HandleTeamTurnEndAsync никто не позовёт (следующего хода может
    // не быть) — карточка молчаливого тупика должна прийти через OnMessageAsync →
    // case BgAgentDoneMessage → публикация. Тест идёт реальным путём
    // (InvokeOnMessageAsync + BgAgentDoneMessage), не прямой вызов HandleBgAgentDoneAsync
    // — иначе повторишь ошибку первой волны (покрываешь только публикацию, а не хук).
    //
    // Снимок HasPendingBg=false — ПОСЛЕ учёта текущего сообщения: HandleStructuredTaskNotification
    // (ClaudeSession.cs:5055) синхронно удаляет задачу из run.PendingBg до публикации
    // BgAgentDoneMessage. Это сценарий «единственный bg-агент умер вместе с прогоном».
    // Сценарий «другой агент жив» — отдельный тест ниже (BgAgentDoneMessage_Aborted_ДругойАгентЖив_*).
    [Fact]
    public async Task BgAgentDoneMessage_Aborted_БезСледующегоХода_КарточкаМолчаливогоТупикаПриходит()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-b63fd8ea-aborted");
        ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
        {
            t.Stage = TeamImplementStage.Planning;
            t.WaveNumber = 0;
            return true;
        });
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasPendingBg).Returns(false); // единственный агент уже удалён из PendingBg
        SetProcess(entry, adapter.Object);

        // Реальный путь: эмулируем смерть bg-агента вместе с прогоном через OnMessageAsync.
        var acc = GetAccumulator(entry);
        await InvokeOnMessageAsync(session.Id, acc,
            new BgAgentDoneMessage(new[] { "tool1" }, Aborted: true), TestRunId);

        var cards = await WaitForEscalationCardsAsync(session.Id, minCount: 1);
        cards.Should().ContainSingle(
            "единственный async-агент умер вместе с прогоном — карточка молчаливого тупика обязана прийти через BgAgentDoneMessage");
        cards[0].Title.Should().Be("Координатор не понял вводную",
            "стадия Planning с WaveNumber == 0 — формулировка «Координатор не понял вводную»");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "pre-claim в HandleBgAgentDoneAsync перевёл стадию в AwaitingDecision");
    }

    // Волна 3 / регрессия P16 в новой форме: структурный task_notification ставит Aborted
    // ДЛЯ ОДНОГО конкретного агента, даже если параллельно работает ДРУГОЙ. Прежний
    // хук (волна 2) поднимал карточку сразу — координатор ЖИВ, второй агент работает,
    // стадия уходит в AwaitingDecision по ложной тревоге. Глеб уронил это репро мутацией.
    // Снимок HasPendingBg=true после удаления текущего агента: второй агент в полёте.
    [Fact]
    public async Task BgAgentDoneMessage_Aborted_ДругойАгентЖив_КарточкаНеПоявляется()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-b63fd8ea-chain-alive");
        ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
        {
            t.Stage = TeamImplementStage.Planning;
            t.WaveNumber = 0;
            return true;
        });
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        // Снимок «после удаления текущего агента»: второй агент в полёте.
        adapter.SetupGet(a => a.HasPendingBg).Returns(true);
        SetProcess(entry, adapter.Object);

        var acc = GetAccumulator(entry);
        await InvokeOnMessageAsync(session.Id, acc,
            new BgAgentDoneMessage(new[] { "tool1" }, Aborted: true), TestRunId);

        // Карточки нет и стадия не ушла: HandleBgAgentDoneAsync увидел hasAsync=true
        // (Major-фикс P16) и вышел без публикации. Ждём чуть-чуть на случай гонки
        // с фоновыми обработчиками.
        await Task.Delay(150);
        Sent<TeamEscalationMessage>()
            .Where(m => m.SessionId == session.Id && !m.Resolved)
            .Should().BeEmpty(
                "другие async-агенты живы — карточка НЕ поднимается (регресс P16)");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Planning,
            "стадия НЕ ушла в AwaitingDecision, координатор ждёт второго агента");
    }

    // Волна 3 / парный к Major: цепочка агентов по очереди абортируется. После
    // последнего аборта HasAsyncAgent == false → карточка поднимается. Гард дождался
    // своего часа (см. шапку HandleBgAgentDoneAsync).
    [Fact]
    public async Task BgAgentDoneMessage_Aborted_ВсеАгентыАбортировались_КарточкаПослеПоследнего()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-b63fd8ea-chain-all-aborted");
        ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
        {
            t.Stage = TeamImplementStage.Planning;
            t.WaveNumber = 0;
            return true;
        });
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        var acc = GetAccumulator(entry);

        // Первый аборт — второй агент ещё жив. Карточки нет.
        adapter.SetupGet(a => a.HasPendingBg).Returns(true);
        await InvokeOnMessageAsync(session.Id, acc,
            new BgAgentDoneMessage(new[] { "tool1" }, Aborted: true), TestRunId);
        await Task.Delay(100);
        Sent<TeamEscalationMessage>()
            .Where(m => m.SessionId == session.Id && !m.Resolved).Should().BeEmpty(
                "первый аборт — другие живые, карточка не поднимается");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Planning);

        // Второй аборт — других агентов больше нет. Карточка появляется.
        adapter.SetupGet(a => a.HasPendingBg).Returns(false);
        await InvokeOnMessageAsync(session.Id, acc,
            new BgAgentDoneMessage(new[] { "tool2" }, Aborted: true), TestRunId);

        var cards = await WaitForEscalationCardsAsync(session.Id, minCount: 1);
        cards.Should().ContainSingle(
            "последний аборт — других агентов нет, карточка молчаливого тупика обязана прийти");
        cards[0].Title.Should().Be("Координатор не понял вводную");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "pre-claim в HandleBgAgentDoneAsync перевёл стадию");
    }

    // Волна 3 / идемпотентность: повторный BgAgentDoneMessage(Aborted=true) с уже
    // AwaitingDecision не дублирует карточку (карточка была поднята раньше через
    // HandleTeamTurnEndAsync, стадия уже переведена публикацией). Защита — НЕ
    // явный `if (AwaitingDecision) return;` (волна 2 держала его ПОСЛЕ
    // `if (!stalledStage) return;`, и строка была мертва), а неявная проверка:
    // AwaitingDecision не входит в stalledStage, и метод выходит через
    // `if (!stalledStage) return;` ещё до атомарного pre-claim.
    [Fact]
    public async Task BgAgentDoneMessage_Aborted_СтадияУжеAwaitingDecision_НеДублируемКарточку()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-b63fd8ea-aborted-idem");
        ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
        {
            t.Stage = TeamImplementStage.AwaitingDecision; // карточка уже поднята раньше
            t.WaveNumber = 0;
            return true;
        });
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        var acc = GetAccumulator(entry);
        await InvokeOnMessageAsync(session.Id, acc,
            new BgAgentDoneMessage(new[] { "tool1" }, Aborted: true), TestRunId);

        // Карточки нет и не будет: stalledStage == false на AwaitingDecision, метод
        // хука выходит до pre-claim. Ждём чуть-чуть на случай гонки.
        await Task.Delay(150);
        Sent<TeamEscalationMessage>()
            .Where(m => m.SessionId == session.Id && !m.Resolved)
            .Should().BeEmpty(
                "AwaitingDecision уже стоит — stalledStage == false, хук выходит до публикации");
    }

    // Волна 3 / Minor 2: AsyncAgentStallSince сбрасывается ТОЛЬКО когда после
    // текущего BgAgentDoneMessage других async-агентов больше нет (HasAsyncAgent == false).
    // Сценарий: цепочка из 3 агентов, каждый абортируется по очереди. Метка НЕ должна
    // сбрасываться на каждом (иначе завершение КАЖДОГО из цепочки перезапускало бы
    // 10-минутный потолок подавления, и суппрессия тянулась неограниченно).
    // Прежний безусловный сброс на ЛЮБОМ BgAgentDoneMessage — ровно то, против чего
    // был написан баг b63fd8ea.
    [Fact]
    public async Task BgAgentDoneMessage_МеткаПодавленияСбрасываетсяТолькоКогдаВсеАгентыУшли()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-b63fd8ea-stallreset-precise");
        ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
        {
            t.Stage = TeamImplementStage.Planning;
            t.WaveNumber = 0;
            return true;
        });
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        // Метка подавления проставлена давно (10+ минут назад) — имитирует сценарий,
        // где гард один раз уже видел подавление. Проверка именно точечности:
        // метка НЕ должна сбрасываться, пока ещё есть другие агенты.
        SetAsyncAgentStallSince(entry, DateTime.UtcNow - TimeSpan.FromMinutes(11));
        var acc = GetAccumulator(entry);

        // Первые два BgAgentDoneMessage — другие агенты живы (HasPendingBg=true).
        // Метка НЕ сбрасывается — иначе возврат в Interview со свежим агентом
        // унаследует длительность от НЕсвязанного прошлого.
        adapter.SetupGet(a => a.HasPendingBg).Returns(true);
        await InvokeOnMessageAsync(session.Id, acc,
            new BgAgentDoneMessage(new[] { "tool1" }, Aborted: true), TestRunId);
        var afterFirst = entry.GetType().GetField("AsyncAgentStallSince")!.GetValue(entry);
        afterFirst.Should().NotBeNull(
            "другие агенты живы — метка НЕ сбрасывается, иначе суппрессия тянется неограниченно");

        await InvokeOnMessageAsync(session.Id, acc,
            new BgAgentDoneMessage(new[] { "tool2" }, Aborted: true), TestRunId);
        var afterSecond = entry.GetType().GetField("AsyncAgentStallSince")!.GetValue(entry);
        afterSecond.Should().NotBeNull(
            "второй агент всё ещё жив — метка НЕ сбрасывается");

        // Третий BgAgentDoneMessage — все агенты ушли (HasPendingBg=false).
        // Метка сбрасывается — следующий всплеск фона считается с нуля.
        adapter.SetupGet(a => a.HasPendingBg).Returns(false);
        await InvokeOnMessageAsync(session.Id, acc,
            new BgAgentDoneMessage(new[] { "tool3" }, Aborted: true), TestRunId);
        var afterThird = entry.GetType().GetField("AsyncAgentStallSince")!.GetValue(entry);
        afterThird.Should().BeNull(
            "агентов больше нет — метка сбрасывается, иначе возврат в Interview со свежим агентом унаследует длительность");
    }

    // Волна 4 задачи b63fd8ea (Minor A): публикация карточки молчаливого тупика ПОСЛЕ
    // успешного TryClaimSilentStall упала (AppendAsync бросил исключение / чат удалён).
    // Клеймо обязано откатиться под WithTeamState, иначе чат навсегда в AwaitingDecision
    // БЕЗ карточки: stalledStage для этой стадии больше не true, гард больше никогда
    // не сработает. Проверяем (а) стадия вернулась в Planning, не застряла в
    // AwaitingDecision, и (б) следующий TryClaimSilentStall снова может пройти.
    [Fact]
    public async Task МолчаливыйТупик_ПубликацияКарточкиУпала_КлеймоОткатывается()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-rollback-publish-fail");
        ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
        {
            t.Stage = TeamImplementStage.Planning;
            t.WaveNumber = 0;
            return true;
        });

        // EscalationRaiser бросает исключение — имитируем сбой AppendAsync/пуш-нотификации.
        // До фикса это исключение молча уходило в Console.Error (HandleTeamTurnCompletedAsync
        // оборачивает Task.Run в try/catch), а стадия навсегда залипала в AwaitingDecision.
        _sut.TeamHandlers.EscalationRaiser = (_, _) =>
            throw new InvalidOperationException("симуляция сбоя публикации карточки");

        // Никаких маркеров эскалации/работы/разговора — путь идёт через гард молчаливого
        // тупика (строки HandleTeamTurnEndAsync 244–303): claim → publish → (исключение)
        // → rollback. HandleTeamTurnCompletedAsync в проде оборачивает Task.Run в try/catch
        // — имитируем это в тесте, чтобы проверить именно откат, а не исключение.
        try
        {
            await _sut.HandleTeamTurnEndAsync(session.Id, "координатор молчит", failed: false);
            throw new InvalidOperationException("сбой публикации должен пробрасываться — прод ловит в Task.Run-обёртке");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("симуляция сбоя публикации карточки"))
        {
            // ожидаемо — исключение пробрасывается, клеймо уже откатилось внутри.
        }

        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Planning,
            "сбой публикации карточки откатывает клеймо, иначе гард больше никогда не сработает — "
            + "stalledStage для AwaitingDecision ложно, а AwaitingDecision карточки не имеет");

        // Следующая попытка: гард не «прилип», чат снова может поднять карточку.
        var claim2 = ((ITeamRunState)_sut).TryClaimSilentStall(session.Id, out _);
        claim2.Should().BeTrue("после отката чат снова в Planning && WaveNumber==0 — следующий claim обязан пройти");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision);
    }

    // Тот же класс (Minor A), но через хук BgAgentDoneMessage(Aborted). Покрывает вторую
    // публикационную точку (HandleBgAgentDoneAsync) и подтверждает, что и там сбой
    // публикации откатывает клеймо. Без этого теста регрессия ограничилась бы
    // HandleTeamTurnEndAsync, а хук остался бы без страховки.
    [Fact]
    public async Task BgAgentDoneMessage_Aborted_ПубликацияУпала_КлеймоОткатывается()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-rollback-bg-fail");
        ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
        {
            t.Stage = TeamImplementStage.Planning;
            t.WaveNumber = 0;
            return true;
        });
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        adapter.SetupGet(a => a.HasPendingBg).Returns(false);
        SetProcess(entry, adapter.Object);

        _sut.TeamHandlers.EscalationRaiser = (_, _) =>
            throw new InvalidOperationException("симуляция сбоя публикации по хуку");

        // InvokeOnMessageAsync прогоняет BgAgentDoneMessage через OnMessageAsync →
        // HandleBgAgentDoneAsync. Исключение из EscalationRaiser НЕ вырывается наружу
        // (текущая ветка в коде — через await без обёртки try/catch в OnMessageAsync, и
        // тест ловит его наверху): для целей теста достаточно прогнать и проверить откат.
        try
        {
            var acc = GetAccumulator(entry);
            await InvokeOnMessageAsync(session.Id, acc,
                new BgAgentDoneMessage(new[] { "tool1" }, Aborted: true), TestRunId);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("симуляция сбоя публикации по хуку"))
        {
            // ожидаемо — исключение пробросилось через OnMessageAsync, клеймо уже откатилось внутри.
        }

        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Planning,
            "и через хук BgAgentDoneMessage сбой публикации карточки откатывает клеймо");

        var claim2 = ((ITeamRunState)_sut).TryClaimSilentStall(session.Id, out _);
        claim2.Should().BeTrue("после отката чат снова в Planning && WaveNumber==0");
    }

    // Волна 5 (Minor 1, CAS): RollbackSilentStallClaim откатывает снимок ТОЛЬКО если
    // состояние всё ещё соответствует «живому клейму» — иначе безусловный откат вернёт
    // стадию в Planning/Interview после успешной публикации карточки, и через StalledMinutes
    // гард поднимет ВТОРУЮ карточку поверх уже видимой пользователю. Прод-сценарий:
    // EscalationRaiser (TeamWaveService.RaiseEscalationAsync) УЖЕ положил карточку в
    // ленту через PublishTeamEscalationAsync, между publish и хвостом (NotificationService.
    // SendAsync) параллельно пришёл ответ человека (стадия сменилась) — после чего хвост
    // упал. Без CAS-гейта catch в HandleTeamTurnEndAsync откатил бы стадию, и пользователь
    // видел бы дубликат. С CAS-гейтом — false на выходе, состояние не трогается.
    [Fact]
    public async Task МолчаливыйТупик_ПубликацияУспешнаХвостУпал_КлеймоНеОткатывается()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-rollback-cas-postpublish");
        ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
        {
            t.Stage = TeamImplementStage.Planning;
            t.WaveNumber = 0;
            return true;
        });

        // EscalationRaiser имитирует прод-путь TeamWaveService.RaiseEscalationAsync:
        // 1) успешная публикация карточки (AppendAsync + WithTeamState «AwaitingDecision» +
        //    BroadcastTeamImplementAsync); 2) параллельная гонка — человек уже ответил на
        //    опубликованную карточку между publish и send-notification, режим ушёл в работу
        //    (стадия Wave, как если бы другой поток успел отработать); 3) хвост SendAsync
        //    бросает исключение. Именно так в проде NotificationService.SendAsync:21
        //    (store.AddAsync + hub.SendAsync без try/catch там) упадёт ПОСЛЕ publish.
        _sut.TeamHandlers.EscalationRaiser = async (_, card) =>
        {
            // Шаг 1: публикация прошла, карточка пользователю видна.
            await ((ITeamHistoryStore)_sut).PublishTeamEscalationAsync(session.Id, card);

            // Шаг 2: между publish и send-notification параллельный поток сменил стадию
            // (варианты в проде: пользователь нажал кнопку карточки, BgAgentDoneMessage от
            // другого агента, response чата и т.п.). Стадия взята ОТЛИЧНОЙ от claim.Stage
            // (Planning) намеренно: при совпадении безусловный откат вернул бы ровно то же
            // значение, и тест перестал бы отличать «CAS есть» от «CAS нет» — гейт был бы
            // вакуумным (проверено мутацией: со стадией Planning снятие CAS-условия в
            // RollbackSilentStallClaim не роняет ни одного теста).
            ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
            {
                t.Stage = TeamImplementStage.Wave;
                return true;
            });

            // Шаг 3: хвост SendAsync бросил — ловится в HandleTeamTurnEndAsync catch.
            throw new InvalidOperationException("симуляция сбоя NotificationService.SendAsync после publish");
        };

        try
        {
            await _sut.HandleTeamTurnEndAsync(session.Id, "координатор молчит", failed: false);
            throw new InvalidOperationException("сбой хвоста публикации должен пробрасываться — прод ловит в Task.Run-обёртке");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("симуляция сбоя NotificationService"))
        {
            // ожидаемо — клеймо НЕ откатилось, потому что состояние уже ушло вперёд.
        }

        // Карточка в ленте есть — пользователь её видит с момента успешного publish.
        Sent<TeamEscalationMessage>().Should().Contain(m => !m.Resolved,
            "пользователь видит опубликованную карточку после успешного publish в EscalationRaiser");

        // Стадия НЕ вернулась в исходное состояние: CAS пропустил откат, потому что
        // t.Stage уже не AwaitingDecision (параллельный поток увёл её в Wave). Без
        // CAS-гейта откат вернул бы Planning && WaveNumber==0 поверх идущей волны —
        // и следующий заход гарда (через StalledMinutes, по умолчанию 30 минут) поднял бы
        // ВТОРУЮ карточку поверх уже видимой первой. Этот ассерт и есть гейт: со снятым
        // CAS-условием он падает.
        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.Stage.Should().Be(TeamImplementStage.Wave,
            "стадия сменилась параллельной гонкой — CAS пропустил откат (иначе гард позже "
            + "поднял бы ВТОРУЮ карточку поверх уже видимой первой)");

        // Второй гейт на то же самое с другой стороны: гард не может переклеймить чат,
        // потому что состояние осталось за параллельным потоком (Wave — не stalledStage).
        // Со снятым CAS-условием стадия была бы Planning && WaveNumber==0, и этот вызов
        // вернул бы true — то есть гард поднял бы вторую карточку.
        var claim2 = ((ITeamRunState)_sut).TryClaimSilentStall(session.Id, out _);
        claim2.Should().BeFalse("стадия Wave — не stalledStage, гард не клеймит");
    }

    // Прод 2026-08-12 (P23): карточка блокера гаснет, когда координатор, разбуженный докладом,
    // продолжает работу маркером team:work — человека просить решения по решённому вопросу
    // не нужно. Стадия уходит из AwaitingDecision в перепланирование вводной.
    [Fact]
    public async Task КарточкаБлокера_КоординаторПродолжилРаботу_ГаснетАвтоматом()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-blocker-autoresolve-work");
        SetPlannerAnswer(backend, frontend);
        var blocker = new TeamEscalation
        {
            Kind = TeamEscalationKind.Blocker,
            Title = "Исполнитель застрял",
            Details = "нет доступа к API",
            Actions = TeamEscalationActions.For(TeamEscalationKind.Blocker),
        };
        await ((ITeamHistoryStore)_sut).PublishTeamEscalationAsync(session.Id, blocker);
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision);

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "<team:work>переделать интеграцию через мок-сервер</team>", failed: false);

        Sent<TeamEscalationMessage>().Should().Contain(m => m.EscalationId == blocker.Id && m.Resolved,
            "координатор снял блокер сам — карточка не должна висеть");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Confirming,
            "team:work запустил перепланирование, координатор ждёт согласования нового плана");
    }

    // Прод 2026-08-12 (P23): карточка блокера гаснет, когда итерация финиширована — все
    // плановые волны закрыты, координатор подвёл итог, а карточка висела в AwaitingDecision.
    // Практика приходит в терминальное Idle, а не остаётся ждать человека по решённому вопросу.
    [Fact]
    public async Task КарточкаБлокера_ВсеВолныЗакрытыИтогом_ГаснетВTerminus()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-blocker-autoresolve-final");
        var blocker = new TeamEscalation
        {
            Kind = TeamEscalationKind.Blocker,
            Title = "Исполнитель застрял",
            Details = "нет доступа к API",
            Actions = TeamEscalationActions.For(TeamEscalationKind.Blocker),
        };
        await ((ITeamHistoryStore)_sut).PublishTeamEscalationAsync(session.Id, blocker);
        ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
        {
            t.WaveNumber = 2; t.PlannedWaves = 2; t.ClosedWave = 2;
            return true;
        });

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "Итог: задача закрыта, волна выполнена.", failed: false);

        Sent<TeamEscalationMessage>().Should().Contain(m => m.EscalationId == blocker.Id && m.Resolved,
            "блокер снят финалом итерации — карточка не должна висеть");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Idle,
            "при закрытых волнах практика приходит в терминальное Idle, а не ждёт человека");
    }

    // Парная страховка к двум тестам выше: карточка блокера уместна, когда координатор НЕ снял
    // предмет блокера — ни team:work, ни закрытых волн. Тогда человека зовём, как и раньше.
    [Fact]
    public async Task КарточкаБлокера_КоординаторНеСнял_ОстаётсяЖдатьЧеловека()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-blocker-keep");
        var blocker = new TeamEscalation
        {
            Kind = TeamEscalationKind.Blocker,
            Title = "Исполнитель застрял",
            Details = "нет доступа к API",
            Actions = TeamEscalationActions.For(TeamEscalationKind.Blocker),
        };
        await ((ITeamHistoryStore)_sut).PublishTeamEscalationAsync(session.Id, blocker);
        ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
        {
            t.WaveNumber = 1; t.PlannedWaves = 2; t.ClosedWave = 0;
            return true;
        });

        await _sut.HandleTeamTurnEndAsync(session.Id, "Разбираюсь, что можно сделать.", failed: false);

        Sent<TeamEscalationMessage>().Should().NotContain(m => m.Resolved,
            "координатор не снял блокер — авто-гашения быть не должно");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "практика ждёт решения человека по актуальному блокеру");
    }

    // Прод 2026-08-12 (P23, вторичный симптом): после ответа человека на карточку остановки
    // при закрытых волнах плана практика приходит в Idle (итерация завершена), а не висит в
    // вечной «волне N из N». Путь по умолчанию (actionId "answer") симметричен
    // ResumeTeamFromDecisionOnUserInput — оба не возвращают в мёртвый Wave.
    [Fact]
    public async Task RespondEscalation_ВсеВолныЗакрыты_ВозвращаетВIdleАНеВWave()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-respond-terminal");
        var blocker = new TeamEscalation
        {
            Kind = TeamEscalationKind.Blocker,
            Title = "Исполнитель застрял",
            Details = "нет доступа к API",
            Actions = TeamEscalationActions.For(TeamEscalationKind.Blocker),
        };
        await ((ITeamHistoryStore)_sut).PublishTeamEscalationAsync(session.Id, blocker);
        ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
        {
            t.WaveNumber = 2; t.PlannedWaves = 2; t.ClosedWave = 2;
            return true;
        });

        var ok = await _sut.RespondTeamEscalationAsync(session.Id, blocker.Id, "answer", userId: TestUserId);

        ok.Should().BeTrue();
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Idle,
            "возвращать в Wave некуда — все волны плана закрыты, итерация завершена");
    }

    // Minor «дубль уведомления эскалации» (волна 3): ветка is_error в ClaudeSession шлёт
    // синтетический ErrorMessage(ExpectResultFollows: true) и следом безусловно ResultMessage
    // того же хода — раньше OnMessageAsync независимо запускал HandleTeamTurnEndAsync на КАЖДОМ
    // из них, и гонка двух фоновых задач давала две одинаковые карточки/push (GET /api/notifications
    // с createdAt до миллисекунды — живая приёмка, заходы 1 и 5). Спаренный ResultMessage
    // должен только погасить флаг SkipNextTeamTurnEnd, а не разобрать ход второй раз.
    [Fact]
    public async Task КонецХода_ПарнаяErrorИResultОдногоХода_ШинаРазбираетОдинРазНеДубли()
    {
        var (session, _, _) = await MakeInterviewStabAsync("ti-dup-notif");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Interview);
        // LastTurnSeq по SessionStartedMessage ставит адаптер; в тесте идём мимо — выставляем
        // явно, иначе RecordTeamTurnEnd(0) отсечёт план по правилу «turnSeq <= 0».
        SetLastTurnSeq(GetEntry(session.Id), 7);
        var acc = new TurnAccumulator(new List<StoredMessage>());

        await InvokeOnMessageAsync(session.Id, acc,
            new ErrorMessage("You've hit your weekly limit", ExpectResultFollows: true), TestRunId);
        await InvokeOnMessageAsync(session.Id, acc,
            new ResultMessage("success", 10, 1, null, null), TestRunId);

        GetLastTeamTurnEnds(GetEntry(session.Id)).Count.Should().Be(1,
            "спаренный ResultMessage не должен создавать второй план по тому же TurnSeq");

        await PublishTurnCompletedAsync(_sut, session.Id, 7, "failed");
        var cards = await WaitForEscalationCardsAsync(session.Id, minCount: 1);
        cards.Should().ContainSingle(
            "спаренный ResultMessage не должен второй раз разбирать тот же ход штаба");
    }

    // Регресс-страховка: после хотя бы одной волны marker-less ответ в planning/idle —
    // легитимный разговор по WorkClassificationProtocol («почему выбрали Киру?» и т.п.),
    // а не тупик. Карточка тут была бы навязчивым шумом на каждой обычной реплике.
    [Fact]
    public async Task КонецХода_РазговорПослеХотяБыОднойВолны_НеЗоветКарточкой()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-chat-after-wave");
        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.WaveNumber = 1;
        team.Stage = TeamImplementStage.Planning;

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "Мы выбрали Дениса, потому что это бэкенд-часть.", failed: false);

        team.Stage.Should().Be(TeamImplementStage.Planning,
            "после хотя бы одной волны разговор без маркера — легитимный ответ, не тупик");
        Sent<TeamEscalationMessage>().Should().BeEmpty();
    }

    [Theory]
    [InlineData("Понял, беру в работу.\n<team:work>добавить экспорт в CSV</team>", "добавить экспорт в CSV")]
    [InlineData("<team:work>  починить сортировку  </team>", "починить сортировку")]
    // Закрытие по имени (</team:work>) — так модель реально генерирует (прод 2026-07-31)
    [InlineData("<team:work>добавить экспорт в CSV</team:work>", "добавить экспорт в CSV")]
    public void МаркерРаботы_РазбираетсяИзОтветаКоординатора(string text, string expected)
    {
        SessionManager.ParseWorkMarker(text).Should().Be(expected);
    }

    // Прод 2026-07-31: координатор выдал постановку ~14 КБ и закрыл тег по имени — строгое
    // </team> маркер роняло, и вводная уходила в вечный цикл «Координатор не понял вводную».
    [Fact]
    public void МаркерРаботы_ЗакрытиеПоИмениНаДлиннойПостановке_Разбирается()
    {
        var brief = new string('x', 14 * 1024);

        SessionManager.ParseWorkMarker($"<team:work>{brief}</team:work>").Should().Be(brief);
        SessionManager.ParseWorkMarker($"<team:work>{brief}</team>").Should().Be(brief);
    }

    [Fact]
    public void МаркерРаботы_ВнутриКодБлокаИлиБезМаркера_Null()
    {
        SessionManager.ParseWorkMarker("Протокол:\n```\n<team:work>пример</team>\n```\nотвечаю").Should().BeNull();
        SessionManager.ParseWorkMarker("Киру выбрал планировщик: фронт — её зона").Should().BeNull();
    }

    // P19: координатор вкладывает дамп компонента в постановку — код обязан дойти до планировщика
    // целиком. До фикса код-блок вырезался из всего текста хода ДО извлечения маркера, и в
    // plan-v1.md после строки «Текущий код компонента:» было пусто.
    [Fact]
    public void МаркерРаботы_КодБлокВнутриМаркера_СохраняетсяЦеликом()
    {
        var text = "<team:work>Текущий код компонента:\n```tsx\nfunction Foo() { return null; }\n```\nПерепиши.</team:work>";
        var expected = "Текущий код компонента:\n```tsx\nfunction Foo() { return null; }\n```\nПерепиши.";

        SessionManager.ParseWorkMarker(text).Should().Be(expected);
    }

    // Инлайн-код тоже сохраняется внутри постановки (не только fenced).
    [Fact]
    public void МаркерРаботы_ИнлайнКодВнутриМаркера_Сохраняется()
    {
        SessionManager.ParseWorkMarker("<team:work>замени `foo()` на `bar()` везде</team>")
            .Should().Be("замени `foo()` на `bar()` везде");
    }

    // Сохранённое свойство (на него есть прод-инцидент): открывающий тег, процитированный в
    // код-блоке, маркером НЕ считается — даже если рядом ниже стоит настоящий маркер.
    [Fact]
    public void МаркерРаботы_ЦитатаВКодБлокеИИнлайн_НеСчитается_НастоящийБерётся()
    {
        // Цитата целым fenced-блоком — не активная постановка
        SessionManager.ParseWorkMarker("Протокол:\n```\n<team:work>переписать компонент</team:work>\n```\nПонятно?")
            .Should().BeNull();
        // Инлайн-цитата `<team:work>…</team>` — не маркер; настоящий маркер ниже разбирается
        SessionManager.ParseWorkMarker("Маркер ставится так: `<team:work>…</team>`.\n<team:work>реальная работа</team>")
            .Should().Be("реальная работа");
    }

    // Смежное (та же правка для эскалации): код внутри <escalate:check> не теряется.
    [Fact]
    public void МаркерЭскалации_КодБлокВнутриМаркера_СохраняетсяЦеликом()
    {
        var text = "<escalate:check>падает тут:\n```\nAssert.True(false)\n```\nсмотри трейс</escalate:check>";

        var parsed = SessionManager.ParseEscalationMarker(text);
        parsed.Should().NotBeNull();
        parsed!.Value.Kind.Should().Be(TeamEscalationKind.CheckFailed);
        parsed.Value.Text.Should().Be("падает тут:\n```\nAssert.True(false)\n```\nсмотри трейс");
    }

    // Маркер разговора (<team:talk/>) — самодостаточный тег, та же защита от цитаты в код-блоке.
    [Fact]
    public void МаркерРазговора_ЦитатаВКодБлоке_НеСчитается_НастоящийСрабатывает()
    {
        SessionManager.HasTalkMarker("Вот пример:\n```\n<team:talk/>\n```\nтак выглядит").Should().BeFalse();
        SessionManager.HasTalkMarker("Вопросов нет.<team:talk/>").Should().BeTrue();
    }

    // Волна 6 (живая приёмка волны 5, скриншоты w5-02/w5-03): маркеры протокола протекали в
    // видимый текст координатора — воспроизведено 4 раза, в т.ч. с закрытием тега по имени.
    // StripTeamProtocolMarkers — общая функция очистки и для сохранённой истории (TurnAccumulator),
    // и для живой трансляции (OnMessageAsync), поэтому тестируем сам разбор без харнесса сессии.
    [Theory]
    [InlineData("Каждый файл — отдельная подзадача.\n\n<team:work>распараллелить по файлам</team:work>",
        "Каждый файл — отдельная подзадача.\n\n")]
    // Осиротевший закрывающий тег без пары (прод 2026-08-02, находка Веры) — служебный
    // синтаксис протокола, человеку не место ни в какой форме, вырезаем и без открывающего
    [InlineData("Каждый файл — отдельная подзадача, можно запустить параллельно</team:work>",
        "Каждый файл — отдельная подзадача, можно запустить параллельно")]
    [InlineData("никакие другие файлы проекта не редактировать. </team>",
        "никакие другие файлы проекта не редактировать. ")]
    [InlineData("снимет блокировку запуска.</escalate>", "снимет блокировку запуска.")]
    [InlineData("<team:work>добавить экспорт</team>", "")]
    [InlineData("Либо исполнитель не прочитал источник.\n<escalate:check>суффикс не применён</escalate:check>",
        "Либо исполнитель не прочитал источник.\n")]
    [InlineData("<escalate:deviation>нужен доступ вне владения</escalate>", "")]
    [InlineData("Понял, вопросов нет.<team:talk/>", "Понял, вопросов нет.")]
    [InlineData("Понял, вопросов нет.<team:talk   />", "Понял, вопросов нет.")]
    // Маркер молчания (B4): ход, которому нечего решать, отвечает ровно им — после стрижки
    // от такого хода не остаётся ни символа, и ниже по течению записи в ленте не будет
    [InlineData("<no-reply/>", "")]
    [InlineData("\n<no-reply />\n", "\n\n")]
    [InlineData("Ставлю новую задачу на Дениса.<no-reply/>", "Ставлю новую задачу на Дениса.")]
    [InlineData("обычный текст без маркеров", "обычный текст без маркеров")]
    public void StripTeamProtocolMarkers_ВырезаетЦеликомЗавершённыйМаркер(string input, string expected)
    {
        SessionManager.StripTeamProtocolMarkers(input).Should().Be(expected);
    }

    // Код-блок — цитата протокола, а не активный вызов (симметрично Parse*/Has* выше): человек
    // мог явно попросить координатора объяснить формат, такую цитату вырезать нельзя.
    [Fact]
    public void StripTeamProtocolMarkers_НеТрогаетМаркерВКодБлоке()
    {
        var text = "Протокол:\n```\n<team:work>пример</team:work>\n```\nвот так это выглядит";
        SessionManager.StripTeamProtocolMarkers(text).Should().Be(text);
    }

    // Маркер разъехался по нескольким чанкам стрима (закрывающий тег пришёл отдельной
    // дельтой от открывающего) — функция работает на УЖЕ СКЛЕЕННОМ тексте (TeamTurnText —
    // один StringBuilder на весь ход), поэтому границы исходных дельт для неё не видны.
    [Fact]
    public void StripTeamProtocolMarkers_МаркерСклеенныйИзНесколькихДельт_ВсёРавноВырезается()
    {
        var chunk1 = "Готовлю план. <team:wo";
        var chunk2 = "rk>добавить экспорт";
        var chunk3 = "</team:work> — всё по делу.";
        SessionManager.StripTeamProtocolMarkers(chunk1 + chunk2 + chunk3)
            .Should().Be("Готовлю план.  — всё по делу.");
    }

    // Хвост P19: парсер принимает код-блок внутри маркера (FindPairedMarkerOutsideCode),
    // а зачистка раньше резала по код-блокам — сегмент до фенса оставался в ленте с
    // буквальным <team:work> и всей постановкой, закрывающий тег съедался как осиротевший.
    // Зачистка обязана вырезать диапазон той же позиционной находкой пары, что и разбор.
    [Theory]
    [InlineData("<team:work>перепиши так:\n```\nvar x = 1;\n```\nостальное как есть</team>", "")]
    [InlineData("Погнали.\n<team:work>дамп:\n```tsx\n<Button/>\n```\nверни как есть</team:work>\nГотово.",
        "Погнали.\n\nГотово.")]
    [InlineData("Проверил.\n<escalate:check>вот след:\n```\nstack trace\n```\nне сходится</escalate:check>",
        "Проверил.\n")]
    public void StripTeamProtocolMarkers_МаркерСКодБлокомВнутри_ВырезаетсяЦеликом(string input, string expected)
    {
        SessionManager.StripTeamProtocolMarkers(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("текст <team:wo", "текст ")]
    [InlineData("текст <escalate:che", "текст ")]
    [InlineData("текст <team:talk", "текст ")]
    [InlineData("текст <team:talk ", "текст ")]
    // Маркер молчания приходит по кускам, как и любой другой: ни один его префикс
    // не должен мелькнуть в стриме до того, как мы поймём, что это протокол
    [InlineData("текст <no", "текст ")]
    [InlineData("текст <no-repl", "текст ")]
    [InlineData("текст <no-reply", "текст ")]
    [InlineData("текст <no-reply/", "текст ")]
    [InlineData("текст <", "текст ")]
    public void TrimAmbiguousMarkerTail_ПридерживаетНезавершённыйПрефиксМаркера(string input, string expected)
    {
        SessionManager.TrimAmbiguousMarkerTail(input).Should().Be(expected);
    }

    // Обычный текст, включая случайное «<», не должен придерживаться до бесконечности —
    // иначе живая трансляция обычной прозы координатора зависала бы посреди хода.
    [Theory]
    [InlineData("сравнение 5 < 10 верно")]
    [InlineData("обычный текст без служебных тегов")]
    [InlineData("html-подобное <div> тоже не наш маркер")]
    public void TrimAmbiguousMarkerTail_НеТрогаетТекстБезПротоколаНаХвосте(string input)
    {
        SessionManager.TrimAmbiguousMarkerTail(input).Should().Be(input);
    }

    // Открывающий тег напечатан ЦЕЛИКОМ, но закрытие ещё не пришло — IsAmbiguousMarkerTail
    // такой хвост уже не ловит (он не префикс открывающего тега, он ему равен), поэтому нужна
    // отдельная проверка «висит незакрытый маркер где-то в тексте».
    [Theory]
    [InlineData("Готовлю план. <team:work>", "Готовлю план. ")]
    [InlineData("Готовлю план. <team:work>постановка ещё пишется", "Готовлю план. ")]
    [InlineData("Нашёл проблему: <escalate:check>", "Нашёл проблему: ")]
    [InlineData("Понял. <team:talk", "Понял. ")]
    [InlineData("Решать нечего. <no-reply", "Решать нечего. ")]
    [InlineData("обычный текст без маркеров", "обычный текст без маркеров")]
    public void TrimUnresolvedMarkerOpen_ПрячетТелоЕщёНеЗакрытогоМаркера(string input, string expected)
    {
        SessionManager.TrimUnresolvedMarkerOpen(input).Should().Be(expected);
    }

    // Волна 6: интеграционная проверка живой трансляции через реальный OnMessageAsync — маркер,
    // разбитый на несколько TextDeltaMessage-чанков (как реально стримит CLI — символ за
    // символом или короткими группами), не должен долетать до клиента ни целиком, ни частично
    // ни в одной из дельт.
    [Fact]
    public async Task ЖиваяТрансляция_МаркерРазбитПоЧанкам_НеПротекаетНиВОднойДельте()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-stream-filter");
        var acc = new TurnAccumulator(new List<StoredMessage>());
        string[] chunks =
        [
            "Каждый файл — отдельная подзадача, ", "можно запустить параллельно.\n\n",
            "<team:wo", "rk>", "постановка для планировщика ", "с деталями", "</team:work>",
            " Готово.",
        ];

        foreach (var chunk in chunks)
            await InvokeOnMessageAsync(session.Id, acc, new TextDeltaMessage(chunk), TestRunId);

        var deltas = Sent<TextDeltaMessage>().Select(m => m.Text).ToList();
        deltas.Should().NotBeEmpty();
        foreach (var d in deltas)
        {
            d.Should().NotContain("<team:work>");
            d.Should().NotContain("</team");
            d.Should().NotContain("<team:wo");
        }
        string.Concat(deltas).Should().Be(
            "Каждый файл — отдельная подзадача, можно запустить параллельно.\n\n Готово.");
    }

    // Ход оборвался (рестарт/упавший процесс) СРАЗУ после незавершённого хвоста, похожего на
    // начало маркера, — живая трансляция придержала его (TrimAmbiguousMarkerTail), а следующей
    // дельты, которая подтвердила бы или опровергла маркер, уже не будет. Конец хода обязан
    // довесить придержанный текст, а не потерять его молча.
    [Fact]
    public async Task ЖиваяТрансляция_ХодОборванПослеНезавершённогоХвоста_ДовешиваетЕгоНаКонцеХода()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-stream-catchup");
        var acc = new TurnAccumulator(new List<StoredMessage>());

        await InvokeOnMessageAsync(session.Id, acc,
            new TextDeltaMessage("Собираю план, минуту <team:wo"), TestRunId);
        Sent<TextDeltaMessage>().Select(m => m.Text)
            .Should().ContainSingle().Which.Should().Be("Собираю план, минуту ",
                "хвост «<team:wo» ещё может дорасти до маркера следующей дельтой, поэтому придержан");

        await InvokeOnMessageAsync(session.Id, acc,
            new ErrorMessage("Сервер был перезапущен во время хода — ход прерван"), TestRunId);

        var all = Sent<TextDeltaMessage>().Select(m => m.Text).ToList();
        string.Concat(all).Should().Be("Собираю план, минуту <team:wo",
            "придержанный хвост дальше ничем не резолвится — конец хода обязан довесить его как есть");
    }

    [Fact]
    public async Task МаркерМолчания_ОбычныйЧат_НеПротекаетНиВОднойДельтеИНеДогоняетНаКонцеХода()
    {
        var session = await MkPlainChatAsync("stream");
        var acc = new TurnAccumulator(new List<StoredMessage>());
        // CLI стримит маркер кусками — ни один префикс не должен мелькнуть на экране
        string[] chunks = ["<no", "-rep", "ly", "/>"];

        foreach (var chunk in chunks)
            await InvokeOnMessageAsync(session.Id, acc, new TextDeltaMessage(chunk), TestRunId);
        await InvokeOnMessageAsync(session.Id, acc,
            new ResultMessage("success", 100, 1, null, null), TestRunId);

        Sent<TextDeltaMessage>().Should().BeEmpty(
            "ход, ответивший ровно маркером, не показывает человеку ничего — ни по дельтам, ни догоняющим текстом на конце хода");
    }

    // Маркер после решения по делу: сам ответ доходит целиком, вырезан только маркер.
    [Fact]
    public async Task МаркерМолчания_ПослеТекста_ТекстДоходитЦеликом()
    {
        var session = await MkPlainChatAsync("tail");
        var acc = new TurnAccumulator(new List<StoredMessage>());

        foreach (var chunk in new[] { "Ставлю новую задачу ", "на Дениса.", "<no-reply/>" })
            await InvokeOnMessageAsync(session.Id, acc, new TextDeltaMessage(chunk), TestRunId);
        await InvokeOnMessageAsync(session.Id, acc,
            new ResultMessage("success", 100, 1, null, null), TestRunId);

        string.Concat(Sent<TextDeltaMessage>().Select(m => m.Text))
            .Should().Be("Ставлю новую задачу на Дениса.");
    }

    // Обычный чат без всякого протокола: стрижка не должна ни задерживать, ни менять прозу
    // (быстрый путь дельт — текст уходит как есть, символ в символ).
    [Fact]
    public async Task ОбычныйЧат_ТекстБезМаркеров_ДоходитБезИзменений()
    {
        var session = await MkPlainChatAsync("plain");
        var acc = new TurnAccumulator(new List<StoredMessage>());
        string[] chunks = ["Сравнил 5 < 10 ", "и <div> в примере — ", "всё на месте."];

        foreach (var chunk in chunks)
            await InvokeOnMessageAsync(session.Id, acc, new TextDeltaMessage(chunk), TestRunId);
        await InvokeOnMessageAsync(session.Id, acc,
            new ResultMessage("success", 100, 1, null, null), TestRunId);

        string.Concat(Sent<TextDeltaMessage>().Select(m => m.Text)).Should().Be(string.Concat(chunks));
    }

    // Буфер стрижки в обычном чате сжимается, как только всё показано (иначе ход с кодом
    // пересканировался бы целиком на каждой дельте). Сжатие не должно ломать разбор маркера,
    // пришедшего ПОСЛЕ такого текста: угловые скобки прозы буфер не удерживают.
    [Fact]
    public async Task МаркерМолчания_ПослеТекстаСУгловымиСкобками_ВырезаетсяВсёРавно()
    {
        var session = await MkPlainChatAsync("angles");
        var acc = new TurnAccumulator(new List<StoredMessage>());

        foreach (var chunk in new[] { "Смотри <Button>", " и <div>.", "<no-", "reply/>" })
            await InvokeOnMessageAsync(session.Id, acc, new TextDeltaMessage(chunk), TestRunId);
        await InvokeOnMessageAsync(session.Id, acc,
            new ResultMessage("success", 100, 1, null, null), TestRunId);

        string.Concat(Sent<TextDeltaMessage>().Select(m => m.Text))
            .Should().Be("Смотри <Button> и <div>.");
    }

    // Цитата протокола в код-блоке — не активный вызов (симметрично HasTalkMarker)
    [Fact]
    public void HasNoReplyMarker_ЦитатаВКодБлоке_НеСчитается_НастоящийСрабатывает()
    {
        SessionManager.HasNoReplyMarker("Отвечай так:\n```\n<no-reply/>\n```\nи всё").Should().BeFalse();
        SessionManager.HasNoReplyMarker("<no-reply/>").Should().BeTrue();
        SessionManager.HasNoReplyMarker("Решать нечего.").Should().BeFalse();
    }

    [Fact]
    public async Task ВводнаяЧеловека_ВОжидании_СбрасываетБюджетИтерацииИСнимаетОстановку()
    {
        // M6: сброс делает не приём сообщения, а классификация вводной как работы (спека
        // «Бюджет»). Разговорное сообщение на приёме не трогает ни потолки, ни «Остановить».
        var (session, backend, _) = await MakeIdleStabAsync("ti-reset");
        SetAdditionalPlannerAnswer(backend);
        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.Budget.TasksUsed = 7;
        team.Budget.WavesUsed = 3;
        team.Budget.RunsUsed = 11;
        team.Budget.WakeupsUsed = 4;
        team.ClosedWave = 2;
        await _sut.StopTeamImplementAsync(session.Id, TestUserId);
        // Занятый чат: ход не запускаем — вводная встаёт в очередь
        session.Status = SessionStatus.Working;

        await _sut.SendMessageAsync(session.Id, "теперь добавь экспорт в XLSX", []);

        var mid = _sut.GetById(session.Id)!.TeamImplement!;
        mid.Budget.TasksUsed.Should().Be(7, "на приёме сообщение ещё не классифицировано");
        mid.Stopped.Should().BeTrue();

        // Координатор классифицировал вводную как работу — итерация открывается заново
        SetTeamTurnFromHuman(GetEntry(session.Id), true);
        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>добавить экспорт в XLSX</team>", failed: false);

        var after = _sut.GetById(session.Id)!.TeamImplement!;
        after.Budget.TasksUsed.Should().Be(0);
        after.Budget.WavesUsed.Should().Be(0);
        after.Budget.RunsUsed.Should().Be(0);
        after.Budget.WakeupsUsed.Should().Be(0);
        after.ClosedWave.Should().Be(0, "новый план начинает счёт волн заново");
        after.Stopped.Should().BeFalse("«Остановить» относилось к прошлой итерации");
    }

    [Fact]
    public async Task ВводнаяЧеловека_ПосредиВолны_БюджетНеСбрасывает()
    {
        // Иначе потолок обходится тривиально: пиши в чат почаще, и работающая практика
        // получает бесконечный бюджет
        var (session, _, _) = await MakeTeamStabAsync("ti-reset-wave");
        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.Stage = TeamImplementStage.Wave;
        team.Budget.RunsUsed = 9;
        session.Status = SessionStatus.Working;

        await _sut.SendMessageAsync(session.Id, "как там дела?", []);

        _sut.GetById(session.Id)!.TeamImplement!.Budget.RunsUsed.Should().Be(9);
    }

    [Fact]
    public async Task СбросБюджета_ДействиямиАгента_Недостижим()
    {
        // Главный риск непрерывного контура: сброс бюджета — вечный двигатель, если до него
        // дотягивается агент. Путь к сбросу ровно один — сообщение человека через хаб.
        var (session, _, _) = await MakeIdleStabAsync("ti-reset-agent");
        var child = await _sut.CreateAsync(session.ProjectId!, ClaudeMode.Auto, name: "Задача: экспорт");
        _sut.SetParent(child.Id, session.Id, TestUserId);
        session.ClaudeSessionId ??= "cli-" + Guid.NewGuid().ToString("N");
        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.Budget.TasksUsed = 7;
        team.Budget.RunsUsed = 11;
        session.Status = SessionStatus.Working;

        // 1. chats_send из чужого чата (агентский REST-канал)
        await _sut.SendMessageAndWaitAsync(session.Id, "продолжай работу", TimeSpan.Zero, agentDepth: 1);
        // 2. служебный ход-реакция (доклад исполнителя, сводка волны)
        await _sut.SendOrEnqueueAsync(session.Id, "отреагируй на доклад", silent: true, suppressTasksExecute: true);
        // 3. доклад-блокер снизу — единственное, что он двигает, это счётчик пробуждений
        await _sut.ReportBlockerAsync(child.Id, "застрял без доступа", TestUserId);

        var after = _sut.GetById(session.Id)!.TeamImplement!.Budget;
        after.TasksUsed.Should().Be(7, "агент не открывает новую итерацию");
        after.RunsUsed.Should().Be(11);
        after.WakeupsUsed.Should().BeGreaterThan(0, "пробуждение штаба агентом, наоборот, расходует квоту");
    }

    [Fact]
    public async Task МаркерРаботы_ВОжиданииПриАвтоВолнах_РазворачиваетВолнуБезПодтверждения()
    {
        var (session, backend, _) = await MakeIdleStabAsync("ti-additional");
        SetAdditionalPlannerAnswer(backend);
        TeamImplementPlan? handed = null;
        _sut.TeamHandlers.WaveStarter = (_, p, _) => { handed = p; return Task.CompletedTask; };
        // Вводная человека: классификация работой открыла новую итерацию (бюджет и счёт волн
        // с нуля — M6: сброс по маркеру, а не по приёму сообщения)
        session.Status = SessionStatus.Working;
        await _sut.SendMessageAsync(session.Id, "теперь добавь выгрузку в XLSX", []);
        SetTeamTurnFromHuman(GetEntry(session.Id), true);

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "Понял, беру в работу.\n<team:work>добавить выгрузку в XLSX</team>", failed: false);

        // Карточка состава опубликована, но клика не ждёт — работа уже роздана
        var planCard = Sent<TeamPlanMessage>().Last();
        planCard.Resolved.Should().BeTrue("добавочный план подтверждения не ждёт");
        planCard.Approved.Should().BeTrue();
        handed.Should().NotBeNull("волна раздана тем же путём, что и по кнопке «Запустить»");
        handed!.Subtasks.Should().ContainSingle().Which.ExecutorPersonaId.Should().Be(backend.Id);

        // Информационная карточка с единственной кнопкой «Остановить»
        var info = Sent<TeamEscalationMessage>().Last();
        info.Kind.Should().Be("waveAdded");
        info.Resolved.Should().BeFalse();
        info.Actions.Select(a => a.Id).Should().Equal("stop");
        info.Details.Should().Contain("XLSX");

        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Stage.Should().Be(TeamImplementStage.Wave, "информационная карточка практику не останавливает");
        ti.PlannedWaves.Should().Be(1, "плановое число волн — из добавочного плана");
        ti.PlanCardId.Should().Be(planCard.PlanId);
    }

    [Fact]
    public async Task МаркерРаботы_АвтоВолныСняты_ПланЖдётПодтверждения()
    {
        // Первоначальный план подтверждается всегда; при снятом авто — и добавочный
        var (session, backend, _) = await MakeIdleStabAsync("ti-additional-manual");
        SetAdditionalPlannerAnswer(backend);
        await _sut.SetTeamImplementAutoAsync(session.Id, autoWaves: false, userId: TestUserId);
        var started = false;
        _sut.TeamHandlers.WaveStarter = (_, _, _) => { started = true; return Task.CompletedTask; };

        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>добавить выгрузку в XLSX</team>", failed: false);

        Sent<TeamPlanMessage>().Last().Resolved.Should().BeFalse();
        started.Should().BeFalse("без авто-волн работа ждёт кнопки «Запустить»");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Confirming);
    }

    [Fact]
    public async Task РазговорноеСообщение_БезМаркера_НиПланаНиВолны()
    {
        var (session, _, _) = await MakeIdleStabAsync("ti-talk");
        var started = false;
        _sut.TeamHandlers.WaveStarter = (_, _, _) => { started = true; return Task.CompletedTask; };

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "Киру выбрал планировщик: фронтовая часть — её зона.", failed: false);

        Sent<TeamPlanMessage>().Should().BeEmpty("вопрос не создаёт плана и задач");
        started.Should().BeFalse();
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Idle);
    }

    [Fact]
    public async Task МаркерРаботы_ПосредиИдущейВолны_Игнорируется()
    {
        var (session, backend, _) = await MakeIdleStabAsync("ti-work-in-wave");
        SetAdditionalPlannerAnswer(backend);
        _sut.GetById(session.Id)!.TeamImplement!.Stage = TeamImplementStage.Wave;

        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>ещё одна фича</team>", failed: false);

        Sent<TeamPlanMessage>().Should().BeEmpty(
            "вторая волна поверх идущей не разворачивается — сначала доигрывает текущая");
    }

    [Fact]
    public async Task МаркерРаботы_ПланировщикНеСмог_ЧеловекПолучаетКарточку()
    {
        // «не понял задачу» — не JSON, без открытой скобки: парсер даёт InvalidJson.
        // Раньше текст карточки был общий «уточните задачу», теперь — отдельный
        // «вернул неразборчивый план» (прод 2026-08-05). Сбой планировщика и сбой
        // парсера — разные вещи: парсер советует «повторите», не «уточните».
        var (session, _, _) = await MakeIdleStabAsync("ti-work-noplan");
        _plannerAnswer = "не понял задачу";

        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>сделай хорошо</team>", failed: false);

        var card = Sent<TeamEscalationMessage>().Last();
        card.Kind.Should().Be("productDecision");
        card.Title.Should().Contain("не построился");
        card.Details.Should().Contain("неразборчивый план");
        // Повторить планирование можно кнопкой — без повторного интервью
        card.Actions.Select(a => a.Id).Should().Equal("retryPlan");
        _sut.GetById(session.Id)!.TeamImplement!.LastPlanRequest.Should().Be("сделай хорошо");
    }

    [Fact]
    public async Task МаркерРаботы_ТаймаутПланировщика_ЧестнаяКарточка()
    {
        // Прод 2026-08-04: планировщик оборвался по таймауту, а человек прочитал
        // «уточните задачу» — теперь карточка называет настоящую причину.
        var (session, _, _) = await MakeTeamStabAsync("ti-work-timeout");
        _plannerException = new ClaudeHomeServer.Services.Llm.LlmTimeoutException();

        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>большая фича с кучей частей</team>", failed: false);

        var card = _sentMessages.OfType<TeamEscalationMessage>().Last();
        card.Kind.Should().Be("productDecision");
        card.Title.Should().Contain("не уложился");
        card.Details.Should().Contain("не уложился во время", "причина отказа — таймаут, а не постановка")
            .And.NotContain("Уточните задачу");
        card.Actions.Select(a => a.Id).Should().Equal("retryPlan");
        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Stage.Should().Be(TeamImplementStage.AwaitingDecision);
        ti.LastPlanRequest.Should().Be("большая фича с кучей частей");
    }

    [Fact]
    public async Task КнопкаПовторитьПланирование_СтроитПланБезПовторногоИнтервью()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-work-retry");
        _plannerException = new ClaudeHomeServer.Services.Llm.LlmTimeoutException();
        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>сделай экспорт задач в CSV</team>", failed: false);
        var card = _sentMessages.OfType<TeamEscalationMessage>().Last();

        // Планировщик ожил — человек жмёт «Повторить планирование»
        _plannerException = null;
        SetPlannerAnswer(backend, frontend);
        var ok = await _sut.RespondTeamEscalationAsync(session.Id, card.EscalationId, "retryPlan",
            userId: TestUserId);

        ok.Should().BeTrue();
        _plannerStub.Calls.Should().Be(2, "первая попытка + один повтор по кнопке");
        _plannerStub.LastPrompt.Should().Contain("сделай экспорт задач в CSV",
            "повтор идёт по сохранённой вводной дословно, без нового хода координатору");
        // Интервью заново не проходится: сразу карточка плана на подтверждение
        var planCard = _sentMessages.OfType<TeamPlanMessage>().Last();
        planCard.Resolved.Should().BeFalse();
        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Stage.Should().Be(TeamImplementStage.Confirming);
        ti.LastPlanRequest.Should().BeNull("успешный план обнуляет сохранённую вводную");
    }

    // «Остановить» с информационной карточки добавочной волны и кнопка режима — ОДНА точка
    // (сбой 28.08.2026): раньше карточный путь ставил только состояние, карточки возврата
    // («Продолжить»/«Завершить итерацию») не появлялось, и продолжить практику было нечем.
    [Fact]
    public async Task ДобавочнаяВолна_КнопкаОстановить_ОстанавливаетИПубликуетКарточкуВозврата()
    {
        var (session, backend, _) = await MakeIdleStabAsync("ti-additional-stop");
        SetAdditionalPlannerAnswer(backend);
        _sut.TeamHandlers.WaveStarter = (_, _, _) => Task.CompletedTask;
        SetTeamTurnFromHuman(GetEntry(session.Id), true);
        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>добавить XLSX</team>", failed: false);
        var info = Sent<TeamEscalationMessage>().Last();

        var ok = await _sut.RespondTeamEscalationAsync(session.Id, info.EscalationId, "stop", userId: TestUserId);

        ok.Should().BeTrue();
        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Stopped.Should().BeTrue("новые волны не стартуют, пока человек не продолжит");
        // Работа не возобновляется: карточка возврата штатно переводит практику в «ждёт
        // решения» — ровно как кнопка режима в ChatsController (единая точка остановки)
        ti.Stage.Should().Be(TeamImplementStage.AwaitingDecision);
        var stopCard = (await ((ITeamHistoryStore)_sut).GetOpenTeamEscalationsAsync(session.Id))
            .Single(e => e.Kind == TeamEscalationKind.Stopped);
        stopCard.Actions.Select(a => a.Id).Should().Equal(["resume", "finish"],
            "продолжить остановленную практику можно только по этой карточке");
        Sent<TeamEscalationMessage>()
            .Should().ContainSingle(m => m.Kind == "stopped" && !m.Resolved);
    }

    // Повторное «Остановить» (новая добавочная волна поверх уже остановленной практики либо
    // повторный клик кнопки режима) не должно плодить вторую карточку возврата: человек
    // решает по той, что уже висит открытой.
    [Fact]
    public async Task ДобавочнаяВолна_ПовторнаяОстановка_КарточкуВозвратаНеДублирует()
    {
        var (session, backend, _) = await MakeIdleStabAsync("ti-additional-stop-twice");
        SetAdditionalPlannerAnswer(backend);
        _sut.TeamHandlers.WaveStarter = (_, _, _) => Task.CompletedTask;
        SetTeamTurnFromHuman(GetEntry(session.Id), true);
        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>добавить XLSX</team>", failed: false);
        var info = Sent<TeamEscalationMessage>().Last();
        await _sut.RespondTeamEscalationAsync(session.Id, info.EscalationId, "stop", userId: TestUserId);

        // Вторая добавочная волна — снова «Остановить», затем и кнопка режима
        var second = new TeamEscalation
        {
            Kind = TeamEscalationKind.WaveAdded,
            Title = "Добавочная волна 2",
            Actions = TeamEscalationActions.For(TeamEscalationKind.WaveAdded),
        };
        await ((ITeamHistoryStore)_sut).PublishTeamEscalationAsync(session.Id, second);
        var ok = await _sut.RespondTeamEscalationAsync(session.Id, second.Id, "stop", userId: TestUserId);
        ok.Should().BeTrue();
        await _sut.StopTeamImplementAsync(session.Id, TestUserId);

        (await ((ITeamHistoryStore)_sut).GetOpenTeamEscalationsAsync(session.Id))
            .Count(e => e.Kind == TeamEscalationKind.Stopped)
            .Should().Be(1, "открытая карточка возврата уже висит — вторая была бы спамом");
        Sent<TeamEscalationMessage>()
            .Count(m => m.Kind == "stopped" && !m.Resolved)
            .Should().Be(1, "и в ленте карточка остановки ровно одна");
        _sut.GetById(session.Id)!.TeamImplement!.Stopped.Should().BeTrue();
    }

    [Fact]
    public async Task ВводнаяЧеловека_ОткрываетИнтервьюИПланРежим()
    {
        var (session, _, _) = await MakeInterviewStabAsync("ti-interview-open");

        var after = _sut.GetById(session.Id)!;
        after.TeamImplement!.Stage.Should().Be(TeamImplementStage.Interview,
            "первая вводная итерации проходит интервью всегда");
        after.Mode.Should().Be(ClaudeMode.Plan, "интервью и планирование идут в план-режиме");
        after.TeamImplement.SavedMode.Should().Be(ClaudeMode.Auto,
            "режим человека запомнен и вернётся после согласования плана");
        after.TeamImplement.InterviewRounds.Should().Be(0, "вопросов ещё не задавали");
        var ws = Sent<TeamImplementMessage>().Last();
        ws.Stage.Should().Be("interview");
        ws.ModeLocked.Should().BeTrue("селектор режима в композере заблокирован");
    }

    [Fact]
    public async Task ОтветНаИнтервью_НеВводная_БюджетНеСбрасывает()
    {
        // Иначе цикл «вопрос — ответ» обнулял бы потолки: каждый ответ человека открывал бы
        // новую итерацию, и бюджет перестал бы что-либо ограничивать
        var (session, _, _) = await MakeInterviewStabAsync("ti-interview-budget");
        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.Budget.TasksUsed = 5;
        team.Budget.RunsUsed = 8;

        await _sut.SendMessageAsync(session.Id, "давай второй вариант", []);

        var after = _sut.GetById(session.Id)!.TeamImplement!;
        after.Budget.TasksUsed.Should().Be(5);
        after.Budget.RunsUsed.Should().Be(8);
        after.Stage.Should().Be(TeamImplementStage.Interview, "ответ не двигает стадию");
    }

    // Дыра покрытия (волна 3): «кристальная» задача — координатор не видит развилок и
    // сразу закрывает интервью маркером работы, ни разу не спросив ASK-карточкой (подтверждено
    // живьём, заход 5 живой приёмки). InterviewRounds обязан остаться 0, план строится сразу.
    [Fact]
    public async Task Интервью_КристальнаяЗадача_СтроитПланБезЕдиногоВопроса()
    {
        var (session, backend, frontend) = await MakeInterviewStabAsync("ti-no-questions",
            "создай about.html с фиксированным содержимым");
        SetPlannerAnswer(backend, frontend);

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "<team:work>about.html, развилок нет</team>", failed: false);

        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.InterviewRounds.Should().Be(0, "координатор не задал ни одного вопроса");
        ti.Stage.Should().Be(TeamImplementStage.Confirming, "план построен и ждёт подтверждения человека");
        Sent<TeamPlanMessage>().Should().ContainSingle();
    }

    [Fact]
    public async Task ВопросКоординатора_ВИнтервью_СчитаетсяРаундом()
    {
        var (session, _, _) = await MakeInterviewStabAsync("ti-interview-round");

        await _sut.OnAskQuestionStabAsync(session.Id);
        await _sut.OnAskQuestionStabAsync(session.Id);

        _sut.GetById(session.Id)!.TeamImplement!.InterviewRounds.Should().Be(2,
            "протокол разрешает не больше двух раундов на вводную — счёт ведёт бэкенд");
    }

    // Единый канал вопросов (запрос владельца 2026-08-04): развилку в живом ходу координатор
    // задаёт ASK-карточкой, а не маркером <escalate:decision> с карточкой-«простынёй». ASK
    // блокирует ход до ответа, после — работа продолжается с того же места: возврат в
    // интервью с паузой волн и перепланированием делал бы из любого вопроса пересборку плана
    // (остался только за явным маркером <escalate:clarify>).
    [Fact]
    public async Task ВопросВСерединеВолны_НеВозвращаетВИнтервью_ПрактикаИдётДальше()
    {
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-ask-in-wave");
        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run, userId: TestUserId);
        ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
        {
            t.WaveNumber = 1;
            t.WaveStartedAt = DateTime.UtcNow;
            return true;
        });

        await _sut.OnAskQuestionStabAsync(session.Id);

        var after = _sut.GetById(session.Id)!.TeamImplement!;
        after.Stage.Should().Be(TeamImplementStage.Wave, "вопрос не останавливает волну");
        after.Replanning.Should().BeFalse("перепланирование вопросом не запускается");
        after.InterviewRounds.Should().Be(0, "раунды интервью тратятся только в самом интервью");
        after.WaveStartedAt.Should().BeNull(
            "пока человек отвечает на ASK, сторож зависших волн не тикает — ожидание человека не зависание");
        _sentMessages.OfType<TeamEscalationMessage>()
            .Should().NotContain(m => m.Kind == "needsClarification",
                "карточка «нужны уточнения» на вопрос в живом ходу больше не публикуется");
    }

    [Fact]
    public async Task КонецХодаПослеВопросаВВолне_ВозвращаетОтсечкиСторожа()
    {
        // Иначе после ответа человека волна осталась бы без надзора навсегда: вопрос погасил
        // отсечки, а настоящий stall волн никто бы больше не поймал
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-ask-restore");
        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run, userId: TestUserId);
        ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
        {
            t.WaveNumber = 1;
            t.WaveStartedAt = DateTime.UtcNow;
            return true;
        });
        await _sut.OnAskQuestionStabAsync(session.Id);
        _sut.GetById(session.Id)!.TeamImplement!.WaveStartedAt.Should().BeNull();

        // Человек ответил ASK-карточкой, ход продолжился и завершился без маркера
        await _sut.HandleTeamTurnEndAsync(session.Id, "Продолжаю по решению человека.", failed: false);

        var after = _sut.GetById(session.Id)!.TeamImplement!;
        after.Stage.Should().Be(TeamImplementStage.Wave);
        after.WaveStartedAt.Should().NotBeNull("после конца хода сторож снова тикает");
    }

    [Fact]
    public async Task ОбрывХодаБезResult_ПослеВопросаВВолне_ТожеВозвращаетОтсечкиСторожа()
    {
        // Прерывание или смерть процесса посреди ASK не шлёт result — HandleTeamTurnEndAsync
        // по такому ходу не зовётся; без возврата отсечек волна осталась бы без надзора.
        // Этап 4 / шаг 2в: путь с ExitedMessage перенесён на шину turn/completed —
        // публикуем Outcome=interrupted через PublishTurnCompletedAsync (а не InvokeOnMessageAsync).
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-ask-interrupted");
        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run, userId: TestUserId);
        ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
        {
            t.WaveNumber = 1;
            t.WaveStartedAt = DateTime.UtcNow;
            return true;
        });
        await _sut.OnAskQuestionStabAsync(session.Id);
        _sut.GetById(session.Id)!.TeamImplement!.WaveStartedAt.Should().BeNull();

        // Шина публикует turn/completed один раз на ход, подписчик HandleTeamTurnCompletedShim
        // восстанавливает сторож на исходе interrupted. Перед публикацией — Working/LastTurnSeq,
        // иначе подписчик не найдёт entry.
        var entry = GetEntry(session.Id);
        _sut.GetById(session.Id)!.Status = SessionStatus.Working;
        SetLastTurnSeq(entry, 7);
        await PublishTurnCompletedAsync(_sut, session.Id, 7, "interrupted");

        // Шина — fire-and-forget, обработка асинхронная.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline
               && _sut.GetById(session.Id)!.TeamImplement!.WaveStartedAt is null)
        {
            await Task.Delay(25);
        }

        _sut.GetById(session.Id)!.TeamImplement!.WaveStartedAt.Should().NotBeNull(
            "обрыв хода возвращает сторожа зависших волн — теперь через подписку turn/completed");
    }

    // Фолбэк единого канала: маркер <escalate:decision> из протокола координатора ушёл, но
    // парсер его принимает (старый транскрипт, привычка модели) — карточка с полем свободного
    // ответа лучше молчаливого зависания.
    [Fact]
    public async Task МаркерDecision_КакФолбэк_ПубликуетКарточкуСПолем()
    {
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-decision-fallback");
        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run, userId: TestUserId);
        ((ITeamRunState)_sut).WithTeamState(session.Id, t => { t.WaveNumber = 1; return true; });

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "Нужно ваше решение.\n<escalate:decision>CSV или XLSX?</escalate>", failed: false);

        var card = _sentMessages.OfType<TeamEscalationMessage>().Last();
        card.Kind.Should().Be("productDecision");
        card.Details.Should().Contain("CSV или XLSX");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "карточка остановки практику ждёт решения — как до единого канала вопросов");
    }

    [Fact]
    public async Task МаркерРаботы_ИзИнтервью_ДаётПланПервойВерсииОтЛицаПланировщика()
    {
        var (session, backend, frontend) = await MakeInterviewStabAsync("ti-interview-plan");
        SetPlannerAnswer(backend, frontend);

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "Вопросов нет — постановка ясна.\n<team:work>экспорт задач в CSV</team>", failed: false);

        var card = Sent<TeamPlanMessage>().Last();
        card.Plan.Version.Should().Be(1);
        card.Resolved.Should().BeFalse("первоначальный план итерации ждёт подтверждения всегда");
        card.Plan.PlannerPersonaId.Should().NotBeNull(
            "карточка плана идёт от лица планировщика — авторство несёт сам Plan, без дублирующего поля в TeamPlanMessage");

        var after = _sut.GetById(session.Id)!;
        after.TeamImplement!.Stage.Should().Be(TeamImplementStage.Confirming);
        after.TeamImplement.PlanVersion.Should().Be(1);
        after.Mode.Should().Be(ClaudeMode.Plan, "план-режим держится до клика «Запустить»");
    }

    [Fact]
    public async Task RespondTeamPlan_Запустить_ВозвращаетРежимЧеловекаИФиксируетВерсию()
    {
        var (session, backend, frontend) = await MakeInterviewStabAsync("ti-interview-run");
        SetPlannerAnswer(backend, frontend);
        _sut.TeamHandlers.WaveStarter = (_, _, _) => Task.CompletedTask;
        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>экспорт</team>", failed: false);
        var planId = Sent<TeamPlanMessage>().Last().PlanId;

        await _sut.RespondTeamPlanAsync(session.Id, planId, TeamPlanDecision.Run, userId: TestUserId);

        var after = _sut.GetById(session.Id)!;
        after.Mode.Should().Be(ClaudeMode.Auto, "после согласования чат работает в прежнем режиме человека");
        after.TeamImplement!.SavedMode.Should().BeNull();
        after.TeamImplement.ApprovedPlanVersion.Should().Be(1, "работа разрешена этой версии плана");
        after.TeamImplement.Stage.Should().Be(TeamImplementStage.Wave);
        Sent<TeamImplementMessage>().Last().ModeLocked.Should().BeFalse(
            "селектор режима снова разблокирован");
    }

    [Fact]
    public async Task SetMode_ПокаШтабПланирует_Отклоняется()
    {
        var (session, _, _) = await MakeInterviewStabAsync("ti-interview-setmode");

        var act = () => _sut.SetMode(session.Id, "acceptEdits");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Штаб планирует*");
        _sut.GetById(session.Id)!.Mode.Should().Be(ClaudeMode.Plan);
    }

    [Fact]
    public async Task ExitPlanMode_ВРежимеШтаба_ЗапрещёнКоординатору()
    {
        // Иначе в план-режиме CLI сам предложит завершить планирование карточкой plan_review,
        // и человек получит два согласования подряд: штатное и нашу карточку плана
        var (session, _, _) = await MakeTeamStabAsync("ti-exitplan");
        var build = typeof(SessionManager).GetMethod("BuildExtraDisallowed",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var inMode = (IReadOnlyList<string>?)build.Invoke(_sut, [TestUserId, null, _sut.GetById(session.Id)!]);
        await _sut.SetTeamImplementAsync(session.Id, enabled: false, userId: TestUserId);
        var offMode = (IReadOnlyList<string>?)build.Invoke(_sut, [TestUserId, null, _sut.GetById(session.Id)!]);

        inMode.Should().Contain("ExitPlanMode");
        (offMode ?? []).Should().NotContain("ExitPlanMode", "вне режима штатное согласование плана работает как обычно");
    }

    [Fact]
    public async Task МаркерУточнений_ВВолне_СтавитВолныНаПаузуИВозвращаетВИнтервью()
    {
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-clarify");
        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run, userId: TestUserId);
        ((ITeamRunState)_sut).WithTeamState(session.Id, t => { t.WaveStartedAt = DateTime.UtcNow; return true; });

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "<escalate:clarify>непонятно, куда класть выгрузку</escalate>", failed: false);

        var after = _sut.GetById(session.Id)!;
        after.TeamImplement!.Stage.Should().Be(TeamImplementStage.Interview);
        after.TeamImplement.Replanning.Should().BeTrue("следующий план — новая версия, и её надо утвердить");
        after.TeamImplement.WaveStartedAt.Should().BeNull("в интервью таймаут волны не тикает");
        after.Mode.Should().Be(ClaudeMode.Plan, "перепланирование тоже идёт в план-режиме");

        var card = Sent<TeamEscalationMessage>().Last();
        card.Kind.Should().Be("needsClarification");
        card.Title.Should().Be("Нужны уточнения — волны на паузе");
        card.Actions.Should().BeEmpty("ответы придут ASK-карточками, кнопок у этой карточки нет");
        card.Details.Should().Contain("куда класть выгрузку");
        card.PersonaId.Should().NotBeNull("карточка идёт от лица координатора");
    }

    [Fact]
    public async Task ПланПослеУточнений_ЖдётПодтвержденияДажеПриАвтоВолнах()
    {
        // Авто-волны покрывают волны по неизменному плану, но не смену самого плана
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-replan");
        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run, userId: TestUserId);
        await _sut.HandleTeamTurnEndAsync(session.Id,
            "<escalate:clarify>неясен формат</escalate>", failed: false);
        var started = false;
        _sut.TeamHandlers.WaveStarter = (_, _, _) => { started = true; return Task.CompletedTask; };
        _plannerAnswer = $$"""
            {"summary":"Экспорт в XLSX","assumptions":["формат — XLSX, как в соседнем модуле"],
             "changes":["CSV заменён на XLSX","под-задача про кнопку убрана"],
             "subtasks":[{"title":"Выгрузка XLSX","goal":"писать xlsx",
              "executorPersonaId":"","executorRationale":"серверная часть",
              "files":["backend/Export.cs"],"wave":1,"doneCriteria":"файл открывается"}]}
            """;

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "<team:work>переделать экспорт на XLSX</team>", failed: false);

        var card = Sent<TeamPlanMessage>().Last();
        card.Plan.Version.Should().Be(2, "план vN после уточнений");
        card.Resolved.Should().BeFalse("новая версия плана требует подтверждения и при авто-волнах");
        card.Plan.Assumptions.Should().ContainSingle().Which.Should().Contain("XLSX");
        card.Plan.Changes.Should().HaveCount(2, "блок «Что изменилось» — от планировщика");
        started.Should().BeFalse("до «Запустить» работа не идёт");

        var after = _sut.GetById(session.Id)!.TeamImplement!;
        after.Stage.Should().Be(TeamImplementStage.Confirming);
        after.PlanVersion.Should().Be(2);
        after.ApprovedPlanVersion.Should().Be(1, "подтверждена пока только прежняя версия");
        after.Replanning.Should().BeFalse("признак снят публикацией новой версии");
    }

    [Fact]
    public async Task ПравкаПлана_ДаётНовуюВерсиюИГаситСтаруюКарточку()
    {
        // Критерий приёмки: правка → версия выросла, новая карточка опубликована, старая погашена
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-edit-plan");
        SetReplannedPlannerAnswer(GetAnyPersona(session), "ревью убрано из плана");
        var started = false;
        _sut.TeamHandlers.WaveStarter = (_, _, _) => { started = true; return Task.CompletedTask; };

        var updated = await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Edit,
            feedback: "убери ревью из плана", userId: TestUserId);

        updated.Should().NotBeNull();
        // Новая версия опубликована и ждёт подтверждения (даже при авто-волнах)
        var newCard = _sentMessages.OfType<TeamPlanMessage>().Last();
        newCard.PlanId.Should().NotBe(plan.Id, "перепланирование — новая карточка");
        newCard.Plan.Version.Should().Be(2, "версия плана выросла");
        newCard.Resolved.Should().BeFalse("новая версия требует подтверждения и при авто-волнах");
        newCard.SupersededBy.Should().BeNull("свежая карточка ничем не заменена");
        // Старая карточка погашена как заменённая — живых кнопок запуска у неё больше нет
        var oldCard = _sentMessages.OfType<TeamPlanMessage>().Last(m => m.PlanId == plan.Id);
        oldCard.Resolved.Should().BeTrue("устаревшая карточка гаснет сразу при входе в перепланирование");
        oldCard.Approved.Should().BeFalse();
        oldCard.SupersededBy.Should().Be(2, "карточка помечена версией, что её заменила");
        started.Should().BeFalse("до «Запустить» работа не идёт");
        // Правка дошла до планировщика дословно
        _plannerStub.LastPrompt.Should().Contain("убери ревью из плана");

        var after = _sut.GetById(session.Id)!.TeamImplement!;
        after.Stage.Should().Be(TeamImplementStage.Confirming);
        after.PlanVersion.Should().Be(2);
        after.ApprovedPlanVersion.Should().Be(0, "новая версия ещё не подтверждена");
        after.Replanning.Should().BeFalse("признак снят публикацией новой версии");
        after.LastPlanFeedback.Should().BeNull("успешный план обнуляет сохранённую правку");
    }

    [Fact]
    public async Task ПравкаПлана_ЗапуститьПоСтаройКарточке_НеПроходит()
    {
        // У устаревшей версии кнопок запуска нет: после публикации v2 клик «Запустить» по v1
        // не стартует волну (карточка уже разрешена — FindTeamPlan её не находит)
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-edit-stale-run");
        SetReplannedPlannerAnswer(GetAnyPersona(session), "ревью убрано");
        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Edit,
            feedback: "убери ревью", userId: TestUserId);
        var started = false;
        _sut.TeamHandlers.WaveStarter = (_, _, _) => { started = true; return Task.CompletedTask; };

        var result = await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run,
            userId: TestUserId);

        result.Should().BeNull("решение по устаревшей карточке не проходит");
        started.Should().BeFalse();
        _sut.GetById(session.Id)!.TeamImplement!.ApprovedPlanVersion.Should().Be(0,
            "старая версия не стала подтверждённой");
    }

    [Fact]
    public async Task ПравкаПлана_СбойПланировщика_ДаётКарточкуСПричиной()
    {
        // Критерий приёмки: правка → сбой планировщика → карточка с причиной (молчаливого тупика нет)
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-edit-fail");
        _plannerAnswer = "не понял, что менять";

        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Edit,
            feedback: "убери ревью из плана", userId: TestUserId);

        var card = _sentMessages.OfType<TeamEscalationMessage>().Last();
        card.Kind.Should().Be("productDecision");
        card.Title.Should().Contain("не привела к новой версии");
        card.Details.Should().Contain("убери ревью из плана", "карточка цитирует правку человека")
            .And.Contain("Повторить планирование");
        card.Actions.Select(a => a.Id).Should().Equal("retryPlan");
        // Старая карточка уже погашена как заменённая — без карточки отказа человек остался бы без плана
        var oldCard = _sentMessages.OfType<TeamPlanMessage>().Last(m => m.PlanId == plan.Id);
        oldCard.SupersededBy.Should().Be(2);

        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Stage.Should().Be(TeamImplementStage.AwaitingDecision);
        ti.LastPlanFeedback.Should().Be("убери ревью из плана", "правка сохранена для повтора");
        ti.LastPlanRequest.Should().Be(plan.Request);
    }

    [Fact]
    public async Task ПравкаПлана_ТаймаутПланировщика_ЧестнаяКарточка()
    {
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-edit-timeout");
        _plannerException = new ClaudeHomeServer.Services.Llm.LlmTimeoutException();

        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Edit,
            feedback: "убери ревью", userId: TestUserId);

        var card = _sentMessages.OfType<TeamEscalationMessage>().Last();
        card.Title.Should().Contain("не уложился");
        card.Details.Should().Contain("не уложился во время", "причина — таймаут, а не правка")
            .And.Contain("убери ревью");
        card.Actions.Select(a => a.Id).Should().Equal("retryPlan");
    }

    [Fact]
    public async Task ПовторитьПланирование_ПослеСбояПравки_СохраняетПравку()
    {
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-edit-retry");
        _plannerException = new ClaudeHomeServer.Services.Llm.LlmTimeoutException();
        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Edit,
            feedback: "убери ревью из плана", userId: TestUserId);
        var card = _sentMessages.OfType<TeamEscalationMessage>().Last();

        // Планировщик ожил — человек жмёт «Повторить планирование»
        _plannerException = null;
        SetReplannedPlannerAnswer(GetAnyPersona(session), "ревью убрано");
        var ok = await _sut.RespondTeamEscalationAsync(session.Id, card.EscalationId, "retryPlan",
            userId: TestUserId);

        ok.Should().BeTrue();
        _plannerStub.LastPrompt.Should().Contain("убери ревью из плана",
            "повтор идёт по той же правке — иначе она потерялась бы");
        var newCard = _sentMessages.OfType<TeamPlanMessage>().Last();
        newCard.Plan.Version.Should().Be(2);
        newCard.Resolved.Should().BeFalse();
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Confirming);
    }

    [Fact]
    public async Task ПравкаПлана_ПустойФидбек_НичегоНеДелает()
    {
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-edit-empty");

        var result = await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Edit,
            feedback: "   ", userId: TestUserId);

        result.Should().BeNull("пустая правка не запускает перепланирование");
        _sut.GetById(session.Id)!.TeamImplement!.PlanVersion.Should().Be(1);
    }

    [Fact]
    public async Task ПравкаТекстомВЧате_МаркерРаботыВПодтверждении_ПересобираетПлан()
    {
        // Свободный текстовый путь: координатор получил правку сообщением и обязан отдать её
        // маркером работы (PlanEditProtocol). Маркер в стадии Confirming теперь не проглатывается.
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-edit-marker");
        SetPlannerAnswer(backend, frontend);
        var (plan, _) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Confirming);
        SetReplannedPlannerAnswer(backend, "ревью убрано");

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "Принял правку.\n<team:work>экспорт в CSV без ревью</team>", failed: false);

        var newCard = _sentMessages.OfType<TeamPlanMessage>().Last();
        newCard.PlanId.Should().NotBe(plan!.Id);
        newCard.Plan.Version.Should().Be(2, "маркер работы в подтверждении — перепланирование");
        newCard.Resolved.Should().BeFalse();
        var oldCard = _sentMessages.OfType<TeamPlanMessage>().Last(m => m.PlanId == plan.Id);
        oldCard.SupersededBy.Should().Be(2, "старая карточка погашена");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Confirming);
    }

    // --- Волна 1: машина состояний (B1, M3, M8, M9) ---

    // B1: добавочная вводная при авто-волнах согласования не ждёт — и режим прав человека
    // обязан вернуться сам. Иначе SavedMode, поставленный входом в план-режим по этой же
    // вводной, снять было бы негде: селектор навсегда залочен «Штаб планирует…».
    [Fact]
    public async Task ДобавочныйПлан_ПриАвтоВолнах_ВозвращаетРежимЧеловека()
    {
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-additional-mode");
        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run, userId: TestUserId);
        // Итерация закончена, режим ждёт следующей вводной
        ((ITeamRunState)_sut).WithTeamState(session.Id, t => { t.Stage = TeamImplementStage.Idle; return true; });
        session.Status = SessionStatus.Working;

        await _sut.SendMessageAsync(session.Id, "теперь добавь выгрузку в XLSX", []);
        _sut.GetById(session.Id)!.TeamImplement!.SavedMode.Should().BeNull(
            "M6: на приёме план-режим не навязывается — сообщение ещё не классифицировано");

        // Классификация работой (вводная человека, M7) — план-режим на планирование,
        // а по публикации добавочного плана режим человека возвращается сам
        SetTeamTurnFromHuman(GetEntry(session.Id), true);
        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>добавить XLSX</team>", failed: false);

        var after = _sut.GetById(session.Id)!;
        after.TeamImplement!.Stage.Should().Be(TeamImplementStage.Wave, "добавочная волна пошла сразу");
        after.TeamImplement.SavedMode.Should().BeNull("режим человека возвращён");
        after.Mode.Should().Be(ClaudeMode.Auto);
        Sent<TeamImplementMessage>().Last().ModeLocked.Should().BeFalse(
            "селектор режима разблокирован");
    }

    // M3: ответ обычным сообщением — равноправная замена кнопок карточки остановки
    [Fact]
    public async Task ТекстВОжиданииРешения_ПослеПервойВолны_ВозвращаетПрактикуВВолну()
    {
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-decision-text");
        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run, userId: TestUserId);
        ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
        {
            t.WaveNumber = 1;
            t.Budget.TasksUsed = 3;
            return true;
        });
        await ((ITeamHistoryStore)_sut).PublishTeamEscalationAsync(session.Id, new TeamEscalation
        {
            Kind = TeamEscalationKind.Blocker,
            Title = "Исполнитель встал",
            Details = "нет доступа к БД",
            Wave = 1,
            Actions = TeamEscalationActions.For(TeamEscalationKind.Blocker),
        });
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision);
        session.Status = SessionStatus.Working;

        await _sut.SendMessageAsync(session.Id, "возьми доступ из appsettings.Local.json", []);

        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.Stage.Should().Be(TeamImplementStage.Wave, "практика пошла дальше с того же места");
        team.StageBeforeDecision.Should().BeNull();
        team.WaveStartedAt.Should().NotBeNull("сторож зависших волн снова тикает");
        team.Budget.TasksUsed.Should().Be(3, "итерация та же — бюджет не сбрасывается");
    }

    [Fact]
    public async Task ТекстВОжиданииРешения_ДоПервойВолны_ВозвращаетВСтадиюДоОжидания()
    {
        var (session, _, _) = await MakeInterviewStabAsync("ti-decision-text-early");
        await ((ITeamHistoryStore)_sut).PublishTeamEscalationAsync(session.Id, new TeamEscalation
        {
            Kind = TeamEscalationKind.ProductDecision,
            Title = "Координатор не понял вводную",
            Details = "ответ без маркера",
            Actions = TeamEscalationActions.For(TeamEscalationKind.ProductDecision),
        });

        await _sut.SendMessageAsync(session.Id, "делаем экспорт в CSV, без вариантов", []);

        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.Stage.Should().Be(TeamImplementStage.Interview,
            "до первой волны возвращаемся туда, откуда ушли в ожидание, а не в «волну-призрак»");
        team.WaveStartedAt.Should().BeNull("волн ещё не было — сторожу нечего сторожить");
    }

    // M3: текст отказа квоты обязан быть честным — из «ждёт решения» план как раз подтверждён.
    // Волна 1 team-blocker-honest уточнила: AwaitingDecision без открытых карточек — стадия
    // осталась по инерции (TryResolveBlockerByFactAsync должен был вернуть её), и запуск
    // разрешён. С блокером — координатор сам разбирается, запуск разрешён. С не-блокером
    // (Stopped/TaskFailed и пр.) — действительно ждём человека, отказ.
    [Fact]
    public async Task КвотаЗапуска_ВОжиданииРешенияБезОткрытыхКарточек_РазрешаетЗапуск()
    {
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-quota-awaiting");
        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run, userId: TestUserId);
        ((ITeamRunState)_sut).WithTeamState(session.Id, t => { t.Stage = TeamImplementStage.AwaitingDecision; return true; });

        var (verdict, reason) = _sut.TryConsumeTeamImplementRun(session.Id, TestUserId);

        // AwaitingDecision + нет открытых карточек — координатор разбирается, запуск разрешён
        ((SessionManager.TeamRunQuota)(int)verdict).Should().Be(SessionManager.TeamRunQuota.Allowed);
        reason.Should().BeNull();
    }

    // M8: клик по карточке v1, когда опубликован v2. С фиксом «Изменить план» (2026-08-04)
    // старая карточка гаснет УЖЕ при входе в перепланирование (кнопок у неё нет), поэтому
    // решение по ней не находит открытой карточки и молча не проходит; состояние не трогается.
    [Fact]
    public async Task RespondTeamPlan_УстаревшаяКарточка_НеМеняетСостояние()
    {
        var (session, backend, frontend) = await MakeInterviewStabAsync("ti-stale-card");
        SetPlannerAnswer(backend, frontend);
        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>экспорт</team>", failed: false);
        var stale = _sentMessages.OfType<TeamPlanMessage>().Last().PlanId;
        // Координатор сказал, что требования неясны → интервью → план v2. Раньше здесь был
        // ASK-вопрос, но с единым каналом вопросов (2026-08-04) вопрос вне интервью практику
        // не двигает — в перепланирование ведёт только явный маркер clarify.
        await _sut.HandleTeamTurnEndAsync(session.Id,
            "<escalate:clarify>уточнение по формату экспорта</escalate>", failed: false);
        // Карточка v1 гаснет сразу при входе в перепланирование — до публикации v2
        _sentMessages.OfType<TeamPlanMessage>().Last(m => m.PlanId == stale)
            .SupersededBy.Should().Be(2, "устаревшая карточка помечена версией-заменой");
        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>экспорт, но в XLSX</team>", failed: false);
        var started = false;
        _sut.TeamHandlers.WaveStarter = (_, _, _) => { started = true; return Task.CompletedTask; };

        var result = await _sut.RespondTeamPlanAsync(session.Id, stale, TeamPlanDecision.Run,
            userId: TestUserId);

        result.Should().BeNull("решение по устаревшей карточке не проходит");
        started.Should().BeFalse();
        var after = _sut.GetById(session.Id)!;
        after.TeamImplement!.Stage.Should().Be(TeamImplementStage.Confirming, "стадия не тронута");
        after.TeamImplement.WaveNumber.Should().Be(0, "«волны-призрака» не завелось");
        after.TeamImplement.ApprovedPlanVersion.Should().Be(0, "старая версия не стала подтверждённой");
        after.Mode.Should().Be(ClaudeMode.Plan, "план-режим не снят посреди перепланирования");
        var card = _sentMessages.OfType<TeamPlanMessage>().Last(m => m.PlanId == stale);
        card.Resolved.Should().BeTrue();
        card.Approved.Should().BeFalse();
    }

    // Легаси-вариант M8: устаревшая карточка ещё НЕ погашена (состояние, сохранённое
    // сервером до фикса) — тогда клик гасит её и объясняет человеку, где свежая.
    [Fact]
    public async Task RespondTeamPlan_УстаревшаяНепогашеннаяКарточка_ГаситИОбъясняет()
    {
        var (session, backend, frontend) = await MakeInterviewStabAsync("ti-stale-legacy");
        SetPlannerAnswer(backend, frontend);
        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>экспорт</team>", failed: false);
        var staleId = _sentMessages.OfType<TeamPlanMessage>().Last().PlanId;
        // Имитация легаси-состояния: опубликована другая карточка, а старая не погашена
        ((ITeamRunState)_sut).WithTeamState(session.Id, t => { t.PlanCardId = "другая-карточка"; t.PlanVersion = 2; return true; });

        var result = await _sut.RespondTeamPlanAsync(session.Id, staleId, TeamPlanDecision.Run,
            userId: TestUserId);

        result.Should().BeNull("решение по устаревшей карточке не проходит");
        var card = _sentMessages.OfType<TeamPlanMessage>().Last(m => m.PlanId == staleId);
        card.Resolved.Should().BeTrue();
        card.Approved.Should().BeFalse();
        _sentMessages.OfType<GuestTextMessage>().Last().Text.Should().Contain("устарела",
            "человек нажал кнопку — обязан узнать, что она больше ни к чему не ведёт");
    }

    // M9: интервью из волны приходит с WaveNumber > 0 — гард молчаливого тупика обязан
    // ловить и его, иначе обещанные карточкой вопросы не приходят никогда
    [Fact]
    public async Task КонецХода_ИнтервьюИзВолныБезВопросов_ДаётКарточку()
    {
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-clarify-stall");
        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run, userId: TestUserId);
        ((ITeamRunState)_sut).WithTeamState(session.Id, t => { t.WaveNumber = 1; return true; });
        await _sut.HandleTeamTurnEndAsync(session.Id,
            "<escalate:clarify>неясен формат выгрузки</escalate>", failed: false);
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Interview);

        await _sut.HandleTeamTurnEndAsync(session.Id, "Подожду ваших уточнений.", failed: false);

        var card = Sent<TeamEscalationMessage>().Last();
        card.Title.Should().Be("Уточнения так и не пришли");
        card.Details.Should().Contain("Подожду ваших уточнений");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.AwaitingDecision,
            "молчаливых тупиков не бывает — карточка зовёт человека");
    }

    [Fact]
    public async Task КонецХода_ВопросКоординатораВЭтомХоду_КарточкиТупикаНет()
    {
        var (session, _, _) = await MakeInterviewStabAsync("ti-stall-asked");

        await _sut.HandleTeamTurnEndAsync(session.Id, "Уточните два момента.", failed: false, asked: true);

        Sent<TeamEscalationMessage>().Should().BeEmpty(
            "ход закончился вопросами человеку — интервью работает, а не стоит");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Interview);
    }

    // --- Волна 2: гарды режима и контур (M1, M4, M5, M6, M7) ---

    // M1: селектор режима в стадии волны — тот же гард, что при включении: в acceptEdits/
    // bypassPermissions CLI не спрашивает разрешений, и CoordinatorWriteGuard молчал бы
    [Fact]
    public async Task SetMode_Штаб_НесовместимыйРежимПоднимаетсяДоAuto()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-mode-guard");

        _sut.SetMode(session.Id, "acceptEdits", TestUserId)!.Mode.Should().Be(ClaudeMode.Auto,
            "в acceptEdits запись через shell проходит мимо сервера — штаб там жить не может");
        _sut.SetMode(session.Id, "bypass", TestUserId)!.Mode.Should().Be(ClaudeMode.Auto);
        // Волна 3: dontAsk — тот же класс, что acceptEdits/bypass («не спрашивать разрешение»
        // по имени режима у CLI) — закрытый вопрос предыдущего аудита
        _sut.SetMode(session.Id, "dontAsk", TestUserId)!.Mode.Should().Be(ClaudeMode.Auto,
            "dontAsk тоже не спрашивает разрешений — гард должен поднимать и его");
        _sut.SetMode(session.Id, "default", TestUserId)!.Mode.Should().Be(ClaudeMode.Default,
            "совместимые режимы не трогаем");
    }

    [Fact]
    public async Task SetMode_ОбычныйЧат_РежимМеняетсяБезГарда()
    {
        var dir = MkProjectDir("ti-mode-plain");
        var project = _projectManager.Create("TI-PLAIN", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        _sut.SetMode(session.Id, "acceptEdits", TestUserId)!.Mode.Should().Be(ClaudeMode.AcceptEdits,
            "гард — только у чата-штаба");
    }

    // M1: режим из тела сообщения — четвёртая точка смены, тоже под гардом
    [Fact]
    public async Task СообщениеСРежимом_Штаб_НесовместимыйРежимПоднимаетсяДоAuto()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-mode-msg");
        session.Name = "штаб";
        // Стадия волны — чтобы приём сообщения не открыл свежую итерацию с план-режимом
        _sut.GetById(session.Id)!.TeamImplement!.Stage = TeamImplementStage.Wave;
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        await _sut.SendMessageAsync(session.Id, "продолжай", [], mode: "acceptEdits");

        _sut.GetById(session.Id)!.Mode.Should().Be(ClaudeMode.Auto);
    }

    // M1: на стадиях интервью/планирования сообщение с mode не сбрасывает навязанный
    // план-режим — тот же лок, что в SetMode (молча: отказ стоил бы самого сообщения)
    [Fact]
    public async Task СообщениеСРежимом_ВПланФазе_НеСбрасываетПланРежим()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-mode-planphase");
        session.Name = "штаб";
        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.Stage = TeamImplementStage.Interview;
        team.SavedMode = ClaudeMode.Auto;
        session.Mode = ClaudeMode.Plan;
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);

        await _sut.SendMessageAsync(session.Id, "ответ на вопрос", [], mode: "acceptEdits");

        _sut.GetById(session.Id)!.Mode.Should().Be(ClaudeMode.Plan,
            "план-режим стадий интервью/планирования снимает только согласование плана");
    }

    // M4: правка состава/тумблера тем же эндпоинтом посреди волны не должна осиротить её
    [Fact]
    public async Task SetTeamImplement_ПоверхАктивного_СохраняетСостояниеИтерации()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-reenable");
        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.Stage = TeamImplementStage.Wave;
        team.WaveNumber = 2;
        team.PlanCardId = "plan-card-1";
        team.PlanVersion = 1;
        team.SavedMode = ClaudeMode.Default;
        team.Budget.TasksUsed = 5;
        team.Budget.RunsUsed = 9;

        var updated = await _sut.SetTeamImplementAsync(session.Id, enabled: true,
            autoWaves: false, userId: TestUserId);

        var ti = updated!.TeamImplement!;
        ti.AutoWaves.Should().BeFalse("настраиваемые поля меняются");
        ti.Stage.Should().Be(TeamImplementStage.Wave, "стадия переживает повторное включение");
        ti.WaveNumber.Should().Be(2);
        ti.PlanCardId.Should().Be("plan-card-1", "иначе закрытие волны не нашло бы план");
        ti.PlanVersion.Should().Be(1);
        ti.SavedMode.Should().Be(ClaudeMode.Default);
        ti.Budget.TasksUsed.Should().Be(5, "бюджет итерации не обнуляется");
        ti.Budget.RunsUsed.Should().Be(9);
    }

    // M5: хабовый «Стоп» — второй путь прерывания, буфер маркеров чистится так же,
    // как при прерывании очередью: иначе маркер убитого хода применился бы задним числом
    [Fact]
    public async Task Interrupt_Хабовый_ЧиститБуферМаркеровУбитогоХода()
    {
        var (session, _, _) = await MakeTeamStabAsync("stab-stop-buffer");
        session.Status = SessionStatus.Working;
        var entry = GetEntry(session.Id);
        var adapter = StubAdapter(entry);
        SetProcess(entry, adapter.Object);
        // Координатор успел написать маркер — он копится в буфере хода штаба
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new TextDeltaMessage("<<<ЭСКАЛАЦИЯ: расхождение с планом>>>"), TestRunId);
        GetTeamTurnText(entry).Should().NotBeEmpty("предусловие: буфер хода штаба непуст");

        _sut.Interrupt(session.Id);

        adapter.Verify(a => a.Interrupt(), Times.Once());
        GetTeamTurnText(entry).Should().BeEmpty(
            "маркер убитого «Стоп» хода не доклеивается к следующему — иначе фантомная эскалация");
    }

    // M6: разговорный вопрос в ожидании — не вводная: потолки, стадия и режим человека
    // не трогаются, а ответ координатора без маркера — легальный, без ложной эскалации
    [Fact]
    public async Task РазговорныйВопрос_ВОжидании_НеТрогаетБюджетСтадиюИРежим()
    {
        var (session, _, _) = await MakeIdleStabAsync("ti-talk-idle");
        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.Budget.TasksUsed = 7;
        team.Budget.RunsUsed = 11;
        session.Status = SessionStatus.Working; // ход не запускаем — сообщение встаёт в очередь

        await _sut.SendMessageAsync(session.Id, "что вы сделали?", []);

        var after = _sut.GetById(session.Id)!;
        after.TeamImplement!.Budget.TasksUsed.Should().Be(7, "разговор бюджет не обнуляет");
        after.TeamImplement.Budget.RunsUsed.Should().Be(11);
        after.TeamImplement.Stage.Should().Be(TeamImplementStage.Idle,
            "стадия не двигается до классификации вводной");
        after.Mode.Should().Be(ClaudeMode.Auto, "план-режим разговору не навязывается");
        after.TeamImplement.SavedMode.Should().BeNull();

        // Координатор честно ответил без маркера — это легальный ответ, а не тупик
        await _sut.HandleTeamTurnEndAsync(session.Id, "Сделали экспорт в CSV за 2 задачи.", failed: false);

        Sent<TeamEscalationMessage>().Should().BeEmpty(
            "ложной эскалации «Координатор не понял вводную» быть не должно");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Idle);
    }

    // M6: маркер разговора — легальный выход из свежего интервью без плана и эскалации
    [Fact]
    public async Task МаркерРазговора_ВИнтервью_ЗакрываетИнтервьюБезЭскалации()
    {
        var (session, _, _) = await MakeInterviewStabAsync("ti-talk-close");
        // Предусловие: стадия интервью, чат в план-режиме, режим человека сохранён
        _sut.GetById(session.Id)!.Mode.Should().Be(ClaudeMode.Plan);

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "Это был просто вопрос — отвечаю: экспорт уже есть.\n<team:talk/>", failed: false);

        var after = _sut.GetById(session.Id)!;
        after.TeamImplement!.Stage.Should().Be(TeamImplementStage.Planning,
            "свежая «итерация» возвращается в ожидание первой вводной");
        after.Mode.Should().Be(ClaudeMode.Auto, "режим человека возвращён");
        after.TeamImplement.SavedMode.Should().BeNull();
        Sent<TeamEscalationMessage>().Should().BeEmpty("ложной эскалации нет");
    }

    // M6: маркер разговора из clarify-интервью (тупик в волне) снимает паузу — волна
    // продолжается, сторож взведён заново, а признак перепланирования снят: плана не было
    [Fact]
    public async Task МаркерРазговора_ИзClarifyИнтервью_ВозвращаетВолну()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-talk-clarify");
        var team = _sut.GetById(session.Id)!.TeamImplement!;
        team.WaveNumber = 1;
        team.PlanCardId = "plan-1";
        team.Stage = TeamImplementStage.Interview;
        team.Replanning = true;
        team.SavedMode = ClaudeMode.Auto;
        session.Mode = ClaudeMode.Plan;

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "Понял, это был вопрос — отвечаю по существу.\n<team:talk/>", failed: false);

        var after = _sut.GetById(session.Id)!;
        after.TeamImplement!.Stage.Should().Be(TeamImplementStage.Wave, "пауза снята — волна продолжается");
        after.TeamImplement.Replanning.Should().BeFalse("интервью закончилось без нового плана");
        after.TeamImplement.WaveStartedAt.Should().NotBeNull("сторож волны взведён заново");
        after.Mode.Should().Be(ClaudeMode.Auto);
        Sent<TeamEscalationMessage>().Should().BeEmpty();
    }

    // M6: у модели обязан быть легальный путь ответа на каждой стадии, где его ждёт
    // stall-гард, — в интервью это маркер разговора
    [Fact]
    public void ПромптКоординатора_ВИнтервью_ДаётЛегальныйВыходРазговором()
    {
        var interview = new SessionTeamImplement { Stage = TeamImplementStage.Interview };
        TeamImplementPrompts.CoordinatorTurn(interview).Should().Contain("<team:talk/>");

        var idle = new SessionTeamImplement { Stage = TeamImplementStage.Idle };
        TeamImplementPrompts.CoordinatorTurn(idle).Should().Contain("<team:work>",
            "в ожидании классификация «работа/разговор» по-прежнему в промпте");
    }

    // M7: агентская вводная (chats_send в штаб), классифицированная как работа, не получает
    // авто-подтверждения — единственное согласование остаётся за человеком
    [Fact]
    public async Task АгентскаяВводная_ВОжидании_ПланЖдётПодтвержденияЧеловека()
    {
        var (session, backend, _) = await MakeIdleStabAsync("ti-agent-input");
        SetAdditionalPlannerAnswer(backend);
        var started = false;
        _sut.TeamHandlers.WaveStarter = (_, _, _) => { started = true; return Task.CompletedTask; };
        // Ход агента: флаг «вводная от человека» не выставлен (по умолчанию false)

        await _sut.HandleTeamTurnEndAsync(session.Id, "<team:work>добавить выгрузку в XLSX</team>", failed: false);

        var card = Sent<TeamPlanMessage>().Last();
        card.Resolved.Should().BeFalse("авто-подтверждение опирается на вводную ЧЕЛОВЕКА");
        card.Approved.Should().NotBeTrue();
        started.Should().BeFalse("волна без согласования человека не стартует");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Confirming);
    }

    // Рефанд квоты пробуждений: списанная авансом единица возвращается при недоставке
    [Fact]
    public async Task РефандПробуждения_ВозвращаетСписаннуюКвоту()
    {
        var (stab, _, _) = await MakeTeamStabAsync("ti-wakeup-refund");

        var wake = _sut.TryConsumeTeamWakeup(stab.Id);
        wake.Allowed.Should().BeTrue();
        _sut.GetById(stab.Id)!.TeamImplement!.Budget.WakeupsUsed.Should().Be(1);

        _sut.RefundTeamWakeup(stab.Id);

        _sut.GetById(stab.Id)!.TeamImplement!.Budget.WakeupsUsed.Should().Be(0,
            "несостоявшееся пробуждение команде ничего не стоит");
    }
}
