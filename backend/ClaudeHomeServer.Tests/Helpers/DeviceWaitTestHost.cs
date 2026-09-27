using System.Reflection;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Helpers;

// Общая сборка тестов фоновой работы при офлайн-устройстве (ADR-016, план §5): настоящие
// SessionManager, TaskManager и исполнитель задач поверх одного фейкового гейта онлайн-статуса.
// Поля с подчёркиванием — чтобы тела тестов читались как у обычной фикстуры.
public sealed class DeviceWaitTestHost : IDisposable
{
    public readonly string _dir;
    public readonly TaskManager _tasks;
    public readonly UserStore _userStore;
    public readonly ProjectManager _projects;
    public readonly SessionManager _sessions;
    public readonly NotificationStore _notifStore;
    public readonly NotificationService _notif;
    public readonly TaskExecutionService _sut;
    public readonly FakeProjectDeviceGate _gate = new();
    public readonly IProjectManager _projectSeam;
    public readonly PersonaManager _personas;
    public readonly AppSettingsService _appSettings;
    public readonly PushService _push;

    public DeviceWaitTestHost()
    {
        _dir = Path.Combine(Path.GetTempPath(), "task_device_wait_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["DefaultProjectsPath"] = Path.Combine(_dir, "homes"),
                ["ClaudeUserProfileDir"] = Path.Combine(_dir, "claude-profile"),
            })
            .Build();

        _userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _appSettings = new AppSettingsService(config);
        _projects = new ProjectManager(config, _userStore, _appSettings);
        _projectSeam = new ProjectManagerAdapter(_projects);
        _personas = new PersonaManager(config);
        _tasks = new TaskManager(config, personas: _personas);
        var broadcaster = new TestSessionBroadcaster();

        var pushStore = new PushSubscriptionStore(config);
        var jwt = new JwtService(config, _userStore, NullLogger<JwtService>.Instance);
        _push = new PushService(config, pushStore, jwt, NullLogger<PushService>.Instance);
        _notifStore = new NotificationStore(config, NullLogger<NotificationStore>.Instance);
        _notif = new NotificationService(_notifStore, broadcaster, _push, _personas, _projects,
            NullLogger<NotificationService>.Instance);

        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var notesSvc = new NotesService(_projects, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, _userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);

        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(wkStore), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var flags = new FeatureFlagService(_userStore);
        var bindings = new PersonaBindingsService(_personas, _projects, wkStore,
            knowledge, new SkillsService(), _userStore, config, NullLogger<PersonaBindingsService>.Instance,
            notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        _sessions = new SessionManager(_projects, new ChatHistoryService(config), config,
            adapters, falCost, usage, _appSettings, _userStore, jwt, server.Object, llmProviders,
            flags, _personas, bindings, subPool,
            NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox,
            broadcaster: broadcaster, deviceGate: _gate);

        _sut = new TaskExecutionService(_tasks, _sessions, _personas, broadcaster, _push, _notif,
            NullLogger<TaskExecutionService>.Instance, config, kb: notesKb,
            projects: _projectSeam, deviceGate: _gate);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        TestFs.DeleteDirectoryResilient(_dir);
    }

    public (User Owner, Project Project) LocalProject()
    {
        var owner = _userStore.Add("device-owner", "password123", "user");
        var project = _projects.CreateLocal("Ноутбук-проект", "/home/u/proj", owner.Id, "dev-1");
        return (owner, project);
    }

    public TaskItem ClaudeTask(User owner, Project project) =>
        _tasks.Create(project.Id, owner.Id, new CreateTaskRequest("Собрать релиз", Assignee: TaskItemAssignee.Claude));

    public async Task<IReadOnlyList<string>> NotificationTitlesAsync(string ownerId) =>
        (await _notifStore.GetListAsync(ownerId)).Select(n => n.Title).ToList();

    // Подставной процесс чата (реестр сессий приватный — white-box приём SessionManagerTests):
    // TCS разрешается текстом первого хода, дошедшего до «CLI»
    public TaskCompletionSource<string> StubProcess(Session chat)
    {
        var delivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var field = typeof(SessionManager).GetField("_sessions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var entry = ((System.Collections.IDictionary)field.GetValue(_sessions)!)[chat.Id]!;
        var adapter = new Mock<ILlmSessionAdapter>();
        adapter.SetupGet(a => a.Info).Returns(chat);
        adapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<int>(), It.IsAny<bool>()))
            .Callback<string, IReadOnlyList<string>, int, bool>((text, _, _, _) => delivered.TrySetResult(text))
            .Returns(Task.CompletedTask);
        entry.GetType().GetField("Process")!.SetValue(entry, adapter.Object);
        chat.Status = SessionStatus.Active;
        return delivered;
    }
}
