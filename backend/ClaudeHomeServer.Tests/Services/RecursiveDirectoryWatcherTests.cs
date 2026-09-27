using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Примитив наблюдения за деревом: подписка идёт по перечню каталогов, а не на всё дерево
/// (разбор OOM/Linux 2026-09-22). Здесь проверяется САМА ПОДПИСКА — сколько слежек встало
/// и на что: фильтрация событий у потребителя (TurnFileWatcher.ShouldIgnore) маскирует
/// лишнюю подписку, событие из служебного каталога он гасит и так, поэтому «карточка не
/// пришла» ничего не доказывает.
/// </summary>
[Collection(TestCollections.Inotify)]
public class RecursiveDirectoryWatcherTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ccs-rdw-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* уборка */ }
    }

    private static readonly string[] Excludes = [".git", "node_modules", "bin", "obj"];

    [SkippableFact]
    public void Подписка_ТолькоНеслужебныеКаталоги()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "подписка по перечню каталогов — только Linux");

        // Своих каталогов 4 (src, src/a, src/b, docs) плюс корень; служебных — 30.
        Directory.CreateDirectory(Path.Combine(_root, "src", "a"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "b"));
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        for (var i = 0; i < 10; i++)
        {
            Directory.CreateDirectory(Path.Combine(_root, "node_modules", "pkg" + i));
            Directory.CreateDirectory(Path.Combine(_root, "src", "a", "obj", "d" + i));
            Directory.CreateDirectory(Path.Combine(_root, ".git", "objects", "x" + i));
        }

        using var watcher = new RecursiveDirectoryWatcher(_root, Excludes, (_, _) => { });
        watcher.Start();

        watcher.WatchCount.Should().Be(5,
            "подписываются корень и четыре своих каталога — служебные поддеревья не обходятся вовсе");
    }

    [SkippableFact]
    public void Потолок_ОстанавливаетПодписку_АНаблюдениеЖивёт()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "потолок слежек — только Linux");

        for (var i = 0; i < 20; i++) Directory.CreateDirectory(Path.Combine(_root, "d" + i));
        var warnings = new List<string>();
        var failures = new List<DirectoryWatchFailure>();

        using var watcher = new RecursiveDirectoryWatcher(_root, Excludes, (_, _) => { },
            maxWatches: 5, onWarning: warnings.Add,
            onFailure: f => { lock (failures) failures.Add(f); });
        watcher.Start();

        watcher.WatchCount.Should().Be(5, "потолок — гарантия конструкции, а не пожелание");
        watcher.IsWatching.Should().BeTrue("упёршись в потолок, наблюдение продолжает работать");
        warnings.Should().ContainSingle(w => w.Contains("потолок", StringComparison.Ordinal),
            "о частичном отказе сообщается один раз, а не на каждый каталог");
        // Строки в stderr мало: для потребителя часть дерева перестала наблюдаться — это
        // ровно потеря событий, и лечится она полной пересинхронизацией, а не пересозданием.
        lock (failures)
            failures.Should().Equal([DirectoryWatchFailure.EventsLost],
                "потолок отдаётся потребителю один раз и именно как потеря событий");
    }

    // Потолок делает ПОРЯДОК обхода решающим: бюджет, потраченный на первое попавшееся
    // толстое поддерево, отнимается у рабочих каталогов рядом с корнем. Обход шириной —
    // единственное, что держит здесь смысл (разбор 2026-09-22: `.venv` python-проекта
    // съедал бюджет целиком, `src/` не подписывался вовсе и молчал).
    [SkippableFact]
    public void ПодПотолком_ПодписываютсяВерхниеУровни_АНеНедраОдногоПоддерева()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "потолок слежек — только Linux");

        // Две симметричные ветки: у каждой по 5 детей, у каждого ребёнка — свои 5 внуков.
        // Имена НЕ из списка исключений: проверяется именно порядок обхода, а не фильтр.
        foreach (var branch in new[] { "a", "b" })
            for (var i = 0; i < 5; i++)
                for (var j = 0; j < 5; j++)
                    Directory.CreateDirectory(Path.Combine(_root, branch, $"{branch}{i}", $"g{j}"));

        // Ровно на корень, обе ветки и всех их детей: 1 + 2 + 10.
        using var watcher = new RecursiveDirectoryWatcher(_root, Excludes, (_, _) => { },
            maxWatches: 13, onWarning: _ => { });
        watcher.Start();

        watcher.WatchCount.Should().Be(13, "потолок — гарантия конструкции");
        // Обход глубиной уходил бы во внуков ПЕРВОЙ попавшейся ветки и выбирал бюджет там,
        // оставляя детей второй ветки без наблюдения вовсе — независимо от того, какая ветка
        // попалась первой. Шириной бюджет тратится по уровням, и дети есть у обеих.
        var expected = (from branch in new[] { "a", "b" }
                        from i in Enumerable.Range(0, 5)
                        select Path.Combine(_root, branch, $"{branch}{i}")).ToArray();
        watcher.Subscribed.Should().Contain(expected,
            "под потолком подписываются каталоги ближе к корню, а не недра одного поддерева " +
            "(разбор 2026-09-22: `.venv` python-проекта съедал бюджет целиком, `src/` молчал)");
    }

    // Чужие экосистемы в списке исключений — не украшение: `.venv` весит тысячи каталогов,
    // и без обрезки он выбирает весь бюджет слежек ещё до рабочего кода.
    [SkippableFact]
    public void КаталогиЧужихЭкосистем_НеПодписываются()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "подписка по перечню каталогов — только Linux");

        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        foreach (var heavy in new[] { ".venv", "venv", "__pycache__", "build", "vendor", ".gradle", ".tox", "coverage" })
            Directory.CreateDirectory(Path.Combine(_root, heavy, "inner"));
        Directory.CreateDirectory(Path.Combine(src, "site-packages", "inner"));

        using var watcher = new RecursiveDirectoryWatcher(_root, TreeExcludes.Names, (_, _) => { });
        watcher.Start();

        watcher.WatchCount.Should().Be(2, "подписаны только корень и src — тяжёлые каталоги не обходятся");
    }

    // Списки исключений у дерева файлов и у ватчера хода обязаны быть одним набором:
    // пополнили один — разъезд со вторым означает «подписались, но глушим» (или наоборот,
    // потратили бюджет слежек на то, что всё равно не показываем).
    [Fact]
    public void СпискиИсключений_ДереваИВатчераХода_НеРазъезжаются()
    {
        var turn = FileWatcherOptions.Default.IgnoreDirs.ToHashSet(StringComparer.OrdinalIgnoreCase);

        TreeExcludes.Names.Except(turn, StringComparer.OrdinalIgnoreCase).Should().BeEmpty(
            "ватчер хода не наблюдает ничего сверх того, что скрыто из дерева файлов");
        // Сверх общего набора у ватчера хода только служебные каталоги инструментов:
        // в дереве файлов они человеку нужны, в ленте чата их правки — нет.
        turn.Except(TreeExcludes.Names, StringComparer.OrdinalIgnoreCase)
            .Should().BeEquivalentTo([".omc", ".claude", ".playwright"]);
    }

    [SkippableFact]
    public async Task СимволическиеСсылки_НеОбходятсяИНеПодписываются()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "обход по ссылкам — только Linux");

        // Настоящих каталогов 3 (src, src/a, src/b) плюс корень. Ссылка `loop -> ..`
        // при обходе по ссылкам даёт бесконечную цепочку src/loop/src/loop/… (потолок
        // слежек её не ловит: тот же inode — тот же wd), `ext -> outside` уводит слежку
        // за пределы корня.
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(src, "a"));
        Directory.CreateDirectory(Path.Combine(src, "b"));
        var outside = Path.Combine(Path.GetTempPath(), "ccs-rdw-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(src, "loop"), "..");
        Directory.CreateSymbolicLink(Path.Combine(_root, "ext"), outside);

        var seen = new List<string>();
        using var watcher = new RecursiveDirectoryWatcher(_root, Excludes,
            (path, _) => { lock (seen) seen.Add(path); });
        try
        {
            watcher.Start();

            watcher.WatchCount.Should().Be(4,
                "подписываются только настоящие каталоги: корень, src, src/a, src/b");

            // Чужой файл создаётся ПЕРВЫМ: дождавшись события своего, мы знаем, что цикл
            // чтения жив и чужое событие не «просто не успело».
            await File.WriteAllTextAsync(Path.Combine(outside, "alien.txt"), "x");
            var mine = Path.Combine(src, "a", "mine.txt");
            await File.WriteAllTextAsync(mine, "x");

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (seen)
                    if (seen.Contains(mine)) break;
                await Task.Delay(50);
            }

            lock (seen)
            {
                seen.Should().Contain(mine, "за настоящим поддеревом наблюдение идёт");
                // Именно ПУТЬ ЧЕРЕЗ ССЫЛКУ: события приходят по пути слежки, поэтому файл
                // из-за ссылки пришёл бы как `_root/ext/alien.txt`, а не по реальному пути
                // вне корня — проверка на префикс `outside` не могла упасть в принципе.
                seen.Should().NotContain(Path.Combine(_root, "ext", "alien.txt"),
                    "слежек вне корня нет — ссылка наружу не обходится");
            }
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { /* уборка */ }
        }
    }

    // Дерево файлов в UI (FileWatcherService) обновляется и по САМИМ каталогам: без их событий
    // созданная папка не появлялась бы в списке до ручного обновления. Ватчеру хода они,
    // наоборот, не нужны — отсюда два режима, и оба проверяются здесь.
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task СозданиеИУдалениеКаталога_ОтдаётсяТолькоВРежимеIncludeDirectories(bool includeDirectories)
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "события каталогов по перечню слежек — только Linux");

        Directory.CreateDirectory(_root);
        var seen = new List<(string Path, DirectoryWatchKind Kind)>();
        using var watcher = new RecursiveDirectoryWatcher(_root, Excludes,
            (path, kind) => { lock (seen) seen.Add((path, kind)); },
            includeDirectories: includeDirectories);
        watcher.Start();

        var fresh = Path.Combine(_root, "fresh");
        var probe = Path.Combine(fresh, "probe.txt");
        Directory.CreateDirectory(fresh);
        await Task.Delay(200); // слежка на новом каталоге должна встать до записи файла
        await File.WriteAllTextAsync(probe, "x");
        // Файл внутри нового каталога приходит в любом режиме: по нему видно, что цикл чтения
        // жив и «события каталога нет» не означает «не успело».
        (await Seen(seen, probe, DirectoryWatchKind.Created)).Should().BeTrue("подписка на новый каталог встала");

        Directory.Delete(fresh, recursive: true);
        (await Seen(seen, probe, DirectoryWatchKind.Deleted)).Should().BeTrue("удаление файла видно");

        if (includeDirectories)
        {
            (await Seen(seen, fresh, DirectoryWatchKind.Created)).Should().BeTrue(
                "появившийся каталог обязан дойти до дерева файлов");
            (await Seen(seen, fresh, DirectoryWatchKind.Deleted)).Should().BeTrue(
                "исчезнувший каталог тоже: иначе он висел бы в дереве до обновления руками");
        }
        else
        {
            // Событий каталога нет — при этом файловые по этому же каталогу уже пришли,
            // то есть проверка не на «не успело».
            (await Seen(seen, fresh, DirectoryWatchKind.Created)).Should().BeFalse(
                "ватчеру хода каталоги не отдаются — только файлы");
            (await Seen(seen, fresh, DirectoryWatchKind.Deleted)).Should().BeFalse(
                "ватчеру хода каталоги не отдаются — только файлы");
        }
    }

    // Пришло ли событие (ожидание с дедлайном: события читаются отдельным потоком).
    private static async Task<bool> Seen(List<(string Path, DirectoryWatchKind Kind)> seen,
        string path, DirectoryWatchKind kind)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            lock (seen)
                if (seen.Contains((path, kind))) return true;
            await Task.Delay(50);
        }
        return false;
    }

    // Удаление КОРНЯ — конец наблюдения, а не выбывание одного каталога: прежде оно тихо
    // чистило карту слежек, IsWatching оставался true, и лестница пересоздания у
    // FileWatcherService не запускалась (`rm -rf proj && git clone … proj` у открытого
    // проекта — дерево в UI стояло мёртвым до реконнекта).
    [SkippableFact]
    public async Task УдалениеКорня_ДаётСбойStopped()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "смерть корневой слежки — только Linux");

        Directory.CreateDirectory(Path.Combine(_root, "src"));
        var failures = new List<DirectoryWatchFailure>();
        using var watcher = new RecursiveDirectoryWatcher(_root, Excludes, (_, _) => { },
            onWarning: _ => { }, onFailure: f => { lock (failures) failures.Add(f); });
        watcher.Start();

        Directory.Delete(_root, recursive: true);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (failures)
                if (failures.Contains(DirectoryWatchFailure.Stopped)) break;
            await Task.Delay(50);
        }
        lock (failures)
            failures.Should().Contain(DirectoryWatchFailure.Stopped,
                "наблюдения больше нет — потребитель обязан узнать об этом и пересоздать наблюдатель");
    }

    // Переполнение очереди уносит с собой и события IN_CREATE новых каталогов: без повторного
    // обхода они не подписались бы никогда (у долгоживущего наблюдателя дерева это `git
    // checkout` ветки с новыми папками — правки в них не доходят ни до UI, ни до синка знаний).
    // Очередь переполняется по-настоящему: потребитель держит поток чтения, а событий
    // создаётся больше, чем помещается в очередь ядра.
    [SkippableFact]
    public async Task ПереполнениеОчереди_ПересинхронизируетПодписки()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "переполнение очереди inotify — только Linux");
        var queueLimit = QueuedEventsLimit();
        Skip.If(queueLimit is null or > 40000, "очередь ядра слишком велика для теста");

        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(work);

        var gate = new SemaphoreSlim(0, 1);
        var held = new TaskCompletionSource();
        var blockedOnce = false;
        var failures = new List<DirectoryWatchFailure>();
        var seen = new List<string>();
        using var watcher = new RecursiveDirectoryWatcher(_root, Excludes,
            (path, _) =>
            {
                // Первый же колбэк держит поток чтения: пока он стоит, очередь ядра копится
                // и переполняется — ровно так это происходит и в бою (медленный потребитель).
                if (!blockedOnce)
                {
                    blockedOnce = true;
                    held.TrySetResult();
                    gate.Wait();
                }
                lock (seen) seen.Add(path);
            },
            onWarning: _ => { }, includeDirectories: true,
            onFailure: f => { lock (failures) failures.Add(f); });
        watcher.Start();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(work, "trigger.txt"), "x");
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10)); // поток чтения встал

            // Переполняем очередь с запасом, и только ПОСЛЕ этого создаём каталог: его
            // IN_CREATE ядру уже некуда положить — событие потеряно гарантированно.
            for (var i = 0; i < queueLimit + 2000; i++)
                File.WriteAllText(Path.Combine(work, $"f{i}.txt"), "x");
            var late = Path.Combine(_root, "late");
            Directory.CreateDirectory(late);
        }
        finally
        {
            gate.Release();
        }

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            lock (failures)
                if (failures.Contains(DirectoryWatchFailure.EventsLost)) break;
            await Task.Delay(100);
        }
        lock (failures)
            failures.Should().Contain(DirectoryWatchFailure.EventsLost,
                "переполнение очереди обязано дойти до потребителя как потеря событий");

        var lateDir = Path.Combine(_root, "late");
        watcher.Subscribed.Should().Contain(lateDir,
            "каталог, чьё событие создания потеряно при переполнении, подписывается повторным обходом");

        // И подписка живая, а не только запись в карте: файл внутри такого каталога доходит.
        var probe = Path.Combine(lateDir, "probe.txt");
        await File.WriteAllTextAsync(probe, "x");
        var probeDeadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < probeDeadline)
        {
            lock (seen)
                if (seen.Contains(probe)) break;
            await Task.Delay(100);
        }
        lock (seen)
            seen.Should().Contain(probe, "после пересинхронизации события из нового каталога приходят");
    }

    // Потолок очереди ядра: сколько событий помещается до IN_Q_OVERFLOW.
    private static int? QueuedEventsLimit()
    {
        try
        {
            return int.TryParse(File.ReadAllText("/proc/sys/fs/inotify/max_queued_events").Trim(), out var v)
                ? v : null;
        }
        catch { return null; }
    }

    // Второе гашение обязано быть пустой операцией. Иначе оно проходит Join мгновенно (поток
    // уже вышел) и делает close() на номер, который ядро успело выдать НОВОМУ владельцу —
    // тихо закрытый сокет Kestrel или чужой inotify. Сценарий реален: Start нового хода и
    // Stop из HandleProcessExitedAsync приходят с разных потоков.
    [SkippableFact]
    public void ПовторныйDispose_НеЗакрываетЧужойДескриптор()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "номера дескрипторов inotify — только Linux");

        Directory.CreateDirectory(_root);
        var backend = new RecursiveDirectoryWatcher.InotifyBackend(_root,
            new HashSet<string>(Excludes, StringComparer.OrdinalIgnoreCase), (_, _) => { },
            maxWatches: 100, warn: _ => { }, includeDirectories: false, fail: _ => { });
        var fd = backend.FdForTests;
        backend.Dispose();

        // Занимаем освободившийся номер своим файлом: ядро отдаёт наименьший свободный.
        var probes = new List<FileStream>();
        try
        {
            FileStream? victim = null;
            for (var i = 0; i < 32 && victim is null; i++)
            {
                var fs = File.OpenRead("/dev/null");
                probes.Add(fs);
                if ((int)fs.SafeFileHandle.DangerousGetHandle() == fd) victim = fs;
            }
            Skip.If(victim is null, "номер закрытого дескриптора занял кто-то другой — проверять нечего");

            backend.Dispose(); // второе гашение того же backend'а

            Fcntl(fd, FGetFd).Should().BeGreaterThanOrEqualTo(0,
                "дескриптор нового владельца остался живым — повторное гашение его не трогает");
        }
        finally
        {
            foreach (var fs in probes) fs.Dispose();
        }
    }

    private const int FGetFd = 1;

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int fd, int cmd);

    [SkippableFact]
    public async Task ПеренесённыйКаталог_НеОставляетФантомныхСлежек()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "снятие слежек поддерева — только Linux");

        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(src, "moved", "inner"));
        using var watcher = new RecursiveDirectoryWatcher(_root, Excludes, (_, _) => { });
        watcher.Start();
        watcher.WatchCount.Should().Be(4); // корень, src, moved, inner

        // Перенос ЗА пределы дерева: ядро не шлёт IN_IGNORED, слежка осталась бы жить
        // с фантомным путём — и её пришлось бы снимать вручную.
        var outside = Path.Combine(Path.GetTempPath(), "ccs-rdw-out-" + Guid.NewGuid().ToString("N"));
        Directory.Move(Path.Combine(src, "moved"), outside);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (watcher.WatchCount > 2 && DateTime.UtcNow < deadline) await Task.Delay(50);
            watcher.WatchCount.Should().Be(2, "слежки уехавшего поддерева сняты (корень и src)");
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { /* уборка */ }
        }
    }
}
