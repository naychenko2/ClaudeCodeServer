using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Т2a (разрез §2, Б17c): предметы в PUT child-profile — отказы валидатора по каждому коду
/// плитки (маршрута и репетитора у предмета больше нет), живое слияние маркеров предмета
/// (Migrated, KnowledgeMoved) в UserStore.SetChildProfile по Id. Уровень стора, без HTTP —
/// по образцу UserStoreChildRulesTests;
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
        bool enabled = true) =>
        new(id, label, glyph, color, enabled);

    // Тело с валидным профилем (provider — запись пула с ApiKey) и указанными предметами.
    private static ChildProfileDto Body(params ChildSubjectDto[] subjects) =>
        new(true, [], KidsKey, "sonnet", null, [.. subjects], null, null, null, null, true);

    // Тело вообще без поля subjects — «предметы не трогать».
    private static ChildProfileDto BodyWithoutSubjects() =>
        new(true, [], KidsKey, "sonnet", null, null, null, null, null, null, true);

    private static ChildProfileValidator.Reject? Validate(ChildProfileDto req, UserStore users, ClaudeSubscriptionPool? pool = null) =>
        ChildProfileValidator.Validate(req, ContainerUser(), users, pool ?? KidsPool(), RouteProviders());

    // Реестр провайдеров для проверок маршрута профиля: deepseek — с ценой, glm — без цены, оба с ключом.
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

    // Ребёнок с профилем и указанными предметами — прямая запись стора, как сделает её первый PUT.
    private static string SeedChild(UserStore store, params ChildSubject[] subjects)
    {
        var user = store.Add("kid-subj-" + Guid.NewGuid().ToString("N"), "password-1", "user");
        store.SetChildProfile(user.Id, new ChildProfile
        {
            Enabled = true,
            ProviderKey = KidsKey,
            Model = "sonnet",
            TutorPersonaId = "tutor-profile",
            Subjects = [.. subjects],
        }, resetRules: false).Should().BeTrue();
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
        foreach (var badColor in new[] { "", "blue", "#0a0a0a", "INFO" })
        {
            var reject = Validate(Body(Subject(color: badColor)), users);
            reject?.Code.Should().Be("subject_color_invalid", $"цвет «{badColor}» обязан быть отбит");
        }
    }

    [Fact]
    public void Валидатор_ВалидныеПредметыИТелоБезSubjects_ОтказаНет()
    {
        var users = NewStore();
        Validate(BodyWithoutSubjects(), users).Should().BeNull();
        Validate(Body(), users).Should().BeNull();
        Validate(Body(Subject(), Subject(id: "russian", label: "Русский язык", color: "rose", enabled: false)), users)
            .Should().BeNull("предмет — только плитка: маршрута и репетитора в нём нет");
    }

    // ======== живое слияние в SetChildProfile ========

    [Fact]
    public void SetChildProfile_СлияниеПоId_МаркерыИзЖивогоПредмета_НовыйIdБезМаркеров()
    {
        var store = NewStore();
        var id = SeedChild(store,
            new ChildSubject { Id = "math", Label = "Математика", Glyph = "Аа", Color = "info", Migrated = true, KnowledgeMoved = true },
            new ChildSubject { Id = "history", Label = "История", Glyph = "Ии", Color = "rose" });

        // Тело правит список и поля, но не маркеры: Migrated/KnowledgeMoved из тела не читаются.
        var edited = new ChildProfile
        {
            Enabled = true,
            ProviderKey = KidsKey,
            Model = "sonnet",
            Subjects =
            [
                new ChildSubject { Id = "math", Label = "Математика", Glyph = "×÷", Color = "info" },
                new ChildSubject { Id = "history", Label = "История", Glyph = "Ии", Color = "rose", Migrated = true },
                new ChildSubject { Id = "physics", Label = "Физика", Glyph = "Ф", Color = "accent", KnowledgeMoved = true },
            ],
        };
        store.SetChildProfile(id, edited, resetRules: false).Should().BeTrue();

        var stored = store.GetById(id)!.ChildMode!;
        stored.Subjects.Select(s => s.Id).Should().Equal(new[] { "math", "history", "physics" }, "порядок тела сохраняется");
        stored.Subjects[0].Migrated.Should().BeTrue("маркер переноса — из живого предмета по Id");
        stored.Subjects[0].KnowledgeMoved.Should().BeTrue("пометка «знания перенесите» — из живого предмета");
        stored.Subjects[0].Glyph.Should().Be("×÷", "остальные поля предмета — тела: админ правит их");
        stored.Subjects[1].Migrated.Should().BeFalse("у живого предмета маркера не было — из тела он не читается");
    }

    [Fact]
    public void SetChildProfile_БезЖивогоПрофиля_ПредметыТела()
    {
        var store = NewStore();
        var user = store.Add("kid-first-" + Guid.NewGuid().ToString("N"), "password-1", "user");
        store.SetChildProfile(user.Id, new ChildProfile
        {
            Enabled = true,
            ProviderKey = KidsKey,
            Model = "sonnet",
            Subjects = [new ChildSubject { Id = "math", Label = "Математика", Glyph = "Аа", Color = "info" }],
        }, resetRules: false).Should().BeTrue();

        store.GetById(user.Id)!.ChildMode!.Subjects.Should().ContainSingle().Which.Label.Should().Be("Математика");
    }

    [Fact]
    public void SetChildProfile_СигналБезПредметов_ЖивойСписокСохранен_БезЖивогоНормализуетсяВПустой()
    {
        var store = NewStore();
        var id = SeedChild(store,
            new ChildSubject { Id = "math", Label = "Математика", Glyph = "Аа", Color = "info" });

        // PUT вкладки «Режим и время»: Subjects=null — живые предметы не тронуты (подзадача Т1c).
        store.SetChildProfile(id, new ChildProfile
        {
            Enabled = true, ProviderKey = KidsKey, Model = "sonnet", Subjects = null!,
        }, resetRules: false).Should().BeTrue();

        var kept = store.GetById(id)!.ChildMode!;
        kept.Subjects.Should().ContainSingle("PUT без предметов не стирает предметы").Which.Id.Should().Be("math");

        // Живого профиля нет — сигнал нормализуется в []: в сохранённом профиле Subjects никогда не null.
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
            new ChildSubject { Id = "math", Label = "Математика", Glyph = "Аа", Color = "info" });

        store.SetChildProfile(id, new ChildProfile
        {
            Enabled = true, ProviderKey = KidsKey, Model = "sonnet", Subjects = [],
        }, resetRules: false).Should().BeTrue();

        store.GetById(id)!.ChildMode!.Subjects.Should().BeEmpty("явный [] в теле — очистить список");
    }
}
