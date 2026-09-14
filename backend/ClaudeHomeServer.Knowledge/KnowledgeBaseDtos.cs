namespace ClaudeHomeServer.Services.Knowledge;

// DTO для REST-контроллера и wsp-тулсета Dify (KnowledgeBaseCatalogService).
// Раньше лежали в KnowledgeBasesController.cs, перенесены ради выноса Knowledge
// в отдельный .csproj (Этап 5, волна 5).

/// <summary>Сводка базы знаний — список.</summary>
public record KnowledgeBaseSummary(
    string Id, string Title, string Type, string Visibility,
    int DocumentCount, DateTime? CreatedAt, bool Deletable, string? Description);

/// <summary>Документ внутри базы знаний (для детальной карточки).</summary>
public record KnowledgeDocumentDto(string Id, string Name, string IndexingStatus, string? Error = null);

/// <summary>Детальная карточка базы со списком документов.</summary>
public record KnowledgeBaseDetail(
    string Id, string Title, string Type, string Visibility,
    int DocumentCount, DateTime? CreatedAt, bool Deletable, string? Description,
    IReadOnlyList<KnowledgeDocumentDto> Documents);
