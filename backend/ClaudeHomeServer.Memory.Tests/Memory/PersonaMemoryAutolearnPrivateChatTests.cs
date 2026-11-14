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

    // Пределы ожидания: отрицательная проверка — «сигнал не появился в окне» (пайплайн быстрый,
    // это доказывает положительный контроль «общий чат»); положительная — «сигнал появился»,
    // предел нужен только чтобы при мёртвом пайплайне тест был красный, а не висел.
    private static readonly TimeSpan NegativeWait = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan PositiveWait = TimeSpan.FromSeconds(5);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "persona-autolearn-priv-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingObserver _observer = new();
    private readonly CountingCheap _cheap = new();
    private readonly StubSessions _sessions = new();
    // Проектная персона: сценарий теста — общий проект, и глобальная персона не может быть
    // собеседником его чата (global_persona_in_shared, см. PersonaMemoryAutolearnService:75).
    // Для проектной персоны автообучение на успешной записи отдаёт событие журнала
    // MemoryLearned (PersonaMemoryAutolearnService:152-159) — на нём дублёр поднимает сигнал.
    private readonly Persona _persona = new()
    {
        Id = PersonaId, OwnerId = Owner, Name = "Тест",
        Scope = PersonaScope.Project, ProjectId = ProjectId,
    };
    private readonly PersonaMemoryService _memory;
    private readonly SignalledEventLog _events = new();
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
            NullLogger<PersonaMemoryAutolearnService>.Instance, _events);
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

        // ожидание сигнала первого шага пайплайна (чтение истории): он не должен появиться
        // (контроль «общий чат» ниже доказывает, что пайплайн быстрый)
        await AwaitNoSignal(_sessions.HistoryRead.Task, NegativeWait,
            "приватный чат не должен дойти до чтения истории");
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

        await AwaitNoSignal(_sessions.HistoryRead.Task, NegativeWait,
            "пустой PrivateToUserId — тот же fail-closed: до чтения истории дело не доходит");
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

        // сигнал — сам вызов LLM; при мёртвом пайплайне — TimeoutException. Контроль
        // non-vacuousness: без него тест приватного чата зелёный и при сломанном сервисе
        await _cheap.Done.Task.WaitAsync(PositiveWait);
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

        // сигнал — сам вызов LLM: приватность вне проекта не читается, пайплайн как раньше
        await _cheap.Done.Task.WaitAsync(PositiveWait);
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

        // сигнал — сам вызов LLM: чат редактора не отсекается, пайплайн живой как в общем чате
        await _cheap.Done.Task.WaitAsync(PositiveWait);
        _cheap.Calls.Should().Be(1);

        // Запись памяти — уже ПОСЛЕ LLM в том же фоновом прогоне (LearnSafeAsync). Сигнал на ней —
        // событие журнала MemoryLearned: сервис отдаёт его только после успешной записи и только
        // проектной персоны (PersonaMemoryAutolearnService:152-159). Не дошла бы запись до стора —
        // сигнала нет, тест красный по таймауту ожидания, а не висит.
        await _events.MemoryLearned.Task.WaitAsync(PositiveWait);
        var entry = _memory.List(Owner, PersonaId, null).Single();
        entry.SourceSessionId.Should().Be(session.Id, "запись пришла из этого чата редактора");
        // Ключ записи — persona.OwnerId: write-path (Remember:156) допускает запись только той
        // персоны, которую видит этот пользователь (_personas.Get(personaId, ownerId) is null →
        // null). Отдельного ассерта «под ключом Editor записи нет» здесь нет: resolver-мок отдаёт
        // персону только Owner'у (ctor), и запись под ключом Editor отсёк бы тот же гейт — такой
        // ассерт был бы верен по построению мока и не краснел бы ни при какой правке сервиса.
    }

    // --- Консультация persona_ask (S2d-5b): LearnFromConsultation ---

    private static readonly string LongQuestion = new('в', 450);
    private static readonly string LongAnswer = new('о', 300);

    [Fact]
    public async Task Консультация_ПриватныйВызыватель_ИзвлечениеНеЗапускается_LlmНоль()
    {
        _sut.LearnFromConsultation(_persona, LongQuestion, LongAnswer, callerIsPrivate: true);

        await AwaitNoSignal(_cheap.Done.Task, NegativeWait,
            "факты приватного чата не должны дойти до LLM");
        _cheap.Calls.Should().Be(0, "факты приватного чата не уходят в общую полку персоны");
        _memory.List(Owner, PersonaId, null).Should().BeEmpty();
    }

    [Fact]
    public async Task Консультация_ОбщийВызыватель_ИзвлечениеЗапускается_LlmОдинВызов()
    {
        _sut.LearnFromConsultation(_persona, LongQuestion, LongAnswer, callerIsPrivate: false);

        // сигнал — сам вызов LLM: контроль, без гейта путь живой, нули выше не тривиальны
        await _cheap.Done.Task.WaitAsync(PositiveWait);
        _cheap.Calls.Should().Be(1);
    }

    // Ожидание БЕЗ сигнала: сигнал появился раньше предела — тест красный с причиной; не появился —
    // ожидание просто вернулась, дальше проверяются счётчики.
    private static async Task AwaitNoSignal(Task signal, TimeSpan timeout, string because)
    {
        try
        {
            await signal.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            return;
        }
        Assert.Fail($"сигнал появился раньше предела {timeout.TotalMilliseconds} мс: {because}");
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
        // сигнал первого шага пайплайна (чтение истории): на его отсутствии стоят отрицательные тесты
        public readonly TaskCompletionSource HistoryRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<StoredMessage> History = [];
        public Session? GetById(string id) => null;
        public IReadOnlyCollection<Session> GetAll() => [];
        public string? ResolveOwnerId(Session s) => Owner;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId)
        {
            HistoryCalls++;
            HistoryRead.TrySetResult();
            return Task.FromResult(History);
        }
        public bool SetWikiMark(string sessionId, string? mark) => false;
    }

    // Дублёр журнала: поднимает сигнал на событии MemoryLearned — сервис отдаёт его только после
    // успешной записи памяти и только проектной персоны (PersonaMemoryAutolearnService:152-159).
    private sealed class SignalledEventLog : IProjectEventLogService
    {
        public readonly TaskCompletionSource MemoryLearned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProjectEvent? Append(string projectId, string ownerId, string type, string actor,
            string summary, string? entityRef = null, string? actorUserId = null)
        {
            if (type == ProjectEventTypes.MemoryLearned) MemoryLearned.TrySetResult();
            return null;
        }
    }

    // Стаб раннера: «[]» — парсер читает как «нечего запоминать»; счётчик и сигнал вызова.
    // Response — ответ по умолчанию (ничего не запоминать); тесту, которому нужен разбор,
    // подменяется тело на валидный JSON формата {items, focus}.
    private sealed class CountingCheap : ICheapTextRunner
    {
        public int Calls;
        public string Response = "[]";
        // RunContinuationsAsynchronously: продолжение await (теста) не должно выполняться на треде,
        // выставившем сигнал (фоновый тред сервиса)
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
