using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// API секций промптов и типовых умений специальностей: каталог дефолтов, слои в
// GET/PUT settings (валидация + отражение), посекочное наследование через стор,
// материализация профиля роли при создании персоны и кнопка «Применить типовые».
// AI-подбор целей имитируется стабом ICheapTextRunner (цель notes/personal существует
// у каждого пользователя); скиллы из каталога хоста в тестах недоступны — контракт
// «отсутствующий скилл пропускается молча» проверяется на несуществующем имени.
[Trait("Category", "Slow")]
public class SpecialtyPromptSectionsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _user;

    public SpecialtyPromptSectionsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateAuthenticatedClient();
        _user = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    private static readonly object EmptyLayer = new
    {
        specialties = new Dictionary<string, object>(),
        presets = Array.Empty<object>(),
    };

    // --- Каталог дефолтов (GET /api/specialties/prompt-sections) ---

    [Fact]
    public async Task Catalog_СекцииИДефолтыПоСпециальностям()
    {
        var catalog = await (await _admin.GetAsync("/api/specialties/prompt-sections"))
            .Content.ReadFromJsonAsync<JsonElement>();

        catalog.GetProperty("textLimit").GetInt32().Should().Be(SpecialtyPromptPresets.SectionTextLimit);
        catalog.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("id").GetString())
            .Should().Equal("history", "codeGraph", "processes", "roleRules");

        var specialties = catalog.GetProperty("specialties");
        specialties.EnumerateObject().Select(p => p.Name).Should().HaveCount(14,
            "все специальности, кроме none");
        specialties.EnumerateObject().Should().NotContain(p => p.Name == "none");

        // Дефолты аналитика: история вкл с текстом, граф кода — выкл, но текст есть
        var analyst = specialties.GetProperty("analyst");
        var history = analyst.GetProperty("sections").EnumerateArray()
            .Single(s => s.GetProperty("id").GetString() == "history");
        history.GetProperty("enabled").GetBoolean().Should().BeTrue();
        history.GetProperty("text").GetString().Should().Contain("dossier_lookup");
        var codeGraph = analyst.GetProperty("sections").EnumerateArray()
            .Single(s => s.GetProperty("id").GetString() == "codeGraph");
        codeGraph.GetProperty("enabled").GetBoolean().Should().BeFalse();
        codeGraph.GetProperty("text").GetString().Should().NotBeNullOrEmpty(
            "выключенная секция включается вручную — текст нужен");

        // Типовой профиль библиотекаря — Knowledge+Notes из дефолтов кода
        var librarianProfile = specialties.GetProperty("librarian").GetProperty("defaultBindings");
        librarianProfile.EnumerateArray().Select(b => b.GetProperty("type").GetString())
            .Should().Equal("knowledge", "notes");
    }

    // --- Слои в GET/PUT settings ---

    [Fact]
    public async Task SettingsPut_СекцииИПрофиль_ОтражаютсяВГлобальномСлое()
    {
        var put = await _admin.PutAsJsonAsync("/api/specialties/settings/global", new
        {
            specialties = new Dictionary<string, object>
            {
                ["analyst"] = new
                {
                    promptSections = new object[]
                    {
                        new { id = "history", enabled = true, text = "Общий текст истории" },
                    },
                    defaultBindings = new object[]
                    {
                        new { type = "notes", mode = "auto", condition = "когда нужны заметки" },
                    },
                },
            },
            presets = Array.Empty<object>(),
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var settings = await (await _user.GetAsync("/api/specialties/settings"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var entry = settings.GetProperty("global").GetProperty("specialties").GetProperty("analyst");
        var section = entry.GetProperty("promptSections").EnumerateArray().Single();
        section.GetProperty("id").GetString().Should().Be("history");
        section.GetProperty("text").GetString().Should().Be("Общий текст истории");
        entry.GetProperty("defaultBindings").EnumerateArray().Single()
            .GetProperty("type").GetString().Should().Be("notes");

        // Эффективный резолв: владелец без своего слоя видит секцию админа
        var userId = SecondUserId();
        _factory.Services.GetRequiredService<SpecialtySettingsStore>()
            .EffectivePromptSections(userId, PersonaSpecialty.Analyst)
            .Single(s => s.Id == "history").Text.Should().Be("Общий текст истории");

        // Уборка: общий слой чистим пустым слоем
        (await _admin.PutAsJsonAsync("/api/specialties/settings/global", EmptyLayer))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SettingsPut_ЯвныйOffВладельцаПерекрываетOnАдмина()
    {
        await _admin.PutAsJsonAsync("/api/specialties/settings/global", new
        {
            specialties = new Dictionary<string, object>
            {
                ["analyst"] = new { promptSections = new object[] { new { id = "roleRules", enabled = true } } },
            },
            presets = Array.Empty<object>(),
        });
        await _user.PutAsJsonAsync("/api/specialties/settings", new
        {
            specialties = new Dictionary<string, object>
            {
                ["analyst"] = new { promptSections = new object[] { new { id = "ruleRules", enabled = false } } },
            },
            presets = Array.Empty<object>(),
        });

        var userId = SecondUserId();
        _factory.Services.GetRequiredService<SpecialtySettingsStore>()
            .EffectivePromptSections(userId, PersonaSpecialty.Analyst)
            .Should().NotContain(s => s.Id == "ruleRules",
                "заданный off владельца перекрывает on админа");

        // Уборка
        await _admin.PutAsJsonAsync("/api/specialties/settings/global", EmptyLayer);
        await _user.PutAsJsonAsync("/api/specialties/settings", EmptyLayer);
    }

    [Fact]
    public async Task SettingsPut_НевалидныеСекцииИПрофили_400()
    {
        var badSection = await _admin.PutAsJsonAsync("/api/specialties/settings/global", new
        {
            specialties = new Dictionary<string, object>
            {
                ["analyst"] = new { promptSections = new object[] { new { id = "no-such", enabled = true } } },
            },
            presets = Array.Empty<object>(),
        });
        badSection.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var badSkill = await _user.PutAsJsonAsync("/api/specialties/settings", new
        {
            specialties = new Dictionary<string, object>
            {
                ["analyst"] = new
                {
                    defaultBindings = new object[] { new { type = "skill", mode = "auto" } },
                },
            },
            presets = Array.Empty<object>(),
        });
        badSkill.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await _user.PutAsJsonAsync("/api/specialties/settings", EmptyLayer);
    }

    // --- Материализация типовых умений ---

    // Стаб с последовательностью ответов: i-й one-shot вызов получает i-й ответ
    // (исчерпана последовательность — пустой массив «ничего не подошло»)
    private sealed class SequencedCheap(params string[] answers) : ICheapTextRunner
    {
        private int _call;

        public bool UsesLocal(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult(_call < answers.Length ? answers[_call++] : "[]");
        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>(_call < answers.Length ? answers[_call++] : "[]");
        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task PersonaCreate_ПрофильРолиМатериализуетсяБезAutoBindings()
    {
        // AI «подобрал» заметки владельца (personal существует у каждого); профиль —
        // назначение пользователю B9, чтобы дефолты кода аналитика не мешали
        using var factory = new TestWebApplicationFactory
        {
            ExtraServices = s => s.AddSingleton<ICheapTextRunner>(new SequencedCheap(
                "[{\"type\":\"notes\",\"target\":\"personal\"}]")),
        };
        var user = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var userId = factory.Services.GetRequiredService<UserStore>()
            .GetAll().Single(u => u.Username == TestWebApplicationFactory.SecondUsername).Id;

        var admin = factory.CreateAuthenticatedClient();
        (await admin.PutAsJsonAsync($"/api/specialties/settings/user/{userId}", new
        {
            specialties = new Dictionary<string, object>
            {
                ["analyst"] = new
                {
                    defaultBindings = new object[]
                    {
                        new { type = "notes", mode = "always", condition = "когда полезны заметки" },
                    },
                },
            },
            presets = Array.Empty<object>(),
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        // Создание БЕЗ autoBindings: профиль материализуется сам (модель «копия при создании»)
        var response = await user.PostAsJsonAsync("/api/personas", new
        {
            name = "Аналитик с типовыми умениями",
            specialty = "analyst",
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var persona = await response.Content.ReadFromJsonAsync<JsonElement>();

        var binding = persona.GetProperty("bindings").EnumerateArray().Single();
        binding.GetProperty("type").GetString().Should().Be("notes");
        binding.GetProperty("target").GetString().Should().Be("personal");
        binding.GetProperty("condition").GetString().Should().Be("когда полезны заметки",
            "условие — из профиля роли, а не из ответа модели");
        binding.GetProperty("mode").GetString().Should().Be("always",
            "режим — из профиля роли");
    }

    [Fact]
    public async Task PersonaCreate_ОтсутствующийСкиллПропускаетсяМолча()
    {
        using var factory = new TestWebApplicationFactory();
        var user = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var userId = factory.Services.GetRequiredService<UserStore>()
            .GetAll().Single(u => u.Username == TestWebApplicationFactory.SecondUsername).Id;

        var admin = factory.CreateAuthenticatedClient();
        (await admin.PutAsJsonAsync($"/api/specialties/settings/user/{userId}", new
        {
            specialties = new Dictionary<string, object>
            {
                ["consultant"] = new
                {
                    defaultBindings = new object[]
                    {
                        new { type = "skill", mode = "auto", condition = "по сценарию", skillName = "нет-такого-скилла" },
                    },
                },
            },
            presets = Array.Empty<object>(),
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await user.PostAsJsonAsync("/api/personas", new
        {
            name = "Консультант без скиллов",
            specialty = "consultant",
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "отсутствующий скилл пропускается молча — создание не падает");
        var persona = await response.Content.ReadFromJsonAsync<JsonElement>();
        persona.GetProperty("bindings").ValueKind.Should().Be(JsonValueKind.Null,
            "единственная запись профиля не материализовалась — привязок нет");
    }

    [Fact]
    public async Task ApplyDefaults_КнопкаПрименитьТиповые()
    {
        // Последовательность ответов стаба: создание персоны — «ничего не подошло»,
        // кнопка — подобраны заметки владельца
        using var factory = new TestWebApplicationFactory
        {
            ExtraServices = s => s.AddSingleton<ICheapTextRunner>(new SequencedCheap(
                "[]",
                "[{\"type\":\"notes\",\"target\":\"personal\"}]")),
        };
        var user = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        // Без специальности — 400 (типовых умений роли нет)
        var plain = await (await user.PostAsJsonAsync("/api/personas", new { name = "Без роли" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        (await user.PostAsync(
                $"/api/personas/{plain.GetProperty("id").GetString()}/bindings/apply-defaults", null))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Секретарь (дефолт кода: Notes+ProjectTasks): при создании подбор пуст —
        // персона без привязок; кнопка доводит типовые умения вручную
        var created = await (await user.PostAsJsonAsync("/api/personas", new
        {
            name = "Секретарь",
            specialty = "secretary",
        })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString()!;
        created.GetProperty("bindings").ValueKind.Should().Be(JsonValueKind.Null,
            "подбор при создании ничего не дал — привязок нет");

        var applied = await (await user.PostAsync($"/api/personas/{id}/bindings/apply-defaults", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        applied.GetProperty("applied").GetInt32().Should().Be(1);
        applied.GetProperty("persona").GetProperty("bindings").EnumerateArray()
            .Should().Contain(b => b.GetProperty("type").GetString() == "notes"
                && b.GetProperty("target").GetString() == "personal"
                && b.GetProperty("condition").GetString() == "когда нужно найти или записать мысль",
                "условие и режим — из типового профиля секретаря, а не из ответа модели");

        // Повторный вызов ничего не добавляет: дубликат отброшен валидацией
        var again = await (await user.PostAsync($"/api/personas/{id}/bindings/apply-defaults", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        again.GetProperty("applied").GetInt32().Should().Be(0,
            "те же цели уже привязаны — дубликат type+target+path не проходит");
    }

    private string SecondUserId() =>
        _factory.Services.GetRequiredService<UserStore>()
            .GetAll().Single(u => u.Username == TestWebApplicationFactory.SecondUsername).Id;
}
