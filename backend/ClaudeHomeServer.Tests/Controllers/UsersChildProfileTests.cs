using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Sessions;
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
    private const string KidsKey2 = "kids-api-2";
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

    // Фолбэк-таймзона калькулятора лимитов (ChildLimitsCalculator.cs:42): окно теста «предмет
    // удалён — чат цел» строится относительно «сейчас» в ней.
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    private readonly Hosts _hosts;
    private readonly HttpClient _admin;
    private readonly UserStore _users;
    private readonly IChildModePolicy _policy;

    public UsersChildProfileTests(Hosts hosts)
    {
        _hosts = hosts;
        _hosts.ExtraConfig[$"ClaudeSubscriptions:{KidsKey}:ApiKey"] = KidsApiKey;
        _hosts.ExtraConfig[$"ClaudeSubscriptions:{KidsKey2}:ApiKey"] = KidsApiKey + "-2";
        // Не-Anthropic провайдер с ценой (тот же набор, что у ChildRouteKit): тестам согласия на
        // риск маршрута (Т5a, Х1) нужен предмет на чужом поставщике, который резолвится в реестре
        // хоста — иначе отказ придёт как provider_not_api_key, а не как проверка согласия.
        _hosts.ExtraConfig["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.test/anthropic";
        _hosts.ExtraConfig["LlmProviders:deepseek:ApiKey"] = "sk-ds";
        _hosts.ExtraConfig["LlmProviders:deepseek:Models:0:Id"] = "deepseek-v4-pro";
        _hosts.ExtraConfig["LlmProviders:deepseek:Models:0:PriceInMissPer1M"] = "0.5";
        _hosts.ExtraConfig["LlmProviders:deepseek:Models:0:PriceOutPer1M"] = "1.0";
        // Тест «предмет удалён — чат цел» создаёт детский чат: корень чатов container-владельца
        // в песочнице (CliRoute.ResolveChatRoot) — та же настройка, что у ChildChatsControllerTests.
        _hosts.ExtraConfig["Sandbox:ProjectsRoot"] = Path.Combine(Path.GetTempPath(), "ccs-kids-profile-" + Guid.NewGuid().ToString("N"));
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
    public async Task Put_ПровайдерНеРезолвится_400_provider_not_api_key_ТекстВеткиForPool()
    {
        // Ключ, который не резолвится ни в одну запись (нет в пуле, нет в реестре провайдеров):
        // ChildRoute.Evaluate → NoRoute → ветка forPool:true RouteReject: тот же код, что у
        // «не запись с ключом», а текст называет пул подписок. Сторож именно этой ветки:
        // предыдущий тест проверяет код, текст forPool здесь — единственный в классе.
        // Мутация «поменять местами тексты веток NoRoute» — красный на Contain, не на коде.
        var id = await NewContainerUser("kid-provider-unresolved");
        var body = new { enabled = true, providerKey = "no-such-provider", model = "sonnet", spaces = new Dictionary<string, object>() };

        var (status, resp) = await PutAsync(id, body);
        status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(resp).Should().Be("provider_not_api_key");
        resp.GetProperty("message").GetString().Should().Contain("пула подписок с API-ключом",
            "отказ профиля — ветка forPool:true: текст называет пул подписок");
        _users.GetById(id)!.ChildMode.Should().BeNull();
    }

    [Fact]
    public async Task PutProfile_RulesChatProviderRoute_Returns400()
    {
        // Сторож «чат правил + облачный провайдер = отказ» (минор ревью Т5a): deepseek —
        // законный маршрут ПРЕДМЕТА с согласия админа (тесты Т5a дают ему 200), но маршрут
        // чата правил — только запись пула с API-ключом: Evaluate профиля всегда идёт с
        // acknowledged: false (ChildProfileValidator.cs:40), а flag согласия в теле — не поле
        // контракта профиля и не читается никогда. Отказ здесь — ChildRouteRefusal.RiskUnacknowledged
        // → код route_risk_unacknowledged в ChildProfileValidator.RouteReject: deepseek резолвится
        // в реестре хоста, но не-Anthropic без согласия. Флаг forPool меняет только текст ветки
        // NoRoute (до неё здесь не доходит) — текст отказа потому называет «не Anthropic», а не пул.
        var id = await NewContainerUser("kid-rules-provider");
        var body = new
        {
            enabled = true,
            providerKey = "deepseek",
            model = "deepseek-v4-pro",
            spaces = new Dictionary<string, object>(),
            acknowledgeRouteRisk = true,   // поля профиля нет в контракте: согласия для него не бывает
        };

        var (status, resp) = await PutAsync(id, body);
        status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(resp).Should().Be("route_risk_unacknowledged");
        resp.GetProperty("message").GetString().Should().Contain("профиля",
            "отказ называет маршрут профиля, а не предмета");
        _users.GetById(id)!.ChildMode.Should().BeNull("отказ включению — профиль не сохранён");
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

    [Fact]
    public async Task Put_УдалениеСироты_ВещаниеБросает_200_ПерсонаУдалена()
    {
        // Т2d + правка вердикта a18a26cf: сбой вещания удаления не должен превращать ответ
        // вызывающего в 500 — профиль уже сохранён, исключение внутри RemoveOrphanTutorsAsync
        // остаётся логом. Здесь ThrowingBroadcaster бросает ровно на вещании удаления: первый
        // PUT провит обоих репетиторов без отказа (ThrowFor ещё null), а второй не содержит
        // нового провижна — живой тьютор профиля и живой ключ «physics» возвращаются Early-return
        // без вещания, единственный ToOwner на этом пути — «deleted» сироты.
        var id = await EnableChild("kid-orphan-broadcast-fail");
        var (firstStatus, firstResp) = await PutAsync(id, new
        {
            enabled = true,
            providerKey = KidsKey,
            model = "sonnet",
            spaces = new Dictionary<string, object>(),
            subjects = new[]
            {
                new { id = "math", label = "Математика", glyph = "×÷", color = "info", providerKey = KidsKey, model = "sonnet" },
                new { id = "physics", label = "Физика", glyph = "×", color = "info", providerKey = KidsKey, model = "sonnet" },
            },
        });
        firstStatus.Should().Be(HttpStatusCode.OK);
        var subjects = firstResp.GetProperty("profile").GetProperty("subjects");
        var mathTutor = subjects[0].GetProperty("tutorPersonaId").GetString()!;
        var physicsTutor = subjects[1].GetProperty("tutorPersonaId").GetString()!;
        mathTutor.Should().NotBeNullOrEmpty();
        physicsTutor.Should().NotBeNullOrEmpty();

        // Второй PUT: «math» в теле нет — его ключ (живой, сохранённый слиянием по Id,
        // UserStore.cs:703) становится кандидатом сироты в RemovedSubjectTutors.
        _hosts.BroadcasterStub.ThrowFor = id;
        try
        {
            var (status, _) = await PutAsync(id, new
            {
                enabled = true,
                providerKey = KidsKey,
                model = "sonnet",
                spaces = new Dictionary<string, object>(),
                subjects = new[]
                {
                    new { id = "physics", label = "Физика", glyph = "×", color = "info", providerKey = KidsKey, model = "sonnet" },
                },
            });
            status.Should().Be(HttpStatusCode.OK,
                "сбой вещания — лог внутри провижера: профиль сохранён, ответ остаётся 200");

            var personas = _hosts.Services.GetRequiredService<PersonaManager>();
            personas.Get(mathTutor, id).Should().BeNull(
                "удаление предшествовало вещанию: отказ вещания не отменяет состоявшееся удаление");
            personas.Get(physicsTutor, id).Should().NotBeNull("репетитор оставшегося предмета не затронут");
        }
        finally
        {
            _hosts.BroadcasterStub.ThrowFor = null;
        }
    }

    [Fact]
    public async Task PutProfile_SubjectRemoved_TutorDeleted_ChatKept()
    {
        // Минор R-Т2d 66780808: RemovedSubjectTutors покрыта настоящим PUT: предмет убран
        // списком тела, его репетитор — сирота (кандидат по Id), репетитор оставшегося предмета
        // не тронут, а чат предмета остаётся — только для чтения.
        var id = await NewContainerUser("kid-subject-removed");
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Moscow).DateTime;
        // Окно, действительное «сейчас» (переход через полночь — законное окно): детский чат
        // тест создаёт, и калькулятор лимитов обязан его пустить.
        var spaces = new Dictionary<string, object>
        {
            ["lessons"] = new
            {
                windows = new[] { new { from = local.AddHours(-2).ToString("HH:mm", CultureInfo.InvariantCulture), to = local.AddHours(2).ToString("HH:mm", CultureInfo.InvariantCulture) } },
                dailyMinutes = 40,
            },
        };
        var (firstStatus, firstResp) = await PutAsync(id, new
        {
            enabled = true,
            providerKey = KidsKey,
            model = "sonnet",
            spaces,
            dailyBudgetUsd = 2,
            monthlyBudgetUsd = 40,
            subjects = new[]
            {
                new { id = "math", label = "Математика", glyph = "×÷", color = "info", providerKey = KidsKey, model = "sonnet" },
                new { id = "physics", label = "Физика", glyph = "×", color = "info", providerKey = KidsKey, model = "sonnet" },
            },
        });
        firstStatus.Should().Be(HttpStatusCode.OK);
        var subjectsJson = firstResp.GetProperty("profile").GetProperty("subjects");
        var mathTutor = subjectsJson[0].GetProperty("tutorPersonaId").GetString()!;
        var physicsTutor = subjectsJson[1].GetProperty("tutorPersonaId").GetString()!;
        mathTutor.Should().NotBeNullOrEmpty();
        physicsTutor.Should().NotBeNullOrEmpty();
        SeedRules(id, RulesT1, "правила согласованы"); // без согласия правил чат не создаётся (RulesPending)

        // Чат «Уроков» предмета «math» — своим клиентом ребёнка (детский чат админ не создаёт).
        var childClient = _hosts.CreateAuthenticatedClient("kid-subject-removed", KidsPassword);
        (await ChildTestSubjects.CreateChat(childClient, "math")).EnsureSuccessStatusCode();

        // Тот же body без «math»: список тела авторитетен — предмет убран.
        var (status, _) = await PutAsync(id, new
        {
            enabled = true,
            providerKey = KidsKey,
            model = "sonnet",
            spaces,
            dailyBudgetUsd = 2,
            monthlyBudgetUsd = 40,
            subjects = new[]
            {
                new { id = "physics", label = "Физика", glyph = "×", color = "info", providerKey = KidsKey, model = "sonnet" },
            },
        });
        status.Should().Be(HttpStatusCode.OK);
        _users.GetById(id)!.ChildMode!.Subjects.Should().ContainSingle().Which.Id.Should().Be("physics");

        var personas = _hosts.Services.GetRequiredService<PersonaManager>();
        personas.Get(mathTutor, id).Should().BeNull("сирота удалённого предмета удалена");
        personas.Get(physicsTutor, id).Should().NotBeNull("репетитор оставшегося предмета не тронут");

        // Чат предмета цел (только чтение — Т1c/Т1d): тот же чат предмета, с флагом закрытого
        // предмета.
        var resp = await childClient.GetAsync("/api/child/spaces/lessons/chats");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var chats = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("chats").EnumerateArray()
            .Where(c => c.GetProperty("subject").GetString() == "math").ToList();
        chats.Should().ContainSingle("чат удалённого предмета остаётся — только для чтения").Which
            .GetProperty("subjectClosed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task PutProfile_ParallelRollback_KeepsForeignTutor()
    {
        // Минор R-Т2b 3a7f4e3c: откат удаляет только репетиторов, созданных ЭТИМ запросом.
        // Чужой репетитор — живая персона ребёнка, чей ключ «сосед» записал в живой профиль
        // ПОСЛЕ снимка до цикла и ДО отката: откат по снимку удалил бы её как «новую», оставив
        // висящий ключ. Запись соседа вставляет тест в точке вещания первой creation запроса
        // (детерминированное вклинивание, тот же приём, что у теста гонки Т2d; настоящая
        // параллельность не моделируется).
        var id = await EnableChild("kid-rollback-race");
        _users.SeedChildSubjectsIfEmpty(id,
        [
            new ChildSubject { Id = "physics", Label = "Физика", Glyph = "×", Color = "info", ProviderKey = KidsKey, Model = "sonnet" },
            new ChildSubject { Id = "math", Label = "Математика", Glyph = "×÷", Color = "info", ProviderKey = KidsKey, Model = "sonnet" },
        ]).Should().BeTrue("профиль ребёнка пуст — засев проходит");

        var foreign = _hosts.Services.GetRequiredService<PersonaManager>().Create(
            id, "Репетитор: Математика", null, null, null, null, null, PersonaScope.Global, null, null, null, false);

        // Первая creation запроса (physics): хук вызывается после закрепления ключа — тест
        // читает свой ключ запроса и записывает соседский в «math»; затем отказ вещания —
        // запрос идёт в откат.
        string? ownTutor = null;
        _hosts.BroadcasterStub.ArmFirstCreated(() =>
        {
            ownTutor = StoredSubject(id, "physics").TutorPersonaId;
            ownTutor.Should().NotBeNullOrEmpty("запрос закрепил своего репетитора до вещания");
            StoredSubject(id, "math").TutorPersonaId = foreign.Id;
        });
        _hosts.BroadcasterStub.ThrowFor = id;
        try
        {
            // Тело без subjects: живой список сохраняется, провиж пустых ключей — своя
            // работа запроса.
            var (status, body) = await PutAsync(id, ValidBody());
            status.Should().Be(HttpStatusCode.InternalServerError);
            CodeOf(body).Should().Be("tutor_provision_failed");

            ownTutor.Should().NotBeNullOrEmpty("хук первой creation не вызвался — гонка не случилась");
            var personas = _hosts.Services.GetRequiredService<PersonaManager>();
            personas.Get(foreign.Id, id).Should().NotBeNull(
                "чужой репетитор появился в профиле после снимка: откат своих его не трогает");
            personas.Get(ownTutor!, id).Should().BeNull("персону, созданную этим запросом, откат удалил");
        }
        finally
        {
            _hosts.BroadcasterStub.OnFirstCreated = null;
            _hosts.BroadcasterStub.ThrowFor = null;
        }
    }

    // --- перезакрепление маршрута предмета (Т2e, решение Андрея 18.11) ---

    /// <summary>Тело формы с предметами заданных пар (id, поставщик, модель, согласие):
    /// список тела авторитетен (тот же формат, что у теста «предмет удалён — чат цел»), окно
    /// «Уроков» действительное «сейчас» — детский чат должен пройти И3 при создании.</summary>
    private static Dictionary<string, object?> RepinBody(params (string Id, string ProviderKey, string Model, bool? Ack)[] subjects)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Moscow).DateTime;
        return new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["providerKey"] = KidsKey,
            ["model"] = "sonnet",
            ["spaces"] = new Dictionary<string, object>
            {
                ["lessons"] = new
                {
                    windows = new[] { new { from = local.AddHours(-2).ToString("HH:mm", CultureInfo.InvariantCulture), to = local.AddHours(2).ToString("HH:mm", CultureInfo.InvariantCulture) } },
                    dailyMinutes = 40,
                },
            },
            ["dailyBudgetUsd"] = 2,
            ["monthlyBudgetUsd"] = 40,
            ["subjects"] = subjects.Select(s => new Dictionary<string, object?>
            {
                ["id"] = s.Id, ["label"] = "Предмет " + s.Id, ["glyph"] = "×", ["color"] = "info",
                ["providerKey"] = s.ProviderKey, ["model"] = s.Model,
                ["acknowledgeRouteRisk"] = s.Ack,
            }).ToArray(),
        };
    }

    /// <summary>Ребёнок с предметами и согласованными правилами: включение → PUT с предметами
    /// (провижн тьюторов, как у админа на живой ручке) → запись согласия правил — без него
    /// чат не создаётся (RulesPending, тот же приём, что у SeedRules в тесте удаления).</summary>
    private async Task<string> EnableChildWithSubjects(string name, params (string Id, string ProviderKey, string Model, bool? Ack)[] subjects)
    {
        var id = await NewContainerUser(name);
        var (status, _) = await PutAsync(id, RepinBody(subjects));
        status.Should().Be(HttpStatusCode.OK);
        SeedRules(id, RulesT1, "правила согласованы");
        return id;
    }

    /// <summary>Чат «Уроков» предмета, созданный клиентом ребёнка (логин — по имени, как в
    /// тесте удаления), — и его закреплённые при создании поля: админ детский чат не создаёт.</summary>
    private async Task<Session> CreateChildChatAsync(string childName, string subjectId)
    {
        var client = _hosts.CreateAuthenticatedClient(childName, KidsPassword);
        (await ChildTestSubjects.CreateChat(client, subjectId)).EnsureSuccessStatusCode();
        return ChildChatOf(_users.FindByUsername(childName)!.Id, subjectId);
    }

    /// <summary>Живой чат ребёнка с этим предметом — ровно один (реестр стора, не ответ ручки).</summary>
    private Session ChildChatOf(string childId, string subjectId) =>
        _hosts.Services.GetRequiredService<SessionManager>().GetAll()
            .Single(s => s.ChildSpace is not null && s.ChildSubject == subjectId && s.OwnerId == childId);

    [Fact]
    public async Task PutProfile_SubjectModelChanged_ChatsGetNewModel_OtherSubjectsAndChildrenUnchanged()
    {
        // Т2e (c1): смена модели предмета перезакрепляет его чаты. Отбор точный: только чаты
        // этого ребёнка этого предмета — соседний «math» того же ребёнка и «russian» другого
        // ребёнка (id предмета — slug и повторяется между детьми) не тронуты.
        // UpdatedAt не двигается: маршрут — настройка чата, а не активность (инвариант роли).
        var kid = await EnableChildWithSubjects("kid-repin-model",
            ("russian", KidsKey, "sonnet", null), ("math", KidsKey, "sonnet", null));
        var other = await EnableChildWithSubjects("kid-repin-other", ("russian", KidsKey, "sonnet", null));

        var russianChat = await CreateChildChatAsync("kid-repin-model", "russian");
        var mathChat = await CreateChildChatAsync("kid-repin-model", "math");
        var otherChat = await CreateChildChatAsync("kid-repin-other", "russian");
        var before = (russianChat.UpdatedAt, mathChat.UpdatedAt, otherChat.UpdatedAt);

        var (status, _) = await PutAsync(kid, RepinBody(("russian", KidsKey, "opus", null), ("math", KidsKey, "sonnet", null)));
        status.Should().Be(HttpStatusCode.OK);

        var sessions = _hosts.Services.GetRequiredService<SessionManager>();
        sessions.GetById(russianChat.Id)!.Model.Should().Be("opus", "чат предмета продолжает на новой модели");
        sessions.GetById(russianChat.Id)!.Provider.Should().Be(KidsKey, "та же запись пула — тот же профиль");
        sessions.GetById(russianChat.Id)!.PersonaId.Should().Be(StoredSubject(kid, "russian").TutorPersonaId,
            "репетитор предмета не изменился");
        sessions.GetById(mathChat.Id)!.Model.Should().Be("sonnet", "предмет без смены маршрута не перезакрепляется");
        sessions.GetById(otherChat.Id)!.Model.Should().Be("sonnet",
            "тот же slug у другого ребёнка не тронут: перезакрепление по владельцу");

        sessions.GetById(russianChat.Id)!.UpdatedAt.Should().Be(before.Item1, "перезакрепление не двигатель активности");
        sessions.GetById(mathChat.Id)!.UpdatedAt.Should().Be(before.Item2);
        sessions.GetById(otherChat.Id)!.UpdatedAt.Should().Be(before.Item3);
    }

    /// <summary>Кладёт транскрипт чата в профиль CLI его текущего поставщика (корень и cwd — как у
    /// хода и у ProviderMigration: песочный профиль и контейнерный путь) и возвращает путь файла
    /// в профиле заданного поставщика.</summary>
    private string SeedTranscript(string childId, Session chat, string fromKey, string csid, string toKey, out string srcFile)
    {
        var route = _hosts.Services.GetRequiredService<CliRoute>();
        var cwd = route.CwdForOwner(childId, route.TryResolveCwd(chat)!);
        var flat = ClaudeHomeServer.Services.Llm.TranscriptMigrator.FlattenCwd(cwd);
        srcFile = Path.Combine(route.ConfigRootFor(childId, fromKey), "projects", flat, csid + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(srcFile)!);
        File.WriteAllText(srcFile, "{\"type\":\"user\"}\n");
        _hosts.Services.GetRequiredService<SessionManager>().GetById(chat.Id)!.ClaudeSessionId = csid;
        return Path.Combine(route.ConfigRootFor(childId, toKey), "projects", flat, csid + ".jsonl");
    }

    [Fact]
    public async Task PutProfile_SubjectProviderChanged_ChatsMovedWithTranscript()
    {
        // Т2e2: сменился поставщик (пул → deepseek, с согласием) — чат перезакрепляется И его
        // транскрипт копируется в профиль нового поставщика: --resume находит его после рестарта.
        // Сверка И1-бис проходит (поля чата равны записи предмета). Исходный файл остаётся.
        var kid = await EnableChildWithSubjects("kid-repin-provider", ("russian", KidsKey, "sonnet", null));
        var chat = await CreateChildChatAsync("kid-repin-provider", "russian");
        var dst = SeedTranscript(kid, chat, KidsKey, "csid-repin-1", "deepseek", out var src);

        var (status, _) = await PutAsync(kid, RepinBody(("russian", "deepseek", "deepseek-v4-pro", true)));
        status.Should().Be(HttpStatusCode.OK);

        var live = _hosts.Services.GetRequiredService<SessionManager>().GetById(chat.Id)!;
        live.Provider.Should().Be("deepseek");
        live.Model.Should().Be("deepseek-v4-pro");
        live.PersonaId.Should().Be(StoredSubject(kid, "russian").TutorPersonaId);
        File.Exists(dst).Should().BeTrue("транскрипт скопирован в профиль нового поставщика");
        File.Exists(src).Should().BeTrue("исходный файл не удаляется");
        _hosts.Services.GetRequiredService<IChildModeGate>().LaunchMismatch(kid, live).Should().BeNull();
    }

    [Fact]
    public async Task PutProfile_SubjectProviderChanged_NoConsent_ChatsUntouched()
    {
        // Без согласия админа маршрут ChildRoute не принимает: PUT отклоняется валидатором,
        // чат и его транскрипт остаются как были.
        var kid = await EnableChildWithSubjects("kid-repin-noack", ("russian", KidsKey, "sonnet", null));
        var chat = await CreateChildChatAsync("kid-repin-noack", "russian");
        var dst = SeedTranscript(kid, chat, KidsKey, "csid-repin-2", "deepseek", out _);

        var (status, _) = await PutAsync(kid, RepinBody(("russian", "deepseek", "deepseek-v4-pro", null)));
        status.Should().NotBe(HttpStatusCode.OK);

        var live = _hosts.Services.GetRequiredService<SessionManager>().GetById(chat.Id)!;
        live.Provider.Should().Be(KidsKey);
        File.Exists(dst).Should().BeFalse();
    }

    [Fact]
    public async Task PutProfile_SubjectProviderChanged_OneChatFails_OthersMoved_Put200()
    {
        // Т2e2: у первого чата целевой файл транскрипта занят каталогом — копия падает; чат остаётся на
        // старых полях (его ход откажет «предмет закрыт»/несовпадением), второй чат переносится, PUT — 200.
        var kid = await EnableChildWithSubjects("kid-repin-partial", ("russian", KidsKey, "sonnet", null));
        var client = _hosts.CreateAuthenticatedClient("kid-repin-partial", KidsPassword);
        (await ChildTestSubjects.CreateChat(client, "russian")).EnsureSuccessStatusCode();
        (await ChildTestSubjects.CreateChat(client, "russian")).EnsureSuccessStatusCode();
        var sessions = _hosts.Services.GetRequiredService<SessionManager>();
        var chats = sessions.GetAll().Where(c => c.ChildSpace is not null && c.ChildSubject == "russian" && c.OwnerId == kid)
            .OrderBy(c => c.Id).ToList();
        chats.Should().HaveCount(2);
        var dstBad = SeedTranscript(kid, chats[0], KidsKey, "csid-partial-bad", "deepseek", out _);
        Directory.CreateDirectory(dstBad);
        var dstOk = SeedTranscript(kid, chats[1], KidsKey, "csid-partial-ok", "deepseek", out _);

        var (status, _) = await PutAsync(kid, RepinBody(("russian", "deepseek", "deepseek-v4-pro", true)));
        status.Should().Be(HttpStatusCode.OK);

        sessions.GetById(chats[0].Id)!.Provider.Should().Be(KidsKey, "сбой переноса — чат остаётся на старых полях");
        sessions.GetById(chats[1].Id)!.Provider.Should().Be("deepseek");
        File.Exists(dstOk).Should().BeTrue();
    }

    /// <summary>Помечает чат идущим ходом (признак awaited-хода агента, один из входов HasTurnInFlight).</summary>
    private void MarkTurnInFlight(string chatId)
    {
        var store = _hosts.Services.GetRequiredService<SessionStore>();
        store.HasTurnInFlight(chatId).Should().BeFalse("чат до пометки свободен");
        store.Sessions[chatId].TurnWaiter = new TaskCompletionSource<TurnResult>();
        store.HasTurnInFlight(chatId).Should().BeTrue("пометка сработала");
    }

    [Fact]
    public async Task PutProfile_SameProviderModelChanged_TurnInFlight_FieldsRewrittenAtOnce()
    {
        // Т2e2 (ревью be3ab479, п.1): идущий ход пропускает перенос только при СМЕНЕ ПОСТАВЩИКА.
        // Смена модели у того же поставщика (ADR-041 р.4, Т2e) переписывает поля сразу — идущий
        // ход доживает со старыми. Мутация «пропуск по HasTurnInFlight до providerChanged» — красный.
        var kid = await EnableChildWithSubjects("kid-repin-inflight-model", ("russian", KidsKey, "sonnet", null));
        var chat = await CreateChildChatAsync("kid-repin-inflight-model", "russian");
        MarkTurnInFlight(chat.Id);

        var (status, _) = await PutAsync(kid, RepinBody(("russian", KidsKey, "opus", null)));
        status.Should().Be(HttpStatusCode.OK);

        var live = _hosts.Services.GetRequiredService<SessionManager>().GetById(chat.Id)!;
        live.Model.Should().Be("opus", "тот же поставщик — поля переписаны, несмотря на идущий ход");
        live.Provider.Should().Be(KidsKey);
    }

    [Fact]
    public async Task PutProfile_ProviderChanged_TurnInFlight_ChatSkipped_TranscriptNotCopied()
    {
        // Т2e2 (ревью be3ab479, п.3): при смене поставщика чат с идущим ходом пропускается — копия
        // неполного транскрипта потеряла бы хвост ответа, который доживающий процесс допишет в старый
        // профиль. Поля и транскрипт не тронуты, PUT — 200. Мутация «удалить блок пропуска» — красный.
        var kid = await EnableChildWithSubjects("kid-repin-inflight-prov", ("russian", KidsKey, "sonnet", null));
        var chat = await CreateChildChatAsync("kid-repin-inflight-prov", "russian");
        var dst = SeedTranscript(kid, chat, KidsKey, "csid-inflight-1", "deepseek", out _);
        MarkTurnInFlight(chat.Id);

        var (status, _) = await PutAsync(kid, RepinBody(("russian", "deepseek", "deepseek-v4-pro", true)));
        status.Should().Be(HttpStatusCode.OK);

        var live = _hosts.Services.GetRequiredService<SessionManager>().GetById(chat.Id)!;
        live.Provider.Should().Be(KidsKey, "идущий ход доживает на прежнем поставщике");
        live.Model.Should().Be("sonnet");
        File.Exists(dst).Should().BeFalse("транскрипт не копируется под идущим ходом");
    }

    [Fact]
    public async Task CatchUp_ProviderChangedDuringTurn_NextStartMovesChatWithTranscript()
    {
        // Т2e2 (ревью be3ab479, п.2): PUT со сменой поставщика на идущем ходе пропустил чат; ход
        // кончился — на старте следующего (TurnIntake → ChatSettings → ProviderMigration.CatchUp…)
        // чат переносится на маршрут предмета вместе с транскриптом, и сверка И1-бис проходит.
        var kid = await EnableChildWithSubjects("kid-catchup", ("russian", KidsKey, "sonnet", null));
        var chat = await CreateChildChatAsync("kid-catchup", "russian");
        var dst = SeedTranscript(kid, chat, KidsKey, "csid-catchup-1", "deepseek", out _);
        MarkTurnInFlight(chat.Id);

        var (status, _) = await PutAsync(kid, RepinBody(("russian", "deepseek", "deepseek-v4-pro", true)));
        status.Should().Be(HttpStatusCode.OK);
        var sessions = _hosts.Services.GetRequiredService<SessionManager>();
        sessions.GetById(chat.Id)!.Provider.Should().Be(KidsKey, "под идущим ходом PUT чат пропустил");
        _hosts.Services.GetRequiredService<IChildModeGate>().LaunchMismatch(kid, sessions.GetById(chat.Id)!)
            .Should().NotBeNull("до догона ход отказал бы по расхождению маршрута");

        _hosts.Services.GetRequiredService<SessionStore>().Sessions[chat.Id].TurnWaiter = null;
        var moved = await _hosts.Services.GetRequiredService<ProviderMigration>().CatchUpChildChatAsync(chat.Id, kid);

        moved.Should().BeTrue();
        var live = sessions.GetById(chat.Id)!;
        live.Provider.Should().Be("deepseek");
        live.Model.Should().Be("deepseek-v4-pro");
        File.Exists(dst).Should().BeTrue("транскрипт скопирован в профиль нового поставщика");
        _hosts.Services.GetRequiredService<IChildModeGate>().LaunchMismatch(kid, live).Should().BeNull();
    }

    [Fact]
    public async Task CatchUp_PoolKey_ChatGetsExactlyThatKey_NotPick()
    {
        // И2′ для пути догона: цель — ровно ProviderKey предмета. Мутация «targetKey = Pick(model)»
        // отправила бы чат на аккаунт, выбранный пулом, а не на запись предмета.
        var kid = await EnableChildWithSubjects("kid-catchup-pool", ("russian", KidsKey, "sonnet", null));
        var chat = await CreateChildChatAsync("kid-catchup-pool", "russian");
        var dst = SeedTranscript(kid, chat, KidsKey, "csid-catchup-2", KidsKey2, out _);
        MarkTurnInFlight(chat.Id);
        (await PutAsync(kid, RepinBody(("russian", KidsKey2, "sonnet", null)))).Status.Should().Be(HttpStatusCode.OK);
        _hosts.Services.GetRequiredService<SessionStore>().Sessions[chat.Id].TurnWaiter = null;

        await _hosts.Services.GetRequiredService<ProviderMigration>().CatchUpChildChatAsync(chat.Id, kid);

        _hosts.Services.GetRequiredService<SessionManager>().GetById(chat.Id)!.Provider.Should().Be(KidsKey2);
        File.Exists(dst).Should().BeTrue("транскрипт переехал в профиль записи предмета");
    }

    [Fact]
    public async Task CatchUp_TurnStillInFlight_ChatSkipped()
    {
        // Догон под идущим ходом — тот же пропуск, что у PUT: поля и транскрипт не тронуты.
        var kid = await EnableChildWithSubjects("kid-catchup-busy", ("russian", KidsKey, "sonnet", null));
        var chat = await CreateChildChatAsync("kid-catchup-busy", "russian");
        var dst = SeedTranscript(kid, chat, KidsKey, "csid-catchup-3", "deepseek", out _);
        (await PutAsync(kid, RepinBody(("russian", "deepseek", "deepseek-v4-pro", true)))).Status.Should().Be(HttpStatusCode.OK);
        _hosts.Services.GetRequiredService<SessionManager>().GetById(chat.Id)!.Provider.Should().Be("deepseek");
        // Чат уже перенесён PUT'ом; вернём его на старые поля и займём ходом
        var live = _hosts.Services.GetRequiredService<SessionManager>().GetById(chat.Id)!;
        live.Provider = KidsKey; live.Model = "sonnet";
        File.Delete(dst);
        MarkTurnInFlight(chat.Id);

        var moved = await _hosts.Services.GetRequiredService<ProviderMigration>().CatchUpChildChatAsync(chat.Id, kid);

        moved.Should().BeFalse();
        live.Provider.Should().Be(KidsKey);
        File.Exists(dst).Should().BeFalse();
    }

    [Fact]
    public async Task CatchUp_AdultChat_Untouched()
    {
        // Чат без детского пространства (и владелец не ребёнок) догоном не затрагивается.
        var store = _hosts.Services.GetRequiredService<SessionStore>();
        var id = "adult-catchup-" + Guid.NewGuid().ToString("N");
        store.Sessions[id] = new SessionEntry
        {
            Info = new Session { Id = id, OwnerId = "adult-owner", Provider = KidsKey, Model = "sonnet" },
        };

        var moved = await _hosts.Services.GetRequiredService<ProviderMigration>().CatchUpChildChatAsync(id, "adult-owner");

        moved.Should().BeFalse();
        store.Sessions[id].Info.Provider.Should().Be(KidsKey);
        store.Sessions[id].Info.Model.Should().Be("sonnet");
        store.Sessions.TryRemove(id, out _);
    }

    [Fact]
    public async Task PutProfile_SubjectPoolKeyChanged_ChatGetsExactlyThatKey_NotPick()
    {
        // Т2e2, сторож И2′: цель переноса — ровно ProviderKey предмета. Мутация «targetKey =
        // _subscriptionPool.Pick(model)» выбирает аккаунт пула сама — чат уехал бы не на запись предмета.
        var kid = await EnableChildWithSubjects("kid-repin-pool", ("russian", KidsKey, "sonnet", null));
        var chat = await CreateChildChatAsync("kid-repin-pool", "russian");
        var dst = SeedTranscript(kid, chat, KidsKey, "csid-repin-3", KidsKey2, out _);

        var (status, _) = await PutAsync(kid, RepinBody(("russian", KidsKey2, "sonnet", null)));
        status.Should().Be(HttpStatusCode.OK);

        _hosts.Services.GetRequiredService<SessionManager>().GetById(chat.Id)!.Provider.Should().Be(KidsKey2);
        File.Exists(dst).Should().BeTrue("транскрипт переехал в профиль выбранной записи пула");
    }

    [Fact]
    public async Task RepinCrossProvider_TwoPuts_SingleFinalRoute()
    {
        // Т2e2 (конкурентность): первый PUT (russian→opus на пуле) встал на провиже нового предмета
        // «art» — после записи профиля и до перезакрепления; второй PUT целиком сменил поставщика
        // (→deepseek) и перенёс чат. Первый продолжается и берёт ЖИВОЙ маршрут (deepseek), а не
        // свой: чат один раз на итоговом маршруте, повторный перенос — no-op, транскрипт на месте.
        var kid = await EnableChildWithSubjects("kid-repin-2puts", ("russian", KidsKey, "sonnet", null));
        var chat = await CreateChildChatAsync("kid-repin-2puts", "russian");
        var dst = SeedTranscript(kid, chat, KidsKey, "csid-repin-4", "deepseek", out _);

        _hosts.BroadcasterStub.ArmCreateHold(kid);
        try
        {
            var putA = PutAsync(kid, RepinBody(("russian", KidsKey, "opus", null), ("art", KidsKey, "sonnet", null)));
            var arrived = false;
            try { arrived = await _hosts.BroadcasterStub.CreateHoldArrived!.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException) { }
            arrived.Should().BeTrue("первый запрос не встал на провиже — окна гонки не было");

            var (statusB, _) = await PutAsync(kid, RepinBody(("russian", "deepseek", "deepseek-v4-pro", true)));
            statusB.Should().Be(HttpStatusCode.OK);

            _hosts.BroadcasterStub.OpenCreateHold();
            (await putA).Status.Should().Be(HttpStatusCode.OK);
        }
        finally { _hosts.BroadcasterStub.ClearCreateHold(); }

        var live = _hosts.Services.GetRequiredService<SessionManager>().GetById(chat.Id)!;
        live.Provider.Should().Be("deepseek");
        live.Model.Should().Be("deepseek-v4-pro");
        File.Exists(dst).Should().BeTrue();
    }

    [Fact]
    public async Task Repin_StaleSnapshot_AfterOrphanRemoval_ChatsMatchLastWrite()
    {
        // Ревью Т2e: поля перезакрепления брались из снимка профиля до await RemoveOrphanTutorsAsync.
        // Первый PUT (убирает «math», russian остаётся sonnet) встал на удалении репетитора-сироты —
        // после записи профиля; второй PUT записал russian→opus и перезакрепил чат. Первый
        // продолжается: со снимком он вернул бы чат на «sonnet». Мутация «finalProfile вместо
        // живого чтения» — красный.
        var kid = await EnableChildWithSubjects("kid-repin-orphan", ("russian", KidsKey, "sonnet", null), ("math", KidsKey, "sonnet", null));
        var chat = await CreateChildChatAsync("kid-repin-orphan", "russian");

        _hosts.BroadcasterStub.ArmDeleteHold(kid);
        try
        {
            var putA = PutAsync(kid, RepinBody(("russian", KidsKey, "sonnet", null)));
            var arrived = false;
            try { arrived = await _hosts.BroadcasterStub.DeleteHoldArrived!.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException) { }
            arrived.Should().BeTrue("первый запрос не встал на удалении сироты — окна гонки не было");

            var (statusB, _) = await PutAsync(kid, RepinBody(("russian", KidsKey, "opus", null)));
            statusB.Should().Be(HttpStatusCode.OK);

            _hosts.BroadcasterStub.OpenDeleteHold();
            (await putA).Status.Should().Be(HttpStatusCode.OK);
        }
        finally { _hosts.BroadcasterStub.ClearDeleteHold(); }

        StoredSubject(kid, "russian").Model.Should().Be("opus");
        _hosts.Services.GetRequiredService<SessionManager>().GetById(chat.Id)!.Model.Should().Be("opus",
            "чат равен последней записи, а не снимку первого запроса");
    }

    [Fact]
    public async Task Repin_ParallelPuts_ChatsMatchFinalProfile()
    {
        // Т2e (c3): два PUT одного ребёнка. Первый записал свой маршрут (russian→opus) и встал
        // внутри провижна нового предмета «art» — на вещании его создания, то есть ПОСЛЕ записи
        // профиля и ДО перезакрепления; в этом окне второй PUT дошёл целиком (russian→haiku,
        // «art» из списка убран) и перезакрепил чаты. Первый продолжается после него и берёт
        // значения ЖИВОГО профиля, а не своего тела: чаты остаются при финальном профиле.
        // Мутация «брать поля из снимка до замка» — первый дописывает свой «sonnet» после
        // второго: чаты при значении старого запроса, тест красный.
        var kid = await EnableChildWithSubjects("kid-repin-race", ("russian", KidsKey, "sonnet", null));
        var chat = await CreateChildChatAsync("kid-repin-race", "russian");

        _hosts.BroadcasterStub.ArmCreateHold(kid);
        try
        {
            var putA = PutAsync(kid, RepinBody(("russian", KidsKey, "opus", null), ("art", KidsKey, "sonnet", null)));

            var arrived = false;
            try { arrived = await _hosts.BroadcasterStub.CreateHoldArrived!.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException) { }
            arrived.Should().BeTrue("первый запрос не встал на провиже нового предмета — окна гонки не было");

            var (statusB, _) = await PutAsync(kid, RepinBody(("russian", KidsKey, "haiku", null)));
            statusB.Should().Be(HttpStatusCode.OK);

            _hosts.BroadcasterStub.OpenCreateHold();
            var (statusA, _) = await putA;
            statusA.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            _hosts.BroadcasterStub.ClearCreateHold();
        }

        StoredSubject(kid, "russian").Model.Should().Be("haiku", "финальный маршрут — последней записи");
        _hosts.Services.GetRequiredService<SessionManager>().GetById(chat.Id)!.Model
            .Should().Be(StoredSubject(kid, "russian").Model,
                "чаты при финальном профиле, а не при значении старого запроса");
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
    public async Task PutProfile_CallerMissing_Returns403CallerNotFound()
    {
        // Минор R-К1b 24b98007: «вызывающий админ не найден в сторе» — отдельный отказ, а не
        // knowledge_unavailable с текстом про Dify. Состояние достижимо только внутри запроса:
        // политика ручки (AdminByStore) читает тот же стор до действия, и на момент авторизации
        // вызывающий в нём есть. Тест держит запрос на списке датасетов — точке после политики
        // и проверки тела, перед поиском вызывающего — и удаляет его там.
        var id = await NewContainerUser("kid-caller-gone");
        var goneAdminName = "admin-gone";
        var goneAdmin = await NewAdminUser(goneAdminName);
        var client = _hosts.CreateAuthenticatedClient(goneAdminName, KidsPassword);

        var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var open = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _hosts.DifyStub.HoldArrived = arrived;
        _hosts.DifyStub.HoldOpen = open;
        try
        {
            var send = client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/users/{id}/child-profile")
            { Content = JsonContent.Create(BodyWithSubjectKnowledge("d-kb")) });
            // Порог — страховка от зависания, не проверка: не дошёл — красный с текстом.
            var arrivedInTime = false;
            try { arrivedInTime = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException) { }
            arrivedInTime.Should().BeTrue("запрос не дошёл до списка датасетов");

            _users.Delete(goneAdmin); // автора привязки в сторе больше нет
            open.SetResult(true);

            var resp = await send;
            var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            CodeOf(body).Should().Be("caller_not_found", "отсутствие вызывающего — не отказ Dify");
            body.TryGetProperty("message", out _).Should().BeTrue("отказ формы {code,message}");
            _users.GetById(id)!.ChildMode.Should().BeNull("отказ до записи профиля");
        }
        finally
        {
            _hosts.DifyStub.HoldArrived = null;
            _hosts.DifyStub.HoldOpen = null;
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
        // Минор R-К1b 24b98007 (п.3): гонка двух PUT без записи в живой объект мимо замка и
        // без порога времени. Два PUT одного ребёнка привязывают РАЗНЫЕ датасеты (основной
        // адмен — свою «самостоятельную», другой адмен — глобальную); барьер на списке
        // датасетов: оба запроса встречаются на нём и идут дальше вместе — окно между
        // первым чтением профиля и записью под локом стора живо для обоих.
        var id = await EnableChild("kid-kb-race");
        var otherAdminName = "kbadmin-c";
        var otherAdmin = await NewAdminUser(otherAdminName);

        _hosts.DifyStub.ArmBarrier(2);
        try
        {
            var putA = PutAsync(id, BodyWithSubjectKnowledge("d-kb"));
            var clientB = _hosts.CreateAuthenticatedClient(otherAdminName, KidsPassword);
            var sendB = clientB.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/users/{id}/child-profile")
            { Content = JsonContent.Create(BodyWithSubjectKnowledge("d-global")) });

            // Барьер открылся только когда оба запроса дошли до списка — гонка состоялась.
            // Порог здесь — страховка от зависания, не проверка: не сошлись — красный с текстом.
            var raced = false;
            try { raced = await _hosts.DifyStub.BarrierArrived.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException) { }
            raced.Should().BeTrue("оба запроса не сошлись на списке датасетов — гонки не было");

            var (statusA, _) = await putA;
            var respB = await sendB;
            statusA.Should().Be(HttpStatusCode.OK);
            respB.StatusCode.Should().Be(HttpStatusCode.OK);

            // Итог — пара одного запроса целиком: либо основного адмена (он записал последним),
            // либо другого адмена (он записал последним). Смешанной пары (datasetId одного
            // запроса с автором другого) быть не может: слияние под локом стора либо сохраняет
            // живую пару целиком (тот же id), либо берёт кандидат одного запроса — его id,
            // его автор, его момент (UserStore.SetChildProfile).
            var kept = StoredKnowledge(id);
            var whole = (kept.DatasetId == "d-kb" && kept.AttachedByUserId == AdminId)
                || (kept.DatasetId == "d-global" && kept.AttachedByUserId == otherAdmin);
            whole.Should().BeTrue("пара привязки — из одного запроса, не смесь двух");
        }
        finally
        {
            _hosts.DifyStub.ClearBarrier();
        }
    }

    // --- GET живого профиля (Д1, дефект 06201244): редактор читает ровно то, что сохранил PUT ---

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string id)
    {
        var resp = await _admin.GetAsync($"/api/users/{id}/child-profile");
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length > 0 ? JsonSerializer.Deserialize<JsonElement>(text) : default);
    }

    [Fact]
    public async Task GetProfile_AfterPut_ReturnsExactlyThePutProfile()
    {
        // c1: PUT с двумя предметами → GET отдаёт тот же профиль, форма равна ответу PUT:
        // предметы в порядке профиля с их репетиторами, согласие и момент маршрута, окна и
        // лимиты, tutorPersonaId. До ручки №10-чтения редактор в свежей сессии видел пустой
        // черновик и сохранением затирал живые предметы (дефект 06201244).
        var id = await NewContainerUser("kid-get-roundtrip");
        var body = RepinBody(("math", KidsKey, "sonnet", null), ("physics", "deepseek", "deepseek-v4-pro", true));
        var (putStatus, putBody) = await PutAsync(id, body);
        putStatus.Should().Be(HttpStatusCode.OK);

        var (status, got) = await GetAsync(id);
        status.Should().Be(HttpStatusCode.OK);
        JsonElement.DeepEquals(putBody.GetProperty("profile"), got.GetProperty("profile")).Should().BeTrue(
            "ответ GET равен ответу PUT: та же форма в обе стороны, живые предметы вместо пустого черновика");
        got.GetProperty("tutorPersonaId").GetString().Should().Be(putBody.GetProperty("tutorPersonaId").GetString());

        // Содержание по пунктам карточки — не только побайтовое равенство: профиль включён,
        // предметы в порядке стора, у каждого свой репетитор, согласие предмета видно с моментом.
        var profile = got.GetProperty("profile");
        profile.GetProperty("enabled").GetBoolean().Should().BeTrue();
        profile.GetProperty("providerKey").GetString().Should().Be(KidsKey);
        profile.GetProperty("model").GetString().Should().Be("sonnet");
        profile.GetProperty("spaces").GetProperty("lessons").GetProperty("dailyMinutes").GetInt32().Should().Be(40);
        profile.GetProperty("spaces").GetProperty("lessons").GetProperty("windows")[0]
            .GetProperty("from").GetString().Should().NotBeNullOrEmpty();
        profile.GetProperty("dailyBudgetUsd").GetDecimal().Should().Be(2);

        var subjects = profile.GetProperty("subjects");
        subjects.EnumerateArray().Select(s => s.GetProperty("id").GetString())
            .Should().Equal(new[] { "math", "physics" },
                "порядок предметов — порядок плиток, как его сохраняет стор");
        for (var i = 0; i < subjects.GetArrayLength(); i++)
        {
            var subject = subjects[i];
            var stored = StoredSubject(id, subject.GetProperty("id").GetString()!);
            subject.GetProperty("tutorPersonaId").GetString().Should().Be(stored.TutorPersonaId,
                "репетитор предмета — живой ключ из стора, а не пуст");
            subject.GetProperty("providerKey").GetString().Should().Be(stored.ProviderKey);
            subject.GetProperty("model").GetString().Should().Be(stored.Model);
            subject.GetProperty("acknowledgeRouteRisk").GetBoolean().Should().Be(stored.RouteAcknowledgedAt is not null);
            subject.GetProperty("routeAcknowledgedAt").ValueKind
                .Should().Be(stored.RouteAcknowledgedAt is null ? JsonValueKind.Null : JsonValueKind.String);
        }
    }

    [Fact]
    public async Task GetProfile_NoProfile_200_ProfileNull_НичегоНеСоздаёт()
    {
        // Профиля нет → 200 с profile: null: это документированная форма ChildProfileResponse
        // (ChildProfileValidator.cs:262-264, тот же ответ, что отдаёт ветка выключения PUT),
        // а не 404 и не «пустой включённый».
        var id = await NewContainerUser("kid-get-none");
        var (status, body) = await GetAsync(id);
        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("profile").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("tutorPersonaId").GetString().Should().BeEmpty();

        // Только чтение: ни профиля, ни тьютора, ни записи в стор GET не делает.
        _users.GetById(id)!.ChildMode.Should().BeNull();
        PersonasOf(id).Should().BeEmpty();
        _policy.For(id).Should().BeNull();
    }

    [Fact]
    public async Task GetProfile_AfterDisable_200_ВыключенныйПрофильЦелён()
    {
        // «Выключен» и «никогда не было» — разные состояния, и редактор обязан различать их:
        // выключенный профиль остаётся в сторе с Enabled=false (тот же ответ ветки {enabled:false}).
        var id = await EnableChild("kid-get-off");
        var (status, body) = await PutAsync(id, new { enabled = false });
        status.Should().Be(HttpStatusCode.OK);

        var (getStatus, got) = await GetAsync(id);
        getStatus.Should().Be(HttpStatusCode.OK);
        var profile = got.GetProperty("profile");
        profile.GetProperty("enabled").GetBoolean().Should().BeFalse();
        profile.GetProperty("providerKey").GetString().Should().Be(KidsKey, "лимиты выключение не стирает");
        got.GetProperty("tutorPersonaId").GetString().Should().NotBeNullOrEmpty("тьютор выключенного профиля жив");
    }

    [Fact]
    public async Task Get_НеАдмин_403()
    {
        var id = await EnableChild("kid-get-403");
        var client = _hosts.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var resp = await client.GetAsync($"/api/users/{id}/child-profile");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_Ребёнок_СвойПрофиль_403()
    {
        // Тот же приём, что у PUT: ребёнок свой профиль не читает и не правит — его состояние
        // ему отдаёт status-ручка №1, а не админский контракт.
        var id = await EnableChild("kid-get-self");
        var client = _hosts.CreateAuthenticatedClient("kid-get-self", KidsPassword);
        var resp = await client.GetAsync($"/api/users/{id}/child-profile");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_НетПользователя_404_user_not_found()
    {
        var (status, body) = await GetAsync("no-such-user-id");
        status.Should().Be(HttpStatusCode.NotFound);
        CodeOf(body).Should().Be("user_not_found");
        body.TryGetProperty("message", out _).Should().BeTrue("отказ формы {code,message}, как у PUT");
    }

    [Fact]
    public async Task Get_ЛокальныйПользователь_200_ProfileNull_БезNotContainer()
    {
        // not_container — проверка ВКЛЮЧЕНИЯ (ChildProfileValidator.cs:34 достижим только когда
        // ветка `!req.Enabled` метода `UsersController.SetChildProfile` не взята). Та же локальная
        // учётка через { enabled: false } даёт 200, поэтому и чтению нечего отказывать: профиля нет → null.
        var resp = await _admin.PostAsJsonAsync("/api/users", new
        {
            username = "kid-get-local",
            password = KidsPassword,
            role = "user",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;

        var (status, body) = await GetAsync(id);
        status.Should().Be(HttpStatusCode.OK);
        CodeOf(body).Should().NotBe("not_container");
        body.GetProperty("profile").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>Подменённый Dify: список датасетов — по слову теста, отказ — по флагу.
    /// Путь листинга — /v1/datasets (BaseAddress + «datasets?page=…» из ListDatasetsAsync);
    /// всё остальное — пустой лист, чтобы ни один фоновый потребитель хоста не увидел здесь
    /// ничего неожиданного.
    /// Держатель и барьер — для тестов гонки ВНУТРИ запроса: список датасетов — точка после
    /// политики и проверки тела, где тест может стоять своим шлюзом (поиск вызывающего и
    /// слияние под локом стора — после неё). Тест ставит их сам и снимает в finally; чужие
    /// тесты к ним не обращаются.</summary>
    internal sealed class FakeDify : HttpMessageHandler
    {
        public bool Throw;
        public string DatasetsJson = "[]";

        // Держатель: первый /datasets сигналит приход и стоит, пока тест не откроет шлюз
        // (тест «вызывающий удалён во время запроса»).
        public TaskCompletionSource<bool>? HoldArrived;
        public TaskCompletionSource<bool>? HoldOpen;

        // Барьер N сторон: каждый /datasets приходит к барьеру; последний из ожидаемых
        // открывает шлюз, и все идут дальше одновременно (гонка двух PUT).
        private TaskCompletionSource<bool>? _barrier;
        private int _barrierRemaining;
        public void ArmBarrier(int parties)
        {
            _barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _barrierRemaining = parties;
        }
        public void ClearBarrier() => Volatile.Write(ref _barrier, null);
        public Task<bool> BarrierArrived => _barrier!.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var isDatasets = request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/datasets");
            if (isDatasets)
            {
                var open = Volatile.Read(ref HoldOpen);
                if (open is not null)
                {
                    Volatile.Read(ref HoldArrived)?.TrySetResult(true);
                    await open.Task.ConfigureAwait(false);
                }
                var barrier = Volatile.Read(ref _barrier);
                if (barrier is not null)
                {
                    if (Interlocked.Decrement(ref _barrierRemaining) <= 0) barrier.TrySetResult(true);
                    await barrier.Task.ConfigureAwait(false);
                }
            }
            if (Throw) throw new HttpRequestException("Dify не ответил");
            var json = isDatasets
                ? $"{{\"data\":{DatasetsJson},\"has_more\":false}}"
                : """{"data":[],"has_more":false}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    // Пропускающий стаб вещания: бросает только по указанному владельцу — стартовый
    // провижн хоста и чужие тесты он не трогает. internal: его держит internal-поле
    // фикстуры (private-тип для него был бы менее доступен — CS0052).
    internal sealed class ThrowingBroadcaster : ISessionBroadcaster
    {
        public string? ThrowFor { get; set; }

        // Хук первой «created» для владельца (тест гонки отката): вызывается ПОСЛЕ закрепления
        // ключа в сторе и ПЕРЕД отказом вещания — точка, где тест вклинивает свою запись в
        // живой профиль запроса. Счётчик обнуляется армингом: чужие тесты с ThrowFor уже
        // нарастили его на своих creation, и «первая» этого теста не стала бы первой.
        // Чужие тесты хука не ставят — поведение стаба прежнее.
        public Action? OnFirstCreated;
        private int _createdCount;
        public void ArmFirstCreated(Action hook)
        {
            Volatile.Write(ref _createdCount, 0);
            Volatile.Write(ref OnFirstCreated, hook);
        }

        // Держатель первого «created» для владельца (Т2e, тест гонки перезакрепления): запрос
        // стоит ВНУТРИ провижна — после записи профиля и до перезакрепления чатов; тест в этом
        // окне дописывает второй PUT и открывает шлюз. В отличие от отказа-вклинивания выше,
        // запрос не отменяется: окно между записью профиля и перезакреплением живое, и гонка
        // настоящая. Счётчик обнуляется армингом — «первый» этого теста именно его.
        public TaskCompletionSource<bool>? CreateHoldArrived;
        private TaskCompletionSource<bool>? _holdOpen;
        private string? _holdFor;
        private int _holdCount;
        public void ArmCreateHold(string ownerId)
        {
            Volatile.Write(ref _holdFor, ownerId);
            Volatile.Write(ref _holdCount, 0);
            CreateHoldArrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _holdOpen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public void OpenCreateHold() => Volatile.Read(ref _holdOpen)?.TrySetResult(true);
        public void ClearCreateHold()
        {
            Volatile.Write(ref _holdFor, null);
            Volatile.Write(ref CreateHoldArrived, null);
            _holdOpen = null;
        }

        // Держатель первого «deleted» (удаление репетитора-сироты): запрос стоит внутри
        // RemoveOrphanTutorsAsync — после записи профиля и до перезакрепления чатов.
        public TaskCompletionSource<bool>? DeleteHoldArrived;
        private TaskCompletionSource<bool>? _delOpen;
        private string? _delFor;
        private int _delCount;
        public void ArmDeleteHold(string ownerId)
        {
            Volatile.Write(ref _delFor, ownerId);
            Volatile.Write(ref _delCount, 0);
            DeleteHoldArrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _delOpen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public void OpenDeleteHold() => Volatile.Read(ref _delOpen)?.TrySetResult(true);
        public void ClearDeleteHold()
        {
            Volatile.Write(ref _delFor, null);
            Volatile.Write(ref DeleteHoldArrived, null);
            _delOpen = null;
        }

        private void MaybeThrow(string ownerId)
        {
            if (ownerId == ThrowFor) throw new InvalidOperationException("вещание тьютора не удалось");
        }

        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
        public Task ToOwner(string ownerId, ServerMessage message)
        {
            if (ownerId == ThrowFor && message is PersonasChangedMessage { Action: "created" } && ++_createdCount == 1)
            {
                OnFirstCreated?.Invoke();
                throw new InvalidOperationException("вещание создания отклонено тестом (вклинивание сделано)");
            }
            if (ownerId == _holdFor && message is PersonasChangedMessage { Action: "created" } && ++_holdCount == 1)
            {
                Volatile.Read(ref CreateHoldArrived)?.TrySetResult(true);
                Volatile.Read(ref _holdOpen)!.Task.GetAwaiter().GetResult();
            }
            if (ownerId == _delFor && message is PersonasChangedMessage { Action: "deleted" } && ++_delCount == 1)
            {
                Volatile.Read(ref DeleteHoldArrived)?.TrySetResult(true);
                Volatile.Read(ref _delOpen)!.Task.GetAwaiter().GetResult();
            }
            MaybeThrow(ownerId);
            return Task.CompletedTask;
        }
        public Task ToProject(string projectId, ServerMessage message) => Task.CompletedTask;
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
    }
}
