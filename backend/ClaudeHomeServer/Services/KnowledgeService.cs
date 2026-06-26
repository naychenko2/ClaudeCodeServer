using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services;

public record DifyDocumentInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("indexing_status")] string IndexingStatus);

public record DifyDocumentItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("indexing_status")] string IndexingStatus);

public record DifyDocumentsPage(
    [property: JsonPropertyName("data")] List<DifyDocumentItem> Data,
    [property: JsonPropertyName("has_more")] bool HasMore,
    [property: JsonPropertyName("total")] int Total);

public record DifyDatasetResponse(
    [property: JsonPropertyName("id")] string Id);

public record DifyDocumentCreateResponse(
    [property: JsonPropertyName("document")] DifyDocumentItem Document);

public class KnowledgeService
{
    // Расширения, которые индексируем как текст (прямая отправка содержимого)
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".cs", ".ts", ".tsx", ".js", ".jsx",
        ".py", ".json", ".yaml", ".yml", ".xml", ".html", ".htm",
        ".css", ".scss", ".toml", ".ini", ".sh", ".bash", ".ps1",
        ".go", ".rs", ".java", ".kt", ".rb", ".php", ".swift",
        ".tf", ".hcl", ".sql", ".graphql", ".proto",
    };

    // Расширения, которые индексируем через file-upload (бинарные документы)
    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".xlsx", ".xls", ".pptx", ".csv", ".epub",
    };

    public static bool IsKnowledgeIndexable(string relativePath)
    {
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();
        return TextExtensions.Contains(ext) || FileExtensions.Contains(ext);
    }

    public static bool IsTextIndexable(string relativePath)
    {
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();
        return TextExtensions.Contains(ext);
    }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DifyOptions _cfg;
    private readonly ProjectManager _projects;
    private readonly SemaphoreSlim _createLock = new(1, 1);

    public KnowledgeService(IHttpClientFactory httpClientFactory, IOptions<DifyOptions> options, ProjectManager projects)
    {
        _httpClientFactory = httpClientFactory;
        _cfg = options.Value;
        _projects = projects;
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient("dify");
        client.BaseAddress = new Uri(_cfg.ApiUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _cfg.ApiKey);
        return client;
    }

    // Lazy-создание датасета при первом обращении; SemaphoreSlim с double-check защищает от гонки
    public async Task<string> EnsureDatasetAsync(Project project, string username)
    {
        if (!string.IsNullOrEmpty(project.DifyDatasetId))
            return project.DifyDatasetId;

        await _createLock.WaitAsync();
        try
        {
            // Двойная проверка после захвата замка
            if (!string.IsNullOrEmpty(project.DifyDatasetId))
                return project.DifyDatasetId;

            if (string.IsNullOrEmpty(_cfg.ApiUrl) || string.IsNullOrEmpty(_cfg.ApiKey))
                throw new InvalidOperationException("Dify не настроен: задайте Dify:ApiUrl и Dify:ApiKey в конфигурации");

            var client = CreateClient();
            var resp = await client.PostAsJsonAsync("datasets", new
            {
                name = $"{username}:{project.Name}",
                indexing_technique = _cfg.IndexingTechnique,
                permission = "only_me",
            });
            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadFromJsonAsync<DifyDatasetResponse>()
                ?? throw new InvalidOperationException("Пустой ответ от Dify при создании датасета");

            _projects.SetDifyDataset(project.Id, body.Id);
            return body.Id;
        }
        finally
        {
            _createLock.Release();
        }
    }

    public async Task<DifyDocumentInfo> IndexFileByTextAsync(
        string datasetId, string fileName, string content, List<string>? tags = null)
    {
        var client = CreateClient();
        var resp = await client.PostAsJsonAsync($"datasets/{datasetId}/document/create_by_text", new
        {
            name = fileName,
            text = content,
            indexing_technique = _cfg.IndexingTechnique,
            process_rule = new { mode = "automatic" },
            doc_metadata = new { tags = (tags ?? []).ToArray() },
        });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<DifyDocumentCreateResponse>()
            ?? throw new InvalidOperationException("Пустой ответ от Dify при индексировании текста");
        return new DifyDocumentInfo(body.Document.Id, body.Document.Name, body.Document.IndexingStatus);
    }

    public async Task<DifyDocumentInfo> IndexFileByBytesAsync(
        string datasetId, string fileName, byte[] content, List<string>? tags = null)
    {
        var client = CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(content), "file", fileName);
        form.Add(new StringContent(System.Text.Json.JsonSerializer.Serialize(new
        {
            indexing_technique = _cfg.IndexingTechnique,
            process_rule = new { mode = "automatic" },
            doc_metadata = new { tags = (tags ?? []).ToArray() },
        })), "data");

        var resp = await client.PostAsync($"datasets/{datasetId}/document/create_by_file", form);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<DifyDocumentCreateResponse>()
            ?? throw new InvalidOperationException("Пустой ответ от Dify при загрузке файла");
        return new DifyDocumentInfo(body.Document.Id, body.Document.Name, body.Document.IndexingStatus);
    }

    public async Task<DifyDocumentsPage> ListDocumentsAsync(string datasetId, int page = 1, int limit = 20)
    {
        var client = CreateClient();
        var resp = await client.GetAsync($"datasets/{datasetId}/documents?page={page}&limit={limit}");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<DifyDocumentsPage>()
            ?? new DifyDocumentsPage([], false, 0);
    }

    public async Task DeleteDocumentAsync(string datasetId, string documentId)
    {
        var client = CreateClient();
        var resp = await client.DeleteAsync($"datasets/{datasetId}/documents/{documentId}");
        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteDatasetAsync(string datasetId)
    {
        var client = CreateClient();
        var resp = await client.DeleteAsync($"datasets/{datasetId}");
        if (resp.StatusCode != System.Net.HttpStatusCode.NoContent)
            resp.EnsureSuccessStatusCode();
    }
}
