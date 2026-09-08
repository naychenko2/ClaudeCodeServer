namespace ClaudeHomeServer.Models;

public class WorkspaceKnowledge
{
    public string RootPath { get; set; } = "";
    public string? DifyDatasetId { get; set; }
    public Dictionary<string, List<string>>? DocumentTags { get; set; }
    // Отслеживаемые файлы базы знаний: relativePath (нормализован '/') → документ Dify + хеш
    // содержимого. По этой карте ProjectKnowledgeSyncService синхронизирует правки/удаления/
    // переносы файлов. null у записей, созданных до фичи — карта бутстрапится из Dify при
    // первом синке (имя документа = относительный путь).
    public Dictionary<string, WorkspaceDocRef>? Docs { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// Ссылка «файл → документ Dify»: id документа + SHA-256 содержимого на момент индексации.
// Пустой Hash — принудительная переиндексация при следующем синке (bootstrap-дубли, перенос).
public class WorkspaceDocRef
{
    public string DocId { get; set; } = "";
    public string Hash { get; set; } = "";
}
