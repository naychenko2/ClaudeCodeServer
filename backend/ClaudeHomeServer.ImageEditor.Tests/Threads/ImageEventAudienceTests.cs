using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.ImageEditor.Threads;

// S2d-2b: image_thread_changed уходит по аудитории чата, а не всей группе проекта
public sealed class ImageEventAudienceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccs_image_aud_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<List<string>> BroadcastAsync(Session? session, Action<Session>? afterCreate = null)
    {
        var calls = new List<string>();
        var dir = new Mock<ISessionDirectory>();
        dir.Setup(d => d.GetById("c1")).Returns(session);
        var hub = new Mock<ISessionBroadcaster>();
        hub.Setup(h => h.ToOwner(It.IsAny<string>(), It.IsAny<ServerMessage>()))
            .Callback<string, ServerMessage>((u, _) => calls.Add("owner:" + u)).Returns(Task.CompletedTask);
        hub.Setup(h => h.ToProject(It.IsAny<string>(), It.IsAny<ServerMessage>()))
            .Callback<string, ServerMessage>((p, _) => calls.Add("project:" + p)).Returns(Task.CompletedTask);
        var svc = new ImageThreadService(new ImageThreadStore(Path.Combine(_dir, ImageThreadStore.DirName)),
            NullLogger<ImageThreadService>.Instance, dir.Object, null, hub.Object);
        // PrivateToUserId читается в момент отправки: меняем после создания сервиса
        if (session is not null) afterCreate?.Invoke(session);
        await svc.BroadcastAsync("owner", "p1", "c1", ImageThreadsState.Empty);
        return calls;
    }

    [Fact]
    public async Task Приватный_чат_только_автору() =>
        (await BroadcastAsync(new Session { Id = "c1", OwnerId = "owner", ProjectId = "p1", PrivateToUserId = "author" }))
            .Should().Equal("owner:author");

    [Fact]
    public async Task Общий_чат_группе_проекта() =>
        (await BroadcastAsync(new Session { Id = "c1", OwnerId = "owner", ProjectId = "p1" }))
            .Should().Equal("project:p1");

    [Fact]
    public async Task Приватность_читается_в_момент_отправки() =>
        (await BroadcastAsync(new Session { Id = "c1", OwnerId = "owner", ProjectId = "p1" }, s => s.PrivateToUserId = "author"))
            .Should().Equal("owner:author");

    [Fact]
    public async Task Чата_уже_нет_только_владельцу() =>
        (await BroadcastAsync(null)).Should().Equal("owner:owner");
}
