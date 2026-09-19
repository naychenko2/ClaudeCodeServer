using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Утечка inotify-экземпляров (инцидент 2026-09-19, Linux-прод: 13 → 8076, после чего каждый
/// новый ход падал на старте TurnFileWatcher). Механизм: Error наблюдателя пересоздавал его
/// прямо из колбэка; новый наблюдатель падал СИНХРОННО внутри своего же EnableRaisingEvents
/// (на проде — ENOSPC при исчерпанном бюджете слежек) и заказывал следующий — рекурсия до
/// лимита экземпляров. Бюджет слежек в тесте не исчерпать безопасно (он общий на пользователя
/// ОС), поэтому синхронную ошибку старта даёт папка без прав на чтение (EACCES на слежку) —
/// тот же путь кода. Шторм старого кода обрывается потолком дескрипторов процесса.
/// </summary>
[Collection(TestCollections.FdLimit)]
public class InotifyLeakRegressionTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "ccs_inotify_" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];
    private string? _lockedDir;

    public InotifyLeakRegressionTests()
    {
        if (OperatingSystem.IsLinux()) InotifyProbe.WarmUp(Path.Combine(_tempDir, "warmup"));
    }

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        if (_lockedDir is not null && OperatingSystem.IsLinux())
            try { File.SetUnixFileMode(_lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch { /* уборка */ }
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* уборка temp — не предмет теста */ }
    }

    private FileWatcherService BuildService()
    {
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["FileWatcher:UsePolling"] = "false",
            // Короткая лестница пауз: за 2 с — порядка десятка пересозданий, а не тысячи
            ["FileWatcher:RecreateDelayMs"] = "50",
            ["FileWatcher:RecreateMaxDelayMs"] = "200",
        }).Build();

        var userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(config, userStore, new AppSettingsService(config));
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Options.Create(new DifyOptions()), wkStore);
        var knowledgeSync = new ProjectKnowledgeSyncService(knowledge, wkStore, projects,
            new ProjectFileGateway(new FileService()), new RecordingHubNotifier(), new NullDifyMetrics(),
            NullLogger<ProjectKnowledgeSyncService>.Instance);
        var graphs = new CodeGraphService(NullLogger<CodeGraphService>.Instance, new ProjectRootLookup(projects),
            new GraphPersistence(_tempDir, NullLogger<GraphPersistence>.Instance), config);

        var svc = new FileWatcherService(projects, new Mock<IHubContext<SessionHub>>().Object,
            knowledgeSync, graphs, config);
        _disposables.Add(svc);
        return svc;
    }

    [SkippableFact]
    public async Task ОшибкаСлежкиПриСтарте_ВсплескВДереве_InotifyНеБольшеЖивыхНаблюдателей()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "inotify и /proc/self/fd — только Linux");

        var root = Path.Combine(_tempDir, "worktree");
        for (var i = 0; i < 50; i++) Directory.CreateDirectory(Path.Combine(root, "src", "d" + i));
        _lockedDir = Path.Combine(root, "locked");
        Directory.CreateDirectory(_lockedDir);
        File.SetUnixFileMode(_lockedDir, UnixFileMode.None);
        var readable = true;
        try { Directory.EnumerateFileSystemEntries(_lockedDir).Any(); }
        catch (UnauthorizedAccessException) { readable = false; }
        Skip.If(readable, "под root права на папку не мешают слежке — сценарий не воспроизвести");

        var svc = BuildService();
        var baseline = InotifyProbe.CountInotifyFds();
        var peak = baseline;

        // Потолок номера fd с запасом в 300: шторм старого кода упирается в EMFILE здесь,
        // а не в общий на пользователя лимит inotify-экземпляров. Сценарий — в Task.Run под
        // таймаутом: старый код, помимо шторма, ловил взаимоблокировку (пересоздание под _lock
        // из Error, который .NET зовёт из-под своего замка слежек), и тест вис бы навсегда.
        var hung = false;
        await InotifyProbe.WithFdLimit(InotifyProbe.HighestFd() + 300, async () =>
        {
            var scenario = Task.Run(async () =>
            {
                svc.WatchPath("worktree:leak", root);
                peak = Math.Max(peak, InotifyProbe.CountInotifyFds());
                // Всплеск как у dotnet build: сотни новых папок с файлом в наблюдаемом дереве
                for (var i = 0; i < 500; i++)
                {
                    var d = Path.Combine(root, "obj", "zz", "d" + i);
                    Directory.CreateDirectory(d);
                    File.WriteAllText(Path.Combine(d, "f"), "");
                }
                var until = DateTime.UtcNow.AddSeconds(2);
                while (DateTime.UtcNow < until)
                {
                    peak = Math.Max(peak, InotifyProbe.CountInotifyFds());
                    await Task.Delay(20);
                }
                svc.UnwatchPath("worktree:leak");
            });
            // Лимит снимается в любом случае — и при зависании, иначе посыпались бы соседние тесты
            hung = await Task.WhenAny(scenario, Task.Delay(TimeSpan.FromSeconds(30))) != scenario;
            if (!hung) await scenario;
        });
        hung.Should().BeFalse("наблюдение со сбойной слежкой не должно зависать (взаимоблокировка пересоздания)");

        var after = await InotifyProbe.WaitInotifyAtMost(baseline, TimeSpan.FromSeconds(10));

        peak.Should().BeLessThanOrEqualTo(baseline + 2,
            "живой наблюдатель один (плюс закрывающийся предыдущий) — экземпляров inotify не больше");
        after.Should().BeLessThanOrEqualTo(baseline,
            "после снятия наблюдателя ни одного inotify-экземпляра не остаётся");
        svc.RecreateCount.Should().BeGreaterThan(1,
            "сценарий обязан реально гонять Error → пересоздание, иначе тест ничего не проверяет");
        svc.RecreateCount.Should().BeLessThan(40,
            "пересоздание идёт по лестнице пауз, а не штормом из колбэка");
    }

    [SkippableFact]
    public async Task TurnFileWatcher_СтартУпалНаЛимите_НаблюдательНеОстаётсяИОшибкаНеUnreachable()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "EMFILE через setrlimit — только Linux");

        var dir = Path.Combine(_tempDir, "turn");
        Directory.CreateDirectory(dir);
        var watcher = new TurnFileWatcher(dir, _ => Task.CompletedTask);
        _disposables.Add(watcher);
        var before = InotifyProbe.InotifyFdNumbers();
        watcher.Start();
        watcher.IsWatching.Should().BeTrue();
        var ownFd = InotifyProbe.InotifyFdNumbers().Except(before).Single();

        Exception? thrown = null;
        // Потолок номера — не выше наименьшей дыры и fd прежнего наблюдателя: Start сперва
        // закрывает прежний, и освободившийся номер не должен достаться новому экземпляру
        await InotifyProbe.WithFdLimit(Math.Min(InotifyProbe.LowestFreeFd(), ownFd), () =>
        {
            thrown = Record.Exception(() => watcher.Start());
            return Task.CompletedTask;
        });

        thrown.Should().BeOfType<IOException>();
        watcher.IsWatching.Should().BeFalse(
            "упавший старт не должен оставлять ни прежний, ни недоделанный FileSystemWatcher");
        TurnErrorClassifier.Classify(new TurnAttemptOutcome { HasResult = false, ErrorText = thrown!.Message })
            .Should().Be(FallbackErrorClass.None, "лимит ОС — не повод жечь цепочку фолбэка");

        watcher.Start();
        watcher.IsWatching.Should().BeTrue("после снятия лимита наблюдатель поднимается штатно");
    }
}
