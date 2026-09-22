using ClaudeHomeServer.Services.Files;
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

        using var watcher = new RecursiveDirectoryWatcher(_root, Excludes, (_, _) => { },
            maxWatches: 5, onWarning: warnings.Add);
        watcher.Start();

        watcher.WatchCount.Should().Be(5, "потолок — гарантия конструкции, а не пожелание");
        watcher.IsWatching.Should().BeTrue("упёршись в потолок, наблюдение продолжает работать");
        warnings.Should().ContainSingle(w => w.Contains("потолок", StringComparison.Ordinal),
            "о частичном отказе сообщается один раз, а не на каждый каталог");
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
                seen.Should().NotContain(p => p.StartsWith(outside, StringComparison.Ordinal),
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
