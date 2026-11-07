using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.ImageEditor.Threads;

// F2-P2f: шов кадров разводит субъектов. Нить лежит у арендатора (ключ хранилищ), а файл проекта у нити по файлу
// читает вызывающий: чужое private (владельца или другого участника) не отдаётся никому, своё — отдаётся
public sealed class ImageFrameSourcePrivateTests : IDisposable
{
    private const string Tenant = "tenant";
    private const string Member = "member";
    private const string Other = "other";
    private const string Chat = "chat-1";
    private const string ProjectId = "p-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "frame-src-" + Guid.NewGuid().ToString("N"));
    private readonly ImageThreadStore _store;
    private readonly ImageFrameSource _source;
    private readonly Dictionary<string, string> _threadByFile = [];

    public ImageFrameSourcePrivateTests()
    {
        var project = Path.Combine(_root, "project");
        _store = new ImageThreadStore(Path.Combine(_root, "threads"));
        var session = new Session { Id = Chat, OwnerId = Tenant, ProjectId = ProjectId };
        var directory = new Mock<ISessionDirectory>();
        directory.Setup(d => d.GetById(Chat)).Returns(session);
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById(ProjectId)).Returns(new Project { Id = ProjectId, OwnerId = Tenant, RootPath = project });
        _source = new ImageFrameSource(_store,
            new ImageThreadService(_store, NullLogger<ImageThreadService>.Instance, directory.Object),
            projects.Object, directory.Object);

        foreach (var who in new[] { Tenant, Member, Other })
        {
            var rel = $".cc-attachments/private/{who}/g1/pic.png";
            Directory.CreateDirectory(Path.Combine(project, ".cc-attachments", "private", who, "g1"));
            File.WriteAllBytes(Path.Combine(project, rel), [1, 2, 3]);
            var revision = _store.Get(Tenant, Chat).Revision;
            _threadByFile[who] = _store.Open(Tenant, Chat, rel, null, revision).Thread!.Id;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private Task<ImageFrame?> Read(string? actor, string ofWho) =>
        _source.GetAsync(Tenant, actor, _threadByFile[ofWho], ImageThreadVersion.OriginId, default);

    [Theory]
    [InlineData(Member, Member)]
    [InlineData(Tenant, Tenant)]
    public async Task Свой_private_читается_вызывающему(string actor, string file) =>
        (await Read(actor, file)).Should().NotBeNull();

    [Theory]
    [InlineData(Member, Tenant)]
    [InlineData(Member, Other)]
    [InlineData(Tenant, Member)]
    [InlineData(null, Member)]
    public async Task Чужое_private_не_читается_ни_участнику_ни_арендатору(string? actor, string file) =>
        (await Read(actor, file)).Should().BeNull();
}
