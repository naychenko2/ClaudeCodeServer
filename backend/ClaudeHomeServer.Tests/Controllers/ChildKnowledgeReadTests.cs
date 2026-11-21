using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Б20a («Знания» ребёнку, ADR-041 ред. 8, решение 81): ребёнку открыто только чтение базы
/// своего проекта-предмета — статус/список документов и поиск. Свой проект-предмет — 200;
/// чужой проект-предмета (ребёнок — его участник: граница держится детской проверкой,
/// а не невидимостью 404) — 403 not_child_subject; индексация и удаление документа —
/// 403 child_mode (эти ручки [ChildAllowed] не имеют, гейт И1 отсекает их до тела ручки).
/// Чтение взрослого участника того же проекта — без изменений (детская проверка — только
/// для детей).
///
/// Документ из «Для репетитора/» поиском ребёнка не находится: в индекс предмета он не входит
/// (Б13a, ProjectKnowledgeSyncService.IsExcludedFromSubjectIndex) — ключи ответов репетитора
/// в выдачу не попадают. Фикстура кладёт в индекс через живой синк-сервис: обычный файл
/// проходит, ключ отказывается — проверка ровно в результате поиска.
///
/// Dify — фейковый HttpMessageHandler (тот же образец, что KnowledgeControllerPrivate-
/// AttachmentsIndexTests): хранилище документов по датасету, поиск отдаёт только то, что
/// реально проиндексировано. Хост подменяет KnowledgeService через ExtraServices — весь
/// живой путь (контроллер → синк-сервис → KnowledgeService) остаётся боевым.
///
/// Мутации (каждая краснит ровно названное):
/// • снять детскую проверку проекта в KnowledgeController — ЧужойПроектПредмета краснеет
///   (200 вместо 403);
/// • снять исключение «Для репетитора/» в IsExcludedFromSubjectIndex — ДокументИзДля-
///   Репетитора краснеет (ключ попадает в индекс и в поиск);
/// • поставить валидацию пустого q в Search до детской проверки — ПустойЗапросНаЧужом-
///   Проекте краснеет (200 вместо 403: пустой запрос обходит отказ доступа);
/// • снять условие IsChildSubject в IsOwnSubjectProject — ОбычныйПроектРебёнка краснеет
///   (владение одно пропускает обычный проект: 200 вместо 403).
/// </summary>
[Trait("Category", "Host")]
public class ChildKnowledgeReadTests : IClassFixture<ChildKnowledgeReadTests.KnowledgeHost>
{
    private const string SubjectId = "matematika";
    private const string KidPassword = "b20a-kid-pw-123456";

    public sealed class KnowledgeHost : TestWebApplicationFactory
    {
        public readonly FakeDifyHandler Dify = new();

        public KnowledgeHost()
        {
            // Чужой проект видим ребёнку только при рубильнике и флаге (SharedProjectsGate,
            // тот же порог, что в ChildChatReadTests): 403 на чужом проекте-предмете должен
            // быть отказом детской проверки, а не отсутствием видимости (404).
            ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
            // Dify — фейковый, настоящий KnowledgeService поверх него (образец
            // KnowledgeControllerPrivateAttachmentsIndexTests). Регистрация после встроенных —
            // побеждает, и весь хост (синк-сервис, контроллер) работает с этим экземпляром.
            ExtraServices = services => services.AddSingleton(sp => new KnowledgeService(
                new FakeHttpFactory(Dify),
                Options.Create(new DifyOptions { ApiUrl = "http://dify.test/v1", ApiKey = "key" }),
                sp.GetRequiredService<WorkspaceKnowledgeStore>()));
        }
    }

    // Фейковый Dify: create_by_text кладёт документ в хранилище датасета; retrieve и список
    // документов отдают ровно это хранилище — «что не проиндексировано, то не находится».
    // public: на него ссылается открытое поле открытого KnowledgeHost (CS0052 иначе).
    public sealed class FakeDifyHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, List<(string Id, string Name, string Text)>> Docs = new();
        private int _seq;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);

            if (request.Method == HttpMethod.Post && path.EndsWith("/document/create_by_text"))
            {
                var root = JsonDocument.Parse(body).RootElement;
                var name = root.GetProperty("name").GetString()!;
                var text = root.GetProperty("text").GetString()!;
                var id = "doc-" + ++_seq;
                Store(DatasetOf(path)).Add((id, name, text));
                return Json($"{{\"document\":{{\"id\":\"{id}\",\"name\":{JsonSerializer.Serialize(name)},\"indexing_status\":\"completed\"}}}}");
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/retrieve"))
            {
                var q = JsonDocument.Parse(body).RootElement.GetProperty("query").GetString() ?? "";
                var records = Store(DatasetOf(path))
                    .Where(d => d.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .Select(d => "{\"segment\":{\"content\":" + JsonSerializer.Serialize(d.Text) +
                        ",\"document\":{\"id\":" + JsonSerializer.Serialize(d.Id) +
                        ",\"name\":" + JsonSerializer.Serialize(d.Name) + ",\"doc_metadata\":null}},\"score\":0.9}");
                return Json($"{{\"records\":[{string.Join(",", records)}]}}");
            }
            if (request.Method == HttpMethod.Get && path.Contains("/documents"))
            {
                var data = Store(DatasetOf(path))
                    .Select(d => "{\"id\":" + JsonSerializer.Serialize(d.Id) + ",\"name\":" + JsonSerializer.Serialize(d.Name) +
                        ",\"indexing_status\":\"completed\",\"error\":null,\"word_count\":1}");
                return Json($"{{\"data\":[{string.Join(",", data)}],\"has_more\":false,\"total\":{data.Count()}}}");
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/datasets"))
                return Json($"{{\"id\":\"ds-{++_seq}\"}}");
            if (request.Method == HttpMethod.Get && path.EndsWith("/datasets"))
                return Json("{\"data\":[],\"has_more\":false,\"total\":0}");
            if (request.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            return Json("{}");
        }

        private List<(string Id, string Name, string Text)> Store(string datasetId) =>
            Docs.TryGetValue(datasetId, out var list) ? list : Docs[datasetId] = [];

        // id датасета из пути /v1/datasets/{id}/…: каждый id экранирован Seg() — возвращаем обратно
        private static string DatasetOf(string path)
        {
            var m = System.Text.RegularExpressions.Regex.Match(path, "/datasets/([^/]+)");
            return Uri.UnescapeDataString(m.Groups[1].Value);
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class FakeHttpFactory(FakeDifyHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private readonly KnowledgeHost _host;

    public ChildKnowledgeReadTests(KnowledgeHost host) => _host = host;

    private ProjectManager Projects => _host.Services.GetRequiredService<ProjectManager>();
    private UserStore Users => _host.Services.GetRequiredService<UserStore>();
    private ProjectKnowledgeSyncService Sync => _host.Services.GetRequiredService<ProjectKnowledgeSyncService>();

    // ── фикстуры ────────────────────────────────────────────────────────────────────

    // Ребёнок с валидным профилем (тот же снимок, что SetChild в ChildModeEndpointGuardTests):
    // живая IChildModePolicy читает его по стору — токен проходит гейт И1 и детская сверка
    // в контроллере видит ребёнка.
    private (string Id, string Username) Kid(string tag)
    {
        var user = Users.Add($"b20a-kid-{tag}", KidPassword, "user");
        user.ChildMode = new ChildProfile
        {
            Enabled = true,
            TutorPersonaId = "tutor",
            ProviderKey = "key",
            Model = "sonnet",
        };
        return (user.Id, user.Username);
    }

    private HttpClient Client(string username, string password) =>
        _host.CreateAuthenticatedClient(username, password);

    // Проект-предмета (маркер ChildSubjectId, форма Б8b) + файлы предмета + индекс через живой
    // синк-сервис (тот же путь, что у панели БЗ): обычный файл входит в индекс,
    // «Для репетитора/» отказывается (Б13a) — в тесте это не утверждается, проверка
    // в результате поиска (мутация «снять исключение» красит его).
    private async Task<Project> SubjectProjectAsync(string childId, string username, string tag)
    {
        var dir = Path.Combine(_host.TempDir, "b20a_" + tag);
        var project = Projects.Create($"Предмет-{tag}", dir, childId, username,
            createDirectory: true, childSubjectId: SubjectId);
        File.WriteAllText(Path.Combine(dir, "урок.md"), "фотосинтез — процесс питания растений");
        Directory.CreateDirectory(Path.Combine(dir, "Для репетитора"));
        File.WriteAllText(Path.Combine(dir, "Для репетитора", "ключи.md"), "хлорофилл — ключ ответа на вопрос урока");

        await Sync.IndexPathAsync(project, username, "урок.md");
        try { await Sync.IndexPathAsync(project, username, "Для репетитора/ключи.md"); }
        catch (InvalidOperationException) { /* вне индекса предмета (Б13a) */ }
        return project;
    }

    private static async Task<JsonDocument> BodyAsync(HttpResponseMessage resp) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

    // ── свой проект-предмет: 200 на обеих ручках ───────────────────────────────────

    [Fact]
    public async Task РебёнокСписокДокументовСвоегоПроекта_200_БезКлючейРепетитора()
    {
        var (kidId, kidName) = Kid("list");
        var project = await SubjectProjectAsync(kidId, kidName, "list");
        using var kid = Client(kidName, KidPassword);

        var resp = await kid.GetAsync($"/api/projects/{project.Id}/knowledge");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "статус и список базы своего проекта-предмета — открыты ребёнку");
        var body = await BodyAsync(resp);
        var names = body.RootElement.GetProperty("documents").EnumerateArray()
            .Select(d => d.GetProperty("name").GetString()).ToList();
        names.Should().Contain("урок.md", "список своей базы ребёнку показывается");
        names.Should().NotContain("Для репетитора/ключи.md", "ключи репетитора не индексировались — их нет и в списке");
    }

    [Fact]
    public async Task РебёнокПоискСвоегоПроекта_НаходитОбычныйФайл()
    {
        var (kidId, kidName) = Kid("search");
        var project = await SubjectProjectAsync(kidId, kidName, "search");
        using var kid = Client(kidName, KidPassword);

        var resp = await kid.GetAsync($"/api/projects/{project.Id}/knowledge/search?q={Uri.EscapeDataString("фотосинтез")}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "поиск своей базы — открыт ребёнку");
        var items = (await BodyAsync(resp)).RootElement.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(1, "поиск работает: обычный файл предмета находится");
        items[0].GetProperty("documentName").GetString().Should().Be("урок.md");
    }

    // ── c2: ключ репетитора в выдачу ребёнка не попадает (Б13a) ─────────────────────

    [Fact]
    public async Task ДокументИзДляРепетитора_ПоискомРебёнка_НеНаходится()
    {
        var (kidId, kidName) = Kid("tutorkeys");
        var project = await SubjectProjectAsync(kidId, kidName, "tutorkeys");
        using var kid = Client(kidName, KidPassword);

        // Термин лежит только в «Для репетитора/ключи.md»: он не вошёл в индекс предмета —
        // поиск возвращает ноль находок. Мутация «снять исключение в IsExcludedFromSubjectIndex»
        // (ProjectKnowledgeSyncService.cs:476) краснит этот тест: ключ проиндексировался бы
        // и нашёлся.
        var resp = await kid.GetAsync($"/api/projects/{project.Id}/knowledge/search?q={Uri.EscapeDataString("хлорофилл")}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(resp)).RootElement.GetProperty("items").EnumerateArray()
            .Should().BeEmpty("ключи ответов репетитора не индексируются в базу предмета (решение 66/Б13a)");
    }

    // ── чужой проект-предмета: 403 на обеих ручках ─────────────────────────────────

    [Fact]
    public async Task ЧужойПроектПредмета_Ребёнку_403_НаОбеихРучках()
    {
        var (kidId, kidName) = Kid("foreign");
        var (otherId, otherName) = Kid("other");
        var foreign = await SubjectProjectAsync(otherId, otherName, "foreign");
        // Видимость чужого проекта есть (участник Reader + рубильник и флаг) — значит 403
        // даёт именно детская проверка «проект — проект-предмета этого ребёнка», а не ProjectScope.
        Projects.SetMembers(foreign.Id,
            [new ProjectMember { UserId = kidId, Role = ProjectRole.Reader, AddedBy = otherId }]);
        Users.SetFeatureFlag(kidId, FeatureFlagKeys.SharedProjects, true);
        using var kid = Client(kidName, KidPassword);

        var list = await kid.GetAsync($"/api/projects/{foreign.Id}/knowledge");
        list.StatusCode.Should().Be(HttpStatusCode.Forbidden, "чужой проект-предмета — не свой");
        (await BodyAsync(list)).RootElement.GetProperty("code").GetString().Should().Be("not_child_subject");

        var search = await kid.GetAsync($"/api/projects/{foreign.Id}/knowledge/search?q={Uri.EscapeDataString("фотосинтез")}");
        search.StatusCode.Should().Be(HttpStatusCode.Forbidden, "и поиск чужой базы — отказан");
        (await BodyAsync(search)).RootElement.GetProperty("code").GetString().Should().Be("not_child_subject");
    }

    // ── пустой q на чужом проекте: доступ раньше валидации запроса ──────────────────

    [Fact]
    public async Task ПустойЗапросНаЧужомПроекте_Ребёнку_403_ДоступРаньшеВалидации()
    {
        var (kidId, kidName) = Kid("emptyq");
        var (otherId, otherName) = Kid("emptyqother");
        var foreign = await SubjectProjectAsync(otherId, otherName, "emptyq");
        // Видимость как в ЧужойПроектПредмета: 403 — отказ именно детской проверки, не ProjectScope.
        Projects.SetMembers(foreign.Id,
            [new ProjectMember { UserId = kidId, Role = ProjectRole.Reader, AddedBy = otherId }]);
        Users.SetFeatureFlag(kidId, FeatureFlagKeys.SharedProjects, true);
        using var kid = Client(kidName, KidPassword);

        // Пустой q на взрослом проекте — 200 с пустой выдачей (KnowledgeController.cs:121-122).
        // Ребёнку отказ обязан прийти до этой ветки: детская проверка в Search стоит на
        // KnowledgeController.cs:119, раньше валидации пустого q (:121). Мутация «поставить
        // валидацию q до проверки доступа» (перенести блок :121-122 перед :119) краснит
        // этот тест: пустой запрос обходит отказ и даёт 200 вместо 403.
        var resp = await kid.GetAsync($"/api/projects/{foreign.Id}/knowledge/search?q=");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, "проверка доступа идёт раньше валидации запроса: пустой запрос не открывает чужую базу");
        (await BodyAsync(resp)).RootElement.GetProperty("code").GetString().Should().Be("not_child_subject");
    }

    // ── обычный проект ребёнка без маркера предмета: 403 на обеих ручках ────────────

    [Fact]
    public async Task ОбычныйПроектРебёнка_БезМаркераПредмета_403()
    {
        var (kidId, kidName) = Kid("plain");
        // Свой обычный проект ребёнка: без childSubjectId — ребёнку открыта база только
        // проекта-предмета (решение 81), владение одно не открывает обычную базу.
        var dir = Path.Combine(_host.TempDir, "b20a_plain");
        var project = Projects.Create("Обычный-проект-plain", dir, kidId, kidName, createDirectory: true);
        File.WriteAllText(Path.Combine(dir, "заметка.md"), "фотосинтез — процесс питания растений");
        using var kid = Client(kidName, KidPassword);

        // Мутация «снять условие IsChildSubject в IsOwnSubjectProject» (KnowledgeController.cs:64)
        // краснит этот тест: одного владения хватает, и обычный проект проходит (200 вместо 403).
        var list = await kid.GetAsync($"/api/projects/{project.Id}/knowledge");
        list.StatusCode.Should().Be(HttpStatusCode.Forbidden, "база открыта только проекту-предмету, не обычному проекту ребёнка");
        (await BodyAsync(list)).RootElement.GetProperty("code").GetString().Should().Be("not_child_subject");

        var search = await kid.GetAsync($"/api/projects/{project.Id}/knowledge/search?q={Uri.EscapeDataString("фотосинтез")}");
        search.StatusCode.Should().Be(HttpStatusCode.Forbidden, "и поиск — только своей базы-предмета");
        (await BodyAsync(search)).RootElement.GetProperty("code").GetString().Should().Be("not_child_subject");
    }

    // ── прочие ручки класса: гейт И1 до тела ручки ─────────────────────────────────

    [Fact]
    public async Task ИндексацияИУдалениеДокумента_Ребёнку_403_ChildMode()
    {
        var (kidId, kidName) = Kid("write");
        var project = await SubjectProjectAsync(kidId, kidName, "write");
        using var kid = Client(kidName, KidPassword);

        // Ни индексация, ни удаление, ни теги: [ChildAllowed] на них нет намеренно (решение 81) —
        // ребёнок базу не наполняет, её наполняет синк папки.
        using var index = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project.Id}/knowledge/index");
        index.Content = new StringContent("{\"relativePath\":\"новый.md\"}", Encoding.UTF8, "application/json");
        var indexResp = await kid.SendAsync(index);
        indexResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await indexResp.Content.ReadAsStringAsync()).Should().Contain("child_mode");

        var delResp = await kid.DeleteAsync($"/api/projects/{project.Id}/knowledge/documents/doc-1");
        delResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await delResp.Content.ReadAsStringAsync()).Should().Contain("child_mode");
    }

    // ── взрослый участник того же проекта: чтение без изменений ─────────────────────

    [Fact]
    public async Task ВзрослыйУчастник_ЧитаетБазуПроектаПредмета_КакРаньше()
    {
        var (kidId, kidName) = Kid("adult");
        var project = await SubjectProjectAsync(kidId, kidName, "adult");
        // Куратор-Editor (тот же состав участника, что записывает синхронизация кураторов)
        var curator = Users.Add("b20a-cur-adult", "b20a-cur-pw-123456", "user");
        Projects.SetMembers(project.Id,
            [new ProjectMember { UserId = curator.Id, Role = ProjectRole.Editor, AddedBy = kidId }]);
        Users.SetFeatureFlag(curator.Id, FeatureFlagKeys.SharedProjects, true);
        using var adult = Client(curator.Username, "b20a-cur-pw-123456");

        var resp = await adult.GetAsync($"/api/projects/{project.Id}/knowledge");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "детская проверка — только для детей, взрослый путь не тронут");
        var names = (await BodyAsync(resp)).RootElement.GetProperty("documents").EnumerateArray()
            .Select(d => d.GetProperty("name").GetString()).ToList();
        names.Should().Contain("урок.md");
    }
}
