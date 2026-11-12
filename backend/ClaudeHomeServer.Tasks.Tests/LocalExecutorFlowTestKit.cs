using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tasks.Tests;

// Общая обвязка тестов счётчика локальных исполнителей и пинка простоя: TaskManager на временном
// каталоге, справочник сессий со статусами и подставные швы Core.
internal sealed class LocalExecutorFlowKit : IDisposable
{
    public const string Owner = "u";
    public const string KeyChat = "chat-key";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lef_" + Guid.NewGuid().ToString("N"));
    public TaskManager Tasks { get; }
    public FakeSessions Sessions { get; } = new();
    public FakeRoster Roster { get; } = new();
    public FakeSender Sender { get; } = new();

    public LocalExecutorFlowKit()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        Tasks = new TaskManager(config);
        Sessions.Add(KeyChat, SessionStatus.Active);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    // Открытая фича, ключевой чат — KeyChat
    public TaskItem NewFeature(string title)
    {
        var epic = Tasks.Create(null, Owner, new CreateTaskRequest(title, Role: TaskRoles.Feature));
        epic.ExecutorLaunchedBySessionId = KeyChat;
        return epic;
    }

    // Шаг фичи персоны: running — исполнитель ведёт ход в своём чате; иначе незапущенный todo.
    // modelTier — переопределение тира карточки (для проверки локальности эффективной модели).
    public TaskItem NewStep(TaskItem epic, string personaId, string stepRef, bool running = false,
        string role = TaskRoles.Step, ModelTier? modelTier = null)
    {
        var t = Tasks.Create(null, Owner, new CreateTaskRequest("Шаг " + stepRef));
        t.FeatureId = epic.Id;
        t.Role = role;
        t.StepRef = stepRef;
        t.PersonaId = personaId;
        if (modelTier is { } tier) t.ModelTier = tier;
        if (running)
        {
            var chat = $"exec-{stepRef}";
            Sessions.Add(chat, SessionStatus.Working);
            t.Status = TaskItemStatus.InProgress;
            t.LinkedSessionId = chat;
        }
        return t;
    }

    public sealed class FakeSessions : ISessionDirectory
    {
        private readonly Dictionary<string, Session> _byId = new();
        public Session Add(string id, SessionStatus status) =>
            _byId[id] = new Session { Id = id, Status = status };
        public Session? GetById(string id) => _byId.GetValueOrDefault(id);
        public IReadOnlyCollection<Session> GetAll() => _byId.Values;
        public string? ResolveOwnerId(Session s) => Owner;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
        public bool SetWikiMark(string sessionId, string? mark) => false;
    }

    // Подставной ростер. Локальность модели — подставная модель реального резолвера
    // (ModelAssignmentResolver в тесте нет): тир «weak» — локальный, без явного тира —
    // модель персоны из Locals; всё остальное — не локальное. LocalNonExecutors — локальные
    // персоны НЕ-исполнители (ревьюер на слабой модели): в For() не входят, их ход локальный.
    public sealed class FakeRoster : ILocalExecutorRoster
    {
        public List<LocalExecutor> Locals { get; } = [];
        public List<LocalExecutor> LocalNonExecutors { get; } = [];
        // Слоты движка; при пустом ростере шов отдаёт null, как боевой
        public LocalEngineSlots Slots { get; set; } = new(4, 0, 0);
        public IReadOnlyList<LocalExecutor> For(string ownerId, string? projectId) => Locals.ToList();
        public LocalEngineSlots? SlotsFor(string ownerId, string? projectId) => Locals.Count == 0 ? null : Slots;

        public LocalTaskTurn? LocalTurnOf(TaskItem task, string ownerId)
        {
            if (task.ModelTier is { } tier)
                return tier == ModelTier.Weak
                    ? new LocalTaskTurn(NameOf(task.PersonaId))
                    : null;
            var local = task.PersonaId is { } pid ? Locals.FirstOrDefault(l => l.PersonaId == pid) : null;
            return local is null ? null : new LocalTaskTurn(local.Name);
        }

        private string? NameOf(string? personaId) =>
            personaId is not { } pid
                ? null
                : Locals.Concat(LocalNonExecutors).FirstOrDefault(p => p.PersonaId == pid)?.Name;
    }

    public sealed class FakeSender : IKeyChatCardSender
    {
        public List<(string SessionId, string Text)> Sent { get; } = [];
        public bool Succeed { get; set; } = true;
        public Task<bool> TrySendAsync(string sessionId, string text)
        {
            if (Succeed) Sent.Add((sessionId, text));
            return Task.FromResult(Succeed);
        }
    }
}
