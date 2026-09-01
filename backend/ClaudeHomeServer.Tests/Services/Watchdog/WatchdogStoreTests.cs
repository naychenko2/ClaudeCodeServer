using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Watchdog;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services.Watchdog;

// Юниты стора сторожей (шаг 1 плана): лимиты постановки, валидация, load/save,
// снятие и уборка отработавших. Всё во временном каталоге, без реальных процессов.
public class WatchdogStoreTests : IDisposable
{
    private readonly string _tempDir;

    public WatchdogStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "watchdog_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private WatchdogStore NewStore() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json")
        }).Build());

    [Fact]
    public void Create_Valid_MakesActiveWithComputedPollTimeout()
    {
        var sut = NewStore();
        var w = sut.Create("owner", "session", "project", "Билд", "py check.py", 45, 30, out var error);
        error.Should().BeNull();
        w.Should().NotBeNull();
        w!.Status.Should().Be(WatchdogStatus.Active);
        // Таймаут запуска сервер считает сам: min(60, интервал)
        w.PollTimeoutSeconds.Should().Be(45);
        w.TimeoutMinutes.Should().Be(30);
        w.DeliveredAt.Should().BeNull();
        w.ConsecutiveLaunchFailures.Should().Be(0);
    }

    [Fact]
    public void Create_LongInterval_CapsPollTimeoutAt60()
    {
        var sut = NewStore();
        var w = sut.Create("owner", "session", null, "Билд", "py check.py", 300, null, out _);
        w!.PollTimeoutSeconds.Should().Be(60);
        w.TimeoutMinutes.Should().Be(WatchdogLimits.DefaultTimeoutMinutes);
    }

    [Theory]
    [InlineData(29)]
    [InlineData(601)]
    public void Create_IntervalOutOfRange_IsRejected(int interval)
    {
        var sut = NewStore();
        sut.Create("owner", "session", null, "Билд", "py check.py", interval, null, out var error);
        error.Should().Contain("Интервал");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public void Create_TtlOutOfRange_IsRejected(int ttl)
    {
        var sut = NewStore();
        sut.Create("owner", "session", null, "Билд", "py check.py", null, ttl, out var error);
        error.Should().Contain("Потолок");
    }

    [Fact]
    public void Create_EmptyNameOrCommand_IsRejected()
    {
        var sut = NewStore();
        sut.Create("owner", "session", null, "  ", "py check.py", null, null, out var e1);
        e1.Should().Contain("имя");
        sut.Create("owner", "session", null, "Билд", "", null, null, out var e2);
        e2.Should().Contain("команда");
    }

    [Fact]
    public void Create_SixthActiveForChat_IsRejected()
    {
        var sut = NewStore();
        for (var i = 0; i < WatchdogLimits.MaxPerChat; i++)
            sut.Create("owner", "session", null, $"Сторож {i}", "true", null, null, out _);
        sut.Create("owner", "session", null, "Лишний", "true", null, null, out var error);
        error.Should().Contain($"уже {WatchdogLimits.MaxPerChat} активных");

        // Отменённый не занимает слот
        var first = sut.GetBySession("session")[0];
        sut.Cancel(first.Id, "owner", out _);
        sut.Create("owner", "session", null, "На освободившееся", "true", null, null, out var ok);
        ok.Should().BeNull();
    }

    [Fact]
    public void Create_TwentyFirstActiveForOwner_IsRejected()
    {
        var sut = NewStore();
        for (var i = 0; i < WatchdogLimits.MaxPerOwner; i++)
            sut.Create("owner", $"session-{i}", null, $"Сторож {i}", "true", null, null, out _);
        sut.Create("owner", "session-new", null, "Лишний", "true", null, null, out var error);
        error.Should().Contain($"уже {WatchdogLimits.MaxPerOwner} активных");
    }

    [Fact]
    public void SaveThenNewStoreLoad_SurvivesRestart()
    {
        var sut = NewStore();
        sut.Create("owner", "session", "project", "Билд", "py check.py", 45, null, out _);

        var revived = NewStore();
        var loaded = revived.GetBySession("session");
        loaded.Should().HaveCount(1);
        loaded[0].Name.Should().Be("Билд");
        loaded[0].IntervalSeconds.Should().Be(45);
        loaded[0].PollTimeoutSeconds.Should().Be(45);
        File.Exists(Path.Combine(_tempDir, "watchdogs.json")).Should().BeTrue();
    }

    [Fact]
    public void CorruptedStoreFile_StartsEmpty()
    {
        File.WriteAllText(Path.Combine(_tempDir, "watchdogs.json"), "{ не json");
        var revived = NewStore();
        revived.GetByOwner("owner").Should().BeEmpty();
    }

    [Fact]
    public void Cancel_ForeignId_ReturnsNull()
    {
        var sut = NewStore();
        var w = sut.Create("owner", "session", null, "Билд", "true", null, null, out _);
        sut.Cancel(w!.Id, "чужой", out var foreignError).Should().BeNull();
        foreignError.Should().BeNull("чужой id — 404 без объяснений");
        sut.GetById(w.Id)!.Status.Should().Be(WatchdogStatus.Active);
        sut.Cancel(w.Id, "owner", out var okError)!.Status.Should().Be(WatchdogStatus.Cancelled);
        okError.Should().BeNull();
    }

    [Fact]
    public void Cancel_TerminalWatchdog_IsRejectedAndKeepsStatus()
    {
        // п.2 ревью: cancel по терминальному сторожу не должен затирать исход —
        // недоставленный будильник терял бы ретраи и флаг недоставки
        var sut = NewStore();
        var w = sut.Create("owner", "session", null, "Билд", "true", null, null, out _)!;
        w.Status = WatchdogStatus.Fired;
        w.FiredAt = DateTime.UtcNow;
        w.DeliveryAttempts = 2;

        sut.Cancel(w.Id, "owner", out var error).Should().BeNull();
        error.Should().Contain("уже завершён").And.Contain("fired");
        w.Status.Should().Be(WatchdogStatus.Fired, "терминал не перезаписывается");
        w.DeliveryAttempts.Should().Be(2);
        w.DeliveredAt.Should().BeNull();
    }

    [Fact]
    public void PruneDelivered_RemovesOnlyOldDeliveredTerminals()
    {
        var sut = NewStore();
        var delivered = sut.Create("owner", "s", null, "Доставленный", "true", null, null, out _)!;
        delivered.Status = WatchdogStatus.Fired;
        delivered.FiredAt = DateTime.UtcNow.AddDays(-2);
        delivered.DeliveredAt = DateTime.UtcNow.AddDays(-2);

        var undelivered = sut.Create("owner", "s", null, "Недоставленный", "true", null, null, out _)!;
        undelivered.Status = WatchdogStatus.TimedOut;
        undelivered.FiredAt = DateTime.UtcNow.AddDays(-2);
        // DeliveredAt остаётся null — недоставка флагом

        var active = sut.Create("owner", "s", null, "Активный", "true", null, null, out _)!;
        var freshDelivered = sut.Create("owner", "s", null, "Свежий", "true", null, null, out _)!;
        freshDelivered.Status = WatchdogStatus.Fired;
        freshDelivered.DeliveredAt = DateTime.UtcNow.AddMinutes(-5);
        sut.Save();

        sut.PruneDelivered(DateTime.UtcNow);

        sut.GetById(delivered.Id).Should().BeNull();
        sut.GetById(undelivered.Id).Should().NotBeNull();
        sut.GetById(active.Id).Should().NotBeNull();
        sut.GetById(freshDelivered.Id).Should().NotBeNull();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* временный каталог — мусор не критичен */ }
    }
}
