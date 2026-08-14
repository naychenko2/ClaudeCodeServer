using System.Net;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Чтение судьбы документов Dify (шаг 1 реконсайлера): фильтр ?status= в листинге,
// десериализация поля error и тихая отсечка без настроенного Dify.
public class KnowledgeServiceDocumentsTests
{
    // Фейковый Dify: запоминает запросы (путь + query), отдаёт настраиваемый JSON
    private sealed class FakeHandler : HttpMessageHandler
    {
        public readonly List<string> Requests = new();
        public string ResponseJson = "{\"data\":[],\"has_more\":false,\"total\":0}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseJson, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static KnowledgeService Create(FakeHandler handler, bool configured = true)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        var opts = new DifyOptions { ApiUrl = configured ? "http://dify.test/v1" : "", ApiKey = configured ? "key" : "" };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        return new KnowledgeService(factory.Object,
            Microsoft.Extensions.Options.Options.Create(opts),
            new WorkspaceKnowledgeStore(config));
    }

    [Fact]
    public async Task ListDocumentsAsync_Прокидывает_Фильтр_Status_В_Query()
    {
        var handler = new FakeHandler();
        var sut = Create(handler);

        await sut.ListDocumentsAsync("ds-1", status: "error");

        handler.Requests.Should().ContainSingle()
            .Which.Should().Contain("/datasets/ds-1/documents").And.Contain("status=error");
    }

    [Fact]
    public async Task ListDocumentsAsync_Без_Status_Не_Добавляет_Параметр()
    {
        var handler = new FakeHandler();
        var sut = Create(handler);

        await sut.ListDocumentsAsync("ds-1");

        handler.Requests.Should().ContainSingle().Which.Should().NotContain("status=");
    }

    [Fact]
    public async Task ListDocumentsAsync_Десериализует_Поле_Error()
    {
        var handler = new FakeHandler
        {
            ResponseJson = """
                {"data":[
                    {"id":"d1","name":"жертва.md","indexing_status":"error","error":"Connection refused: ollama"},
                    {"id":"d2","name":"здоровый.md","indexing_status":"completed"}
                ],"has_more":false,"total":2}
                """,
        };
        var sut = Create(handler);

        var page = await sut.ListDocumentsAsync("ds-1", status: "error");

        page.Data.Should().HaveCount(2);
        page.Data[0].Error.Should().Be("Connection refused: ollama");
        page.Data[0].IndexingStatus.Should().Be("error");
        page.Data[1].Error.Should().BeNull();
    }

    [Fact]
    public async Task ListAllDocumentsAsync_Прокидывает_Status_На_Каждую_Страницу()
    {
        var handler = new FakeHandler();
        var sut = Create(handler);

        await sut.ListAllDocumentsAsync("ds-1", status: "error");

        handler.Requests.Should().NotBeEmpty();
        handler.Requests.Should().OnlyContain(r => r.Contains("status=error"));
    }

    [Fact]
    public async Task Без_Настроенного_Dify_Отдают_Пусто_Без_Вызовов()
    {
        var handler = new FakeHandler();
        var sut = Create(handler, configured: false);

        var one = await sut.ListDocumentsAsync("ds-1", status: "error");
        var all = await sut.ListAllDocumentsAsync("ds-1");

        one.Data.Should().BeEmpty();
        all.Data.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
    }
}
