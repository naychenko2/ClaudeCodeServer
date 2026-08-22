namespace ClaudeHomeServer.Models;

// Устаревание якоря (этап 2 — ранжирование recall); на этапе 1 всегда Active,
// кроме SummaryFailed-скелетов, которые из recall исключаются собственным флагом.
public enum DossierStatus { Active, Degraded, Archived }

// Происхождение записи (этап 4 — импорт «Историй решений»): Own — паспорт рождён при
// коммите из чата/задачи этого инстанса; Imported — прочитан из ветки ccs/dossiers/v1
// (восстановление на новой машине, чужая общая папка). Старые записи без поля читаются
// как Own — дефолт значения enum.
public enum DossierOrigin { Own, Imported }

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

    // Момент снятия паспорта (захват, не сам коммит): DossierStore.Add ставит UtcNow
    // для новых own-записей; у Imported и у записей до появления поля — null (неизвестен).
    public DateTimeOffset? CapturedAt { get; set; }

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

    // Происхождение (этап 4): у Imported заполнены автор и ветка-источник — tip-коммит
    // ветки, из которой запись приехала (GitDossiersTip). У Own оба поля null.
    public DossierOrigin Origin { get; set; } = DossierOrigin.Own;
    public string? ImportedAuthor { get; set; }
    public string? ImportedFromBranch { get; set; }
}
