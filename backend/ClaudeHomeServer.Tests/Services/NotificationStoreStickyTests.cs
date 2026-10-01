using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Закреплённое уведомление: флаг доходит до элемента списка и переживает перечитывание
// с диска; у старых записей (без поля) — false.
public class NotificationStoreStickyTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "notif_sticky_tests_" + Guid.NewGuid().ToString("N"));

    public NotificationStoreStickyTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private NotificationStore Create() => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_tempDir, "projects.json") })
            .Build(),
        NullLogger<NotificationStore>.Instance);

    [Fact]
    public async Task Sticky_RoundTripsThroughDisk_DefaultFalse()
    {
        var store = Create();
        var sticky = await store.AddAsync("u1", new CreateNotificationRequest { Title = "CLI", Sticky = true });
        var plain = await store.AddAsync("u1", new CreateNotificationRequest { Title = "обычное" });

        sticky.Sticky.Should().BeTrue();
        plain.Sticky.Should().BeFalse();

        var reread = await Create().GetListAsync("u1");
        reread.Single(n => n.Id == sticky.Id).Sticky.Should().BeTrue();
        reread.Single(n => n.Id == plain.Id).Sticky.Should().BeFalse();
    }
}
