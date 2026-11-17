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
    public void ПредметыПереживаютКругСПорядкомПлиток()
    {
        // Порядок Subjects = порядок плиток на главной (срез §1): круг сериализации обязан
        // вернуть и порядок, и каждое поле предмета, включая Enabled=false и согласие админа.
        var profile = FullProfile();
        profile.Subjects =
        [
            new ChildSubject
            {
                Id = "literature", Label = "Литература", Glyph = "Аа", Color = "rose",
                TutorPersonaId = "p-tutor-lit", ProviderKey = "anthropic-api-kids", Model = "sonnet",
            },
            new ChildSubject
            {
                Id = "world", Label = "Окружающий мир", Glyph = "♣", Color = "success",
                TutorPersonaId = "p-tutor-world", ProviderKey = "deepseek", Model = "deepseek-chat",
                RouteAcknowledgedAt = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc),
                RouteAcknowledgedBy = "u-dad",
                Enabled = false,
            },
        ];
        var original = new User { Username = "kid", ChildMode = profile };

        var restored = JsonSerializer.Deserialize<User>(
            JsonSerializer.Serialize(original, UsersJson), UsersJson)!;

        restored.ChildMode!.Subjects.Should().HaveCount(2);
        restored.ChildMode!.Subjects[0].Id.Should().Be("literature", "порядок списка = порядок плиток");
        restored.ChildMode!.Subjects[1].Id.Should().Be("world");
        restored.ChildMode!.Subjects.Should().BeEquivalentTo(
            profile.Subjects, "предмет (глиф, цвет, маршрут, согласие, Enabled=false) обязан вернуться по всем полям");
    }

    [Fact]
    public void ПредметБезПривязкиЗнаний_ЧитаетсяСNull()
    {
        // Запись users.json до появления привязки (К1a): ключа "Knowledge" в json нет —
        // аддитивное поле читается с null, без миграции формата, BackupSchema.Version не двигается.
        const string legacy = """{"Id":"u1","Username":"kid","ChildMode":{"Subjects":[{"Id":"world","Label":"Окружающий мир","Enabled":true}]}}""";

        var user = JsonSerializer.Deserialize<User>(legacy, UsersJson)!;

        user.ChildMode!.Subjects[0].Knowledge.Should().BeNull("аддитивное поле: старый профиль читается без привязки");
    }

    [Fact]
    public void ПривязкаЗнанийПереживаетКругВсемиТремяПолями()
    {
        // К1a: id датасета, автор и момент привязки — все три поля обязаны вернуться после
        // круга users.json; сама привязка (допустимость датасета) — проверка К1b/К3, не здесь.
        var profile = FullProfile();
        profile.Subjects =
        [
            new ChildSubject
            {
                Id = "math", Label = "Математика", Glyph = "×÷", Color = "info",
                ProviderKey = "anthropic-api-kids", Model = "sonnet",
                Knowledge = new ChildSubjectKnowledge(
                    "dify-ds-math", "u-dad", new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc)),
            },
        ];
        var original = new User { Username = "kid", ChildMode = profile };

        var restored = JsonSerializer.Deserialize<User>(
            JsonSerializer.Serialize(original, UsersJson), UsersJson)!;

        var knowledge = restored.ChildMode!.Subjects[0].Knowledge;
        knowledge.Should().NotBeNull();
        knowledge!.DatasetId.Should().Be("dify-ds-math");
        knowledge.AttachedByUserId.Should().Be("u-dad");
        knowledge.AttachedAt.Should().Be(new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void СтарыйПрофильВолны1_БезSubjects_ЧитаетсяСПустымСписком()
    {
        // Запись users.json до появления предметов: ключа "Subjects" в ней нет —
        // чтение не должно бросать исключение, список остаётся пустым (засев — миграция Т1b).
        const string legacy = """{"Id":"u1","Username":"kid","ChildMode":{"Enabled":true,"TutorPersonaId":"p-tutor","ProviderKey":"anthropic-api-kids","Model":"sonnet","Spaces":{"lessons":{"Windows":[{"From":"09:00","To":"13:00","Weekdays":[1,3,5]}],"DailyMinutes":45}},"WarnMinutes":5}}""";

        var user = JsonSerializer.Deserialize<User>(legacy, UsersJson)!;

        user.ChildMode.Should().NotBeNull();
        user.ChildMode!.Subjects.Should().BeEmpty("аддитивное поле: старый профиль читается с Subjects=[]");
    }

    [Fact]
    public void ЦветаПредметов_ТолькоИзБелогоСпискаКлючей()
    {
        // Ключи цвета — контракт с валидатором (Т2a) и фронтом: там ключ сопоставляется
        // паре C.* токенов. Разъезд значений ломает и отказы валидатора, и плитки.
        ChildSubject.ColorKeys.Should().Equal("info", "rose", "success", "warning", "accent");
    }

    [Fact]
    public void СтарыйSessionsJson_БезПоля_ЧитаетсяОбычнымЧатом()
    {
        const string legacy = """{"id":"s1","provider":"claude","model":"sonnet"}""";

        var session = JsonSerializer.Deserialize<Session>(legacy, SessionsJson)!;

        session.ChildSpace.Should().BeNull("аддитивное поле: старый sessions.json читается без миграции");
        session.ChildSubject.Should().BeNull("аддитивное поле: старый чат читается без предмета");
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
            ChildSubject = "world",
            OnboardingKind = OnboardingKinds.ChildRules,
        };

        var restored = JsonSerializer.Deserialize<Session>(
            JsonSerializer.Serialize(original, SessionsJson), SessionsJson)!;

        restored.ChildSpace.Should().Be(ChildSpaces.Lessons);
        restored.ChildSubject.Should().Be("world");
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
