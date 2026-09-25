using ClaudeHomeServer.Services.ImageEditor.Spending;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.ImageEditor.Spending;

public class ImageEditSpendStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly ImageEditSpendStore _store;

    public ImageEditSpendStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ies_tests_" + Guid.NewGuid().ToString("N"));
        _store = new ImageEditSpendStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* дев-стенд, не критично */ }
    }

    private static ImageEditSpendRecord Rec(string owner, double amount, ImageEditSpendUnit unit = ImageEditSpendUnit.Usd) =>
        new(Guid.NewGuid().ToString("N"), owner, "fal", "nano-banana-2", amount, unit, DateTime.UtcNow, "task-1");

    [Fact]
    public void Query_NonAdmin_SeesOnlyOwnRecords()
    {
        _store.Record(Rec("userA", 0.08));
        _store.Record(Rec("userB", 1.5, ImageEditSpendUnit.Credits));

        var result = _store.Query("userA", isAdmin: false);

        result.Should().ContainSingle();
        result[0].OwnerId.Should().Be("userA");
    }

    [Fact]
    public void Query_Admin_SeesAllOwnersRecords()
    {
        _store.Record(Rec("userA", 0.08));
        _store.Record(Rec("userB", 1.5, ImageEditSpendUnit.Credits));

        var result = _store.Query("userA", isAdmin: true);

        result.Should().HaveCount(2);
        result.Select(r => r.OwnerId).Should().BeEquivalentTo(["userA", "userB"]);
    }

    [Fact]
    public void Record_PersistsAcrossReload()
    {
        _store.Record(Rec("userA", 0.08));

        var reloaded = new ImageEditSpendStore(_dir);

        reloaded.Query("userA", isAdmin: true).Should().ContainSingle();
    }
}
