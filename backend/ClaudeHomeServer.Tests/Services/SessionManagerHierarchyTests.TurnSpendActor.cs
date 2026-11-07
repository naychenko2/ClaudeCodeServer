using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Sessions;
using ClaudeHomeServer.Services.Spend;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Блокер вердикта efa973f5: в бою ядро обязано передать реестр актора в учёт расхода хода
// (SessionManager.cs:10473 → SpendMapping.RecordTurnSpend). Запись строится пока ход жив —
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
}
