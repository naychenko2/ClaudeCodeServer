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
/// Тулсет репетитора (К3, разрез «Знания репетитора» §3, И6): search_materials / list_materials /
/// read_material на МАРШРУТЕ /mcp/tutor/{sessionId}. Хост настоящий: ребёнок и его чат заводятся
/// боевыми ручками, KnowledgeService — настоящий с подменённым HTTP (список датасетов и поиск
/// диктует тест). Тулсет берётся из реестра DI, а не строится руками: проверяется то, что едет в бой.
///
/// Что фиксируют тесты:
/// — состав: ровно три инструмента на валидной паре «владелец, сессия», пусто на каждой невалидной
///   (шаги 1–4 §3: не ребёнок, DenyAll, чужой владелец, чат правил, без предмета, расхождение И1-бис);
/// — вызов повторяет тот же резолв: каждый отказ — isError с одним нейтральным текстом, наружу
///   ничего не течёт;
/// — привязка базы перепроверяется на КАЖДОМ поиске: админ-привязывший не админ / исчез, датасет
///   переименован в непривязываемый или чужой (окно «снимок против живого» ревью К1b), «Знания»
///   выключены;
/// — датасет берётся только из записи предмета: аргумент dataset_id схемой не принят и поиск его
///   игнорирует;
/// — файлы: id только из описи предмета, окно части ≤6000 символов, part вне 1..N — отказ, kind
///   отдаётся как есть (в том числе image, которого стор ещё не принимает).
/// </summary>
[Trait("Category", "Host")]
public class TutorToolsetTests : IClassFixture<TutorToolsetTests.Hosts>
{
    public sealed class Hosts : TestWebApplicationFactory
    {
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

    /// <summary>Подменённый Dify: список датасетов и ответ поиска — по слову теста.</summary>
    internal sealed class FakeDify : HttpMessageHandler
    {
        public bool Throw;
        public string DatasetsJson = "[]";
        public string RecordsJson = "[]";
        public readonly List<string> RetrievePaths = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Throw) throw new HttpRequestException("Dify не ответил");
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (request.Method == HttpMethod.Get && path.EndsWith("/datasets"))
                json = $"{{\"data\":{DatasetsJson},\"has_more\":false}}";
            else if (request.Method == HttpMethod.Post && path.EndsWith("/retrieve"))
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
    private const string DatasetId = "d-kb";
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    private readonly Hosts _hosts;
    private readonly HttpClient _admin;
    private readonly UserStore _users;
    private readonly TutorToolset _tutor;
    private readonly ChildSubjectMaterialsStore _materials;

    public TutorToolsetTests(Hosts hosts)
    {
        _hosts = hosts;
        _hosts.ExtraConfig[$"ClaudeSubscriptions:{KidsKey}:ApiKey"] = "sk-ant-test-kids";
        _hosts.ExtraConfig["Sandbox:ProjectsRoot"] = Path.Combine(Path.GetTempPath(), "ccs-tutor-" + Guid.NewGuid().ToString("N"));
        _admin = _hosts.CreateAuthenticatedClient();
        _users = _hosts.Services.GetRequiredService<UserStore>();
        _tutor = (TutorToolset)_hosts.Services.GetRequiredService<McpToolsetRegistry>().Find(TutorToolset.ServerName)!;
        _materials = _hosts.Services.GetRequiredService<ChildSubjectMaterialsStore>();
        // Список датасетов и привязка — общие умолчания; каждый тест правит своё и возвращает
        _hosts.DifyStub.Throw = false;
        _hosts.DifyStub.DatasetsJson = Datasets();
        _hosts.DifyStub.RecordsJson = "[]";
        _hosts.Dify.ApiUrl = "http://dify.test/v1";
    }

    private string AdminId => _users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
    private static string AdminName => TestWebApplicationFactory.TestUsername;

    private static string Datasets(params (string Id, string Name)[] extra) =>
        "[" + string.Join(",", new (string Id, string Name)[]
            {
                (DatasetId, AdminName + ":kb:математика"),
                ("d-notes", AdminName + ":notes"),
                ("d-global", "учебники"),
            }.Concat(extra).Select(d => $$"""{"id":"{{d.Id}}","name":"{{d.Name}}","document_count":1}""")) + "]";

    // Список, где датасет предмета носит заданное имя (его «переименовали»), плюс общая база
    private static string DatasetsWhere(string name) =>
        $$"""[{"id":"{{DatasetId}}","name":"{{name}}","document_count":1},{"id":"d-global","name":"учебники","document_count":1}]""";

    private sealed record Kid(string Id, string SessionId);

    // Ребёнок, предмет «russian» с привязанной базой d-kb (привязал админ), чат «Уроков»: путь боевой —
    // профиль админской ручкой, предмет и привязка в живом профиле, чат — ручкой ребёнка.
    private async Task<Kid> NewKidAsync(bool withKnowledge = true)
    {
        var name = "kid-" + Guid.NewGuid().ToString("N")[..10];
        var resp = await _admin.PostAsJsonAsync("/api/users", new
        {
            username = name, password = ChildPassword, role = "user",
            executionEnvironment = ExecutionEnvironments.Container,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Moscow).DateTime;
        var from = local.AddHours(-2).ToString("HH:mm", CultureInfo.InvariantCulture);
        var to = local.AddHours(2).ToString("HH:mm", CultureInfo.InvariantCulture);
        var put = await _admin.PutAsJsonAsync($"/api/users/{id}/child-profile", new
        {
            enabled = true, providerKey = KidsKey, model = "sonnet",
            spaces = new Dictionary<string, object> { ["lessons"] = new { windows = new[] { new { from, to } }, dailyMinutes = 40 } },
            dailyBudgetUsd = 2, monthlyBudgetUsd = 40,
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        ChildTestSubjects.Seed(_hosts.Services, id);
        var profile = _users.GetById(id)!.ChildMode!;
        profile.RulesAgreedAt = new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);
        profile.RulesSummary = "правила";
        if (withKnowledge)
            profile.Subjects.Single().Knowledge = new ChildSubjectKnowledge(DatasetId, AdminId, DateTime.UtcNow);

        using var child = _hosts.CreateAuthenticatedClient(name, ChildPassword);
        var created = await ChildTestSubjects.CreateChat(child);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var sid = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync())
            .GetProperty("sessionId").GetString()!;
        return new Kid(id, sid);
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

    private static readonly string[] ThreeTools = ["search_materials", "list_materials", "read_material"];

    private async Task<ChildSubjectMaterial> AddTextAsync(Kid k, string text, string name = "учебник.txt")
    {
        var res = await _materials.AddAsync(k.Id, Subject, name, System.Text.Encoding.UTF8.GetBytes(text), AdminId);
        res.Accepted.Should().BeTrue(res.Code);
        return res.Material!;
    }

    private static string Record(string doc, string content, double score = 0.9) =>
        JsonSerializer.Serialize(new
        {
            segment = new { content, document = new { id = "doc-" + doc, name = doc } },
            score,
        });

    // ── состав ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Состав_ВалиднаяПара_РовноТриИнструмента_БезDatasetIdИПутейВСхеме()
    {
        var k = await NewKidAsync();

        _tutor.ToolsFor(Ctx(k)).Select(t => t.Name).Should().BeEquivalentTo(ThreeTools);
        var allNames = _tutor.ToolsFor(Ctx(k))
            .SelectMany(t => t.InputSchema["properties"]!.AsObject().Select(p => p.Key)).ToList();
        allNames.Should().BeEquivalentTo(["query", "topK", "id", "part"],
            "ни dataset_id, ни путей, ни «всего файла» в схеме нет");
        foreach (var t in _tutor.ToolsFor(Ctx(k)))
            t.InputSchema["additionalProperties"]!.GetValue<bool>().Should().BeFalse(t.Name);
    }

    [Fact]
    public async Task Состав_НеЗависитОтПривязкиБазыИЕёОтсутствия()
    {
        var withBase = await NewKidAsync();
        var noBase = await NewKidAsync(withKnowledge: false);
        Tools(noBase).Should().BeEquivalentTo(ThreeTools, "привязка меняет ответ, а не состав");
        Tools(withBase).Should().Equal(Tools(noBase));
    }

    [Fact]
    public async Task Описания_ListИRead_НесутСтатичнуюСтрокуПроКартинки()
    {
        var k = await NewKidAsync();
        var tools = _tutor.ToolsFor(Ctx(k));
        const string note = "текст распознан с фото, нечитаемые места помечены [?]";
        tools.Single(t => t.Name == "list_materials").Description.Should().Contain("kind image").And.Contain(note);
        tools.Single(t => t.Name == "read_material").Description.Should().Contain("kind image").And.Contain(note);
        tools.Should().OnlyContain(t => t.Description.Length < 2048);
    }

    // §3 шаг 1 — не ребёнок, DenyAll; шаг 2 — чужой, чат правил, без предмета; шаг 3 — И1-бис

    [Fact]
    public async Task Состав_НеРебёнок_Пусто()
    {
        var k = await NewKidAsync();
        Profile(k).Enabled = false;
        Tools(k).Should().BeEmpty("профиль выключен — владелец стал обычным пользователем");
        (await Call(k, "list_materials")).Should().Be(Denied());
    }

    [Fact]
    public async Task Состав_ПрофильDenyAll_Пусто()
    {
        var k = await NewKidAsync();
        Profile(k).Model = "";
        Tools(k).Should().BeEmpty();
        (await Call(k, "list_materials")).Should().Be(Denied());
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
        (await Call(a, "read_material", new { id = "m_x", part = 1 }, owner: b.Id)).Should().Be(Denied());
    }

    [Fact]
    public async Task Состав_ЗаголовокЧатаНеРавенХвосту_Отказ()
    {
        var a = await NewKidAsync();
        var b = await NewKidAsync();
        Tools(a, caller: a.SessionId).Should().BeEquivalentTo(ThreeTools, "заголовок равен хвосту");
        Tools(a, caller: b.SessionId).Should().BeEmpty("X-Caller-Session-Id ≠ хвост");
        (await Call(a, "list_materials", caller: b.SessionId)).Should().Be(Denied());
    }

    [Fact]
    public async Task Состав_ЧатПравил_Пусто()
    {
        var k = await NewKidAsync();
        var chat = Chat(k);
        // Чат правил сверяется с полями профиля, а не предмета: поля подгоняем, чтобы сверка И1-бис
        // прошла и отказывал именно шаг 2 (чат правил), а не шаг 3
        chat.OnboardingKind = OnboardingKinds.ChildRules;
        chat.PersonaId = Profile(k).TutorPersonaId;
        chat.Provider = Profile(k).ProviderKey;
        chat.Model = Profile(k).Model;
        _hosts.Services.GetRequiredService<IChildModeGate>().LaunchMismatch(k.Id, chat).Should().BeNull();

        Tools(k).Should().BeEmpty();
        (await Call(k, "list_materials")).Should().Be(Denied());
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
        // Предмет выключен / удалён / сменили репетитора — отказывает та же LaunchMismatch, что на старте хода
        var off = await NewKidAsync();
        Subj(off).Enabled = false;
        Tools(off).Should().BeEmpty("предмет выключен");
        (await Call(off, "list_materials")).Should().Be(Denied());

        var gone = await NewKidAsync();
        Profile(gone).Subjects.Clear();
        Tools(gone).Should().BeEmpty("предмет удалён");

        var swapped = await NewKidAsync();
        Subj(swapped).TutorPersonaId = "другой-репетитор";
        Tools(swapped).Should().BeEmpty("репетитор предмета сменился");
        (await Call(swapped, "search_materials", new { query = "дроби" })).Should().Be(Denied());
    }

    // ── search_materials ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Поиск_Успех_ФрагментыНеДлиннее1500_ТопКИзСхемы_ИДатасетТолькоИзЗаписиПредмета()
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
        def.Text.Should().NotContain("d-kb").And.NotContain("d-global", "id датасета ребёнку/модели не отдаётся");
        _hosts.DifyStub.RetrievePaths.Should().ContainSingle().Which.Should().Contain("/datasets/d-kb/retrieve",
            "датасет — только из записи предмета; аргумент dataset_id тулсет не читает");

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
    public async Task Поиск_УПредметаНетБазы_ЧестныйОтказ_ФайлыПредметаРаботают()
    {
        var k = await NewKidAsync(withKnowledge: false);
        var m = await AddTextAsync(k, "Дроби бывают обыкновенные и десятичные.");

        var search = await Call(k, "search_materials", new { query = "дроби" });
        search.IsError.Should().BeTrue();
        search.Text.Should().Contain("нет базы знаний");
        (await Call(k, "list_materials")).IsError.Should().BeFalse();
        (await Call(k, "read_material", new { id = m.Id, part = 1 })).Text.Should().Contain("Дроби бывают");
    }

    [Fact]
    public async Task Поиск_ПривязавшийНеАдминИлиИсчез_Отказ()
    {
        var k = await NewKidAsync();
        _hosts.DifyStub.RecordsJson = "[" + Record("Учебник", "текст") + "]";
        (await Call(k, "search_materials", new { query = "дроби" })).IsError.Should().BeFalse("исходное состояние зелёное");

        // привязал обычный пользователь (не админ по стору)
        Subj(k).Knowledge = new ChildSubjectKnowledge(DatasetId, k.Id, DateTime.UtcNow);
        (await Call(k, "search_materials", new { query = "дроби" })).Should().Be(Denied());
        // пользователя с таким id нет (каскад удаления): новый пользователь с тем же именем права не наследует
        Subj(k).Knowledge = new ChildSubjectKnowledge(DatasetId, "user-no-such-id", DateTime.UtcNow);
        (await Call(k, "search_materials", new { query = "дроби" })).Should().Be(Denied());
        _hosts.DifyStub.RetrievePaths.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Поиск_ДатасетПереименованВНепривязываемый_ИЛиДругойАдмин_Отказ()
    {
        var k = await NewKidAsync();
        _hosts.DifyStub.RecordsJson = "[" + Record("Учебник", "текст") + "]";

        // админ переименовал базу в «базу заметок» — привязывать её к предмету нельзя
        _hosts.DifyStub.DatasetsJson = DatasetsWhere(AdminName + ":notes");
        (await Call(k, "search_materials", new { query = "дроби" })).Should().Be(Denied());
        _hosts.DifyStub.DatasetsJson = DatasetsWhere(AdminName + ":wiki");
        (await Call(k, "search_materials", new { query = "дроби" })).Should().Be(Denied());

        // датасета нет в свежем списке Dify вообще
        _hosts.DifyStub.DatasetsJson = """[{"id":"d-global","name":"учебники","document_count":1}]""";
        (await Call(k, "search_materials", new { query = "дроби" })).Should().Be(Denied());
    }

    [Fact]
    public async Task Поиск_ЧужаяБазаДругогоПользователя_ВОкноСнимокПротивЖивого_Отказ()
    {
        // Ревью К1b: валидатор пропускает «тот же id» по снимку, а стор решает по живому — между ними другой
        // админ мог сменить привязку на ЧУЖУЮ базу. Тулсет проверяет доступ на каждом чтении базы.
        var k = await NewKidAsync();
        var foreignOwner = await NewKidAsync(); // любой другой пользователь стора: предикат опирается на список имён
        var foreignName = _users.GetById(foreignOwner.Id)!.Username;
        _hosts.DifyStub.DatasetsJson = DatasetsWhere(foreignName + ":kb:чужая");
        _hosts.DifyStub.RecordsJson = "[" + Record("Чужой учебник", "секрет") + "]";
        _hosts.DifyStub.RetrievePaths.Clear();

        var res = await Call(k, "search_materials", new { query = "дроби" });
        res.Should().Be(Denied());
        res.Text.Should().NotContain("секрет");
        _hosts.DifyStub.RetrievePaths.Should().BeEmpty("до чужой базы запрос не доходит");
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

    // ── list_materials / read_material ────────────────────────────────────────────

    [Fact]
    public async Task Опись_ОтдаётKindКакЕсть_ВключаяImage_ИЧислоЧастей()
    {
        var k = await NewKidAsync();
        var txt = await AddTextAsync(k, new string('в', 12001));

        // image стор пока не принимает (КИ1b): строку и текст кладём в формате стора вручную.
        // read_material читает text/ любого вида, а list_materials отдаёт kind как есть.
        var dir = Path.Combine(ChildSubjectMaterialsStore.RootFromConfig(
            _hosts.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()), k.Id, Subject);
        Directory.CreateDirectory(Path.Combine(dir, "text"));
        File.WriteAllText(Path.Combine(dir, "text", "m_image0000001.txt"), "параграф с фото [?] конец");
        var manifest = (await _materials.ListAsync(k.Id, Subject)).ToList();
        manifest.Add(new ChildSubjectMaterial("m_image0000001", "стр 5", "image", 10, DateTimeOffset.UtcNow, AdminId,
            "m_image0000001.jpg", "m_image0000001.txt"));
        File.WriteAllText(Path.Combine(dir, "materials.json"), JsonSerializer.Serialize(manifest, ChildSubjectMaterialsStore.Json));

        var list = await Call(k, "list_materials");
        list.IsError.Should().BeFalse();
        var items = JsonDocument.Parse(list.Text).RootElement.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(2);
        items[0].GetProperty("id").GetString().Should().Be(txt.Id);
        items[0].GetProperty("kind").GetString().Should().Be("text");
        items[0].GetProperty("parts").GetInt32().Should().Be(3, "12001 символ = 6000 + 6000 + 1");
        items[1].GetProperty("kind").GetString().Should().Be("image", "kind отдаётся как есть");
        items[1].GetProperty("parts").GetInt32().Should().Be(1);
        list.Text.Should().NotContain("storedName").And.NotContain(".jpg").And.NotContain("uploadedBy");

        (await Call(k, "read_material", new { id = "m_image0000001", part = 1 })).Text.Should().Contain("параграф с фото [?]");
    }

    [Fact]
    public async Task Чтение_ЧастиНеДлиннее6000_СПометкойЧастьКИзN_ГраницыЧастей()
    {
        var k = await NewKidAsync();
        var m = await AddTextAsync(k, new string('а', 6000) + new string('б', 6000) + "в");

        var first = await Call(k, "read_material", new { id = m.Id, part = 1 });
        first.IsError.Should().BeFalse();
        first.Text.Should().StartWith("часть 1 из 3\n");
        first.Text["часть 1 из 3\n".Length..].Should().Be(new string('а', 6000), "ровно 6000 — одна часть");
        var second = await Call(k, "read_material", new { id = m.Id, part = 2 });
        second.Text.Should().Be("часть 2 из 3\n" + new string('б', 6000));
        var last = await Call(k, "read_material", new { id = m.Id, part = 3 });
        last.Text.Should().Be("часть 3 из 3\nв", "последняя часть — остаток");

        // граница ровно на 6000: одна часть, не две
        var exact = await AddTextAsync(k, new string('г', 6000), "ровно.txt");
        (await Call(k, "read_material", new { id = exact.Id, part = 1 })).Text.Should().StartWith("часть 1 из 1\n");
        (await Call(k, "read_material", new { id = exact.Id, part = 2 })).IsError.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(1000000)]
    public async Task Чтение_PartВнеДиапазона_Отказ(int part)
    {
        var k = await NewKidAsync();
        var m = await AddTextAsync(k, new string('а', 6001)); // 2 части
        (await Call(k, "read_material", new { id = m.Id, part })).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Чтение_БезPartИлиНеЧисло_Отказ()
    {
        var k = await NewKidAsync();
        var m = await AddTextAsync(k, "текст");
        (await Call(k, "read_material", new { id = m.Id })).IsError.Should().BeTrue();
        (await Call(k, "read_material", new { id = m.Id, part = "один" })).IsError.Should().BeTrue();
        (await Call(k, "read_material", new { id = m.Id, part = 1.5 })).IsError.Should().BeTrue();
        (await Call(k, "read_material", new { part = 1 })).IsError.Should().BeTrue("id обязателен");
    }

    [Fact]
    public async Task Чтение_ЧужойIdМатериала_ДругойПредметИДругойРебёнок_НеНайден()
    {
        var a = await NewKidAsync();
        var b = await NewKidAsync();
        var mine = await AddTextAsync(a, "мой текст");
        var foreign = await AddTextAsync(b, "ЧУЖОЙ СЕКРЕТ");

        (await Call(a, "read_material", new { id = mine.Id, part = 1 })).Text.Should().Contain("мой текст");
        var res = await Call(a, "read_material", new { id = foreign.Id, part = 1 });
        res.IsError.Should().BeTrue();
        res.Text.Should().NotContain("ЧУЖОЙ СЕКРЕТ");
        // «id» — не путь: подстановка пути/обхода — тот же «нет такого»
        foreach (var bad in new[] { "../../x", "text/" + mine.Id + ".txt", mine.Id + ".txt", "files/" + mine.Id })
            (await Call(a, "read_material", new { id = bad, part = 1 })).IsError.Should().BeTrue(bad);
    }

    [Fact]
    public async Task Чтение_ТекстВнешнийСимлинк_НеОтдаётся()
    {
        var k = await NewKidAsync();
        var m = await AddTextAsync(k, "настоящий");
        var dir = Path.Combine(ChildSubjectMaterialsStore.RootFromConfig(
            _hosts.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()), k.Id, Subject);
        var outside = Path.Combine(Path.GetTempPath(), "ccs-tutor-out-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "ВНЕШНИЙ ФАЙЛ");
        var textPath = Path.Combine(dir, "text", m.TextName);
        File.Delete(textPath);
        try { File.CreateSymbolicLink(textPath, outside); }
        catch (Exception) { return; } // нет прав на симлинки в этой среде — проверку делает стор К2a
        var res = await Call(k, "read_material", new { id = m.Id, part = 1 });
        res.IsError.Should().BeTrue();
        res.Text.Should().NotContain("ВНЕШНИЙ ФАЙЛ");
    }

    // ── устойчивость ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task НеизвестныйИнструмент_ИсключениеКонтракта_АРезолвПередНим_НеНужен()
    {
        var k = await NewKidAsync();
        var act = async () => await Call(k, "dify_search");
        await act.Should().ThrowAsync<ArgumentException>("как у соседних тулсетов: неизвестное имя — ошибка контракта");
    }

    [Fact]
    public async Task СбойДиска_НейтральныйТекст_ИсключениеНаружуНеВыходит()
    {
        var k = await NewKidAsync();
        var m = await AddTextAsync(k, "текст");
        var dir = Path.Combine(ChildSubjectMaterialsStore.RootFromConfig(
            _hosts.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()), k.Id, Subject);
        // Опись — не JSON: загрузчик стора отдаёт null/пусто, инструмент отвечает без падения
        File.WriteAllText(Path.Combine(dir, "materials.json"), "{не json");
        var list = await Call(k, "list_materials");
        list.Text.Should().NotContain("Exception");
        (await Call(k, "read_material", new { id = m.Id, part = 1 })).IsError.Should().BeTrue();
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
                @params = new { name = "list_materials", arguments = new { } },
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
