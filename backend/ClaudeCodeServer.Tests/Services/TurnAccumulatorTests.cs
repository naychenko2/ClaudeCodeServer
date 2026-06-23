using ClaudeCodeServer.Protocol;
using ClaudeCodeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeCodeServer.Tests.Services;

public class TurnAccumulatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ChatHistoryService _histSvc;

    public TurnAccumulatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "acc_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _histSvc = new ChatHistoryService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json")
            }).Build());
    }

    [Fact]
    public void GetAll_EmptyAccumulator_ReturnsEmpty()
    {
        var acc = new TurnAccumulator([]);
        acc.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void GetAll_WithPreloadedHistory_ReturnsHistory()
    {
        var history = new List<StoredMessage> { new StoredTextMessage("old") };
        var acc = new TurnAccumulator(history);
        acc.GetAll().Should().HaveCount(1);
    }

    [Fact]
    public void OnUserMessage_AddsToCurrentTurn()
    {
        var acc = new TurnAccumulator([]);
        acc.OnUserMessage("hello", []);
        var all = acc.GetAll();
        all.Should().HaveCount(1);
        all.OfType<StoredUserMessage>().Single().Text.Should().Be("hello");
    }

    [Fact]
    public void OnUserMessage_WithAttachments_StoresAttachments()
    {
        var acc = new TurnAccumulator([]);
        acc.OnUserMessage("text", ["file.txt", "other.cs"]);
        var msg = acc.GetAll().Single() as StoredUserMessage;
        msg!.AttachedPaths.Should().BeEquivalentTo(["file.txt", "other.cs"]);
    }

    [Fact]
    public void OnSessionStarted_AddsSessionStartedMessage()
    {
        var acc = new TurnAccumulator([]);
        acc.OnSessionStarted("claude-3", "auto");
        var started = acc.GetAll().OfType<StoredSessionStartedMessage>().Single();
        started.Model.Should().Be("claude-3");
        started.Mode.Should().Be("auto");
    }

    [Fact]
    public void OnTextDelta_Accumulates_FlushedByOnToolUse()
    {
        var acc = new TurnAccumulator([]);
        acc.OnTextDelta("hello ");
        acc.OnTextDelta("world");
        // буфер ещё не зафиксирован в ход, но виден в снимке как единый текст
        acc.GetAll().Should().ContainSingle()
            .Which.Should().BeOfType<StoredTextMessage>().Which.Text.Should().Be("hello world");

        // OnToolUse триггерит FlushBuffers
        acc.OnToolUse("t1", "bash", new { });
        var all = acc.GetAll();
        all.Should().HaveCount(2);
        all[0].Should().BeOfType<StoredTextMessage>().Which.Text.Should().Be("hello world");
        all[1].Should().BeOfType<StoredToolUseMessage>();
    }

    [Fact]
    public void OnThinkingDelta_Accumulates_FlushedByOnToolUse()
    {
        var acc = new TurnAccumulator([]);
        acc.OnThinkingDelta("I think ");
        acc.OnThinkingDelta("therefore");
        acc.OnToolUse("t1", "read", new { });

        var all = acc.GetAll();
        all[0].Should().BeOfType<StoredThinkingMessage>().Which.Text.Should().Be("I think therefore");
    }

    [Fact]
    public void OnToolResult_UpdatesPendingToolUse()
    {
        var acc = new TurnAccumulator([]);
        acc.OnToolUse("t1", "bash", new { });
        acc.OnToolResult("t1", "output here", false);

        var tool = acc.GetAll().OfType<StoredToolUseMessage>().Single();
        tool.Result.Should().Be("output here");
        tool.IsError.Should().BeFalse();
    }

    [Fact]
    public void OnToolResult_ErrorFlag_SetsIsError()
    {
        var acc = new TurnAccumulator([]);
        acc.OnToolUse("t1", "bash", null);
        acc.OnToolResult("t1", "error message", true);

        var tool = acc.GetAll().OfType<StoredToolUseMessage>().Single();
        tool.IsError.Should().BeTrue();
    }

    [Fact]
    public void OnFileChanged_AddsFileChangedMessage()
    {
        var acc = new TurnAccumulator([]);
        acc.OnFileChanged("src/file.cs", 10, 3);

        var changed = acc.GetAll().OfType<StoredFileChangedMessage>().Single();
        changed.Path.Should().Be("src/file.cs");
        changed.Added.Should().Be(10);
        changed.Removed.Should().Be(3);
    }

    [Fact]
    public async Task OnResultAsync_FlushesBuffersAndSavesToHistory()
    {
        var sessionId = Guid.NewGuid().ToString();
        var acc = new TurnAccumulator([], sessionId);
        acc.OnUserMessage("hi", []);
        acc.OnTextDelta("response text");

        await acc.OnResultAsync("success", 1000, 1, null, null, null, null, _histSvc);

        var loaded = await _histSvc.LoadAsync(sessionId);
        loaded.Should().HaveCount(3); // user + text + result
        loaded[0].Should().BeOfType<StoredUserMessage>();
        loaded[1].Should().BeOfType<StoredTextMessage>().Which.Text.Should().Be("response text");
        loaded[2].Should().BeOfType<StoredResultMessage>().Which.Subtype.Should().Be("success");
    }

    [Fact]
    public async Task OnErrorAsync_FlushesBuffersAndSavesToHistory()
    {
        var sessionId = Guid.NewGuid().ToString();
        var acc = new TurnAccumulator([], sessionId);
        acc.OnTextDelta("partial");

        await acc.OnErrorAsync("something went wrong", _histSvc);

        var loaded = await _histSvc.LoadAsync(sessionId);
        loaded.Should().HaveCount(2);
        loaded[0].Should().BeOfType<StoredTextMessage>();
        loaded[1].Should().BeOfType<StoredErrorMessage>().Which.Text.Should().Be("something went wrong");
    }

    [Fact]
    public void GetAll_CombinesOldHistoryAndCurrentTurn()
    {
        var history = new List<StoredMessage> { new StoredTextMessage("old") };
        var acc = new TurnAccumulator(history);
        acc.OnUserMessage("new", []);

        var all = acc.GetAll();
        all.Should().HaveCount(2);
        all[0].Should().BeOfType<StoredTextMessage>().Which.Text.Should().Be("old");
        all[1].Should().BeOfType<StoredUserMessage>().Which.Text.Should().Be("new");
    }

    [Fact]
    public async Task OnResultAsync_AfterFlush_CurrentTurnCleared()
    {
        var sessionId = Guid.NewGuid().ToString();
        var acc = new TurnAccumulator([], sessionId);
        acc.OnUserMessage("msg1", []);
        await acc.OnResultAsync("done", 500, 1, null, null, null, null, _histSvc);

        // второй тёрн
        acc.OnUserMessage("msg2", []);
        var all = acc.GetAll();
        // история (1 user + 1 result) + текущий (1 user) = 3
        all.Should().HaveCount(3);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
