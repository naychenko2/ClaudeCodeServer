using ClaudeHomeServer.Core.Telemetry;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services.Memory;

/// <summary>
/// S2d-5b (разрез 4bc3d04f §1, путь 17): автообучение памяти персоны по приватному чату
/// не запускается вовсе — ни чтения истории, ни LLM. Ход приватного чата виден только
/// автору, а память проектной персоны подмешивается ко всем чатам проекта. Проверка —
/// в самом сервисе (OnSessionMessageAsync), как в TeamMemoryAutolearnService (S2d-5a).
///
/// Последние два теста — контроль non-vacuousness: в общем чате и вне проекта пайплайн
/// живой (LLM зовётся один раз). Без них первый тест зелёный и при сломанном сервисе.
///
/// Третий контроль (W4-A2, решение Андрея от 12.11): чат редактора общего проекта — тот же
/// живой пайплайн. Авто-память проектной персоны — поведение персоны, а не правка человеком:
/// она пополняется и из чатов редактора, а запись уходит под ключом арендатора (persona.OwnerId).
/// </summary>
public class PersonaMemoryAutolearnPrivateChatTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Editor = "editor-1";
    private const string ProjectId = "p-shared";
    private const string PersonaId = "persona-1";

    private static readonly ResultMessage Result = new("success", 100, 1, null, null);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "persona-autolearn-priv-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingObserver _observer = new();
    private readonly CountingCheap _cheap = new();
    private readonly StubSessions _sessions = new();
    private readonly Persona _persona = new() { Id = PersonaId, OwnerId = Owner, Name = "Тест" };
    private readonly PersonaMemoryService _memory;
    private readonly PersonaMemoryAutolearnService _sut;

    public PersonaMemoryAutolearnPrivateChatTests()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
        }).Build();

        var lookup = new Mock<IPersonaLookup>();
        lookup.Setup(l => l.GetByIdInternal(PersonaId)).Returns(_persona);

        var resolver = new Mock<IPersonaResolver>();
        resolver.Setup(r => r.Get(PersonaId, Owner)).Returns(_persona);

        _memory = new PersonaMemoryService(
            new Mock<IKnowledgeIndex>().Object, resolver.Object, lookup.Object,
            new Mock<IPersonaDirectory>().Object, new Mock<IPersonaEvents>().Object,
            new Mock<IDifyMetrics>().Object, new Mock<IUserStore>().Object, config,
            NullLogger<PersonaMemoryService>.Instance);
        var consolidation = new PersonaMemoryConsolidationService(
            _memory, resolver.Object, lookup.Object, _cheap, config,
            NullLogger<PersonaMemoryConsolidationService>.Instance);

        _sut = new PersonaMemoryAutolearnService(
            _sessions, _observer, lookup.Object, _memory, consolidation, _cheap, config,
            NullLogger<PersonaMemoryAutolearnService>.Instance);
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
        var session = new Session
        {
            Id = "s-private", ProjectId = ProjectId, PersonaId = PersonaId, PrivateToUserId = "author-1",
        };
        // содержательная история — как в общем контроле: без проверки приватности пайплайн
        // дошёл бы до LLM (проверено мутацией), поэтому нули ниже не тривиальны
        _sessions.History =
        [
            new StoredUserMessage(new string('а', 450)),
            new StoredTextMessage(new string('б', 300)),
        ];

        await _observer.Handler(session, Result);

        // даём фону шанс «догнать» (контроль «общий чат» ниже доказывает, что пайплайн быстрый)
        await Task.Delay(300);
        _sessions.HistoryCalls.Should().Be(0, "приватный чат отсекается ДО чтения истории");
        _cheap.Calls.Should().Be(0,
            "приватный чат не должен доходить ни до истории, ни до платного LLM");
        _memory.List(Owner, PersonaId, null).Should().BeEmpty();
    }

    [Fact]
    public async Task ПриватныйЧатСМусорнымPrivateToUserId_ТожеПропуск()
    {
        await _sut.StartAsync(CancellationToken.None);
        // Пустая строка — тот же fail-closed, что в SessionAccess.CanSee: приватность не читается
        var session = new Session
        {
            Id = "s-private2", ProjectId = ProjectId, PersonaId = PersonaId, PrivateToUserId = "",
        };
        _sessions.History =
        [
            new StoredUserMessage(new string('а', 450)),
            new StoredTextMessage(new string('б', 300)),
        ];

        await _observer.Handler(session, Result);

        await Task.Delay(300);
        _sessions.HistoryCalls.Should().Be(0);
        _cheap.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ОбщийЧат_АвтообучениеРаботаетКакРаньше_LlmОдинВызов()
    {
        await _sut.StartAsync(CancellationToken.None);
        var session = new Session { Id = "s-open", ProjectId = ProjectId, PersonaId = PersonaId };
        // содержательный ход: порог AutolearnGate.CheckContent (400) пройден
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

    [Fact]
    public async Task ЧатВнеПроекта_АвтообучениеРаботаетКакРаньше_LlmОдинВызов()
    {
        await _sut.StartAsync(CancellationToken.None);
        // Приватность — признак проектного чата (S2d-7a); вне проекта PrivateToUserId не
        // ставится и персонный чат (самый частый случай автообучения) не затронут
        var session = new Session { Id = "s-outside", PersonaId = PersonaId };
        _sessions.History =
        [
            new StoredUserMessage(new string('а', 450)),
            new StoredTextMessage(new string('б', 300)),
        ];

        await _observer.Handler(session, Result);

        var winner = await Task.WhenAny(_cheap.Done.Task, Task.Delay(5000));
        winner.Should().Be(_cheap.Done.Task, "персонный чат вне проекта — как раньше: доходит до LLM");
        _cheap.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ЧатРедактораОбщегоПроекта_АвтообучениеРаботает_ПамятьПодКлючомВладельца()
    {
        await _sut.StartAsync(CancellationToken.None);
        // Чат общего проекта мог создать редактор, а не владелец (W4). Авто-память персоны —
        // поведение персоны, а не правка человеком: она пополняется и из такого чата. Сервис
        // автора чата с владельцем проекта не сверяет вовсе: на путь влияют только приватность
        // (PrivateToUserId — здесь null) и гейт содержательности (AutolearnGate — пройден).
        // Проект в этом пути не читается: «общий» здесь выражен самим чатом — ProjectId +
        // автор-редактор; owner-ключ берётся из персоны (persona.OwnerId = арендатор).
        _cheap.Response = """{"items":[{"type":"semantic","text":"Пользователь ведёт волну 4.","salience":0.7}],"focus":null}""";
        var session = new Session
        {
            Id = "s-editor", ProjectId = ProjectId, PersonaId = PersonaId, CreatedByUserId = Editor,
        };
        _sessions.History =
        [
            new StoredUserMessage(new string('а', 450)),
            new StoredTextMessage(new string('б', 300)),
        ];

        await _observer.Handler(session, Result);

        var winner = await Task.WhenAny(_cheap.Done.Task, Task.Delay(5000));
        winner.Should().Be(_cheap.Done.Task,
            "чат редактора общего проекта не отсекается: пайплайн живой, как в общем чате");
        _cheap.Calls.Should().Be(1);

        // Запись памяти — уже ПОСЛЕ LLM в том же фоновом прогоне (LearnSafeAsync): ждём её
        // появления, а не проверяем сразу после вызова.
        var appeared = false;
        for (var i = 0; i < 100 && !appeared; i++)
        {
            appeared = _memory.List(Owner, PersonaId, null).Count > 0;
            if (!appeared) await Task.Delay(50);
        }
        // Ключ записи — persona.OwnerId (арендатор). Write-path (Remember/RememberAsync)
        // допускает запись только той персоны, которую видит этот пользователь
        // (_personas.Get(personaId, ownerId) is null → null): проключили бы автора чата —
        // редактора — записи не было бы вовсе, ни под Owner, ни под Editor.
        appeared.Should().BeTrue(
            "факт из чата редактора обязан попасть в память проектной персоны под ключом владельца проекта");
        var entry = _memory.List(Owner, PersonaId, null).Single();
        entry.SourceSessionId.Should().Be(session.Id, "запись пришла из этого чата редактора");
        _memory.List(Editor, PersonaId, null).Should().BeEmpty(
            "полка персоны принадлежит арендатору: под ключом редактора записи нет");
    }

    // --- Консультация persona_ask (S2d-5b): LearnFromConsultation ---

    private static readonly string LongQuestion = new('в', 450);
    private static readonly string LongAnswer = new('о', 300);

    [Fact]
    public async Task Консультация_ПриватныйВызыватель_ИзвлечениеНеЗапускается_LlmНоль()
    {
        _sut.LearnFromConsultation(_persona, LongQuestion, LongAnswer, callerIsPrivate: true);

        await Task.Delay(300);
        _cheap.Calls.Should().Be(0, "факты приватного чата не уходят в общую полку персоны");
        _memory.List(Owner, PersonaId, null).Should().BeEmpty();
    }

    [Fact]
    public async Task Консультация_ОбщийВызыватель_ИзвлечениеЗапускается_LlmОдинВызов()
    {
        _sut.LearnFromConsultation(_persona, LongQuestion, LongAnswer, callerIsPrivate: false);

        var winner = await Task.WhenAny(_cheap.Done.Task, Task.Delay(5000));
        winner.Should().Be(_cheap.Done.Task, "контроль: без гейта путь живой, нули выше не тривиальны");
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
        public bool SetWikiMark(string sessionId, string? mark) => false;
    }

    // Стаб раннера: «[]» — парсер читает как «нечего запоминать»; счётчик и сигнал вызова.
    // Response — ответ по умолчанию (ничего не запоминать); тесту, которому нужен разбор,
    // подменяется тело на валидный JSON формата {items, focus}.
    private sealed class CountingCheap : ICheapTextRunner
    {
        public int Calls;
        public string Response = "[]";
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
            return Task.FromResult(Response);
        }
        public Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
            object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            Done.TrySetResult();
            return Task.FromResult<string?>(Response);
        }
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null,
            object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            Done.TrySetResult();
            return Task.FromResult(new OneShotResult(Response, null, 0));
        }
    }
}
