using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services.Memory;

/// <summary>
/// S2d-5a (разрез 4bc3d04f §1 «S2d-5a», путь 17): автообучение памяти команды по приватному
/// чату не запускается вовсе — ни чтения истории, ни LLM. Ход приватного чата виден только
/// автору, а извлеченное знание ушло бы в общую память команды — всем участникам проекта.
/// Проверка — в самом сервисе (OnSessionMessageAsync), а не в AutolearnGate: автообучение
/// персон закрывает тот же признак своим шагом S2d-5b.
///
/// Второй тест — контроль non-vacuousness: в общем чате пайплайн живой (LLM зовётся один раз).
/// Без него первый тест зелёный и при сломанном сервисе.
/// </summary>
public class TeamMemoryAutolearnPrivateChatTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string ProjectId = "p-shared";

    private static readonly ResultMessage Result = new("success", 100, 1, null, null);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "team-autolearn-priv-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingObserver _observer = new();
    private readonly CountingCheap _cheap = new();
    private readonly StubSessions _sessions = new();
    private readonly TeamMemoryService _memory;
    private readonly TeamMemoryAutolearnService _sut;

    public TeamMemoryAutolearnPrivateChatTests()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
        }).Build();

        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById(ProjectId)).Returns(new Project { Id = ProjectId, OwnerId = Owner });

        _memory = new TeamMemoryService(config, cheap: _cheap);
        var consolidation = new TeamMemoryConsolidationService(_memory, _cheap, config,
            NullLogger<TeamMemoryConsolidationService>.Instance);

        _sut = new TeamMemoryAutolearnService(
            _sessions, _observer, projects.Object, _memory, consolidation, _cheap, config,
            NullLogger<TeamMemoryAutolearnService>.Instance, new Mock<ISessionBroadcaster>().Object);
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* чужой держатель */ }
    }

    [Fact]
    public async Task ПриватныйЧат_АвтообучениеНеЗапускается_ИсторияILlmНоль()
    {
        await _sut.StartAsync(CancellationToken.None);
        var session = new Session { Id = "s-private", ProjectId = ProjectId, PrivateToUserId = "author-1" };
        // содержательная история — как в общем контроле: без проверки приватности пайплайн
        // дошёл бы до LLM (проверено мутацией), поэтому нули ниже не тривиальны
        _sessions.History =
        [
            new StoredUserMessage(new string('а', 450)),
            new StoredTextMessage(new string('б', 300)),
        ];

        await _observer.Handler(session, Result);

        // даём фону шанс «догнать» (контроль «общий чат» ниже доказывает, что пайплайн быстрый)
        var winner = await Task.WhenAny(_cheap.Done.Task, Task.Delay(300));
        winner.Should().NotBe(_cheap.Done.Task, "приватный чат не должен дойти до LLM");
        _sessions.HistoryCalls.Should().Be(0, "приватный чат отсекается ДО чтения истории");
        _cheap.Calls.Should().Be(0,
            "приватный чат не должен доходить ни до истории, ни до платного LLM");
        _memory.List(Owner, ProjectId).Should().BeEmpty();
    }

    [Fact]
    public async Task ПриватныйЧатСМусорнымPrivateToUserId_ТожеПропуск()
    {
        await _sut.StartAsync(CancellationToken.None);
        // Пустая строка — тот же fail-closed, что в SessionAccess.CanSee: приватность не читается
        var session = new Session { Id = "s-private2", ProjectId = ProjectId, PrivateToUserId = "" };
        _sessions.History =
        [
            new StoredUserMessage(new string('а', 450)),
            new StoredTextMessage(new string('б', 300)),
        ];

        await _observer.Handler(session, Result);

        var winner = await Task.WhenAny(_cheap.Done.Task, Task.Delay(300));
        winner.Should().NotBe(_cheap.Done.Task, "приватный чат не должен дойти до LLM");
        _sessions.HistoryCalls.Should().Be(0);
        _cheap.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ОбщийЧат_АвтообучениеРаботаетКакРаньше_LlmОдинВызов()
    {
        await _sut.StartAsync(CancellationToken.None);
        var session = new Session { Id = "s-open", ProjectId = ProjectId };
        // содержательный ход: порог AutolearnGate.CheckContent (400) и анти-спам роста (600) пройдены
        _sessions.History =
        [
            new StoredUserMessage(new string('а', 450)),
            new StoredTextMessage(new string('б', 300)),
        ];

        await _observer.Handler(session, Result);

        var winner = await Task.WhenAny(_cheap.Done.Task, Task.Delay(5000));
        winner.Should().Be(_cheap.Done.Task,
            "в общем чате пайплайн живой: извлечение обязано дойти до LLM — иначе тест приватного "
            + "чата зелёный при сломанном сервисе");
        _cheap.Calls.Should().Be(1);
    }

    // --- Стабы ---

    private sealed class CapturingObserver : ISessionMessageObserver
    {
        public Func<Session, ServerMessage, Task> Handler = (_, _) => Task.CompletedTask;
        public void Attach(Func<Session, ServerMessage, Task> handler) => Handler = handler;
        public void Detach(Func<Session, ServerMessage, Task> handler) { }
    }

    private sealed class StubSessions : ISessionDirectory
    {
        public int HistoryCalls;
        public IReadOnlyList<StoredMessage> History = [];
        public Session? GetById(string id) => null;
        public IReadOnlyCollection<Session> GetAll() => [];
        public string? ResolveOwnerId(Session s) => Owner;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId)
        {
            HistoryCalls++;
            return Task.FromResult(History);
        }
    }

    // Стаб раннера: «[]» — парсер читает как «нечего запоминать»; счётчик и сигнал вызова.
    private sealed class CountingCheap : ICheapTextRunner
    {
        public int Calls;
        public readonly TaskCompletionSource Done = new();
        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public bool HasRoute(string actionKey) => true;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            Done.TrySetResult();
            return Task.FromResult("[]");
        }
        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            Done.TrySetResult();
            return Task.FromResult<string?>("[]");
        }
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null,
            object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            Done.TrySetResult();
            return Task.FromResult(new OneShotResult("[]", null, 0));
        }
    }
}
