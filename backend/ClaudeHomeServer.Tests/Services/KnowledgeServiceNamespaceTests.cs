using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Неймспейс контура в общем Dify (DifyOptions.Namespace): префикс имени добавляется при
// создании/переименовании датасета и срезается при листинге; чужие неймспейсы скрыты.
public class KnowledgeServiceNamespaceTests : IDisposable
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string Body)> Calls = new();
        public string DatasetsJson = "[]";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method.Method, path, body));

            if (request.Method == HttpMethod.Get && path == "/v1/datasets")
                return Json($"{{\"data\":{DatasetsJson},\"has_more\":false,\"total\":0}}");
            if (request.Method == HttpMethod.Post && path == "/v1/datasets")
                return Json("{\"id\":\"ds-1\"}");
            return Json("{}");
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private readonly string _tempDir;
    private readonly RecordingHandler _dify = new();

    public KnowledgeServiceNamespaceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "kns_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* тест-мусор */ }
        GC.SuppressFinalize(this);
    }

    private KnowledgeService Make(string ns, params string[] foreign)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(_dify, disposeHandler: false));
        return new KnowledgeService(factory.Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions
            {
                ApiUrl = "http://dify.test/v1",
                ApiKey = "key",
                Namespace = ns,
                ForeignNamespaces = foreign.ToList(),
            }),
            new WorkspaceKnowledgeStore(config));
    }

    private static string NameOf(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("name").GetString()!;

    [Fact]
    public async Task Create_Добавляет_Префикс_Неймспейса()
    {
        var sut = Make("dev");
        await sut.CreateDatasetAsync("user:notes");
        NameOf(_dify.Calls.Single(c => c.Method == "POST").Body).Should().Be("dev:user:notes");
    }

    [Fact]
    public async Task Create_Без_Неймспейса_Имя_Как_Есть()
    {
        var sut = Make("");
        await sut.CreateDatasetAsync("user:notes");
        NameOf(_dify.Calls.Single(c => c.Method == "POST").Body).Should().Be("user:notes");
    }

    [Fact]
    public async Task Ensure_Создаёт_Датасет_Проекта_С_Префиксом()
    {
        var sut = Make("dev");
        var project = new Project { Name = "proj", RootPath = Path.Combine(_tempDir, "proj"), OwnerId = "u1" };
        await sut.EnsureDatasetAsync(project, "user");
        NameOf(_dify.Calls.Single(c => c.Method == "POST").Body).Should().Be("dev:user:proj");
    }

    [Fact]
    public async Task Rename_Добавляет_Префикс()
    {
        var sut = Make("dev");
        await sut.RenameDatasetAsync("ds-1", "user:renamed");
        NameOf(_dify.Calls.Single(c => c.Method == "PATCH").Body).Should().Be("dev:user:renamed");
    }

    [Fact]
    public async Task List_Отдаёт_Только_Свой_Неймспейс_Без_Префикса()
    {
        var sut = Make("dev");
        _dify.DatasetsJson = JsonSerializer.Serialize(new[]
        {
            new { id = "1", name = "dev:user:notes" },
            new { id = "2", name = "user:plain" },          // прод-датасет без префикса
            new { id = "3", name = "other:kb:Чужая" },      // похоже на юзерский префикс — не наш ns
        });
        var list = await sut.ListDatasetsAsync();
        list.Select(d => d.Name).Should().BeEquivalentTo(new[] { "user:notes" });
    }

    [Fact]
    public async Task List_Без_Неймспейса_Скрывает_Чужие_Контуры()
    {
        var sut = Make("", "dev");
        _dify.DatasetsJson = JsonSerializer.Serialize(new[]
        {
            new { id = "1", name = "dev:user:notes" },      // dev-контур — скрыт
            new { id = "2", name = "user:notes" },
            new { id = "3", name = "Публичная" },
        });
        var list = await sut.ListDatasetsAsync();
        list.Select(d => d.Name).Should().BeEquivalentTo(new[] { "user:notes", "Публичная" });
    }
}
