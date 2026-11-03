using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// S2d-6b-4: вложения при смене видимости чата. Загрузка и перенос идут под одним VisibilityLock,
// поэтому файл всегда лежит в раскладке, соответствующей флагу PrivateToUserId.
public partial class SessionManagerTests
{
    private string ProjectRootOf(Session s) => _projectManager.GetById(s.ProjectId!)!.RootPath;

    private string Upload(Session s, string userId, string name, string content = "x")
    {
        var root = ProjectRootOf(s);
        return _sut.SaveChatAttachment(s.Id, userId, name, rel =>
        {
            var full = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        })!;
    }

    [Fact]
    public async Task ВложениеДоПеревода_ПослеПриватного_ЛежитВПоддереве_АвторЧитает_УчастникНет()
    {
        var session = await SharedChatAsync("att-priv");
        var shared = Upload(session, TestUserId, "pic.png", "secret");
        var root = ProjectRootOf(session);
        File.Exists(Path.Combine(root, shared)).Should().BeTrue();

        var result = _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true);

        result.Outcome.Should().Be(ChatVisibilityOutcome.Ok);
        var moved = result.AttachmentMoves!.Should().ContainKey(shared).WhoseValue;
        File.Exists(Path.Combine(root, shared)).Should().BeFalse("общая раскладка не должна хранить содержимое приватного чата");
        File.ReadAllText(Path.Combine(root, moved)).Should().Be("secret");
        PrivateAttachments.IsForeign(moved, TestUserId).Should().BeFalse();
        PrivateAttachments.IsForeign(moved, Участник).Should().BeTrue();
        Directory.GetFiles(Path.Combine(root, ".cc-attachments"), "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine(".cc-attachments", "private"))).Should().BeEmpty("поиск участника пуст");
    }

    [Fact]
    public async Task СнятиеПриватности_ВложениеПереезжаетВОбщуюРаскладку()
    {
        var session = await SharedChatAsync("att-unpriv");
        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome.Should().Be(ChatVisibilityOutcome.Ok);
        var priv = Upload(session, TestUserId, "doc.txt", "body");
        priv.Should().StartWith(".cc-attachments/private/" + TestUserId + "/");

        var result = _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: false);

        result.Outcome.Should().Be(ChatVisibilityOutcome.Ok);
        var shared = result.AttachmentMoves!.Should().ContainKey(priv).WhoseValue;
        PrivateAttachments.IsForeign(shared, Участник).Should().BeFalse("участники читают общее вложение");
        File.ReadAllText(Path.Combine(ProjectRootOf(session), shared)).Should().Be("body");
        File.Exists(Path.Combine(ProjectRootOf(session), priv)).Should().BeFalse();
    }

    [Fact]
    public async Task ГонкаUploadИПереключения_НиОдинФайлНеВНевернойРаскладке()
    {
        for (var i = 0; i < 30; i++)
        {
            var session = await SharedChatAsync("att-race" + i);
            var paths = new System.Collections.Concurrent.ConcurrentBag<string>();
            using var barrier = new Barrier(2);
            var upload = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (var k = 0; k < 5; k++) paths.Add(Upload(session, TestUserId, $"f{k}.bin"));
            });
            var toggle = Task.Run(() =>
            {
                barrier.SignalAndWait();
                _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true);
            });
            await Task.WhenAll(upload, toggle);

            var root = ProjectRootOf(session);
            var priv = session.PrivateToUserId is not null;
            var onDisk = Directory.GetFiles(Path.Combine(root, ".cc-attachments"), "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).ToList();
            onDisk.Should().HaveCount(5);
            onDisk.Should().OnlyContain(f => PrivateAttachments.IsForeign(f, Участник) == priv,
                "раскладка каждого файла соответствует флагу видимости");
            session.Attachments.Should().BeEquivalentTo(onDisk, "учёт указывает на реальные пути");
        }
    }

    [Fact]
    public async Task ПереносНеУдался_ФлагНеСтавится_ФайлНаМесте()
    {
        var session = await SharedChatAsync("att-fail");
        var shared = Upload(session, TestUserId, "a.png");
        // Занимаем целевой путь каталогом-файлом: File.Move(overwrite:false) падает
        var target = PrivateAttachments.ToPrivate(TestUserId, shared)!;
        var full = Path.Combine(ProjectRootOf(session), target);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "occupied");

        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome
            .Should().Be(ChatVisibilityOutcome.AttachmentsMoveFailed);

        session.PrivateToUserId.Should().BeNull();
        File.Exists(Path.Combine(ProjectRootOf(session), shared)).Should().BeTrue();
    }
    // Эмуляция рестарта: sessions.json перечитывается заново, записи в памяти заменяются десериализованными.
    private void ReloadSessionsFromDisk() =>
        typeof(SessionManager).GetMethod("LoadSessions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_sut, null);

    [Fact]
    public async Task УчётВложения_ПереживаетРестарт_ПриватностьПослеНегоПереноситФайл()
    {
        var session = await SharedChatAsync("att-restart");
        var shared = Upload(session, TestUserId, "pic.png", "secret");
        var root = ProjectRootOf(session);

        // Автосейв по таймеру в тестах выключен: учёт обязан лечь на диск самим сохранением вложения
        ReloadSessionsFromDisk();

        var result = _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true);

        result.Outcome.Should().Be(ChatVisibilityOutcome.Ok);
        var moved = result.AttachmentMoves!.Should().ContainKey(shared).WhoseValue;
        File.Exists(Path.Combine(root, shared)).Should().BeFalse("в общей раскладке файла приватного чата быть не должно");
        File.ReadAllText(Path.Combine(root, moved)).Should().Be("secret");
        moved.Should().StartWith(".cc-attachments/private/" + TestUserId + "/");
    }

    private (string Private, string Key) PrivateChatWithMessage(Session session, bool accumulatorLive)
    {
        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome.Should().Be(ChatVisibilityOutcome.Ok);
        var priv = Upload(session, TestUserId, "doc.txt", "body");
        var entry = GetEntry(session.Id);
        var key = session.ClaudeSessionId ?? session.Id;
        var acc = new TurnAccumulator([], key);
        acc.OnUserMessage("смотри файл", [priv]);
        var flush = acc.GetAll();
        // Сообщение должно лежать в истории, а не только в текущем ходе: тот же вид, что после завершённого хода
        _historyService.SaveAsync(key, flush).GetAwaiter().GetResult();
        entry.GetType().GetField("Accumulator")!.SetValue(entry,
            accumulatorLive ? new TurnAccumulator(_historyService.LoadAsync(key).GetAwaiter().GetResult(), key) : null);
        return (priv, key);
    }

    // Поставить сообщение с вложением в очередь занятого чата — тем же путём, что «честная
    // очередь» пользователя (SendMessageAsync по занятому чату → EnqueuePendingAsync).
    private async Task EnqueueWithAttachmentAsync(Session session, string text, IReadOnlyList<string> paths)
    {
        session.Status = SessionStatus.Working;
        var outcome = await _sut.SendMessageAsync(session.Id, text, paths, senderUserId: TestUserId);
        outcome.Should().Be(SessionManager.SendUserOutcome.Queued, "занятый чат: сообщение встаёт в очередь");
    }

    // Файл в рабочей папке проекта по относительному пути (для чужого пути вне .cc-attachments).
    private void WriteProjectFile(Session session, string rel, string content)
    {
        var full = Path.Combine(ProjectRootOf(session), rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // S2d-6b-7: очередь отложенных сообщений едет за файлами так же, как лента. Без переписи
    // отложенное сообщение ушло бы по старому private/… и AttachmentInliner молча пропустил бы
    // перенесённый файл — вложение терялось бы для автора и участников общего чата.
    [Fact]
    public async Task СнятиеПриватности_ПутиВОчередиПереписаны_ФайлыПоНимЧитаются()
    {
        var session = await SharedChatAsync("att-queue");
        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome.Should().Be(ChatVisibilityOutcome.Ok);
        var priv = Upload(session, TestUserId, "doc.txt", "body");
        await EnqueueWithAttachmentAsync(session, "посмотри файл", [priv]);

        var result = _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: false);

        result.Outcome.Should().Be(ChatVisibilityOutcome.Ok);
        var shared = result.AttachmentMoves!.Should().ContainKey(priv).WhoseValue;
        var root = ProjectRootOf(session);
        var queued = _sut.GetPending(session.Id).Should().ContainSingle().Subject;
        queued.AttachedPaths.Should().BeEquivalentTo(new[] { shared }, "отложенное сообщение ссылается на новый путь");
        File.ReadAllText(Path.Combine(root, queued.AttachedPaths![0])).Should().Be("body");
        PrivateAttachments.IsForeign(queued.AttachedPaths[0], Участник).Should().BeFalse("участник читает общее вложение");
        // Снимок на диске — тот же: перепись переживает рестарт (её читает RestorePending)
        _historyService.LoadPending(session.Id)!.Items.Should().ContainSingle().Subject
            .AttachedPaths.Should().BeEquivalentTo(new[] { shared }, "pending.json переписан вместе с памятью");
    }

    // Контракт карты: меняются только пути из attachmentMoves, порядок списка не трогается.
    [Fact]
    public async Task СнятиеПриватности_ЧужойПутьИПорядокВСообщенииСохраняются()
    {
        var session = await SharedChatAsync("att-queue-foreign");
        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome.Should().Be(ChatVisibilityOutcome.Ok);
        var priv = Upload(session, TestUserId, "doc.txt", "body");
        WriteProjectFile(session, "docs/note.md", "чужое");
        await EnqueueWithAttachmentAsync(session, "файл и заметка", [priv, "docs/note.md"]);

        var result = _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: false);

        result.Outcome.Should().Be(ChatVisibilityOutcome.Ok);
        var shared = result.AttachmentMoves!.Should().ContainKey(priv).WhoseValue;
        var queued = _sut.GetPending(session.Id).Should().ContainSingle().Subject;
        queued.AttachedPaths.Should().Equal(new[] { shared, "docs/note.md" },
            "чужой путь не тронут, порядок списка сохранён");
        File.ReadAllText(Path.Combine(ProjectRootOf(session), "docs/note.md")).Should().Be("чужое");
    }

    // Порядок «перенос → перепись»: не удалось перенести файлы — очередь остаётся как была,
    // вместе с флагом (состояние целое, путь в нём резолвится).
    [Fact]
    public async Task ПереносНеУдался_ОчередьНеМеняется_ФлагОстаётсяПриватным()
    {
        var session = await SharedChatAsync("att-queue-fail");
        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome.Should().Be(ChatVisibilityOutcome.Ok);
        var priv = Upload(session, TestUserId, "doc.txt", "body");
        await EnqueueWithAttachmentAsync(session, "посмотри файл", [priv]);
        // Занимаем целевой общий путь: File.Move(overwrite:false) падает
        var target = Path.Combine(ProjectRootOf(session), PrivateAttachments.ToShared(priv)!);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "occupied");

        _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: false).Outcome
            .Should().Be(ChatVisibilityOutcome.AttachmentsMoveFailed);

        var queued = _sut.GetPending(session.Id).Should().ContainSingle().Subject;
        queued.AttachedPaths.Should().Equal(new[] { priv }, "отказ переноса не трогает очередь");
        session.PrivateToUserId.Should().Be(TestUserId, "флаг не снимается");
        File.ReadAllText(Path.Combine(ProjectRootOf(session), priv)).Should().Be("body");
    }

    // Гонка (S2d-6b-7): снятие приватности и постановка сообщения в очередь идут параллельно.
    // Перепись — под PendingLock, постановка — под тем же замком: ни одно сообщение не теряется
    // и не дублируется, порядок очереди сохраняется, каждый путь очереди резолвится. Чужой путь
    // второго сообщения вне .cc-attachments — чтобы гонка не рвала assertion на переносимом.
    [Fact]
    public async Task ГонкаСнятиеПриватностиИПостановкаВОчередь_НичегоНеТеряется_ПутиРезолвятся()
    {
        for (var i = 0; i < 10; i++)
        {
            var session = await SharedChatAsync("att-queue-race" + i);
            _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: true).Outcome.Should().Be(ChatVisibilityOutcome.Ok);
            var priv = Upload(session, TestUserId, $"d{i}.txt", "body");
            var foreign = $"docs/n{i}.md";
            WriteProjectFile(session, foreign, "чужое");
            var id = session.Id;
            var root = ProjectRootOf(session);
            await EnqueueWithAttachmentAsync(session, "первое", [priv]);

            using var go = new ManualResetEventSlim(false);
            var enqueue = Task.Run(async () =>
            {
                go.Wait();
                return await _sut.SendMessageAsync(id, "второе", [foreign], senderUserId: TestUserId);
            });
            var toggle = Task.Run(() => { go.Wait(); return _sut.SetChatVisibility(id, TestUserId, makePrivate: false); });
            go.Set();
            var sent = await enqueue.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await toggle.WaitAsync(TimeSpan.FromSeconds(10));

            sent.Should().Be(SessionManager.SendUserOutcome.Queued, $"итерация {i}");
            result.Outcome.Should().Be(ChatVisibilityOutcome.Ok, $"итерация {i}");
            var moved = result.AttachmentMoves!.Should().ContainKey(priv).WhoseValue;

            var queued = _sut.GetPending(id);
            queued.Should().HaveCount(2, $"итерация {i}: ни одно сообщение не потеряно и не задублировано");
            queued[0].Text.Should().Be("первое", "порядок очереди не изменился");
            queued[1].Text.Should().Be("второе");
            queued[0].AttachedPaths.Should().Equal(new[] { moved }, $"итерация {i}: стоявшее в очереди переписано");
            queued[1].AttachedPaths.Should().Equal(new[] { foreign }, "чужой путь не тронут");
            foreach (var p in queued.SelectMany(q => q.AttachedPaths!))
                File.Exists(Path.Combine(root, p)).Should().BeTrue($"итерация {i}: {p} не резолвится");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task СнятиеПриватности_ПутиВСообщенияхПереписаны_ФайлыПоНимЧитаются(bool accumulatorLive)
    {
        var session = await SharedChatAsync("att-paths" + accumulatorLive);
        var (priv, key) = PrivateChatWithMessage(session, accumulatorLive);

        var result = _sut.SetChatVisibility(session.Id, TestUserId, makePrivate: false);

        result.Outcome.Should().Be(ChatVisibilityOutcome.Ok);
        var shared = result.AttachmentMoves!.Should().ContainKey(priv).WhoseValue;
        var root = ProjectRootOf(session);
        var fromDisk = (await _historyService.LoadAsync(key)).OfType<StoredUserMessage>().Single();
        fromDisk.AttachedPaths.Should().BeEquivalentTo(new[] { shared }, "history.json ссылается на новый путь");
        PrivateAttachments.IsForeign(fromDisk.AttachedPaths![0], Участник).Should().BeFalse("участник читает общее вложение");
        File.ReadAllText(Path.Combine(root, fromDisk.AttachedPaths[0])).Should().Be("body");
        if (accumulatorLive)
        {
            var acc = (TurnAccumulator)GetEntry(session.Id).GetType().GetField("Accumulator")!.GetValue(GetEntry(session.Id))!;
            acc.GetAll().OfType<StoredUserMessage>().Single().AttachedPaths.Should().BeEquivalentTo(new[] { shared }, "аккумулятор в памяти тоже переписан");
        }
    }
}
