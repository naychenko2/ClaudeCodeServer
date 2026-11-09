using System.Net;
using System.Text;

namespace ClaudeHomeServer.Tests.Helpers;

// Записывающий хендлер «Dify» (блокер приёмки волны 4.1): копия KnowledgeService в тестах
// ходит через него вместо сети — canned-ответы по пути запроса плюс журнал (метод, путь,
// тело), по которому сторожа проверяют, что traversal-пейлоад не дошёл до «чужого» датасета
public sealed class RecordingDifyHandler : HttpMessageHandler
{
    public sealed record Entry(string Method, string Path, string? Body);

    private readonly object _lock = new();
    private readonly List<Entry> _entries = [];

    public IReadOnlyList<Entry> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    public void Reset()
    {
        lock (_lock) _entries.Clear();
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken ct)
    {
        var body = request.Content is null
            ? null
            : request.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
        var path = request.RequestUri!.AbsolutePath;
        lock (_lock) _entries.Add(new(request.Method.Method, path, body));

        // Свой датасет тестового пользователя — чтобы ResolveReadableAsync("ds-own-1")
        // проходил гейт релевантности; остальное — пустые страницы
        var json = (path, request.Method.Method) switch
        {
            ("/datasets", "POST") => "{\"id\":\"ds-created-1\"}",
            ("/datasets", _) => "{\"data\":[{\"id\":\"ds-own-1\",\"name\":\"testuser:kb:заметки\","
                + "\"permission\":\"only_me\",\"document_count\":0,\"word_count\":0,\"created_at\":0}],"
                + "\"has_more\":false}",
            var (p, _) when p.EndsWith("/segments") => "{\"data\":[]}",
            var (p, _) when p.StartsWith("/datasets/") && p.Contains("/documents") =>
                "{\"data\":[],\"has_more\":false,\"total\":0}",
            _ => "{\"data\":[],\"has_more\":false}",
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
    }
}

// Фабрика HttpClient'ов поверх записывающего хендлера: подменяет именованную фабрику «dify»
// в KnowledgeService без сети и без настроенной секции Dify
public sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
