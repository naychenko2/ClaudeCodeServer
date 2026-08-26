using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Срок хранения архива чатов — личная настройка (кнопка «Хранить» в строке архивного
// списка). Инвариант хранения: «не удалять» пишется как null, а не как 0 — иначе в
// users.json копились бы два разных способа сказать одно и то же.
public class UserStoreArchiveRetentionTests : IDisposable
{
    private readonly string _tempDir;

    public UserStoreArchiveRetentionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc_user_archive_store_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private UserStore BuildStore()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        }).Build();
        return new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
    }

    [Fact]
    public void ПоУмолчанию_НеЗадан()
    {
        var store = BuildStore();
        var user = store.Add("u1", "password123", "user");
        user.ArchiveRetentionDays.Should().BeNull("архив вечен, пока человек не решит иначе");
    }

    [Fact]
    public void Set_СохраняетИПереживаетПерезагрузку()
    {
        var store = BuildStore();
        var user = store.Add("u1", "password123", "user");

        store.SetArchiveRetentionDays(user.Id, 30).Should().BeTrue();
        store.GetById(user.Id)!.ArchiveRetentionDays.Should().Be(30);

        BuildStore().GetById(user.Id)!.ArchiveRetentionDays.Should().Be(30);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    [InlineData(-5)]
    public void Set_НеположительноеЗначение_СтановитсяNull(int? days)
    {
        var store = BuildStore();
        var user = store.Add("u1", "password123", "user");
        store.SetArchiveRetentionDays(user.Id, 90);

        store.SetArchiveRetentionDays(user.Id, days).Should().BeTrue();
        store.GetById(user.Id)!.ArchiveRetentionDays.Should().BeNull();
    }

    [Fact]
    public void Set_НеизвестныйПользователь_False()
    {
        BuildStore().SetArchiveRetentionDays("no-such-user", 30).Should().BeFalse();
    }
}
