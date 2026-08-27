using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Экранирование сегментов пути в API Dify (блокер приёмки волны 4.1, слой сервиса —
/// закрывает и REST-путь вызовов): голая интерполяция documentId позволяла dot-segment-
/// пейлоаду «../../{uuid}/documents/{doc}» резолвиться HttpClient'ом по RFC в ЧУЖОЙ
/// датасет. Сегмент обязан остаться одним непрозрачным куском пути: «/» кодируется
/// в %2F, и пейлоад не покидает свой датасет. Парный гейт для модели — белый список
/// формы в DifyToolset (DifyToolsetTraversalGuardTests); чистый «..» экранирование не
/// трогает (точки unreserved) — он закрыт тулсетом, а REST-клиенту даёт не больше
/// власти, чем легитимный DELETE своего датасета.
/// </summary>
public class KnowledgeServicePathEscapeTests : IDisposable
{
    private readonly string _temp =
        Path.Combine(Path.GetTempPath(), "ccs-dify-escape-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly RecordingDifyHandler _handler = new();

    public KnowledgeServicePathEscapeTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* уборка best-effort */ }
    }

    private KnowledgeService Service()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_temp, "projects.json"),
            })
            .Build();
        return new KnowledgeService(
            new StubHttpClientFactory(_handler),
            Options.Create(new DifyOptions { ApiUrl = "http://dify-fake.test", ApiKey = "k" }),
            new WorkspaceKnowledgeStore(config));
    }

    [Theory]
    // Пейлоад проверяющего в первозданном виде
    [InlineData("../../aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/documents/victim-doc")]
    // Свой doc + выход наверх к чужому датасету
    [InlineData("my-doc/../../aaaaaaaa-bbbb/documents/victim")]
    // Попытка подсунуть уже-экранированные слеши (двойное кодирование нейтрализует её же)
    [InlineData("..%2f..%2faaaaaaaa-bbbb/documents/victim")]
    public async Task TraversalВDocumentId_НеПокидаетСвойДатасет(string payload)
    {
        var service = Service();
        await service.DeleteDocumentAsync("my-ds", payload);

        var path = _handler.Entries.Single().Path;
        path.Should().StartWith("/datasets/my-ds/documents/",
            "экранированный id — один непрозрачный сегмент внутри своего датасета");
        path.Should().Contain("%2F",
            "«/» пейлоада обязан кодироваться — иначе RFC-резолв вырежет dot-сегменты");
        path.Should().NotStartWith("/datasets/aaaaaaaa",
            "запрос не уходит в чужой датасет — то, что требовала приёмка для REST-пути");
    }

    [Fact]
    public async Task TraversalВDocumentId_ListSegments_ПутьИЗапросЦелы()
    {
        var service = Service();
        await service.ListSegmentsAsync("my-ds", "../../victim-uuid/documents/victim-doc");

        var entry = _handler.Entries.Single();
        entry.Method.Should().Be("GET");
        entry.Path.Should().StartWith("/datasets/my-ds/documents/")
            .And.NotStartWith("/datasets/victim-uuid");
    }

    [Fact]
    public async Task TraversalВDatasetId_НеПокидаетПространствоДатасетов()
    {
        var service = Service();
        await service.DeleteDatasetAsync("../../victim-uuid/documents/victim-doc");

        _handler.Entries.Single().Path.Should().StartWith("/datasets/")
            .And.NotStartWith("/datasets/victim-uuid");
    }

    /// <summary>Легитимные id (UUID) экранирование не меняет — трафик живой интеграции тот же.</summary>
    [Fact]
    public async Task ГодныйDocumentId_ПутьБезИзменений()
    {
        var service = Service();
        await service.DeleteDocumentAsync("my-ds", "11111111-2222-3333-4444-555555555555");

        _handler.Entries.Single().Path
            .Should().Be("/datasets/my-ds/documents/11111111-2222-3333-4444-555555555555");
    }
}
