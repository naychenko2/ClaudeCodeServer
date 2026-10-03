using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChatContext;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using static ClaudeHomeServer.Tests.Services.ChatContext.ChatContextTestData;

namespace ClaudeHomeServer.Tests.Services.ChatContext;

public class ChatContextStoreTests : IDisposable
{
    private readonly string _root = TempRoot();
    private readonly RecordingNotifier _notifier = new();

    private ChatContextStore Store(params string[] kinds) =>
        new(_root, Registry(kinds.Length == 0 ? ["image", "audio", "image-character", "project-file"] : kinds), _notifier);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void Get_Missing_ReturnsEmptyRevisionZero()
    {
        var state = Store().Get("u1", "s1");
        state.Revision.Should().Be(0);
        state.Primary.Should().BeNull();
        state.Refs.Should().BeEmpty();
    }

    [Fact]
    public void Write_RaisesRevision_AndPersistsAcrossInstances()
    {
        var store = Store();
        var a = store.SetPrimary("u1", "s1", Item("image", "a"), 0);
        a.Revision.Should().Be(1);
        var b = store.AddRef("u1", "s1", Item("project-file", "x.md"), 1);
        b.Revision.Should().Be(2);

        var reread = Store().Get("u1", "s1");
        reread.Revision.Should().Be(2);
        reread.Primary!.Ref["key"]!.GetValue<string>().Should().Be("a");
        reread.Refs.Should().HaveCount(1);
        _notifier.Calls.Select(c => c.Revision).Should().Equal(1, 2);
    }

    [Fact]
    public void StaleRevision_ThrowsConflict_WithCurrentState()
    {
        var store = Store();
        store.SetPrimary("u1", "s1", Item("image", "a"), 0);

        var act = () => store.AddRef("u1", "s1", Item("project-file", "x"), 0);

        var ex = act.Should().Throw<ChatContextConflictException>().Which;
        ex.Code.Should().Be(ChatContextErrors.ContextChanged);
        ex.Current.Revision.Should().Be(1);
        store.Get("u1", "s1").Refs.Should().BeEmpty();
    }

    [Fact]
    public void NullRevision_SkipsCheck()
    {
        var store = Store();
        store.SetPrimary("u1", "s1", Item("image", "a"), null);
        store.AddRef("u1", "s1", Item("project-file", "x"), null).Revision.Should().Be(2);
    }

    [Fact]
    public void SetPrimary_ReplacesPrimary_AcrossKinds()
    {
        var store = Store();
        store.SetPrimary("u1", "s1", Item("image", "pic"), null);
        var state = store.SetPrimary("u1", "s1", Item("audio", "snd"), null);
        state.Primary!.Kind.Should().Be("audio");
    }

    [Fact]
    public void SetPrimary_RemovesSameObjectFromRefs()
    {
        var store = Store();
        store.AddRef("u1", "s1", Item("image", "pic", "style"), null);
        store.AddRef("u1", "s1", Item("image", "other", "style"), null);

        var state = store.SetPrimary("u1", "s1", Item("image", "pic"), null);

        state.Primary!.Ref["key"]!.GetValue<string>().Should().Be("pic");
        state.Refs.Select(r => r.Ref["key"]!.GetValue<string>()).Should().Equal("other");
    }

    [Fact]
    public void SetPrimary_SameObjectChosenByHuman_TurnsAgentIntoHuman()
    {
        var store = Store();
        store.SetPrimary("u1", "s1", Item("image", "pic", by: ContextActor.Agent), null);

        var state = store.SetPrimary("u1", "s1", Item("image", "pic", by: ContextActor.Human), null);

        state.Primary!.By.Should().Be(ContextActor.Human);
        state.Revision.Should().Be(2);
        _notifier.Calls.Select(c => c.Revision).Should().Equal(1, 2);
        store.Get("u1", "s1").Primary!.By.Should().Be(ContextActor.Human);
    }

    [Fact]
    public void SetPrimary_SameObjectSameActor_IsNoop()
    {
        var store = Store();
        var before = store.SetPrimary("u1", "s1", Item("image", "pic"), null);
        var after = store.SetPrimary("u1", "s1", Item("image", "pic"), null);
        after.Revision.Should().Be(before.Revision);
        _notifier.Calls.Should().HaveCount(1);
    }

    [Fact]
    public void AddRef_ObjectAlreadyPrimary_IsNoop()
    {
        var store = Store();
        var before = store.SetPrimary("u1", "s1", Item("image", "pic"), null);
        var after = store.AddRef("u1", "s1", Item("image", "pic", "style"), null);
        after.Revision.Should().Be(before.Revision);
        after.Refs.Should().BeEmpty();
    }

    [Fact]
    public void SetPrimary_Null_ClearsPrimary_KeepsRefs()
    {
        var store = Store();
        store.SetPrimary("u1", "s1", Item("image", "pic"), null);
        store.AddRef("u1", "s1", Item("project-file", "x"), null);
        var state = store.SetPrimary("u1", "s1", null, null);
        state.Primary.Should().BeNull();
        state.Refs.Should().HaveCount(1);
    }

    [Fact]
    public void AddRef_SeventeenthRef_IsRejected_WithRefsLimit()
    {
        var store = Store();
        for (var i = 0; i < ChatContextErrors.MaxRefs; i++)
            store.AddRef("u1", "s1", Item("project-file", $"f{i}"), null);

        var act = () => store.AddRef("u1", "s1", Item("project-file", "extra"), null);

        act.Should().Throw<ChatContextException>().Which.Code.Should().Be(ChatContextErrors.RefsLimit);
        store.Get("u1", "s1").Refs.Should().HaveCount(ChatContextErrors.MaxRefs);
    }

    [Fact]
    public void AddRef_DuplicateKindRefRole_IsNotAdded_ButOtherRoleIs()
    {
        var store = Store();
        store.AddRef("u1", "s1", Item("image", "pic", "style"), null);
        var dup = store.AddRef("u1", "s1", Item("image", "pic", "style"), null);
        dup.Refs.Should().HaveCount(1);
        dup.Revision.Should().Be(1);

        store.AddRef("u1", "s1", Item("image", "pic", "object"), null).Refs.Should().HaveCount(2);
    }

    [Fact]
    public void UnknownKind_IsRejected_WithKindUnknown()
    {
        var store = Store("image");
        var act = () => store.AddRef("u1", "s1", Item("audio", "x"), null);
        act.Should().Throw<ChatContextException>().Which.Code.Should().Be(ChatContextErrors.KindUnknown);
        var act2 = () => store.SetPrimary("u1", "s1", Item("audio", "x"), null);
        act2.Should().Throw<ChatContextException>().Which.Code.Should().Be(ChatContextErrors.KindUnknown);
    }

    [Fact]
    public void RemoveRef_RemovesById_UnknownIdIsNoop()
    {
        var store = Store();
        store.AddRef("u1", "s1", Item("project-file", "a", id: "i1"), null);
        store.AddRef("u1", "s1", Item("project-file", "b", id: "i2"), null);

        var state = store.RemoveRef("u1", "s1", "i1", null);
        state.Refs.Select(r => r.Id).Should().Equal("i2");

        store.RemoveRef("u1", "s1", "nope", null).Revision.Should().Be(state.Revision);
    }

    [Fact]
    public void Clear_EmptiesEverything_AndSecondClearIsNoop()
    {
        var store = Store();
        store.SetPrimary("u1", "s1", Item("image", "pic"), null);
        store.AddRef("u1", "s1", Item("project-file", "x"), null);

        var cleared = store.Clear("u1", "s1", null);
        cleared.Primary.Should().BeNull();
        cleared.Refs.Should().BeEmpty();
        store.Clear("u1", "s1", null).Revision.Should().Be(cleared.Revision);
    }

    [Fact]
    public void Forget_RemovesMatching_WithoutRevisionCheck()
    {
        var store = Store();
        store.SetPrimary("u1", "s1", Item("image", "pic"), null);
        store.AddRef("u1", "s1", Item("project-file", "keep"), null);
        store.AddRef("u1", "s1", Item("project-file", "gone"), null);

        store.Forget("u1", "s1", i => i.Ref["key"]!.GetValue<string>() is "pic" or "gone");

        var state = store.Get("u1", "s1");
        state.Primary.Should().BeNull();
        state.Refs.Select(r => r.Ref["key"]!.GetValue<string>()).Should().Equal("keep");
        _notifier.Calls.Last().Revision.Should().Be(state.Revision);
    }

    [Fact]
    public void Forget_NothingMatches_DoesNotWriteOrNotify()
    {
        var store = Store();
        store.AddRef("u1", "s1", Item("project-file", "a"), null);
        var calls = _notifier.Calls.Count;
        store.Forget("u1", "s1", _ => false);
        _notifier.Calls.Should().HaveCount(calls);
    }

    [Fact]
    public void File_IsKeyedByOwner_OtherOwnerDoesNotSeeIt()
    {
        var store = Store();
        store.SetPrimary("owner-a", "s1", Item("image", "pic"), null);

        store.Get("owner-b", "s1").Primary.Should().BeNull();
        File.Exists(Path.Combine(_root, "owner-a", "s1.json")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "owner-b", "s1.json")).Should().BeFalse();
    }

    [Theory]
    [InlineData("..", "s1")]
    [InlineData("u1", "../x")]
    [InlineData("u/1", "s1")]
    public void PathSegments_WithTraversal_AreRejected(string owner, string session)
    {
        var act = () => Store().Get(owner, session);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Notifier_FailureDoesNotBreakWrite()
    {
        var failing = new Mock<IChatContextNotifier>();
        failing.Setup(n => n.Changed(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ChatContextState>()))
            .Throws(new InvalidOperationException("boom"));
        var store = new ChatContextStore(_root, Registry("image"), failing.Object);

        var state = store.SetPrimary("u1", "s1", Item("image", "pic"), null);

        state.Revision.Should().Be(1);
        await Task.CompletedTask;
    }

    // Контекст — настройка чата: запись не трогает Session (UpdatedAt, архив) и не пишет ничего,
    // кроме своего каталога. Рассылка идёт через настоящий ChatContextBroadcaster, чтобы и его путь
    // оказался под проверкой.
    [Fact]
    public async Task Writes_DoNotMoveSessionUpdatedAt_OrArchive()
    {
        var updated = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var session = new Session { Id = "s1", UpdatedAt = updated, ArchivedAt = updated.AddHours(1) };
        var archivedBefore = session.IsArchived;

        var sessions = new Mock<ISessionDirectory>();
        sessions.Setup(s => s.GetById("s1")).Returns(session);
        var projects = new Mock<IProjectManager>();
        var sent = new TaskCompletionSource<ServerMessage>();
        var hub = new Mock<ISessionBroadcaster>();
        hub.Setup(h => h.ToOwner("u1", It.IsAny<ServerMessage>()))
            .Callback<string, ServerMessage>((_, m) => sent.TrySetResult(m))
            .Returns(Task.CompletedTask);

        var registry = Registry("image", "project-file");
        var broadcaster = new ChatContextBroadcaster(registry, sessions.Object, projects.Object, hub.Object,
            NullLogger<ChatContextBroadcaster>.Instance);
        var store = new ChatContextStore(_root, registry, broadcaster);

        store.SetPrimary("u1", "s1", Item("image", "pic"), null);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        store.AddRef("u1", "s1", Item("project-file", "x"), null);
        store.Clear("u1", "s1", null);

        session.UpdatedAt.Should().Be(updated);
        session.IsArchived.Should().Be(archivedBefore);
        Directory.GetFileSystemEntries(_root).Select(Path.GetFileName).Should().Equal("u1");
    }
}
