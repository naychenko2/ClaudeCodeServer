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
        // Карточка теперь приходит после retry-паузы атрибуции (~400мс + 1.5с) — ожидание
        // с запасом, CI-раннер слабее локального.
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllText(path, "a\nb");

        var received = new ConcurrentQueue<FileChangedMessage>();
        var signal = new SemaphoreSlim(0);
        var attributor = new FileChangeAttributor();
        using var watcher = CreateWatcher(_root, received, signal, attributor, ownerSessionId: "session-a");
        watcher.Start();
        await Task.Delay(200);

        File.WriteAllText(path, "a\nb\nc");
        var msg = await WaitForMessageAsync(signal, received, TimeSpan.FromSeconds(5));

        msg.Should().NotBeNull();
        msg!.External.Should().BeTrue("пути нет ни в чьих заявках — правка сделана не Edit/Write этой сессии");
    }

    [Fact]
    public async Task ЗаявкаДругойСессииОпоздала_КарточкаПодавленаПослеПерепроверки()
    {
        // Регресс на гонку параллельных ходов: файл правит чат A (Edit/Write), но его
        // заявка подтверждается по tool_result ПОЗЖЕ самого файла. Ватчер чата B на
        // момент debounce (400мс) заявок не видит — раньше сразу выносил вердикт
        // external=true, и правка A уезжала в ленту B как «Изменение вне чата».
        // Теперь кандидат на «вне чата» перепроверяется после паузы: опоздавшая чужая
        // заявка гасит карточку (её покажет собственный ватчер A).
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllText(path, "a\nb");

        var received = new ConcurrentQueue<FileChangedMessage>();
        var signal = new SemaphoreSlim(0);
        var attributor = new FileChangeAttributor();
        using var watcher = CreateWatcher(_root, received, signal, attributor, ownerSessionId: "session-b");
        watcher.Start();
        await Task.Delay(200);

        // Прогрев кэша: без него oldContent=null и added считается от нуля
        File.WriteAllText(path, "a\nb\nc");
        var warmup = await WaitForMessageAsync(signal, received, TimeSpan.FromSeconds(5));
        warmup.Should().NotBeNull("прогрев кэша обязателен — иначе тест ничего не проверяет");

        // Правка «чата A»: файл меняется, заявка A приходит ВНУТРИ retry-окна — уже
        // после debounce (400мс), но до перепроверки (~400мс + 1.5с)
        File.WriteAllText(path, "a\nb\nc\nd");
        await Task.Delay(700);
        attributor.Claim("session-a", path);

        var msg = await WaitForMessageAsync(signal, received, TimeSpan.FromSeconds(4));

        msg.Should().BeNull("заявка чужой сессии появилась в retry-окне — карточку в этом чате гасим, её покажет ватчер чата-источника");
    }

    [Fact]
    public async Task СвояЗаявкаОпоздала_КарточкаНеВнешняя()
    {
        // Вторая половина той же гонки: файл правит САМ этот чат, но tool_result (и
        // заявка) опаздывает относительно файла. После перепроверки заявка своя —
        // карточка уходит с external=false, а не «Изменение вне чата».
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllText(path, "a\nb");

        var received = new ConcurrentQueue<FileChangedMessage>();
        var signal = new SemaphoreSlim(0);
        var attributor = new FileChangeAttributor();
        using var watcher = CreateWatcher(_root, received, signal, attributor, ownerSessionId: "session-a");
        watcher.Start();
        await Task.Delay(200);

        File.WriteAllText(path, "a\nb\nc");
        var warmup = await WaitForMessageAsync(signal, received, TimeSpan.FromSeconds(5));
        warmup.Should().NotBeNull("прогрев кэша обязателен — иначе тест ничего не проверяет");

        File.WriteAllText(path, "a\nb\nc\nd");
        await Task.Delay(700);
        attributor.Claim("session-a", path);

        var msg = await WaitForMessageAsync(signal, received, TimeSpan.FromSeconds(4));

        msg.Should().NotBeNull("своя опоздавшая заявка не гасит карточку — это правка нашего хода");
        msg!.External.Should().BeFalse("заявка своей сессии появилась в retry-окне — правка сделана Edit/Write этого чата");
    }

    [Fact]
    public async Task ЗаменаСтроки_БезИзмененияЧислаСтрок_ДоходитДоЛентыСЧестнымиЧислами()
    {
        // Регресс на находку: раньше CountLineDiff считал только newCount - oldCount.
        // Правка, заменяющая содержимое строки без добавления/удаления строк, давала
        // added=0 и removed=0 — OnFileSystemEvent возвращался ДО вызова _onMessage,
        // карточка не приходила вообще. Теперь diff — мультисет по содержимому строк:
        // замена одной строки даёт 1 добавлена (новое содержимое) + 1 удалена (старое).
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllText(path, "line1\nline2\nline3");

        var received = new ConcurrentQueue<FileChangedMessage>();
        var signal = new SemaphoreSlim(0);
        var attributor = new FileChangeAttributor();
        using var watcher = CreateWatcher(_root, received, signal, attributor, ownerSessionId: "session-a");
        watcher.Start();
        await Task.Delay(200);

        // Прогрев кэша: без него oldContent=null и любое изменение даёт ненулевой diff —
        // это маскировало бы проверяемый сценарий. Обе карточки здесь с атрибутором,
        // т.е. идут через retry-паузу (~1.9с) — ожидания с запасом под CI.
        File.WriteAllText(path, "line1\nline2X\nline3");
        var warmup = await WaitForMessageAsync(signal, received, TimeSpan.FromSeconds(5));
        warmup.Should().NotBeNull("прогрев кэша обязателен — иначе тест ничего не проверяет");

        // Реальная проверка: меняем содержимое строки, число строк то же (3 -> 3)
        File.WriteAllText(path, "line1\nline2Y\nline3");
        var msg = await WaitForMessageAsync(signal, received, TimeSpan.FromSeconds(5));

        msg.Should().NotBeNull("замена содержимого строки — реальная правка, карточка обязана дойти");
        msg!.Added.Should().Be(1, "line2Y — новая строка, которой не было в старом содержимом");
        msg.Removed.Should().Be(1, "line2X — старая строка, которой нет в новом содержимом");
    }

    [Fact]
    public async Task ПравкаБезИзмененияСодержимого_НеДоходитДоЛенты()
    {
        // Условие раннего выхода теперь — точное равенство содержимого (oldContent ==
        // newContent), а не added==0 && removed==0 от diff-алгоритма. Перезапись файла
        // тем же текстом (touch/re-save без реальных изменений) по-прежнему не должна
        // порождать карточку.
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllText(path, "line1\nline2\nline3");

        var received = new ConcurrentQueue<FileChangedMessage>();
        var signal = new SemaphoreSlim(0);
        using var watcher = CreateWatcher(_root, received, signal);
        watcher.Start();
        await Task.Delay(200);

        // Прогрев кэша.
        File.WriteAllText(path, "line1\nline2X\nline3");
        var warmup = await WaitForMessageAsync(signal, received, TimeSpan.FromSeconds(3));
        warmup.Should().NotBeNull("прогрев кэша обязателен — иначе тест ничего не проверяет");

        // Перезапись тем же самым содержимым — реального изменения нет.
        File.WriteAllText(path, "line1\nline2X\nline3");
        var msg = await WaitForMessageAsync(signal, received, TimeSpan.FromMilliseconds(1500));

        msg.Should().BeNull("содержимое не изменилось — карточка не должна приходить");
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
