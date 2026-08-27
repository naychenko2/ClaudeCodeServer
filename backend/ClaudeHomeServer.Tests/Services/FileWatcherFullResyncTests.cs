using System.Text.Json;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Полная пересинхронизация filesChanged: переполнение MaxPaths и пересоздание watcher'а
/// обязаны слать full=true (раньше всё сверх лимита молча выбрасывалось, а события за время
/// сбоя watcher'а не компенсировались — список файлов в UI устаревал до ручного обновления).
/// Юнит-набор: сервис в изоляции, хаб — Moq-рекордер (по образцу ChatArchivedEventTests);
/// переполнение проверяется в polling-режиме — он детерминированнее FileSystemWatcher в CI.
/// </summary>
public class FileWatcherFullResyncTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "ccs_fwt_" + Guid.NewGuid().ToString("N"));
    private readonly List<FileWatcherService> _services = [];

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* уборка temp — не предмет теста */ }
    }

    // Мок хаба: Group(name) запоминает адресата, SendCoreAsync — группу + сериализованный
    // payload события. OnSent — подписка для ожидания через TaskCompletionSource.
    private sealed class HubRecorder
    {
        private readonly object _gate = new();
        private readonly List<Action<string, JsonElement>> _subscribers = [];

        public IHubContext<SessionHub> Context { get; }

        public HubRecorder()
        {
            string? currentGroup = null;
            var proxy = new Mock<IClientProxy>();
            proxy
                .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Callback<string, object[], CancellationToken>((_, args, _) =>
                {
                    var payload = JsonSerializer.SerializeToElement(args[0]);
                    lock (_gate)
                    {
                        foreach (var s in _subscribers) s(currentGroup!, payload);
                    }
                })
                .Returns(Task.CompletedTask);
            var clients = new Mock<IHubClients>();
            clients.Setup(c => c.Group(It.IsAny<string>()))
                .Callback<string>(g => currentGroup = g)
                .Returns(proxy.Object);
            var hub = new Mock<IHubContext<SessionHub>>();
            hub.Setup(h => h.Clients).Returns(clients.Object);
            Context = hub.Object;
        }

        public void OnSent(string group, Action<JsonElement> handler)
        {
            lock (_gate) _subscribers.Add((g, p) => { if (g == group) handler(p); });
        }
    }

    private (FileWatcherService Svc, string ProjectId, string Dir) Build(HubRecorder hub, bool usePolling, int pollMs)
    {
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["FileWatcher:UsePolling"] = usePolling ? "true" : "false",
            ["FileWatcher:PollIntervalMs"] = pollMs.ToString(),
        }).Build();

        var userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var owner = userStore.Add("fwt-owner-" + Guid.NewGuid().ToString("N")[..8], "pw-123456", "user");
        var projects = new ProjectManager(config, userStore, new AppSettingsService(config));

        var dir = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var project = projects.Create("fwt-" + Guid.NewGuid().ToString("N")[..8], dir, owner.Id, owner.Username);

        // Dify не настроен → KnowledgeService.IsConfigured=false → QueueSync — тихий no-op:
        // тест проверяет контракт события, а не синк знаний
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Options.Create(new DifyOptions()), wkStore);
        var knowledgeSync = new ProjectKnowledgeSyncService(knowledge, wkStore, projects,
            new FileService(), hub.Context, NullLogger<ProjectKnowledgeSyncService>.Instance);
        var graphs = new CodeGraphService(NullLogger<CodeGraphService>.Instance, projects,
            new GraphPersistence(_tempDir, NullLogger<GraphPersistence>.Instance), config);

        var svc = new FileWatcherService(projects, hub.Context, knowledgeSync, graphs, config);
        _services.Add(svc);
        return (svc, project.Id, dir);
    }

    [Fact]
    public async Task ПереполнениеЛимитаПутей_ШлётFullВместоТихойРезки()
    {
        var hub = new HubRecorder();
        // Интервал 5с: все файлы пишутся ДО первого скана — дифф накапливается одним
        // заходом, и переполнение срабатывает атомарно, без partial-флашей с full=false
        var (svc, projectId, dir) = Build(hub, usePolling: true, pollMs: 5000);

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.OnSent("project_" + projectId, p =>
        {
            if (p.GetProperty("full").GetBoolean()) tcs.TrySetResult(p);
        });

        svc.Watch(projectId, "conn-overflow");

        for (var i = 0; i < 205; i++)
            File.WriteAllText(Path.Combine(dir, $"file{i}.txt"), "x");

        var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        done.Should().Be(tcs.Task,
            "переполнение MaxPaths обязано дать событие full=true — раньше всё сверх лимита молча выбрасывалось");

        var payload = await tcs.Task;
        payload.GetProperty("projectId").GetString().Should().Be(projectId);
        payload.GetProperty("full").GetBoolean().Should().BeTrue();
        payload.GetProperty("paths").GetArrayLength().Should().Be(0,
            "при full пути не слаим — клиент перезагружает всё раскрытое");
    }

    [Fact]
    public async Task ПересозданиеВатчера_ШлётFullПересинхронизацию()
    {
        var hub = new HubRecorder();
        var (svc, projectId, _) = Build(hub, usePolling: false, pollMs: 0);

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.OnSent("project_" + projectId, p =>
        {
            if (p.GetProperty("full").GetBoolean()) tcs.TrySetResult(p);
        });

        svc.Watch(projectId, "conn-recreate");
        // internal-обёртка (InternalsVisibleTo): путь Error → RecreateWatcher без реального сбоя ФС
        svc.RecreateWatcher(projectId);

        var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        done.Should().Be(tcs.Task,
            "пересоздание watcher'а обязано компенсировать потерянные за время сбоя события сигналом full");

        var payload = await tcs.Task;
        payload.GetProperty("projectId").GetString().Should().Be(projectId);
        payload.GetProperty("full").GetBoolean().Should().BeTrue();
        payload.GetProperty("paths").GetArrayLength().Should().Be(0,
            "какие пути потеряны за время сбоя — неизвестно, клиенту остаётся полная пересинхронизация");
    }
}
