using System.Collections.Concurrent;
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

public record DifyMetadataField(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type);

public record DifyDatasetMetadataResponse(
    [property: JsonPropertyName("doc_metadata")] List<DifyMetadataField> DocMetadata);

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
        var name = Path.GetFileName(relativePath).ToLowerInvariant();
        // ext=="" → Makefile, LICENSE; ext==name → .gitignore, .env (дотфайлы)
        var isText = ext == "" || ext == name || TextExtensions.Contains(ext);
        return isText || FileExtensions.Contains(ext);
    }

    public static bool IsTextIndexable(string relativePath)
    {
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();
        var name = Path.GetFileName(relativePath).ToLowerInvariant();
        return ext == "" || ext == name || TextExtensions.Contains(ext);
    }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DifyOptions _cfg;
    private readonly WorkspaceKnowledgeStore _workspaceStore;
    private readonly SemaphoreSlim _createLock = new(1, 1);
    // Кэш: datasetId → fieldId поля "tags" (чтобы не делать GET metadata при каждом сохранении тегов)
    private readonly ConcurrentDictionary<string, string> _tagsFieldIdCache = new();

    public KnowledgeService(IHttpClientFactory httpClientFactory, IOptions<DifyOptions> options,
        WorkspaceKnowledgeStore workspaceStore)
    {
        _httpClientFactory = httpClientFactory;
        _cfg = options.Value;
        _workspaceStore = workspaceStore;
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient("dify");
        client.BaseAddress = new Uri(_cfg.ApiUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _cfg.ApiKey);
        return client;
    }

    // Lazy-создание датасета при первом обращении; SemaphoreSlim с double-check защищает от гонки.
    // Данные хранятся в WorkspaceKnowledgeStore, привязанные к rootPath — общие для всех проектов
    // в одной папке.
    public async Task<string> EnsureDatasetAsync(Project project, string username)
    {
        var wk = _workspaceStore.GetOrCreate(project.RootPath);
        if (!string.IsNullOrEmpty(wk.DifyDatasetId))
            return wk.DifyDatasetId;

        await _createLock.WaitAsync();
        try
        {
            wk = _workspaceStore.GetOrCreate(project.RootPath);
            if (!string.IsNullOrEmpty(wk.DifyDatasetId))
                return wk.DifyDatasetId;

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

            wk.DifyDatasetId = body.Id;
            _workspaceStore.Save(wk);
            return wk.DifyDatasetId;
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

    // Возвращает fieldId поля "tags" в метаданных датасета; создаёт поле если его нет
    public async Task<string> EnsureTagsFieldAsync(string datasetId)
    {
        if (_tagsFieldIdCache.TryGetValue(datasetId, out var cached))
            return cached;

        var client = CreateClient();
        var resp = await client.GetAsync($"datasets/{datasetId}/metadata");
        resp.EnsureSuccessStatusCode();

        var meta = await resp.Content.ReadFromJsonAsync<DifyDatasetMetadataResponse>();
        var existing = meta?.DocMetadata.FirstOrDefault(f => f.Name == "tags");
        if (existing is not null)
        {
            _tagsFieldIdCache[datasetId] = existing.Id;
            return existing.Id;
        }

        var createResp = await client.PostAsJsonAsync($"datasets/{datasetId}/metadata",
            new { type = "string", name = "tags" });
        createResp.EnsureSuccessStatusCode();

        var created = await createResp.Content.ReadFromJsonAsync<DifyMetadataField>()
            ?? throw new InvalidOperationException("Пустой ответ при создании поля tags");
        _tagsFieldIdCache[datasetId] = created.Id;
        return created.Id;
    }

    // Обновляет значение тегов для документа в Dify без переиндексирования
    public async Task UpdateDocumentTagsAsync(string datasetId, string documentId, List<string> tags)
    {
        var fieldId = await EnsureTagsFieldAsync(datasetId);
        var tagValue = string.Join(",", tags);
        var client = CreateClient();
        var resp = await client.PostAsJsonAsync($"datasets/{datasetId}/documents/metadata", new
        {
            operation_data = new[]
            {
                new
                {
                    document_id = documentId,
                    metadata_list = new[] { new { id = fieldId, name = "tags", value = tagValue } },
                    partial_update = true,
                }
            }
        });
        resp.EnsureSuccessStatusCode();
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
