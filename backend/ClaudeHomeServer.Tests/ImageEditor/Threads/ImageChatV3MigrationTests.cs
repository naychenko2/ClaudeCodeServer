using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Backup;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.ImageEditor.Characters;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.ImageEditor.Threads;

// Миграция чатов картинки v2 на v3 (ADR-019, решение 2): чат уходит в архив без сдвига
// UpdatedAt, получает маркер MigratedAt, повторный прогон ничего не меняет, чат, который человек
// вернул из архива, повторно не архивируется, обычные чаты не трогаются, схема бэкапа та же.
public class ImageChatV3MigrationTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly string _projectId;
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));

    public ImageChatV3MigrationTests()
    {
        (_projectId, _) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.TestUsername);
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();
    private string SessionsFile => Path.Combine(_factory.TempDir, "sessions.json");

    private ImageChatV3Migration Migration() => new(Sessions, NullLogger<ImageChatV3Migration>.Instance, _time);

    private async Task<Session> ImageChat(string? path = "images/hero.png")
    {
        var chat = await Sessions.CreateAsync(_projectId, ClaudeMode.AcceptEdits, name: "hero.png · правка");
        var live = Sessions.GetById(chat.Id)!;
        live.ImageChat = new SessionImageChat { CurrentPath = path };
        live.UpdatedAt = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
        Sessions.SaveSessions();
        return live;
    }

    [Fact]
    public async Task Чат_картинки_уходит_в_архив_без_сдвига_UpdatedAt_и_с_маркером()
    {
        var chat = await ImageChat();
        var plain = await Sessions.CreateAsync(_projectId, ClaudeMode.AcceptEdits, name: "Обычный чат");
        var updatedAt = chat.UpdatedAt;

        Migration().Run().Should().Be(1);

        var after = Sessions.GetById(chat.Id)!;
        after.IsArchived.Should().BeTrue();
        after.UpdatedAt.Should().Be(updatedAt, "архивация — не активность");
        after.ArchivedAt.Should().Be(_time.GetUtcNow().UtcDateTime);
        after.ArchivedBy.Should().Be(SessionManager.LegacyImageChatArchivedBy);
        after.ImageChat!.MigratedAt.Should().Be(_time.GetUtcNow().UtcDateTime);
        after.ImageChat.CurrentPath.Should().Be("images/hero.png", "привязка v2 читается и дальше");
        Sessions.GetById(plain.Id)!.IsArchived.Should().BeFalse("обычный чат миграция не трогает");

        // На диске то же самое: миграция пережила бы рестарт
        using var doc = JsonDocument.Parse(File.ReadAllText(SessionsFile));
        doc.RootElement.ToString().Should().Contain("MigratedAt");
    }

    [Fact]
    public async Task Повторный_прогон_ничего_не_меняет()
    {
        var chat = await ImageChat();
        Migration().Run();
        var fileAfterFirst = File.ReadAllText(SessionsFile);
        var archivedAt = Sessions.GetById(chat.Id)!.ArchivedAt;
        _time.Advance(TimeSpan.FromHours(3));

        Migration().Run().Should().Be(0);

        File.ReadAllText(SessionsFile).Should().Be(fileAfterFirst, "второй прогон sessions.json не переписывает");
        Sessions.GetById(chat.Id)!.ArchivedAt.Should().Be(archivedAt);
    }

    [Fact]
    public async Task Возвращённый_человеком_чат_повторно_не_архивируется()
    {
        var chat = await ImageChat();
        Migration().Run();
        Sessions.SetArchived(chat.Id, archived: false, by: "user");

        Migration().Run().Should().Be(0);

        Sessions.GetById(chat.Id)!.IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task Уже_архивный_чат_получает_только_маркер()
    {
        var chat = await ImageChat();
        Sessions.SetArchived(chat.Id, archived: true, by: "user");
        var archivedAt = Sessions.GetById(chat.Id)!.ArchivedAt;

        Migration().Run().Should().Be(1);

        var after = Sessions.GetById(chat.Id)!;
        after.ArchivedAt.Should().Be(archivedAt);
        after.ArchivedBy.Should().Be("user");
        after.ImageChat!.MigratedAt.Should().NotBeNull();
    }

    [Fact]
    public void Схема_бэкапа_не_растёт() =>
        BackupSchema.Version.Should().Be(9, "маркер MigratedAt аддитивный, формат файлов тот же");

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
