using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Проверка формы правки A′: публичный API NotesAiService не меняется, но личный слот
// владельца пробивается в вызов cheap.RunAsync (ownerId == userId). Фейк раннера фиксирует
// полученный ownerId — этого достаточно: правка механическая, остальные вызовы ловит компилятор.
public class NotesAiConsumerTests : IDisposable
{
    private const string User = "u1";
    private readonly string _dir;
    private readonly NotesService _notes;

    public NotesAiConsumerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "notes_ai_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, "notes", User));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(config, new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projects = new ProjectManager(config, users, appSettings);
        _notes = new NotesService(projects, config, NullLogger<NotesService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // Перехватывающий раннер: отвечает пустым JSON-массивом и фиксирует ownerId вызова.
    private sealed class CapturingRunner : ICheapTextRunner
    {
        public string? LastOwnerId;
        public bool UsesLocal(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "claude";

        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            LastOwnerId = ownerId;
            return Task.FromResult("[]");
        }
        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null,
            object? jsonFormat = null, CancellationToken ct = default)
        {
            LastOwnerId = ownerId;
            return Task.FromResult(new OneShotResult("[]", null, 0));
        }
    }

    [Fact]
    public async Task SuggestTags_ПробрасываетOwnerIdВладельцаВРаннер()
    {
        var note = _notes.Create(User, new CreateNoteRequest("Заметка", "содержимое про бассейны и хлор", "personal"));
        var runner = new CapturingRunner();
        var sut = new NotesAiService(_notes, new ConfigurationBuilder().Build(), runner);

        await sut.SuggestTagsAsync(User, note.Id, CancellationToken.None);

        Assert.Equal(User, runner.LastOwnerId);
    }
}
