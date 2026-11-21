using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ClaudeHomeServer.Tests.Services.Mcp;

/// <summary>
/// Тулсет репетитора (К3, разрез «Знания репетитора» §3, И6, Б13b, Б17c): search_materials на МАРШРУТЕ /mcp/tutor/{sessionId}. Хост настоящий: ребёнок, его проект-предмета и
/// его чат заводятся боевыми ручками, KnowledgeService — настоящий с подменённым HTTP (ответ поиска
/// диктует тест). Тулсет берётся из реестра DI, а не строится руками: проверяется то, что едет в бой.
///
/// Что фиксируют тесты:
/// — состав: ровно один инструмент на валидной паре «владелец, сессия», пусто на каждой невалидной
///   (шаги 1–4 §3: не ребёнок, DenyAll, чужой владелец, чат правил, без предмета, расхождение И1-бис,
///   в том числе смена руководителя проекта-предмета);
/// — вызов повторяет тот же резолв: каждый отказ — isError с одним нейтральным текстом, наружу
///   ничего не течёт;
/// — Б13b: search_materials ищет ТОЛЬКО по базе проекта-предмета этого чата (WorkspaceKnowledgeStore
///   по рабочей папке сессии); аргумент dataset_id схемой не принят и поиск его игнорирует;
/// — у проекта без базы — отдельный честный отказ;
/// — Б17c: list_materials/read_material и привязанная база предмета (ChildSubject.Knowledge) сняты.
///
/// Фикстура урока (Б9a: урок — чат проекта-предмета): чат создаётся до детского профиля (старт
/// детского чата с предметом вне проекта гейт отказывает «старый урок», а сервисной точки создания
/// чата ВНУТРИ проекта-предмета ещё нет — её заводит Б9b), затем поля урока дописываются прямой
/// записью — той же техникой, которой тесты правят поля чата. Проект-предмета и его руководитель
/// с явной моделью — прямая запись (образцы: UsersChildProfileTests.SeedSubjectProject,
/// ChildLaunchPinTests.SeedSubjectProject): провижер Б17a делает то же самое на точках записи.
/// </summary>
[Trait("Category", "Host")]
public class TutorToolsetTests : IClassFixture<TutorToolsetTests.Hosts>
{
    public sealed class Hosts : TestWebApplicationFactory
    {
        // Корень песочницы — на фикстуре, а не на тест-классе: хост один на класс, и его
        // Sandbox:ProjectsRoot — значение первого экземпляра. Проект-предмета обязан лежать
        // внутри НЕГО (ProjectManager.EnsureRootAllowed), поэтому тесту нужен ровно тот путь,
        // который получил хост, а не свежий guid следующего теста.
        internal readonly string SandboxRoot = Path.Combine(Path.GetTempPath(), "ccs-tutor-" + Guid.NewGuid().ToString("N"));

        internal readonly FakeDify DifyStub = new();
        internal readonly DifyOptions Dify = new() { ApiUrl = "http://dify.test/v1", ApiKey = "test-key" };

        public Hosts() => ExtraServices = services =>
        {
            services.AddSingleton<IOwnerSpendReader>(new FixedSpend());
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

    private sealed class FixedSpend : IOwnerSpendReader
    {
        public decimal TodayUsd(string ownerId, TimeZoneInfo timeZone) => 0.1m;
        public decimal MonthUsd(string ownerId, TimeZoneInfo timeZone) => 0.5m;
    }

    /// <summary>Подменённый Dify: ответ поиска — по слову теста; путь /retrieve запоминается.</summary>
    internal sealed class FakeDify : HttpMessageHandler
    {
        public bool Throw;
        public string RecordsJson = "[]";
        public readonly List<string> RetrievePaths = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Throw) throw new HttpRequestException("Dify не ответил");
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (request.Method == HttpMethod.Post && path.EndsWith("/retrieve"))
            {
                lock (RetrievePaths) RetrievePaths.Add(path);
                json = $"{{\"records\":{RecordsJson}}}";
            }
            else json = """{"data":[],"has_more":false}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private const string KidsKey = "kids-api";
    private const string ChildPassword = "kids-test-password";
    private const string Subject = ChildTestSubjects.Key;
    private const string Model = "sonnet";
    // Датасет проекта-предмета (его заводит синк Б13a): запрос идёт ровно в него.
    private const string ProjectDatasetId = "d-subject-kb";
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    private readonly Hosts _hosts;
    private readonly HttpClient _admin;
    private readonly UserStore _users;
    private readonly TutorToolset _tutor;
    private readonly WorkspaceKnowledgeStore _knowledge;

    public TutorToolsetTests(Hosts hosts)
    {
        _hosts = hosts;
        _hosts.ExtraConfig[$"ClaudeSubscriptions:{KidsKey}:ApiKey"] = "sk-ant-test-kids";
        _hosts.ExtraConfig["Sandbox:ProjectsRoot"] = _hosts.SandboxRoot;
        _admin = _hosts.CreateAuthenticatedClient();
        _users = _hosts.Services.GetRequiredService<UserStore>();
        _tutor = (TutorToolset)_hosts.Services.GetRequiredService<McpToolsetRegistry>().Find(TutorToolset.ServerName)!;
        _knowledge = _hosts.Services.GetRequiredService<WorkspaceKnowledgeStore>();
        _hosts.DifyStub.Throw = false;
        _hosts.DifyStub.RecordsJson = "[]";
        _hosts.Dify.ApiUrl = "http://dify.test/v1";
    }

    private string AdminId => _users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private sealed record Kid(string Id, string SessionId, string ProjectId, string Root, string LeaderId);

    /// <summary>
    /// Ребёнок с предметом «russian», его проектом-предмета (маркер ChildSubjectId), персоной-
    /// руководителем с явной моделью и чатом этого проекта. withProjectKnowledge — запись базы
    /// проекта в WorkspaceKnowledgeStore (то, что оставит синк Б13a).
    /// </summary>
    private async Task<Kid> NewKidAsync(bool withProjectKnowledge = true)
    {
        var name = "kid-" + Guid.NewGuid().ToString("N")[..10];
        var resp = await _admin.PostAsJsonAsync("/api/users", new
        {
            username = name, password = ChildPassword, role = "user",
            executionEnvironment = ExecutionEnvironments.Container,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;

        // Чат создаётся ДО детского профиля: сервисной точки создания чата внутри проекта-предмета
        // ещё нет (её заводит Б9b), а старт детского чата с предметом вне проекта отказывает гейтом
        // («старый урок»). Пока владельца нет в детском режиме, старт валиден; поля урока дописываются
        // ниже прямой записью.
        var sessions = _hosts.Services.GetRequiredService<SessionManager>();
        var chat = await sessions.CreateChatAsync(id, ChildModeLaunch.RequiredMode, model: Model, providerKey: KidsKey);

        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Moscow).DateTime;
        var from = local.AddHours(-2).ToString("HH:mm", CultureInfo.InvariantCulture);
        var to = local.AddHours(2).ToString("HH:mm", CultureInfo.InvariantCulture);
        var put = await _admin.PutAsJsonAsync($"/api/users/{id}/child-profile", new
        {
            enabled = true, acknowledgeRouteRisk = true, providerKey = KidsKey, model = Model,
            spaces = new Dictionary<string, object> { ["lessons"] = new { windows = new[] { new { from, to } }, dailyMinutes = 40 } },
            dailyBudgetUsd = 2, monthlyBudgetUsd = 40,
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        ChildTestSubjects.Seed(_hosts.Services, id);
        var profile = _users.GetById(id)!.ChildMode!;
        profile.RulesAgreedAt = new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);
        profile.RulesSummary = "правила";

        // Проект-предмета и его руководитель — прямая запись (провижер Б8b/Б17a делает то же на
        // точках записи предмета): папка внутри песочницы хоста, маркер вместе с записью,
        // модель руководителя — единственный источник маршрута урока (решение 71).
        var root = Path.Combine(_hosts.SandboxRoot, "Предметы", Subject + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); // Create с createDirectory: false папку требует существующей
        var projects = _hosts.Services.GetRequiredService<ProjectManager>();
        var project = projects.Create("Русский язык", root, id, name, createDirectory: false, childSubjectId: Subject);
        var leader = _hosts.Services.GetRequiredService<PersonaManager>()
            .Create(id, "Репетитор " + Subject, null, null, null, Model, null,
                PersonaScope.Project, project.Id, null, null, memoryEnabled: false);
        projects.SetDefaultPersona(project.Id, leader.Id);

        if (withProjectKnowledge)
        {
            var wk = _knowledge.GetOrCreate(root);
            wk.DifyDatasetId = ProjectDatasetId;
            _knowledge.Save(wk);
        }

        // Чат — урок этого проекта-предмета: поля равны живой маршруте руководителя (то же, что
        // в продукте делает догон CatchUpChildChatAsync).
        chat = sessions.GetById(chat.Id)!;
        chat.ProjectId = project.Id;
        chat.ChildSpace = "lessons";
        chat.ChildSubject = Subject;
        chat.PersonaId = leader.Id;

        _hosts.Services.GetRequiredService<IChildModeGate>().LaunchMismatch(id, chat).Should().BeNull(
            "фикстура обязана строить живой урок (И1-бис проходит): иначе тесты проверяют ничего");
        return new Kid(id, chat.Id, project.Id, root, leader.Id);
    }

    private ChildProfile Profile(Kid k) => _users.GetById(k.Id)!.ChildMode!;
    private ChildSubject Subj(Kid k) => Profile(k).Subjects.Single(s => s.Id == Subject);
    private Session Chat(Kid k) => _hosts.Services.GetRequiredService<SessionManager>().GetOwned(k.SessionId, k.Id)!;

    private static McpToolCallContext Ctx(Kid k, string? owner = null, string? caller = null, string? tail = null) =>
        new(owner ?? k.Id, caller, tail ?? k.SessionId);

    private IReadOnlyList<string> Tools(Kid k, string? owner = null, string? caller = null, string? tail = null) =>
        _tutor.ToolsFor(Ctx(k, owner, caller, tail)).Select(t => t.Name).ToList();

    private Task<McpToolCallResult> Call(Kid k, string tool, object? args = null,
        string? owner = null, string? caller = null, string? tail = null) =>
        _tutor.CallAsync(tool,
            args is null ? new JsonObject() : JsonSerializer.SerializeToNode(args)!.AsObject(),
            Ctx(k, owner, caller, tail), CancellationToken.None);

    private static readonly string[] OneTool = ["search_materials"];

    private static string Record(string doc, string content, double score = 0.9) =>
        JsonSerializer.Serialize(new
        {
            segment = new { content, document = new { id = "doc-" + doc, name = doc } },
            score,
        });

    // ── состав ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Состав_ВалиднаяПара_РовноОдинИнструмент_БезDatasetIdИПутейВСхеме()
    {
        var k = await NewKidAsync();

        _tutor.ToolsFor(Ctx(k)).Select(t => t.Name).Should().BeEquivalentTo(OneTool);
        var allNames = _tutor.ToolsFor(Ctx(k))
            .SelectMany(t => t.InputSchema["properties"]!.AsObject().Select(p => p.Key)).ToList();
        allNames.Should().BeEquivalentTo(["query", "topK"],
            "ни dataset_id, ни путей, ни «всего файла» в схеме нет");
        foreach (var t in _tutor.ToolsFor(Ctx(k)))
            t.InputSchema["additionalProperties"]!.GetValue<bool>().Should().BeFalse(t.Name);
    }

    [Fact]
    public async Task Состав_НеДвигаетсяОтБазыПроекта()
    {
        var withBase = await NewKidAsync();
        var noBase = await NewKidAsync(withProjectKnowledge: false);
        var schemasBefore = _tutor.ToolsFor(Ctx(withBase)).Select(t => t.Name + t.Description + t.InputSchema.ToJsonString()).ToList();
        Tools(noBase).Should().BeEquivalentTo(OneTool, "база проекта меняет ответ, а не состав");
        Tools(withBase).Should().Equal(Tools(noBase));

        // появление базы у проекта состав и схемы не двигает: меняется только ответ инструмента
        _tutor.ToolsFor(Ctx(withBase)).Select(t => t.Name + t.Description + t.InputSchema.ToJsonString())
            .Should().Equal(schemasBefore, "tools/list не зависит от базы проекта");
        Tools(noBase).Should().BeEquivalentTo(OneTool);
    }

    // §3 шаг 1 — не ребёнок, DenyAll; шаг 2 — чужой, чат правил, без предмета; шаг 3 — И1-бис

    [Fact]
    public async Task Состав_НеРебёнок_Пусто()
    {
        var k = await NewKidAsync();
        Profile(k).Enabled = false;
        Tools(k).Should().BeEmpty("профиль выключен — владелец стал обычным пользователем");
        (await Call(k, "search_materials", new { query = "дроби" })).Should().Be(Denied());
    }

    [Fact]
    public async Task Состав_ПрофильDenyAll_Пусто()
    {
        var k = await NewKidAsync();
        Profile(k).Model = "";
        Tools(k).Should().BeEmpty();
        (await Call(k, "search_materials", new { query = "дроби" })).Should().Be(Denied());
    }

    [Fact]
    public async Task Состав_ЧужойВладелец_ЧужаяСессия_Пусто()
    {
        var a = await NewKidAsync();
        var b = await NewKidAsync();
        Tools(a, owner: b.Id).Should().BeEmpty("сессия A под владельцем B");
        Tools(a, owner: AdminId).Should().BeEmpty("админ не владелец детского чата");
        Tools(a, tail: "no-such-session").Should().BeEmpty();
        Tools(a, tail: "../../etc").Should().BeEmpty("хвост — один сегмент белого списка");
        Tools(a, tail: b.SessionId).Should().BeEmpty("хвост чужого чата под своим владельцем");
        (await Call(a, "search_materials", new { query = "дроби" }, owner: b.Id)).Should().Be(Denied());
    }

    [Fact]
    public async Task Состав_ЗаголовокЧатаНеРавенХвосту_Отказ()
    {
        var a = await NewKidAsync();
        var b = await NewKidAsync();
        Tools(a, caller: a.SessionId).Should().BeEquivalentTo(OneTool, "заголовок равен хвосту");
        Tools(a, caller: b.SessionId).Should().BeEmpty("X-Caller-Session-Id ≠ хвост");
        (await Call(a, "search_materials", new { query = "дроби" }, caller: b.SessionId)).Should().Be(Denied());
    }

    [Fact]
    public async Task Состав_ЧатПравил_Пусто()
    {
        var k = await NewKidAsync();
        var chat = Chat(k);
        // Чат правил живёт вне проекта (Б9a): чат правил сверяется с полями профиля, а не предмета —
        // поля подгоняем, чтобы сверка И1-бис прошла и отказывал именно шаг 2 (чат правил), а не шаг 3
        chat.OnboardingKind = OnboardingKinds.ChildRules;
        chat.ProjectId = null;
        chat.PersonaId = Profile(k).TutorPersonaId;
        chat.Provider = Profile(k).ProviderKey;
        chat.Model = Profile(k).Model;
        _hosts.Services.GetRequiredService<IChildModeGate>().LaunchMismatch(k.Id, chat).Should().BeNull();

        Tools(k).Should().BeEmpty();
        (await Call(k, "search_materials", new { query = "дроби" })).Should().Be(Denied());
    }

    [Fact]
    public async Task Состав_ЧатБезПространстваИлиБезПредмета_Пусто()
    {
        var k = await NewKidAsync();
        var chat = Chat(k);
        chat.ChildSubject = null;
        Tools(k).Should().BeEmpty("нет предмета");
        chat.ChildSubject = Subject;
        chat.ChildSpace = null;
        Tools(k).Should().BeEmpty("нет пространства");
    }

    [Fact]
    public async Task Состав_РасхождениеИ1бис_Пусто()
    {
        // Предмет выключен / удалён / сменили руководителя проекта — отказывает та же LaunchMismatch,
        // что на старте хода. Маршрут урока — модель руководителя проекта-предмета (решение 71),
        // поэтому «сменили репетитора» здесь = смена DefaultPersonaId проекта.
        var off = await NewKidAsync();
        Subj(off).Enabled = false;
        Tools(off).Should().BeEmpty("предмет выключен");
        (await Call(off, "search_materials", new { query = "дроби" })).Should().Be(Denied());

        var gone = await NewKidAsync();
        Profile(gone).Subjects.Clear();
        Tools(gone).Should().BeEmpty("предмет удалён");

        var swapped = await NewKidAsync();
        var other = _hosts.Services.GetRequiredService<PersonaManager>()
            .Create(swapped.Id, "Другой репетитор", null, null, null, Model, null,
                PersonaScope.Project, swapped.ProjectId, null, null, memoryEnabled: false);
        _hosts.Services.GetRequiredService<ProjectManager>().SetDefaultPersona(swapped.ProjectId, other.Id);
        Tools(swapped).Should().BeEmpty("руководитель проекта-предмета сменился");
        (await Call(swapped, "search_materials", new { query = "дроби" })).Should().Be(Denied());
    }

    // ── search_materials (Б13b: только база проекта-предмета этого чата) ──────────

    [Fact]
    public async Task Поиск_Успех_ФрагментыНеДлиннее1500_ТопКИзСхемы_ИДатасетТолькоИзПроектаЧата()
    {
        var k = await NewKidAsync();
        var big = new string('я', 4000);
        _hosts.DifyStub.RecordsJson = "[" + string.Join(",",
            Enumerable.Range(1, 8).Select(i => Record($"Учебник {i}", big))) + "]";
        _hosts.DifyStub.RetrievePaths.Clear();

        var def = await Call(k, "search_materials", new { query = "дроби", dataset_id = "d-global" });
        def.IsError.Should().BeFalse(def.Text);
        var items = JsonDocument.Parse(def.Text).RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(3, "topK по умолчанию = 3");
        foreach (var it in items.EnumerateArray())
        {
            it.GetProperty("text").GetString()!.Length.Should().BeLessThanOrEqualTo(1500);
            it.GetProperty("document").GetString().Should().StartWith("Учебник");
        }
        def.Text.Should().NotContain(ProjectDatasetId).And.NotContain("d-global", "id датасета ребёнку/модели не отдаётся");
        _hosts.DifyStub.RetrievePaths.Should().ContainSingle().Which.Should().Contain($"/datasets/{ProjectDatasetId}/retrieve",
            "датасет — только проекта-предмета чата; аргумент dataset_id тулсет не читает");

        var max = await Call(k, "search_materials", new { query = "дроби", topK = 99 });
        JsonDocument.Parse(max.Text).RootElement.GetProperty("items").GetArrayLength().Should().Be(5, "topK ≤ 5");
        var min = await Call(k, "search_materials", new { query = "дроби", topK = 0 });
        JsonDocument.Parse(min.Text).RootElement.GetProperty("items").GetArrayLength().Should().Be(1, "topK ≥ 1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Поиск_ПустойЗапрос_Отказ(string query)
    {
        var k = await NewKidAsync();
        (await Call(k, "search_materials", new { query })).IsError.Should().BeTrue();
        (await Call(k, "search_materials")).IsError.Should().BeTrue("query обязателен");
    }

    [Fact]
    public async Task Поиск_ЗапросДлиннее500_Отказ_Ровно500_Проходит()
    {
        var k = await NewKidAsync();
        (await Call(k, "search_materials", new { query = new string('а', 501) })).IsError.Should().BeTrue();
        (await Call(k, "search_materials", new { query = new string('а', 500) })).IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Поиск_УПроектаНетБазы_ЧестныйОтказ()
    {
        var k = await NewKidAsync(withProjectKnowledge: false);
        var search = await Call(k, "search_materials", new { query = "дроби" });
        search.IsError.Should().BeTrue();
        search.Text.Should().Contain("нет базы знаний");
        _hosts.DifyStub.RetrievePaths.Should().BeEmpty("без базы проекта к Dify не идём");
    }

    [Fact]
    public async Task Поиск_ЗнанияВыключеныИлиDifyУпал_НейтральныйОтказ_БезИсключения()
    {
        var k = await NewKidAsync();
        _hosts.DifyStub.RecordsJson = "[" + Record("Учебник", "текст") + "]";

        _hosts.Dify.ApiUrl = ""; // IsConfigured = false
        (await Call(k, "search_materials", new { query = "дроби" })).Should().Be(Denied());
        _hosts.Dify.ApiUrl = "http://dify.test/v1";

        _hosts.DifyStub.Throw = true;
        var res = await Call(k, "search_materials", new { query = "дроби" });
        res.Should().Be(Denied(), "Dify упал: текст исключения ребёнку и модели не отдаётся");
        res.Text.Should().NotContain("Dify не ответил");
    }

    [Fact]
    public async Task НеизвестныйИнструмент_ИсключениеКонтракта_ВключаяСнятыеСписокИЧтение()
    {
        var k = await NewKidAsync();
        var act = async () => await Call(k, "list_materials");
        await act.Should().ThrowAsync<ArgumentException>("как у соседних тулсетов: неизвестное имя — ошибка контракта");
    }

    // ── маршрут (И6 №9) ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Маршрут_ДетскийJwtНаMcpTutor_403()
    {
        var k = await NewKidAsync();
        var name = _users.GetById(k.Id)!.Username;
        using var child = _hosts.CreateAuthenticatedClient(name, ChildPassword);
        var resp = await child.PostAsJsonAsync($"/mcp/tutor/{k.SessionId}",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, "детский токен на /mcp/* закрывает middleware (маршрут без [ChildAllowed])");
    }

    [Fact]
    public async Task Маршрут_ЧужойТокенСОбычнымПользователем_ПустойСоставИОтказВызова()
    {
        var k = await NewKidAsync();
        // Админ-человек и второй обычный пользователь не владельцы детского чата: ни состава, ни вызова
        foreach (var client in new[]
        {
            _admin,
            _hosts.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword),
        })
        {
            var list = await client.PostAsJsonAsync($"/mcp/tutor/{k.SessionId}",
                new { jsonrpc = "2.0", id = 1, method = "tools/list" });
            list.StatusCode.Should().Be(HttpStatusCode.OK);
            JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement
                .GetProperty("result").GetProperty("tools").GetArrayLength().Should().Be(0);

            var call = await client.PostAsJsonAsync($"/mcp/tutor/{k.SessionId}", new
            {
                jsonrpc = "2.0", id = 2, method = "tools/call",
                @params = new { name = "search_materials", arguments = new { query = "дроби" } },
            });
            var result = JsonDocument.Parse(await call.Content.ReadAsStringAsync()).RootElement.GetProperty("result");
            result.GetProperty("isError").GetBoolean().Should().BeTrue();
        }
    }

    [Fact]
    public async Task Маршрут_БезХвоста_404_ТулсетВРеестре()
    {
        _hosts.Services.GetRequiredService<McpToolsetRegistry>().Find("tutor").Should().BeOfType<TutorToolset>();
        var resp = await _admin.PostAsJsonAsync("/mcp/tutor", new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "параметризованному тулсету хвост обязателен");
    }

    private static McpToolCallResult Denied() => new(TutorToolset.UnavailableText, IsError: true);
}
