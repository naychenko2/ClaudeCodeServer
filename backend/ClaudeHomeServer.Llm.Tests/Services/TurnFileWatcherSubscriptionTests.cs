using System.Collections.Concurrent;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Бюджет inotify-слежек одного хода (разбор OOM/Linux 2026-09-22). FileSystemWatcher с
/// IncludeSubdirectories на Linux ставит слежку на КАЖДЫЙ каталог дерева, включая
/// .git/node_modules/bin/obj: фильтры FileWatcherOptions режут события, а не подписку.
/// На этом репозитории это было 16–22 тыс. слежек за ход при дефолтном потолке ядра
/// 65 536 — два хода выедали лимит, и user-инстанс systemd переставал видеть memory.events
/// своих scope («Failed to add memory inotify watch descriptor … No space left on device»).
/// Замер идёт на настоящем дереве репозитория: синтетическая папка в temp такой формы
/// (двадцать .csproj с bin/obj, node_modules, .git) не воспроизводит.
/// </summary>
// Ожиданий в классе нет: первый тест — замер подписки (без пауз вовсе), второй ждёт
// события карточек с ограниченными окнами. Метка Slow снята по переводу ожидания
// на событие (паттерн Э4, W4).
[Collection(TestCollections.Inotify)]
public class TurnFileWatcherSubscriptionTests
{
    // Потолок задачи: один ход на этом репозитории — меньше 5 000 слежек.
    private const int WatchBudget = 5000;

    // Корень репозитория от каталога сборки тестов вверх до папки с .git и backend/.
    // .git — и файл (git worktree), и каталог (обычный репозиторий): в worktree
    // Directory.Exists ложится в false, и тест молча пропускался (TreeExcludes.HasOwnGit).
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var hasGit = File.Exists(Path.Combine(dir.FullName, ".git"))
                || Directory.Exists(Path.Combine(dir.FullName, ".git"));
            if (hasGit && Directory.Exists(Path.Combine(dir.FullName, "backend"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    [SkippableFact]
    public void СтартНаРепозитории_НеВыедаетБюджетСлежек()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "inotify и /proc/self/fdinfo — только Linux");
        var root = FindRepoRoot();
        Skip.If(root is null, "тест идёт на настоящем дереве репозитория");

        var before = InotifyProbe.CountWatches();
        var instancesBefore = InotifyProbe.CountInotifyFds();
        using var watcher = new TurnFileWatcher(root!, _ => Task.CompletedTask);
        watcher.Start();
        var own = InotifyProbe.CountWatches() - before;
        var ownInstances = InotifyProbe.CountInotifyFds() - instancesBefore;
        watcher.Stop();

        own.Should().BeGreaterThan(0, "наблюдение обязано реально встать, иначе замер пустой");
        own.Should().BeLessThan(WatchBudget,
            $"ход на репозитории не должен выедать бюджет inotify (замерено {own})");
        // Свойство конструкции, а не замер: разбиение дерева на поддеревья с отдельным
        // FileSystemWatcher на каждое дало бы на этом репозитории 178 экземпляров при
        // дефолтном потолке ядра 128.
        ownInstances.Should().Be(1, "наблюдатель хода держит ровно один экземпляр inotify");
    }

    [SkippableFact]
    public async Task ФайлВГлубинеИВНовомКаталоге_ДоходитДоЛенты_АСлужебныйКаталогНет()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "подписка по перечню каталогов — только Linux");

        var root = Path.Combine(Path.GetTempPath(), "ccs-watch-sub-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "src", "deep"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "pkg"));
        var deepFile = Path.Combine(root, "src", "deep", "a.txt");
        File.WriteAllText(deepFile, "one\n");

        var seen = new ConcurrentQueue<string>();
        var signal = new SemaphoreSlim(0);
        var watcher = new TurnFileWatcher(root, msg =>
        {
            if (msg is FileChangedMessage m) { seen.Enqueue(m.Path); signal.Release(); }
            return Task.CompletedTask;
        }, timing: new FileWatcherTiming(TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(80)));
        try
        {
            watcher.Start();

            // Правка в уже существующем вложенном каталоге
            File.WriteAllText(deepFile, "one\ntwo\n");
            (await signal.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue("правка в глубине дерева видна");

            // Каталог, созданный уже ПОСЛЕ старта наблюдения, тоже должен подписаться.
            // Подписку ставит поток чтения на событии mkdir; первая запись может попасть
            // до неё — тогда файл ловится обзором нового каталога (AddSubtree с
            // emitExistingFiles, RecursiveDirectoryWatcher.cs:414-463). Не спим «на глазок»:
            // повторяем запись, пока не пришла карточка — подписка гарантирована, поэтому
            // ни одна попытка после первой не мимо.
            var fresh = Path.Combine(root, "src", "fresh");
            Directory.CreateDirectory(fresh);
            var freshFile = Path.Combine(fresh, "b.txt");
            var gotFresh = false;
            for (var attempt = 1; attempt <= 3 && !gotFresh; attempt++)
            {
                File.WriteAllText(freshFile, $"hello {attempt}\n");
                gotFresh = await signal.WaitAsync(TimeSpan.FromSeconds(2));
            }
            gotFresh.Should().BeTrue("новый каталог подписывается на лету");

            // Из служебного каталога карточка не доходит. Что на него ещё и не ПОДПИСЫВАЮТСЯ —
            // отдельная проверка (RecursiveDirectoryWatcherTests): здесь лишнюю подписку
            // замаскировал бы фильтр событий ShouldIgnore.
            // Negative-окно выведено из тестового debounce (80мс): карточка, просочись она
            // через обе защиты, пришла бы не позже debounce+обработки; 800мс — ~10× запас.
            File.WriteAllText(Path.Combine(root, "node_modules", "pkg", "index.js"), "x\n");
            (await signal.WaitAsync(TimeSpan.FromMilliseconds(800))).Should().BeFalse("карточки из служебного каталога нет");

            seen.Should().Contain(p => p.EndsWith("deep/a.txt", StringComparison.Ordinal));
            seen.Should().Contain(p => p.EndsWith("fresh/b.txt", StringComparison.Ordinal));
        }
        finally
        {
            watcher.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { /* уборка */ }
        }
    }
}
