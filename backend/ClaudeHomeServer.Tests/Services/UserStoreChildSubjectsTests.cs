using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Т2a (разрез §2): предметы в PUT child-profile — отказы валидатора по каждому коду,
/// живое слияние тьюторов предмета в UserStore.SetChildProfile (по Id, из живого профиля)
/// и SetChildSubjectTutor. Уровень стора, без HTTP — по образцу UserStoreChildRulesTests;
/// HTTP-половина пути (маппинг DTO и сохранение предметов при PUT без subjects) —
/// UsersChildProfileTests.
/// </summary>
public class UserStoreChildSubjectsTests : IDisposable
{
    private const string KidsKey = "kids-api";
    private const string KidsApiKey = "sk-ant-test-kids";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ccs-userstore-subjects_" + Guid.NewGuid().ToString("N"));

    public UserStoreChildSubjectsTests() => Directory.CreateDirectory(_tempDir);

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

    private static ClaudeSubscriptionPool Pool(params (string Key, string? ApiKey)[] subs)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (key, apiKey) in subs) dict[$"ClaudeSubscriptions:{key}:ApiKey"] = apiKey;
        return new ClaudeSubscriptionPool(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    private static ClaudeSubscriptionPool KidsPool() => Pool((KidsKey, KidsApiKey));

    private static User ContainerUser() => new()
    {
        Username = "kid-validator",
        ExecutionEnvironment = ExecutionEnvironments.Container,
    };

    private static ChildSubjectDto Subject(
        string id = "math", string label = "Математика", string glyph = "Аа", string color = "info",
        string providerKey = KidsKey, string model = "sonnet", bool enabled = true, string? tutor = null) =>
        new(id, label, glyph, color, providerKey, model, enabled, tutor, Knowledge: null);

    // Тело с валидным профилем (provider — запись пула с ApiKey) и указанными предметами.
    private static ChildProfileDto Body(params ChildSubjectDto[] subjects) =>
        new(true, [], KidsKey, "sonnet", null, [.. subjects], null, null, null, null);

    // Тело вообще без поля subjects — «предметы не трогать».
    private static ChildProfileDto BodyWithoutSubjects() =>
        new(true, [], KidsKey, "sonnet", null, null, null, null, null, null);

    private static ChildProfileValidator.Reject? Validate(ChildProfileDto req, UserStore users, ClaudeSubscriptionPool? pool = null) =>
        ChildProfileValidator.Validate(req, ContainerUser(), users, pool ?? KidsPool(), RouteProviders());

    // Реестр провайдеров для проверок маршрута (Т5a): deepseek — с ценой, glm — без цены, оба с ключом.
    private static LlmProviderRegistry RouteProviders() => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.test/anthropic",
            ["LlmProviders:deepseek:ApiKey"] = "sk-ds",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-v4-pro",
            ["LlmProviders:deepseek:Models:0:PriceInMissPer1M"] = "0.5",
            ["LlmProviders:deepseek:Models:0:PriceOutPer1M"] = "1.0",
            ["LlmProviders:glm:AnthropicBaseUrl"] = "https://api.glm.test/anthropic",
            ["LlmProviders:glm:ApiKey"] = "sk-glm",
            ["LlmProviders:glm:Models:0:Id"] = "glm-5.2",
        }).Build());

    // Ребёнок с профилем и указанными предметами — прямая запись стора, как сделает её
    // первый PUT или миграция. Тьютора предмета — только SetChildSubjectTutor: первый PUT
    // его не пишет (без живого профиля SetChildProfile обнуляет тьюторов тела), поэтому
    // тьюторы снимем ДО записи — она их обнуляет на тех же объектах.
    private static string SeedChild(UserStore store, params ChildSubject[] subjects)
    {
        var user = store.Add("kid-subj-" + Guid.NewGuid().ToString("N"), "password-1", "user");
        var tutors = subjects.Select(s => (s.Id, s.TutorPersonaId)).ToList();
        store.SetChildProfile(user.Id, new ChildProfile
        {
            Enabled = true,
            ProviderKey = KidsKey,
            Model = "sonnet",
            TutorPersonaId = "tutor-profile",
            Subjects = [.. subjects],
        }, resetRules: false).Should().BeTrue();
        foreach (var (subjectId, tutor) in tutors)
            if (tutor.Length > 0)
                store.SetChildSubjectTutor(user.Id, subjectId, tutor).Should().BeTrue();
        return user.Id;
    }

    // ======== валидатор: по одному кейсу на каждый код, с точным кодом ========

    [Fact]
    public void Валидатор_КлючНеSlug_subject_id_invalid()
    {
        var users = NewStore();
        // Пустой, верхний регистр, пробел, кириллица, длиннее 32, «_» вне алфавита slug.
        foreach (var badId in new[] { "", "Math", "ma th", "математика", new string('x', 33), "math_" })
        {
            var reject = Validate(Body(Subject(id: badId)), users);
            reject?.Code.Should().Be("subject_id_invalid", $"ключ «{badId}» обязан быть отбит");
        }
    }

    [Fact]
    public void Валидатор_ДубльКлючей_subject_id_invalid()
    {
        var users = NewStore();
        // Тот же ключ второй раз — не различим ни для чата (Session.ChildSubject), ни для провижна.
        var reject = Validate(Body(Subject(id: "math"), Subject(id: "math", label: "Алгебра")), users);
        reject.Should().NotBeNull("дубль ключа обязан быть отбит");
        reject!.Code.Should().Be("subject_id_invalid");
    }

    [Fact]
    public void Валидатор_ИмяПустоеИлиДлиннее40_subject_label_invalid()
    {
        var users = NewStore();
        foreach (var badLabel in new[] { "", "   ", new string('о', 41) })
        {
            var reject = Validate(Body(Subject(label: badLabel)), users);
            reject?.Code.Should().Be("subject_label_invalid", $"имя длиной {badLabel.Length} обязано быть отбито");
        }
    }

    [Fact]
    public void Валидатор_ГлифПустойИлиДлиннее3_subject_glyph_invalid()
    {
        var users = NewStore();
        foreach (var badGlyph in new[] { "", " ", "Аааа" })
        {
            var reject = Validate(Body(Subject(glyph: badGlyph)), users);
            reject?.Code.Should().Be("subject_glyph_invalid", $"глиф «{badGlyph}» обязан быть отбит");
        }
    }

    [Fact]
    public void Валидатор_ЦветВнеПалитры_subject_color_invalid()
    {
        var users = NewStore();
        // Ключей пять (ChildSubject.ColorKeys); hex и сторонний ключ — не ключ палитры.
        foreach (var badColor in new[] { "", "blue", "#0a0a0a", "INFO" })
        {
            var reject = Validate(Body(Subject(color: badColor)), users);
            reject?.Code.Should().Be("subject_color_invalid", $"цвет «{badColor}» обязан быть отбит");
        }
    }

    [Fact]
    public void Валидатор_ПровайдерПредметаНеЗаписьСApiKey_provider_not_api_key()
    {
        var users = NewStore();
        // Ключ вне пула и ключ пула без ApiKey (только OAuth) — тот же критерий, что у профиля (И2).
        var absent = Validate(Body(Subject(providerKey: "no-such-sub")), users);
        absent?.Code.Should().Be("provider_not_api_key");

        var pool = Pool((KidsKey, KidsApiKey), ("oauth-sub", null));
        var noKey = Validate(Body(Subject(providerKey: "oauth-sub")), users, pool);
        noKey?.Code.Should().Be("provider_not_api_key", "запись пула без ApiKey — не маршрут ребёнка");
    }

    [Fact]
    public void Валидатор_ВалидныеПредметыИТелоБезSubjects_ОтказаНет()
    {
        var users = NewStore();
        // Тело без поля subjects — нечего проверять: живые предметы сохраняет стор.
        Validate(BodyWithoutSubjects(), users).Should().BeNull();
        // Явный [] — законен (очистить).
        Validate(Body(), users).Should().BeNull();
        // Валидные предметы — включая тьютора в теле: он не читается и не проверяется (Т2a).
        Validate(Body(
            Subject(),
            Subject(id: "russian", label: "Русский язык", glyph: "Аа", color: "rose", enabled: false, tutor: "any-tutor")),
            users).Should().BeNull();
    }

    // ======== живое слияние в SetChildProfile ========

    [Fact]
    public void SetChildProfile_СлияниеПоId_ЖивойТьюторСильнееТела_НовыйIdПустой()
    {
        var store = NewStore();
        var id = SeedChild(store,
            new ChildSubject { Id = "math", Label = "Математика", Glyph = "Аа", Color = "info", TutorPersonaId = "live-tutor" },
            new ChildSubject { Id = "history", Label = "История", Glyph = "Ии", Color = "rose" });

        // Тело правит список и поля, но не персон: чужие tutorPersonaId в сторе не пишутся.
        var edited = new ChildProfile
        {
            Enabled = true,
            ProviderKey = KidsKey,
            Model = "sonnet",
            Subjects =
            [
                new ChildSubject { Id = "math", Label = "Математика", Glyph = "×÷", Color = "info", TutorPersonaId = "dto-tutor" },
                new ChildSubject { Id = "history", Label = "История", Glyph = "Ии", Color = "rose", TutorPersonaId = "dto-tutor-2" },
                new ChildSubject { Id = "physics", Label = "Физика", Glyph = "Ф", Color = "accent", TutorPersonaId = "dto-tutor-3" },
            ],
        };
        store.SetChildProfile(id, edited, resetRules: false).Should().BeTrue();

        var stored = store.GetById(id)!.ChildMode!;
        stored.Subjects.Select(s => s.Id).Should().Equal(new[] { "math", "history", "physics" }, "порядок тела сохраняется");
        stored.Subjects[0].TutorPersonaId.Should().Be("live-tutor", "тьютор предмета — из живого профиля по Id, не из тела");
        stored.Subjects[0].Glyph.Should().Be("×÷", "остальные поля предмета — тела: админ правит их");
        stored.Subjects[1].TutorPersonaId.Should().Be("", "живой тьютор пуст — он и остаётся: тело персону не пишет");
        stored.Subjects[2].TutorPersonaId.Should().Be("", "новый Id — пусто: персону пишет SetChildSubjectTutor (Т2b)");
    }

    [Fact]
    public void SetChildProfile_БезЖивогоПрофиля_ТьюторыТелаОбнулены()
    {
        // Первый PUT, создающий профиль: живого нет — новые все Id, тьютор из тела не
        // пишется (персону пишет только SetChildSubjectTutor, Т2b); остальные поля — тела.
        var store = NewStore();
        var user = store.Add("kid-first-" + Guid.NewGuid().ToString("N"), "password-1", "user");
        store.SetChildProfile(user.Id, new ChildProfile
        {
            Enabled = true,
            ProviderKey = KidsKey,
            Model = "sonnet",
            Subjects =
            [
                new ChildSubject { Id = "math", Label = "Математика", Glyph = "Аа", Color = "info", TutorPersonaId = "dto-tutor" },
                new ChildSubject { Id = "history", Label = "История", Glyph = "Ии", Color = "rose" },
            ],
        }, resetRules: false).Should().BeTrue();

        var stored = store.GetById(user.Id)!.ChildMode!;
        stored.Subjects[0].TutorPersonaId.Should().Be("", "без живого профиля тьютор из тела не пишется");
        stored.Subjects[0].Label.Should().Be("Математика", "остальные поля предмета — тела: админ правит их");
        stored.Subjects[1].TutorPersonaId.Should().Be("");
    }

    [Fact]
    public void SetChildProfile_СигналБезПредметов_ЖивойСписокСохранен_БезЖивогоНормализуетсяВПустой()
    {
        var store = NewStore();
        var id = SeedChild(store,
            new ChildSubject { Id = "math", Label = "Математика", Glyph = "Аа", Color = "info", TutorPersonaId = "live-tutor" });

        // PUT вкладки «Режим и время»: Subjects=null — живые предметы не тронуты (подзадача Т1c).
        store.SetChildProfile(id, new ChildProfile
        {
            Enabled = true, ProviderKey = KidsKey, Model = "sonnet", Subjects = null!,
        }, resetRules: false).Should().BeTrue();

        var kept = store.GetById(id)!.ChildMode!;
        kept.Subjects.Should().ContainSingle("PUT без предметов не стирает предметы").Which.Id.Should().Be("math");
        kept.Subjects[0].TutorPersonaId.Should().Be("live-tutor");

        // Живого профиля нет — сигнал нормализуется в []: в сохранённом профиле
        // Subjects никогда не null (потребители — ChildChatsController — разыменуют его без ?.).
        var plain = store.Add("kid-plain-" + Guid.NewGuid().ToString("N"), "password-1", "user");
        store.SetChildProfile(plain.Id, new ChildProfile
        {
            Enabled = true, ProviderKey = KidsKey, Model = "sonnet", Subjects = null!,
        }, resetRules: false).Should().BeTrue();
        store.GetById(plain.Id)!.ChildMode!.Subjects.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void SetChildProfile_ЯвныйПустойСписок_Очищает()
    {
        var store = NewStore();
        var id = SeedChild(store,
            new ChildSubject { Id = "math", Label = "Математика", Glyph = "Аа", Color = "info", TutorPersonaId = "live-tutor" });

        store.SetChildProfile(id, new ChildProfile
        {
            Enabled = true, ProviderKey = KidsKey, Model = "sonnet", Subjects = [],
        }, resetRules: false).Should().BeTrue();

        store.GetById(id)!.ChildMode!.Subjects.Should().BeEmpty("явный [] в теле — очистить список");
    }

    // ======== SetChildSubjectTutor ========

    [Fact]
    public void SetChildSubjectTutor_ПишетСуществующийПредмет_ЧужойПредметИБезПрофиляFalse()
    {
        var store = NewStore();
        var id = SeedChild(store,
            new ChildSubject { Id = "math", Label = "Математика", Glyph = "Аа", Color = "info" });

        store.SetChildSubjectTutor(id, "math", "subject-tutor").Should().BeTrue();
        store.GetById(id)!.ChildMode!.Subjects[0].TutorPersonaId.Should().Be("subject-tutor");

        store.SetChildSubjectTutor(id, "no-such-subject", "x").Should().BeFalse("предмета нет — писать не во что");
        var plain = store.Add("kid-plain-" + Guid.NewGuid().ToString("N"), "password-1", "user");
        store.SetChildSubjectTutor(plain.Id, "math", "x").Should().BeFalse("профиля нет");
        store.SetChildSubjectTutor("no-such-user", "math", "x").Should().BeFalse("пользователя нет");

        // Следующий PUT с тем же Id сохраняет написанную персону — она переживает правку полей.
        store.SetChildProfile(id, new ChildProfile
        {
            Enabled = true,
            ProviderKey = KidsKey,
            Model = "sonnet",
            Subjects = [new ChildSubject { Id = "math", Label = "Математика", Glyph = "×÷", Color = "accent", TutorPersonaId = "dto-tutor" }],
        }, resetRules: false).Should().BeTrue();
        store.GetById(id)!.ChildMode!.Subjects[0].TutorPersonaId.Should().Be("subject-tutor");
    }

    // ======== конкурентность ========

    [Fact]
    public async Task SetProfile_ParallelWithSetSubjectTutor_TutorKept()
    {
        // Отдача Monitor-лока не FIFO (тот же приём, что у Seed_ParallelWithAdminSet):
        // гонка повторяется, чтобы мутация «брать тьютора предмета из тела» попала в запись
        // хотя бы в одном порядке. При правильном коде любой порядок даёт одно и то же:
        // SetChildSubjectTutor первым — слияние по Id возьмёт "live-tutor" из живого;
        // SetChildProfile первым — SetChildSubjectTutor напишет в новый список по тому же Id.
        var store = NewStore();
        for (var iter = 0; iter < 20; iter++)
        {
            var id = SeedChild(store,
                new ChildSubject { Id = "math", Label = "Математика", Glyph = "Аа", Color = "info" });
            var newProfile = new ChildProfile
            {
                Enabled = true,
                ProviderKey = KidsKey,
                Model = "sonnet",
                Subjects = [new ChildSubject { Id = "math", Label = "Математика", Glyph = "×÷", Color = "info", TutorPersonaId = "dto-tutor" }],
            };

            var tutorTask = Task.Run(() => store.SetChildSubjectTutor(id, "math", "live-tutor"));
            var profileTask = Task.Run(() => store.SetChildProfile(id, newProfile, resetRules: false));
            var all = Task.WhenAll(tutorTask, profileTask);
            (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(30)))).Should().Be(all, "гонка не завершилась");

            store.GetById(id)!.ChildMode!.Subjects.Should().ContainSingle()
                .Which.TutorPersonaId.Should().Be("live-tutor", "репетитор не теряется ни в одном порядке двух вызовов");
        }
    }

    // ======== Т5a (И2′): маршрут предмета — по сторожу на каждый путь ========

    private static ChildSubjectDto RouteSubject(string providerKey, string model, bool? ack = null) =>
        new("math", "Математика", "Аа", "info", providerKey, model, true, null, null, ack);

    [Theory]
    [InlineData("tier:strong")]
    [InlineData("preset:chain-1")]
    [InlineData("default")]
    [InlineData("claude")]
    public void Валидатор_МаркерВМодели_model_not_concrete(string model)
    {
        var reject = Validate(Body(RouteSubject(KidsKey, model)), NewStore());
        reject.Should().NotBeNull();
        reject!.Code.Should().Be("model_not_concrete");
    }

    [Fact]
    public void Валидатор_ОблачнаяМодельБезЦены_model_unpriced()
    {
        var reject = Validate(Body(RouteSubject("glm", "glm-5.2", ack: true)), NewStore());
        reject.Should().NotBeNull();
        reject!.Code.Should().Be("model_unpriced");
    }

    [Fact]
    public void Валидатор_НеПулБезСогласия_route_risk_unacknowledged_СоСогласием_Проходит()
    {
        var users = NewStore();
        var reject = Validate(Body(RouteSubject("deepseek", "deepseek-v4-pro")), users);
        reject.Should().NotBeNull();
        reject!.Code.Should().Be("route_risk_unacknowledged");
        Validate(Body(RouteSubject("deepseek", "deepseek-v4-pro", ack: true)), users).Should().BeNull();
    }

    [Fact]
    public void Валидатор_ЖивоеСогласиеТогоЖеПровайдера_ЗаменяетФлагВТеле()
    {
        var users = NewStore();
        var user = ContainerUser();
        user.ChildMode = new ChildProfile
        {
            Enabled = true,
            Subjects = [new ChildSubject { Id = "math", ProviderKey = "deepseek", RouteAcknowledgedAt = DateTime.UtcNow }],
        };
        var req = Body(RouteSubject("deepseek", "deepseek-v4-pro"));
        ChildProfileValidator.Validate(req, user, users, KidsPool(), RouteProviders()).Should().BeNull();
        // Живое согласие относится к другому провайдеру — не считается (смена providerKey его не переживает).
        user.ChildMode.Subjects[0].ProviderKey = "glm";
        ChildProfileValidator.Validate(req, user, users, KidsPool(), RouteProviders())!.Code
            .Should().Be("route_risk_unacknowledged");
    }

    [Fact]
    public void Валидатор_МодельЧужогоПровайдера_model_not_of_provider()
    {
        var reject = Validate(Body(RouteSubject("deepseek", "glm-5.2", ack: true)), NewStore());
        reject.Should().NotBeNull();
        reject!.Code.Should().Be("model_not_of_provider");
    }

    [Fact]
    public void Валидатор_ЗаписьПулаСТокеномПодписки_provider_not_api_key()
    {
        var pool = new ClaudeSubscriptionPool(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ClaudeSubscriptions:mixed:ApiKey"] = "sk-ant-mixed",
            ["ClaudeSubscriptions:mixed:OAuthToken"] = "oauth-mixed",
        }).Build());
        var reject = Validate(Body(RouteSubject("mixed", "sonnet")), NewStore(), pool);
        reject.Should().NotBeNull();
        reject!.Code.Should().Be("provider_not_api_key");
    }

    private static ChildProfile ProfileWith(ChildSubject subject) => new()
    {
        Enabled = true, ProviderKey = KidsKey, Model = "sonnet", Subjects = [subject],
    };

    private static ChildSubject Routed(string provider, DateTime? ack = null, string? by = null) => new()
    {
        Id = "math", Label = "Математика", Glyph = "Аа", Color = "info",
        ProviderKey = provider, Model = "m", RouteAcknowledgedAt = ack, RouteAcknowledgedBy = by,
    };

    [Fact]
    public void Согласие_ТотЖеПровайдер_ПрежняяПараСохраняется()
    {
        var store = NewStore();
        var at = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var id = SeedChild(store, Routed("deepseek", at, "admin-1"));
        // Повторный PUT без нового согласия и с новым кандидатом — живая пара не переставляется.
        store.SetChildProfile(id, ProfileWith(Routed("deepseek")), false).Should().BeTrue();
        var kept = store.GetById(id)!.ChildMode!.Subjects.Single();
        (kept.RouteAcknowledgedAt, kept.RouteAcknowledgedBy).Should().Be((at, "admin-1"));
        store.SetChildProfile(id, ProfileWith(Routed("deepseek", DateTime.UtcNow, "admin-2")), false);
        store.GetById(id)!.ChildMode!.Subjects.Single().RouteAcknowledgedBy.Should().Be("admin-1");
    }

    [Fact]
    public void Согласие_СменаProviderKey_Сбрасывается()
    {
        var store = NewStore();
        var id = SeedChild(store, Routed("deepseek", DateTime.UtcNow, "admin-1"));
        store.SetChildProfile(id, ProfileWith(Routed("glm")), false).Should().BeTrue();
        var s = store.GetById(id)!.ChildMode!.Subjects.Single();
        s.RouteAcknowledgedAt.Should().BeNull("согласие от старого ProviderKey не переживает смену");
        s.RouteAcknowledgedBy.Should().BeNull();
    }

    [Fact]
    public void Согласие_СменаProviderKey_СНовымСогласием_БерётсяКандидат()
    {
        var store = NewStore();
        var id = SeedChild(store, Routed("deepseek", new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), "admin-1"));
        var at = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        store.SetChildProfile(id, ProfileWith(Routed("glm", at, "admin-2")), false);
        var s = store.GetById(id)!.ChildMode!.Subjects.Single();
        (s.RouteAcknowledgedAt, s.RouteAcknowledgedBy).Should().Be((at, "admin-2"));
    }

    [Fact]
    public async Task Ack_ParallelWithProviderChange_AckReset()
    {
        // Два PUT одновременно: A меняет ProviderKey (без согласия), B пересохраняет прежний ProviderKey,
        // опираясь на живое согласие. При любом порядке слияние под замком по ЖИВОМУ предмету даёт без
        // согласия: A→B — B сверяется с уже сменённым ключом, B→A — A сбрасывает. Мутация «не сбрасывать
        // согласие» оставляет его в обоих порядках.
        var store = NewStore();
        for (var iter = 0; iter < 20; iter++)
        {
            var id = SeedChild(store, Routed("deepseek", DateTime.UtcNow, "admin-1"));
            var change = Task.Run(() => store.SetChildProfile(id, ProfileWith(Routed("glm")), false));
            var resave = Task.Run(() => store.SetChildProfile(id, ProfileWith(Routed("deepseek")), false));
            var all = Task.WhenAll(change, resave);
            (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(30)))).Should().Be(all, "гонка не завершилась");

            var s = store.GetById(id)!.ChildMode!.Subjects.Single();
            s.RouteAcknowledgedAt.Should().BeNull($"итерация {iter}: согласие от старого ProviderKey не переживает смену");
        }
    }
}
