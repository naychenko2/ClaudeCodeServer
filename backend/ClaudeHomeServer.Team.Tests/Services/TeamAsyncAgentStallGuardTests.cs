using FluentAssertions;
using ClaudeHomeServer.Services.Team;

namespace ClaudeHomeServer.Tests.Services;

// Юнит-тесты чистой функции подавления гарда молчаливого тупика async-агентом
// (задача b63fd8ea). По образцу ClaudeSessionWatchdogMappingTests: вынесенная логика
// тестируется без реального процесса/CLI/DateTime.UtcNow.
public class TeamAsyncAgentStallGuardTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    // Async-агента нет → гард НЕ подавляется, идёт штатная логика молчаливого тупика.
    // Сюда же попадает пустая запись (новый заход в stalledStage без async-агента).
    [Fact]
    public void ShouldSuppress_НетAsyncАгента_ВозвращаетFalse()
    {
        var now = DateTime.UtcNow;
        TeamAsyncAgentStallGuard.ShouldSuppress(hasAsyncAgent: false, stallSince: null, now, Timeout)
            .Should().BeFalse();
        TeamAsyncAgentStallGuard.ShouldSuppress(hasAsyncAgent: false, stallSince: now - TimeSpan.FromMinutes(15),
            now, Timeout).Should().BeFalse();
    }

    // Async-агент жив, но метки ещё нет (первый вызов гарда за текущий заход) → подавляем:
    // мы ещё в начале окна, ждать маркера team:work разумно.
    [Fact]
    public void ShouldSuppress_AsyncАгентЕстьМеткиНет_ВозвращаетTrue()
    {
        TeamAsyncAgentStallGuard.ShouldSuppress(hasAsyncAgent: true, stallSince: null, DateTime.UtcNow, Timeout)
            .Should().BeTrue();
    }

    // Async-агент жив, длительность подавления КОРОЧЕ порога → гард молчит (поведение P16).
    [Fact]
    public void ShouldSuppress_AsyncАгентКорочеПорога_ВозвращаетTrue()
    {
        var now = new DateTime(2026, 09, 06, 12, 0, 0, DateTimeKind.Utc);
        var stallSince = now - TimeSpan.FromMinutes(5);
        TeamAsyncAgentStallGuard.ShouldSuppress(true, stallSince, now, Timeout).Should().BeTrue();
    }

    // Async-агент жив, длительность подавления ДЛИННЕЕ порога → гард поднимает карточку
    // молчаливого тупика. Это и есть починка бага b63fd8ea: раньше подавление висело
    // бессрочно, теперь у него есть свой потолок.
    [Fact]
    public void ShouldSuppress_AsyncАгентДольшеПорога_ВозвращаетFalse()
    {
        var now = new DateTime(2026, 09, 06, 12, 0, 0, DateTimeKind.Utc);
        var stallSince = now - TimeSpan.FromMinutes(11);
        TeamAsyncAgentStallGuard.ShouldSuppress(true, stallSince, now, Timeout).Should().BeFalse();
    }

    // Граница: ровно порог → НЕ подавляем. Логика строгая (now - stallSince < timeout),
    // иначе карточка опоздала бы на 1 тик.
    [Fact]
    public void ShouldSuppress_РовноНаГраницеПорога_ВозвращаетFalse()
    {
        var now = new DateTime(2026, 09, 06, 12, 0, 0, DateTimeKind.Utc);
        var stallSince = now - Timeout;
        TeamAsyncAgentStallGuard.ShouldSuppress(true, stallSince, now, Timeout).Should().BeFalse();
    }

    // Контрактный размер потолка: 10 минут. Зафиксирован как поведенческая граница —
    // меняться должен осознанно (и через комментарий в TeamAsyncAgentStallGuard.cs).
    [Fact]
    public void DefaultSuppressionTimeout_Равно10Минутам()
    {
        TeamAsyncAgentStallGuard.DefaultSuppressionTimeout.Should().Be(TimeSpan.FromMinutes(10));
    }
}
