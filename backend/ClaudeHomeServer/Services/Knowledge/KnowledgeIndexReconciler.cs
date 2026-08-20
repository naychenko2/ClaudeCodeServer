namespace ClaudeHomeServer.Services.Knowledge;

// Настройки реконсайлера error-документов Dify (секция Dify:Reconcile).
// Mode — off (дефолт, dark launch: дев-стенд на копии боевого data не должен лечить
// боевые датасеты) | observe (только читает и считает, сторы не трогает — базовая
// цифра для критерия приёмки ДО первой мутации) | heal (лечит).
public sealed record KnowledgeReconcileOptions(
    string Mode,
    TimeSpan Interval,          // тик сервиса
    TimeSpan TargetInterval,    // базовый период обхода одного датасета
    int MaxPerCycle,            // потолок инвалидаций за тик (на все цели)
    TimeSpan MaxBackoff,        // потолок личного периода цели
    int MaxAttemptsPerEntry)    // попыток лечения записи до карантина (временные ошибки)
{
    public const string Section = "Dify:Reconcile";

    public static KnowledgeReconcileOptions Read(IConfiguration config)
    {
        var s = config.GetSection(Section);
        return new KnowledgeReconcileOptions(
            Mode: (s["Mode"] ?? "off").Trim().ToLowerInvariant(),
            Interval: ReadSpan(s, "Interval", TimeSpan.FromMinutes(5)),
            TargetInterval: ReadSpan(s, "TargetInterval", TimeSpan.FromMinutes(15)),
            MaxPerCycle: int.TryParse(s["MaxPerCycle"], out var mpc) && mpc > 0 ? mpc : 100,
            MaxBackoff: ReadSpan(s, "MaxBackoff", TimeSpan.FromHours(2)),
            MaxAttemptsPerEntry: int.TryParse(s["MaxAttemptsPerEntry"], out var maxA) && maxA > 0 ? maxA : 5);
    }

    private static TimeSpan ReadSpan(IConfiguration s, string key, TimeSpan fallback) =>
        TimeSpan.TryParse(s[key], System.Globalization.CultureInfo.InvariantCulture, out var v) && v > TimeSpan.Zero
            ? v : fallback;
}

// Состояние цели после обхода — для видимости (шаг 4) и тестов
public sealed record ReconcileTargetStatus(string Label, int Healable, int Unhealable);

// Фоновый реконсайлер error-документов Dify (план «восстановление error-документов»,
// вариант B). CCS принимает подтверждение ПРИЁМА документа за подтверждение ИНДЕКСАЦИИ:
// {DocId, Hash} пишется в стор сразу после create, и документ, упавший на эмбеддингах
// (статус error), для дифф-синка невидим навсегда — хеш совпадает. Реконсайлер замыкает
// уже существующую идемпотентную петлю: находит error-документы (?status=error),
// сопоставляет со сторами участников (ResolveAsync), сбрасывает хеши (InvalidateAsync,
// Hash="") и пинает штатный синк (KickSync) — тот сам удаляет error-док и пересоздаёт
// документ из источника истины. Состояние живёт в самом Dify (список error-доков —
// персистентная очередь ретраев), нового хранилища нет.
//
// - Backoff per-target: у каждой цели свой NextDueAt; число healable не уменьшилось —
//   личный период ×2 до MaxBackoff, уменьшилось — сброс. Сироты (unhealable) в расчёт
//   не входят — иначе вечный MaxBackoff из-за неустранимого «пола».
// - Recovered — по ИСЧЕЗНОВЕНИЮ ключа из error-множества на следующем обходе, не по
//   попытке (иначе это счётчик попыток, при лежащем провайдере он раздувается кратно).
// - Карантин «ядовитых» записей: in-memory счётчик попыток по «Label:EntryKey» (по DocId
//   нельзя — он меняется при каждом пересоздании). Временная ошибка (провайдер) — до
//   MaxAttemptsPerEntry попыток, прочее (вероятно контентная) — до 2; дальше ключ
//   отбрасывается сразу после ResolveAsync, до мутации. Карантин — до рестарта.
// - Видимость (шаг 4): снимок LastCounts/RecoveredTotal читают гейджи телеметрии, а
//   владельцам целей уходит уведомление, когда healable-ошибки держатся ≥2 обходов
//   подряд — не чаще раза в сутки на владельца (дедуп in-memory: после рестарта
//   уведомление может продублироваться, осознанный компромисс плана).
public sealed class KnowledgeIndexReconciler : BackgroundService
{
    // Сколько обходов подряд ошибки должны держаться, чтобы беспокоить владельца:
    // одиночный провал лечится сам следующим синком, сообщать о нём нечего
    private const int RoundsBeforeNotify = 2;

    private static readonly TimeSpan NotifyCooldown = TimeSpan.FromHours(24);

    private readonly KnowledgeService _knowledge;
    private readonly IReadOnlyList<IKnowledgeSyncParticipant> _participants;
    private readonly IConfiguration _config;
    private readonly ILogger<KnowledgeIndexReconciler> _log;
    private readonly TimeProvider _time;
    private readonly IKnowledgeAlertNotifier? _notifier;

    // Личное состояние цели (ключ — Label): период, срок следующего обхода, прошлый срез
    private sealed class TargetState
    {
        public DateTimeOffset NextDueAt = DateTimeOffset.MinValue;   // первый обход — сразу
        public TimeSpan CurrentInterval;
        public int LastHealableCount = -1;                            // -1 — ещё не обходили
        public HashSet<string> PrevErrorKeys = new();                 // healable-ключи прошлого обхода
        public int HealableRounds;                                    // обходов подряд с healable-ошибками
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, TargetState> _targets = new();
    private readonly Dictionary<string, int> _attempts = new();       // «Label:EntryKey» → попыток
    private readonly HashSet<string> _quarantine = new();
    private readonly Dictionary<string, ReconcileTargetStatus> _lastCounts = new();
    private readonly Dictionary<string, DateTimeOffset> _notifiedAt = new();   // userId → когда уведомляли
    private long _recovered;
    private string _lastMode = "off";

    public KnowledgeIndexReconciler(KnowledgeService knowledge,
        IEnumerable<IKnowledgeSyncParticipant> participants, IConfiguration config,
        ILogger<KnowledgeIndexReconciler> log, TimeProvider? timeProvider = null,
        IKnowledgeAlertNotifier? notifier = null)
    {
        _knowledge = knowledge;
        _participants = participants.ToList();
        _config = config;
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
        _notifier = notifier;
    }

    // Снимок для видимости (шаг 4) и тестов
    public long RecoveredTotal { get { lock (_gate) return _recovered; } }
    public IReadOnlyList<ReconcileTargetStatus> LastCounts
    {
        get { lock (_gate) return _lastCounts.Values.ToList(); }
    }
    public IReadOnlyCollection<string> QuarantinedKeys
    {
        get { lock (_gate) return _quarantine.ToList(); }
    }

    // Следующий личный период цели — чистая функция (тесты backoff не тайминговые).
    // Healable не уменьшилось (и есть что лечить) — период ×2 до потолка, иначе сброс к базе.
    internal static TimeSpan NextInterval(TimeSpan current, KnowledgeReconcileOptions opts,
        int lastHealable, int healableNow)
    {
        if (lastHealable < 0 || healableNow == 0 || healableNow < lastHealable)
            return opts.TargetInterval;
        var doubled = current * 2;
        return doubled > opts.MaxBackoff ? opts.MaxBackoff : doubled;
    }

    // Срез для гейджа: суммы по типу датасета (префикс Label — notes/persona/team/dossiers/
    // project) и по лечимости. Именно суммы, а не по цели: Label содержит id персоны и путь
    // проекта — как тег это и PII, и кардинальность.
    internal static IReadOnlyList<(string DatasetType, bool Healable, long Count)> AggregateByType(
        IReadOnlyList<ReconcileTargetStatus> counts)
    {
        var acc = new Dictionary<(string, bool), long>();
        foreach (var c in counts)
        {
            var colon = c.Label.IndexOf(':');
            var type = colon > 0 ? c.Label[..colon] : c.Label;
            acc[(type, true)] = acc.GetValueOrDefault((type, true)) + c.Healable;
            acc[(type, false)] = acc.GetValueOrDefault((type, false)) + c.Unhealable;
        }
        return acc.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList();
    }

    // Временная ошибка индексации (лежит провайдер эмбеддингов) — ретраи оправданы дольше,
    // чем при контентной (документ падает сам по себе и будет падать снова)
    internal static bool IsTransientError(string error) =>
        error.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
        || error.Contains("timeout", StringComparison.OrdinalIgnoreCase)
        || error.Contains("timed out", StringComparison.OrdinalIgnoreCase)
        || error.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
        || error.Contains("unavailable", StringComparison.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = KnowledgeReconcileOptions.Read(_config).Interval;
        using var timer = new PeriodicTimer(interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await TickAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _log.LogWarning(ex, "reconcile: тик не удался"); }
            }
        }
        catch (OperationCanceledException) { /* остановка хоста */ }
    }

    // Один проход по всем целям, у которых наступил срок. Публичный — тесты зовут напрямую,
    // без таймера. Mode читается на каждом тике (горячая смена без рестарта); смена режима
    // сбрасывает backoff-состояние: в observe healable по определению не уменьшается, и без
    // сброса к включению heal все цели доползли бы до MaxBackoff — бэкфилл стартовал бы вяло.
    public async Task TickAsync(CancellationToken ct = default)
    {
        var opts = KnowledgeReconcileOptions.Read(_config);
        lock (_gate)
        {
            if (opts.Mode != _lastMode)
            {
                _targets.Clear();
                _lastMode = opts.Mode;
            }
        }
        if (opts.Mode is not ("observe" or "heal") || !_knowledge.IsConfigured) return;

        var budget = opts.MaxPerCycle;
        foreach (var participant in _participants)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<KnowledgeSyncTarget> targets;
            try { targets = participant.ListTargets(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "reconcile: участник {Type} не отдал цели", participant.GetType().Name);
                continue;
            }
            // Упавшая цель не обрывает тик — остальные датасеты обходятся дальше
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                try { budget = await ProcessTargetAsync(target, opts, budget, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log.LogWarning(ex, "reconcile: цель {Label}", target.Label); }
            }
        }
    }

    private async Task<int> ProcessTargetAsync(KnowledgeSyncTarget target,
        KnowledgeReconcileOptions opts, int budget, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        TargetState st;
        lock (_gate)
        {
            if (!_targets.TryGetValue(target.Label, out st!))
                _targets[target.Label] = st = new TargetState { CurrentInterval = opts.TargetInterval };
            if (now < st.NextDueAt) return budget;
        }

        // Судьба документов — из самого Dify; фильтр по статусу страхуем клиентской
        // проверкой (старая версия могла проигнорировать ?status=)
        var page = await _knowledge.ListAllDocumentsAsync(target.DatasetId, status: "error");
        ct.ThrowIfCancellationRequested();
        var errorDocs = page.Data
            .Where(d => string.Equals(d.IndexingStatus, "error", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Сопоставление со стором участника: нашлись — healable, остальные — сироты
        var resolved = await target.ResolveAsync(errorDocs.Select(d => d.Id).ToList());
        var healableKeys = resolved.Select(r => r.EntryKey).ToHashSet(StringComparer.Ordinal);
        var unhealable = errorDocs.Count - resolved.Count;

        List<(string DocId, string EntryKey)> toHeal = new();
        List<string> notifyOwners = new();
        var healMode = opts.Mode == "heal";
        lock (_gate)
        {
            // Recovered — ключи, исчезнувшие из error-множества с прошлого обхода;
            // их счётчики попыток больше не нужны
            foreach (var key in st.PrevErrorKeys.Where(k => !healableKeys.Contains(k)))
            {
                _recovered++;
                _attempts.Remove($"{target.Label}:{key}");
            }
            _lastCounts[target.Label] = new ReconcileTargetStatus(target.Label, healableKeys.Count, unhealable);

            st.CurrentInterval = NextInterval(st.CurrentInterval, opts, st.LastHealableCount, healableKeys.Count);
            st.LastHealableCount = healableKeys.Count;
            st.PrevErrorKeys = healableKeys;
            st.NextDueAt = now + st.CurrentInterval;
            st.HealableRounds = healableKeys.Count > 0 ? st.HealableRounds + 1 : 0;

            if (unhealable > 0)
                _log.LogInformation("reconcile: {Label} — {Orphans} error-документов без записи в сторе (сироты)",
                    target.Label, unhealable);

            // Владельцу сообщаем и в observe: видимость от режима лечения не зависит.
            // Отметку времени ставим здесь же, под локом — иначе два датасета одного
            // владельца в одном тике дали бы два уведомления.
            if (st.HealableRounds >= RoundsBeforeNotify)
            {
                foreach (var userId in target.OwnerUserIds)
                {
                    if (_notifiedAt.TryGetValue(userId, out var last) && now - last < NotifyCooldown) continue;
                    _notifiedAt[userId] = now;
                    notifyOwners.Add(userId);
                }
            }

            // Карантин отбрасывается ДО мутации; классификация по тексту ошибки Dify
            var errorByDoc = errorDocs.ToDictionary(d => d.Id, d => d.Error ?? "");
            foreach (var pair in resolved)
            {
                if (!healMode || toHeal.Count >= budget) break;
                var qk = $"{target.Label}:{pair.EntryKey}";
                if (_quarantine.Contains(qk)) continue;
                var attempts = _attempts.GetValueOrDefault(qk);
                var limit = IsTransientError(errorByDoc.GetValueOrDefault(pair.DocId, ""))
                    ? opts.MaxAttemptsPerEntry
                    : Math.Min(2, opts.MaxAttemptsPerEntry);
                if (attempts >= limit)
                {
                    _quarantine.Add(qk);
                    _log.LogWarning("reconcile: запись {Entry} в карантине после {Attempts} попыток (ошибка: {Error})",
                        qk, attempts, errorByDoc.GetValueOrDefault(pair.DocId, "—"));
                    continue;
                }
                _attempts[qk] = attempts + 1;
                toHeal.Add(pair);
            }
        }

        // Уведомление — вне лока (и до мутаций: оно от режима не зависит)
        if (notifyOwners.Count > 0) await NotifyOwnersAsync(notifyOwners, ct);

        if (toHeal.Count == 0) return budget;
        // Сброс хешей по отобранным ключам и пинок штатного синка — строго после
        // освобождения своих структур; локи участника берут его делегаты
        await target.InvalidateAsync(toHeal.Select(p => p.EntryKey).ToList());
        target.KickSync();
        _log.LogInformation("reconcile: {Label} — сброшено {Count} хешей, синк поставлен в очередь",
            target.Label, toHeal.Count);
        return budget - toHeal.Count;
    }

    // Текст без жаргона: владельцу важно знать, что поиск может врать, а не как
    // называется провайдер эмбеддингов
    private async Task NotifyOwnersAsync(IReadOnlyList<string> userIds, CancellationToken ct)
    {
        if (_notifier is null) return;
        foreach (var userId in userIds)
        {
            await _notifier.NotifyAsync(userId,
                "Часть знаний не проиндексирована",
                "Некоторые записи знаний и памяти не удалось проиндексировать — поиск и recall "
                    + "по ним могут быть неполными. Сервер повторяет попытки автоматически.",
                ct);
        }
    }
}
