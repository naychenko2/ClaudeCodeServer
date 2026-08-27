using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Сторож dot-segment traversal в document_id (блокер приёмки волны 4.1): пейлоад
/// «../../{uuid}/documents/{doc}» со СВОИМ валидным dataset_id раньше проходил гейт
/// релевантности (ResolveReadableAsync проверяет только первичный dataset_id), а HttpClient
/// резолвил dot-сегменты по RFC — запрос уходил в ЧУЖОЙ датасет под общим ключом workspace:
/// чтение, перезапись и удаление чужих документов. Лечение: белый список формы id
/// (IsValidDifyId) до резолва датасета и любого HTTP.
///
/// Dify здесь ФАКТИКА: KnowledgeService подменён копией на записывающем хендлере —
/// тесты не ходят в сеть и не зависят от настроенной секции (в отличие от
/// DifyHttpOwnerIsolationTests). Заодно — лимит имени публичной базы (неблокирующее №4).
/// </summary>
public class DifyToolsetTraversalGuardTests : IClassFixture<DifyToolsetTraversalGuardTests.Host>, IDisposable
{
    /// <summary>
    /// Один хост на ВЕСЬ класс (IClassFixture), а не по тесту: xUnit создаёт экземпляр класса
    /// на каждый тест, и 12 подъёмов WebApplicationFactory — это и нагрузка, и 12 мёртвых
    /// окон статического WorkflowMetaResolver.Log (Program.cs перезаписывает его логгером
    /// каждого хоста; после Dispose логгер мёртв, и параллельные тесты ловят
    /// ObjectDisposedException на пустяковом warning'е).
    /// </summary>
    public sealed class Host : IDisposable
    {
        public readonly RecordingDifyHandler Handler = new();

        public Host()
        {
            Factory = new TestWebApplicationFactory
            {
                // IsConfigured=true без сети: тулсет и каталог ходят в записывающий хендлер
                ExtraServices = services => services.AddSingleton<KnowledgeService>(sp =>
                    new KnowledgeService(
                        new StubHttpClientFactory(Handler),
                        Options.Create(new DifyOptions { ApiUrl = "http://dify-fake.test", ApiKey = "test-key" }),
                        sp.GetRequiredService<WorkspaceKnowledgeStore>())),
            };
        }

        public TestWebApplicationFactory Factory { get; }

        public void Dispose()
        {
            Factory.Dispose();
            // Закрываем окно NullLogger'ом: фоновой значение статика после Dispose мёртвое,
            // а тесты, проверяющие warnings резолвера, подменяют Log своим коллектором
            WorkflowMetaResolver.Log = NullLogger.Instance;
        }
    }

    private readonly Host _host;

    public DifyToolsetTraversalGuardTests(Host host) => _host = host;

    public void Dispose() => _host.Handler.Reset();

    private HttpClient Client => _host.Factory.CreateAuthenticatedClient();

    private async Task<string> CreateSessionInFreshProjectAsync()
    {
        var client = Client;
        var project = await client.PostAsJsonAsync("/api/projects",
            new { name = $"dify-guard-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(
            await project.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        var session = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(
            await session.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> CallToolAsync(string sessionId, string tool, object args)
    {
        var resp = await Client.PostAsJsonAsync($"/mcp/dify/{sessionId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = tool, arguments = args },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private static string ResultText(JsonElement call) =>
        call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    /// <summary>
    /// Пейлоад проверки в КАЖДОМ document-инструменте: валидный отказ про форму id,
    /// и запрос НЕ уходит — гейт стоит до резолва датасета (никакого HTTP к Dify).
    /// </summary>
    [Theory]
    [InlineData("update_document_by_text")]
    [InlineData("update_document_by_file")]
    [InlineData("delete_document")]
    [InlineData("list_segments")]
    [InlineData("add_segments")]
    public async Task TraversalВDocumentId_ВалидныйОтказ_ЗапросНеУходит(string tool)
    {
        var sessionId = await CreateSessionInFreshProjectAsync();
        _host.Handler.Reset();

        var call = await CallToolAsync(sessionId, tool, new
        {
            dataset_id = "ds-own-1",
            document_id = "../../aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/documents/victim-doc",
            text = "злой текст",
            file_base64 = Convert.ToBase64String([1, 2, 3]),
            file_name = "evil.txt",
            segments = new[] { new { content = "злой сегмент" } },
        });

        call.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        ResultText(call).Should().Contain("document_id").And.Contain("не допускаются");
        _host.Handler.Entries.Should().BeEmpty(
            "пейлоад отбивается гейтом формы ДО резолва датасета и любого HTTP к Dify");
    }

    /// <summary>
    /// Чистый dot-segment «..» (экранирование его не трогает — точки unreserved): у модели
    /// его обязан отбивать тот же белый список, иначе delete_document с document_id=".."
    /// резолвился бы в DELETE самого датасета.
    /// </summary>
    [Theory]
    [InlineData("delete_document", "..")]
    [InlineData("delete_document", "../..")]
    [InlineData("list_segments", ".")]
    public async Task ЧистыйDotSegment_ОтказБезЗапроса(string tool, string documentId)
    {
        var sessionId = await CreateSessionInFreshProjectAsync();
        _host.Handler.Reset();

        var call = await CallToolAsync(sessionId, tool,
            new { dataset_id = "ds-own-1", document_id = documentId });

        call.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        ResultText(call).Should().Contain("не допускаются");
        _host.Handler.Entries.Should().BeEmpty("dot-segment отбивается гейтом формы до любого HTTP");
    }

    /// <summary>
    /// Позитивный контроль: настоящий UUID проходит гейт и уезжает обычным путём —
    /// белому списку удовлетворяют все реальные id документов Dify.
    /// </summary>
    [Fact]
    public async Task ГодныйDocumentId_ПроходитГейтИдетВDify()
    {
        var sessionId = await CreateSessionInFreshProjectAsync();
        _host.Handler.Reset();

        var call = await CallToolAsync(sessionId, "list_segments", new
        {
            dataset_id = "ds-own-1",
            document_id = "11111111-2222-3333-4444-555555555555",
        });

        call.GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse(
            "валидный UUID проходит гейт формы");
        _host.Handler.Entries.Should().Contain(e =>
            e.Method == "GET" &&
            e.Path == "/datasets/ds-own-1/documents/11111111-2222-3333-4444-555555555555/segments",
            "легитимный вызов доезжает до Dify прежним путём");
    }

    /// <summary>
    /// Неблокирующее №4 приёмки 4.1: публичная база живёт БЕЗ префикса владельца — её
    /// планка лимита полные 40 символов. Имя ровно в лимит проходит и уезжает в Dify
    /// без «{username}:kb:».
    /// </summary>
    [Fact]
    public async Task ПубличнаяБазаРовноВЛимит_ПроходитБезПрефикса()
    {
        var sessionId = await CreateSessionInFreshProjectAsync();
        _host.Handler.Reset();
        var name = new string('б', 40);

        var call = await CallToolAsync(sessionId, "create_dataset",
            new { name = name, permission = "all_team_members" });

        call.GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse(
            "публичное имя в 40 символов — в лимите, префикса у него нет");
        var post = _host.Handler.Entries.Should().ContainSingle(
            e => e.Method == "POST" && e.Path == "/datasets").Subject;
        // Тело — JSON с \u-экранированной кириллицей, сверяем разобранное значение
        var payload = JsonSerializer.Deserialize<JsonElement>(post.Body!);
        payload.GetProperty("name").GetString().Should().Be(name);
        payload.GetProperty("permission").GetString().Should().Be("all_team_members");
    }

    /// <summary>
    /// Сверх лимита (41–45) — отказ с ПРАВИЛЬНОЙ планкой: полные 40, а не бюджет личной
    /// базы (у «testuser» это 28). Пропускать длинное имя нельзя — Dify сам ответит сырой
    /// 400-й pydantic, которую этот гейт и призван подменить внятным текстом.
    /// </summary>
    [Theory]
    [InlineData(41)]
    [InlineData(45)]
    public async Task ПубличнаяБазаСверхЛимита_ПодсказкаБезЛичногоБюджета(int length)
    {
        var sessionId = await CreateSessionInFreshProjectAsync();
        _host.Handler.Reset();

        var call = await CallToolAsync(sessionId, "create_dataset",
            new { name = new string('б', length), permission = "all_team_members" });

        call.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        var text = ResultText(call);
        text.Should().Contain("40 символов");
        text.Should().NotContain("28 символов",
            "подсказка обязана считать бюджет публичной базы (40), а не личной (40 − длина префикса)");
        _host.Handler.Entries.Should().NotContain(e => e.Method == "POST",
            "сверхлимитное имя не доезжает до Dify");
    }
}
