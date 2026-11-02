using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// Единая точка правды «первое живое звено ключевой чата + переход по продолжениям»
// (FeatureKeyChain.FirstLive). Мутации (по одному тесту каждая, вывод — в отчёте задачи):
//  • убрать переход по ContinuedInSessionId — тесты «продолжение…» красные;
//  • убрать visited.Add (защита от цикла) — тест «цикл» уходит в MaxContinuationDepth и возвращает
//    другое звено — красный;
//  • убрать пропуск мёртвого звена (continue) — тест «первое звено мертво» красный.
public class FeatureKeyChainFirstLiveTests
{
    private static Func<string, FeatureKeyChain.KeyChatProbe?> Probe(Dictionary<string, string?> live) =>
        id => live.TryGetValue(id, out var cont) ? new FeatureKeyChain.KeyChatProbe(cont) : null;

    [Fact]
    public void ПервоеЗвеноЖивое_БезПродолжения_ОноСамо()
    {
        FeatureKeyChain.FirstLive(["a", "b"], Probe(new() { ["a"] = null, ["b"] = null })).Should().Be("a");
    }

    [Fact]
    public void ПервоеЗвеноМертво_БерётсяСледующееЖивое()
    {
        FeatureKeyChain.FirstLive(["a", "b"], Probe(new() { ["b"] = null })).Should().Be("b");
    }

    [Fact]
    public void Продолжение_ЖивоеПродолжение_ВместоИсходного()
    {
        FeatureKeyChain.FirstLive(["a"], Probe(new() { ["a"] = "a2", ["a2"] = null })).Should().Be("a2");
    }

    [Fact]
    public void Продолжение_ЦепочкаПродолжений_ПоследнееЖивое()
    {
        FeatureKeyChain.FirstLive(["a"], Probe(new() { ["a"] = "a2", ["a2"] = "a3", ["a3"] = null })).Should().Be("a3");
    }

    [Fact]
    public void Продолжение_Мертво_ОстаёмсяНаПоследнемЖивом()
    {
        FeatureKeyChain.FirstLive(["a"], Probe(new() { ["a"] = "ушло" })).Should().Be("a");
    }

    [Fact]
    public void Продолжение_Цикл_Прерывается()
    {
        FeatureKeyChain.FirstLive(["a"], Probe(new() { ["a"] = "b", ["b"] = "a" })).Should().Be("b",
            "цикл a→b→a останавливается на втором шаге, без бесконечного обхода");
    }

    [Fact]
    public void Продолжение_Глубже_Предела_ОстановкаНаПределе()
    {
        var live = new Dictionary<string, string?>();
        for (var i = 0; i < 20; i++) live[$"s{i}"] = $"s{i + 1}";
        live["s20"] = null;

        FeatureKeyChain.FirstLive(["s0"], Probe(live)).Should().Be($"s{FeatureKeyChain.MaxContinuationDepth}");
    }

    [Fact]
    public void ЖивыхЗвеньевНет_Null()
    {
        FeatureKeyChain.FirstLive(["a", "b"], Probe(new())).Should().BeNull();
        FeatureKeyChain.FirstLive([], Probe(new() { ["a"] = null })).Should().BeNull();
    }

    // KeySession проектора ходит через ту же функцию: продолжение из снимка сессий владельца
    [Fact]
    public void KeySession_ПереходитКПродолжениюИзСнимка()
    {
        var epic = new TaskItem { Id = "e", SourceSessionId = "a" };
        var sessions = new[]
        {
            new FeatureIndexSession("a", null, null, false, ContinuedInSessionId: "a2"),
            new FeatureIndexSession("a2", null, null, false),
        };

        FeatureProjector.KeySession(epic, [epic], sessions).Should().Be("a2");
    }

    [Fact]
    public void KeySession_ПовторIdВСнимке_НеПадает()
    {
        var epic = new TaskItem { Id = "e", SourceSessionId = "a" };
        var sessions = new[]
        {
            new FeatureIndexSession("a", null, null, false),
            new FeatureIndexSession("a", null, null, false),
        };

        FeatureProjector.KeySession(epic, [epic], sessions).Should().Be("a");
    }

    // Каталог сессий: чужой владелец продолжением не считается (предохранитель из постановки)
    [Fact]
    public void ПоСправочнику_ЧужоеПродолжение_НеБерётся()
    {
        var dir = new FakeDirectory(
            new Session { Id = "a", ContinuedInSessionId = "x" },
            new Session { Id = "x" });
        dir.OwnerOf["x"] = "чужой";

        FeatureKeyChain.FirstLive(["a"], dir, "u").Should().Be("a");
    }

    private sealed class FakeDirectory : ISessionDirectory
    {
        private readonly Dictionary<string, Session> _byId;
        public Dictionary<string, string> OwnerOf { get; } = new();
        public FakeDirectory(params Session[] sessions) => _byId = sessions.ToDictionary(s => s.Id);
        public Session? GetById(string id) => _byId.GetValueOrDefault(id);
        public IReadOnlyCollection<Session> GetAll() => _byId.Values;
        public string? ResolveOwnerId(Session s) => OwnerOf.GetValueOrDefault(s.Id, "u");
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
        public bool SetWikiMark(string sessionId, string? mark) => false;
    }
}
