using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// KM-B1e: админ-ручка PUT /api/users/{id}/child-profile (ADR-041 «Контракт», ручка №10).
/// В тестовом хосте — запись пула с ApiKey (И2: ребёнку положена отдельная запись с ключом,
/// не подписка): без неё проверка provider_not_api_key срабатывала бы и на валидном теле,
/// а «валидный → 200» был бы зелёным на пустоте.
/// </summary>
[Trait("Category", "Host")]
public class UsersChildProfileTests : IClassFixture<UsersChildProfileTests.Hosts>
{
    /// <summary>
    /// Стаб вещания живёт на фикстуре, а не на тест-классе: xunit строит тест-класс на каждый
    /// тест, а хост — один на класс; экземпляр первого теста уже стоит в DI, и правка
    /// «своего» экземпляра в следующем тесте до него не доходит.
    /// Стаб бросает только по указанному владельцу: стартовый провижн хоста и чужие тесты
    /// он не трогает; им тест вызывает отказ провижнера тьютора (путь 500 + откат профиля) —
    /// другой управляемой точки отказа провижера в хосте нет.
    /// </summary>
    public sealed class Hosts : TestWebApplicationFactory
    {
        // internal: тип поля — приватный вложенный класс тест-класса, public-поле было бы
        // менее доступным, чем его тип (CS0052)
        internal readonly ThrowingBroadcaster BroadcasterStub = new();

        // Знания (К1b): настоящий KnowledgeService с подменённым HTTP — тот же приём, что
        // SessionCoreTestHost.cs:136 и KnowledgeServiceNamespaceTests: проверка привязки идёт по
        // настоящему ListDatasetsAsync (обход пагинации, срезание префикса неймспейса), список
        // датасетов диктует тест. Подменяется один сервис, не IHttpClientFactory хоста: чужие
        // HTTP-тесты этого фикстура не задевает.
        internal readonly FakeDify DifyStub = new();
        // IsConfigured читает ровно эти поля (KnowledgeService.cs:228): тесту, которому нужен
        // «Dify не настроен», достаточно погасить ApiUrl — тот же состояние инстанса без Dify.
        internal readonly DifyOptions Dify = new() { ApiUrl = "http://dify.test/v1", ApiKey = "test-key" };

        public Hosts() => ExtraServices = services =>
        {
            services.AddSingleton<ISessionBroadcaster>(BroadcasterStub);
            services.AddSingleton(sp =>
            {
                var http = new Mock<IHttpClientFactory>();
                http.Setup(f => f.CreateClient(It.IsAny<string>()))
                    .Returns(() => new HttpClient(DifyStub, disposeHandler: false));
                return new KnowledgeService(http.Object, Microsoft.Extensions.Options.Options.Create(Dify),
                    sp.GetRequiredService<WorkspaceKnowledgeStore>());
            });
        };
    }

    private const string KidsKey = "kids-api";
    private const string KidsApiKey = "sk-ant-test-kids";
    private const string KidsPassword = "kids-test-password";

    // Датасеты подменённого Dify (id → логическое имя, в той форме, в какой их отдаёт
    // ListDatasetsAsync): своя самостоятельная — привязать можно; своя база заметок — нельзя;
    // общая без префикса — можно. Свою базу конкретного адмена тест добавляет сам: учётки
    // пользователей в общем сторе фикстуры уникальны на тест, и префикс имени датасета обязан
    // называть ровно этого пользователя.
    private static readonly (string Id, string Name)[] BaseDatasets =
    [
        ("d-kb", "testuser:kb:математика"),
        ("d-notes", "testuser:notes"),
        ("d-global", "учебники"),
    ];

    private static string DatasetsJson(params (string Id, string Name)[] extra) =>
        "[" + string.Join(",", BaseDatasets.Concat(extra)
            .Select(d => $$"""{"id":"{{d.Id}}","name":"{{d.Name}}","document_count":1}""")) + "]";

    private static readonly DateTime RulesT0 = new(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime RulesT1 = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    private readonly Hosts _hosts;
    private readonly HttpClient _admin;
    private readonly UserStore _users;
    private readonly IChildModePolicy _policy;

    public UsersChildProfileTests(Hosts hosts)
    {
        _hosts = hosts;
        _hosts.ExtraConfig[$"ClaudeSubscriptions:{KidsKey}:ApiKey"] = KidsApiKey;
        // Не-Anthropic провайдер с ценой (тот же набор, что у ChildRouteKit): тестам согласия на
        // риск маршрута (Т5a, Х1) нужен предмет на чужом поставщике, который резолвится в реестре
        // хоста — иначе отказ придёт как provider_not_api_key, а не как проверка согласия.
        _hosts.ExtraConfig["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.test/anthropic";
        _hosts.ExtraConfig["LlmProviders:deepseek:ApiKey"] = "sk-ds";
        _hosts.ExtraConfig["LlmProviders:deepseek:Models:0:Id"] = "deepseek-v4-pro";
        _hosts.ExtraConfig["LlmProviders:deepseek:Models:0:PriceInMissPer1M"] = "0.5";
        _hosts.ExtraConfig["LlmProviders:deepseek:Models:0:PriceOutPer1M"] = "1.0";
        _admin = _hosts.CreateAuthenticatedClient();
        _users = _hosts.Services.GetRequiredService<UserStore>();
        _policy = _hosts.Services.GetRequiredService<IChildModePolicy>();
        // Список датасетов подменённого Dify общий для тестов привязки; «Dify отказал» —
        // по флагу, его каждый тест ставит сам и снимает в finally.
        _hosts.DifyStub.DatasetsJson = DatasetsJson();
        _hosts.DifyStub.Throw = false;
    }

    private string AdminId => _users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    /// <summary>Новый пользователь с container-средой через админский POST (ребёнка заводим
    /// сразу в песочнице: среда после чатов не меняется).</summary>
    private async Task<string> NewContainerUser(string name)
    {
        var resp = await _admin.PostAsJsonAsync("/api/users", new
        {
            username = name,
            password = "kids-test-password",
            role = "user",
            executionEnvironment = ExecutionEnvironments.Container,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return body.GetProperty("id").GetString()!;
    }

    /// <summary>Валидное тело включения: провайдер — запись пула с ApiKey, «Уроки» с окном
    /// и дневным лимитом, опекун — существующий пользователь.</summary>
    private object ValidBody(string guardianId = "") => new
    {
        enabled = true,
        guardianUserIds = guardianId.Length > 0 ? new[] { guardianId } : Array.Empty<string>(),
        providerKey = KidsKey,
        model = "sonnet",
        spaces = new Dictionary<string, object>
        {
            ["lessons"] = new
            {
                windows = new[] { new { from = "09:00", to = "20:00", weekdays = new[] { 1, 3, 5 } } },
                dailyMinutes = 40,
            },
        },
        dailyBudgetUsd = 2,
        monthlyBudgetUsd = 40,
        warnMinutes = 5,
    };

    private async Task<(HttpStatusCode Status, JsonElement Body)> PutAsync(string id, object body)
    {
        var resp = await _admin.PutAsJsonAsync($"/api/users/{id}/child-profile", body);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length > 0 ? JsonSerializer.Deserialize<JsonElement>(text) : default);
    }

    private static string CodeOf(JsonElement body) =>
        body.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";

    /// <summary>Включить режим и вернуть id ребёнка (общая преамбула тестов правил/гонки).</summary>
    private async Task<string> EnableChild(string name)
    {
        var id = await NewContainerUser(name);
        var (status, body) = await PutAsync(id, ValidBody());
        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("tutorPersonaId").GetString()!.Should().NotBeNullOrEmpty();
        return id;
    }

    /// <summary>Прямая запись согласия правил в стор — как сделает их свой agree (KM-B6b):
    /// отдельной ручки согласия в этом шаге нет, а её запись обязана переживать PUT профиля.</summary>
    private void SeedRules(string childId, DateTime at, string summary)
    {
        var profile = _users.GetById(childId)!.ChildMode!;
        profile.RulesAgreedAt = at;
        profile.RulesSummary = summary;
    }

    /// <summary>Новый адмен через админский POST: для «своей базы другого адмена» (имя должно
    /// попасть в others валидатора) и для повторного PUT от другого адмена.</summary>
    private async Task<string> NewAdminUser(string name)
    {
        var resp = await _admin.PostAsJsonAsync("/api/users", new
        {
            username = name,
            password = KidsPassword,
            role = "admin",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    /// <summary>Тело с одним предметом «math» и привязкой базы знаний (К1b): datasetId —
    /// привязать, null — снять. Остальные поля предмета валидны: отказ не должен прийти
    /// из них. Пустой/отсутствующий datasetId — снятие, а не привязка. guardians — та же длина пути запроса, что у теста согласия правил.</summary>
    private static Dictionary<string, object?> BodyWithSubjectKnowledge(string? datasetId, string[]? guardians = null) => new()
    {
        ["enabled"] = true,
        ["providerKey"] = KidsKey,
        ["model"] = "sonnet",
        ["spaces"] = new Dictionary<string, object>(),
        ["guardianUserIds"] = guardians ?? Array.Empty<string>(),
        ["subjects"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["id"] = "math",
                ["label"] = "Математика",
                ["glyph"] = "М",
                ["color"] = "info",
                ["providerKey"] = KidsKey,
                ["model"] = "sonnet",
                ["knowledge"] = datasetId is null ? (object?)null : new { datasetId },
            },
        },
    };

    /// <summary>Привязка сохранённого предмета — из стора, а не из ответа.</summary>
    private ChildSubjectKnowledge StoredKnowledge(string childId, string subjectId = "math") =>
        _users.GetById(childId)!.ChildMode!.Subjects.Single(s => s.Id == subjectId).Knowledge!;

    /// <summary>Тело с предметом «math» на чужом поставщике (deepseek — в реестре хоста с ценой):
    /// acknowledgeRouteRisk — true / null (поля нет) / false: трёхзначный флаг согласия (Х1).
    /// Остальные поля предмета валидны: отказ не должен прийти из них. guardians — та же длина
    /// пути запроса, что у тестов гонки.</summary>
    private static Dictionary<string, object?> BodyWithRoutedSubject(object? acknowledge, string[]? guardians = null) => new()
    {
        ["enabled"] = true,
        ["providerKey"] = KidsKey,
        ["model"] = "sonnet",
        ["spaces"] = new Dictionary<string, object>(),
        ["guardianUserIds"] = guardians ?? Array.Empty<string>(),
        ["subjects"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["id"] = "math",
                ["label"] = "Математика",
                ["glyph"] = "М",
                ["color"] = "info",
                ["providerKey"] = "deepseek",
                ["model"] = "deepseek-v4-pro",
                ["acknowledgeRouteRisk"] = acknowledge,
            },
        },
    };

    /// <summary>Предмет сохранённого профиля — из стора, а не из ответа.</summary>
    private ChildSubject StoredSubject(string childId, string subjectId = "math") =>
        _users.GetById(childId)!.ChildMode!.Subjects.Single(s => s.Id == subjectId);

    // --- отказы таблицы ADR: по одному кейсу на каждый, с точным кодом ---

    [Fact]
    public async Task Put_НеАдмин_403()
    {
        var child = await NewContainerUser("kid-403");
        var client = _hosts.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var resp = await client.PutAsJsonAsync($"/api/users/{child}/child-profile", ValidBody());
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // режим не включился: права на запись у не-админа не было
        _users.GetById(child)!.ChildMode.Should().BeNull();
    }

    [Fact]
    public async Task Put_НетПользователя_404_user_not_found()
    {
        var (status, body) = await PutAsync("no-such-user-id", ValidBody());
        status.Should().Be(HttpStatusCode.NotFound);
        CodeOf(body).Should().Be("user_not_found");
    }

    [Fact]
    public async Task Put_ЛокальнаяСреда_400_not_container()
    {
        // Обычный пользователь (local) — режим не включается: ход ребёнка живёт в песочнице,
        // а среда после появления чатов не меняется (guard Update).
        var resp = await _admin.PostAsJsonAsync("/api/users", new
        {
            username = "kid-local",
            password = "kids-test-password",
            role = "user",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        var (status, body) = await PutAsync(id, ValidBody());
        status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(body).Should().Be("not_container");
        _users.GetById(id)!.ChildMode.Should().BeNull();
    }

    [Fact]
    public async Task Put_ПровайдерНеЗаписьСApiKey_400_provider_not_api_key()
    {
        // Ключ вне пула и ключ «claude» (его нет в тестовом конфиге) — не запись с ApiKey:
        // ребёнку положена отдельная запись пула с ключом (И2), тихий откат на подписку запрещён.
        var id = await NewContainerUser("kid-provider");
        var body = new { enabled = true, providerKey = "claude", model = "sonnet", spaces = new Dictionary<string, object>() };

        var (status, resp) = await PutAsync(id, body);
        status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(resp).Should().Be("provider_not_api_key");
        _users.GetById(id)!.ChildMode.Should().BeNull();
    }

    [Fact]
    public async Task Put_ОкноНечитаемое_400_invalid_window()
    {
        var id = await NewContainerUser("kid-window");
        var body = new
        {
            enabled = true,
            providerKey = KidsKey,
            model = "sonnet",
            spaces = new Dictionary<string, object>
            {
                ["lessons"] = new { windows = new[] { new { from = "25:00", to = "20:00" } } },
            },
        };

        var (status, resp) = await PutAsync(id, body);
        status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(resp).Should().Be("invalid_window");
        _users.GetById(id)!.ChildMode.Should().BeNull();
    }

    [Fact]
    public async Task Put_ДниВнеISO_400_invalid_window()
    {
        var id = await NewContainerUser("kid-days");
        var body = new
        {
            enabled = true,
            providerKey = KidsKey,
            model = "sonnet",
            spaces = new Dictionary<string, object>
            {
                ["lessons"] = new { windows = new[] { new { from = "09:00", to = "20:00", weekdays = new[] { 0, 8 } } } },
            },
        };

        var (status, resp) = await PutAsync(id, body);
        status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(resp).Should().Be("invalid_window");
    }

    [Fact]
    public async Task Put_ПространствоНеизвестное_400_invalid_space()
    {
        var id = await NewContainerUser("kid-space");
        var body = new
        {
            enabled = true,
            providerKey = KidsKey,
            model = "sonnet",
            spaces = new Dictionary<string, object>
            {
                ["homework"] = new { windows = Array.Empty<object>() },
            },
        };

        var (status, resp) = await PutAsync(id, body);
        status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(resp).Should().Be("invalid_space");
        _users.GetById(id)!.ChildMode.Should().BeNull();
    }

    [Fact]
    public async Task Put_ОпекунНеСуществует_400_guardian_not_found()
    {
        var id = await NewContainerUser("kid-guardian");
        var (status, resp) = await PutAsync(id, ValidBody("no-such-guardian"));
        status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(resp).Should().Be("guardian_not_found");
        _users.GetById(id)!.ChildMode.Should().BeNull();
    }

    // --- валидный путь ---

    [Fact]
    public async Task Put_Валидный_200_ТьюторПроведён_ПолитикаВидитРебёнка()
    {
        var guardian = AdminId;
        var id = await NewContainerUser("kid-valid");

        var (status, resp) = await PutAsync(id, ValidBody(guardian));
        status.Should().Be(HttpStatusCode.OK);

        var tutorId = resp.GetProperty("tutorPersonaId").GetString()!;
        tutorId.Should().NotBeNullOrEmpty();

        var profile = resp.GetProperty("profile");
        profile.GetProperty("providerKey").GetString().Should().Be(KidsKey);
        profile.GetProperty("model").GetString().Should().Be("sonnet");
        profile.GetProperty("warnMinutes").GetInt32().Should().Be(5);
        profile.GetProperty("spaces").GetProperty("lessons").GetProperty("windows")[0]
            .GetProperty("from").GetString().Should().Be("09:00");
        profile.GetProperty("spaces").GetProperty("lessons").GetProperty("dailyMinutes")
            .GetInt32().Should().Be(40);

        // Тьютор проведён и закреплён в профиле стора (не только в ответе),
        // и сам профиль проходит политику — ребёнок виден её потребителям.
        var stored = _users.GetById(id)!.ChildMode!;
        stored.Enabled.Should().BeTrue();
        stored.TutorPersonaId.Should().Be(tutorId);
        var snapshot = _policy.For(id);
        snapshot.Should().NotBeNull("профиль включён — политика обязана видеть ребёнка");
        snapshot!.DenyAll.Should().BeFalse("проведённый профиль проходит валидацию политики");
    }

    [Fact]
    public async Task Put_Выключение_РежимСнят_НастройкиСохранены()
    {
        var id = await EnableChild("kid-off");
        _policy.For(id).Should().NotBeNull();

        var (status, resp) = await PutAsync(id, new { enabled = false });
        status.Should().Be(HttpStatusCode.OK);
        resp.GetProperty("profile").GetProperty("enabled").GetBoolean().Should().BeFalse();

        // Тело {enabled:false} просит выключить режим, а не стереть лимиты: профиль целён,
        // Enabled=false; для политики это уже обычный пользователь.
        var stored = _users.GetById(id)!.ChildMode!;
        stored.Enabled.Should().BeFalse();
        stored.ProviderKey.Should().Be(KidsKey);
        stored.Model.Should().Be("sonnet");
        _policy.For(id).Should().BeNull("выключенный профиль — обычный пользователь");
    }

    [Fact]
    public async Task Put_БезSubjects_ЖивыеПредметыСохранены_ОтветНесётЖивогоТьютора()
    {
        var id = await EnableChild("kid-subjects-keep");
        var tutor = _users.GetById(id)!.ChildMode!.TutorPersonaId;
        // Засев предмета прямой записью в живой профиль (как делает миграция): предмет
        // несёт поля профиля и его тьютора.
        ChildTestSubjects.Seed(_hosts.Services, id);

        // PUT вкладки «Режим и время»: тела без subjects — правка лимитов не стирает
        // предметы (живое слияние Т2a: ToChildProfile даёт стору сигнал null, он сохраняет живой список).
        var (status, body) = await PutAsync(id, ValidBody());
        status.Should().Be(HttpStatusCode.OK);

        var stored = _users.GetById(id)!.ChildMode!;
        stored.Subjects.Should().ContainSingle("PUT без subjects не стирает предметы")
            .Which.Id.Should().Be(ChildTestSubjects.Key);
        stored.Subjects[0].TutorPersonaId.Should().Be(tutor, "живой тьютор предмета сохранён");

        // Ответ отдаёт предметы с живым тьютором (Т2a: tutorPersonaId — поле ответа, из тела не читалось).
        body.GetProperty("profile").GetProperty("subjects")[0].GetProperty("tutorPersonaId")
            .GetString().Should().Be(tutor);
    }

    // --- репетиторы предметов (Т2b): провижн при добавлении, откат при отказе ---

    [Fact]
    public async Task Put_НовыйПредмет_200_РепетиторПроведён_КлючВПредмете()
    {
        var id = await EnableChild("kid-subject-provision");

        var body = new
        {
            enabled = true,
            providerKey = KidsKey,
            model = "sonnet",
            spaces = new Dictionary<string, object>(),
            subjects = new[]
            {
                new { id = "math", label = "Математика", glyph = "×÷", color = "info", providerKey = KidsKey, model = "sonnet" },
            },
        };
        var (status, resp) = await PutAsync(id, body);
        status.Should().Be(HttpStatusCode.OK);

        // У предмета в ответе непустой tutorPersonaId: персону провит сам PUT (Т2b),
        // ключ закреплён в записи стора, персона — ребёнка.
        var subjectTutor = resp.GetProperty("profile").GetProperty("subjects")[0]
            .GetProperty("tutorPersonaId").GetString()!;
        subjectTutor.Should().NotBeNullOrEmpty();
        _users.GetById(id)!.ChildMode!.Subjects[0].TutorPersonaId.Should().Be(subjectTutor);
        var persona = _hosts.Services.GetRequiredService<PersonaManager>().Get(subjectTutor, id);
        persona.Should().NotBeNull();
        persona!.Name.Should().Be("Репетитор: Математика");

        // Тьютор профиля не подменён предметным: это разные персоны (разрез §1).
        _users.GetById(id)!.ChildMode!.TutorPersonaId.Should().NotBe(subjectTutor);
    }

    [Fact]
    public async Task Put_ПровижерПредметаУпал_500_ПрофильРавенПрежнему_БезСирот()
    {
        var id = await EnableChild("kid-subject-fail");
        var tutor = _users.GetById(id)!.ChildMode!.TutorPersonaId;
        // Живой предмет (засев, как делает миграция: тьютор профиля).
        ChildTestSubjects.Seed(_hosts.Services, id);

        // Отказ провижна — вещание репетитора нового предмета (живой тьютор профиля
        // EnsureAsync возвращает без вещания, бросает только новая персона).
        _hosts.BroadcasterStub.ThrowFor = id;
        try
        {
            var body = new
            {
                enabled = true,
                providerKey = KidsKey,
                model = "sonnet",
                spaces = new Dictionary<string, object>(),
                subjects = new[]
                {
                    new { id = ChildTestSubjects.Key, label = "Русский язык", glyph = "Аа", color = "info", providerKey = KidsKey, model = "sonnet" },
                    new { id = "math", label = "Математика", glyph = "×÷", color = "info", providerKey = KidsKey, model = "sonnet" },
                },
            };
            var (status, resp) = await PutAsync(id, body);
            status.Should().Be(HttpStatusCode.InternalServerError);
            CodeOf(resp).Should().Be("tutor_provision_failed");

            // Профиль равен прежнему: предмет тот же засевный, с тьютором профиля.
            var stored = _users.GetById(id)!.ChildMode!;
            stored.TutorPersonaId.Should().Be(tutor);
            stored.Subjects.Should().ContainSingle().Which.Id.Should().Be(ChildTestSubjects.Key);
            stored.Subjects[0].TutorPersonaId.Should().Be(tutor, "живой предмет отказанный PUT не трогает");

            // Созданная персона предмета удалена откатом: у ребёнка остались тьютор профиля
            // («Помощник») и дефолт-ассистент (провится при POST /api/users, UsersController.cs:51);
            // полуперсоны отказанного вызова (имя «Репетитор: …») не остаётся.
            _hosts.Services.GetRequiredService<PersonaManager>().GetAllInternal()
                .Count(p => p.OwnerId == id && p.Name.StartsWith("Репетитор: ")).Should().Be(0,
                    "откат удалил репетитора, созданного этим вызовом: сироты в personas.json нет");
        }
        finally
        {
            _hosts.BroadcasterStub.ThrowFor = null;
        }
    }

    // --- правила ребёнка: их не принимает тело, resetRules — единственный сброс ---

    [Fact]
    public async Task Put_БезResetRules_ПравилаНеТронуты()
    {
        var id = await EnableChild("kid-rules-keep");
        SeedRules(id, RulesT0, "Уроки по будням после уроков");

        var (status, _) = await PutAsync(id, ValidBody());
        status.Should().Be(HttpStatusCode.OK);

        var stored = _users.GetById(id)!.ChildMode!;
        stored.RulesAgreedAt.Should().Be(RulesT0, "правка лимитов не трогает согласие ребёнка");
        stored.RulesSummary.Should().Be("Уроки по будням после уроков");
    }

    [Fact]
    public async Task Put_ResetRules_ПравилаСброшены()
    {
        var id = await EnableChild("kid-rules-reset");
        SeedRules(id, RulesT0, "Старые правила");

        var body = new { enabled = true, providerKey = KidsKey, model = "sonnet", resetRules = true };
        var (status, _) = await PutAsync(id, body);
        status.Should().Be(HttpStatusCode.OK);

        var stored = _users.GetById(id)!.ChildMode!;
        stored.RulesAgreedAt.Should().BeNull("resetRules=true — админ просил сброс обоих полей");
        stored.RulesSummary.Should().BeNull();
    }

    // --- конкурентность ---

    [Fact]
    public async Task PutProfile_ConcurrentWithAgree_KeepsRulesAgreedAt()
    {
        var id = await EnableChild("kid-race-agree");
        SeedRules(id, RulesT0, "Согласие до правки");
        // Ссылка на профиль до правки: её смена в сторе — знак, что запись PUT состоялась.
        var initial = _users.GetById(id)!.ChildMode!;

        // Тело с длинным списком опекунов: проверка опекуна — обращение к стору на каждый id.
        // Между первым чтением запроса (auth-проверка админа) и его записью под локом —
        // десятки обращений к стору; атомарная правка читает живое значение в конце этого
        // пути, вариант «читать пользователя вне блокировки» — фиксирует его на первом чтении.
        var manyGuardians = Enumerable.Repeat(AdminId, 60).ToArray();
        var body = new
        {
            enabled = true,
            guardianUserIds = manyGuardians,
            providerKey = KidsKey,
            model = "sonnet",
            spaces = new Dictionary<string, object>(),
        };

        const long minute = TimeSpan.TicksPerMinute;
        var stop = false;
        var lastTicks = RulesT0.Ticks;
        var agree = Task.Run(() =>
        {
            // Плотная серия «согласий» ребёнка: прямые записи в живой профиль, пока профиль
            // не заменён. Значения монотонны и различимы (по минуте на запись).
            // Пишем только в initial: после замены стора GetById вернул бы новый профиль,
            // и последняя итерация серии записалась бы в него — любое значение выглядело
            // «свежим» и проверка теряла смысл.
            while (!Volatile.Read(ref stop) && ReferenceEquals(_users.GetById(id)!.ChildMode, initial))
            {
                lastTicks += minute;
                initial.RulesAgreedAt = new DateTime(Volatile.Read(ref lastTicks), DateTimeKind.Utc);
                initial.RulesSummary = "Согласие во время правки";
            }
        });

        // Барьер на локе стора: публичный RemapModels держит лок и вызывает Func на каждом
        // непустом слоте — ставим слот админу, чтобы Func гарантированно вызвался. Пока барьер
        // держит лок, запрос ещё не начался; с выходом из-под лока первый же доступ запроса
        // к стору (auth-чтение) конкурирует с серией за тот же лок. Слот после барьера не
        // меняется (Func возвращает null — «не менять»), файл не переписывается.
        _users.SetModelTiers(AdminId, "sonnet", null, null);
        _users.RemapModels(_ => null);

        // Marker — значение серии на момент пуска запроса; дальше по потоку только
        // прибавляется. Сравнение — с КОНЦОМ серии, а не с marker: атомарная запись берёт
        // живое значение, и оно отличается от последнего записанного на 1–2 итерации
        // (текущая итерация серии, начатая до замены). «Снимок первого чтения» отстаёт на
        // весь путь запроса от чтения до записи (60 опекунов — десятки обращений к стору
        // при живой серии): на замере — 187 минут против порога 60.
        var atStart = new DateTime(Volatile.Read(ref lastTicks), DateTimeKind.Utc);
        var (status, _) = await PutAsync(id, body);
        Volatile.Write(ref stop, true);
        await agree;

        status.Should().Be(HttpStatusCode.OK);
        var kept = _users.GetById(id)!.ChildMode!.RulesAgreedAt!.Value;
        var duringEdit = new DateTime(Volatile.Read(ref lastTicks), DateTimeKind.Utc);
        duringEdit.Should().BeOnOrAfter(atStart.AddMinutes(20), "плотная серия согласия во время правки была");
        // Живое значение на момент записи = последнее значение серии (±1–2 итерации).
        var lagMinutes = (duringEdit.Ticks - kept.Ticks) / minute;
        lagMinutes.Should().BeLessThan(60,
            "согласие ребёнка во время правки сохранено: RulesAgreedAt — живое значение стора на момент записи, не снимок первого чтения");
    }

    [Fact]
    public async Task Put_ДваОдновременно_ЦелыйПрофильНеСмесь()
    {
        var id = await NewContainerUser("kid-race-put");
        var bodyA = ValidBody();
        var bodyB = new
        {
            enabled = true,
            providerKey = KidsKey,
            model = "haiku",
            spaces = new Dictionary<string, object>
            {
                ["lessons"] = new { windows = Array.Empty<object>(), dailyMinutes = 10 },
            },
        };

        var putA = PutAsync(id, bodyA);
        var putB = PutAsync(id, bodyB);
        await Task.WhenAll(putA, putB);
        (await putA).Status.Should().Be(HttpStatusCode.OK);
        (await putB).Status.Should().Be(HttpStatusCode.OK);

        // Результат — один целый профиль (чей-то целиком), а не смесь: модель и дневный
        // лимит приходят из одной записи.
        var stored = _users.GetById(id)!.ChildMode!;
        var whole = (stored.Model == "sonnet" && stored.Spaces["lessons"].DailyMinutes == 40)
            || (stored.Model == "haiku" && stored.Spaces["lessons"].DailyMinutes == 10);
        whole.Should().BeTrue("одновременные PUT не перемешивают поля");
    }

    // --- согласие на не-Anthropic маршрут (Т5a, И2′): трёхзначный acknowledgeRouteRisk (Х1) ---

    [Fact]
    public async Task PutProfile_AckFalse_SameProvider_RevokesAck()
    {
        var id = await EnableChild("kid-ack-revoke");

        // Согласие стоит: PUT с true — пара (момент запроса, адмен) в сторе и в ответе.
        var (first, firstBody) = await PutAsync(id, BodyWithRoutedSubject(true));
        first.Should().Be(HttpStatusCode.OK);
        var seeded = StoredSubject(id);
        seeded.RouteAcknowledgedAt.Should().NotBeNull();
        seeded.RouteAcknowledgedBy.Should().Be(AdminId);
        firstBody.GetProperty("profile").GetProperty("subjects")[0]
            .GetProperty("acknowledgeRouteRisk").GetBoolean().Should().BeTrue();

        // Снятая галка при том же providerKey — ОТЗЫВ: 200, предмет сохранён «не согласованным».
        // 400 route_risk_unacknowledged на false не отдаём: запуск хода откажет сам
        // (ChildRoute.Resolve читает RouteAcknowledgedAt предмета).
        var (status, body) = await PutAsync(id, BodyWithRoutedSubject(false));
        status.Should().Be(HttpStatusCode.OK);
        var subject = StoredSubject(id);
        subject.Id.Should().Be("math", "предмет сохранён");
        subject.ProviderKey.Should().Be("deepseek", "тот же providerKey");
        subject.RouteAcknowledgedAt.Should().BeNull("снятая галка отзывает живое согласие того же providerKey");
        subject.RouteAcknowledgedBy.Should().BeNull();
        var json = body.GetProperty("profile").GetProperty("subjects")[0];
        json.GetProperty("acknowledgeRouteRisk").GetBoolean().Should().BeFalse();
        json.GetProperty("routeAcknowledgedAt").ValueKind.Should().Be(JsonValueKind.Null, "ответ — снимок после записи");
    }

    [Fact]
    public async Task PutProfile_AckOmitted_SameProvider_KeepsAck()
    {
        var id = await EnableChild("kid-ack-keep");
        (await PutAsync(id, BodyWithRoutedSubject(true))).Status.Should().Be(HttpStatusCode.OK);
        var at0 = StoredSubject(id).RouteAcknowledgedAt!.Value;
        StoredSubject(id).RouteAcknowledgedBy.Should().Be(AdminId);

        // Поля нет — живое согласие того же providerKey остаётся (как было): пара не переставляется
        // и не обнуляется, ответ отдаёт её как стоящую.
        var (status, body) = await PutAsync(id, BodyWithRoutedSubject(null));
        status.Should().Be(HttpStatusCode.OK);
        var subject = StoredSubject(id);
        subject.RouteAcknowledgedAt.Should().Be(at0, "повторный PUT без поля момента не меняет");
        subject.RouteAcknowledgedBy.Should().Be(AdminId, "ни автора");
        body.GetProperty("profile").GetProperty("subjects")[0]
            .GetProperty("acknowledgeRouteRisk").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task PutProfile_AckAndRevokeParallel_NoMixedPair()
    {
        var id = await EnableChild("kid-ack-race");
        var manyGuardians = Enumerable.Repeat(AdminId, 60).ToArray();

        // Живое согласие до гонки: с него начинается то, что обязан не вернуть отзыв, если
        // параллельный «true» ещё держит прежнюю пару.
        (await PutAsync(id, BodyWithRoutedSubject(true))).Status.Should().Be(HttpStatusCode.OK);
        var at0 = StoredSubject(id).RouteAcknowledgedAt!.Value;

        // Два PUT одного ребёнка одновременно: один просит согласие, второй его отзывает. Оба —
        // длинный путь (60 опекунов — десятки обращений к стору): окно между первым чтением
        // профиля запросом и его записью под локом — живое.
        var putAck = PutAsync(id, BodyWithRoutedSubject(true, manyGuardians));
        var putRevoke = PutAsync(id, BodyWithRoutedSubject(false, manyGuardians));
        await Task.WhenAll(putAck, putRevoke);
        (await putAck).Status.Should().Be(HttpStatusCode.OK);
        (await putRevoke).Status.Should().Be(HttpStatusCode.OK);

        // Итог — одно из двух состояний целиком: либо согласия нет (отзыв записан последним),
        // либо свежее согласие PUT-«true» (он записан последним после отзыва). Прежней пары (at0)
        // среди исходов нет: «false» снимает её безусловно, а «true» при живом согласии её не
        // переставляет. Смешанной пары тоже нет: At и By приходят из одной записи под локом.
        var stored = StoredSubject(id);
        var outcome = (stored.RouteAcknowledgedAt is null && stored.RouteAcknowledgedBy is null)
            || (stored.RouteAcknowledgedAt > at0 && stored.RouteAcknowledgedBy == AdminId);
        outcome.Should().BeTrue("одновременные согласие и отзыв дают целый исход, не смесь пары At/By");
    }

    // только персоны-тьюторы (роль «Репетитор…»): дефолт-ассистент ребёнка не в счёт
    private IReadOnlyList<Persona> PersonasOf(string ownerId) =>
        _hosts.Services.GetRequiredService<PersonaManager>().GetAllInternal()
            .Where(p => p.OwnerId == ownerId && p.Role?.StartsWith("Репетитор") == true).ToList();

    [Fact]
    public async Task Put_Повторный_ТотЖеТьютор_БезВторогоПомощника()
    {
        var id = await EnableChild("kid-tutor-once");
        var first = _users.GetById(id)!.ChildMode!.TutorPersonaId;

        var (status, body) = await PutAsync(id, ValidBody());
        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("tutorPersonaId").GetString().Should().Be(first);

        var (status2, body2) = await PutAsync(id, new { enabled = true, providerKey = KidsKey, model = "sonnet", resetRules = true });
        status2.Should().Be(HttpStatusCode.OK);
        body2.GetProperty("tutorPersonaId").GetString().Should().Be(first, "resetRules тьютора не сбрасывает");
        PersonasOf(id).Should().ContainSingle("тьютор у ребёнка один");
    }

    [Fact]
    public async Task Put_ПровижерУпал_ResetRules_ОткатСохраняетПрежниеПравилаИТьютора()
    {
        var id = await EnableChild("kid-rollback-rules");
        SeedRules(id, RulesT0, "Прежние правила");
        var tutor = _users.GetById(id)!.ChildMode!.TutorPersonaId;
        // ключ тьютора «повис» (персона жива, ключа нет): Ensure создаст новую персону,
        // вещание упадёт — откат обязан удалить новую и вернуть профиль как был
        var live = _users.GetById(id)!.ChildMode!;
        live.TutorPersonaId = "";
        _hosts.BroadcasterStub.ThrowFor = id;
        try
        {
            var (status, _) = await PutAsync(id, new { enabled = true, providerKey = KidsKey, model = "sonnet", resetRules = true });
            status.Should().Be(HttpStatusCode.InternalServerError);
            var stored = _users.GetById(id)!.ChildMode!;
            stored.RulesAgreedAt.Should().Be(RulesT0, "откат целиком: правила прежние, а не сброшенные resetRules");
            stored.RulesSummary.Should().Be("Прежние правила");
            PersonasOf(id).Select(p => p.Id).Should().BeEquivalentTo(new[] { tutor }, "новая персона удалена, прежняя цела");
        }
        finally
        {
            _hosts.BroadcasterStub.ThrowFor = null;
        }
    }

    [Fact]
    public async Task Put_ПровижерУпал_500_ПрофильНеСохранён()
    {
        var id = await NewContainerUser("kid-provisioner-fail");
        // Отказ провижнера — вещание тьютора бросает после создания персоны:
        // 500 с {code, message}, профиль остаётся таким, каким был (его не было).
        _hosts.BroadcasterStub.ThrowFor = id;
        try
        {
            var (status, resp) = await PutAsync(id, ValidBody());
            status.Should().Be(HttpStatusCode.InternalServerError);
            CodeOf(resp).Should().NotBeNullOrEmpty();
            _users.GetById(id)!.ChildMode.Should().BeNull("отказ включению — профиль не сохранён");
            PersonasOf(id).Should().BeEmpty("персона, созданная упавшим вызовом, не остаётся сиротой");
        }
        finally
        {
            _hosts.BroadcasterStub.ThrowFor = null;
        }
    }

    // --- привязка базы знаний к предмету (К1b, разрез §1/§3) ---

    [Fact]
    public async Task Put_Знание_СвояСамостоятельная_200_АвторВызывающегоАдмена_ВОтветеБезАвтора()
    {
        var id = await NewContainerUser("kid-kb-own");
        var (status, body) = await PutAsync(id, BodyWithSubjectKnowledge("d-kb"));
        status.Should().Be(HttpStatusCode.OK);

        // Ответ: id датасета и момент, поставленный сервером; автора в ответе нет (К1b).
        var knowledge = body.GetProperty("profile").GetProperty("subjects")[0].GetProperty("knowledge");
        knowledge.GetProperty("datasetId").GetString().Should().Be("d-kb");
        knowledge.TryGetProperty("attachedAt", out var at).Should().BeTrue("ответ несёт момент привязки");
        DateTimeOffset.Parse(at.GetString()!).Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        knowledge.TryGetProperty("attachedByUserId", out var _).Should().BeFalse("автор привязки наружу не отдаётся");

        // Стор: автор — id вызывающего адмена (не имя, не из тела).
        StoredKnowledge(id).DatasetId.Should().Be("d-kb");
        StoredKnowledge(id).AttachedByUserId.Should().Be(AdminId);
    }

    [Fact]
    public async Task Put_Знание_ТелоНесётAttachedByUserId_Игнорируется()
    {
        var id = await NewContainerUser("kid-kb-body-author");
        var body = BodyWithSubjectKnowledge("d-kb");
        var subject = (Dictionary<string, object?>)((object[])body["subjects"]!)[0];
        subject["knowledge"] = new { datasetId = "d-kb", attachedAt = RulesT0, attachedByUserId = "not-me" };

        var (status, _) = await PutAsync(id, body);
        status.Should().Be(HttpStatusCode.OK);

        // Ни момент, ни автор из тела не прочитаны: их ставит сервер.
        var stored = StoredKnowledge(id);
        stored.AttachedByUserId.Should().Be(AdminId);
        stored.AttachedAt.Should().NotBe(RulesT0);
    }

    [Fact]
    public async Task Put_Знание_БазаЗаметокАдмена_400_dataset_not_attachable()
    {
        var id = await NewContainerUser("kid-kb-notes");
        var (status, resp) = await PutAsync(id, BodyWithSubjectKnowledge("d-notes"));
        status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(resp).Should().Be("dataset_not_attachable");
        // Отказ до записи: профиль не создан.
        _users.GetById(id)!.ChildMode.Should().BeNull();
    }

    [Fact]
    public async Task Put_Знание_СвояБазаДругогоАдмена_400_dataset_not_attachable()
    {
        // Из-за ревью К1a (953ef02f): предикат считает имя «глобальным», если его владельца нет
        // в others. Без второго адмена в списке его «…:kb:…» привязался бы как общая база —
        // валидатор обязан передавать ВСЕХ пользователей, включая адменов.
        const string otherAdminName = "kbadmin-a";
        var otherAdmin = await NewAdminUser(otherAdminName);
        _users.GetById(otherAdmin)!.Role.Should().Be("admin");
        _hosts.DifyStub.DatasetsJson = DatasetsJson(("d-other-admin", $"{otherAdminName}:kb:физика"));

        var id = await NewContainerUser("kid-kb-other-admin");
        var (status, resp) = await PutAsync(id, BodyWithSubjectKnowledge("d-other-admin"));
        status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(resp).Should().Be("dataset_not_attachable");
    }

    [Fact]
    public async Task Put_Знание_ГлобальнаяБаза_200()
    {
        var id = await NewContainerUser("kid-kb-global");
        var (status, _) = await PutAsync(id, BodyWithSubjectKnowledge("d-global"));
        status.Should().Be(HttpStatusCode.OK);
        StoredKnowledge(id).DatasetId.Should().Be("d-global");
        StoredKnowledge(id).AttachedByUserId.Should().Be(AdminId);
    }

    [Fact]
    public async Task Put_Знание_DifyНеНастроен_400_knowledge_unavailable()
    {
        var id = await NewContainerUser("kid-kb-off");
        // IsConfigured читает эти поля (KnowledgeService.cs:228) — то же состояние инстанса,
        // на котором вертикаль «Знаний» не настроена: отказ до записи, не исключение.
        _hosts.Dify.ApiUrl = "";
        try
        {
            var (status, resp) = await PutAsync(id, BodyWithSubjectKnowledge("d-kb"));
            status.Should().Be(HttpStatusCode.BadRequest);
            CodeOf(resp).Should().Be("knowledge_unavailable");
            _users.GetById(id)!.ChildMode.Should().BeNull();
        }
        finally
        {
            _hosts.Dify.ApiUrl = "http://dify.test/v1";
        }
    }

    [Fact]
    public async Task Put_Знание_DifyОтказал_400_knowledge_unavailable_БезИсключения()
    {
        var id = await NewContainerUser("kid-kb-down");
        _hosts.DifyStub.Throw = true;
        try
        {
            var (status, resp) = await PutAsync(id, BodyWithSubjectKnowledge("d-kb"));
            status.Should().Be(HttpStatusCode.BadRequest);
            CodeOf(resp).Should().Be("knowledge_unavailable");
            _users.GetById(id)!.ChildMode.Should().BeNull();
        }
        finally
        {
            _hosts.DifyStub.Throw = false;
        }
    }

    [Fact]
    public async Task Put_Знание_ТотЖеIdОтДругогоАдмена_ПараНеМеняется_NullСнимает()
    {
        var id = await NewContainerUser("kid-kb-keep");
        const string otherAdminName = "kbadmin-b";
        var otherAdmin = await NewAdminUser(otherAdminName);

        var (first, _) = await PutAsync(id, BodyWithSubjectKnowledge("d-kb"));
        first.Should().Be(HttpStatusCode.OK);
        var author = StoredKnowledge(id).AttachedByUserId;
        var at = StoredKnowledge(id).AttachedAt;
        author.Should().Be(AdminId);

        // Тот же datasetId от другого адмена: автор и момент — прежние (К1b: поле ставит
        // сервер по живому значению, повторный вызов его не переставляет).
        var client = _hosts.CreateAuthenticatedClient(otherAdminName, KidsPassword);
        var resp = await client.PutAsJsonAsync($"/api/users/{id}/child-profile", BodyWithSubjectKnowledge("d-kb"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var kept = StoredKnowledge(id);
        kept.AttachedByUserId.Should().Be(AdminId, "повторный PUT другого адмена автора не меняет");
        kept.AttachedAt.Should().Be(at, "и момент привязки");
        kept.AttachedByUserId.Should().NotBe(otherAdmin);

        // Явный null — привязка снята (поле в сторе пустое).
        var (off, offBody) = await PutAsync(id, BodyWithSubjectKnowledge(null));
        off.Should().Be(HttpStatusCode.OK);
        StoredKnowledge(id).Should().BeNull();
        offBody.GetProperty("profile").GetProperty("subjects")[0]
            .GetProperty("knowledge").ValueKind.Should().Be(JsonValueKind.Null, "ответ отдаёт снятую привязку как null");
    }

    [Fact]
    public async Task PutProfile_ParallelKnowledge_NoMixedPair()
    {
        var id = await EnableChild("kid-kb-race");
        var otherAdmin = await NewAdminUser("kbadmin-c");

        // Первая привязка — обычным PUT: с неё начинается то, что обязан не вернуть второй
        // запрос, если живое значение к моменту его записи уже другое.
        (await PutAsync(id, BodyWithSubjectKnowledge("d-kb"))).Status.Should().Be(HttpStatusCode.OK);
        var initial = _users.GetById(id)!.ChildMode!;
        var subject = initial.Subjects.Single(s => s.Id == "math");

        const long minute = TimeSpan.TicksPerMinute;
        var stop = false;
        var lastTicks = RulesT0.Ticks;
        var rebind = Task.Run(() =>
        {
            // Плотная серия перепривязок ТОГО ЖЕ датасета другим адменом: прямые записи в
            // живой профиль, пока он не заменён (тот же приём, что у серии согласия правил).
            // Пара (id, автор, момент) монотонна и различима: чужая пара узнаётся по автору.
            while (!Volatile.Read(ref stop) && ReferenceEquals(_users.GetById(id)!.ChildMode, initial))
            {
                lastTicks += minute;
                subject.Knowledge = new ChildSubjectKnowledge("d-kb", otherAdmin,
                    new DateTime(Volatile.Read(ref lastTicks), DateTimeKind.Utc));
            }
        });

        // Барьер на локе стора и длинный путь запроса (60 опекунов — десятки обращений к стору):
        // окно между первым чтением профиля запросом и его записью под локом — живое.
        _users.SetModelTiers(AdminId, "sonnet", null, null);
        _users.RemapModels(_ => null);

        var (status, _) = await PutAsync(id, BodyWithSubjectKnowledge("d-kb", Enumerable.Repeat(AdminId, 60).ToArray()));
        Volatile.Write(ref stop, true);
        await rebind;

        status.Should().Be(HttpStatusCode.OK);
        var kept = StoredKnowledge(id);
        // Пара целиком из живой привязки стора на момент записи: автор — серии, а не
        // вызывающего адмена; момент — её последнее значение (±1–2 итерации), а не UtcNow
        // запроса и не значение первого PUT. Слияние по снимку профиля вне замка отстаёт на
        // весь путь запроса — на замере десятки минут.
        kept.DatasetId.Should().Be("d-kb");
        kept.AttachedByUserId.Should().Be(otherAdmin, "живая пара стора, а не кандидат этого запроса");
        var duringEdit = new DateTime(Volatile.Read(ref lastTicks), DateTimeKind.Utc);
        var lagMinutes = (duringEdit.Ticks - kept.AttachedAt.Ticks) / minute;
        lagMinutes.Should().BeLessThan(60,
            "привязка, сделанная во время правки, сохранена: слияние под локом стора по живому значению, не по снимку первого чтения");
    }

    /// <summary>Подменённый Dify: список датасетов — по слову теста, отказ — по флагу.
    /// Путь листинга — /v1/datasets (BaseAddress + «datasets?page=…» из ListDatasetsAsync);
    /// всё остальное — пустой лист, чтобы ни один фоновый потребитель хоста не увидел здесь
    /// ничего неожиданного.</summary>
    internal sealed class FakeDify : HttpMessageHandler
    {
        public bool Throw;
        public string DatasetsJson = "[]";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Throw) throw new HttpRequestException("Dify не ответил");
            var json = request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/datasets")
                ? $"{{\"data\":{DatasetsJson},\"has_more\":false}}"
                : """{"data":[],"has_more":false}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    // Пропускающий стаб вещания: бросает только по указанному владельцу — стартовый
    // провижн хоста и чужие тесты он не трогает. internal: его держит internal-поле
    // фикстуры (private-тип для него был бы менее доступен — CS0052).
    internal sealed class ThrowingBroadcaster : ISessionBroadcaster
    {
        public string? ThrowFor { get; set; }

        private void MaybeThrow(string ownerId)
        {
            if (ownerId == ThrowFor) throw new InvalidOperationException("вещание тьютора не удалось");
        }

        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
        public Task ToOwner(string ownerId, ServerMessage message)
        {
            MaybeThrow(ownerId);
            return Task.CompletedTask;
        }
        public Task ToProject(string projectId, ServerMessage message) => Task.CompletedTask;
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
    }
}
