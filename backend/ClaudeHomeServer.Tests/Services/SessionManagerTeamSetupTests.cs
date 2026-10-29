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

public class SessionManagerTeamSetupTests : SessionManagerTestBase
{
    // --- Этап 4 / шаг 1а: слот текста хода на SessionEntry с ключом по TurnSeq ---
    // Этап 4 / шаг 1в: тесты LastTurnText_* сняты вместе с шагом 1а — LastTurnTexts ушёл в
    // LastTeamTurnEnds, отдельного хранилища больше нет. Покрытие правил переехало в тесты
    // LastTeamTurnEnd_Store_* ниже.

    // --- Этап 4 / шаг 1в: хранилище плана вызова HandleTeamTurnEndAsync на SessionEntry ---
    // Правила те же, что у прежнего LastTurnTexts: первая запись выигрывает, нулевой ключ не
    // кладётся, изъятие с удалением. На чтении (TryTakeTeamTurnEnd) подписчик turn/completed
    // берёт план и зовёт HandleTeamTurnEndAsync — двойной терминал одного хода даёт ровно
    // один план по правилу «первая запись выигрывает», повторное изъятие возвращает false.

    [Fact]
    public async Task LastTeamTurnEnd_RecordTeamTurnEnd_ПерваяЗаписьПоКлючуВыигрывает()
    {
        var session = await MkBusySessionAsync("ltte-first-wins");
        var entry = GetEntry(session.Id);
        var slot = GetLastTeamTurnEnds(entry);

        InvokeRecordTeamTurnEnd(entry, 7, "первый", failed: false, asked: false);
        InvokeRecordTeamTurnEnd(entry, 7, "второй", failed: true, asked: false);

        slot.Count.Should().Be(1);
        InvokeTryTakeTeamTurnEnd(entry, 7, out var text, out var failed, out _).Should().BeTrue();
        text.Should().Be("первый");
        failed.Should().BeFalse(
            "повторная запись по тому же TurnSeq не затирает первую — двойной терминал одного хода");
    }

    [Fact]
    public async Task LastTeamTurnEnd_RecordTeamTurnEnd_НулевойКлючНеКладётся()
    {
        var session = await MkBusySessionAsync("ltte-zero-seq");
        var entry = GetEntry(session.Id);
        var slot = GetLastTeamTurnEnds(entry);

        InvokeRecordTeamTurnEnd(entry, 0, "текст", failed: true, asked: false);
        InvokeRecordTeamTurnEnd(entry, -1, "текст", failed: true, asked: false);

        slot.Count.Should().Be(0,
            "turnSeq <= 0 — синтетические ходы (local voice), turn/completed по ним не публикуется");
    }

    [Fact]
    public async Task LastTeamTurnEnd_TryTakeTeamTurnEnd_ИзымаетИПовторноВозвращаетFalse()
    {
        var session = await MkBusySessionAsync("ltte-take-once");
        var entry = GetEntry(session.Id);

        InvokeRecordTeamTurnEnd(entry, 7, "текст", failed: true, asked: true);

        InvokeTryTakeTeamTurnEnd(entry, 7,
            out var firstText, out var firstFailed, out var firstAsked).Should().BeTrue();
        firstText.Should().Be("текст");
        firstFailed.Should().BeTrue();
        firstAsked.Should().BeTrue();
        // контракт подписчика: повторное чтение по тому же ключу — false (запись удалена)
        InvokeTryTakeTeamTurnEnd(entry, 7, out _, out _, out _).Should().BeFalse();
        GetLastTeamTurnEnds(entry).Count.Should().Be(0);
    }

    [Fact]
    public async Task LastTeamTurnEnd_TryTakeTeamTurnEnd_ЧужойКлючНеТрогаетЗапись()
    {
        var session = await MkBusySessionAsync("ltte-take-other");
        var entry = GetEntry(session.Id);

        InvokeRecordTeamTurnEnd(entry, 7, "текст", failed: false, asked: false);
        GetLastTeamTurnEnds(entry).Count.Should().Be(1, "предусловие: запись лежит");

        InvokeTryTakeTeamTurnEnd(entry, 8, out _, out _, out _).Should().BeFalse();
        GetLastTeamTurnEnds(entry).Count.Should().Be(1,
            "чужой ключ не изымает чужую запись — словарь по ключу");
    }

    [Fact]
    public async Task LastTeamTurnEnd_Потолок_ВытесняетСамуюСтаруюЗаписьНеСвежую()
    {
        // Защита та же, что у прежнего LastTurnTexts: при превышении потолка (MaxLastTeamTurnEndEntries = 8)
        // вытесняется самая старая запись (минимальный TurnSeq), не свежая. Иначе бы запись
        // свежего хода вытеснила ещё не прочитанную запись предыдущего.
        var session = await MkBusySessionAsync("ltte-cap");
        var entry = GetEntry(session.Id);
        var slot = GetLastTeamTurnEnds(entry);

        for (int i = 1; i <= 9; i++)
            InvokeRecordTeamTurnEnd(entry, i, $"t{i}", failed: false, asked: false);

        slot.Count.Should().Be(8);
        slot.Count.Should().BeLessThan(9, "потолок 8 записей");
        slot.Contains(1).Should().BeFalse("старейшая запись TurnSeq=1 вытеснена");
    }

    // --- Этап 4 / шаг 1в: тесты проводки turn/completed для HandleTeamTurnEndAsync ---
    // Каждый тест идёт полным продовым путём: InvokeOnMessageAsync кладёт план в
    // LastTeamTurnEnds при терминале хода, PublishTurnCompletedAsync публикует событие на
    // шине, HandleTeamTurnCompletedShim (подписчик, регистрируется в конструкторе
    // SessionManager) забирает план и асинхронно зовёт HandleTeamTurnEndAsync. Мутация
    // проверяется в отчёте: снять фильтр по Outcome либо TryTakeTeamTurnEnd — тест обязан
    // упасть; в случае interrupted/cancelled/crashed именно этот тест закрывает дыру
    // «фантомная эскалация», которую не удалось проверить живьём (CLI на стенде
    // завершается сам).

    [Fact]
    public async Task TurnWire_OutcomeSuccess_ШтабРазбираетХодРовноОдинРаз()
    {
        var (session, _, _) = await MakeInterviewStabAsync("wire-success");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Interview);

        await DriveOneTeamTurnAsync(_sut, session.Id, 7, "success",
            "Без маркера и без волн это молчаливый тупик интервью.", failed: false);

        // success → штаб разбирает. На Interview без маркера и волн — карточка «Уточнения
        // так и не пришли» (вид молчаливого тупика по Э7-фиксу). Ждём доставки через шину:
        // HandleTeamTurnEndAsync идёт в Task.Run, Send — fire-and-forget.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var cards = new List<TeamEscalationMessage>();
        while (DateTime.UtcNow < deadline)
        {
            cards = Sent<TeamEscalationMessage>().Where(m => m.SessionId == session.Id && !m.Resolved).ToList();
            if (cards.Count >= 1) { await Task.Delay(100); break; }
            await Task.Delay(30);
        }
        cards.Should().ContainSingle("success → штаб разобрал ход через шину и опубликовал карточку");
    }

    [Fact]
    public async Task TurnWire_OutcomeFailed_ШтабРазбираетХодРовноОдинРаз()
    {
        var (session, _, _) = await MakeInterviewStabAsync("wire-failed");

        await DriveOneTeamTurnAsync(_sut, session.Id, 7, "failed",
            "Тот же сценарий молчаливого тупика, но ход упал.", failed: true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var cards = new List<TeamEscalationMessage>();
        while (DateTime.UtcNow < deadline)
        {
            cards = Sent<TeamEscalationMessage>().Where(m => m.SessionId == session.Id && !m.Resolved).ToList();
            if (cards.Count >= 1) { await Task.Delay(100); break; }
            await Task.Delay(30);
        }
        cards.Should().ContainSingle("failed → штаб разобрал ход и опубликовал карточку");
    }

    [Fact]
    public async Task TurnWire_OutcomeEgressDown_ШтабРазбирает()
    {
        // egress_down — общий канал наружу мёртв. По FALLBACK-оркестрации FailEgressAsync шлёт
        // ErrorMessage(ExpectResultFollows=true) → result — для шины это исход «failed», но
        // публикатор ставит Outcome="egress_down". Штаб должен разобрать так же, как и failed:
        // внутри HandleTeamTurnEndAsync текст «причина недоступности канала» попадает в карточку.
        var (session, _, _) = await MakeInterviewStabAsync("wire-egress");

        await DriveOneTeamTurnAsync(_sut, session.Id, 7, "egress_down",
            "Канал наружу недоступен — координатор не смог ответить.", failed: true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var cards = new List<TeamEscalationMessage>();
        while (DateTime.UtcNow < deadline)
        {
            cards = Sent<TeamEscalationMessage>().Where(m => m.SessionId == session.Id && !m.Resolved).ToList();
            if (cards.Count >= 1) { await Task.Delay(100); break; }
            await Task.Delay(30);
        }
        cards.Should().ContainSingle("egress_down → штаб разобрал как failed");
    }

    [Fact]
    public async Task TurnWire_OutcomeLocalDown_ШтабРазбирает()
    {
        // local_down — локальный движок недоступен (pre-flight проба). По FailLocalDownAsync →
        // ErrorMessage → result, как egress_down. Шестой исход (коммит… нет, седьмой: код
        // знает 7 исходов, ADR пока 6 — поправлено в этом же коммите).
        var (session, _, _) = await MakeInterviewStabAsync("wire-local");

        await DriveOneTeamTurnAsync(_sut, session.Id, 7, "local_down",
            "Локальный движок не поднялся — координатор не смог ответить.", failed: true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var cards = new List<TeamEscalationMessage>();
        while (DateTime.UtcNow < deadline)
        {
            cards = Sent<TeamEscalationMessage>().Where(m => m.SessionId == session.Id && !m.Resolved).ToList();
            if (cards.Count >= 1) { await Task.Delay(100); break; }
            await Task.Delay(30);
        }
        cards.Should().ContainSingle("local_down → штаб разобрал как failed");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TurnWire_OutcomeInterrupted_ШтабНеРазбираетКарточкиНет()
    {
        // interrupted — НЕ конец хода штаба. Обе точки прерывания (Стоп, прерывание ради
        // очереди) чистят буфер маркеров, и подписчик должен молча выйти. Если бы сработал —
        // воспроизвёлся бы продовый дефект «фантомная эскалация» (коммит fcce2753 показал,
        // что на interrupted штаб раньше не разбирался; проверка живьём не воспроизводилась
        // — CLI на стенде завершается сам за 11–15 секунд). Этот тест закрывает ту дыру.
        // MakeTeamStabAsync (не InterviewStab): не запускает реальный адаптер — тест
        // проверяет только логику раннего выхода подписчика, не гард тупика.
        var (session, _, _) = await MakeTeamStabAsync("wire-interrupted");
        var entry = GetEntry(session.Id);

        await DriveOneTeamTurnAsync(_sut, session.Id, 7, "interrupted",
            "текст прерванного хода", failed: false);

        // Дать подписчику время на обработку события. Шина — fire-and-forget, обработка
        // асинхронная. Если подписчик ошибочно дёрнул HandleTeamTurnEndAsync, карточка
        // появится в течение секунды; ждём две для запаса.
        await Task.Delay(2000);
        var cards = Sent<TeamEscalationMessage>()
            .Where(m => m.SessionId == session.Id && !m.Resolved).ToList();
        cards.Should().BeEmpty(
            "interrupted — НЕ конец хода штаба, обе точки прерывания чистят буфер маркеров ради защиты от «волны-призрака»");

        // LastTeamTurnEnds НЕ должен быть тронут — событие interrupted не должно изымать
        // план, которого для исхода interrupted в OnMessageAsync и не клали.
        // (Кладём план, потому что DriveOneTeamTurnAsync идёт через терминал; подписчик
        // на interrupted должен увидеть ключ и не трогать — а на успехе/не-интеррапт
        // изымает.)
        GetLastTeamTurnEnds(entry).Contains(7).Should().BeTrue(
            "подписчик на interrupted не должен трогать LastTeamTurnEnds — ключ TurnSeq у каждого хода свой, конкуренции нет");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TurnWire_OutcomeCancelled_ШтабНеРазбирает()
    {
        // MakeTeamStabAsync (не InterviewStab): не запускает реальный адаптер — тест
        // проверяет только логику раннего выхода подписчика, не гард тупика.
        var (session, _, _) = await MakeTeamStabAsync("wire-cancelled");

        await DriveOneTeamTurnAsync(_sut, session.Id, 7, "cancelled",
            "отменено человеком", failed: false);

        await Task.Delay(2000);
        Sent<TeamEscalationMessage>()
            .Where(m => m.SessionId == session.Id && !m.Resolved).ToList()
            .Should().BeEmpty("cancelled — НЕ конец хода штаба");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TurnWire_OutcomeCrashed_БезТерминалаDownstreamШтабНеРазбирает()
    {
        // crashed через SettleAsync: сбой оркестрации случился ДО первой попытки (lastEnd=null,
        // в hold'е ещё ничего) — downstream не получил ни Error, ни Result, OnMessageAsync
        // терминала не видел и плана в LastTeamTurnEnds не положил. Разбирать нечего: буфер
        // маркеров этого хода никто не осушал, и «конец хода штаба» здесь не наступил.
        var (session, _, _) = await MakeInterviewStabAsync("wire-crashed-no-plan");
        var entry = GetEntry(session.Id);
        _sut.GetById(session.Id)!.Status = SessionStatus.Working;
        SetLastTurnSeq(entry, 7);

        // Терминал downstream НЕ идёт — сразу шина.
        await PublishTurnCompletedAsync(_sut, session.Id, 7, "crashed");

        await Task.Delay(2000);
        Sent<TeamEscalationMessage>()
            .Where(m => m.SessionId == session.Id && !m.Resolved).ToList()
            .Should().BeEmpty("плана в слоте нет — терминал хода downstream не дошёл, разбирать нечего");
    }

    [Fact]
    public async Task TurnWire_OutcomeCrashed_ПослеПровалаДоставкиШтабРазбирает()
    {
        // Регресс на дыру, внесённую переводом штаба на подписку (83e3cf78): ход упал как
        // crashed, но ПЕРЕД этим была неудачная доставка — FallbackLlmSessionAdapter пошёл
        // в FailClosedAsync и отдал downstream пару ErrorMessage(ExpectResultFollows=true)
        // → ResultMessage("error"). Для ленты ход состоялся: текст осушен, план лёг в
        // LastTeamTurnEnds. Значит маркеры этого хода обязаны быть разобраны — до 83e3cf78
        // разбор шёл прямым вызовом из OnMessageAsync.
        //
        // Отличие от теста выше — ровно в факте «терминал дошёл downstream», и подписчик
        // обязан решать по нему, а не по одному Outcome.
        var (session, _, _) = await MakeInterviewStabAsync("wire-crashed-failclosed");
        var entry = GetEntry(session.Id);
        // Стаб ставит вводную в очередь. Терминал ниже выводит чат из Working, очередь поднимает
        // настоящий ход, и он падает в тестовом окружении. Если его SessionStartedMessage не
        // дошёл до result, LastTurnSeq остаётся 7 (выставлен рукой ниже), и его пустой план ложится
        // под ключ 7 уже ПОСЛЕ изъятия нашего. Изымать его некому: шина публикует тот ход под
        // своим TurnSeq. На медленном CI проверка слота ловила этот чужой план, поэтому очередь
        // снимаем: здесь проверяется только разбор нашего хода.
        foreach (var pending in _sut.GetPending(session.Id))
            await _sut.CancelPendingAsync(session.Id, pending.Id);
        _sut.GetPending(session.Id).Should().BeEmpty("предусловие: постороннего хода из очереди не будет");
        _sut.GetById(session.Id)!.Status = SessionStatus.Working;
        SetLastTurnSeq(entry, 7);

        var acc = new TurnAccumulator(new List<StoredMessage>());
        await InvokeOnMessageAsync(session.Id, acc,
            new TextDeltaMessage("Ход упал после провала доставки."));
        // Ровно тот порядок, что шлёт FailClosedAsync: задержанная ошибка перед финальным result.
        await InvokeOnMessageAsync(session.Id, acc,
            new ErrorMessage("не удалось выполнить", ExpectResultFollows: true));
        await InvokeOnMessageAsync(session.Id, acc,
            new ResultMessage("error", 10, 1, null, null));

        await PublishTurnCompletedAsync(_sut, session.Id, 7, "crashed");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var cards = new List<TeamEscalationMessage>();
        while (DateTime.UtcNow < deadline)
        {
            cards = Sent<TeamEscalationMessage>().Where(m => m.SessionId == session.Id && !m.Resolved).ToList();
            if (cards.Count >= 1) { await Task.Delay(100); break; }
            await Task.Delay(30);
        }
        cards.Should().ContainSingle(
            "crashed после провала доставки — терминал ушёл downstream, план в слоте есть: штаб обязан разобрать ход");

        GetLastTeamTurnEnds(entry).Contains(7).Should().BeFalse(
            "план изъят из слота, а не висит до вытеснения потолком");
    }

    [Fact]
    public async Task TurnWire_ЧужойTurnSeq_ПодписчикМолчитКарточкиНет()
    {
        // По ключу 5 в LastTeamTurnEnds лежит план, но шина публикует событие с TurnSeq=7
        // (например, пришёл поздний терминал чужого хода). TryTakeTeamTurnEnd по 7 возвращает
        // false (запись есть только под 5) — подписчик пишет WARN и не вызывает штаб.
        // MakeTeamStabAsync (не InterviewStab): не запускает реальный адаптер — тест
        // проверяет только логику раннего выхода подписчика, не гард тупика.
        var (session, _, _) = await MakeTeamStabAsync("wire-stray");
        var entry = GetEntry(session.Id);
        _sut.GetById(session.Id)!.Status = SessionStatus.Working;
        SetLastTurnSeq(entry, 5);

        // Терминал по TurnSeq=5 — план в LastTeamTurnEnds[5].
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new TextDeltaMessage("текст чужого хода"));
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            new ResultMessage("success", 10, 1, null, null));

        // А шина публикуется по TurnSeq=7 (другой ход). Подписчик ищет по 7 — записи нет.
        await PublishTurnCompletedAsync(_sut, session.Id, 7, "success");

        await Task.Delay(1500);
        Sent<TeamEscalationMessage>()
            .Where(m => m.SessionId == session.Id && !m.Resolved).ToList()
            .Should().BeEmpty("чужой TurnSeq — нет плана по ключу события, подписчик молчит");

        // План под ключом 5 лежит — подписчик его не тронул, потому что искал по 7.
        // Это страховка: если бы подписчик изымал «любой», карточка появилась бы с
        // потерянным текстом чужого хода.
        GetLastTeamTurnEnds(entry).Contains(5).Should().BeTrue(
            "чужой ключ события не тронул план по своему ключу — изоляция по TurnSeq");
    }

    [Fact]
    public async Task RestoreWaveWatchdog_OutcomeInterrupted_ВосстанавливаетОтсечкиСторожа()
    {
        // interrupted — ExitedMessage доезжает downstream (SettleAsync; fallback на 648
        // для гонки), и шим обязан вернуть отсечки сторожа. Старая ветка в OnMessageAsync
        // на 9441 делала это синхронно — теперь на шине.
        var (session, _, _) = await MakeStabInWaveWithPausedWatchdogAsync("rww-interrupted");
        var before = _sut.GetById(session.Id)!.TeamImplement!.WaveStartedAt;
        before.Should().BeNull("предусловие: сторож погашен");

        await PublishTurnCompletedForWaveAsync(_sut, session.Id, 7, "interrupted");

        var after = await GetWaveStartedAtAsync(_sut, session.Id);
        after.Should().NotBeNull(
            "interrupted — ExitedMessage доезжает, шим должен восстановить отсечки сторожа");
    }

    [Fact]
    public async Task RestoreWaveWatchdog_OutcomeCrashed_ВосстанавливаетОтсечкиСторожа()
    {
        // crashed — путь двоится (SettleAsync vs FailClosedAsync), на шине различить нельзя,
        // но RestoreWaveWatchdogIfPaused сам гейтится no-op по стадии/волне. Сторож должен
        // восстановиться, потому что чаще всего crashed идёт через SettleAsync с задержанным
        // ExitedMessage — а FailClosedAsync на этом стенде не воспроизвести без провальной
        // попытки доставки. Этот тест проверяет, что шим НЕ молчит на crashed глобально:
        // если восстановление не происходит — путь сломан.
        var (session, _, _) = await MakeStabInWaveWithPausedWatchdogAsync("rww-crashed");

        await PublishTurnCompletedForWaveAsync(_sut, session.Id, 7, "crashed");

        var after = await GetWaveStartedAtAsync(_sut, session.Id);
        after.Should().NotBeNull(
            "crashed — ExitedMessage доезжает в SettleAsync-варианте, шим восстанавливает сторож; "
            + "для FailClosedAsync-варианта сам RestoreWaveWatchdogIfPaused гейтится no-op");
    }

    [Fact]
    public async Task RestoreWaveWatchdog_OutcomeCancelled_СторожНеТрогает()
    {
        // cancelled — return до SettleAsync, downstream ничего не получает. Старая ветка на
        // ExitedMessage здесь не срабатывала (ExitedMessage не приходил) — и новая не должна.
        var (session, _, _) = await MakeStabInWaveWithPausedWatchdogAsync("rww-cancelled");

        await PublishTurnCompletedForWaveAsync(_sut, session.Id, 7, "cancelled");

        // Достаточно одной проверки после паузы — на cancelled шим молча возвращается.
        await Task.Delay(500);
        var after = _sut.GetById(session.Id)!.TeamImplement!.WaveStartedAt;
        after.Should().BeNull(
            "cancelled — downstream ничего не получает, отсечки сторожа восстанавливать нечем");
    }

    [Fact]
    public async Task RestoreWaveWatchdog_OutcomeSuccess_ВосстанавливаетОтсечкиСторожа()
    {
        // success — путь через HandleTeamTurnEndAsync, который и до этой правки звал
        // RestoreWaveWatchdogIfPaused на 7593. Тест проверяет, что штатный путь жив.
        var (session, _, _) = await MakeStabInWaveWithPausedWatchdogAsync("rww-success");

        await DriveOneTeamTurnAsync(_sut, session.Id, 7, "success",
            "Координатор ответил без маркера, волна не закрыта.", failed: false);

        var after = await GetWaveStartedAtAsync(_sut, session.Id);
        after.Should().NotBeNull(
            "success — HandleTeamTurnEndAsync зовёт RestoreWaveWatchdogIfPaused на 7593; "
            + "это поведение не должно было сломаться переездом");
    }

    [Fact]
    public async Task RestoreWaveWatchdog_OutcomeFailed_ВосстанавливаетОтсечкиСторожа()
    {
        var (session, _, _) = await MakeStabInWaveWithPausedWatchdogAsync("rww-failed");

        await DriveOneTeamTurnAsync(_sut, session.Id, 7, "failed",
            "Ход упал, координатор не смог ответить.", failed: true);

        var after = await GetWaveStartedAtAsync(_sut, session.Id);
        after.Should().NotBeNull("failed — путь через HandleTeamTurnEndAsync восстанавливает сторож");
    }

    [Fact]
    public async Task RestoreWaveWatchdog_OutcomeEgressDown_ВосстанавливаетОтсечкиСторожа()
    {
        var (session, _, _) = await MakeStabInWaveWithPausedWatchdogAsync("rww-egress");

        await DriveOneTeamTurnAsync(_sut, session.Id, 7, "egress_down",
            "Канал наружу недоступен.", failed: true);

        var after = await GetWaveStartedAtAsync(_sut, session.Id);
        after.Should().NotBeNull("egress_down — путь через HandleTeamTurnEndAsync восстанавливает сторож");
    }

    [Fact]
    public async Task RestoreWaveWatchdog_OutcomeLocalDown_ВосстанавливаетОтсечкиСторожа()
    {
        var (session, _, _) = await MakeStabInWaveWithPausedWatchdogAsync("rww-local");

        await DriveOneTeamTurnAsync(_sut, session.Id, 7, "local_down",
            "Локальный движок не поднялся.", failed: true);

        var after = await GetWaveStartedAtAsync(_sut, session.Id);
        after.Should().NotBeNull("local_down — путь через HandleTeamTurnEndAsync восстанавливает сторож");
    }

    [Fact]
    public async Task RestoreWaveWatchdog_НетTeamImplement_InterruptedМолчит()
    {
        // Гард шима: без живого TeamImplement (чат вне режима «Командная реализация»)
        // восстанавливать отсечки нечего — шим обязан тихо выйти.
        var dir = MkProjectDir("rww-no-ti");
        var project = _projectManager.Create("RWW-NTI", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        session.Status = SessionStatus.Working;
        session.TeamImplement.Should().BeNull("предусловие: чат без штаба");

        // Публикуем turn/completed — шим не должен упасть на null TeamImplement.
        await PublishTurnCompletedForWaveAsync(_sut, session.Id, 7, "interrupted");

        // Если шим дёрнул что-то на null и упал, тест кинул бы исключение раньше.
        await Task.Delay(200);
    }

    [Fact]
    public async Task RestoreWaveWatchdog_ЗакрытаяВолна_InterruptedНеТрогает()
    {
        // Гард самого RestoreWaveWatchdogIfPaused: если волна уже закрыта
        // (ClosedWave >= WaveNumber), отсечки возвращать нечего. interrupted приходит,
        // шим зовёт метод, метод сам гейтится no-op.
        var (session, _, _) = await MakeStabInWaveWithPausedWatchdogAsync("rww-closed");
        ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
        {
            t.WaveNumber = 2;
            t.ClosedWave = 2; // волна уже закрыта
            t.WaveStartedAt = null; // но отсечки всё ещё погашены
            return true;
        });

        await PublishTurnCompletedForWaveAsync(_sut, session.Id, 7, "interrupted");

        await Task.Delay(500);
        var after = _sut.GetById(session.Id)!.TeamImplement!.WaveStartedAt;
        after.Should().BeNull(
            "ClosedWave >= WaveNumber — гард RestoreWaveWatchdogIfPaused не возвращает отсечки");
    }

    [Fact]
    public async Task SetTeamImplement_Включение_СоздаётСостояниеСДефолтамиИШлётWs()
    {
        var session = await MakeStabForModeAsync("ti-on");

        var updated = await _sut.SetTeamImplementAsync(session.Id, enabled: true, userId: TestUserId);

        var ti = updated!.TeamImplement;
        ti.Should().NotBeNull();
        ti!.Stage.Should().Be(TeamImplementStage.Interview,
            "волна 3: по спеке Э8 первая стадия итерации — интервью, а не планирование");
        ti.AutoWaves.Should().BeTrue("авто-волны включены по умолчанию");
        ti.WaveNumber.Should().Be(0);
        ti.ExecutorPersonaIds.Should().BeEmpty("пустой список = вся команда проекта");
        // Потолки бюджета из плана
        ti.Budget.MaxTasks.Should().Be(12);
        ti.Budget.MaxWaves.Should().Be(4);
        ti.Budget.MaxRuns.Should().Be(20);
        ti.Budget.MaxRetries.Should().Be(3);
        ti.Budget.MaxWakeups.Should().Be(10, "дефолт потолка срочных пробуждений координатора агентом");
        // Поля «израсходовано» стартуют с нуля
        ti.Budget.TasksUsed.Should().Be(0);
        // Рассылка WS-события режима
        var msg = Sent<TeamImplementMessage>().Single();
        msg.Type.Should().Be("team_implement");
        msg.Active.Should().BeTrue();
        msg.Stage.Should().Be("interview");
        msg.AutoWaves.Should().BeTrue();
        // Состояние видно на сессии (wire-контракт)
        _sut.GetById(session.Id)!.TeamImplement.Should().NotBeNull();
    }

    [Fact]
    public async Task SetTeamImplement_СоставИАвтоПриВключении_Сохраняются()
    {
        var dir = MkProjectDir("ti-cfg");
        var project = _projectManager.Create("TI-CFG", dir, TestUserId, TestUsername);
        Persona Mk(string name) => _personaManager.Create(TestUserId, name, null, null, null, null, null,
            PersonaScope.Project, project.Id, null, null, memoryEnabled: false);
        var coordinator = Mk("Алекс");
        var planner = Mk("Полина");
        var executors = new[] { Mk("Денис").Id, Mk("Кира").Id };
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var updated = await _sut.SetTeamImplementAsync(session.Id, enabled: true,
            autoWaves: false, coordinatorPersonaId: coordinator.Id, plannerPersonaId: planner.Id,
            executorPersonaIds: executors, userId: TestUserId);

        var ti = updated!.TeamImplement!;
        ti.AutoWaves.Should().BeFalse();
        ti.CoordinatorPersonaId.Should().Be(coordinator.Id);
        ti.PlannerPersonaId.Should().Be(planner.Id);
        ti.ExecutorPersonaIds.Should().Equal(executors);
    }

    [Fact]
    public async Task SetTeamImplement_Выключение_ОбнуляетПоле()
    {
        var session = await MakeStabForModeAsync("ti-off");
        await _sut.SetTeamImplementAsync(session.Id, enabled: true, userId: TestUserId);

        var updated = await _sut.SetTeamImplementAsync(session.Id, enabled: false, userId: TestUserId);

        updated!.TeamImplement.Should().BeNull();
        _sut.GetById(session.Id)!.TeamImplement.Should().BeNull();
        // Последнее WS-событие — режим выключен
        Sent<TeamImplementMessage>().Last().Active.Should().BeFalse();
    }

    // Э7-фикс (Major №1): CoordinatorWriteGuard проверяет команду Bash/PowerShell в момент
    // permission-запроса — а CLI в acceptEdits/bypassPermissions его вообще не присылает
    // (проверено вживую той же командой из находки Веры), гейт был бы мёртв.
    [Theory]
    [InlineData(ClaudeMode.AcceptEdits)]
    [InlineData(ClaudeMode.Bypass)]
    public async Task SetTeamImplement_НесовместимыйРежимПрав_ПереводитВAuto(ClaudeMode incompatible)
    {
        var session = await MakeStabForModeAsync("ti-mode-force-" + incompatible, incompatible);

        await _sut.SetTeamImplementAsync(session.Id, enabled: true, userId: TestUserId);

        _sut.GetById(session.Id)!.Mode.Should().Be(ClaudeMode.Auto,
            "в этом режиме CLI одобряет Bash клиентски, минуя сервер — гейт не сработает");
    }

    [Theory]
    [InlineData(ClaudeMode.Default)]
    [InlineData(ClaudeMode.Auto)]
    public async Task SetTeamImplement_УжеСовместимыйРежимПрав_НеТрогает(ClaudeMode compatible)
    {
        var session = await MakeStabForModeAsync("ti-mode-keep-" + compatible, compatible);

        await _sut.SetTeamImplementAsync(session.Id, enabled: true, userId: TestUserId);

        _sut.GetById(session.Id)!.Mode.Should().Be(compatible,
            "режим и так спрашивает разрешение на Bash — трогать выбор пользователя незачем");
    }

    [Fact]
    public async Task SetTeamImplementAuto_ПереключаетФлагНаХодуБезВыключенияРежима()
    {
        var session = await MakeStabForModeAsync("ti-auto");
        await _sut.SetTeamImplementAsync(session.Id, enabled: true, autoWaves: true, userId: TestUserId);

        var updated = await _sut.SetTeamImplementAutoAsync(session.Id, autoWaves: false, userId: TestUserId);

        var ti = updated!.TeamImplement!;
        ti.AutoWaves.Should().BeFalse("флаг переключён на ходу");
        ti.Stage.Should().Be(TeamImplementStage.Interview, "сама стадия не тронута");
        _sut.GetById(session.Id)!.TeamImplement.Should().NotBeNull("режим остался активен");
    }

    // Minor (волна 3): выключение режима посреди незакрытой волны раньше не оставляло следа —
    // задачи волны сиротели молча (доисполняются, но никто не подводит итог)
    [Fact]
    public async Task SetTeamImplement_ВыключениеПосредиВолны_ОставляетСледВЛенте()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-off-midwave");
        ((ITeamRunState)_sut).WithTeamState(session.Id, t =>
        {
            t.WaveNumber = 1;
            t.ClosedWave = 0; // волна ещё не закрыта
            return true;
        });

        await _sut.SetTeamImplementAsync(session.Id, enabled: false, userId: TestUserId);

        var note = Sent<GuestTextMessage>().Should().ContainSingle().Subject;
        note.Text.Should().Contain("волны 1");
        note.Text.Should().Contain("выключен");
    }

    [Fact]
    public async Task SetTeamImplement_ВыключениеБезЖивойВолны_НичегоНеПишетВЛенту()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-off-idle");
        // Дефолт: WaveNumber == 0 — волна ещё не стартовала

        await _sut.SetTeamImplementAsync(session.Id, enabled: false, userId: TestUserId);

        Sent<GuestTextMessage>().Should().BeEmpty(
            "без незакрытой волны обрывать нечего — лишняя карточка была бы шумом");
    }

    [Fact]
    public async Task SetTeamImplementAuto_БезАктивногоРежима_НеСоздаётСостояние()
    {
        var dir = MkProjectDir("ti-auto-null");
        var project = _projectManager.Create("TI-ANULL", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var updated = await _sut.SetTeamImplementAutoAsync(session.Id, autoWaves: false, userId: TestUserId);

        updated!.TeamImplement.Should().BeNull("переключение авто не включает режим");
    }

    [Fact]
    public async Task SetTeamImplement_ЧужойВладелец_Null()
    {
        var dir = MkProjectDir("ti-owner");
        var project = _projectManager.Create("TI-OWN", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var updated = await _sut.SetTeamImplementAsync(session.Id, enabled: true, userId: "another-user");

        updated.Should().BeNull("чужой владелец не включает режим");
        _sut.GetById(session.Id)!.TeamImplement.Should().BeNull();
    }

    [Fact]
    public void TeamImplement_СериализуетсяRoundTrip_ПереживаетРестарт()
    {
        // Переживание рестарта: Session целиком (с TeamImplement) сериализуется/десериализуется
        // теми же опциями, что SessionManager._jsonOpts (enum converter), и поле попадает в JSON.
        var opts = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var session = new Session
        {
            TeamImplement = new SessionTeamImplement
            {
                Stage = TeamImplementStage.Wave,
                WaveNumber = 2,
                AutoWaves = false,
                ExecutorPersonaIds = ["a", "b"],
                Budget = new TeamImplementBudget { TasksUsed = 3, WavesUsed = 1 },
            }
        };

        var json = JsonSerializer.Serialize(session, opts);
        var restored = JsonSerializer.Deserialize<Session>(json, opts)!;

        // sessions.json хранит PascalCase (SessionManager._jsonOpts без naming policy);
        // REST/SignalR-wire — camelCase (ASP.NET default). Проверяем попадание в хранилище.
        json.Should().Contain("TeamImplement", "поле попадает в JSON-стор (видно фронту)");
        json.Should().Contain("AutoWaves", "флаг авто персистентен");
        var ti = restored.TeamImplement!;
        ti.Should().NotBeNull();
        ti.Stage.Should().Be(TeamImplementStage.Wave);
        ti.WaveNumber.Should().Be(2);
        ti.AutoWaves.Should().BeFalse();
        ti.ExecutorPersonaIds.Should().Equal("a", "b");
        ti.Budget.TasksUsed.Should().Be(3);
        ti.Budget.WavesUsed.Should().Be(1);
    }

    // Дыра покрытия (волна 3): рестарт сервера ПОСРЕДИ интервью — SavedMode/PlanVersion/
    // InterviewRounds обязаны пережить сериализацию, иначе после рестарта чат теряет либо
    // навязанный план-режим (селектор разблокировался бы посреди интервью), либо счёт раундов
    // (лимит вопросов обнулился бы и координатор мог бы спрашивать заново).
    [Fact]
    public void TeamImplement_РестартПосредиИнтервью_СохраняетSavedModePlanVersionInterviewRounds()
    {
        var opts = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var session = new Session
        {
            Mode = ClaudeMode.Plan,
            TeamImplement = new SessionTeamImplement
            {
                Stage = TeamImplementStage.Interview,
                SavedMode = ClaudeMode.Auto,
                PlanVersion = 1,
                ApprovedPlanVersion = 0,
                InterviewRounds = 1,
                Replanning = true,
                FirstIterationOpened = true,
                LastPlanRequest = "экспорт в CSV",
                LastPlanFeedback = "убери ревью из плана",
            }
        };

        var json = JsonSerializer.Serialize(session, opts);
        var restored = JsonSerializer.Deserialize<Session>(json, opts)!;

        var ti = restored.TeamImplement!;
        restored.Mode.Should().Be(ClaudeMode.Plan);
        ti.Stage.Should().Be(TeamImplementStage.Interview);
        ti.SavedMode.Should().Be(ClaudeMode.Auto, "иначе селектор режима после рестарта разблокировался бы посреди интервью");
        ti.PlanVersion.Should().Be(1);
        ti.InterviewRounds.Should().Be(1, "иначе лимит раундов обнулился бы и координатор мог бы спрашивать заново");
        ti.Replanning.Should().BeTrue();
        ti.FirstIterationOpened.Should().BeTrue();
        ti.LastPlanRequest.Should().Be("экспорт в CSV");
        ti.LastPlanFeedback.Should().Be("убери ревью из плана",
            "иначе повтор планирования после рестарта потерял бы правку человека");
    }

    [Fact]
    public async Task CreateTeamPlan_РаздаётРаботуПоПрофилюИПубликуетКарточку()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-plan");
        SetPlannerAnswer(backend, frontend);

        var (plan, reason) = await _sut.CreateTeamPlanAsync(session.Id, "Добавить экспорт в CSV", TestUserId);

        reason.Should().BeNull();
        plan.Should().NotBeNull();
        // Бэкендовая часть — бэкендеру, фронтовая — фронтендеру, обоснование в данных карточки
        plan!.Subtasks[0].ExecutorPersonaId.Should().Be(backend.Id);
        plan.Subtasks[0].ExecutorRationale.Should().Be("Серверная часть — его зона");
        plan.Subtasks[1].ExecutorPersonaId.Should().Be(frontend.Id);
        plan.WaveCount.Should().Be(2);

        // Карточка ушла в ленту WS-событием
        var card = Sent<TeamPlanMessage>().Single();
        card.Type.Should().Be("team_plan");
        card.PlanId.Should().Be(plan.Id);
        card.Resolved.Should().BeFalse("план ждёт подтверждения человека");
        card.Plan.Subtasks.Should().HaveCount(2);

        // Режим перешёл в стадию подтверждения и запомнил карточку
        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Stage.Should().Be(TeamImplementStage.Confirming);
        ti.PlanCardId.Should().Be(plan.Id);
    }

    // --- «Замысел в карточке и полный план файлом» (решение владельца 2026-08-02) ---

    [Fact]
    public async Task CreateTeamPlan_СЗамыслом_ПишетФайлПланаИКладётПутьВКарточку()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-plan-file");
        _plannerAnswer = $$"""
            {"summary":"Экспорт задач в CSV",
             "intent":"Идём через готовый эндпоинт экспорта и кнопку в тулбаре, сложную фильтрацию не делаем.",
             "subtasks":[
               {"title":"Эндпоинт экспорта","goal":"GET /api/tasks/export",
                "executorPersonaId":"{{backend.Id}}","executorRationale":"Серверная часть — его зона",
                "files":["backend/Controllers/TasksController.cs"],"wave":1,"doneCriteria":"отдаёт CSV"},
               {"title":"Кнопка «Экспорт»","goal":"Кнопка в тулбаре",
                "executorPersonaId":"{{frontend.Id}}","executorRationale":"UI — её зона",
                "files":["frontend/src/components/Toolbar.tsx"],"wave":2,"doneCriteria":"файл скачивается"}]}
            """;

        var (plan, reason) = await _sut.CreateTeamPlanAsync(session.Id, "Добавить экспорт в CSV", TestUserId);

        reason.Should().BeNull();
        plan.Should().NotBeNull();
        plan!.Intent.Should().Contain("Идём через готовый эндпоинт");
        plan.PlanFilePath.Should().NotBeNull()
            .And.StartWith("docs/plans/team/").And.EndWith("plan-v1.md");

        var project = _projectManager.GetById(session.ProjectId!)!;
        var full = Path.Combine(project.RootPath, plan.PlanFilePath!.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(full).Should().BeTrue("сервер рендерит файл при публикации карточки, а не модель");
        var content = File.ReadAllText(full);
        content.Should().Contain("Эндпоинт экспорта").And.Contain("Идём через готовый эндпоинт")
            .And.Contain("Серверная часть — его зона").And.Contain("`backend/Controllers/TasksController.cs`");

        // Путь в карточке совпадает с записанным файлом — контракт для фронта
        var card = Sent<TeamPlanMessage>().Single();
        card.Plan.PlanFilePath.Should().Be(plan.PlanFilePath);
    }

    [Fact]
    public async Task CreateTeamPlan_Перепланирование_ДаётВторойФайлПерваяЦела()
    {
        var (session, plan, _) = await MakeStabWithPlanAndStarterAsync("ti-plan-replan");
        await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run, userId: TestUserId);
        var v1Path = plan.PlanFilePath;
        v1Path.Should().NotBeNull().And.EndWith("plan-v1.md");

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "<escalate:clarify>неясен формат</escalate>", failed: false);
        _plannerAnswer = $$"""
            {"summary":"Экспорт в XLSX","intent":"Меняем формат на XLSX по просьбе человека.",
             "subtasks":[{"title":"Выгрузка XLSX","goal":"писать xlsx",
              "executorPersonaId":"","executorRationale":"серверная часть",
              "files":["backend/Export.cs"],"wave":1,"doneCriteria":"файл открывается"}]}
            """;

        await _sut.HandleTeamTurnEndAsync(session.Id,
            "<team:work>переделать экспорт на XLSX</team>", failed: false);

        var card = Sent<TeamPlanMessage>().Last();
        card.Plan.Version.Should().Be(2, "план vN после уточнений");
        var v2Path = card.Plan.PlanFilePath;
        v2Path.Should().NotBeNull().And.EndWith("plan-v2.md").And.NotBe(v1Path,
            "версия плана — отдельный файл, перепланирование не перезаписывает предыдущий");

        var project = _projectManager.GetById(session.ProjectId!)!;
        string Full(string rel) => Path.Combine(project.RootPath, rel.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(Full(v1Path!)).Should().BeTrue();
        File.Exists(Full(v2Path!)).Should().BeTrue();
        File.ReadAllText(Full(v1Path!)).Should().NotContain("Выгрузка XLSX",
            "перепланирование не должно перезаписывать первую версию");
        File.ReadAllText(Full(v2Path!)).Should().Contain("Выгрузка XLSX");
    }

    // Прод 2026-08-03 (находка Веры): в пятом прогоне того же чата карточка плана ссылалась
    // на файл ЧЕТВЁРТОГО прогона — слаг папки строится из имени чата (не меняется по ходу
    // разговора), а версия плана у каждой новой вводной снова стартует с v1. Две вводные
    // подряд в одном чате обязаны дать два разных файла с рабочими ссылками, и файл первой
    // вводной не должен быть тронут второй.
    [Fact]
    public async Task ДвеВводныеПодряд_ДваРазныхФайлаПлана_ПерваяЦела()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-two-briefs");
        SetPlannerAnswer(backend, frontend);

        // Первая вводная — настоящим приёмом сообщения (как в проде): ResetTeamIterationOnUserInput
        // открывает вводную №1 ДО того, как координатор классифицирует её работой.
        await _sut.SendMessageAsync(session.Id, "добавить экспорт в CSV", []);
        SetTeamTurnFromHuman(GetEntry(session.Id), true);
        await _sut.HandleTeamTurnEndAsync(session.Id,
            "<team:work>добавить экспорт в CSV</team>", failed: false);

        var plan1 = Sent<TeamPlanMessage>().Last();
        plan1.Plan.PlanFilePath.Should().NotBeNull().And.EndWith("iter1/plan-v1.md");
        await _sut.RespondTeamPlanAsync(session.Id, plan1.PlanId, TeamPlanDecision.Run, userId: TestUserId);

        // Волна отработала, проверка подведена — режим ждёт следующую вводную (как MakeIdleStabAsync)
        await _sut.HandleTeamTurnEndAsync(session.Id, "итог: всё готово", failed: false);
        _sut.GetById(session.Id)!.TeamImplement!.Stage = TeamImplementStage.Checking;
        await _sut.HandleTeamTurnEndAsync(session.Id,
            "Итерация завершена: 2 задачи, проверка пройдена", failed: false);
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Idle);

        // Вторая вводная — новый запрос в том же чате, то же имя чата (слаг папки не меняется)
        SetAdditionalPlannerAnswer(backend);
        session.Status = SessionStatus.Working;
        await _sut.SendMessageAsync(session.Id, "теперь добавь выгрузку в XLSX", []);
        SetTeamTurnFromHuman(GetEntry(session.Id), true);
        await _sut.HandleTeamTurnEndAsync(session.Id,
            "Понял, беру в работу.\n<team:work>добавить выгрузку в XLSX</team>", failed: false);

        var plan2 = Sent<TeamPlanMessage>().Last();
        var path1 = plan1.Plan.PlanFilePath!;
        var path2 = plan2.Plan.PlanFilePath;

        path2.Should().NotBeNull().And.EndWith("iter2/plan-v1.md")
            .And.NotBe(path1, "вторая вводная того же чата обязана писать в свой файл");

        var project = _projectManager.GetById(session.ProjectId!)!;
        string Full(string rel) => Path.Combine(project.RootPath, rel.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(Full(path1)).Should().BeTrue("ссылка первой карточки обязана вести на существующий файл");
        File.Exists(Full(path2!)).Should().BeTrue("ссылка второй карточки обязана вести на существующий файл");
        File.ReadAllText(Full(path1)).Should().Contain("Эндпоинт экспорта")
            .And.NotContain("XLSX-выгрузка", "файл первой вводной не должен быть тронут второй");
        File.ReadAllText(Full(path2!)).Should().Contain("XLSX-выгрузка");
    }

    [Fact]
    public async Task CreateTeamPlan_ГлобальныйЧатБезПроекта_ПубликуетсяБезФайлаИБезОшибок()
    {
        // Чат вне проекта резолвит домашнюю папку через UserStore — нужен настоящий пользователь,
        // а не голый TestUserId (тот заведён только в data проектов, не в UserStore)
        var owner = _userStore.Add("ti-global-owner", "password123", "user");
        Persona MkGlobal(string name, string role, PersonaSpecialty spec = PersonaSpecialty.None) =>
            _personaManager.Create(owner.Id, name, role, null, null, null, null,
                PersonaScope.Global, null, null, null, memoryEnabled: false, specialty: spec);

        var coordinator = MkGlobal("Алекс-Г", "Тимлид", PersonaSpecialty.Coordinator);
        var executor = MkGlobal("Денис-Г", "Бэкенд");
        var session = await _sut.CreatePersonaChatAsync(owner.Id, coordinator.Id, ClaudeMode.Auto);
        session.ProjectId.Should().BeNull("персона и чат глобальные — команды проекта нет");

        await _sut.SetTeamImplementAsync(session.Id, enabled: true,
            executorPersonaIds: [executor.Id], userId: owner.Id);
        _plannerAnswer = $$"""
            {"summary":"Сделать штуку","intent":"Коротко и по делу.",
             "subtasks":[{"title":"Штука","executorPersonaId":"{{executor.Id}}",
              "executorRationale":"его зона","wave":1}]}
            """;

        var (plan, reason) = await _sut.CreateTeamPlanAsync(session.Id, "Сделай штуку", owner.Id);

        reason.Should().BeNull();
        plan.Should().NotBeNull();
        plan!.PlanFilePath.Should().BeNull("вне проекта писать план некуда — практика при этом работает как раньше");
        Sent<TeamPlanMessage>().Single().Plan.PlanFilePath.Should().BeNull();
    }

    [Fact]
    public async Task CreateTeamPlan_ОшибкаЗаписиФайла_ПубликацияНеПадает()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-plan-write-fail");
        SetPlannerAnswer(backend, frontend);
        var project = _projectManager.GetById(session.ProjectId!)!;
        // Занимаем ожидаемый путь файла директорией — запись бросит исключение
        var rel = TeamPlanFileRenderer.RelativePath(session.Name, session.Id, 1, 1);
        Directory.CreateDirectory(Path.Combine(project.RootPath, rel.Replace('/', Path.DirectorySeparatorChar)));

        var (plan, reason) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);

        reason.Should().BeNull("ошибка записи файла плана не должна ронять публикацию карточки");
        plan.Should().NotBeNull();
        plan!.PlanFilePath.Should().BeNull("запись не удалась — ссылки в карточке нет, а не отказ публикации");
        Sent<TeamPlanMessage>().Should().ContainSingle();
    }

    // B2 приёмки: отказ обязан приходить ДО интервью, а не после потраченного хода
    [Fact]
    public async Task SetTeamImplement_ЧатБезПерсоны_ОтклоняетНаВходеИНеСоздаётСостояние()
    {
        var dir = MkProjectDir("ti-nocoord");
        var project = _projectManager.Create("TI-NC", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var act = () => _sut.SetTeamImplementAsync(session.Id, enabled: true, userId: TestUserId);

        var ex = (await act.Should().ThrowAsync<TeamImplementSetupException>()).Which;
        ex.Code.Should().Be(TeamImplementSetupException.NoCoordinator, "фронт различает отказ машинно");
        ex.Message.Should().Contain("координатора");
        _sut.GetById(session.Id)!.TeamImplement.Should().BeNull("режим не включился");
        Sent<TeamImplementMessage>().Should().BeEmpty("WS-события режима не было");
    }

    // Симметричный случай спеки: состава исполнителей нет (в команде только сам координатор,
    // явный список пуст) — подбирать не из кого. Отказ тоже на входе, а не после интервью.
    [Fact]
    public async Task SetTeamImplement_БезСоставаИсполнителей_ОтклоняетНаВходе()
    {
        var dir = MkProjectDir("ti-noexec");
        var project = _projectManager.Create("TI-NOEXEC", dir, TestUserId, TestUsername);
        var coordinator = _personaManager.Create(TestUserId, "Алекс-один", "Тимлид", null, null, null, null,
            PersonaScope.Project, project.Id, null, null, memoryEnabled: false);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, personaId: coordinator.Id);

        var act = () => _sut.SetTeamImplementAsync(session.Id, enabled: true,
            coordinatorPersonaId: coordinator.Id, userId: TestUserId);

        var ex = (await act.Should().ThrowAsync<TeamImplementSetupException>()).Which;
        ex.Code.Should().Be(TeamImplementSetupException.NoExecutors);
        ex.Message.Should().Contain("исполнителей");
        _sut.GetById(session.Id)!.TeamImplement.Should().BeNull();
    }

    // Тот же чат, но состав задан явно — включается
    [Fact]
    public async Task SetTeamImplement_ЯвныйСостав_Включается()
    {
        var dir = MkProjectDir("ti-exec-explicit");
        var project = _projectManager.Create("TI-EXEC", dir, TestUserId, TestUsername);
        Persona Mk(string name) => _personaManager.Create(TestUserId, name, null, null, null, null, null,
            PersonaScope.Project, project.Id, null, null, memoryEnabled: false);
        var coordinator = Mk("Алекс-2");
        var executor = Mk("Денис-2");
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto, personaId: coordinator.Id);

        var updated = await _sut.SetTeamImplementAsync(session.Id, enabled: true,
            executorPersonaIds: [executor.Id], userId: TestUserId);

        updated!.TeamImplement!.ExecutorPersonaIds.Should().Equal(executor.Id);
    }

    [Fact]
    public async Task CreateTeamPlan_БезВключённогоРежима_Отказ()
    {
        var dir = MkProjectDir("ti-nomode");
        var project = _projectManager.Create("TI-NM", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var (plan, reason) = await _sut.CreateTeamPlanAsync(session.Id, "сделай фичу", TestUserId);

        plan.Should().BeNull();
        reason.Should().Contain("не включён");
    }

    [Fact]
    public async Task RespondTeamPlan_СменаИсполнителяДоЗапуска_КарточкаОстаётсяОткрытой()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-reassign");
        SetPlannerAnswer(backend, frontend);
        var (plan, _) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);
        var subtask = plan!.Subtasks[0];

        var updated = await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Reassign,
            subtask.Id, frontend.Id, TestUserId);

        updated!.Subtasks[0].ExecutorPersonaId.Should().Be(frontend.Id, "исполнитель сменён вручную");
        updated.Subtasks[0].ExecutorRationale.Should().Contain("вручную");
        updated.Approved.Should().BeNull("карточка ещё не решена");
        var last = Sent<TeamPlanMessage>().Last();
        last.Resolved.Should().BeFalse();
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Confirming);
    }

    [Fact]
    public async Task RespondTeamPlan_ЧужойИсполнитель_Отклоняется()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-alien");
        SetPlannerAnswer(backend, frontend);
        var (plan, _) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);

        var updated = await _sut.RespondTeamPlanAsync(session.Id, plan!.Id, TeamPlanDecision.Reassign,
            plan.Subtasks[0].Id, "чужая-персона", TestUserId);

        updated.Should().BeNull("исполнителем можно поставить только свою персону");
        plan.Subtasks[0].ExecutorPersonaId.Should().Be(backend.Id, "назначение не тронуто");
    }

    [Fact]
    public async Task RespondTeamPlan_Запуск_РешаетКарточкуИДвигаетСтадию()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-run");
        SetPlannerAnswer(backend, frontend);
        var (plan, _) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);

        var updated = await _sut.RespondTeamPlanAsync(session.Id, plan!.Id, TeamPlanDecision.Run,
            userId: TestUserId);

        updated!.Approved.Should().BeTrue();
        var last = Sent<TeamPlanMessage>().Last();
        last.Resolved.Should().BeTrue();
        last.Approved.Should().BeTrue();
        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Stage.Should().Be(TeamImplementStage.Wave);
        // Плановое число волн — из плана, а не из потолка бюджета: бейдж покажет «из 2», а не «из 4»
        ti.PlannedWaves.Should().Be(2);
        ti.Budget.MaxWaves.Should().Be(4, "потолок бюджета к плановому числу волн отношения не имеет");
    }

    [Fact]
    public async Task RespondTeamPlan_Запуск_ЗовётРаздачуВолны()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-wave-hook");
        SetPlannerAnswer(backend, frontend);
        var (plan, _) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);
        // Хук раздачи (в бою его вешает TeamWaveService — цикл DI разорван им же)
        TeamImplementPlan? handed = null;
        _sut.TeamHandlers.WaveStarter = (_, p, _) => { handed = p; return Task.CompletedTask; };

        await _sut.RespondTeamPlanAsync(session.Id, plan!.Id, TeamPlanDecision.Run, userId: TestUserId);

        handed.Should().BeSameAs(plan, "по «Запустить» задачи раздаёт бэкенд, а не модель");
    }

    [Fact]
    public async Task RespondTeamPlan_Отмена_РаздачуНеЗовёт()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-wave-cancel");
        SetPlannerAnswer(backend, frontend);
        var (plan, _) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);
        var called = false;
        _sut.TeamHandlers.WaveStarter = (_, _, _) => { called = true; return Task.CompletedTask; };

        await _sut.RespondTeamPlanAsync(session.Id, plan!.Id, TeamPlanDecision.Cancel, userId: TestUserId);

        called.Should().BeFalse();
        _sut.GetById(session.Id)!.TeamImplement!.PlannedWaves.Should().Be(0);
    }

    [Fact]
    public async Task RespondTeamPlan_Отмена_ВозвращаетКПланированию()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-cancel");
        SetPlannerAnswer(backend, frontend);
        var (plan, _) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);

        var updated = await _sut.RespondTeamPlanAsync(session.Id, plan!.Id, TeamPlanDecision.Cancel,
            userId: TestUserId);

        updated!.Approved.Should().BeFalse();
        var ti = _sut.GetById(session.Id)!.TeamImplement!;
        ti.Stage.Should().Be(TeamImplementStage.Planning);
        ti.PlanCardId.Should().BeNull("отменённая карточка не остаётся текущей");
    }

    [Fact]
    public async Task RespondTeamPlan_ЧужойВладелец_Null()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-owner2");
        SetPlannerAnswer(backend, frontend);
        var (plan, _) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);

        var updated = await _sut.RespondTeamPlanAsync(session.Id, plan!.Id, TeamPlanDecision.Run,
            userId: "another-user");

        updated.Should().BeNull();
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Confirming);
    }

    [Fact]
    public async Task RespondTeamPlan_ПослеРестарта_ПланПоднимаетсяСДискаИВолнаРаздаётся()
    {
        var (session, plan) = await MakeRestartedStabWithPlanAsync("ti-plan-restart");
        TeamImplementPlan? handed = null;
        _sut.TeamHandlers.WaveStarter = (_, p, _) => { handed = p; return Task.CompletedTask; };

        var updated = await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run,
            userId: TestUserId);

        updated.Should().NotBeNull("после рестарта карточка живёт на диске — оттуда её и берём");
        updated!.Approved.Should().BeTrue();
        handed.Should().NotBeNull();
        handed!.Id.Should().Be(plan.Id, "волна раздаётся по плану, поднятому с диска");
        _sut.GetById(session.Id)!.TeamImplement!.Stage.Should().Be(TeamImplementStage.Wave);

        // Решение записано в историю: карточка погашена и повторный клик волну не удвоит
        var history = await _sut.GetHistoryAsync(session.Id);
        var card = history.OfType<StoredTeamPlanMessage>().Last();
        card.Resolved.Should().BeTrue();
        card.Approved.Should().BeTrue();

        handed = null;
        var second = await _sut.RespondTeamPlanAsync(session.Id, plan.Id, TeamPlanDecision.Run,
            userId: TestUserId);
        second.Should().BeNull("двойной клик по уже решённой карточке проходит только раз");
        handed.Should().BeNull();
    }

    [Fact]
    public async Task SaveTeamPlanCard_ПослеРестарта_ПишетРаздачуВИсториюНаДиске()
    {
        // Раздача проставляет под-задачам TaskId. Не запиши мы это на диск — следующее
        // чтение плана увидело бы их нерозданными и завело дубли задач.
        var (session, plan) = await MakeRestartedStabWithPlanAsync("ti-plan-save-disk");
        plan.Subtasks[0].TaskId = "task-42";

        await ((ITeamHistoryStore)_sut).SavePlanCardAsync(session.Id,
            new PlanCardWriteRequest(plan, Resolved: false, Approved: null, SupersededBy: null));

        var reloaded = await ((ITeamHistoryStore)_sut).GetTeamPlanAsync(session.Id, plan.Id);
        reloaded!.Subtasks[0].TaskId.Should().Be("task-42");
    }

    [Fact]
    public async Task TeamPlan_КарточкаПопадаетВИсторию_ПереживаетПерезагрузку()
    {
        var (session, backend, frontend) = await MakeTeamStabAsync("ti-history");
        SetPlannerAnswer(backend, frontend);
        var (plan, _) = await _sut.CreateTeamPlanAsync(session.Id, "Экспорт", TestUserId);

        var history = await _sut.GetHistoryAsync(session.Id);
        var card = history.OfType<StoredTeamPlanMessage>().Single();

        card.PlanId.Should().Be(plan!.Id);
        card.Resolved.Should().BeFalse();
        card.Plan.Subtasks.Should().HaveCount(2);
        card.Plan.Subtasks[0].ExecutorRationale.Should().NotBeEmpty("обоснование видно в данных карточки");
    }

    [Fact]
    public async Task КвотаЗапуска_РежимИЦелыйБюджет_РазрешаетИСразуСчитаетРасход()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-ok");
        SetWaveStage(session.Id);

        var (verdict, reason) = _sut.TryConsumeTeamImplementRun(session.Id, TestUserId);

        verdict.Should().Be(SessionManager.TeamRunQuota.Allowed,
            "на ходу-реакции штаба запрет заменён квотой — иначе автономный цикл невозможен");
        reason.Should().BeNull();
        _sut.GetById(session.Id)!.TeamImplement!.Budget.RunsUsed.Should().Be(1,
            "счёт ведёт бэкенд в точке запуска, а не модель");
    }

    [Fact]
    public async Task ГейтЗапуска_ОбычныйХодШтаба_ТожеРасходуетКвоту()
    {
        // Дыра, которую чиним: на НЕреакционном ходу фильтр выходил до квоты, и координатор
        // мог заспамить «создать задачу + запустить» мимо бюджета итерации
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-human");
        SetWaveStage(session.Id);
        var context = MakeMcpCallContext(session.Id);

        ExecuteFilter().OnActionExecuting(context);

        context.Result.Should().BeNull("бюджет цел — запуск разрешён");
        var budget = _sut.GetById(session.Id)!.TeamImplement!.Budget;
        budget.RunsUsed.Should().Be(1, "квота расходуется на любом ходу штаба, не только на реакционном");
        budget.TasksUsed.Should().Be(1, "запущенная руками задача — такая же задача итерации");
    }

    [Fact]
    public async Task ГейтЗапуска_ОбычныйХодШтабаПриВыбранномБюджете_Отказ()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-human-out");
        SetWaveStage(session.Id);
        var budget = _sut.GetById(session.Id)!.TeamImplement!.Budget;
        budget.RunsUsed = budget.MaxRuns;
        var context = MakeMcpCallContext(session.Id);

        ExecuteFilter().OnActionExecuting(context);

        var result = context.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.ObjectResult>().Subject;
        result.StatusCode.Should().Be(403);
        budget.RunsUsed.Should().Be(budget.MaxRuns, "отказ ничего не расходует");
    }

    // Единственная кнопка, поднимающая потолки, — «Добавить бюджет» на карточке
    // BudgetExhausted; попросить её координатор не может (в протоколе лишь deviation/check/
    // clarify). Пока гейт молчал, штаб упирался в отказ бесконечно: человек жал «Разрешить»
    // на карточке расхождения с планом, а та бюджет не трогает (прод 2026-08-08).
    [Fact]
    public async Task КвотаЗапуска_ВыбранныйБюджет_ПоднимаетКарточкуСКнопкойДобавитьБюджет()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-card");
        SetWaveStage(session.Id);
        var budget = _sut.GetById(session.Id)!.TeamImplement!.Budget;
        budget.WavesUsed = budget.MaxWaves;

        // Ждём СОБЫТИЕ, а не время: карточка публикуется фоновой задачей (FireAndForget),
        // и на голодном раннере CI пауза фиксированной длины давала бы плавающий провал
        var raised = new TaskCompletionSource<TeamEscalation>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sut.TeamHandlers.EscalationRaiser = (_, card) => { raised.TrySetResult(card); return Task.CompletedTask; };

        var (verdict, _) = _sut.TryConsumeTeamImplementRun(session.Id, TestUserId);

        verdict.Should().Be(SessionManager.TeamRunQuota.Exhausted);
        var finished = await Task.WhenAny(raised.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        finished.Should().Be(raised.Task, "исчерпание бюджета обязано дойти до человека карточкой");
        var escalation = await raised.Task;
        escalation.Kind.Should().Be(TeamEscalationKind.BudgetExhausted);
        escalation.Actions.Select(a => a.Id).Should().Contain("addBudget",
            "потолки поднимает только эта кнопка — без неё штаб остаётся в тупике");
    }

    // Прочие отказы гейта человек уже видит: «остановлено» и «ждёт решения» висят карточкой,
    // неподтверждённый план — карточкой плана. Вторая карточка была бы спамом.
    [Fact]
    public async Task КвотаЗапуска_ОтказНеИзБюджета_КарточкуНеПлодит()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-card-none");
        SetWaveStage(session.Id);
        ((ITeamRunState)_sut).WithTeamState(session.Id, t => { t.Stopped = true; return true; });

        var raised = new TaskCompletionSource<TeamEscalation>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sut.TeamHandlers.EscalationRaiser = (_, card) => { raised.TrySetResult(card); return Task.CompletedTask; };

        var (verdict, _) = _sut.TryConsumeTeamImplementRun(session.Id, TestUserId);

        verdict.Should().Be(SessionManager.TeamRunQuota.Exhausted);
        // Ожидание отрицательного результата: ждём с запасом и убеждаемся, что не пришло.
        // Провал в другую сторону (медленный раннер) даст ложно-зелёный, а не ложно-красный
        var finished = await Task.WhenAny(raised.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        finished.Should().NotBe(raised.Task, "про остановку человек уже знает из своей же карточки");
    }

    [Fact]
    public async Task ГейтЗапуска_ОбычныйЧатВнеРежима_РаботаетКакРаньше()
    {
        var dir = MkProjectDir("ti-quota-plain-filter");
        var project = _projectManager.Create("TI-QPF", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);
        var context = MakeMcpCallContext(session.Id);

        ExecuteFilter().OnActionExecuting(context);

        context.Result.Should().BeNull("вне режима обычный ход как был разрешён, так и остался");
    }

    // m3 (второй проход Глеба): OnActionExecuting списывает квоту АВАНСОМ, до попытки
    // запуска — Execute может вернуть 404 (задача не найдена). Действие не состоялось,
    // платить команде не с чего — OnActionExecuted обязан вернуть списанную единицу.
    [Fact]
    public async Task ГейтЗапуска_ДействиеВернуло404_ВозвращаетСписаннуюКвоту()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-refund-404");
        SetWaveStage(session.Id);
        var context = MakeMcpCallContext(session.Id);
        var filter = ExecuteFilter();
        filter.OnActionExecuting(context);
        _sut.GetById(session.Id)!.TeamImplement!.Budget.RunsUsed.Should().Be(1, "квота списана авансом");

        var executed = new Microsoft.AspNetCore.Mvc.Filters.ActionExecutedContext(
            context, [], controller: new object())
        { Result = new Microsoft.AspNetCore.Mvc.NotFoundResult() };
        filter.OnActionExecuted(executed);

        var budget = _sut.GetById(session.Id)!.TeamImplement!.Budget;
        budget.RunsUsed.Should().Be(0, "действие не состоялось (404) — впустую списанная единица вернулась");
        budget.TasksUsed.Should().Be(0);
    }

    [Fact]
    public async Task ГейтЗапуска_ДействиеУспешно_КвотаОстаётсяСписанной()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-refund-ok");
        SetWaveStage(session.Id);
        var context = MakeMcpCallContext(session.Id);
        var filter = ExecuteFilter();
        filter.OnActionExecuting(context);

        var executed = new Microsoft.AspNetCore.Mvc.Filters.ActionExecutedContext(
            context, [], controller: new object())
        { Result = new Microsoft.AspNetCore.Mvc.OkObjectResult(new { }) };
        filter.OnActionExecuted(executed);

        _sut.GetById(session.Id)!.TeamImplement!.Budget.RunsUsed.Should().Be(1, "успех — единица расходуется честно");
    }

    [Fact]
    public async Task КвотаЗапуска_ИзЧатаИсполненияПодШтабом_СписываетсяСБюджетаШтаба()
    {
        // Запуск второго уровня: исполнитель заводит задачу и запускает её из своего чата.
        // Без подъёма к штабу это был бы обход бюджета этажом ниже.
        var (stab, _, _) = await MakeTeamStabAsync("ti-quota-child");
        SetWaveStage(stab.Id);
        var child = await _sut.CreateAsync(stab.ProjectId!, ClaudeMode.Auto);
        _sut.SetParent(child.Id, stab.Id, TestUserId);

        var (verdict, _) = _sut.TryConsumeTeamImplementRun(child.Id, TestUserId);

        verdict.Should().Be(SessionManager.TeamRunQuota.Allowed);
        var budget = _sut.GetById(stab.Id)!.TeamImplement!.Budget;
        budget.RunsUsed.Should().Be(1, "расход лёг на бюджет штаба, а не потерялся");
    }

    [Fact]
    public async Task КвотаЗапуска_ИсчерпанныйБюджет_ОтказСПричиной()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-out");
        SetWaveStage(session.Id);
        var budget = _sut.GetById(session.Id)!.TeamImplement!.Budget;
        budget.RunsUsed = budget.MaxRuns;

        var (verdict, reason) = _sut.TryConsumeTeamImplementRun(session.Id, TestUserId);

        verdict.Should().Be(SessionManager.TeamRunQuota.Exhausted);
        reason.Should().Contain("запусков исполнителей");
        budget.RunsUsed.Should().Be(budget.MaxRuns, "отказ ничего не расходует");
    }

    [Fact]
    public async Task КвотаЗапуска_ВнеРежимаПрактики_ГейтРаботаетКакРаньше()
    {
        var dir = MkProjectDir("ti-quota-plain");
        var project = _projectManager.Create("TI-QP", dir, TestUserId, TestUsername);
        var session = await _sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var (verdict, _) = _sut.TryConsumeTeamImplementRun(session.Id, TestUserId);

        verdict.Should().Be(SessionManager.TeamRunQuota.NotTeamMode,
            "обычный чат остаётся под прежним запретом DenyOnDelegatedTurn");
    }

    [Fact]
    public async Task КвотаЗапуска_ЧужойВладелец_НеРаспознаётРежим()
    {
        var (session, _, _) = await MakeTeamStabAsync("ti-quota-alien");

        var (verdict, _) = _sut.TryConsumeTeamImplementRun(session.Id, "another-user");

        verdict.Should().Be(SessionManager.TeamRunQuota.NotTeamMode);
        _sut.GetById(session.Id)!.TeamImplement!.Budget.RunsUsed.Should().Be(0);
    }
}
