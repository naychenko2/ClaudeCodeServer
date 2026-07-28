using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.CodeGraph.Core;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// REST-доступ к графу кода: 200 на построенном графе, 404 если граф не строился,
/// 403 на чужом проекте, isStale отражает изменения .cs-файлов.
/// Граф кладётся напрямую в стор (построение Roslyn покрыто CSharpGraphRoslynAcceptanceTests) —
/// здесь проверяется именно REST-контракт: контроллер → GetSnapshotAsync → DTO → isStale.
/// </summary>
public class CodeGraphControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _tempProjectDir;

    public CodeGraphControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _tempProjectDir = Path.Combine(factory.TempDir, "codegraph_projects");
        Directory.CreateDirectory(_tempProjectDir);
        // Регистрируем лёгкий фейк-провайдер для .cs — POST-build реально строит граф
        // (без тяжёлого Roslyn; он покрыт отдельно в Acceptance-тестах). Синглтон общий —
        // достаточно зарегистрировать один раз на класс.
        _factory.Services.GetRequiredService<CodeGraphService>()
            .RegisterProvider(".cs", new FakeCsGraphProvider());
    }

    private string MkProjectDir(string name)
    {
        var dir = Path.Combine(_tempProjectDir, name + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task<JsonElement> CreateProjectAsync(string name, string dir)
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new { name, rootPath = dir });
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private static void WriteCs(string dir)
    {
        // Нужны .cs-файлы, чтобы isStale было что сравнивать по mtime.
        File.WriteAllText(Path.Combine(dir, "Foo.cs"), "namespace Demo { public class Foo {} }");
        File.WriteAllText(Path.Combine(dir, "Bar.cs"), "namespace Demo { public class Bar {} }");
    }

    // Синтетический граф: класс реализует интерфейс и ссылается на другой тип.
    private static CodeGraph SampleGraph() => new()
    {
        Nodes = new()
        {
            ["Demo.IFoo"] = new CodeGraphNode
            {
                Id = "Demo.IFoo",
                Label = "IFoo",
                FullyQualifiedName = "Demo.IFoo",
                SourceFile = "IFoo.cs",
                SourceLocation = "L1",
                Kind = NodeKind.Interface
            },
            ["Demo.Foo"] = new CodeGraphNode
            {
                Id = "Demo.Foo",
                Label = "Foo",
                FullyQualifiedName = "Demo.Foo",
                SourceFile = "Foo.cs",
                SourceLocation = "L1",
                Kind = NodeKind.Class
            },
            ["Demo.Bar"] = new CodeGraphNode
            {
                Id = "Demo.Bar",
                Label = "Bar",
                FullyQualifiedName = "Demo.Bar",
                SourceFile = "Bar.cs",
                SourceLocation = "L1",
                Kind = NodeKind.Class
            },
        },
        Edges = new()
        {
            new() { Source = "Demo.Foo", Target = "Demo.IFoo", Relation = EdgeRelation.Implements, Confidence = EdgeConfidence.Extracted },
            new() { Source = "Demo.Foo", Target = "Demo.Bar", Relation = EdgeRelation.References, Confidence = EdgeConfidence.Extracted },
        },
    };

    private async Task SaveGraphAsync(string rootPath, CodeGraph graph)
    {
        var persistence = _factory.Services.GetRequiredService<GraphPersistence>();
        await persistence.SaveAsync(rootPath, graph, CancellationToken.None);
    }

    [Fact]
    public async Task Get_ПостроенныйГраф_Возвращает200()
    {
        var dir = MkProjectDir("withgraph");
        WriteCs(dir);
        var project = await CreateProjectAsync("graphproj", dir);
        var id = project.GetProperty("id").GetString()!;
        await SaveGraphAsync(dir, SampleGraph());

        var resp = await _client.GetAsync($"/api/projects/{id}/code-graph");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("nodes").GetArrayLength().Should().Be(3);
        body.GetProperty("edges").GetArrayLength().Should().Be(2);
        body.GetProperty("metadata").GetProperty("builtAt").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("metadata").GetProperty("isStale").GetBoolean().Should().BeFalse(
            "исходники не менялись после построения");
        body.GetProperty("metadata").GetProperty("nodeCount").GetInt32().Should().Be(3);
        body.GetProperty("metadata").GetProperty("edgeCount").GetInt32().Should().Be(2);
        body.GetProperty("metadata").GetProperty("fileCount").GetInt32().Should().BeGreaterOrEqualTo(2);

        // Контракт v1: kind/relation/confidence — строки (без утечки enum'ов).
        var firstNode = body.GetProperty("nodes").EnumerateArray().First();
        firstNode.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        firstNode.GetProperty("label").GetString().Should().NotBeNullOrEmpty();
        firstNode.GetProperty("kind").GetString().Should().BeOneOf("Class", "Interface", "Struct", "Enum");
        foreach (var edge in body.GetProperty("edges").EnumerateArray())
        {
            edge.GetProperty("relation").GetString().Should().BeOneOf("Calls", "Implements", "References");
            edge.GetProperty("confidence").GetString().Should().BeOneOf("Extracted", "Inferred");
        }
    }

    [Fact]
    public async Task Get_ГрафНеПостроен_Возвращает404ИЗапускаетФоновуюПостройку()
    {
        var dir = MkProjectDir("nograph");
        WriteCs(dir);
        var project = await CreateProjectAsync("nograph", dir);
        var id = project.GetProperty("id").GetString()!;

        // Граф не строили — эндпоинт отдаёт 404, но запускает фоновый initial-build
        // (HOTFIX прода) и маркирует ответ заголовком/флагом building — UI перезапросит.
        var resp = await _client.GetAsync($"/api/projects/{id}/code-graph");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        resp.Headers.Contains("X-CodeGraph-Building").Should().BeTrue();
        resp.Headers.GetValues("X-CodeGraph-Building").First().Should().Be("true");
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("message").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("building").GetBoolean().Should().BeTrue(
            "GET при отсутствии графа запускает фоновую постройку");

        // Фоновый rebuild действительно отработал — стор появился (graph.json создан).
        var persistence = _factory.Services.GetRequiredService<GraphPersistence>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        bool appeared;
        do
        {
            appeared = await persistence.LoadSnapshotAsync(dir, CancellationToken.None) is not null;
            if (appeared) break;
            await Task.Delay(150);
        } while (DateTime.UtcNow < deadline);
        appeared.Should().BeTrue("build-on-first-GET: фоновый rebuild сохраняет граф в стор");

        // Повторный GET после постройки — граф доступен (200), повторный rebuild не нужен.
        var after = await _client.GetAsync($"/api/projects/{id}/code-graph");
        after.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Get_НесуществующийПроект_Возвращает404()
    {
        var resp = await _client.GetAsync("/api/projects/no-such-project/code-graph");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_ЧужойПроект_Возвращает403()
    {
        var dir = MkProjectDir("owned");
        WriteCs(dir);
        var project = await CreateProjectAsync("owned", dir);
        var id = project.GetProperty("id").GetString()!;
        await SaveGraphAsync(dir, SampleGraph());

        // Второй пользователь не владеет проектом — доступ запрещён.
        var other = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var resp = await other.GetAsync($"/api/projects/{id}/code-graph");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_ИсходникиИзменилисьПослеПостроения_ОтмечаетStale()
    {
        var dir = MkProjectDir("stale");
        WriteCs(dir);
        var project = await CreateProjectAsync("stale", dir);
        var id = project.GetProperty("id").GetString()!;
        await SaveGraphAsync(dir, SampleGraph());

        // Сдвигаем mtime .cs в будущее — детерминированно позже BuiltAt, без sleep.
        File.SetLastWriteTimeUtc(Path.Combine(dir, "Bar.cs"), DateTime.UtcNow.AddSeconds(30));

        var resp = await _client.GetAsync($"/api/projects/{id}/code-graph");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("metadata").GetProperty("isStale").GetBoolean().Should().BeTrue(
            "mtime .cs больше BuiltAt — граф устарел");
    }

    [Fact]
    public async Task Build_СтроитГраф_Возвращает202ИГрафПоявляется()
    {
        var dir = MkProjectDir("build");
        WriteCs(dir);
        var project = await CreateProjectAsync("buildproj", dir);
        var id = project.GetProperty("id").GetString()!;

        // До построения графа нет — 404 (панель покажет empty-state).
        var before = await _client.GetAsync($"/api/projects/{id}/code-graph");
        before.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Явный триггер построения — 202 Accepted.
        var resp = await _client.PostAsync($"/api/projects/{id}/code-graph/build", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // После построения граф доступен — 200 с узлами, извлечёнными из .cs.
        var after = await _client.GetAsync($"/api/projects/{id}/code-graph");
        after.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await after.Content.ReadAsStringAsync());
        body.GetProperty("nodes").GetArrayLength().Should().BeGreaterThan(0,
            "POST-build построил граф из .cs через фейк-провайдер");
    }

    [Fact]
    public async Task Build_НесуществующийПроект_Возвращает404()
    {
        var resp = await _client.PostAsync("/api/projects/no-such-project/code-graph/build", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Build_ЧужойПроект_Возвращает403()
    {
        var dir = MkProjectDir("owned-build");
        WriteCs(dir);
        var project = await CreateProjectAsync("owned-build", dir);
        var id = project.GetProperty("id").GetString()!;

        // Второй пользователь не владеет проектом — построение запрещено.
        var other = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var resp = await other.PostAsync($"/api/projects/{id}/code-graph/build", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ===== Тонкие запросы для MCP-сервера codegraph (find / neighbors / hubs) =====

    // Проект с сохранённым SampleGraph — общая подготовка тонких запросов.
    private async Task<string> ProjectWithGraphAsync(string name)
    {
        var dir = MkProjectDir(name);
        WriteCs(dir);
        var project = await CreateProjectAsync(name, dir);
        await SaveGraphAsync(dir, SampleGraph());
        return project.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Find_ПоИмениТипа_ВозвращаетУзелСМестомИСтепенью()
    {
        var id = await ProjectWithGraphAsync("find");

        var resp = await _client.GetAsync($"/api/projects/{id}/code-graph/find?q=Foo");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        // «Foo» — подстрока и в Demo.IFoo, но точное совпадение имени ранжируется выше
        body.GetProperty("total").GetInt32().Should().Be(2);
        var node = body.GetProperty("results").EnumerateArray().First();
        node.GetProperty("fqn").GetString().Should().Be("Demo.Foo");
        node.GetProperty("kind").GetString().Should().Be("Class");
        node.GetProperty("location").GetString().Should().Be("Foo.cs:1", "«L1» → строка 1");
        node.GetProperty("degree").GetInt32().Should().Be(2, "Foo реализует IFoo и ссылается на Bar");
    }

    [Fact]
    public async Task Find_Лимит_ОбрезаетВыдачуНоTotalПолный()
    {
        var id = await ProjectWithGraphAsync("find-limit");

        // «Demo» входит в FQN всех трёх узлов — лимит режет выдачу, total остаётся честным.
        var resp = await _client.GetAsync($"/api/projects/{id}/code-graph/find?q=Demo&limit=1");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("total").GetInt32().Should().Be(3);
        body.GetProperty("results").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Neighbors_ВходящиеИИсходящие_СТипомСвязи()
    {
        var id = await ProjectWithGraphAsync("neighbors");

        // Узел задан коротким именем — резолвится по Label.
        var resp = await _client.GetAsync($"/api/projects/{id}/code-graph/neighbors?node=Foo");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("node").GetProperty("fqn").GetString().Should().Be("Demo.Foo");
        body.GetProperty("totalOut").GetInt32().Should().Be(2);
        body.GetProperty("totalIn").GetInt32().Should().Be(0);
        body.GetProperty("byRelation").GetProperty("Implements").GetInt32().Should().Be(1);
        body.GetProperty("byRelation").GetProperty("References").GetInt32().Should().Be(1);

        var neighbors = body.GetProperty("neighbors").EnumerateArray().ToList();
        neighbors.Should().HaveCount(2);
        neighbors.Should().OnlyContain(n => n.GetProperty("direction").GetString() == "out");
        neighbors.Select(n => n.GetProperty("fqn").GetString())
            .Should().BeEquivalentTo(["Demo.IFoo", "Demo.Bar"]);
    }

    [Fact]
    public async Task Neighbors_ФильтрПоНаправлениюИСвязи_СужаетВыдачу()
    {
        var id = await ProjectWithGraphAsync("neighbors-filter");

        // Кто реализует IFoo — входящие связи с relation=Implements.
        var resp = await _client.GetAsync(
            $"/api/projects/{id}/code-graph/neighbors?node=Demo.IFoo&direction=in&relation=Implements");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("total").GetInt32().Should().Be(1);
        var neighbor = body.GetProperty("neighbors").EnumerateArray().Single();
        neighbor.GetProperty("fqn").GetString().Should().Be("Demo.Foo");
        neighbor.GetProperty("direction").GetString().Should().Be("in");
        neighbor.GetProperty("relation").GetString().Should().Be("Implements");
        neighbor.GetProperty("confidence").GetString().Should().Be("Extracted");
    }

    [Fact]
    public async Task Neighbors_НеизвестныйУзел_Возвращает404()
    {
        var id = await ProjectWithGraphAsync("neighbors-miss");

        var resp = await _client.GetAsync($"/api/projects/{id}/code-graph/neighbors?node=НетТакого");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("message").GetString().Should().Contain("codegraph_find",
            "модели нужно сказать, чем уточнить имя");
    }

    [Fact]
    public async Task Hubs_ВозвращаетТопПоСвязностиИРазмерГрафа()
    {
        var id = await ProjectWithGraphAsync("hubs");

        var resp = await _client.GetAsync($"/api/projects/{id}/code-graph/hubs?limit=2");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("nodeCount").GetInt32().Should().Be(3);
        body.GetProperty("edgeCount").GetInt32().Should().Be(2);
        var hubs = body.GetProperty("hubs").EnumerateArray().ToList();
        hubs.Should().HaveCount(2);
        hubs[0].GetProperty("fqn").GetString().Should().Be("Demo.Foo", "у Foo degree 2 — больше всех");
        hubs[0].GetProperty("degree").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ТонкиеЗапросы_ГрафНеПостроен_Возвращают404()
    {
        var dir = MkProjectDir("thin-nograph");
        WriteCs(dir);
        var project = await CreateProjectAsync("thin-nograph", dir);
        var id = project.GetProperty("id").GetString()!;

        var find = await _client.GetAsync($"/api/projects/{id}/code-graph/find?q=Foo");
        find.StatusCode.Should().Be(HttpStatusCode.NotFound);
        find.Headers.Contains("X-CodeGraph-Building").Should().BeTrue(
            "как и GET снимка, тонкий запрос запускает фоновую постройку");
    }

    [Fact]
    public async Task ТонкиеЗапросы_ЧужойПроект_Возвращают403()
    {
        var id = await ProjectWithGraphAsync("thin-owned");
        var other = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        (await other.GetAsync($"/api/projects/{id}/code-graph/find?q=Foo"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await other.GetAsync($"/api/projects/{id}/code-graph/neighbors?node=Foo"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await other.GetAsync($"/api/projects/{id}/code-graph/hubs"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

