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
        // Корень песочницы — на фикстуре, а не на тест-классе: хост один на класс, и его
        // Sandbox:ProjectsRoot — значение первого экземпляра. Проект изолированного владельца
        // обязан лежать внутри НЕГО (ProjectManager.EnsureRootAllowed), поэтому тесту нужен
        // ровно тот путь, который получил хост, а не свежий guid следующего теста.
        internal readonly string SandboxRoot = Path.Combine(Path.GetTempPath(), "ccs-kids-profile-" + Guid.NewGuid().ToString("N"));

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
        _hosts.ExtraConfig["Sandbox:ProjectsRoot"] = _hosts.SandboxRoot;
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
    /// и дневным лимитом, опекун — существующий пользователь. Согласие на риск маршрута здесь
    /// стоит: без него не годен ни один маршрут, включая запись пула (И2″, решение 26), —
    /// тело без него уже не «валидное».</summary>
    private object ValidBody(string guardianId = "") => new
    {
        enabled = true,
        acknowledgeRouteRisk = true,
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

    /// <summary>Предмет сохранённого профиля — из стора, а не из ответа.</summary>
    /// <summary>Проект-предмет ребёнка (Б17a): репетитор — его руководитель (DefaultPersonaId).</summary>
    private Project LeadProject(string childId, string subjectId) =>
        _hosts.Services.GetRequiredService<ProjectManager>().GetByOwner(childId)
            .Single(p => p.ChildSubjectId == subjectId);

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
    public async Task Put_ПровайдерНеРезолвится_400_provider_not_api_key_ТекстОдинДляПрофиляИПредмета()
    {
        // Ключ, который не резолвится ни в одну запись (нет в пуле, нет в реестре провайдеров):
        // ChildRoute.Evaluate → NoRoute → RouteReject. Отдельной «профильной» ветки текста (forPool)
        // больше нет: маршрут ребёнка — любой маршрут реестра взрослых (решение 25), поэтому и
        // «что не так» у профиля и предмета одно. Мутация «вернуть ветку forPool» — красный на
        // Contain, не на коде.
        var id = await NewContainerUser("kid-provider-unresolved");
        var body = new { enabled = true, providerKey = "no-such-provider", model = "sonnet", spaces = new Dictionary<string, object>() };

        var (status, resp) = await PutAsync(id, body);
        status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(resp).Should().Be("provider_not_api_key");
        resp.GetProperty("message").GetString().Should().Contain("записью пула с API-ключом или включённым провайдером моделей",
            "текст NoRoute один: он называет и пул, и реестр провайдеров");
        _users.GetById(id)!.ChildMode.Should().BeNull();
    }

    [Fact]
    public async Task PutProfile_ЧатПравилНаЧужомПоставщике_БезСогласияПрофиля_400_route_risk_unacknowledged()
    {
        // Маршрут профиля (его несёт чат правил) — любой маршрут реестра взрослых, но только за
        // согласием профиля (решение 26): deepseek резолвится в реестре хоста и с ценой, поэтому
        // отказ — ChildRouteRefusal.RiskUnacknowledged, а не provider_not_api_key.
        var id = await NewContainerUser("kid-rules-provider");
        var body = new
        {
            enabled = true,
            providerKey = "deepseek",
            model = "deepseek-v4-pro",
            spaces = new Dictionary<string, object>(),
        };

        var (status, resp) = await PutAsync(id, body);
        status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(resp).Should().Be("route_risk_unacknowledged");
        resp.GetProperty("message").GetString().Should().Contain("нужно согласие на профиле ребёнка",
            "отказ называет согласие профиля — единственное, что теперь бывает");
        _users.GetById(id)!.ChildMode.Should().BeNull("отказ включению — профиль не сохранён");
    }

    [Fact]
    public async Task PutProfile_ЧатПравилНаЧужомПоставщике_ССогласиемПрофиля_200_ПараВСторе()
    {
        var id = await NewContainerUser("kid-rules-provider-ack");
        var (status, resp) = await PutAsync(id, new
        {
            enabled = true,
            providerKey = "deepseek",
            model = "deepseek-v4-pro",
            spaces = new Dictionary<string, object>(),
            acknowledgeRouteRisk = true,
        });
        status.Should().Be(HttpStatusCode.OK);

        var (at, by) = StoredConsent(id);
        at.Should().NotBeNull("согласие профиля ставится телом с acknowledgeRouteRisk=true");
        by.Should().Be(AdminId, "автор согласия — админ из claim запроса");
        resp.GetProperty("profile").GetProperty("acknowledgeRouteRisk").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Put_ОкноНечитаемое_400_invalid_window()
    {
        var id = await NewContainerUser("kid-window");
        // Согласие профиля в теле нужно, чтобы тело дошло до проверяемой проверки: без него
        // маршрут не годен (И2″) и отказ был бы route_risk_unacknowledged, а не invalid_window.
        var body = new
        {
            enabled = true,
            acknowledgeRouteRisk = true,
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
            // Тот же смысл, что в тесте нечитаемого окна: без согласия профиля отказ был бы по согласию.
            acknowledgeRouteRisk = true,
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
            // Тот же смысл, что в тестах окон: без согласия профиля отказ был бы по согласию.
            acknowledgeRouteRisk = true,
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
    public async Task Put_БезSubjects_ЖивыеПредметыСохранены_ОтветБезПолейМаршрута()
    {
        var id = await EnableChild("kid-subjects-keep");
        // Засев предмета прямой записью в живой профиль (как делает миграция).
        ChildTestSubjects.Seed(_hosts.Services, id);

        // PUT вкладки «Режим и время»: тела без subjects — правка лимитов не стирает
        // предметы (живое слияние Т2a: ToChildProfile даёт стору сигнал null, он сохраняет живой список).
        var (status, body) = await PutAsync(id, ValidBody());
        status.Should().Be(HttpStatusCode.OK);

        var stored = _users.GetById(id)!.ChildMode!;
        stored.Subjects.Should().ContainSingle("PUT без subjects не стирает предметы")
            .Which.Id.Should().Be(ChildTestSubjects.Key);

        // Ответ отдаёт плитку предмета без репетитора, маршрута и знаний (Б17c): их несёт проект-предмет.
        var tile = body.GetProperty("profile").GetProperty("subjects")[0];
        foreach (var removed in new[] { "tutorPersonaId", "providerKey", "model", "knowledge", "acknowledgeRouteRisk", "routeAcknowledgedAt" })
            tile.TryGetProperty(removed, out _).Should().BeFalse($"поле «{removed}» предмета снято");
        tile.GetProperty("knowledgeMoved").GetBoolean().Should().BeFalse("базы знаний у предмета не было");
    }

    // --- репетиторы предметов (Т2b): провижн при добавлении, откат при отказе ---

    [Fact]
    public async Task Put_НовыйПредмет_200_РепетиторПроведён_РуководительПроекта()
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

        // Б17a: репетитор — руководитель проекта предмета (DefaultPersonaId — единственная точка
        // правды), персона проектная и ребёнка; ключа в предмете больше нет (Б17c).
        var project = LeadProject(id, "math");
        var subjectTutor = project.DefaultPersonaId!;
        subjectTutor.Should().NotBeNullOrEmpty();
        var persona = _hosts.Services.GetRequiredService<PersonaManager>().Get(subjectTutor, id);
        persona.Should().NotBeNull();
        persona!.Name.Should().Be("Репетитор: Математика");
        persona.Scope.Should().Be(PersonaScope.Project);
        persona.ProjectId.Should().Be(project.Id);

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
    public async Task PutProfile_SubjectRemoved_LeadersKept_ChatKept()
    {
        // PUT убрал предмет списком тела: репетиторы-руководители проектов (ни удалённого, ни оставшегося
        // предмета) не трогаются — проект удалённого предмета живёт (ADR-041), чат остаётся только для
        // чтения. Б17c: контракта сирот старых ключей предмета больше нет (полей нет).
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
        // Согласие профиля в первом теле (решение 26): без него ни один маршрут не годен (И2″) —
        // тело отбилось бы до записи. Второе тело поля не несёт: живое согласие сохраняется.
        var (firstStatus, _) = await PutAsync(id, new
        {
            enabled = true,
            acknowledgeRouteRisk = true,
            providerKey = KidsKey,
            model = "sonnet",
            spaces,
            dailyBudgetUsd = 2,
            monthlyBudgetUsd = 40,
            subjects = new[]
            {
                new { id = "math", label = "Математика", glyph = "×÷", color = "info" },
                new { id = "physics", label = "Физика", glyph = "×", color = "info" },
            },
        });
        firstStatus.Should().Be(HttpStatusCode.OK);
        var mathLead = LeadProject(id, "math").DefaultPersonaId;
        var physicsLead = LeadProject(id, "physics").DefaultPersonaId;
        mathLead.Should().NotBeNullOrEmpty();
        physicsLead.Should().NotBeNullOrEmpty();
        var personas = _hosts.Services.GetRequiredService<PersonaManager>();
        SeedRules(id, RulesT1, "правила согласованы"); // без согласия правил чат не создаётся (RulesPending)

        // Урок «math» — своим клиентом ребёнка через единственную точку создания (Б9b): чат
        // проекта-предмета; руководителю заранее дана живая модель (И10), без неё точка 409.
        SetLeadModel(id, "math", "sonnet");
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
                new { id = "physics", label = "Физика", glyph = "×", color = "info" },
            },
        });
        status.Should().Be(HttpStatusCode.OK);
        _users.GetById(id)!.ChildMode!.Subjects.Should().ContainSingle().Which.Id.Should().Be("physics");

        // Руководители — не сироты: проект удалённого предмета живёт (ADR-041), его уроки остаются
        // видимыми; их удаление — не контракт этой ручки.
        personas.Get(mathLead!, id).Should().NotBeNull("руководитель удалённого предмета не сирота: проект живёт");
        personas.Get(physicsLead!, id).Should().NotBeNull("руководитель оставшегося предмета не тронут");

        // Чат предмета цел (только чтение — Т1c/Т1d): тот же чат проекта-предмета, с флагом закрытого
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
            new ChildSubject { Id = "physics", Label = "Физика", Glyph = "×", Color = "info"},
            new ChildSubject { Id = "math", Label = "Математика", Glyph = "×÷", Color = "info"},
        ]).Should().BeTrue("профиль ребёнка пуст — засев проходит");

        var foreign = _hosts.Services.GetRequiredService<PersonaManager>().Create(
            id, "Репетитор: Математика", null, null, null, null, null, PersonaScope.Global, null, null, null, false);

        // Первая creation запроса (physics): хук вызывается после закрепления руководителя —
        // тест читает id своей персоны; затем отказ вещания — провижнер откатывает её сам.
        // Чужая персона ребёнка при этом остаётся.
        string? ownTutor = null;
        _hosts.BroadcasterStub.ArmFirstCreated(() =>
        {
            ownTutor = LeadProject(id, "physics").DefaultPersonaId;
            ownTutor.Should().NotBeNullOrEmpty("запрос закрепил своего руководителя до вещания");
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
            LeadProject(id, "physics").Should().NotBeNull("проект предмета остаётся — повторный провижн его подхватит");
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
    /// «Уроков» действительное «сейчас» — детский чат должен пройти И3 при создании.
    /// Согласие на риск с решения 26 — одно на профиль, и без него не годен ни один маршрут
    /// (И2″), поэтому тело всегда с ним: флаг в строке предмета больше ничего не решает.
    /// Тесту, которому нужен отказ «без согласия», — снять поле из returned тела.</summary>
    private static Dictionary<string, object?> RepinBody(params (string Id, string ProviderKey, string Model, bool? Ack)[] subjects)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Moscow).DateTime;
        return new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["acknowledgeRouteRisk"] = true,
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
    /// (провижн проектов и руководителей, как у админа на живой ручке) → запись согласия правил — без него чат не создаётся (RulesPending,
    /// тот же приём, что у SeedRules в тесте удаления).</summary>
    private async Task<string> EnableChildWithSubjects(string name, params (string Id, string ProviderKey, string Model, bool? Ack)[] subjects)
    {
        var id = await NewContainerUser(name);
        var (status, _) = await PutAsync(id, RepinBody(subjects));
        status.Should().Be(HttpStatusCode.OK);
        SeedRules(id, RulesT1, "правила согласованы");
        return id;
    }

    /// <summary>Чат «Уроков» предмета, созданный клиентом ребёнка (логин — по имени, как в
    /// тесте удаления): Б9b — единственной точкой создания урока POST …/chat; это чат проекта-
    /// предмета, его репетитор — руководитель проекта (И10). До создания тест обязан дать
    /// руководителю развёрнутую модель (SetLeadModel): без живой модели урока точка создаёт
    /// 409 subject_not_ready.</summary>
    private async Task<Session> CreateChildChatAsync(string childName, string subjectId)
    {
        var client = _hosts.CreateAuthenticatedClient(childName, KidsPassword);
        var resp = await ChildTestSubjects.CreateChat(client, subjectId);
        var text = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.Created, $"чат предмета не создан: {text}");
        var id = JsonSerializer.Deserialize<JsonElement>(text).GetProperty("sessionId").GetString()!;
        return _hosts.Services.GetRequiredService<SessionManager>().GetById(id)!;
    }

    /// <summary>Живой чат ребёнка с этим предметом (реестр стора, не ответ ручки): для урока в
    /// проекте-предмете владелец — по проекту (SessionStore.ResolveOwnerId), как его считает
    /// и перезакрепление (ProviderMigration.RepinChildSubjectAsync).</summary>
    private List<Session> ChildChatsOf(string childId, string subjectId)
    {
        var store = _hosts.Services.GetRequiredService<SessionStore>();
        return _hosts.Services.GetRequiredService<SessionManager>().GetAll()
            .Where(s => s.ChildSpace is not null && s.ChildSubject == subjectId && store.ResolveOwnerId(s) == childId)
            .ToList();
    }

    /// <summary>Живая модель репетитора — модель персоны-руководителя проекта-предмета (И10):
    /// провижер Б17a создаёт её без своей модели (её задаёт куратор штатной дверью персоны,
    /// разрез §14.3) — тест даёт её той же дверью. Возвращает id руководителя.</summary>
    private string SetLeadModel(string childId, string subjectId, string model)
    {
        var leadId = LeadProject(childId, subjectId).DefaultPersonaId;
        leadId.Should().NotBeNullOrEmpty($"у предмета {subjectId} нет руководителя — провижер Б17a не отработал");
        _hosts.Services.GetRequiredService<PersonaManager>().Update(leadId!, childId,
            null, null, null, null, model, null, null, null, null, null, null);
        return leadId!;
    }

    [Fact]
    public async Task PutProfile_SubjectModelChanged_ChatsGetNewModel_OtherSubjectsAndChildrenUnchanged()
    {
        // Т2e (c1) на живом маршруте (И10, решение 71): модель урока — у персоны-руководителя,
        // записанная модель предмета больше не указ. Тест меняет модель руководителя «russian»
        // дверью персоны; следующий PUT перезакрепляет чаты этого предмета на живой маршрут.
        // Отбор точный: только чаты этого ребёнка этого предмета — соседний «math» того же
        // ребёнка и «russian» другого ребёнка (id предмета — slug и повторяется между детьми)
        // не тронуты. UpdatedAt не двигается: маршрут — настройка чата, а не активность (инвариант роли).
        var kid = await EnableChildWithSubjects("kid-repin-model",
            ("russian", KidsKey, "sonnet", null), ("math", KidsKey, "sonnet", null));
        var other = await EnableChildWithSubjects("kid-repin-other", ("russian", KidsKey, "sonnet", null));
        var russianLead = SetLeadModel(kid, "russian", "sonnet");
        SetLeadModel(kid, "math", "sonnet");
        SetLeadModel(other, "russian", "sonnet");

        var russianChat = await CreateChildChatAsync("kid-repin-model", "russian");
        var mathChat = await CreateChildChatAsync("kid-repin-model", "math");
        var otherChat = await CreateChildChatAsync("kid-repin-other", "russian");
        var before = (russianChat.UpdatedAt, mathChat.UpdatedAt, otherChat.UpdatedAt);

        // Живая модель репетитора «russian» — opus (та же дверь, что у куратора). Тело PUT с той же
        // парой остаётся валидным, но маршрут решает руководитель, а не запись предмета.
        _hosts.Services.GetRequiredService<PersonaManager>().Update(russianLead, kid,
            null, null, null, null, "opus", null, null, null, null, null, null);

        var (status, _) = await PutAsync(kid, RepinBody(("russian", KidsKey, "opus", null), ("math", KidsKey, "sonnet", null)));
        status.Should().Be(HttpStatusCode.OK);

        var sessions = _hosts.Services.GetRequiredService<SessionManager>();
        sessions.GetById(russianChat.Id)!.Model.Should().Be("opus", "чат предмета продолжает на новой модели репетитора");
        sessions.GetById(russianChat.Id)!.Provider.Should().Be(KidsKey, "та же запись пула — тот же профиль");
        sessions.GetById(russianChat.Id)!.PersonaId.Should().Be(russianLead,
            "репетитор предмета не изменился");
        sessions.GetById(mathChat.Id)!.Model.Should().Be("sonnet", "предмет без смены живого маршрута не перезакрепляется");
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
        // Т2e2 на живом маршруте (И10): смена поставщика начинается со смены модели репетитора
        // (поставщик — по модели): тест даёт руководителю «deepseek-v4-pro», следующий PUT
        // (предмет на deepseek, с согласием) перезакрепляет чат И его транскрипт копируется в
        // профиль нового поставщика: --resume находит его после рестарта. Сверка И1-бис проходит
        // (поля чата равны живому маршруту). Исходный файл остаётся.
        var kid = await EnableChildWithSubjects("kid-repin-provider", ("russian", KidsKey, "sonnet", null));
        var lead = SetLeadModel(kid, "russian", "sonnet");
        var chat = await CreateChildChatAsync("kid-repin-provider", "russian");
        var dst = SeedTranscript(kid, chat, KidsKey, "csid-repin-1", "deepseek", out var src);

        SetLeadModel(kid, "russian", "deepseek-v4-pro");
        var (status, _) = await PutAsync(kid, RepinBody(("russian", "deepseek", "deepseek-v4-pro", true)));
        status.Should().Be(HttpStatusCode.OK);

        var live = _hosts.Services.GetRequiredService<SessionManager>().GetById(chat.Id)!;
        live.Provider.Should().Be("deepseek");
        live.Model.Should().Be("deepseek-v4-pro");
        live.PersonaId.Should().Be(lead);
        File.Exists(dst).Should().BeTrue("транскрипт скопирован в профиль нового поставщика");
        File.Exists(src).Should().BeTrue("исходный файл не удаляется");
        _hosts.Services.GetRequiredService<IChildModeGate>().LaunchMismatch(kid, live).Should().BeNull();
    }

    [Fact]
    public async Task PutProfile_SubjectProviderChanged_NoConsent_ChatsUntouched()
    {
        // Отзыв согласия при живом маршруте (решение 26 + И2″): без согласия не годен ни один
        // маршрут, поэтому PUT отбивается валидатором — чат и его транскрипт остаются как были.
        // Флаг в строке предмета здесь null — он бы ничего не изменил: согласия предмета нет.
        var kid = await EnableChildWithSubjects("kid-repin-noack", ("russian", KidsKey, "sonnet", null));
        SetLeadModel(kid, "russian", "sonnet");
        var chat = await CreateChildChatAsync("kid-repin-noack", "russian");
        var dst = SeedTranscript(kid, chat, KidsKey, "csid-repin-2", "deepseek", out _);

        var body = RepinBody(("russian", "deepseek", "deepseek-v4-pro", null));
        body["acknowledgeRouteRisk"] = false; // адним снимает согласие — и тело становится невалидным
        var (status, resp) = await PutAsync(kid, body);
        status.Should().NotBe(HttpStatusCode.OK);
        CodeOf(resp).Should().Be("route_risk_unacknowledged");

        var live = _hosts.Services.GetRequiredService<SessionManager>().GetById(chat.Id)!;
        live.Provider.Should().Be(KidsKey);
        File.Exists(dst).Should().BeFalse();
    }

    [Fact]
    public async Task PutProfile_SubjectProviderChanged_OneChatFails_OthersMoved_Put200()
    {
        // Т2e2: у первого чата целевой файл транскрипта занят каталогом — копия падает; чат остаётся на
        // старых полях (его ход откажет несовпадением с живым маршрутом), второй чат переносится, PUT — 200.
        var kid = await EnableChildWithSubjects("kid-repin-partial", ("russian", KidsKey, "sonnet", null));
        SetLeadModel(kid, "russian", "sonnet");
        var client = _hosts.CreateAuthenticatedClient("kid-repin-partial", KidsPassword);
        (await ChildTestSubjects.CreateChat(client, "russian")).EnsureSuccessStatusCode();
        (await ChildTestSubjects.CreateChat(client, "russian")).EnsureSuccessStatusCode();
        var sessions = _hosts.Services.GetRequiredService<SessionManager>();
        var chats = ChildChatsOf(kid, "russian").OrderBy(c => c.Id).ToList();
        chats.Should().HaveCount(2);
        var dstBad = SeedTranscript(kid, chats[0], KidsKey, "csid-partial-bad", "deepseek", out _);
        Directory.CreateDirectory(dstBad);
        var dstOk = SeedTranscript(kid, chats[1], KidsKey, "csid-partial-ok", "deepseek", out _);

        SetLeadModel(kid, "russian", "deepseek-v4-pro");
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
        // ход доживает со старыми. Живая модель — у репетитора (И10): тест меняет её дверью
        // персоны; оба варианта — на записи пула KidsKey. Мутация «пропуск по HasTurnInFlight до
        // providerChanged» — красный.
        var kid = await EnableChildWithSubjects("kid-repin-inflight-model", ("russian", KidsKey, "sonnet", null));
        SetLeadModel(kid, "russian", "sonnet");
        var chat = await CreateChildChatAsync("kid-repin-inflight-model", "russian");
        MarkTurnInFlight(chat.Id);

        SetLeadModel(kid, "russian", "opus");
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
        // профиль. Смена поставщика начинается со смены модели репетитора (поставщик — по модели).
        // Поля и транскрипт не тронуты, PUT — 200. Мутация «удалить блок пропуска» — красный.
        var kid = await EnableChildWithSubjects("kid-repin-inflight-prov", ("russian", KidsKey, "sonnet", null));
        SetLeadModel(kid, "russian", "sonnet");
        var chat = await CreateChildChatAsync("kid-repin-inflight-prov", "russian");
        var dst = SeedTranscript(kid, chat, KidsKey, "csid-inflight-1", "deepseek", out _);
        MarkTurnInFlight(chat.Id);

        SetLeadModel(kid, "russian", "deepseek-v4-pro");
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
        // чат переносится на живой маршрут предмета вместе с транскриптом, и сверка И1-бис проходит.
        var kid = await EnableChildWithSubjects("kid-catchup", ("russian", KidsKey, "sonnet", null));
        SetLeadModel(kid, "russian", "sonnet");
        var chat = await CreateChildChatAsync("kid-catchup", "russian");
        var dst = SeedTranscript(kid, chat, KidsKey, "csid-catchup-1", "deepseek", out _);
        MarkTurnInFlight(chat.Id);

        SetLeadModel(kid, "russian", "deepseek-v4-pro");
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
    public async Task CatchUp_TurnStillInFlight_ChatSkipped()
    {
        // Догон под идущим ходом — тот же пропуск, что у PUT: поля и транскрипт не тронуты.
        var kid = await EnableChildWithSubjects("kid-catchup-busy", ("russian", KidsKey, "sonnet", null));
        SetLeadModel(kid, "russian", "sonnet");
        var chat = await CreateChildChatAsync("kid-catchup-busy", "russian");
        var dst = SeedTranscript(kid, chat, KidsKey, "csid-catchup-3", "deepseek", out _);
        SetLeadModel(kid, "russian", "deepseek-v4-pro");
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
    public async Task RepinCrossProvider_TwoPuts_SingleFinalRoute()
    {
        // Т2e2 (конкурентность): первый PUT (russian→opus на пуле) встал на провиже нового предмета
        // «art» — после записи профиля и до перезакрепления. В этом окне тест даёт репетитору «russian»
        // внешнюю модель (живой маршрут, И10) и второй PUT (→deepseek) доходит целиком, перенося чат.
        // Первый продолжается и берёт ЖИВОЙ маршрут (deepseek), а не свой: чат один раз на итоговом
        // маршруте, повторный перенос — no-op, транскрипт на месте.
        var kid = await EnableChildWithSubjects("kid-repin-2puts", ("russian", KidsKey, "sonnet", null));
        SetLeadModel(kid, "russian", "sonnet");
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

            SetLeadModel(kid, "russian", "deepseek-v4-pro");
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

    // --- согласие на риск маршрутов (ADR-041 ред. 4, решение 26): ОДНО на профиль ребёнка,
    // трёхзначный acknowledgeRouteRisk в теле профиля; в теле ПРЕДМЕТА поле игнорируется ---

    /// <summary>Тело валидного профиля с трёхзначным согласием на риск (решение 26): true —
    /// поставить, null — поля в теле нет («не трогать»), false — отозвать. Маршрут профиля здесь
    /// — запись пула с ApiKey; по И2″ она тоже маршрут только с согласием профиля, поэтому тело
    /// с null/флаговым отсутствием поля годится лишь когда согласие уже стоит в сторе.</summary>
    private static Dictionary<string, object?> BodyWithProfileConsent(object? acknowledge) => new()
    {
        ["enabled"] = true,
        ["providerKey"] = KidsKey,
        ["model"] = "sonnet",
        ["spaces"] = new Dictionary<string, object>(),
        ["guardianUserIds"] = Array.Empty<string>(),
        ["acknowledgeRouteRisk"] = acknowledge,
    };

    /// <summary>Согласие профиля — из стора, а не из ответа.</summary>
    private (DateTimeOffset? At, string? By) StoredConsent(string childId)
    {
        var profile = _users.GetById(childId)!.ChildMode!;
        return (profile.RouteRiskAcknowledgedAt, profile.RouteRiskAcknowledgedBy);
    }

    [Fact]
    public async Task PutProfile_СогласиеTrue_СтавитПару_ОтветЕёНесёт()
    {
        var id = await NewContainerUser("kid-consent-set");
        var (status, resp) = await PutAsync(id, BodyWithProfileConsent(true));
        status.Should().Be(HttpStatusCode.OK);

        var (at, by) = StoredConsent(id);
        at.Should().NotBeNull("согласие ставится телом с acknowledgeRouteRisk=true");
        by.Should().Be(AdminId, "автор — админ из claim запроса");
        var profile = resp.GetProperty("profile");
        profile.GetProperty("acknowledgeRouteRisk").GetBoolean().Should().BeTrue();
        profile.GetProperty("routeRiskAcknowledgedAt").ValueKind.Should().Be(JsonValueKind.String,
            "ответ — снимок после записи: момент согласия виден фронту");
    }

    [Fact]
    public async Task PutProfile_БезСогласия_ЗаписьПула_ТожеОтбивается_400_route_risk_unacknowledged()
    {
        // И2″ (решения 25–26): режима «безопасный маршрут без согласия» больше нет — без согласия
        // не годен ни один маршрут, включая запись пула с ApiKey. Форма маршрута проверяется
        // раньше (нет записи / чужая модель / нет цены), согласие — последнее: поэтому здесь
        // именно route_risk_unacknowledged, а не provider_not_api_key.
        var id = await NewContainerUser("kid-consent-none");
        var (status, resp) = await PutAsync(id, BodyWithProfileConsent(null));
        status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(resp).Should().Be("route_risk_unacknowledged");
        _users.GetById(id)!.ChildMode.Should().BeNull("отказ включению — профиля нет");
    }

    [Fact]
    public async Task PutProfile_СменаМоделиИПоставщика_СогласиеНеСбрасывается()
    {
        var id = await NewContainerUser("kid-consent-keep");
        var (first, _) = await PutAsync(id, new
        {
            enabled = true,
            providerKey = "deepseek",
            model = "deepseek-v4-pro",
            spaces = new Dictionary<string, object>(),
            acknowledgeRouteRisk = true,
        });
        first.Should().Be(HttpStatusCode.OK);
        var (at0, by0) = StoredConsent(id);
        at0.Should().NotBeNull();

        // PUT без поля согласия и с другим маршрутом профиля — сменились и поставщик, и модель.
        // Согласие одно на профиль и к маршруту не привязано (в отличие от согласия предмета):
        // пара остаётся той же, ответ отдаёт её как стоящую. Мутация «сбрасывать согласие при
        // смене маршрута» — красный.
        var (status, resp) = await PutAsync(id, new
        {
            enabled = true,
            providerKey = KidsKey,
            model = "sonnet",
            spaces = new Dictionary<string, object>(),
        });
        status.Should().Be(HttpStatusCode.OK);
        var (at, by) = StoredConsent(id);
        at.Should().Be(at0, "смена модели и поставщика согласия не касается");
        by.Should().Be(by0, "ни автора");
        resp.GetProperty("profile").GetProperty("acknowledgeRouteRisk").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task PutProfile_СогласиеFalse_Отозвано_ОбаПоляСнимаютсяВместе()
    {
        var id = await NewContainerUser("kid-consent-revoke");
        (await PutAsync(id, BodyWithProfileConsent(true))).Status.Should().Be(HttpStatusCode.OK);
        StoredConsent(id).At.Should().NotBeNull();

        // Отзыв — телом без маршрутов: при живом маршруте отозвать нельзя, потому что маршрут
        // без согласия не годен (И2″) и такое тело отбивается (это отдельный тест
        // PutProfile_SubjectProviderChanged_NoConsent_ChatsUntouched). Пустой маршрут допустим.
        var (status, resp) = await PutAsync(id, new
        {
            enabled = true,
            spaces = new Dictionary<string, object>(),
            acknowledgeRouteRisk = false,
        });
        status.Should().Be(HttpStatusCode.OK);
        var (at, by) = StoredConsent(id);
        at.Should().BeNull("снятая галка отзывает согласие безусловно");
        by.Should().BeNull("половины согласия нет: оба поля снимаются одной записью под локом");
        var profile = resp.GetProperty("profile");
        profile.GetProperty("acknowledgeRouteRisk").GetBoolean().Should().BeFalse();
        profile.GetProperty("routeRiskAcknowledgedAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task PutProfile_БезМаршрута_200_НеНастроен_ПолитикаDenyAll()
    {
        // Профиль без маршрутов («не настроен») допустим при включении: админ сохраняет лимиты,
        // ещё не выбрав поставщика. «Ходить нельзя» закрывает не валидатор, а политика:
        // нечитаемый профиль (пустые providerKey/model) — DenyAll. Мутация «отказывать пустой
        // маршрут» — красный на статусе.
        var id = await NewContainerUser("kid-no-route");
        var (status, _) = await PutAsync(id, new
        {
            enabled = true,
            spaces = new Dictionary<string, object> { ["lessons"] = new { dailyMinutes = 20 } },
        });
        status.Should().Be(HttpStatusCode.OK);

        var stored = _users.GetById(id)!.ChildMode!;
        stored.ProviderKey.Should().Be("");
        stored.Model.Should().Be("");
        StoredConsent(id).At.Should().BeNull("без маршрутов согласия тоже нет");
        _policy.For(id)!.DenyAll.Should().BeTrue("пустой маршрут — нечитаемый профиль: всё запрещено");
    }

    [Theory]
    // 20 прогонов в одном запуске (к2 c2): гонка нестабильна по природе, одиночный зелёный прогон
    // ничего не доказывает. Мутация «снять замок стора» (лок SetChildProfile) — красный здесь.
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    public async Task PutProfile_ГонкаСогласияИПравок_СогласиеСтоит_ПоляВторогоПрименены(int iter)
    {
        // Ребёнок уже с согласием (ValidBody): тело «не трогать» с маршрутом валидно ровно потому,
        // что живое согласие стоит (И2″) — и именно его второй PUT не должен затирать.
        var id = await EnableChild("kid-consent-race-" + iter);
        var at0 = StoredConsent(id).At!.Value;
        var manyGuardians = Enumerable.Repeat(AdminId, 60).ToArray();

        // Два PUT одного ребёнка одновременно: один ставит согласие, второй его не трогает и
        // правит другие поля (свою модель и свой список опекунов). Оба — длинный путь: проверка
        // 60 опекунов — десятки обращений к стору между первым чтением запроса и его записью
        // под локом, то есть окно, в котором гонка реальна.
        var putAck = PutAsync(id, new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["providerKey"] = KidsKey,
            ["model"] = "sonnet",
            ["spaces"] = new Dictionary<string, object>(),
            ["acknowledgeRouteRisk"] = true,
        });
        var putEdit = PutAsync(id, new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["providerKey"] = KidsKey,
            ["model"] = "haiku",
            ["spaces"] = new Dictionary<string, object>(),
            ["guardianUserIds"] = manyGuardians,
        });
        var both = Task.WhenAll(putAck, putEdit);
        (await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(30)))).Should().Be(both,
            $"итерация {iter}: гонка не завершилась");
        (await putAck).Status.Should().Be(HttpStatusCode.OK);
        (await putEdit).Status.Should().Be(HttpStatusCode.OK);

        var stored = _users.GetById(id)!.ChildMode!;
        // Согласие стоит при любом порядке записей и это та же пара: PUT «не трогать» живую
        // пару не стирает, PUT «поставить» при живом согласии её не переставляет, а замок по
        // пользователю (не по экземпляру запроса) не даёт перемешанной пары At/By.
        stored.RouteRiskAcknowledgedAt.Should().Be(at0,
            $"итерация {iter}: момент согласия — момент его постановки, его не двигает ни один из PUT");
        stored.RouteRiskAcknowledgedBy.Should().Be(AdminId,
            $"итерация {iter}: половины пары нет: автор согласия — админ, поставивший его");
        // Поля второго применены целиком: модель и список опекунов — из одной записи, а не смесь.
        var whole = (stored.Model == "sonnet" && stored.GuardianUserIds.Count == 0)
            || (stored.Model == "haiku" && stored.GuardianUserIds.Count == 60);
        whole.Should().BeTrue($"итерация {iter}: одновременные PUT дают один целый профиль, не смесь полей");
    }

    // 20 прогонов (c4): гонка нестабильна по природе, одиночный зелёный ничего не доказывает.
    // Мутация «пару согласия читать снимком профиля вне замка стора» — красный здесь: второй PUT,
    // прочитавший профиль до согласия первого, записал бы его обратно как «согласия нет».
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    public async Task PutProfile_ГонкаСогласияСЧистогоПрофиля_СогласиеСтоит_ПриЛюбомПорядке(int iter)
    {
        // Чистый профиль: включён, маршрутов нет, согласия нет (тот же состояние, что у
        // «не настроен» выше). Два PUT одновременно: первый ставит согласие и свой маршрут,
        // второй согласия не просит и правит другие поля (свою модель, свой список опекунов).
        var id = await NewContainerUser("kid-consent-clean-race-" + iter);
        (await PutAsync(id, new { enabled = true, spaces = new Dictionary<string, object>() }))
            .Status.Should().Be(HttpStatusCode.OK);
        StoredConsent(id).At.Should().BeNull("профиль чистый: согласия нет");
        var manyGuardians = Enumerable.Repeat(AdminId, 60).ToArray();

        var putAck = PutAsync(id, new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["providerKey"] = KidsKey,
            ["model"] = "sonnet",
            ["spaces"] = new Dictionary<string, object>(),
            ["acknowledgeRouteRisk"] = true,
        });
        var putEdit = PutAsync(id, new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["providerKey"] = KidsKey,
            ["model"] = "haiku",
            ["spaces"] = new Dictionary<string, object>(),
            ["guardianUserIds"] = manyGuardians,
        });
        var both = Task.WhenAll(putAck, putEdit);
        (await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(30)))).Should().Be(both,
            $"итерация {iter}: гонка не завершилась");
        var ack = await putAck;
        var edit = await putEdit;

        // Порядок любой. Согласие ставит тот, кто его просит, — он проходит всегда. Второй PUT
        // без живого согласия меняет маршрут, поэтому отклонён (это правильный исход, а не провал);
        // если согласие уже стоит — он проходит.
        ack.Status.Should().Be(HttpStatusCode.OK, $"итерация {iter}");
        (edit.Status == HttpStatusCode.OK || CodeOf(edit.Body) == "route_risk_unacknowledged")
            .Should().BeTrue($"итерация {iter}: отказ второго PUT — только по согласию, не чем-то другим");

        var stored = _users.GetById(id)!.ChildMode!;
        // Инвариант: согласие, которое успели поставить, не теряется ни в одном порядке и оно
        // целое (момент и автор — из одной записи).
        stored.RouteRiskAcknowledgedAt.Should().NotBeNull($"итерация {iter}: согласие потеряно вторым PUT");
        stored.RouteRiskAcknowledgedBy.Should().Be(AdminId, $"итерация {iter}: половины пары нет");
        // Профиль — одна целая запись, не смесь полей двух запросов.
        var whole = (stored.Model == "sonnet" && stored.GuardianUserIds.Count == 0)
            || (stored.Model == "haiku" && stored.GuardianUserIds.Count == 60);
        whole.Should().BeTrue($"итерация {iter}: одновременные PUT дали смесь полей, а не целый профиль");
    }

    // --- Б2b (И10, решение 71): живой маршрут урока — модель персоны-руководителя проекта-предмета ---

    [Fact]
    public async Task SubjectProject_ChatCatchesUpToLeadPersonaModel_NotToSubjectRecord()
    {
        // И10: модель урока — у руководителя проекта-предмета, записанная модель предмета больше
        // не указ. Проект и руководитель — провижер Б17a на PUT с предметом; урок создан на живой
        // модели руководителя («sonnet» — та же, что в записи предмета). Тест меняет модель
        // руководителя на «haiku» (дверью персоны): чат отстаёт от живого маршрута, ход ему отказал
        // бы, догон приводит поля к живому маршруту и сверка проходит. Мутация «LiveRouteOf брать
        // поля предмета, когда проект-предмета есть» — красный на первом же утверждении (догон
        // нечего менять: запись предмета — «sonnet»).
        var name = "kid-lead-model";
        var kid = await EnableChildWithSubjects(name, ("russian", KidsKey, "sonnet", null));
        var tutorId = SetLeadModel(kid, "russian", "sonnet");
        var chat = await CreateChildChatAsync(name, "russian");
        var sessions = _hosts.Services.GetRequiredService<SessionManager>();
        var migration = _hosts.Services.GetRequiredService<ProviderMigration>();
        var gate = _hosts.Services.GetRequiredService<IChildModeGate>();

        SetLeadModel(kid, "russian", "haiku");
        gate.LaunchMismatch(kid, sessions.GetById(chat.Id)!).Should().NotBeNull(
            "поля чата отстают от живой модели репетитора — до догона ход отказал бы");
        (await migration.CatchUpChildChatAsync(chat.Id, kid)).Should().BeTrue();

        var pinned = sessions.GetById(chat.Id)!;
        pinned.PersonaId.Should().Be(tutorId, "репетитор чата — руководитель проекта");
        pinned.Model.Should().Be("haiku");
        pinned.Provider.Should().Be(KidsKey,
            "поставщик — по модели: Claude-модель обслуживает запись предмета, Pick не зовётся (И2′)");
        gate.LaunchMismatch(kid, pinned).Should().BeNull();

        // Смена модели репетитора любой дверью (здесь — правкой персоны) → следующий догон приводит
        // поля снова: хук на каждую дверь не нужен (решение 71).
        _hosts.Services.GetRequiredService<PersonaManager>().Update(tutorId, kid,
            null, null, null, null, "sonnet", null, null, null, null, null, null);
        (await migration.CatchUpChildChatAsync(chat.Id, kid)).Should().BeTrue();
        sessions.GetById(chat.Id)!.Model.Should().Be("sonnet");
        gate.LaunchMismatch(kid, sessions.GetById(chat.Id)!).Should().BeNull();
    }

    [Fact]
    public async Task SubjectProject_LeadPersonaDeleted_RefusalWithReason_NoException()
    {
        // Сбой резолва персоны (руководителя удалили) — отказ запуска с причиной, без
        // необработанного исключения; догон такие чаты не трогает.
        var name = "kid-lead-gone";
        var kid = await EnableChildWithSubjects(name, ("russian", KidsKey, "sonnet", null));
        var tutorId = SetLeadModel(kid, "russian", "sonnet");
        var chat = await CreateChildChatAsync(name, "russian");
        var sessions = _hosts.Services.GetRequiredService<SessionManager>();
        var gate = _hosts.Services.GetRequiredService<IChildModeGate>();
        var live = sessions.GetById(chat.Id)!;
        live.PersonaId.Should().Be(tutorId, "урок создан на руководителе проекта (И10)");
        gate.LaunchMismatch(kid, live).Should().BeNull("на живом репетиторе и той же модели сверка проходит");

        _hosts.Services.GetRequiredService<PersonaManager>().Delete(tutorId, kid);

        var act = () => gate.LaunchMismatch(kid, live);
        act.Should().NotThrow("причина — текст отказа, не исключение");
        act().Should().Be("персона-репетитор предмета не найдена");
        gate.LiveRouteFor(kid, live).Should().BeNull("живого маршрута нет — приводить нечего");
    }

    // 20 прогонов (c4): ход закрепления и правка модели персоны идут одновременно.
    // Мутация «писать поля чата по частям вне очереди догона» — красный: поля вышли бы смесью
    // состояний двух запросов (персона одного, модель другого).
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    public async Task SubjectProject_МодельРепетитораМеняетсяНаСтартеХода_ЧатНаОдномЦеломМаршруте(int iter)
    {
        var name = $"kid-lead-race-{iter}";
        var kid = await EnableChildWithSubjects(name, ("russian", KidsKey, "sonnet", null));
        var tutorId = SetLeadModel(kid, "russian", "haiku");
        var chat = await CreateChildChatAsync(name, "russian");
        var sessions = _hosts.Services.GetRequiredService<SessionManager>();
        var migration = _hosts.Services.GetRequiredService<ProviderMigration>();
        var gate = _hosts.Services.GetRequiredService<IChildModeGate>();

        // Одновременно: куратор меняет модель репетитора (дверь персоны) и стартует ход (догон
        // на его старте). Ход обязан пойти или на прежнем, или на новом маршруте ЦЕЛИКОМ.
        var change = Task.Run(() => _hosts.Services.GetRequiredService<PersonaManager>()
            .Update(tutorId, kid, null, null, null, null, "sonnet", null, null, null, null, null, null));
        var both = Task.WhenAll(change, migration.CatchUpChildChatAsync(chat.Id, kid));
        (await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(30)))).Should().Be(both,
            $"итерация {iter}: гонка не завершилась");

        var live = sessions.GetById(chat.Id)!;
        live.PersonaId.Should().Be(tutorId, $"итерация {iter}");
        live.Model.Should().BeOneOf("haiku", "sonnet",
            $"итерация {iter}: поля чата — одна целая модель, не смесь двух запросов");

        // Живой маршрут — ровно тот, что сейчас у репетитора; следующий догон приводит поля к нему,
        // и строгая сверка проходит. Это и есть «догон покрывает любую дверь правки».
        var target = gate.LiveRouteFor(kid, live);
        target.Should().NotBeNull($"итерация {iter}");
        target!.Model.Should().Be("sonnet");
        // Следующий догон приводит поля к живому маршруту — или они уже приведены этим ходом,
        // и тогда менять нечего. Значит не факт, что догон что-то изменил; факт другой: после
        // него поля равны живому маршруту, и строгая сверка проходит.
        await migration.CatchUpChildChatAsync(chat.Id, kid);
        var settled = sessions.GetById(chat.Id)!;
        settled.Model.Should().Be("sonnet", $"итерация {iter}: поля чата не приведены к живому маршруту");
        gate.LaunchMismatch(kid, settled).Should().BeNull($"итерация {iter}");
    }

    [Fact]
    public async Task PutProfile_ПровижерУпал_500_СогласиеЛибоЦеликомЛибоНикак()
    {
        // 500 с откатом профиля (провижер тьютора бросает после записи) не оставляет половины
        // согласия: первый PUT (профиля не было) — после отката профиля нет целиком, ни момента,
        // ни автора.
        var id = await NewContainerUser("kid-consent-rollback");
        _hosts.BroadcasterStub.ThrowFor = id;
        try
        {
            var (status, _) = await PutAsync(id, BodyWithProfileConsent(true));
            status.Should().Be(HttpStatusCode.InternalServerError);
            _users.GetById(id)!.ChildMode.Should().BeNull("откат возвращает прежний снимок целиком");
        }
        finally
        {
            _hosts.BroadcasterStub.ThrowFor = null;
        }

        // Ребёнок с согласием, отзыв под упавшим провижером: откат возвращает прежний профиль
        // как был — пара остаётся обеими полями, «половины» (At без By) после 500 быть не может.
        // Отзыв — телом без маршрутов (при живом маршруте отозвать нельзя: маршрут без согласия
        // не годен, такое тело отбивается до стора — это другой тест).
        var kid = await EnableChild("kid-consent-rollback-2");
        (await PutAsync(kid, BodyWithProfileConsent(true))).Status.Should().Be(HttpStatusCode.OK);
        var (at0, by0) = StoredConsent(kid);
        _users.GetById(kid)!.ChildMode!.TutorPersonaId = ""; // ключ тьютора «повис» — Ensure бросит
        _hosts.BroadcasterStub.ThrowFor = kid;
        try
        {
            var (status, _) = await PutAsync(kid, new
            {
                enabled = true,
                spaces = new Dictionary<string, object>(),
                acknowledgeRouteRisk = false,
            });
            status.Should().Be(HttpStatusCode.InternalServerError);
            var (at, by) = StoredConsent(kid);
            (at, by).Should().Be((at0, by0), "откат целиком: согласие либо ценой, либо никакого");
        }
        finally
        {
            _hosts.BroadcasterStub.ThrowFor = null;
        }
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
        // предметы в порядке стора, у каждого свой репетитор, согласие профиля видно с моментом
        // (решение 26: оно одно на профиль; у предмета в ответе — только историческая пара).
        var profile = got.GetProperty("profile");
        profile.GetProperty("enabled").GetBoolean().Should().BeTrue();
        profile.GetProperty("providerKey").GetString().Should().Be(KidsKey);
        profile.GetProperty("model").GetString().Should().Be("sonnet");
        profile.GetProperty("acknowledgeRouteRisk").GetBoolean().Should().BeTrue(
            "тело просило согласие — оно стоит и видно обеим ручкам");
        profile.GetProperty("routeRiskAcknowledgedAt").ValueKind.Should().Be(JsonValueKind.String);
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
            foreach (var removed in new[] { "tutorPersonaId", "providerKey", "model", "knowledge", "acknowledgeRouteRisk", "routeAcknowledgedAt" })
                subject.TryGetProperty(removed, out _).Should().BeFalse($"поле «{removed}» предмета снято (Б17c)");
            subject.GetProperty("knowledgeMoved").GetBoolean().Should().Be(stored.KnowledgeMoved);
        }
    }

    [Fact]
    public async Task GetProfile_NoProfile_200_ProfileNull_НичегоНеСоздаёт()
    {
        // Профиля нет → 200 с profile: null: это документированная форма ChildProfileResponse
        // (record ChildProfileResponse в конце ChildProfileValidator.cs, тот же ответ, что отдаёт
        // ветка выключения PUT),
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
        // not_container — проверка ВКЛЮЧЕНИЯ (ветка ExecutionEnvironment != Container в начале
        // ChildProfileValidator.Validate, её зовёт Put_ЛокальнаяСреда_400_not_container; достижима
        // только когда ветка `!req.Enabled` метода `UsersController.SetChildProfile` не взята). Та же локальная
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
