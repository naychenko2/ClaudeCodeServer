using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Юнит-тесты ChangelogService на публичный контракт БЕЗ git-репозиториев и без claude.
// Git-агрегация (группировка коммитов по дням) требует настоящего git-стенда и подвержена
// флаку окружения/таймзоны — она проверяется вручную и E2E, а не здесь. Тут — поведение
// на пустом наборе проектов, ленивая ветка GetDay без коммитов (не дёргает claude),
// а также инвалидация и очистка кеша.
public class ChangelogServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _cacheDir;
    private readonly ChangelogService _sut;

    public ChangelogServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "changelog_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _cacheDir = Path.Combine(_tempDir, "data", "changelog");

        // Без Changelog:SourceRepoPath — источник не задан, коммитов нет
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json")
            }).Build();

        var dsOptions = Microsoft.Extensions.Options.Options.Create(new ClaudeHomeServer.Models.DeepSeekOptions());
        _sut = new ChangelogService(new FileService(), config, NullLogger<ChangelogService>.Instance,
            new ClaudeHomeServer.Services.Llm.DeepSeek.DeepSeekClient(
                new Moq.Mock<IHttpClientFactory>().Object, dsOptions), dsOptions);
    }

    // ─── Источник не задан ──────────────────────────────────────────────────

    [Fact]
    public void GetDays_БезИсточника_ПустойСписок()
    {
        _sut.GetDays(30).Should().BeEmpty();
    }

    [Fact]
    public void GetNewCommitCount_БезИсточника_Ноль()
    {
        _sut.GetNewCommitCount(DateTimeOffset.Now.AddDays(-7)).Should().Be(0);
    }

    [Fact]
    public void GetStatus_БезИсточника_НеНастроен()
    {
        var status = _sut.GetStatus();
        status.Configured.Should().BeFalse();
        status.Detail.Should().NotBeNullOrEmpty();
    }

    // ─── Ленивая сводка дня без коммитов не должна дёргать claude ────────────

    [Fact]
    public async Task GetDay_ДатаБезКоммитов_ПустойДеньБезClaude()
    {
        // Нет проектов → нет коммитов дня → ранний возврат пустого дня (claude не запускается)
        var day = await _sut.GetDay("2000-01-01");

        day.Date.Should().Be("2000-01-01");
        day.Items.Should().BeEmpty();
    }

    // ─── Инвалидация дня ────────────────────────────────────────────────────

    [Fact]
    public void InvalidateDay_НесуществующийДень_НеБросает()
    {
        var act = () => _sut.InvalidateDay("2000-01-01");
        act.Should().NotThrow();
    }

    [Fact]
    public void InvalidateDay_УдаляетТолькоУказанныйДеньИзКеша()
    {
        // Готовим кеш-файл с двумя днями вручную (формат: { "date": { shasHash, items } })
        Directory.CreateDirectory(_cacheDir);
        var cacheFile = Path.Combine(_cacheDir, "product.json");
        File.WriteAllText(cacheFile,
            """{"2026-07-01":{"ShasHash":"aaa","Items":[]},"2026-07-02":{"ShasHash":"bbb","Items":[]}}""");

        _sut.InvalidateDay("2026-07-01");

        var json = File.ReadAllText(cacheFile);
        json.Should().NotContain("2026-07-01");
        json.Should().Contain("2026-07-02"); // второй день остался
    }

    // ─── Полная очистка ─────────────────────────────────────────────────────

    [Fact]
    public void ClearAll_УдаляетФайлКеша()
    {
        Directory.CreateDirectory(_cacheDir);
        var cacheFile = Path.Combine(_cacheDir, "product.json");
        File.WriteAllText(cacheFile, """{"2026-07-01":{"ShasHash":"aaa","Items":[]}}""");

        _sut.ClearAll();

        File.Exists(cacheFile).Should().BeFalse();
    }

    [Fact]
    public void ClearAll_БезФайла_НеБросает()
    {
        var act = () => _sut.ClearAll();
        act.Should().NotThrow();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
