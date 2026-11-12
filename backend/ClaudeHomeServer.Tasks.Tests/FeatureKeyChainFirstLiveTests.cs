using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// Единая точка правды «первое живое звено ключевой чата» (FeatureKeyChain.FirstLive).
// Мутации (по одному тесту каждая, вывод — в отчёте задачи):
//  • не проверять живость (вернуть первое звено цепочки) — тест «первое звено мертво» красный;
//  • брать последнее живое вместо первого (Last) — тест «первое звено живое» красный.
public class FeatureKeyChainFirstLiveTests
{
    private static Func<string, bool> Live(params string[] ids) => new HashSet<string>(ids).Contains;

    [Fact]
    public void ПервоеЗвеноЖивое_БерётсяОно()
    {
        FeatureKeyChain.FirstLive(["a", "b"], Live("a", "b")).Should().Be("a");
    }

    [Fact]
    public void ПервоеЗвеноМертво_БерётсяСледующееЖивое()
    {
        FeatureKeyChain.FirstLive(["a", "b"], Live("b")).Should().Be("b");
    }

    [Fact]
    public void ЖивыхЗвеньевНет_Null()
    {
        FeatureKeyChain.FirstLive(["a", "b"], Live()).Should().BeNull();
        FeatureKeyChain.FirstLive([], Live("a")).Should().BeNull();
    }

    // Порядок звеньев: запускающий дочерних → чат создания эпика → запустивший эпик.
    // Мутация: вернуть SourceSessionId первым звеном — тест краснеет.
    [Fact]
    public void Of_ПорядокЗвеньев_ЗапускающийПотомСоздательЭпикаПотомЗапустившийЭпик()
    {
        var epic = new TaskItem { Id = "e", SourceSessionId = "architect", ExecutorLaunchedBySessionId = "launcher-epic" };
        var s1 = new TaskItem { Id = "s1", ExecutorLaunchedBySessionId = "orchestrator" };
        var s2 = new TaskItem { Id = "s2", ExecutorLaunchedBySessionId = "orchestrator" };
        var s3 = new TaskItem { Id = "s3", ExecutorLaunchedBySessionId = "other" };

        FeatureKeyChain.Of(epic, [epic, s1, s2, s3])
            .Should().Equal("orchestrator", "architect", "launcher-epic");
    }

    // Эпик заведён в чате A, два шага запущены из B, третий — человеком из UI (без запускающего):
    // ключевой чат — B для всех, включая третий. Мутация: SourceSessionId первым → тест краснеет.
    [Fact]
    public void KeySession_ЭпикВA_ШагиИзB_КлючевойЧатB()
    {
        var epic = new TaskItem { Id = "e", SourceSessionId = "A" };
        var s1 = new TaskItem { Id = "s1", ExecutorLaunchedBySessionId = "B" };
        var s2 = new TaskItem { Id = "s2", ExecutorLaunchedBySessionId = "B" };
        var s3 = new TaskItem { Id = "s3" };
        var sessions = new[]
        {
            new FeatureIndexSession("A", null, null, false),
            new FeatureIndexSession("B", null, null, false),
        };

        FeatureProjector.KeySession(epic, [epic, s1, s2, s3], sessions).Should().Be("B");
    }

    // Запущенных дочерних нет — ключевой чат как раньше: чат создания эпика
    [Fact]
    public void KeySession_НетЗапущенныхДочерних_ЧатСозданияЭпика()
    {
        var epic = new TaskItem { Id = "e", SourceSessionId = "A" };
        var step = new TaskItem { Id = "s1" };
        var sessions = new[] { new FeatureIndexSession("A", null, null, false) };

        FeatureProjector.KeySession(epic, [epic, step], sessions).Should().Be("A");
    }

    [Fact]
    public void Of_ДублиСхлопываются_ПустыеЗвеньяПропускаются()
    {
        var epic = new TaskItem { Id = "e", SourceSessionId = "orchestrator", ExecutorLaunchedBySessionId = "orchestrator" };
        var step = new TaskItem { Id = "s", ExecutorLaunchedBySessionId = "orchestrator" };

        FeatureKeyChain.Of(epic, [epic, step]).Should().Equal("orchestrator");
        FeatureKeyChain.Of(new TaskItem { Id = "e" }, []).Should().BeEmpty();
    }

    [Fact]
    public void Of_РавенствоЧастот_МеньшийId()
    {
        var epic = new TaskItem { Id = "e", SourceSessionId = "z" };
        var a = new TaskItem { Id = "a", ExecutorLaunchedBySessionId = "b-chat" };
        var b = new TaskItem { Id = "b", ExecutorLaunchedBySessionId = "a-chat" };

        FeatureKeyChain.Of(epic, [epic, a, b]).Should().Equal("a-chat", "z");
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

    // Каталог сессий: чужое звено пропускается, отсутствующего в справочнике нет (предохранитель
    // из постановки)
    [Fact]
    public void ПоСправочнику_ЧужоеЗвено_Пропускается()
    {
        var dir = new FakeDirectory(
            new Session { Id = "a" },
            new Session { Id = "x" });
        dir.OwnerOf["x"] = "чужой";

        FeatureKeyChain.FirstLive(["x", "a"], dir, "u").Should().Be("a");
        FeatureKeyChain.FirstLive(["x", "нет"], dir, "u").Should().BeNull();
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
