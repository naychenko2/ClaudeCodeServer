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
    public async Task Get_ГрафНеПостроен_Возвращает404()
    {
        var dir = MkProjectDir("nograph");
        WriteCs(dir);
        var project = await CreateProjectAsync("nograph", dir);
        var id = project.GetProperty("id").GetString()!;

        // Граф не строили — эндпоинт отдаёт 404 с понятным сообщением.
        var resp = await _client.GetAsync($"/api/projects/{id}/code-graph");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("message").GetString().Should().NotBeNullOrEmpty();
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
}

