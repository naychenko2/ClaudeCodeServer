using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Dify v1 отбивает query длиннее 250 символов ошибкой 400 — обрезка на проводе
// (иначе recall заметок и памяти персон пустой на любом длинном ходе).
public class KnowledgeQueryTrimTests : IDisposable
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public readonly List<string> Bodies = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"records\":[]}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private readonly string _tempDir;
    private readonly RecordingHandler _dify = new();

    public KnowledgeQueryTrimTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "kqt_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* тест-мусор */ }
        GC.SuppressFinalize(this);
    }

    private KnowledgeService Make()
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
            }),
            new WorkspaceKnowledgeStore(config));
    }

    [Fact]
    public void TrimQuery_Обрезает_Длинный_Запрос_До_Потолка()
    {
        var long770 = new string('я', 770);
        KnowledgeService.TrimQuery(long770).Length.Should().Be(KnowledgeService.MaxQueryLength);
        KnowledgeService.MaxQueryLength.Should().Be(250);
    }

    [Fact]
    public void TrimQuery_Короткий_Не_Трогает_И_Тримит_Пробелы()
    {
        KnowledgeService.TrimQuery("  привет  ").Should().Be("привет");
        KnowledgeService.TrimQuery(null).Should().Be("");
    }

    [Fact]
    public async Task Retrieve_Отправляет_В_Dify_Запрос_Не_Длиннее_250()
    {
        var sut = Make();
        await sut.RetrieveAsync("ds-1", new string('a', 770), 8, searchMethod: "semantic_search");

        var sent = JsonDocument.Parse(_dify.Bodies.Single()).RootElement.GetProperty("query").GetString()!;
        sent.Length.Should().Be(250);
    }
}
