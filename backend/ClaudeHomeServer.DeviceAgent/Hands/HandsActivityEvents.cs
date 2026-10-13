using System.Collections.Concurrent;
using System.Runtime.Versioning;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Hands;

/// <summary>Имена именованных событий одного хода с руками — их получает мост в аргументах.</summary>
/// <param name="Acted"><see cref="HandsBridgeArgs.ActivityEvent"/>: мост → агент, «подействовал».</param>
/// <param name="Busy"><see cref="HandsBridgeArgs.BusyEvent"/>: мост → агент, «действие упёрлось в замок машины».</param>
/// <param name="TurnEnd"><see cref="HandsBridgeArgs.TurnEndEvent"/>: агент → мост, «ход окончен, отпусти замок».</param>
internal sealed record HandsEventNames(string Acted, string Busy, string TurnEnd)
{
    /// <summary>Имена хода: общий суффикс — ход и guid, чтобы чужое событие с тем же именем не попалось.</summary>
    public static HandsEventNames For(string turnId)
    {
        var suffix = turnId + "." + Guid.NewGuid().ToString("N")[..8];
        return new HandsEventNames(HandsBridgeArgs.ActivityEventPrefix + suffix, HandsBridgeArgs.BusyEventPrefix + suffix,
            HandsBridgeArgs.TurnEndEventPrefix + suffix);
    }
}

/// <summary>
/// События хода между агентом и мостом. Агент создаёт их до запуска хода: мост поднимает
/// «подействовал» при первом действии рук (только тогда агент шлёт «ИИ управляет компьютером») и
/// «руки заняты» при отказе по замку машины; агент поднимает «ход окончен» по концу хода в выводе CLI.
/// </summary>
internal interface IHandsActivityEvents
{
    IHandsActivityWait Create(HandsEventNames names);
}

/// <summary>События одного хода; Dispose — ход кончился, ждать и поднимать больше нечего.</summary>
internal interface IHandsActivityWait : IDisposable
{
    HandsEventNames Names { get; }

    /// <summary>Завершается, когда мост поднял «подействовал»; после Dispose не завершится уже никогда.</summary>
    Task Acted { get; }

    /// <summary>
    /// Подписка на «руки заняты» — на каждый подъём. Подъём до подписки не теряется: обработчик
    /// получит его сразу.
    /// </summary>
    void OnBusy(Action handler);

    /// <summary>Ход окончен: мост отпускает замок машины. Процесс CLI при этом живёт дальше.</summary>
    void EndTurn();
}

/// <summary>
/// Windows: именованные события в пространстве сеанса входа (<c>Local\</c>). «Подействовал» — с
/// ручным сбросом (нужен один раз), «занято» и «ход окончен» — с автосбросом (каждый подъём — одно
/// пробуждение ждущего).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NamedHandsActivityEvents : IHandsActivityEvents
{
    public IHandsActivityWait Create(HandsEventNames names) => new Wait(names);

    private sealed class Wait : IHandsActivityWait
    {
        private readonly EventWaitHandle _acted;
        private readonly EventWaitHandle _busy;
        private readonly EventWaitHandle _turnEnd;
        private readonly RegisteredWaitHandle _actedRegistration;
        private readonly TaskCompletionSource _actedTask = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _sync = new();
        private RegisteredWaitHandle? _busyRegistration;
        private int _disposed;

        public Wait(HandsEventNames names)
        {
            Names = names;
            _acted = Open(names.Acted, EventResetMode.ManualReset);
            _busy = Open(names.Busy, EventResetMode.AutoReset);
            _turnEnd = Open(names.TurnEnd, EventResetMode.AutoReset);
            _actedRegistration = ThreadPool.RegisterWaitForSingleObject(_acted,
                (_, _) => { if (Volatile.Read(ref _disposed) == 0) _actedTask.TrySetResult(); },
                null, Timeout.Infinite, executeOnlyOnce: true);
        }

        public HandsEventNames Names { get; }

        public Task Acted => _actedTask.Task;

        public void OnBusy(Action handler)
        {
            lock (_sync)
            {
                if (Volatile.Read(ref _disposed) == 1 || _busyRegistration is not null) return;
                // Автосброс: подъём до подписки ждёт в событии и разбудит регистрацию сразу
                _busyRegistration = ThreadPool.RegisterWaitForSingleObject(_busy,
                    (_, _) => { if (Volatile.Read(ref _disposed) == 0) handler(); },
                    null, Timeout.Infinite, executeOnlyOnce: false);
            }
        }

        public void EndTurn()
        {
            if (Volatile.Read(ref _disposed) == 0) _turnEnd.Set();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            lock (_sync) _busyRegistration?.Unregister(null);
            _actedRegistration.Unregister(null);
            _acted.Dispose();
            _busy.Dispose();
            _turnEnd.Dispose();
        }

        private static EventWaitHandle Open(string name, EventResetMode mode)
        {
            var handle = new EventWaitHandle(false, mode, name);
            // Событие с таким именем уже поднято — не наше (имя несёт guid хода), начинаем с чистого
            handle.Reset();
            return handle;
        }
    }
}

/// <summary>
/// События внутри процесса: тесты и платформы, где рук нет. <see cref="Raise"/> — «мост
/// подействовал», <see cref="RaiseBusy"/> — «руки заняты», <see cref="TurnEnds"/> — сколько раз
/// агент сказал мосту «ход окончен».
/// </summary>
internal sealed class InProcessHandsActivityEvents : IHandsActivityEvents
{
    private readonly ConcurrentDictionary<string, Wait> _waits = new(StringComparer.Ordinal);

    public IHandsActivityWait Create(HandsEventNames names)
    {
        var wait = new Wait(this, names);
        _waits[names.Acted] = wait;
        return wait;
    }

    /// <summary>Имена событий «подействовал», которые сейчас ждут, — по ним тест находит ход.</summary>
    public IReadOnlyCollection<string> Names => _waits.Keys.ToList();

    /// <summary>Подняли «ход окончен» по ходу с этим именем «подействовал».</summary>
    public event Action<string>? TurnEnded;

    /// <summary>Поднять событие; false — такого события никто не ждёт.</summary>
    public bool Raise(string name)
    {
        if (!_waits.TryGetValue(name, out var wait)) return false;
        wait.Set();
        return true;
    }

    /// <summary>Мост упёрся в замок машины; false — хода с таким событием нет.</summary>
    public bool RaiseBusy(string name)
    {
        if (!_waits.TryGetValue(name, out var wait)) return false;
        wait.Busy();
        return true;
    }

    public int TurnEnds(string name) => _waits.TryGetValue(name, out var wait) ? wait.TurnEndCount : 0;

    private sealed class Wait(InProcessHandsActivityEvents owner, HandsEventNames names) : IHandsActivityWait
    {
        private readonly TaskCompletionSource _acted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _sync = new();
        private Action? _onBusy;
        private int _pendingBusy;
        private int _turnEnds;

        public HandsEventNames Names { get; } = names;

        public Task Acted => _acted.Task;

        public int TurnEndCount => Volatile.Read(ref _turnEnds);

        public void Set() => _acted.TrySetResult();

        public void Busy()
        {
            Action? handler;
            lock (_sync)
            {
                handler = _onBusy;
                if (handler is null) _pendingBusy++;
            }
            handler?.Invoke();
        }

        public void OnBusy(Action handler)
        {
            int pending;
            lock (_sync)
            {
                if (_onBusy is not null) return;
                _onBusy = handler;
                pending = _pendingBusy;
                _pendingBusy = 0;
            }
            for (var i = 0; i < pending; i++) handler();
        }

        public void EndTurn()
        {
            Interlocked.Increment(ref _turnEnds);
            owner.TurnEnded?.Invoke(Names.Acted);
        }

        public void Dispose() => owner._waits.TryRemove(new KeyValuePair<string, Wait>(Names.Acted, this));
    }
}
