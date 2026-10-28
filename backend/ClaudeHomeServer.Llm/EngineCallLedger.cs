namespace ClaudeHomeServer.Services.Llm;

// Звено цепочки вызова места к именованному движку (контракт фронта LocalModelChainLink).
// `Bonsai` — собственное звено движка: имя взято из контракта и общее для всех
// LocalEngines:*, фронт не знает имени движка (разрез v2, §4).
public enum EngineCallLink { Bonsai, Insurance, Claude }

// Итог цепочки: какое звено ответило. Missed — ответа нет: цепочка вернула пусто или
// бросила (внешняя отмена тоже: вызов состоялся, ответа не дал).
public enum EngineCallOutcome { Bonsai, Insurance, Claude, Missed }

// Ответы мест за последний час одного движка (контракт фронта answersLastHour).
// sinceAt = max(now − 1 ч, старт процесса): честный знаменатель доли — процессу
// пять минут, и знаменатель не «пустой час».
public sealed record EngineAnswerCounts(int Bonsai, int Insurance, int Claude, int Missed,
    DateTimeOffset SinceAt);

// Вызов места прямо сейчас (контракт фронта callsNow): место, звено, длительность.
// Ни OwnerId, ни промпта: место — системное действие, не чат (разрез v2, §4).
// Link == null — вызов уже в журнале, но ещё не вошёл ни в одно звено («ждёт в очереди»:
// проба готовности или слот допуска движка). Place — название места из каталога.
public sealed record EngineCallNow(string ActionKey, string Place, EngineCallLink? Link,
    DateTimeOffset StartedAt, TimeSpan Duration);

// Журнал вызовов мест к именованным движкам: «кто сейчас на модели» + ответы за час
// (блок «Дежурство», разрез v2 §4, шаг Б5). Единственный писатель — CheapTextRunner,
// и только цепочек, где маршрут дал движок (router.EngineOf(route) != null): явная модель
// админа и места не на движке в журнал не входят.
//
// Всё живёт только в памяти: на каждый движок — кольцо из 60 минутных клеток и список
// открытых вызовов; на диск ничего не пишется, окно — час, панель опрашивается живым
// (разрез §6.5). Инвариант «учёт за ход — только шлюз» не затронут: это учёт вызовов
// мест, а не ходов, и в LocalModelTurnMeter он не попадает.
//
// Синглтон в DI (LlmSubsystem), не статический Instance: журнал живёт с контейнером,
// а тест собирает свой — со своими часами.
public sealed class EngineCallLedger
{
    private const int Cells = 60; // окно часа минутными клетками

    private readonly TimeProvider _time;
    private readonly DateTimeOffset _startedAt;
    private readonly object _gate = new();
    private readonly Dictionary<string, EngineLedger> _byEngine = new(StringComparer.OrdinalIgnoreCase);

    public EngineCallLedger(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _startedAt = _time.GetUtcNow();
    }

    // Начало вызова движка: запись «сейчас» БЕЗ звена — вызов ещё не вошёл в цепочку
    // (ждёт пробы готовности или слота). Возвращает ручку Step/End; null — имя движка или
    // ключ места пусты, вызов не журналируется.
    public EngineCallHandle? Begin(string engineName, string actionKey)
    {
        if (string.IsNullOrWhiteSpace(engineName) || string.IsNullOrWhiteSpace(actionKey)) return null;
        lock (_gate)
        {
            if (!_byEngine.TryGetValue(engineName, out var owner))
                _byEngine[engineName] = owner = new EngineLedger();
            var call = new OpenCall(owner, actionKey, _time.GetUtcNow());
            owner.Open.Add(call);
            return new EngineCallHandle(this, call);
        }
    }

    // Ответы за последние 60 минут: окно [текущая минута − 59, текущая минута], записи
    // старше — вне (тест на 61 мин). У движка без записей — нули: «нет данных» решает
    // контроллер (B3c), здесь счёт всегда настоящий.
    public EngineAnswerCounts AnswersLastHour(string engineName)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var nowMinute = now.ToUnixTimeSeconds() / 60;
            int bonsai = 0, insurance = 0, claude = 0, missed = 0;
            if (_byEngine.TryGetValue(engineName, out var owner))
                for (var i = 0; i < Cells; i++)
                {
                    var m = owner.CellMinute[i];
                    if (m < nowMinute - Cells + 1 || m > nowMinute) continue;
                    bonsai += owner.Counts[i, (int)EngineCallOutcome.Bonsai];
                    insurance += owner.Counts[i, (int)EngineCallOutcome.Insurance];
                    claude += owner.Counts[i, (int)EngineCallOutcome.Claude];
                    missed += owner.Counts[i, (int)EngineCallOutcome.Missed];
                }
            var hourAgo = now - TimeSpan.FromHours(1);
            var sinceAt = hourAgo > _startedAt ? hourAgo : _startedAt;
            return new EngineAnswerCounts(bonsai, insurance, claude, missed, sinceAt);
        }
    }

    // Открытые вызовы движка прямо сейчас: место, звено (nullable — «ждёт в очереди»),
    // длительность с начала вызова. Без владельца и промпта.
    public IReadOnlyList<EngineCallNow> CallsNow(string engineName)
    {
        lock (_gate)
        {
            if (!_byEngine.TryGetValue(engineName, out var owner) || owner.Open.Count == 0)
                return [];
            var now = _time.GetUtcNow();
            var list = new List<EngineCallNow>(owner.Open.Count);
            foreach (var c in owner.Open)
                list.Add(new EngineCallNow(c.ActionKey, PlaceOf(c.ActionKey), c.Link, c.StartedAt,
                    now - c.StartedAt));
            return list;
        }
    }

    // Название места наружу (контракт `place`): Title из каталога; неизвестный ключ —
    // сам ключ: строка журнала не должна пустеть из-за выжившего из каталога места.
    private static string PlaceOf(string actionKey) =>
        LocalActionCatalog.Find(actionKey)?.Title ?? actionKey;

    internal void StepOf(OpenCall call, EngineCallLink link)
    {
        lock (_gate)
        {
            if (call.Closed) return;
            call.Link = link;
        }
    }

    // Закрытие вызова: строка уходит из «сейчас», итог — в клетку текущей минуты.
    // Повторный End — no-op: у цепочки один результат, двойной счёт был бы враньём.
    internal void EndOf(OpenCall call, EngineCallOutcome outcome)
    {
        lock (_gate)
        {
            if (call.Closed) return;
            call.Closed = true;
            call.Owner.Open.Remove(call);
            var cell = CellOf(call.Owner, _time.GetUtcNow().ToUnixTimeSeconds() / 60);
            call.Owner.Counts[cell, (int)outcome]++;
        }
    }

    // Клетка кольца для минуты: чужая (устаревшая) запись в этой клетке стирается —
    // кольцо переживает сутки без чистки по таймеру.
    private static int CellOf(EngineLedger owner, long minute)
    {
        var idx = (int)(minute % Cells);
        if (owner.CellMinute[idx] != minute)
        {
            for (var b = 0; b < 4; b++) owner.Counts[idx, b] = 0;
            owner.CellMinute[idx] = minute;
        }
        return idx;
    }

    internal sealed class EngineLedger
    {
        // Кольцо минутных клеток: CellMinute[i] — unix-минута содержимого клетки i
        // (0 — клетка пустая). Counts — корзины EngineCallOutcome этой минуты.
        public readonly long[] CellMinute = new long[Cells];
        public readonly int[,] Counts = new int[Cells, 4];
        public readonly List<OpenCall> Open = [];
    }

    internal sealed class OpenCall(EngineLedger owner, string actionKey, DateTimeOffset startedAt)
    {
        public EngineLedger Owner { get; } = owner;
        public string ActionKey { get; } = actionKey;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public EngineCallLink? Link { get; set; }
        public bool Closed { get; set; }
    }
}

// Ручка одного вызова в журнале: Step меняет текущее звено, End закрывает вызов и
// кладёт итог в минутную клетку. После End оба метода — no-op (см. EndOf).
public sealed class EngineCallHandle
{
    private readonly EngineCallLedger _ledger;
    private readonly EngineCallLedger.OpenCall _call;

    internal EngineCallHandle(EngineCallLedger ledger, EngineCallLedger.OpenCall call)
    {
        _ledger = ledger;
        _call = call;
    }

    public void Step(EngineCallLink link) => _ledger.StepOf(_call, link);
    public void End(EngineCallOutcome outcome) => _ledger.EndOf(_call, outcome);
}
