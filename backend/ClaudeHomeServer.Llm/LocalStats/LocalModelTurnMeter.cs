using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Services.Turn;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Llm;

// Счётчик хода локальной модели (флаг local-model-stats): собирает метрики запросов хода
// (фазы, скорость, итог) и отдаёт живые события (OnLive, ≤1 Гц на сессию) и итог хода
// (OnTurn). Подписан на шину хода (TurnCompleted/SessionDeleted), как FallbackLlmSessionAdapter.
//
// Состояние — по sessionId: два параллельных чата не смешиваются. Сессию определяет по
// токену хода (turnId → sessionId, ownerId); запрос без сессии (токен не найден/отозван)
// считается «прочим» и в чат не идёт. Метрики выключены / нет приёмника — ничего не делает,
// исключений наружу нет.
//
// ILocalModelTurnMeter — второе имя того же singleton'а (см. регистрацию в LlmSubsystem):
// живые чаты на движке (ActiveChats) и итог последнего хода (GetLastTurn) — для панели
// «Локальные модели» и REST. Название/проект чата — через Core-шов ISessionDirectory
// (SessionManager в Main): шов необязателен, без него поля null.
public sealed class LocalModelTurnMeter : ILocalModelRequestSink, ILocalModelTurnMeter, IToolCallLeakSink
{
    private readonly TurnTokenService _tokens;
    private readonly ILocalModelStatsReader? _reader;
    private readonly ILocalModelLiveSink? _sink;
    private readonly IOptionsMonitor<LocalModelStatsOptions> _options;
    private readonly TimeProvider _time;
    private readonly ISessionDirectory? _sessionsDir;
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionState> _sessions = new();

    // Дроссель живых событий: не чаще 1 раза в секунду на сессию.
    private static readonly TimeSpan LiveThrottle = TimeSpan.FromSeconds(1);

    // Подстраховка от фантомного чата (M4): если сессия без активности (нет ни одного
    // запроса в работе и нет новых событий) дольше этого порога — не показываем в панели.
    // Нормальный ход завершается TurnCompleted, который чистит InWork; таймаут — только
    // на случай, если TurnCompleted не пришёл (процесс убит, событие потеряно).
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    // Minor 1 (финальное ревью): запрос ещё не получил первого токена (фаза queued/prefill) —
    // LastActivity не двигается (нет стрима), но чат нужен в панели. Порог 20 мин: длинный
    // prefill (150k) или ожидание в очереди vLLM не прячут чат. Для generating (есть первый
    // токен) — обычный IdleTimeout: OnTokenCount обновляет LastActivity на каждом токене.
    private static readonly TimeSpan PrefillIdleTimeout = TimeSpan.FromMinutes(20);

    // B3 (OOM-разведка 26.10): окно выгрузки idle-записей. _sessions чистился только по
    // SessionDeleted: архивированный (не удалённый) чат, хоть раз запросивший локальный движок,
    // держал запись навсегда (+1 запись на каждый когда-либо работавший на локали чат). Запись
    // без запроса в работе и без активности дольше этого окна удаляется целиком: панель она
    // давно не показывает (ActiveChats требует запрос в работе), а итог последнего хода живёт,
    // пока чат «живой», — то же наблюдаемое поведение, что после рестарта бэкенда («счётчик
    // ходов живёт в памяти», SessionLocalModelController). Окно равно толерантности silent
    // prefill (PrefillIdleTimeout): без активности 20 мин чат мёртв уже для панели.
    private static readonly TimeSpan IdleUnloadWindow = TimeSpan.FromMinutes(20);

    // M5: окно скользящей скорости генерации. Скорость = Δтокены / Δвремя по окну.
    // ~3 с: достаточно для сглаживания джиттера, коротко для отклика на смену скорости.
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(3);

    public LocalModelTurnMeter(
        TurnTokenService tokens,
        IOptionsMonitor<LocalModelStatsOptions> options,
        TimeProvider time,
        ILocalModelStatsReader? reader = null,
        ILocalModelLiveSink? sink = null,
        ISessionDirectory? sessionsDir = null)
    {
        _tokens = tokens;
        _options = options;
        _time = time;
        _reader = reader;
        _sink = sink;
        _sessionsDir = sessionsDir;
    }

    // Подписка на шину хода: ход завершён (итог) и сессия удалена (чистка состояния).
    public void Subscribe(ITurnEventBus events)
    {
        events.OnNotification<TurnCompleted>(OnTurnCompleted, "LocalModelTurnMeter.TurnCompleted");
        events.OnNotification<SessionDeleted>(OnSessionDeleted, "LocalModelTurnMeter.SessionDeleted");
    }

    // ─── ILocalModelRequestSink: шлюз отводит сюда копию SSE-потока запроса ───────────────
    //
    // Ключ учёта — requestId (уникален на HTTP-запрос, выдаёт шлюз): параллельные запросы
    // одного хода (сабагенты, боковые) не затирают друг друга. Привязка к сессии — через
    // turnId (один на процесс CLI): ResolveSession(turnId) → (sessionId, ownerId).

    // Запрос начался: фаза queued (есть ждущие) или prefill (нет), в работу.
    public void OnRequestStarted(string requestId, string turnId)
    {
        var (sessionId, ownerId) = ResolveSession(turnId);
        if (sessionId is null) return; // бесхозный: в чат не идёт
        var waiting = CurrentWaiting();
        var phase = waiting > 0 ? "queued" : "prefill";
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var s = GetOrAdd(sessionId, ownerId);
            s.InWork[requestId] = new RequestState
            {
                Phase = phase,
                StartedAt = now,
                QueuedStartAt = phase == "queued" ? now : null,
            };
            s.LastPhase = phase;
            s.LastActivity = now;
        }
        EmitLive(sessionId, phase, null, waiting);
    }

    // Первый токен: фаза generating; время, проведённое в queued, уходит в итог.
    public void OnFirstToken(string requestId, string turnId)
    {
        var (sessionId, ownerId) = ResolveSession(turnId);
        if (sessionId is null) return;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var s = GetOrAdd(sessionId, ownerId);
            if (s.InWork.TryGetValue(requestId, out var r))
            {
                if (r.QueuedStartAt is { } qs)
                    s.Acc.QueueMs = (s.Acc.QueueMs ?? TimeSpan.Zero) + (now - qs);
                r.Phase = "generating";
                r.FirstTokenAt = now;
                r.QueuedStartAt = null;
                s.LastPhase = "generating";
                s.LastActivity = now;
            }
        }
        EmitLive(sessionId, "generating", null, CurrentWaiting());
    }

    // Запрос завершился: итог RequestUsage накапливается в сумму хода, запрос снимается с работы.
    public void OnRequestCompleted(string requestId, string turnId, RequestUsage usage)
    {
        var (sessionId, ownerId) = ResolveSession(turnId);
        if (sessionId is null) return;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var s = GetOrAdd(sessionId, ownerId);
            if (s.InWork.Remove(requestId, out var r))
            {
                // Запрос так и не получил первый токен: всё время от старта — в queued.
                if (r.QueuedStartAt is { } qs)
                    s.Acc.QueueMs = (s.Acc.QueueMs ?? TimeSpan.Zero) + (now - qs);
                s.Acc.OutTokens = (s.Acc.OutTokens ?? 0) + (int)(usage.OutputTokens ?? 0);
                s.Acc.InputTokens += (int)(usage.InputTokens ?? 0);
                s.Acc.CacheRead += (int)(usage.CacheReadTokens ?? 0);
                s.Acc.CacheCreation += (int)(usage.CacheCreationTokens ?? 0);
                if (usage.Ttft is { } ttft)
                {
                    // TTFT хода = ttft запроса, который начался раньше всех (не минимальный ttft).
                    if (s.Acc.TtftMs is null || r.StartedAt < s.Acc.TtftStartedAt!.Value)
                    {
                        s.Acc.TtftMs = (int)ttft.TotalMilliseconds;
                        s.Acc.TtftStartedAt = r.StartedAt;
                    }
                }
                if (usage.GenDuration is { } gen)
                    s.Acc.GenDuration = (s.Acc.GenDuration ?? TimeSpan.Zero) + gen;
                s.LastActivity = now;
            }
        }
    }

    // M5: приращение токенов запроса во время стриминга. Наблюдатель шлёт накопленную
    // оценку токенов ЭТОГО запроса (по тексту дельт, не по числу дельт); мы добавляем
    // приращение в монотонный счётчик сессии (Minor 2: не сумма по активным — она падает
    // при завершении запроса) и пишем в скользящее окно. Скорость (EmitLive) — из окна:
    // Δтокены / Δвремя.
    //
    // ADR-029 (DFlash): наблюдатель оценивает токены по тексту (1 токен ≈ 4 символа),
    // а не масштабирует число дельт глобальным TokensPerStep (который ≠ per-request
    // токенов/дельту: замер 1.67–7.69). Оценка per-request, без глобального коэффициента.
    public void OnTokenCount(string requestId, string turnId, int cumulativeTokens)
    {
        var (sessionId, ownerId) = ResolveSession(turnId);
        if (sessionId is null) return;
        var now = _time.GetUtcNow();
        bool hasWork;
        // cumulativeTokens — уже оценка токенов (наблюдатель), не число дельт: коэффициент
        // не применяем. Клампуем в [0, 100000] от аномалий (сбой текста, отрицательное).
        var scaled = Math.Clamp(cumulativeTokens, 0, 100_000);
        lock (_gate)
        {
            var s = GetOrAdd(sessionId, ownerId);
            if (s.InWork.TryGetValue(requestId, out var r))
            {
                // Minor 2: приращение токенов ЭТОГО запроса → монотонный счётчик сессии.
                // Завершённый запрос (вне InWork) не меняет счётчик: его вклад заморожен.
                var delta = scaled - r.TokenCount;
                r.TokenCount = scaled;
                s.SessionTotalTokens += delta;
            }
            // Записываем точку в окно; prune старых (> SpeedWindow).
            s.TokenWindow.Add((now, s.SessionTotalTokens));
            var cutoff = now - SpeedWindow;
            while (s.TokenWindow.Count > 0 && s.TokenWindow[0].Time < cutoff)
                s.TokenWindow.RemoveAt(0);
            s.LastActivity = now;
            hasWork = s.InWork.Count > 0;
        }
        // M5: живое событие 1 Гц — только из OnTokenCount (единственный источник
        // приращения). EmitLive вне lock (сам берёт _gate): троттлинг 1 Гц внутри.
        if (hasWork)
            EmitLive(sessionId, "generating", ComputeLiveGenTps(sessionId), CurrentWaiting());
    }

    // Подсказочные заголовки (GatewayHintHeaders, ADR-029): классифицируем запрос хода
    // (основной / сабагент / компакция / боковой) и запоминаем типы сабагентов + вид
    // компакции. Вызывается шлюзом сразу после OnRequestStarted, только когда наблюдатель
    // создан (локальный маршрут + метрики включены). Сессия без токена хода — бесхозная,
    // подсказки в чат не идут (тот же приём, что у остальных колбэков).
    public void OnRequestHints(string requestId, string turnId, GatewayHintHeaders hints)
    {
        var (sessionId, ownerId) = ResolveSession(turnId);
        if (sessionId is null) return;
        lock (_gate)
        {
            var s = GetOrAdd(sessionId, ownerId);
            var acc = s.Acc;
            switch (hints.RequestClass)
            {
                case "main": acc.MainRequests++; break;
                case "subagent":
                case "workflow": acc.SubagentRequests++; break;
                case "compaction": acc.CompactionRequests++; break;
                case "auxiliary": acc.AuxiliaryRequests++; break;
            }
            if (hints.AgentType is { } at && !acc.AgentTypes.Contains(at))
                acc.AgentTypes.Add(at);
            // Вид компакции: сам запрос-суммаризация, иначе первый основной запрос после неё.
            if (hints.Compaction is { } c) acc.CompactionKind = c;
            else if (hints.ContextCompacted is { } cc) acc.CompactionKind ??= cc;
        }
    }

    // IToolCallLeakSink: ClaudeSession детектировал утечку вызова инструмента в текст в ходе
    // этой сессии. Копим в накопитель текущего хода; итог уходит в LocalModelTurnSummary.
    // Сессия не в _sessions (не локальная, или ход ещё не стартовал) — no-op: счётчик не копит.
    public void NoteLeak(string sessionId, string kind)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var s)) return;
            switch (kind)
            {
                case "tail": s.Acc.ToolCallLeakTail++; break;
                case "whole_call": s.Acc.ToolCallLeakWholeCall++; break;
            }
        }
    }

    // M5: живая скорость из скользящего окна: Δтокены / Δвремя. null — нет приращения
    // (окно пусто или одна точка). Не 0: нулевая скорость = «нет данных», не «0 ток/с».
    private double? ComputeLiveGenTps(string sessionId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var s) || s.TokenWindow.Count < 2)
                return null;
            var first = s.TokenWindow[0];
            var last = s.TokenWindow[^1];
            var dt = (last.Time - first.Time).TotalSeconds;
            if (dt <= 0) return null;
            var dTokens = last.TotalTokens - first.TotalTokens;
            if (dTokens <= 0) return null; // нет приращения → null, не 0
            return (double)dTokens / dt;
        }
    }

    // ─── Шина хода ─────────────────────────────────────────────────────────────────────────

    // Ход завершён: итог = сумма накопленного, живём дальше (накопитель сбрасывается).
    // Итог храним в состоянии сессии (LastSummary) — GetLastTurn отдаёт его после рестарта
    // живых событий, а пока ходов не было — null.
    // Тумблер выключен (Enabled=false) — итог не шлём и не храним (как EmitLive): GetLastTurn
    // отдаёт null; накопитель всё равно сбрасывается, чтобы следующий (включённый) ход начал
    // с чистого листа.
    // InWork очищается БЕЗУСЛОВНО (M4): если оборвался первый запрос хода (кнопка «Стоп»),
    // фантомный чат не должен висеть в панели.
    private Task OnTurnCompleted(TurnCompleted e)
    {
        var enabled = _options.CurrentValue.Enabled;
        var sessionId = e.Turn.SessionId;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var s)) return Task.CompletedTask;
            if (s.Acc.OutTokens is not null || s.Acc.GenDuration is not null)
            {
                if (enabled)
                {
                    var summary = BuildSummary(s.Acc);
                    _sink?.OnTurn(sessionId, summary);
                    s.LastSummary = summary;
                }
            }
            s.Acc = new TurnAccumulator();
            s.InWork.Clear();
            s.TokenWindow.Clear();
            s.SessionTotalTokens = 0;
            // B3: конец хода — активность чата: иначе ход без завершённых запросов в конце
            // (долгий инструмент без обращений к движку) выгрузил бы свой итог сразу, не прожив
            // idle-окна. После этой отметки запись становится idle и выгружается по окну.
            s.LastActivity = _time.GetUtcNow();
            // Конец хода — момент, когда записи становятся idle: вычищаем и чужие, на занятом
            // сервере выгрузка идёт с каждым ходом, без чтения панели.
            SweepIdleLocked();
        }
        return Task.CompletedTask;
    }

    // Сессия удалена: чистим её состояние.
    private Task OnSessionDeleted(SessionDeleted e)
    {
        lock (_gate)
            _sessions.Remove(e.Turn.SessionId);
        return Task.CompletedTask;
    }

    // ─── Вспомогательное ───────────────────────────────────────────────────────────────────

    // Сессия по токену хода: (sessionId, ownerId) или (null, null) — бесхозный.
    private (string? SessionId, string? OwnerId) ResolveSession(string turnId) =>
        _tokens.FindGrant(turnId) is { } g ? (g.SessionId, g.OwnerId) : (null, null);

    // Сколько прогонов ждут GPU в общей очереди (0 — нет данных/движок не настроен).
    private int CurrentWaiting() => _reader?.Current.Waiting ?? 0;

    // Выгрузка idle-записей (под _gate, B3): критерий — нет запроса в работе И нет активности
    // дольше IdleUnloadWindow. Активные записи (запрос в работе) не трогаются: их InWork/
    // TokenWindow — данные живой панели. Записи создаются только запросом к движку, поэтому
    // выгрузка на конце хода и на точках чтения ограничивает их число: на пустом сервере оно
    // не растёт (новых записей нет), на занятом — чистится с каждым ходом, без чтения панели.
    private void SweepIdleLocked()
    {
        var now = _time.GetUtcNow();
        List<string>? doomed = null;
        foreach (var (sessionId, s) in _sessions)
            if (s.InWork.Count == 0 && now - s.LastActivity > IdleUnloadWindow)
                (doomed ??= []).Add(sessionId);
        if (doomed is null) return;
        foreach (var id in doomed)
            _sessions.Remove(id);
    }

    // Живое событие: не чаще 1 раза в секунду на сессию, только пока есть запрос в работе.
    private void EmitLive(string sessionId, string phase, double? genTps, int waiting)
    {
        if (_sink is null) return;
        if (!_options.CurrentValue.Enabled) return;
        lock (_gate)
        {
            var s = GetOrAdd(sessionId);
            if (s.InWork.Count == 0) return;
            var now = _time.GetUtcNow();
            if (now - s.LastLive < LiveThrottle) return;
            s.LastLive = now;
        }
        _sink.OnLive(sessionId, phase, genTps, waiting);
    }

    private SessionState GetOrAdd(string sessionId, string? ownerId = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var s))
            _sessions[sessionId] = s = new SessionState();
        if (s.OwnerId is null && ownerId is not null)
            s.OwnerId = ownerId;
        return s;
    }

    // ─── ILocalModelTurnMeter: живой вид для панели «Локальные модели» и REST ─────────────

    // Чаты с запросом в работе на локальном движке. OwnerId — из токена хода (grant),
    // название/проект — через ISessionDirectory (SessionManager); без шва — null.
    // GenTps — средняя скорость завершённых запросов текущего хода (токены/сек);
    // null — первый запрос ещё не завершён.
    public IReadOnlyList<TurnChatLive> ActiveChats()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            // B3: точка чтения панели — триггер выгрузки idle-записей.
            SweepIdleLocked();
            var list = new List<TurnChatLive>(_sessions.Count);
            foreach (var (sessionId, s) in _sessions)
            {
                if (s.InWork.Count == 0) continue;
                // Подстраховка (M4): запрос в работе, но без активности > порога —
                // фантом (процесс убит, TurnCompleted не пришёл). Не показываем.
                // Minor 1: prefill/queued (нет первого токена) — порог 20 мин (длинный prefill);
                // generating (есть первый токен) — 5 мин (OnTokenCount обновляет LastActivity).
                var hasFirstToken = s.InWork.Values.Any(r => r.FirstTokenAt is not null);
                var timeout = hasFirstToken ? IdleTimeout : PrefillIdleTimeout;
                if (now - s.LastActivity > timeout) continue;
                var info = _sessionsDir?.GetById(sessionId);
                list.Add(new TurnChatLive(
                    sessionId,
                    s.OwnerId ?? "",
                    info?.ProjectId,
                    info?.Name,
                    CurrentGenTps(s.Acc),
                    s.LastPhase));
            }
            return list;
        }
    }

    // Итог последнего завершённого хода сессии; null — ходов не было (или сессия не известна,
    // в т.ч. выгружена по idle-окну, B3: та же семантика, что после рестарта бэкенда).
    public LocalModelTurnSummary? GetLastTurn(string sessionId)
    {
        lock (_gate)
        {
            // B3: тот же критерий, что у ActiveChats: idle-запись выгружается до чтения.
            SweepIdleLocked();
            return _sessions.TryGetValue(sessionId, out var s) ? s.LastSummary : null;
        }
    }

    // Текущая скорость чата: средняя по завершённым запросам хода (output/генерация).
    // Та же формула, что в BuildSummary; null — данных пока нет.
    private static double? CurrentGenTps(TurnAccumulator acc)
    {
        if (acc.OutTokens is int outTokens && acc.GenDuration is { } gen && gen > TimeSpan.Zero)
            return outTokens / gen.TotalSeconds;
        return null;
    }

    // Итог хода из накопленного: GenTps = output/генерация, CacheHitPerc по сумме токенов,
    // QueueMsApprox ≈ (округлено вверх до секунды).
    private static LocalModelTurnSummary BuildSummary(TurnAccumulator acc)
    {
        double? genTps = null;
        if (acc.OutTokens is int outTokens && acc.GenDuration is { } gen && gen > TimeSpan.Zero)
            genTps = outTokens / gen.TotalSeconds;

        double? cacheHit = null;
        var total = acc.InputTokens + acc.CacheRead + acc.CacheCreation;
        if (total > 0)
            cacheHit = (double)acc.CacheRead / total;

        // Атрибуция: разрез по классам запросов + типы сабагентов (distinct, порядок) + вид
        // компакции. AgentTypes — null, если сабагентов не было (не пустой массив: «не было»).
        var agentTypes = acc.AgentTypes.Count > 0 ? acc.AgentTypes.ToArray() : null;

        return new LocalModelTurnSummary
        {
            OutTokens = acc.OutTokens,
            GenDuration = acc.GenDuration,
            GenTps = genTps,
            TtftMs = acc.TtftMs,
            // Миллисекунды, округлённые вверх до целой секунды (контракт ServerMessage/фронт).
            QueueMsApprox = acc.QueueMs is { } q ? (int)(Math.Ceiling(q.TotalMilliseconds / 1000) * 1000) : null,
            CacheHitPerc = cacheHit,
            MainRequests = acc.MainRequests,
            SubagentRequests = acc.SubagentRequests,
            CompactionRequests = acc.CompactionRequests,
            AuxiliaryRequests = acc.AuxiliaryRequests,
            AgentTypes = agentTypes,
            CompactionKind = acc.CompactionKind,
            ToolCallLeakTail = acc.ToolCallLeakTail,
            ToolCallLeakWholeCall = acc.ToolCallLeakWholeCall,
        };
    }

    // Запрос в работе: фаза, момент старта, момент первого токена, старт фазы queued.
    // M5: TokenCount — накопленный счётчик токенов запроса (≈1 на content_block_delta).
    private sealed class RequestState
    {
        public string Phase = "prefill";
        public DateTimeOffset StartedAt;
        public DateTimeOffset? FirstTokenAt;
        public DateTimeOffset? QueuedStartAt;
        public int TokenCount;
    }

    // Состояние сессии: запросы в работе + накопленный итог текущего хода + дроссель живых.
    // OwnerId — из токена хода (grant), LastPhase — фаза последнего живого события,
    // LastSummary — итог последнего завершённого хода (GetLastTurn).
    // LastActivity — последний момент активности (старт/токен/завершение запроса):
    // подстраховка от фантомного чата (M4) — без активности > IdleTimeout чат не висит в панели.
    // M5: TokenWindow — скользящее окно (время, суммарные токены) для живой скорости.
    private sealed class SessionState
    {
        public readonly Dictionary<string, RequestState> InWork = new();
        public TurnAccumulator Acc = new();
        public DateTimeOffset LastLive = DateTimeOffset.MinValue;
        public DateTimeOffset LastActivity = DateTimeOffset.MinValue;
        public string? OwnerId;
        public string LastPhase = "unknown";
        public LocalModelTurnSummary? LastSummary;
        // M5: скользящее окно живой скорости: (момент, суммарные токены сессии).
        // Окончание: точки старше SpeedWindow прuned'ятся; скорость = Δтокены / Δвремя.
        public readonly List<(DateTimeOffset Time, int TotalTokens)> TokenWindow = new();
        // Minor 2 (финальное ревью): монотонный суммарный счётчик токенов сессии (все запросы
        // хода, включая завершённые). Не «сумма по InWork» (падает при завершении запроса),
        // а накопитель: запрос в работе — его текущий TokenCount, завершённый — финальное
        // значение. Окно живой скорости строится по этому счётчику, не по сумме активных.
        public int SessionTotalTokens;
    }

    // Накопленный итог хода: сумма по завершённым запросам хода.
    private sealed class TurnAccumulator
    {
        public int? OutTokens;
        public int InputTokens;
        public int CacheRead;
        public int CacheCreation;
        public int? TtftMs;
        // Старт запроса, чей ttft записан в TtftMs: для сравнения при параллельных запросах.
        public DateTimeOffset? TtftStartedAt;
        public TimeSpan? GenDuration;
        public TimeSpan? QueueMs;

        // Атрибуция по подсказочным заголовкам (GatewayHintHeaders): класс запроса и компакция.
        // Считаем по запросам хода (не только завершённым): подсказка приходит на старте запроса.
        public int MainRequests;
        public int SubagentRequests;   // subagent + workflow
        public int CompactionRequests;
        public int AuxiliaryRequests;
        // Типы сабагентов: distinct, порядок первый-встреченный (List, не HashSet — порядок).
        public readonly List<string> AgentTypes = new();
        // Вид компакции (auto | manual | reactive): первый встреченный (compaction, иначе context-compacted).
        public string? CompactionKind;

        // Утечки вызова инструмента в текст (IToolCallLeakSink.NoteLeak): по видам.
        public int ToolCallLeakTail;
        public int ToolCallLeakWholeCall;
    }
}
