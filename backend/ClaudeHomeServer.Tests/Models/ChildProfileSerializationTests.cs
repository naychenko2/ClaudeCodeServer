using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Models;

// Контракты хранения детского режима (ADR-041 §1): ChildProfile едет в users.json через
// User.ChildMode, ChildSpace — в sessions.json через Session. Обе записи аддитивные:
// старые файлы читаются с null без миграции, BackupSchema.Version не двигается.
public class ChildProfileSerializationTests
{
    // Опции стора пользователей (UserStore.JsonOptions)
    private static readonly JsonSerializerOptions UsersJson = new() { WriteIndented = true };

    // Опции стора сессий (SessionStore._jsonOpts)
    private static readonly JsonSerializerOptions SessionsJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static ChildProfile FullProfile() => new()
    {
        Enabled = true,
        GuardianUserIds = ["u-dad", "u-mom"],
        TutorPersonaId = "p-tutor",
        ProviderKey = "anthropic-api-kids",
        Model = "sonnet",
        Spaces =
        {
            [ChildSpaces.Lessons] = new SpaceLimits
            {
                Windows =
                {
                    new TimeWindow { From = "09:00", To = "13:00", Weekdays = [1, 3, 5] },
                    // окно через полночь (To < From) — вечернее, будни
                    new TimeWindow { From = "19:30", To = "21:00", Weekdays = [1, 2, 3, 4, 5] },
                },
                DailyMinutes = 45,
            },
        },
        DailyBudgetUsd = 2.50m,
        MonthlyBudgetUsd = 30.00m,
        WarnMinutes = 5,
        RulesAgreedAt = new DateTime(2026, 11, 17, 10, 30, 0, DateTimeKind.Utc),
        RulesSummary = "Уроки — по будням в окнах, время считает сердцебиение, неприятно — кнопка.",
    };

    [Fact]
    public void ПрофильПолногоСостава_ПереживаетКругUsersJson()
    {
        var original = new User { Username = "kid", ChildMode = FullProfile() };

        var restored = JsonSerializer.Deserialize<User>(
            JsonSerializer.Serialize(original, UsersJson), UsersJson)!;

        restored.ChildMode.Should().NotBeNull();
        restored.ChildMode!.Should().BeEquivalentTo(
            original.ChildMode, "полный профиль (2 окна, Weekdays, DailyMinutes, бюджеты) обязан вернуться по всем полям");
        restored.ChildMode!.Spaces[ChildSpaces.Lessons].Windows[0].Weekdays.Should().Equal(1, 3, 5);
        restored.ChildMode!.MonthlyBudgetUsd.Should().Be(30.00m);
    }

    [Fact]
    public void СтарыйUsersJson_БезПоля_ЧитаетсяОбычнымПользователем()
    {
        // Запись, созданная до появления поля: без childMode/ChildMode в json.
        const string legacy = """{"Id":"u1","Username":"old","Role":"user","ExecutionEnvironment":"local"}""";

        var user = JsonSerializer.Deserialize<User>(legacy, UsersJson)!;

        user.ChildMode.Should().BeNull("аддитивное поле: старый users.json читается без миграции");
    }

    [Fact]
    public void СтарыйSessionsJson_БезПоля_ЧитаетсяОбычнымЧатом()
    {
        const string legacy = """{"id":"s1","provider":"claude","model":"sonnet"}""";

        var session = JsonSerializer.Deserialize<Session>(legacy, SessionsJson)!;

        session.ChildSpace.Should().BeNull("аддитивное поле: старый sessions.json читается без миграции");
    }

    [Fact]
    public void ДетскийЧат_ПереживаетКругСChildSpace()
    {
        var original = new Session
        {
            Id = "s1",
            Provider = "anthropic-api-kids",
            Model = "sonnet",
            ChildSpace = ChildSpaces.Lessons,
            OnboardingKind = OnboardingKinds.ChildRules,
        };

        var restored = JsonSerializer.Deserialize<Session>(
            JsonSerializer.Serialize(original, SessionsJson), SessionsJson)!;

        restored.ChildSpace.Should().Be(ChildSpaces.Lessons);
        restored.OnboardingKind.Should().Be(OnboardingKinds.ChildRules);
    }

    [Fact]
    public void ОбычныйЧат_ПереживаетКругБезДетскогоПространства()
    {
        var restored = JsonSerializer.Deserialize<Session>(
            JsonSerializer.Serialize(new Session { Id = "s1" }, SessionsJson), SessionsJson)!;

        restored.ChildSpace.Should().BeNull();
    }

    [Fact]
    public void КонстантыПространствИОнбординга_СовпадаютСКонтрактом()
    {
        // Ключи — строки из контракта /api/child/* (ADR-041): «unknown_space» сверяется с ними,
        // разъезд формулировки ломает и стор, и ответы ручки.
        ChildSpaces.Lessons.Should().Be("lessons");
        ChildSpaces.Studio.Should().Be("studio");
        OnboardingKinds.ChildRules.Should().Be("child-rules");
    }
}
