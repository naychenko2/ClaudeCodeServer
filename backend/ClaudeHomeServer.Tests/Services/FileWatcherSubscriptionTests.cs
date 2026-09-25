using System.Text.Json;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using ClaudeHomeServer.Services.Files;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Бюджет inotify-слежек ДОЛГОЖИВУЩИХ наблюдателей (разбор OOM/Linux 2026-09-22). В отличие от
/// ватчера хода, эти живут по одному на открытый проект и на отдельное дерево чата (до 30 минут
/// простоя), поэтому у владельца с двумя десятками проектов именно они и составляли постоянный
/// расход: FileSystemWatcher с IncludeSubdirectories ставил слежку на КАЖДЫЙ каталог дерева
/// (16 390 на этом репозитории), а TreeExcludes резал только события.
/// Замер идёт на настоящем дереве репозитория: синтетическая папка в temp такой формы
/// (двадцать .csproj с bin/obj, node_modules, .git) не воспроизводит.
/// </summary>
[Collection(TestCollections.Inotify)]
public class FileWatcherSubscriptionTests : IDisposable
{
    // Потолок задачи: один открытый проект на этом репозитории — меньше 1000 слежек.
    private const int WatchBudget = 1000;

    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "ccs_fws_" + Guid.NewGuid().ToString("N"));
    private readonly List<FileWatcherService> _services = [];

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* уборка temp — не предмет теста */ }
    }

    // Корень репозитория от каталога сборки тестов вверх до папки с .git и backend/.
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                && Directory.Exists(Path.Combine(dir.FullName, "backend"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    // Сервис с проектом на заданном корне. sentPaths — что ушло в группу проекта событием
    // filesChanged (Moq-рекордер хаба: сериализованный payload).
    private (FileWatcherService Svc, string ProjectId) Build(string root, List<JsonElement> sent)
    {
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["FileWatcher:UsePolling"] = "false",
        }).Build();

        var userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var owner = userStore.Add("fws-" + Guid.NewGuid().ToString("N")[..8], "pw-123456", "user");
        var projects = new ProjectManager(config, userStore, new AppSettingsService(config));
        var project = projects.Create("fws-" + Guid.NewGuid().ToString("N")[..8], root, owner.Id, owner.Username);

        // Dify не настроен → QueueSync — тихий no-op: тест про подписку и события, не про синк.
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Options.Create(new DifyOptions()), wkStore);
        var knowledgeSync = new ProjectKnowledgeSyncService(knowledge, wkStore, projects,
            new ProjectFileGateway(new FileService()), new RecordingHubNotifier(), new NullDifyMetrics(),
            NullLogger<ProjectKnowledgeSyncService>.Instance);
        var graphs = new CodeGraphService(NullLogger<CodeGraphService>.Instance, new ProjectRootLookup(projects),
            new GraphPersistence(_tempDir, NullLogger<GraphPersistence>.Instance), config);

        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, args, _) =>
            {
                lock (sent) sent.Add(JsonSerializer.SerializeToElement(args[0]));
            })
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var svc = new FileWatcherService(projects, hub.Object, knowledgeSync, graphs, config);
        _services.Add(svc);
        return (svc, project.Id);
    }

    [SkippableFact]
    public void ОткрытыйПроектНаРепозитории_НеВыедаетБюджетСлежек()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "inotify и /proc/self/fdinfo — только Linux");
        var root = FindRepoRoot();
        Skip.If(root is null, "тест идёт на настоящем дереве репозитория");

        var (svc, projectId) = Build(root!, []);
        var watchesBefore = InotifyProbe.CountWatches();
        var instancesBefore = InotifyProbe.CountInotifyFds();

        svc.Watch(projectId, "conn-budget").Should().BeTrue("наблюдатель поднят впервые");
        var own = InotifyProbe.CountWatches() - watchesBefore;
        var ownInstances = InotifyProbe.CountInotifyFds() - instancesBefore;
        svc.Unwatch(projectId, "conn-budget");

        own.Should().BeGreaterThan(0, "наблюдение обязано реально встать, иначе замер пустой");
        own.Should().BeLessThan(WatchBudget,
            $"открытый проект не должен выедать бюджет inotify (замерено {own})");
        // Свойство конструкции, а не замер: разбиение дерева на поддеревья с отдельным
        // FileSystemWatcher на каждое дало бы на этом репозитории 178 экземпляров при
        // дефолтном потолке ядра 128.
        ownInstances.Should().Be(1, "наблюдатель проекта держит ровно один экземпляр inotify");
    }

    [SkippableFact]
    public async Task НовыйКаталогИФайлВНём_ДоходятДоКлиента_АСлужебныйКаталогНет()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "подписка по перечню каталогов — только Linux");

        var root = Path.Combine(_tempDir, "proj");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "pkg"));
        var sent = new List<JsonElement>();
        var (svc, projectId) = Build(root, sent);

        svc.Watch(projectId, "conn-tree");
        // Каталог, созданный уже ПОСЛЕ старта наблюдения: дерево файлов в UI обновляется
        // именно по его событию, а файл внутри него — по подписке, встающей на лету.
        Directory.CreateDirectory(Path.Combine(root, "src", "fresh"));
        await Task.Delay(300);
        File.WriteAllText(Path.Combine(root, "src", "fresh", "a.txt"), "x");
        // Служебный каталог не подписан вовсе — его правка до клиента доехать не может.
        File.WriteAllText(Path.Combine(root, "node_modules", "pkg", "index.js"), "x");

        var paths = await WaitPaths(sent, "src/fresh/a.txt", TimeSpan.FromSeconds(10));
        paths.Should().Contain("src/fresh", "появившийся каталог обязан дойти до дерева файлов");
        paths.Should().NotContain(p => p.StartsWith("node_modules", StringComparison.Ordinal),
            "служебное поддерево не наблюдается и в события не попадает");

        svc.Unwatch(projectId, "conn-tree");
    }

    // Собирает пути из всех событий filesChanged, пока не появится ожидаемый.
    private static async Task<List<string>> WaitPaths(List<JsonElement> sent, string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var paths = new List<string>();
            lock (sent)
                foreach (var payload in sent)
                    paths.AddRange(payload.GetProperty("paths").EnumerateArray().Select(p => p.GetString()!));
            if (paths.Contains(expected)) return paths;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"событие по {expected} не доехало до клиента: [{string.Join(", ", paths)}]");
            await Task.Delay(100);
        }
    }
}
