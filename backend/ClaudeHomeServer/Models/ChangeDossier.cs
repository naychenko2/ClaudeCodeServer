namespace ClaudeHomeServer.Models;

// Устаревание якоря (этап 2 — ранжирование recall); на этапе 1 всегда Active,
// кроме SummaryFailed-скелетов, которые из recall исключаются собственным флагом.
public enum DossierStatus { Active, Degraded, Archived }

// Паспорт изменения (ADR-004): AI-выжимка «зачем, что решили, что отвергли, какие грабли»,
// рождающаяся автоматически при коммите из чата/задачи. Ключ уникальности —
// OwnerId+ProjectId+CommitSha (плюс совпадение с любым из SupersededSha, см. DossierStore).
public class ChangeDossier
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string OwnerId { get; set; }
    public required string ProjectId { get; set; }
    public required string CommitSha { get; set; }
    public string CommitSubject { get; set; } = "";
    public DateTimeOffset CommittedAt { get; set; }

    // Прежние SHA после переякорения при squash/rebase (§7 ADR-004)
    public List<string> SupersededSha { get; set; } = [];

    // Ссылки — могут протухнуть (чат/задача удалены), сам паспорт от этого не страдает
    public string? SessionId { get; set; }
    public string? TaskId { get; set; }
    public string? PersonaId { get; set; }

    // Якоря: файлы — всегда (относительные пути), символы — только для C# (FQN типов)
    public List<string> Files { get; set; } = [];
    public List<string> Symbols { get; set; } = [];

    // Выжимка (JSON-контракт CheapTextRunner, ключ dossier-summary)
    public string Why { get; set; } = "";
    public List<string> Decisions { get; set; } = [];
    public List<string> Rejected { get; set; } = [];
    public List<string> Pitfalls { get; set; } = [];
    public List<string> Invariants { get; set; } = [];

    // Модель не ответила / JSON не распарсился — сохранён «скелет» (сообщение коммита +
    // якоря + ссылки), факт коммита не потерян, но в recall (этап 2) не участвует
    public bool SummaryFailed { get; set; }

    public string? DifyDocId { get; set; }
    public DossierStatus Status { get; set; } = DossierStatus.Active;
}
