using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
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

public record DifyDatasetListItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    // Дополнительные поля списка датасетов (использует раздел «Знания»): право доступа,
    // счётчики, дата создания, описание. У старых вызовов остаются необязательными.
    [property: JsonPropertyName("permission")] string? Permission = null,
    [property: JsonPropertyName("document_count")] int DocumentCount = 0,
    [property: JsonPropertyName("word_count")] int WordCount = 0,
    [property: JsonPropertyName("created_at")] double? CreatedAt = null,
    [property: JsonPropertyName("description")] string? Description = null);

public record DifyDatasetsPage(
    [property: JsonPropertyName("data")] List<DifyDatasetListItem> Data,
    [property: JsonPropertyName("has_more")] bool HasMore);

public record DifyDocumentCreateResponse(
    [property: JsonPropertyName("document")] DifyDocumentItem Document);

// --- Retrieve (семантический поиск по датасету) ---

public record DifyRetrieveDocument(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    // Структурные метаданные документа (напр. meeting_date/meeting_id/meeting_source) —
    // значения произвольных типов, приводятся к строке при маппинге в чанк.
    [property: JsonPropertyName("doc_metadata")] Dictionary<string, JsonElement>? DocMetadata = null);

public record DifyRetrieveSegment(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("document")] DifyRetrieveDocument Document);

public record DifyRetrieveRecord(
    [property: JsonPropertyName("segment")] DifyRetrieveSegment Segment,
    [property: JsonPropertyName("score")] double Score);

public record DifyRetrieveResponse(
    [property: JsonPropertyName("records")] List<DifyRetrieveRecord> Records);

// Чанк результата поиска. Metadata — структурные метаданные документа-источника
// (дата встречи, id, источник и т.п.), приведённые к строкам; null/пусто — их нет.
public record DifyRetrieveChunk(string Content, double Score, string DocumentId, string DocumentName,
    IReadOnlyDictionary<string, string>? Metadata = null);

// Условие фильтрации по метаданным. Op — строковый оператор Dify (contains, not contains,
// start with, end with, is, is not, empty, not empty). Value не нужен для empty/not empty.
public record KnowledgeMetadataFilter(string Name, string Op, string? Value);

// Поле метаданных датасета (имя + тип: string/number/time).
public record KnowledgeMetadataFieldInfo(string Name, string Type);

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

    // Dify настроен (ApiUrl + ApiKey) — для graceful degradation семантики заметок
    public bool IsConfigured =>
        !string.IsNullOrEmpty(_cfg.ApiUrl) && !string.IsNullOrEmpty(_cfg.ApiKey);

    // Создание датасета по имени, без привязки к проекту (для заметок пользователя).
    // permission: "only_me" (личный) | "all_team_members" (публичный, общий на workspace);
    // description — необязательное описание (Dify хранит сам). Существующие вызовы
    // (заметки/проекты) остаются на дефолте only_me.
    public async Task<string> CreateDatasetAsync(string name, string permission = "only_me", string? description = null)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Dify не настроен: задайте Dify:ApiUrl и Dify:ApiKey в конфигурации");
        var payload = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["indexing_technique"] = _cfg.IndexingTechnique,
            ["permission"] = permission,
        };
        if (!string.IsNullOrWhiteSpace(description)) payload["description"] = description;
        var client = CreateClient();
        var resp = await client.PostAsJsonAsync("datasets", payload);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DifyDatasetResponse>()
            ?? throw new InvalidOperationException("Пустой ответ от Dify при создании датасета");
        return body.Id;
    }

    // Поиск по датасету: чанки со score и документом-источником. По умолчанию
    // ГИБРИДНЫЙ поиск — смысловой (векторный) + полнотекстовый (по ключевым словам)
    // в одном запросе; Dify объединяет результаты. Работает без reranking-модели
    // (reranking_enable=false). Если датасет не поддерживает гибрид (напр. economy-
    // индексация без полнотекста) — тихий фолбэк на чисто смысловой поиск.
    // filters — необязательная фильтрация по метаданным документов (дата встречи,
    // источник и т.п.); logic — как объединять условия ("and"/"or").
    // searchMethod (явный): "semantic_search" | "full_text_search" | "hybrid_search".
    // Если не задан — гибридный поиск с тихим фолбэком на семантический (как раньше).
    public async Task<IReadOnlyList<DifyRetrieveChunk>> RetrieveAsync(string datasetId, string query, int topK = 8,
        IReadOnlyList<KnowledgeMetadataFilter>? filters = null, string logic = "and", string? searchMethod = null)
    {
        if (!string.IsNullOrEmpty(searchMethod))
            return await RetrieveWithMethodAsync(datasetId, query, topK, searchMethod, filters, logic);
        try { return await RetrieveWithMethodAsync(datasetId, query, topK, "hybrid_search", filters, logic); }
        catch { return await RetrieveWithMethodAsync(datasetId, query, topK, "semantic_search", filters, logic); }
    }

    private async Task<IReadOnlyList<DifyRetrieveChunk>> RetrieveWithMethodAsync(
        string datasetId, string query, int topK, string searchMethod,
        IReadOnlyList<KnowledgeMetadataFilter>? filters, string logic)
    {
        var client = CreateClient();
        // retrieval_model строим словарём: metadata_filtering_conditions добавляется
        // только при наличии фильтров (внутри retrieval_model — top-level Dify игнорирует)
        var retrievalModel = new Dictionary<string, object?>
        {
            ["search_method"] = searchMethod,
            ["reranking_enable"] = false,
            ["top_k"] = topK,
            ["score_threshold_enabled"] = false,
        };
        if (filters is { Count: > 0 })
            retrievalModel["metadata_filtering_conditions"] = new
            {
                logical_operator = logic is "or" ? "or" : "and",
                conditions = filters.Select(f => new
                {
                    name = f.Name,
                    comparison_operator = f.Op,
                    value = f.Value ?? "",
                }),
            };
        var resp = await client.PostAsJsonAsync($"datasets/{datasetId}/retrieve", new { query, retrieval_model = retrievalModel });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DifyRetrieveResponse>()
            ?? throw new InvalidOperationException("Пустой ответ от Dify при поиске");
        return body.Records
            .Select(r => new DifyRetrieveChunk(r.Segment.Content, r.Score,
                r.Segment.Document.Id, r.Segment.Document.Name,
                MapMetadata(r.Segment.Document.DocMetadata)))
            .ToList();
    }

    // Поля метаданных датасета (имя + тип) — для валидации фильтра и подсказки персоне,
    // по каким полям вообще можно фильтровать. Пусто — метаданных у датасета нет.
    public async Task<IReadOnlyList<KnowledgeMetadataFieldInfo>> ListMetadataFieldsAsync(string datasetId)
    {
        if (!IsConfigured) return [];
        var client = CreateClient();
        var resp = await client.GetAsync($"datasets/{datasetId}/metadata");
        resp.EnsureSuccessStatusCode();
        var meta = await resp.Content.ReadFromJsonAsync<DifyDatasetMetadataResponse>();
        return meta?.DocMetadata
            .Select(f => new KnowledgeMetadataFieldInfo(f.Name, f.Type))
            .ToList() ?? [];
    }

    // Метаданные документа → строковый словарь (пустые/null значения отбрасываем).
    private static IReadOnlyDictionary<string, string>? MapMetadata(Dictionary<string, JsonElement>? meta)
    {
        if (meta is null || meta.Count == 0) return null;
        var result = new Dictionary<string, string>();
        foreach (var (key, value) in meta)
        {
            var s = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => value.GetRawText(),
            };
            if (!string.IsNullOrWhiteSpace(s)) result[key] = s;
        }
        return result.Count == 0 ? null : result;
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

    private static string GetMimeType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".xls"  => "application/vnd.ms-excel",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".pdf"  => "application/pdf",
            ".csv"  => "text/csv",
            ".epub" => "application/epub+zip",
            _       => "application/octet-stream",
        };
    }

    public async Task<DifyDocumentInfo> IndexFileByBytesAsync(
        string datasetId, string fileName, byte[] content, List<string>? tags = null)
    {
        var client = CreateClient();
        using var form = new MultipartFormDataContent();

        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(GetMimeType(fileName));
        form.Add(fileContent, "file", Path.GetFileName(fileName));

        form.Add(new StringContent(System.Text.Json.JsonSerializer.Serialize(new
        {
            indexing_technique = _cfg.IndexingTechnique,
            process_rule = new { mode = "automatic" },
        })), "data");

        var resp = await client.PostAsync($"datasets/{datasetId}/document/create_by_file", form);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Dify вернул {(int)resp.StatusCode}: {errBody}");
        }

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

    // Все датасеты рабочего пространства Dify (id + имя), с обходом пагинации.
    // Имя несёт префикс владельца («{username}:…») — фильтрацию по нему делает вызывающий.
    public async Task<IReadOnlyList<DifyDatasetListItem>> ListDatasetsAsync()
    {
        if (!IsConfigured) return [];
        const int pageSize = 100;
        var all = new List<DifyDatasetListItem>();
        var client = CreateClient();
        var page = 1;
        while (true)
        {
            var resp = await client.GetAsync($"datasets?page={page}&limit={pageSize}");
            resp.EnsureSuccessStatusCode();
            var p = await resp.Content.ReadFromJsonAsync<DifyDatasetsPage>();
            if (p is null) break;
            all.AddRange(p.Data);
            if (!p.HasMore || p.Data.Count == 0) break;
            page++;
        }
        return all;
    }

    // Возвращает ВСЕ документы датасета, обходя пагинацию Dify (одна страница ограничена).
    // Без этого статус БЗ показывал только первую страницу, и недавно добавленные документы
    // в крупных датасетах (>limit) были не видны на вкладке знаний.
    public async Task<DifyDocumentsPage> ListAllDocumentsAsync(string datasetId)
    {
        const int pageSize = 100;
        var all = new List<DifyDocumentItem>();
        var page = 1;
        var total = 0;
        while (true)
        {
            var p = await ListDocumentsAsync(datasetId, page, pageSize);
            all.AddRange(p.Data);
            total = p.Total;
            if (!p.HasMore || p.Data.Count == 0) break;
            page++;
        }
        return new DifyDocumentsPage(all, false, total);
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
