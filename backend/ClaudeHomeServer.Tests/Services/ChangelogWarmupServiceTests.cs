using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Юнит-тесты логики отбора дней на фоновый прогрев сводок «Что нового».
// Как и в ChangelogServiceTests, git-стенды не поднимаем: вся решающая логика
// извлечена в чистый предикат ChangelogWarmupService.ShouldWarm — его и проверяем.
// Сам проход (TickAsync → GetDay) без claude/git проверяется вручную и E2E.
public class ChangelogWarmupServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.FromHours(3));
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);

    [Fact]
    public void ShouldWarm_АктуальнаяСводкаВКеше_НеГреется()
    {
        // Cached=true покрывает и degraded-дни с совпадающим хешем — их не перегенерируем
        var c = new WarmupCandidate("2026-07-17", Cached: true, LastCommitAt: Now.AddDays(-1));
        ChangelogWarmupService.ShouldWarm(c, Now, Cooldown).Should().BeFalse();
    }

    [Fact]
    public void ShouldWarm_ГорячийДень_КоммитыЕщеСыплются_Пропускается()
    {
        // Последний коммит 5 минут назад — сводка тут же протухнет, не жжем токены
        var c = new WarmupCandidate("2026-07-18", Cached: false, LastCommitAt: Now.AddMinutes(-5));
        ChangelogWarmupService.ShouldWarm(c, Now, Cooldown).Should().BeFalse();
    }

    [Fact]
    public void ShouldWarm_СегодняшнийДеньОстыл_Греется()
    {
        var c = new WarmupCandidate("2026-07-18", Cached: false, LastCommitAt: Now.AddMinutes(-30));
        ChangelogWarmupService.ShouldWarm(c, Now, Cooldown).Should().BeTrue();
    }

    [Fact]
    public void ShouldWarm_УстаревшаяСводка_ХешРазошелсяИКоммитыУтихли_Греется()
    {
        // Сводка была, но после нее пришли новые коммиты (Cached=false из-за хеша)
        var c = new WarmupCandidate("2026-07-18", Cached: false, LastCommitAt: Now - Cooldown);
        ChangelogWarmupService.ShouldWarm(c, Now, Cooldown).Should().BeTrue();
    }

    [Fact]
    public void ShouldWarm_ПрошлыйХолодныйДень_Греется()
    {
        var c = new WarmupCandidate("2026-07-15", Cached: false, LastCommitAt: Now.AddDays(-3));
        ChangelogWarmupService.ShouldWarm(c, Now, Cooldown).Should().BeTrue();
    }
}
