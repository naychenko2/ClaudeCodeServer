using ClaudeHomeServer.Telemetry.Alerts;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Telemetry;

/// <summary>
/// История алертов в <c>data/alert-state.json</c>: погасший алерт помечается, а не
/// забывается — из этих записей раздел «Инциденты» строит секцию «Недавние».
///
/// Ключевой инвариант — <see cref="AlertStateStore.KnownFingerprints"/> отдаёт только
/// горящие: иначе повторное возгорание перестало бы считаться новым событием и никого
/// бы не разбудило.
/// </summary>
public class AlertStateHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ccs-alert-state-" + Guid.NewGuid().ToString("N")[..8]);

    private AlertStateStore NewStore()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            }).Build();
        return new AlertStateStore(config, NullLogger<AlertStateStore>.Instance);
    }

    private string StateFile => Path.Combine(_dir, "alert-state.json");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* временная папка — не повод ронять прогон */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void MarkResolved_KeepsRecordButDropsItFromKnown()
    {
        var store = NewStore();
        store.Remember("fp-1", new AlertMemo("Ходы LLM падают", DateTimeOffset.UtcNow));

        store.KnownFingerprints.Should().Contain("fp-1");

        store.MarkResolved(["fp-1"]);

        store.KnownFingerprints.Should().BeEmpty("погасший алерт больше не «уже сообщённый»");
        store.Recall("fp-1").Should().NotBeNull("запись остаётся историей");
        store.Recall("fp-1")!.ResolvedAt.Should().NotBeNull();
    }

    [Fact]
    public void ReFiringAfterResolve_CountsAsNewEvent()
    {
        var store = NewStore();
        store.Remember("fp-1", new AlertMemo("Ходы LLM падают", DateTimeOffset.UtcNow));
        store.MarkResolved(["fp-1"]);

        // Тот же отпечаток загорелся снова — Diff обязан увидеть его как «started»
        var alert = new SignozAlert
        {
            Fingerprint = "fp-1",
            Labels = new Dictionary<string, string> { ["alertname"] = "Ходы LLM падают" },
        };
        var diff = AlertDigest.Diff([alert], store.KnownFingerprints);

        diff.Started.Should().ContainSingle().Which.Fingerprint.Should().Be("fp-1");
    }

    [Fact]
    public void Recent_ReturnsFreshFirst()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow;
        store.Remember("old", new AlertMemo("Старый", now.AddHours(-3)));
        store.Remember("fresh", new AlertMemo("Свежий", now.AddMinutes(-5)));

        var recent = store.Recent();

        recent.First().Fingerprint.Should().Be("fresh");
        recent.Should().HaveCount(2);
    }

    /// <summary>
    /// Реальный потолок ФАЙЛА, а не выдачи: <c>Recent</c> сам клампит limit, поэтому
    /// утверждение про его длину не могло бы провалиться в принципе. Считаем записи в
    /// сторе после вытеснения.
    /// </summary>
    [Fact]
    public void ResolvedHistory_IsCappedInStore()
    {
        var store = NewStore();
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        for (var i = 0; i < AlertStateStore.MaxHistory + 10; i++)
        {
            var fingerprint = $"fp-{i:D3}";
            store.Remember(fingerprint, new AlertMemo($"Алерт {i}", start.AddMinutes(i)));
            store.MarkResolved([fingerprint], start.AddMinutes(i + 1));
        }

        // Старейшие вытеснены: первых десяти в сторе не осталось, последние на месте
        store.Recall("fp-000").Should().BeNull("старейший погасший вытеснен потолком истории");
        store.Recall("fp-009").Should().BeNull();
        store.Recall("fp-059").Should().NotBeNull("свежие записи остаются");

        // И то же самое в файле — потолок обязан пережить перезапуск
        var reloaded = NewStore();
        reloaded.Recent(int.MaxValue).Should().HaveCount(AlertStateStore.MaxHistory);
        File.ReadAllText(StateFile).Should().NotContain("fp-000");
    }

    [Fact]
    public void FiringRecords_AreNotEvictedByHistoryCap()
    {
        var store = NewStore();
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        store.Remember("burning", new AlertMemo("Горит давно", start));
        for (var i = 0; i < AlertStateStore.MaxHistory + 5; i++)
        {
            var fingerprint = $"fp-{i:D3}";
            store.Remember(fingerprint, new AlertMemo($"Алерт {i}", start.AddMinutes(i)));
            store.MarkResolved([fingerprint], start.AddMinutes(i + 1));
        }

        store.Recall("burning").Should().NotBeNull("горящий алерт — рабочее состояние, а не история");
        store.KnownFingerprints.Should().Contain("burning");
    }

    [Fact]
    public void OldFormatFile_IsStillReadable()
    {
        // Файл прошлой версии: только title и firedAt, без новых необязательных полей
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StateFile,
            """{"fp-legacy":{"title":"Старый алерт","firedAt":"2026-08-01T10:00:00+00:00"}}""");

        var store = NewStore();

        var memo = store.Recall("fp-legacy");
        memo.Should().NotBeNull();
        memo!.Title.Should().Be("Старый алерт");
        memo.ResolvedAt.Should().BeNull("старая запись считается горящей, как и раньше");
        store.KnownFingerprints.Should().Contain("fp-legacy");
    }
}
