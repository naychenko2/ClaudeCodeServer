namespace ClaudeHomeServer.Services.Llm;

// Сборщик скорости ответа (фича «Скорость ответа»): копит завершённые запросы по дорожкам
// статистики и открытые запросы (ждут завершения), по ним считает окна 5m и 1h.
//
// Singleton: состояние живёт в памяти процесса, на диск не пишется — панель живая, а история
// нужна ровно на час. Потокобезопасно: один lock на всё (шлюз зовёт из параллельных запросов).
//
// Память ограничена с двух сторон и по времени, и по числу: кольцо завершённых — старче часа
// выбрасывается, дальше потолка на дорожку вырезаются самые старые; открытые без завершения
// стареют по последней активности (процесс убит посреди запроса — запись не живёт вечно) и не
// растут выше потолка открытых.
public sealed class LocalModelAnswerSpeed : ILocalModelAnswerSpeed
{
    // Порог медианы скорости: запросы с меньшим числом выходных токенов в медиану не входят —
    // короткий ответ это в основном prefill и ожидание слота, а не генерация. Отдаётся наружу
    // вместе с агрегатами (фронт подписывает им число).
    public const int MinTokensForMedian = 32;

    // Окна агрегатов: 5 минут (как публикует OpenRouter) и 1 час.
    public static readonly TimeSpan Window5m = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Window1h = TimeSpan.FromHours(1);

    // Потолок кольца на дорожку: ~1 МБ записей при 50 000 запросов в час; дальше рос бы только
    // риск, а не точность. При переполнении вырезаются самые старые (кольцо).
    // internal — тесты меряют оба потолка по нему же, а не по своему числу.
    internal const int MaxRecordsPerTrack = 50_000;

    // Потолок открытых запросов: шторм запросов без завершения (движок висит) не должен растить
    // словарь до TTL — он ограничен и по числу. Одновременно живых запросов к локальному
    // движку десятки; десяток тысяч — запас, после которого растёт только риск.
    internal const int MaxOpenRequests = 10_000;

    // TTL открытого запроса: без активности дольше часа он мёртв (процесс убит посреди запроса,
    // событие потеряно) — запись удаляется, метрика теряется.
    private static readonly TimeSpan OpenRequestTtl = TimeSpan.FromHours(1);

    private readonly TimeProvider _time;
    private readonly object _gate = new();

    // Открытые запросы — по requestId (уникален на HTTP-запрос, Guid "N"): OnFirstToken и
    // OnRequestCompleted приходят без дорожки, только с id, поэтому дорожка хранится в записи.
    private readonly Dictionary<string, OpenRequest> _open = [];

    // Кольцо завершённых — по дорожкам. Записи добавляются в порядке завершения (часы одного
    // процесса), поэтому «самая старая» = первая в списке.
    private readonly Dictionary<string, List<DoneRequest>> _done = [];

    private sealed class OpenRequest
    {
        public required string Track;
        public DateTimeOffset StartedAt;
        public DateTimeOffset LastActivity;
        public DateTimeOffset? FirstTokenAt;
    }

    // Завершённый запрос: времена сведены к секундам (дальше они только знаменатели);
    // токены nullable — движок не отдал usage.
    private readonly record struct DoneRequest(DateTimeOffset CompletedAt, double DurationSec, double? TtftSec, long? Tokens);

    public LocalModelAnswerSpeed(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public void OnRequestStarted(string requestId, string track, DateTimeOffset startedAt)
    {
        if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(track)) return;
        lock (_gate)
        {
            Prune(startedAt);
            // Потолок открытых: при переполнении вытесняется самый старый по старту
            // (EvictOldestOpen сравнивает StartedAt). TTL открытого считается по последней
            // активности (Prune) — это разные основания: старый по старту не обязательно ближе
            // всех к TTL. Вытесняем его как ждущего без результата дольше всех; новый запрос
            // ещё может дать метрику.
            if (!_open.ContainsKey(requestId) && _open.Count >= MaxOpenRequests)
                EvictOldestOpen();
            // Повторный старт с тем же id (невозможен у шлюза, возможен у повторного вызова):
            // новая запись перекрывает старую — считаем по последнему старту.
            _open[requestId] = new OpenRequest { Track = track, StartedAt = startedAt, LastActivity = startedAt };
        }
    }

    public void OnFirstToken(string requestId, DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(requestId)) return;
        lock (_gate)
        {
            if (!_open.TryGetValue(requestId, out var o)) return; // запрос не начинался здесь или уже выжил
            o.FirstTokenAt ??= at;
            o.LastActivity = at;
        }
    }

    public void OnRequestCompleted(string requestId, long? outputTokens, DateTimeOffset completedAt)
    {
        if (string.IsNullOrEmpty(requestId)) return;
        lock (_gate)
        {
            // Сначала чистка: открытый запрос, который молчал дольше часа, уже мёртв — его
            // «завершение» метрикой не становится (иначе в Σ попала бы двухчасовая длительность).
            Prune(completedAt);
            if (!_open.Remove(requestId, out var o)) return; // начала не было — метрики не выдумываем

            var durationSec = (completedAt - o.StartedAt).TotalSeconds;
            double? ttftSec = o.FirstTokenAt is { } first && first >= o.StartedAt
                ? (first - o.StartedAt).TotalSeconds
                : null;

            if (!_done.TryGetValue(o.Track, out var ring)) _done[o.Track] = ring = [];
            ring.Add(new DoneRequest(completedAt, durationSec, ttftSec, outputTokens));
            if (ring.Count > MaxRecordsPerTrack)
                ring.RemoveRange(0, ring.Count - MaxRecordsPerTrack);
        }
    }

    public AnswerSpeedSnapshot SnapshotOf(string track)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Prune(now);
            var ring = _done.TryGetValue(track ?? "", out var r) ? r : (List<DoneRequest>)[];
            return new AnswerSpeedSnapshot
            {
                MinTokensForMedian = MinTokensForMedian,
                Windows = [Aggregate("5m", ring, now, Window5m), Aggregate("1h", ring, now, Window1h)],
            };
        }
    }

    // Одно окно: [now−span, now] по моменту ЗАВЕРШЕНИЯ (half-open снизу: ровно на границе —
    // входит, на такт раньше — нет). Пустое окно — requests 0 и null-метрики.
    private static AnswerSpeedWindow Aggregate(string name, List<DoneRequest> ring, DateTimeOffset now, TimeSpan span)
    {
        var from = now - span;
        var inWindow = new List<DoneRequest>(ring.Count);
        foreach (var r in ring)
            if (r.CompletedAt >= from)
                inWindow.Add(r);

        // Сквозная скорость: Σ токенов ÷ Σ времени. В суммы идут только записи с известными
        // токенами и положительной длительностью (нулевая длительность — нулевой знаменатель).
        long tokens = 0;
        double seconds = 0;
        foreach (var r in inWindow)
        {
            if (r.Tokens is not { } t || r.DurationSec <= 0) continue;
            tokens += t;
            seconds += r.DurationSec;
        }
        double? endToEnd = seconds > 0 ? tokens / seconds : null;

        // Медиана скорости по запросам от порога токенов (короткие ответы оценку занижают).
        var rates = new List<double>();
        foreach (var r in inWindow)
        {
            if (r.Tokens is not { } t || t < MinTokensForMedian || r.DurationSec <= 0) continue;
            rates.Add(t / r.DurationSec);
        }

        var ttft = new List<double>();
        foreach (var r in inWindow)
            if (r.TtftSec is { } s)
                ttft.Add(s);

        return new AnswerSpeedWindow
        {
            Window = name,
            EndToEndTps = endToEnd,
            MedianTps = Median(rates),
            MedianTtftSec = Median(ttft),
            P90TtftSec = P90(ttft),
            Requests = inWindow.Count,
        };
    }

    // Медиана: среднее двух средних при чётном n. Пусто — null (не 0). Вход не мутируется.
    internal static double? Median(List<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }

    // 90-й перцентиль: ближайший ранг ceil(0.9·n)−1 в отсортированном массиве (n=1 → 0).
    // Пусто — null. Вход не мутируется.
    internal static double? P90(List<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        var idx = Math.Min(sorted.Count - 1, (int)Math.Ceiling(0.9 * sorted.Count) - 1);
        return sorted[idx];
    }

    // Вытеснение самого старого открытого запроса: линейный поиск по словарю, который уже
    // ограничен потолком. Вызывается только под замком и только при переполнении — то есть
    // только в шторме запросов без завершения.
    private void EvictOldestOpen()
    {
        string? oldest = null;
        DateTimeOffset oldestStart = default;
        foreach (var (id, o) in _open)
        {
            if (oldest is null || o.StartedAt < oldestStart)
            {
                oldest = id;
                oldestStart = o.StartedAt;
            }
        }
        if (oldest is not null) _open.Remove(oldest);
    }

    // Чистка по «сейчас»: кольцо — всё, что старше часа; открытые — без активности старше TTL.
    private void Prune(DateTimeOffset now)
    {
        var cutoff = now - Window1h;
        // Кольцо добавляется в порядке завершения (часы одного процесса), поэтому старое — префикс:
        // снимаем голову, пока она старше окна, а не сканируем весь список.
        foreach (var ring in _done.Values)
        {
            var cut = 0;
            while (cut < ring.Count && ring[cut].CompletedAt < cutoff) cut++;
            if (cut > 0) ring.RemoveRange(0, cut);
        }

        List<string>? dead = null;
        foreach (var (id, o) in _open)
            if (o.LastActivity < cutoff)
                (dead ??= []).Add(id);
        if (dead is not null)
            foreach (var id in dead)
                _open.Remove(id);
    }
}
