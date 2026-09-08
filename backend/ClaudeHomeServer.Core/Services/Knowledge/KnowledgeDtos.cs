using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Services.Knowledge;

// Возвращаемые типы методов интерфейса IKnowledgeIndex (Core). Вынесены из
// KnowledgeService.cs (Main), чтобы контракт мог быть объявлен в Core
// без обратной зависимости. Все шесть методов интерфейса возвращают bool,
// Task, Task<string>, Task<IReadOnlyList<DifyRetrieveChunk>> или
// Task<DifyDocumentInfo> — поэтому в Core нужны эти четыре типа (остальные
// DTO — DifyDocumentItem, DifySegmentItem, DifyDatasetListItem и т.п. —
// остаются внутренними деталями KnowledgeService).
public record DifyDocumentInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("indexing_status")] string IndexingStatus);

// Чанк результата поиска. Metadata — структурные метаданные документа-источника
// (дата встречи, id, источник и т.п.), приведённые к строкам; null/пусто — их нет.
public record DifyRetrieveChunk(string Content, double Score, string DocumentId, string DocumentName,
    IReadOnlyDictionary<string, string>? Metadata = null);

// Условие фильтрации по метаданным. Op — строковый оператор Dify (contains, not contains,
// start with, end with, is, is not, empty, not empty). Value не нужен для empty/not empty.
// Тип переехал из KnowledgeService.cs (Main) — IKnowledgeIndex.RetrieveAsync в Core
// принимает IReadOnlyList<KnowledgeMetadataFilter>?.
public record KnowledgeMetadataFilter(string Name, string Op, string? Value);

// Имя + тип поля метаданных (string/number/time) — переехал из KnowledgeService.cs
// ради IKnowledgeIndex.CreateDatasetAsync, который опционально принимает
// IReadOnlyList<KnowledgeMetadataFieldInfo>?.
public record KnowledgeMetadataFieldInfo(string Name, string Type);
