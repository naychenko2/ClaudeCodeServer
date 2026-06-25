using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

public class ChatHistoryServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ChatHistoryService _sut;

    public ChatHistoryServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "chat_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _sut = new ChatHistoryService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json")
            }).Build());
    }

    [Fact]
    public async Task LoadAsync_NonExistentSession_ReturnsEmpty()
    {
        var result = await _sut.LoadAsync("nonexistent-session");
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_ReturnsSameMessages()
    {
        var sessionId = Guid.NewGuid().ToString();
        var messages = new List<StoredMessage>
        {
            new StoredUserMessage("hello", null),
            new StoredTextMessage("world")
        };

        await _sut.SaveAsync(sessionId, messages);
        var loaded = await _sut.LoadAsync(sessionId);

        loaded.Should().HaveCount(2);
        loaded[0].Should().BeOfType<StoredUserMessage>()
            .Which.Text.Should().Be("hello");
        loaded[1].Should().BeOfType<StoredTextMessage>()
            .Which.Text.Should().Be("world");
    }

    [Fact]
    public async Task SaveAsync_CreatesDirectoriesAutomatically()
    {
        var sessionId = "new-session-" + Guid.NewGuid();
        var messages = new List<StoredMessage> { new StoredTextMessage("test") };

        await _sut.SaveAsync(sessionId, messages);

        var path = Path.Combine(_tempDir, "sessions", sessionId, "history.json");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_CorruptedFile_ReturnsEmpty()
    {
        var sessionId = "corrupt-session";
        var dir = Path.Combine(_tempDir, "sessions", sessionId);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "history.json"), "NOT_VALID_JSON{{{{");

        var result = await _sut.LoadAsync(sessionId);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_OverwritesExisting()
    {
        var sessionId = Guid.NewGuid().ToString();
        var first = new List<StoredMessage> { new StoredTextMessage("first") };
        var second = new List<StoredMessage> { new StoredTextMessage("second") };

        await _sut.SaveAsync(sessionId, first);
        await _sut.SaveAsync(sessionId, second);

        var loaded = await _sut.LoadAsync(sessionId);
        loaded.Should().HaveCount(1);
        loaded[0].Should().BeOfType<StoredTextMessage>().Which.Text.Should().Be("second");
    }

    [Fact]
    public async Task SaveAndLoad_AllStoredMessageTypes_RoundTripsCorrectly()
    {
        var sessionId = Guid.NewGuid().ToString();
        var tool = new StoredToolUseMessage { Id = "t1", Name = "bash", Input = new { command = "ls" } };
        tool.Result = "file1.txt";
        tool.IsError = false;

        var messages = new List<StoredMessage>
        {
            new StoredUserMessage("prompt", ["file.txt"]),
            new StoredSessionStartedMessage("claude-3", "auto"),
            new StoredThinkingMessage("thinking..."),
            new StoredTextMessage("response"),
            tool,
            new StoredFileChangedMessage("src/file.cs", 5, 2),
            new StoredResultMessage("success", 1500, 1),
            new StoredErrorMessage("oops")
        };

        await _sut.SaveAsync(sessionId, messages);
        var loaded = await _sut.LoadAsync(sessionId);

        loaded.Should().HaveCount(8);
        loaded[0].Should().BeOfType<StoredUserMessage>().Which.AttachedPaths.Should().Contain("file.txt");
        loaded[1].Should().BeOfType<StoredSessionStartedMessage>().Which.Model.Should().Be("claude-3");
        loaded[2].Should().BeOfType<StoredThinkingMessage>().Which.Text.Should().Be("thinking...");
        loaded[3].Should().BeOfType<StoredTextMessage>();
        loaded[4].Should().BeOfType<StoredToolUseMessage>().Which.Result.Should().Be("file1.txt");
        loaded[5].Should().BeOfType<StoredFileChangedMessage>().Which.Added.Should().Be(5);
        loaded[6].Should().BeOfType<StoredResultMessage>().Which.DurationMs.Should().Be(1500);
        loaded[7].Should().BeOfType<StoredErrorMessage>().Which.Text.Should().Be("oops");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
