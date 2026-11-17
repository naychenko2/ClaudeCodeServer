using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// UserStore.AgreeChildRules (KM-B6b, ADR-041 ручка №7) — инвариант гонки на уровне стора,
/// без HTTP и хоста: первый вызов пишет согласие (true), второй — false и не перезаписывает
/// summary победителя. Именно эту проверку (UserStore.cs:679, «RulesAgreedAt is not null»)
/// HTTP-тест Agree_Parallel_OneWins не краснил при её удалении: здесь она прямая.
/// </summary>
public class UserStoreChildRulesTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ccs-userstore-childrules_" + Guid.NewGuid().ToString("N"));

    public UserStoreChildRulesTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private UserStore NewStore() => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        }).Build(),
        new Helpers.FakeHostEnvironment(),
        NullLogger<UserStore>.Instance);

    // Ребёнок с профилем без согласия — с лимитом и тьютором: согласие их не трогает никогда.
    private static string SeedChild(UserStore store)
    {
        var user = store.Add("kid-store-" + Guid.NewGuid().ToString("N"), "password-1", "user");
        store.SetChildProfile(user.Id, new ChildProfile
        {
            Enabled = true,
            ProviderKey = "kids-api",
            Model = "sonnet",
            TutorPersonaId = "tutor-test",
            Spaces = { ["lessons"] = new SpaceLimits { DailyMinutes = 40 } },
            DailyBudgetUsd = 2m,
        }, resetRules: false).Should().BeTrue();
        return user.Id;
    }

    [Fact]
    public async Task AgreeChildRules_ГонкаДвухВызовов_ПобедительОдин_SecondDoesNotOverwrite()
    {
        var store = NewStore();
        var id = SeedChild(store);
        var at = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

        var results = await Task.WhenAll(
            Task.Run(() => store.AgreeChildRules(id, at, "первый итог")),
            Task.Run(() => store.AgreeChildRules(id, at, "второй итог")));

        results.Should().ContainSingle(r => r, "один вызов под локом выигрывает гонку")
            .And.ContainSingle(r => !r, "второй — уже согласовано");
        var stored = store.GetById(id)!.ChildMode!;
        stored.RulesAgreedAt.Should().Be(at);
        // Что именно победил — не детерминировано; детерминировано другое: в сторе итог
        // того, чей вызов вернул true, и ни одна гонка не даёт смесь двух итогов.
        stored.RulesSummary.Should().BeOneOf("первый итог", "второй итог");
    }

    [Fact]
    public void AgreeChildRules_Последовательно_ВторойFalseИНеПерезаписывает()
    {
        var store = NewStore();
        var id = SeedChild(store);
        var at = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

        store.AgreeChildRules(id, at, "первые правила").Should().BeTrue();
        store.AgreeChildRules(id, at.AddMinutes(1), "вторые правила").Should().BeFalse();

        var stored = store.GetById(id)!.ChildMode!;
        stored.RulesAgreedAt.Should().Be(at, "второй вызов не двигает момент согласия");
        stored.RulesSummary.Should().Be("первые правила", "второй вызов не перезаписывает итог победителя");
    }

    [Fact]
    public void AgreeChildRules_ПишетТолькоПравила_ЛимитыИТьюторЦелы_ИНетПрофиляFalse()
    {
        var store = NewStore();
        var id = SeedChild(store);
        var at = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

        store.AgreeChildRules(id, at, "договорились").Should().BeTrue();

        var stored = store.GetById(id)!.ChildMode!;
        stored.RulesAgreedAt.Should().Be(at);
        stored.RulesSummary.Should().Be("договорились");
        stored.Spaces["lessons"].DailyMinutes.Should().Be(40);
        stored.DailyBudgetUsd.Should().Be(2m);
        stored.TutorPersonaId.Should().Be("tutor-test");

        // Профиля нет — не «уже согласовано», а нечего писать: та же false,
        // вызывающий по живому состоянию отличит её от 409 (503).
        store.SetChildProfile(id, null, resetRules: false).Should().BeTrue();
        store.AgreeChildRules(id, at, "после удаления профиля").Should().BeFalse();
    }
}
