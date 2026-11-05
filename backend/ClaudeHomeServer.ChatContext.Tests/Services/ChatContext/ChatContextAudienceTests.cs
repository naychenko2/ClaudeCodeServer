using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services.ChatContext;

// S2d-2b: chat_context_changed уходит по аудитории чата, а не всей группе проекта
public class ChatContextAudienceTests
{
    private static async Task<(List<string> Calls, TaskCompletionSource Done)> RunAsync(Session session)
    {
        var calls = new List<string>();
        var done = new TaskCompletionSource();
        var sessions = new Mock<ISessionDirectory>();
        sessions.Setup(s => s.GetById(session.Id)).Returns(session);
        var hub = new Mock<ISessionBroadcaster>();
        hub.Setup(h => h.ToOwner(It.IsAny<string>(), It.IsAny<ServerMessage>()))
            .Callback<string, ServerMessage>((u, _) => { calls.Add("owner:" + u); done.TrySetResult(); })
            .Returns(Task.CompletedTask);
        hub.Setup(h => h.ToProject(It.IsAny<string>(), It.IsAny<ServerMessage>()))
            .Callback<string, ServerMessage>((p, _) => { calls.Add("project:" + p); done.TrySetResult(); })
            .Returns(Task.CompletedTask);
        var b = new ChatContextBroadcaster(sessions.Object, hub.Object, NullLogger<ChatContextBroadcaster>.Instance);
        b.Changed("owner", session.Id, new ChatContextState(1, null, []));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return (calls, done);
    }

    [Fact]
    public async Task PrivateChat_GoesOnlyToAuthor()
    {
        var (calls, _) = await RunAsync(new Session { Id = "s1", OwnerId = "owner", ProjectId = "p1", PrivateToUserId = "author" });
        calls.Should().Equal("owner:author");
    }

    [Fact]
    public async Task SharedChat_GoesToProjectGroup()
    {
        var (calls, _) = await RunAsync(new Session { Id = "s1", OwnerId = "owner", ProjectId = "p1" });
        calls.Should().Equal("project:p1");
    }

    [Fact]
    public async Task ChatOutsideProject_GoesToOwner()
    {
        var (calls, _) = await RunAsync(new Session { Id = "s1", OwnerId = "owner" });
        calls.Should().Equal("owner:owner");
    }
}
