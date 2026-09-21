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
/// ОС), поэтому синхронную ошибку слежки на подпапке даёт путь длиннее PATH_MAX
/// (ENAMETOOLONG) — тот же путь кода, и одинаково под любым пользователем: права на папку
/// (chmod 000) root обходит, а EMFILE при урезанном лимите fd бросается из старта
/// исключением и Error не порождает вовсе. Шторм старого кода обрывается потолком
/// дескрипторов процесса.
/// </summary>
[Collection(TestCollections.Inotify)]
public class InotifyLeakRegressionTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "ccs_inotify_" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];
    // Переименованная вершина цепочки с путём длиннее PATH_MAX: перед уборкой её надо вернуть
    // к короткому имени, иначе рекурсивное удаление само упрётся в ENAMETOOLONG.
    private (string Long, string Short)? _deepChain;

    public InotifyLeakRegressionTests()
    {
        if (OperatingSystem.IsLinux()) InotifyProbe.WarmUp(Path.Combine(_tempDir, "warmup"));
    }

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        if (_deepChain is { } chain && Directory.Exists(chain.Long))
            try { Directory.Move(chain.Long, chain.Short); }
            catch { /* уборка */ }
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* уборка temp — не предмет теста */ }
    }

    // Цепочка вложенных папок, у самой глубокой из которых полный путь длиннее PATH_MAX (4096):
    // создаётся короткой (иначе mkdir сам упал бы на ENAMETOOLONG), затем вершина
    // переименовывается в имя из 250 символов. Родитель последней папки ещё открывается,
    // а inotify_add_watch на ней самой отвечает ENAMETOOLONG — .NET шлёт это в Error.
    private void CreateTooLongChain(string root)
    {
        const int PathMax = 4096;
        var top = Path.Combine(root, "c");
        var segment = new string('s', 50);
        var levels = (PathMax - 100 - top.Length) / (segment.Length + 1);
        var deepest = top;
        for (var i = 0; i < levels; i++) deepest = Path.Combine(deepest, segment);
        Directory.CreateDirectory(deepest);
        var longTop = Path.Combine(root, new string('L', 250));
        Directory.Move(top, longTop);
        _deepChain = (longTop, top);
        (longTop.Length + deepest.Length - top.Length).Should().BeGreaterThan(PathMax,
            "иначе слежка на глубокой папке встанет и сценарий ничего не проверит");
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
        CreateTooLongChain(root);

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
        // Теория — порядка десятка за 2 с; граница с запасом на медленный раннер: смысл
        // проверки — «нет шторма на сотни и тысячи», а не точное число.
        svc.RecreateCount.Should().BeLessThan(100,
            "пересоздание идёт по лестнице пауз, а не штормом из колбэка");
    }

    [Fact]
    public void WatchPath_ТотЖеПутьБезНаблюдателя_ПоднимаетЕгоНаТойЖеЗаписи()
    {
        var root = Path.Combine(_tempDir, "same");
        Directory.CreateDirectory(root);
        var svc = BuildService();

        svc.WatchPath("worktree:same", root);
        var first = svc.Inspect("worktree:same");
        first.Should().NotBeNull();
        first!.Value.Watching.Should().BeTrue();

        svc.DropWatcherKeepEntry("worktree:same");
        svc.Inspect("worktree:same")!.Value.Watching.Should().BeFalse();

        svc.WatchPath("worktree:same", root);
        var second = svc.Inspect("worktree:same");
        second!.Value.Entry.Should().BeSameAs(first.Value.Entry,
            "живая запись переиспользуется: подмена новой теряла бы накопленные PendingPaths");
        second.Value.Watching.Should().BeTrue("наблюдение поднимается заново на той же записи");
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
