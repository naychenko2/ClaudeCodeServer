using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace ClaudeHomeServer.DeviceAgent.Hands;

/// <summary>
/// Событие хода «мост подействовал» (<see cref="Protocol.HandsBridgeArgs.ActivityEvent"/>): агент
/// создаёт его до запуска хода, мост поднимает при первом действии рук, и только тогда агент
/// шлёт «ИИ управляет компьютером».
/// </summary>
internal interface IHandsActivityEvents
{
    IHandsActivityWait Create(string name);
}

/// <summary>Ожидание события одного хода; Dispose — ход кончился, ждать больше нечего.</summary>
internal interface IHandsActivityWait : IDisposable
{
    string Name { get; }

    /// <summary>Завершается, когда мост поднял событие; после Dispose не завершится уже никогда.</summary>
    Task Acted { get; }
}

/// <summary>Windows: именованное событие с ручным сбросом в пространстве сеанса входа (<c>Local\</c>).</summary>
[SupportedOSPlatform("windows")]
internal sealed class NamedHandsActivityEvents : IHandsActivityEvents
{
    public IHandsActivityWait Create(string name) => new Wait(name);

    private sealed class Wait : IHandsActivityWait
    {
        private readonly EventWaitHandle _event;
        private readonly RegisteredWaitHandle _registration;
        private readonly TaskCompletionSource _acted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public Wait(string name)
        {
            Name = name;
            _event = new EventWaitHandle(false, EventResetMode.ManualReset, name);
            // Событие с таким именем уже поднято — не наше (имя несёт guid хода), начинаем с чистого
            _event.Reset();
            _registration = ThreadPool.RegisterWaitForSingleObject(_event,
                (_, _) => { if (Volatile.Read(ref _disposed) == 0) _acted.TrySetResult(); },
                null, Timeout.Infinite, executeOnlyOnce: true);
        }

        public string Name { get; }

        public Task Acted => _acted.Task;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _registration.Unregister(null);
            _event.Dispose();
        }
    }
}

/// <summary>События внутри процесса: тесты и платформы, где рук нет. <see cref="Raise"/> — «мост подействовал».</summary>
internal sealed class InProcessHandsActivityEvents : IHandsActivityEvents
{
    private readonly ConcurrentDictionary<string, Wait> _waits = new(StringComparer.Ordinal);

    public IHandsActivityWait Create(string name)
    {
        var wait = new Wait(this, name);
        _waits[name] = wait;
        return wait;
    }

    /// <summary>Имена событий, которые сейчас ждут, — по ним тест находит событие хода.</summary>
    public IReadOnlyCollection<string> Names => _waits.Keys.ToList();

    /// <summary>Поднять событие; false — такого события никто не ждёт.</summary>
    public bool Raise(string name)
    {
        if (!_waits.TryGetValue(name, out var wait)) return false;
        wait.Set();
        return true;
    }

    private sealed class Wait(InProcessHandsActivityEvents owner, string name) : IHandsActivityWait
    {
        private readonly TaskCompletionSource _acted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name { get; } = name;

        public Task Acted => _acted.Task;

        public void Set() => _acted.TrySetResult();

        public void Dispose() => owner._waits.TryRemove(new KeyValuePair<string, Wait>(Name, this));
    }
}
