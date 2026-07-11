using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Чистые предикаты расписания проактивности персон (LastOccurrenceUtc / ShouldFire).
// Полный пайплайн (создание чата + отправка) требует claude.exe и здесь не гоняется.
public class PersonaProactiveServiceTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    // Фиксированный не-UTC пояс (без DST): UTC+5
    private static readonly TimeZoneInfo Plus5 = TimeZoneInfo.CreateCustomTimeZone(
        "test+5", TimeSpan.FromHours(5), "UTC+5", "UTC+5");

    private static PersonaProactiveConfig Cfg(
        PersonaScheduleType type = PersonaScheduleType.Daily,
        string time = "09:00", List<int>? weekdays = null,
        string instruction = "собери бриф", bool enabled = true,
        DateTime? lastFiredAt = null) => new()
    {
        Enabled = enabled,
        Type = type,
        Weekdays = weekdays,
        Time = time,
        Instruction = instruction,
        LastFiredAt = lastFiredAt,
    };

    // ─── LastOccurrenceUtc ───────────────────────────────────────────────────

    [Fact]
    public void LastOccurrence_Daily_ДоВремени_ВчерашнийМомент()
    {
        // 08:30 UTC, расписание на 09:00 — последний момент был вчера в 09:00
        var now = new DateTime(2026, 7, 8, 8, 30, 0, DateTimeKind.Utc);

        var occ = PersonaProactiveService.LastOccurrenceUtc(Cfg(), Utc, now);

        occ.Should().Be(new DateTime(2026, 7, 7, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void LastOccurrence_Daily_ПослеВремени_СегодняшнийМомент()
    {
        var now = new DateTime(2026, 7, 8, 9, 30, 0, DateTimeKind.Utc);

        var occ = PersonaProactiveService.LastOccurrenceUtc(Cfg(), Utc, now);

        occ.Should().Be(new DateTime(2026, 7, 8, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void LastOccurrence_Weekdays_ВСубботу_ПоследняяПятница()
    {
        // 11.07.2026 — суббота; последний будний момент — пятница 10.07 09:00
        var now = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);

        var occ = PersonaProactiveService.LastOccurrenceUtc(
            Cfg(PersonaScheduleType.Weekdays), Utc, now);

        occ.Should().Be(new DateTime(2026, 7, 10, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void LastOccurrence_Weekly_ПоСпискуДней()
    {
        // Сегодня среда 08.07; расписание — Пн(1) и Чт(4) → последний момент: понедельник 06.07
        var now = new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);

        var occ = PersonaProactiveService.LastOccurrenceUtc(
            Cfg(PersonaScheduleType.Weekly, weekdays: [1, 4]), Utc, now);

        occ.Should().Be(new DateTime(2026, 7, 6, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void LastOccurrence_Weekly_БезДней_Null()
    {
        var now = new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);

        PersonaProactiveService.LastOccurrenceUtc(
            Cfg(PersonaScheduleType.Weekly, weekdays: null), Utc, now).Should().BeNull();
    }

    [Fact]
    public void LastOccurrence_ТаймзонаНеUtc_КонвертируетКорректно()
    {
        // 09:00 локальных в UTC+5 = 04:00 UTC. Сейчас 05:00 UTC (10:00 локальных) —
        // момент уже был сегодня.
        var now = new DateTime(2026, 7, 8, 5, 0, 0, DateTimeKind.Utc);

        var occ = PersonaProactiveService.LastOccurrenceUtc(Cfg(), Plus5, now);

        occ.Should().Be(new DateTime(2026, 7, 8, 4, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void LastOccurrence_НевалидноеВремя_Null()
    {
        var now = new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);

        PersonaProactiveService.LastOccurrenceUtc(Cfg(time: "мусор"), Utc, now).Should().BeNull();
    }

    // ─── ShouldFire ──────────────────────────────────────────────────────────

    [Fact]
    public void ShouldFire_МоментНаступилИНеСрабатывали_True()
    {
        var now = new DateTime(2026, 7, 8, 9, 5, 0, DateTimeKind.Utc);

        PersonaProactiveService.ShouldFire(Cfg(), Utc, now).Should().BeTrue();
    }

    [Fact]
    public void ShouldFire_УжеСрабатывалиПоЭтомуМоменту_False()
    {
        var now = new DateTime(2026, 7, 8, 9, 5, 0, DateTimeKind.Utc);
        // LastFiredAt позже сегодняшнего 09:00 — по этому моменту уже сработано
        var cfg = Cfg(lastFiredAt: new DateTime(2026, 7, 8, 9, 1, 0, DateTimeKind.Utc));

        PersonaProactiveService.ShouldFire(cfg, Utc, now).Should().BeFalse();
    }

    [Fact]
    public void ShouldFire_СрабатывалиВчера_СегодняСноваTrue()
    {
        var now = new DateTime(2026, 7, 8, 9, 5, 0, DateTimeKind.Utc);
        var cfg = Cfg(lastFiredAt: new DateTime(2026, 7, 7, 9, 1, 0, DateTimeKind.Utc));

        PersonaProactiveService.ShouldFire(cfg, Utc, now).Should().BeTrue();
    }

    [Fact]
    public void ShouldFire_МоментСтаршеОкна_False()
    {
        // Weekly по понедельникам; сейчас пятница — момент старше 24 ч (защита от лавины)
        var now = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
        var cfg = Cfg(PersonaScheduleType.Weekly, weekdays: [1]);

        PersonaProactiveService.ShouldFire(cfg, Utc, now).Should().BeFalse();
    }

    [Fact]
    public void ShouldFire_ВыключеноИлиПустаяИнструкция_False()
    {
        var now = new DateTime(2026, 7, 8, 9, 5, 0, DateTimeKind.Utc);

        PersonaProactiveService.ShouldFire(Cfg(enabled: false), Utc, now).Should().BeFalse();
        PersonaProactiveService.ShouldFire(Cfg(instruction: "  "), Utc, now).Should().BeFalse();
    }

    // ─── Триггер-промпт ──────────────────────────────────────────────────────

    [Fact]
    public void BuildTriggerPrompt_НачинаетсяСМаркераИСодержитИнструкцию()
    {
        var persona = new Persona { Name = "Вера", Proactive = Cfg(instruction: "собери утренний бриф") };

        var prompt = PersonaProactiveService.BuildTriggerPrompt(
            persona, new DateTime(2026, 7, 8, 9, 0, 0));

        prompt.Should().StartWith("⏰ Сработал триггер по расписанию");
        prompt.Should().Contain("собери утренний бриф");
    }
}
