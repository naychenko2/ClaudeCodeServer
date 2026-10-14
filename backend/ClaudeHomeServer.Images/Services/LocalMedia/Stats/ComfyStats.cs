using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Снимок медиа-стенда ComfyUI (IComfyStats, шаг 26): собирает из монитора очереди (running/pending/
// foreign/down/version), реестра (происхождение/ETA/исход) и стора (тонкие активные задачи) снимок
// для карточек редакторов и вкладки «Медиа». Статус: disabled (LocalMedia:Enabled=false) / down
// (ComfyUI недоступен) / busy (слоты полны ИЛИ идёт тяжёлая) / working. Чужие прогоны — только
// классификация монитора (instance/transcription/external): текст промптов и имена файлов не читаются
public sealed class ComfyStats(
    ComfyQueueMonitor monitor,
    ComfyPromptLedger ledger,
    LocalMediaJobStore store,
    ComfyProgressTracker? steps,
    IConfiguration config) : IComfyStats
{
    // Снимок медиа-стенда: running/queue/lastHour + слоты + статус + DownSince. null — стенд выключен.
    // Touch(): открытая вкладка «Медиа» опрашивает этот снимок — панель активна, очередь опрашиваем
    // с быстрым шагом (2 с) на время TouchWindow, а не с простым (15 с)
    public Task<ComfyMediaStatsSnapshot?> GetMediaAsync(CancellationToken ct)
    {
        monitor.Touch();
        return Task.FromResult(BuildSnapshot());
    }

    // Снимок для владельца X (карточки редакторов): свои задачи — полные (место, ETA старта, шаги),
    // чужие — только kind/heavy/остаток (без ownerId/sessionId/projectId/prompt/slug/prompt_id/времени)
    public Task<ComfyOwnerSnapshot?> GetForOwnerAsync(string ownerId, CancellationToken ct)
    {
        var full = BuildSnapshot();
        if (full is null) return Task.FromResult<ComfyOwnerSnapshot?>(null);
        ComfyOwnerSnapshot? result = BuildOwnerSnapshot(full, ownerId);
        return Task.FromResult<ComfyOwnerSnapshot?>(result);
    }

    // ─── Сборка полного снимка ──────────────────────────────────────────────────────────────

    private ComfyMediaStatsSnapshot? BuildSnapshot()
    {
        var options = LocalMediaOptions.Read(config);
        if (!options.Enabled)
            return new ComfyMediaStatsSnapshot(
                Configured: false,
                Status: "disabled",
                DownSince: null,
                Version: null,
                LoadedModels: [],
                SlotsUsed: 0,
                SlotsLimit: options.MaxComfyQueue,
                Gpus: [],
                Running: [],
                Queue: [],
                LastHour: [],
                Unsupported: []);

        var snap = monitor.Snapshot;
        var running = snap?.Running ?? [];
        var pending = snap?.Pending ?? [];
        var foreign = snap?.Foreign ?? [];
        var down = monitor.Down;
        var downSince = monitor.DownSince;
        var version = snap?.Version;

        // «Загружено сейчас» + карты узла — из кэша монитора (маршрут /ccs/stats, шаг 31).
        // CcsSupported — видели ли валидный ответ (version 1): нет → узел не сообщает,
        // пометка в unsupported, gpus/loadedModels пустые.
        var ccs = snap?.Ccs;
        var ccsHasData = ccs is { HasData: true };
        var loadedModels = ccsHasData ? ccs!.LoadedModels : [];
        var gpus = ccsHasData ? ccs!.Gpus : [];
        var unsupported = new List<string>();
        if (!monitor.CcsSupported)
            unsupported.Add("узел не сообщает загруженные модели");

        var slotsUsed = running.Count + pending.Count;
        var slotsLimit = options.MaxComfyQueue;

        var runningRows = running.Select(item => JobRow(item, foreign, pending, isRunning: true)).ToList();
        var queueRows = pending.Select((item, i) => JobRow(item, foreign, pending, isRunning: false, i)).ToList();
        var lastHour = ledger.Recent().Select(RecentRow).ToList();

        var heavyRunning = runningRows.Any(r => r.Heavy);
        var status = down ? "down" : (slotsUsed >= slotsLimit || heavyRunning ? "busy" : "working");

        return new ComfyMediaStatsSnapshot(
            Configured: true,
            Status: status,
            DownSince: downSince,
            Version: version,
            Gpus: gpus,
            LoadedModels: loadedModels,
            SlotsUsed: slotsUsed,
            SlotsLimit: slotsLimit,
            Running: runningRows,
            Queue: queueRows,
            LastHour: lastHour,
            Unsupported: unsupported);
    }

    // Строка прогона (идущая или в очереди): метаданные из реестра (origin) или стора (fallback),
    // чужие — классификация монитора. ETA старта — ComfyQueueEta (остаток идущей + Σ eta впереди)
    private ComfyJobRow JobRow(
        ComfyQueueItem item,
        IReadOnlyList<ComfyForeignRun> foreign,
        IReadOnlyList<ComfyQueueItem> pending,
        bool isRunning,
        int pendingIndex = -1)
    {
        // Метаданные: реестр (origin) → стор (fallback) → чужой (классификация)
        var (kind, op, heavy, ownerId, sessionId, eta) = ResolveMeta(item.PromptId);

        // Чужой прогон: классификация монитора (instance/transcription/external)
        var foreignKind = foreign.FirstOrDefault(f => f.PromptId == item.PromptId)?.Kind;
        var isForeign = foreignKind is not null;
        if (isForeign)
        {
            kind = ForeignKind(foreignKind!.Value);
            op = "";
            heavy = false;
            ownerId = "";
            sessionId = null;
        }

        // ETA старта: ComfyQueueEta (остаток идущей + Σ eta ждущих впереди)
        int? startIn = null;
        if (!isRunning)
        {
            ComfyQueueEta.Running? running = null;
            var runningItems = monitor.Snapshot?.Running;
            if (runningItems is { Count: > 0 } r)
            {
                var first = r[0];
                running = new ComfyQueueEta.Running(ResolveMeta(first.PromptId).Eta, LedgerStartedAt(first.PromptId));
            }
            var waiting = pending
                .Where(p => p.PromptId != item.PromptId)
                .Take(pendingIndex < 0 ? 0 : pendingIndex)
                .Select(p => new ComfyQueueEta.Waiting(ResolveMeta(p.PromptId).Eta))
                .ToList();
            startIn = ComfyQueueEta.StartInSeconds(running, TimeProvider.System.GetUtcNow(), waiting, pendingIndex);
        }

        // Шаги: только для идущих наших прогонов (ComfyProgressTracker)
        var progress = isRunning && !isForeign ? steps?.Get(item.PromptId) : null;

        // Остаток: eta − (now − startedAt), не меньше 0
        int? remaining = null;
        if (eta is { } e)
        {
            var started = LedgerStartedAt(item.PromptId);
            var elapsed = started is { } s ? Math.Max(0, (TimeProvider.System.GetUtcNow() - s).TotalSeconds) : 0;
            remaining = Math.Max(0, (int)(e - elapsed));
        }

        return new ComfyJobRow(
            Kind: kind,
            Op: op,
            Heavy: heavy,
            OwnerId: ownerId ?? "",
            SessionId: sessionId,
            ProjectId: null,
            Foreign: isForeign ? ForeignLabel(foreignKind!.Value) : null,
            Steps: progress is { } p ? new LocalGpuSteps(p.Step, p.Total, p.Stage) : null,
            Percent: progress?.Percent,
            PercentExact: progress is not null,
            EtaSeconds: eta,
            RemainingSeconds: remaining,
            StartInSeconds: startIn,
            StartedAt: LedgerStartedAt(item.PromptId));
    }

    // Метаданные прогона по prompt_id: реестр (origin) → стор (fallback) → null
    // Возвращает (kind, op, heavy, ownerId, sessionId, eta)
    private (string Kind, string Op, bool Heavy, string? OwnerId, string? SessionId, int? Eta) ResolveMeta(string promptId)
    {
        // Реестр: origin (kind/op/heavy/owner/session) + eta
        if (ledger.TryGet(promptId, out var record) && record is not null)
        {
            var origin = record.Origin;
            if (origin is not null)
                return (origin.Kind, origin.Op, origin.Heavy, origin.OwnerId, origin.SessionId, record.EtaSeconds);
            // Origin null (апскейл, редакторы до шага 28): fallback на стор
            var storeRec = store.ActiveSnapshot().FirstOrDefault(s => s.PromptId == promptId);
            if (storeRec is not null)
                return (storeRec.Kind, storeRec.Op, storeRec.Heavy, storeRec.OwnerId, storeRec.SessionId, record.EtaSeconds ?? storeRec.EtaSeconds);
            // Ни origin, ни store: eta из реестра, остальное неизвестно
            return ("other", "", false, null, null, record.EtaSeconds);
        }
        return ("other", "", false, null, null, null);
    }

    // Время начала прогона из реестра (первое наблюдение running)
    private DateTimeOffset? LedgerStartedAt(string promptId) =>
        ledger.TryGet(promptId, out var record) ? record?.StartedAt : null;

    // Завершённый за последний час прогон: kind/op/owner/исход/runSeconds/eta/finishedAt
    private ComfyDoneRow RecentRow(ComfyPromptRecord record)
    {
        var origin = record.Origin;
        var (kind, op, ownerId) = origin is not null
            ? (origin.Kind, origin.Op, origin.OwnerId)
            : ("other", "", "");
        return new ComfyDoneRow(
            Kind: kind,
            Op: op,
            OwnerId: ownerId,
            Outcome: record.Outcome ?? "failed",
            RunSeconds: record.RunSeconds,
            EtaSeconds: record.EtaSeconds,
            FinishedAt: record.CompletedAt ?? record.EnqueuedAt);
    }

    // ─── Снимок для владельца X ─────────────────────────────────────────────────────────────

    // Свои задачи — полные (как в GetMediaAsync), чужие — только kind/heavy/остаток
    private ComfyOwnerSnapshot BuildOwnerSnapshot(ComfyMediaStatsSnapshot full, string ownerId)
    {
        var own = new List<ComfyJobRow>();
        var foreign = new List<ComfyJobRow>();

        void Split(ComfyJobRow row)
        {
            if (row.OwnerId == ownerId)
                own.Add(row);
            else
                foreign.Add(new ComfyJobRow(
                    Kind: row.Kind,
                    Op: "",
                    Heavy: row.Heavy,
                    OwnerId: "",
                    SessionId: null,
                    ProjectId: null,
                    Foreign: row.Foreign,
                    Steps: null,
                    Percent: null,
                    PercentExact: false,
                    EtaSeconds: null,
                    RemainingSeconds: row.RemainingSeconds,
                    StartInSeconds: null,
                    StartedAt: null));
        }

        foreach (var row in full.Running) Split(row);
        foreach (var row in full.Queue) Split(row);

        return new ComfyOwnerSnapshot(
            Configured: full.Configured,
            Status: full.Status,
            DownSince: full.DownSince,
            SlotsUsed: full.SlotsUsed,
            SlotsLimit: full.SlotsLimit,
            Own: own,
            Foreign: foreign);
    }

    // ─── Хелперы: классификация чужих ───────────────────────────────────────────────────────

    // Вид чужого прогона по классификации: transcription → audio, instance/external → other
    private static string ForeignKind(ComfyForeignKind kind) => kind switch
    {
        ComfyForeignKind.Transcription => "audio",
        _ => "other",
    };

    // Метка чужого прогона для панели: instance | transcription | external
    private static string ForeignLabel(ComfyForeignKind kind) => kind switch
    {
        ComfyForeignKind.Instance => "instance",
        ComfyForeignKind.Transcription => "transcription",
        _ => "external",
    };
}

// Снимок для владельца X (карточки редакторов, шаг 26): свои задачи — полные (место, ETA старта,
// шаги), чужие — только kind/heavy/остаток. Сериализация наружу не утекает чужих данных
public sealed record ComfyOwnerSnapshot(
    bool Configured,
    string Status,
    DateTimeOffset? DownSince,
    int SlotsUsed,
    int SlotsLimit,
    IReadOnlyList<ComfyJobRow> Own,
    IReadOnlyList<ComfyJobRow> Foreign);
