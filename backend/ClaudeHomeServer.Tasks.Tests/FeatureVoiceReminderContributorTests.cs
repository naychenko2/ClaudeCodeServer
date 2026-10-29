using Microsoft.Extensions.DependencyInjection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Projector;
using ClaudeHomeServer.Services.Turn;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tasks.Tests;

// Волна 6, B2: напоминание оркестратору об устаревшем голосе фичи — хвостовая секция хода.
//
// Мутации (проверены прогоном, см. отчёт задачи):
//  • убрать InTurnTail — тест «хвост хода» красный (секция уехала бы в системный блок);
//  • не сверять сессию с ключевым чатом (убрать `!= sessionId`) — тест «чужой чат» красный;
//  • не проверять Stale — тест «свежий голос» красный; не проверять Note is null — тест «нет голоса» красный;
//  • убрать отсев закрытых — тест «закрытая фича» красный.
public class FeatureVoiceReminderContributorTests : IDisposable
{
    private const string Owner = "u";
    private const string KeyChat = "chat-key";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vr_" + Guid.NewGuid().ToString("N"));
    private readonly TaskManager _tasks;
    private readonly FeatureVoiceReminderContributor _sut;

    private sealed class Sessions(params string[] ids) : ISessionDirectory
    {
        public Session? GetById(string id) => ids.Contains(id) ? new Session { Id = id } : null;
        public IReadOnlyCollection<Session> GetAll() => ids.Select(i => new Session { Id = i }).ToList();
        public string? ResolveOwnerId(Session s) => Owner;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>([]);
    }

    public FeatureVoiceReminderContributorTests()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _tasks = new TaskManager(config);
        _sut = new FeatureVoiceReminderContributor(_tasks, new FeatureProjector(_tasks),
            new ServiceCollection().AddSingleton<ISessionDirectory>(new Sessions(KeyChat, "chat-other")).BuildServiceProvider());
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private TaskItem NewFeature(string title = "Сводка фичи")
    {
        var epic = _tasks.Create(null, Owner, new CreateTaskRequest(title, Role: TaskRoles.Feature));
        epic.ExecutorLaunchedBySessionId = KeyChat;
        return epic;
    }

    private async Task<PromptSection?> Build(string sessionId)
    {
        var ctx = new PromptSessionContext(new Session { Id = sessionId }, Owner, null, "/root");
        var result = await _sut.BuildAsync(ctx, null);
        return result?.Sections.SingleOrDefault();
    }

    [Fact]
    public async Task КлючевойЧатБезГолоса_НапоминаетЗаписать_ХвостомХода()
    {
        NewFeature();

        var section = await Build(KeyChat);

        section.Should().NotBeNull();
        section!.InTurnTail.Should().BeTrue("секция в системном блоке обнулила бы prefix cache");
        section.Text.Should().Contain("«Сводка фичи»").And.Contain("featureNote").And.Contain("featureEstimate");
    }

    [Fact]
    public async Task УстаревшийГолос_НапоминаетОбновить()
    {
        var epic = NewFeature();
        // Стадия записи не совпадает с активной — голос устарел (StaleOf)
        _tasks.SetFeatureNote(epic.Id, "было ревью", KeyChat, byUser: false, "review", "running");

        var section = await Build(KeyChat);

        section!.Text.Should().Contain("«Сводка фичи» устарел").And.Contain("этап сменился");
    }

    [Fact]
    public async Task СвежийГолос_Молчит()
    {
        var epic = NewFeature();
        var run = new FeatureProjector(_tasks).Project(epic.Id, Owner)!;
        _tasks.SetFeatureNote(epic.Id, "всё идёт", KeyChat, byUser: false,
            FeatureBrief.ActiveStageOf(run), FeatureBrief.KindAtWriteOf(run));

        (await Build(KeyChat)).Should().BeNull();
    }

    [Fact]
    public async Task ЧужойЧат_Молчит()
    {
        NewFeature();

        (await Build("chat-other")).Should().BeNull("напоминание только в ключевом чате оркестратора");
    }

    [Fact]
    public async Task ЗакрытаяФича_Молчит()
    {
        var epic = NewFeature();
        epic.Status = TaskItemStatus.Done;

        (await Build(KeyChat)).Should().BeNull();
    }
}
