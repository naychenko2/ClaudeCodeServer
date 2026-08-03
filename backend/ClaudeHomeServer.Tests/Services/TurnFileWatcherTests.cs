using System.Collections.Concurrent;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// TurnFileWatcher — детерминированная проверка задачи «доходит ли карточка от правки
// вне чата»: в живом прогоне (Вера) правка файла через bash во время хода не дала
// FileChangedMessage вообще, хотя git-панель прирост файла увидела. Ручной прогон это
// окно не воспроизводит (правка должна попасть между стартом и концом хода за секунды) —
// здесь события эмулируются прямой записью на диск, без гонки с реальным claude CLI.
public class TurnFileWatcherTests : IDisposable
{
    private readonly string _root;

    public TurnFileWatcherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccs-watcher-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static TurnFileWatcher CreateWatcher(string root, ConcurrentQueue<FileChangedMessage> received,
        SemaphoreSlim signal, FileChangeAttributor? attributor = null, string? ownerSessionId = null) =>
        new(root, msg =>
        {
            if (msg is FileChangedMessage fcm) { received.Enqueue(fcm); signal.Release(); }
            return Task.CompletedTask;
        }, attributor: attributor, ownerSessionId: ownerSessionId);

    private static async Task<FileChangedMessage?> WaitForMessageAsync(
        SemaphoreSlim signal, ConcurrentQueue<FileChangedMessage> received, TimeSpan timeout)
    {
        var got = await signal.WaitAsync(timeout);
        return got && received.TryDequeue(out var msg) ? msg : null;
    }

    [Fact]
    public async Task ВнешняяПравка_СИзменениемЧислаСтрок_ДоходитДоЛенты()
    {
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllText(path, "line1\nline2\nline3");

        var received = new ConcurrentQueue<FileChangedMessage>();
        var signal = new SemaphoreSlim(0);
        using var watcher = CreateWatcher(_root, received, signal);
        watcher.Start();
        await Task.Delay(200); // дать FileSystemWatcher подготовиться

        // _fileCache заполняется только по факту FS-события ПОСЛЕ Start — файл, созданный
        // до старта, в кэше отсутствует. Прогрев обязателен, иначе oldContent=null и added
        // считается от нуля, а не от реального предыдущего состояния.
        File.WriteAllText(path, "line1\nline2\nline3\nX");
        var warmup = await WaitForMessageAsync(signal, received, TimeSpan.FromSeconds(3));
        warmup.Should().NotBeNull();

        File.WriteAllText(path, "line1\nline2\nline3\nX\nline5");

        var msg = await WaitForMessageAsync(signal, received, TimeSpan.FromSeconds(3));

        msg.Should().NotBeNull("правка, меняющая число строк, обязана дойти до ленты");
        msg!.Added.Should().Be(1);
        msg.Removed.Should().Be(0);
    }

    [Fact]
    public async Task БезАтрибутора_СообщениеВсегдаНеВнешнее()
    {
        // Без FileChangeAttributor/ownerSessionId (как в этом тесте) ветка проставления
        // External недостижима — external остаётся false по умолчанию (TurnFileWatcher.cs:116).
        // Это ожидаемое поведение для сессий без владельца — не баг сам по себе.
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllText(path, "a\nb");

        var received = new ConcurrentQueue<FileChangedMessage>();
        var signal = new SemaphoreSlim(0);
        using var watcher = CreateWatcher(_root, received, signal);
        watcher.Start();
        await Task.Delay(200);

        File.WriteAllText(path, "a\nb\nc");
        var msg = await WaitForMessageAsync(signal, received, TimeSpan.FromSeconds(3));

        msg.Should().NotBeNull();
        msg!.External.Should().BeFalse();
    }

    [Fact]
    public async Task НезаявленнаяПравка_САтрибутором_ПомечаетсяВнешней()
    {
        // Сценарий "правка вне чата": заявок на путь нет ни у кого (эмулирует bash-команду
        // без Edit/Write). Проверяет, что ветка External=true технически достижима, когда
        // diff вообще замечен (см. следующий тест на случай, когда он НЕ замечен).
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllText(path, "a\nb");

        var received = new ConcurrentQueue<FileChangedMessage>();
        var signal = new SemaphoreSlim(0);
        var attributor = new FileChangeAttributor();
        using var watcher = CreateWatcher(_root, received, signal, attributor, ownerSessionId: "session-a");
        watcher.Start();
        await Task.Delay(200);

        File.WriteAllText(path, "a\nb\nc");
        var msg = await WaitForMessageAsync(signal, received, TimeSpan.FromSeconds(3));

        msg.Should().NotBeNull();
        msg!.External.Should().BeTrue("пути нет ни в чьих заявках — правка сделана не Edit/Write этой сессии");
    }

    [Fact]
    public async Task ВнешняяПравка_БезИзмененияЧислаСтрок_НеДоходитДоЛентыВообще()
    {
        // Ключевая находка: CountLineDiff (TurnFileWatcher.cs:176-181) считает только
        // newCount - oldCount. Правка, заменяющая содержимое строки без добавления/удаления
        // строк, даёт added=0 и removed=0 — OnFileSystemEvent возвращается на строке 113,
        // ДО вызова _onMessage. Карточка не приходит вообще, а не просто теряет External —
        // это и объясняет отчёт Веры (git-панель прирост увидела, лента — нет).
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllText(path, "line1\nline2\nline3");

        var received = new ConcurrentQueue<FileChangedMessage>();
        var signal = new SemaphoreSlim(0);
        var attributor = new FileChangeAttributor();
        using var watcher = CreateWatcher(_root, received, signal, attributor, ownerSessionId: "session-a");
        watcher.Start();
        await Task.Delay(200);

        // Прогрев кэша: без него oldContent=null и любое изменение даёт ненулевой diff —
        // это маскировало бы проверяемый сценарий.
        File.WriteAllText(path, "line1\nline2X\nline3");
        var warmup = await WaitForMessageAsync(signal, received, TimeSpan.FromSeconds(3));
        warmup.Should().NotBeNull("прогрев кэша обязателен — иначе тест ничего не проверяет");

        // Реальная проверка: меняем содержимое строки, число строк то же (3 -> 3)
        File.WriteAllText(path, "line1\nline2Y\nline3");
        var msg = await WaitForMessageAsync(signal, received, TimeSpan.FromMilliseconds(1500));

        msg.Should().BeNull(
            "CountLineDiff не видит правку без изменения числа строк — FileChangedMessage не уходит вовсе, " +
            "поэтому пометка «Изменение вне чата» для такой правки в принципе не может появиться");
    }

    [Fact]
    public async Task ОстановленныйВатчер_НеШлётСообщения()
    {
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllText(path, "line1");

        var received = new ConcurrentQueue<FileChangedMessage>();
        var signal = new SemaphoreSlim(0);
        var watcher = CreateWatcher(_root, received, signal);
        watcher.Start();
        await Task.Delay(200);
        watcher.Stop();

        File.WriteAllText(path, "line1\nline2");
        var msg = await WaitForMessageAsync(signal, received, TimeSpan.FromMilliseconds(800));

        msg.Should().BeNull();
        watcher.Dispose();
    }
}
