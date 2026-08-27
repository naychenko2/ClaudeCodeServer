using AiHomeDesktop.Core.Channel;
using AiHomeDesktop.Core.Protocol;
using FluentAssertions;
using Xunit;

namespace AiHomeDesktop.Core.Tests;

/// <summary>
/// Журнал вызовов отвечает на два вопроса реконнекта: «этот вызов уже приходил?» и
/// «что осталось дослать?». Пути строятся от Path.GetTempPath() — тесты обязаны идти
/// и на Linux, где Windows-литерал считался бы относительным путём.
/// </summary>
public class CallJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "aihome-journal-" + Guid.NewGuid().ToString("N"));

    private CallJournal New(TimeProvider? time = null, TimeSpan? ttl = null) =>
        new(Path.Combine(_dir, "calls.json"), time, ttl);

    [Fact]
    public void ПовторныйВызов_НеОткрывается_ИОтдаётПрежнююЗапись()
    {
        var journal = New();
        journal.TryBegin("c1", DesktopCallKinds.Screen, out _).Should().BeTrue();

        journal.TryBegin("c1", DesktopCallKinds.Screen, out var known).Should().BeFalse(
            "исполнять пришедший второй раз вызов нельзя: авто-ретраев в этой грани нет нигде");
        known.CallId.Should().Be("c1");
    }

    [Fact]
    public void НедоехавшийРезультат_ОстаётсяВОчередиДосылки()
    {
        var journal = New();
        journal.TryBegin("c1", DesktopCallKinds.Open, out _);
        journal.RecordResult("c1", DeviceCallResultBody.Refused(DesktopOutcomes.Ok, "готово"));

        journal.Undelivered().Should().ContainSingle().Which.CallId.Should().Be("c1");

        journal.MarkDelivered("c1");
        journal.Undelivered().Should().BeEmpty("доехавший результат досылать больше не нужно");
    }

    [Fact]
    public void ЗаписьПереживаетПерезапускКлиента()
    {
        var first = New();
        first.TryBegin("c1", DesktopCallKinds.Screen, out _);
        first.RecordResult("c1", DeviceCallResultBody.Refused(DesktopOutcomes.Ok, "готово"));

        // Новый экземпляр = перезапуск клиента: журнал живёт на диске ровно ради этого
        var second = New();
        second.Undelivered().Should().ContainSingle().Which.CallId.Should().Be("c1");
        second.TryBegin("c1", DesktopCallKinds.Screen, out _).Should().BeFalse();
    }

    [Fact]
    public void СтарыеЗаписи_ВыбрасываютсяПоTtl()
    {
        var clock = new FakeTime(DateTimeOffset.Parse("2026-08-20T10:00:00Z"));
        var journal = New(clock, TimeSpan.FromMinutes(15));
        journal.TryBegin("c1", DesktopCallKinds.Screen, out _);

        clock.Now = clock.Now.AddMinutes(16);
        journal.Prune();

        journal.Find("c1").Should().BeNull("журнал отвечает на вопросы реконнекта, а не хранит историю");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* временный каталог — не повод падать тесту */ }
        GC.SuppressFinalize(this);
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
