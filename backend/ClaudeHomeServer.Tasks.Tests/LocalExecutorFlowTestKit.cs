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

    // Шаг фичи персоны: running — исполнитель ведёт ход в своём чате; иначе незапущенный todo
    public TaskItem NewStep(TaskItem epic, string personaId, string stepRef, bool running = false,
        string role = TaskRoles.Step)
    {
        var t = Tasks.Create(null, Owner, new CreateTaskRequest("Шаг " + stepRef));
        t.FeatureId = epic.Id;
        t.Role = role;
        t.StepRef = stepRef;
        t.PersonaId = personaId;
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

    public sealed class FakeRoster : ILocalExecutorRoster
    {
        public List<LocalExecutor> Locals { get; } = [];
        public IReadOnlyList<LocalExecutor> For(string ownerId, string? projectId) => Locals.ToList();
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
