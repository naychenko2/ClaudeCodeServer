using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Services.Spend;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Блокер вердикта efa973f5: в бою ядро обязано передать реестр актора в учёт расхода хода
// (SessionManager.cs:11022 → SpendMapping.RecordTurnSpend). Запись строится пока ход жив —
// переход статуса по result идёт ниже по методу, ядро снимает актора вместе со статусом,
// и потребитель после конца хода получил бы null (урок R-S2a-6c). Здесь — ровно боевой
// порядок: живой ход участника → result → запись несёт его ActorUserId; серверный ход
// (без инициатора) — без актора.
public partial class SessionManagerHierarchyTests
{
    private sealed class RecordingSpend : ISpendCollector
    {
        public List<SpendRecord> Records { get; } = [];
        public void Record(SpendRecord record) => Records.Add(record);
    }

    // Фикстура собирает _sut без коллектора (параметр ctor необязательный): подставляем
    // считающий в то же поле _spend, которое читает боевой путь. С S3-6 расход хода пишет
    // TurnCompletion через TurnCostLedger: боевое поле — на журнале цен роли.
    private RecordingSpend AttachSpend()
    {
        var rec = new RecordingSpend();
        var completion = typeof(SessionManager).GetField("_completion",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(_sut)!;
        var costs = typeof(TurnCompletion).GetField("_costs",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(completion)!;
        typeof(TurnCostLedger).GetField("_spend",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(costs, rec);
        return rec;
    }

    private static ResultMessage TurnResult() =>
        new("success", 100, 1, new UsageInfo(1000, 200, 0, 0), 0.01);

    [Fact]
    public async Task ХодУчастника_ЗаписьРасодаНесётЕгоАктора_БоевойПуть()
    {
        var (session, _) = await RunChatAsync("sp-actor");
        var spend = AttachSpend();

        await _sut.SendMessageAndWaitAsync(session.Id, "из чата", TimeSpan.Zero, senderUserId: Участник);
        // result приходит при живом ходе — здесь ядро и читает актора
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            TurnResult(), TestRunId);

        var rec = Assert.Single(spend.Records);
        rec.ActorUserId.Should().Be(Участник, "учёт идёт до снятия актора — реестр ещё жив");
        rec.OwnerId.Should().Be(TestUserId, "ключ хранилища остаётся арендатором-владельцем");
    }

    [Fact]
    public async Task СерверныйХод_ЗаписьРасодаБезАктора()
    {
        var (session, _) = await RunChatAsync("sp-server");
        var spend = AttachSpend();

        await _sut.SendMessageAsync(session.Id, "автоматика", [], auto: true);
        await InvokeOnMessageAsync(session.Id, new TurnAccumulator(new List<StoredMessage>()),
            TurnResult(), TestRunId);

        Assert.Single(spend.Records).ActorUserId.Should().BeNull(
            "хода без инициатора в реестре нет — срез чтения отнесёт запись владельцу");
    }

    // ── Инициатор хода в записи траты (защёлка SessionEntry.TurnFromHuman) ──────────────────
    // Проба идёт боевым порядком: подача → result при живом ходе → запись. Значения: human —
    // защёлка взведена подачей человека; agent — запись хода есть, защёлка не взведена.

    private async Task<string?> InitiatorOfNextResultAsync(string sessionId, RecordingSpend spend, long runId = TestRunId)
    {
        var before = spend.Records.Count;
        await InvokeOnMessageAsync(sessionId, new TurnAccumulator(new List<StoredMessage>()), TurnResult(), runId);
        spend.Records.Should().HaveCount(before + 1);
        return spend.Records[^1].Initiator;
    }

    [Fact]
    public async Task Инициатор_ПодачаЧеловека_Human_ИАктороМУчастникаОн_НеВлияет()
    {
        var (session, _) = await RunChatAsync("in-human");
        var spend = AttachSpend();

        await _sut.SendMessageAsync(session.Id, "от человека", [], senderUserId: Участник);

        (await InitiatorOfNextResultAsync(session.Id, spend)).Should().Be(SpendInitiators.Human);
        spend.Records[0].ActorUserId.Should().Be(Участник);
    }

    // chats_send: senderUserId — id человека-инициатора (turnActor), но ход агентский.
    [Fact]
    public async Task Инициатор_ChatsSend_Agent_ДажеЕслиSenderUserIdЧеловека()
    {
        var (session, _) = await RunChatAsync("in-chatssend");
        var spend = AttachSpend();

        await _sut.SendMessageAndWaitAsync(session.Id, "от агента", TimeSpan.Zero, senderUserId: Участник);

        (await InitiatorOfNextResultAsync(session.Id, spend)).Should().Be(SpendInitiators.Agent);
    }

    // chats_send поверх armed-защёлки прошлого хода человека, не закрытого result'ом: подача
    // агента обязана перезаписать признак, а не унаследовать его.
    [Fact]
    public async Task Инициатор_ChatsSendПослеПодачиЧеловека_Agent()
    {
        var (session, fake) = await RunChatAsync("in-overwrite");
        var spend = AttachSpend();
        await _sut.SendMessageAsync(session.Id, "от человека", [], senderUserId: Участник);
        EndTurn(session, fake);

        await _sut.SendMessageAndWaitAsync(session.Id, "от агента", TimeSpan.Zero, senderUserId: Участник);

        (await InitiatorOfNextResultAsync(session.Id, spend)).Should().Be(SpendInitiators.Agent);
    }

    // Внутренние подачи: дренаж агентского, WorkLoop и nudge (systemDirective/auto), возобновление
    // после рестарта (auto), тихая подача suppressTasksExecute, агентская глубина — не человек.
    [Theory]
    [InlineData(true, false, false, 0)]   // auto: дренаж, возобновление
    [InlineData(false, true, false, 0)]   // systemDirective: WorkLoop, nudge
    [InlineData(false, false, true, 0)]   // suppressTasksExecute
    [InlineData(false, false, false, 1)]  // агентская глубина
    public async Task Инициатор_ВнутренниеПодачи_НеHuman(bool auto, bool directive, bool suppress, int depth)
    {
        var (session, _) = await RunChatAsync($"in-int-{auto}-{directive}-{suppress}-{depth}");
        var spend = AttachSpend();

        await _sut.SendMessageAsync(session.Id, "внутренняя", [], systemDirective: directive, auto: auto,
            suppressTasksExecute: suppress, agentDepth: depth, senderUserId: Участник);

        (await InitiatorOfNextResultAsync(session.Id, spend)).Should().Be(SpendInitiators.Agent);
    }

    // Очередь: сообщение человека (QueueUser) → human, агентское (QueueAgent) → agent, хотя
    // у обоих senderUserId — id человека. Идём настоящим путём: постановка → дренаж.
    [Theory]
    [InlineData(false, SpendInitiators.Human)]
    [InlineData(true, SpendInitiators.Agent)]
    public async Task Инициатор_ИзОчереди(bool agentic, string expected)
    {
        var (session, fake) = await RunChatAsync("in-queue-" + agentic);
        var spend = AttachSpend();
        await _sut.SendMessageAsync(session.Id, "первый ход", [], senderUserId: TestUserId);
        session.Status = SessionStatus.Working;
        if (agentic)
            await _sut.SendMessageAndWaitAsync(session.Id, "в очередь", TimeSpan.Zero, agentDepth: 1, senderUserId: Участник);
        else
            await _sut.SendMessageAsync(session.Id, "в очередь", [], senderUserId: Участник);
        _sut.GetVisiblePending(session.Id).Should().ContainSingle();
        EndTurn(session, fake);

        await DrainNextForRaceAsync(session.Id);

        (await InitiatorOfNextResultAsync(session.Id, spend)).Should().Be(expected);
    }

    // Защёлка живёт один ход: пока живы фоновые агенты, CLI начинает новые ходы без подачи, и
    // такой ход — агентский. Заодно повтор фолбэком: оркестратор держит result каждой попытки и
    // отдаёт ядру ОДИН финальный (FallbackLlmSessionAdapterTests: ContainSingle), поэтому
    // повторная подача защёлку не перевзводит — и не должна.
    [Fact]
    public async Task Инициатор_АвтономныйХодПослеХодаЧеловека_Agent_ЗащёлкаСнятаНаResult()
    {
        var (session, _) = await RunChatAsync("in-latch");
        var spend = AttachSpend();
        await _sut.SendMessageAsync(session.Id, "от человека", [], senderUserId: Участник);

        (await InitiatorOfNextResultAsync(session.Id, spend)).Should().Be(SpendInitiators.Human);
        (await InitiatorOfNextResultAsync(session.Id, spend)).Should().Be(SpendInitiators.Agent,
            "второй result без новой подачи — автономный ход, человеку не приписывается");
    }

    // Хвост ревью R-S-SPEND-ИНИЦ, п.3: осиротевший result доживающего прогона доезжает до ядра
    // через ветку turn=null адаптера (FallbackLlmSessionAdapter.cs:356 пропускает ResultMessage
    // при снятой оркестрации — вниз уходит с id мёртвого прогона, замыкающим ход он не является).
    // Между подачей человека и её result — лишний result чужого прогона: защёлку он не съедает,
    // запись хода остаётся human.
    [Fact]
    public async Task Инициатор_ЛишнийResultЧужогоПрогона_НеСнимаетЗащёлку()
    {
        var (session, _) = await RunChatAsync("in-foreign-run");
        var spend = AttachSpend();
        await _sut.SendMessageAsync(session.Id, "от человека", [], senderUserId: Участник);

        (await InitiatorOfNextResultAsync(session.Id, spend, runId: TestRunId + 1)).Should().NotBe(SpendInitiators.Human,
            "лишний result чужого прогона не получает защёлку идущего хода");
        (await InitiatorOfNextResultAsync(session.Id, spend)).Should().Be(SpendInitiators.Human,
            "защёлку снимает только result, закрывающий ход");
    }
}
