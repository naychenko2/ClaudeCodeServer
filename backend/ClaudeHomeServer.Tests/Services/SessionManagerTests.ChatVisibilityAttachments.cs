using ClaudeHomeServer.Models;
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
}
