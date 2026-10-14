using System.Collections.Concurrent;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Запись реестра прогонов ComfyUI: кто поставил (Origin — шаг 6), оценка времени (EtaSeconds),
// когда встал в очередь (EnqueuedAt), когда начался (StartedAt) и чем закончился (Outcome,
// RunSeconds, CompletedAt). Потокобезопасность — ConcurrentDictionary; поля после EnqueuedAt
// ставит один поток (MarkStarted/Complete), гонки на них не критичны
public sealed class ComfyPromptRecord
{
    public string PromptId { get; }
    public LocalJobOrigin? Origin { get; }
    public int? EtaSeconds { get; }
    public DateTimeOffset EnqueuedAt { get; }
    public DateTimeOffset? StartedAt { get; internal set; }
    // completed | failed | cancelled
    public string? Outcome { get; internal set; }
    public int? RunSeconds { get; internal set; }
    public DateTimeOffset? CompletedAt { get; internal set; }

    internal ComfyPromptRecord(string promptId, LocalJobOrigin? origin, int? etaSeconds, DateTimeOffset enqueuedAt)
    {
        PromptId = promptId;
        Origin = origin;
        EtaSeconds = etaSeconds;
        EnqueuedAt = enqueuedAt;
    }
}

// Единый реестр prompt_id ComfyUI → происхождение/ETA/исход (в памяти): «чей этот прогон».
// Покрывает и прямые local_* (они ещё и в LocalMediaJobStore), и прогоны редакторов (картинки/
// звук/видео), которых в сторе нет. ComfyProgressTracker берёт из него «наш ли это prompt_id».
// Кольцо завершённых (Recent) хранит записи не старше RecentWindow: статистика смотрит свежее,
// а память не растёт без конца
public sealed class ComfyPromptLedger(TimeProvider? time = null)
{
    // Кольцо завершённых: не старше этого
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, ComfyPromptRecord> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ComfyPromptRecord> _recent = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // Постановка прогона: запись с EnqueuedAt. Повторная регистрация того же prompt_id
    // идемпотентна (существующая запись не затирается); повторная постановка после завершения
    // (запись уже в _recent) не теряет Origin: новый null при старом заданном — держим старый
    public void Register(string promptId, LocalJobOrigin? origin, int? etaSeconds)
    {
        if (string.IsNullOrEmpty(promptId)) return;
        if (origin is null && _recent.TryGetValue(promptId, out var recent) && recent.Origin is { } oldOrigin)
            origin = oldOrigin;
        _active.AddOrUpdate(promptId,
            _ => new ComfyPromptRecord(promptId, origin, etaSeconds, _time.GetUtcNow()),
            (_, existing) => existing);
        // Новый прогон того же prompt_id — старое завершение не в счёт
        _recent.TryRemove(promptId, out _);
    }

    // Прогон начался (первое наблюдение running). AddOrUpdate: запись времени начала идёт через
    // фабрику словара (атомарна), а не «TryGetValue и запись» (гонка двух наблюдателей);
    // повторный вызов не двигает время начала (StartedAt уже задан). Неизвестный id — no-op, как раньше
    public void MarkStarted(string promptId)
    {
        if (!_active.ContainsKey(promptId)) return;
        _active.AddOrUpdate(promptId,
            _ => throw new InvalidOperationException("MarkStarted: запись исчезла между проверкой и записью"),
            (_, record) =>
            {
                if (record.StartedAt is null) record.StartedAt = _time.GetUtcNow();
                return record;
            });
    }

    // Перенос в кольцо завершённых; кольцо хранит записи не старше RecentWindow
    public void Complete(string promptId, string outcome, int? runSeconds)
    {
        if (_active.TryRemove(promptId, out var record))
        {
            record.Outcome = NormalizeOutcome(outcome);
            record.RunSeconds = runSeconds;
            record.CompletedAt = _time.GetUtcNow();
            _recent[promptId] = record;
            Prune();
        }
    }

    // Исход прогона: только completed | failed | cancelled, иначе failed (защита от мусора)
    private static string NormalizeOutcome(string? outcome) =>
        outcome is "completed" or "failed" or "cancelled" ? outcome : "failed";

    // Активна и известна: трекер копит шаги только для таких prompt_id
    public bool Contains(string promptId) => _active.ContainsKey(promptId);

    // Есть хотя бы один активный прогон: слушатель прогресса держит сокет, пока есть кому слушать.
    // bool по _active, без аллокаций (Count у ConcurrentDictionary — O(1))
    public bool HasActive() => _active.Count > 0;

    // Запись (активная или из кольца); null — не знаем
    public bool TryGet(string promptId, out ComfyPromptRecord? record)
    {
        if (_active.TryGetValue(promptId, out record)) return true;
        return _recent.TryGetValue(promptId, out record);
    }

    // Активные прогоны, новые сверху
    public IReadOnlyList<ComfyPromptRecord> Active() =>
        _active.Values.OrderByDescending(r => r.EnqueuedAt).ToList();

    // Кольцо завершённых (не старше RecentWindow), новые сверху
    public IReadOnlyList<ComfyPromptRecord> Recent()
    {
        Prune();
        return _recent.Values.OrderByDescending(r => r.CompletedAt ?? r.EnqueuedAt).ToList();
    }

    // Чистка кольца: записи старше RecentWindow (по CompletedAt, а её нет — по EnqueuedAt)
    public void Prune()
    {
        var now = _time.GetUtcNow();
        foreach (var (id, record) in _recent)
        {
            var anchor = record.CompletedAt ?? record.EnqueuedAt;
            if (now - anchor > RecentWindow) _recent.TryRemove(id, out _);
        }
    }
}
