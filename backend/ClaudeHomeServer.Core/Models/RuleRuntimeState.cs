namespace ClaudeHomeServer.Services;

// Runtime-состояние одного правила автоматизации. НЕ конфигурация — high-churn: обновляется
// на каждом тике/срабатывании, поэтому живёт отдельно от personas.json (в data/persona-automation-state.json),
// чтобы не переписывать конфиг персон и не дёргать OnPersonaChanged (который сбрасывает адаптеры сессий).
// Переживает рестарт; при удалении правила/персоны запись остаётся, но лениво не читается.
// (Перенесено в Core для TriggerSources: TriggerContext несёт это состояние,
// а TriggerSources — отдельная сборка, видящая только Core.)
public sealed class RuleRuntimeState
{
    public DateTime? LastFiredAt { get; set; }
    public DateTime? LastResultAt { get; set; }
    // "yes" | "no" | "throttled" | "quiet" | текст ошибки — для наблюдаемости (UI «последний результат»)
    public string? LastResult { get; set; }
    public int RunCount { get; set; }
    // Закреплённый чат правила: создаётся при первом срабатывании, переиспользуется далее.
    public string? SessionId { get; set; }
    // Per-source снапшоты для дифф-детекции (какой из них валиден — зависит от Trigger.Type правила):
    public Dictionary<string, string>? TaskStatusSnapshot { get; set; }  // taskId → status
    public string? LastGitHeadSha { get; set; }                          // HEAD проекта, который смотрит правило
    public Dictionary<string, string>? NoteHashes { get; set; }          // noteId → sha256(title\ntags\nupdatedAt)
    public Dictionary<string, long>? FileSnapshot { get; set; }          // rel → LastWriteTicks (glob-отфильтровано)
}
