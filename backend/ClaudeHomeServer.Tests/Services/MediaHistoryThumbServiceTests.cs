using ClaudeHomeServer.Services.MediaHistory;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Миниатюры истории (ADR-030 §5): MediaHistoryThumbService. Тест — на отмене: закрытая вкладка
// (отменённый CancellationToken) не должна ронять 500, а вернуть null (фронт рисует плитку).
// Мутация «убрать catch OperationCanceledException» — тест краснеет (исключение уходит наружу)
public class MediaHistoryThumbServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "thumb_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception) { /* уборка temp — не предмет теста */ }
    }

    private MediaHistoryThumbService NewService()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "data"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json"),
        }).Build();
        return new MediaHistoryThumbService(config, NullLogger<MediaHistoryThumbService>.Instance);
    }

    // Отмена (закрытая вкладка) — null, не 500
    [Fact]
    public async Task Отмена_Null()
    {
        var svc = NewService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var res = await svc.GetThumbAsync("rec-1", 0, "/tmp/source.png", "image/png", cts.Token);
        res.Should().BeNull();
    }
}
