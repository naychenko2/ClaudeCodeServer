namespace ClaudeHomeServer.DeviceAgent.Update;

/// <summary>Что держит агента занятым: текст — для причины «обновление ждёт».</summary>
internal enum WorkKind
{
    Turn,
    Relay,
    Process,
}

/// <summary>
/// Реестр активности агента (agent-distribution Р8): аренды ходов и запросов ретранслятора
/// (канал исполнения), терминалов и дев-серверов превью (долгоживущие процессы вертикалей).
/// Самообновление переключает версию, только когда реестр пуст.
///
/// Новые аренды обновление не блокирует — ждущее переключение не отказывает ходам. Закрывает
/// приём ровно одно мгновение: <see cref="TrySeal"/> атомарно проверяет пустоту и
/// запечатывает реестр, после чего агент переставляет указатели и выходит. Опоздавший к
/// этому мгновению канал получает отказ (<see cref="TryAcquire"/> — null), а не обрыв на
/// полуслове.
/// </summary>
internal sealed class ActivityRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<WorkKind, int> _counts = [];
    private bool _sealed;

    /// <summary>Аренда взята или закрыта.</summary>
    public event Action? Changed;

    public bool IsIdle { get { lock (_gate) return _counts.Count == 0; } }

    /// <summary>Взять аренду; null — реестр запечатан: агент прямо сейчас уходит на новую версию.</summary>
    public IDisposable? TryAcquire(WorkKind kind)
    {
        lock (_gate)
        {
            if (_sealed) return null;
            _counts[kind] = _counts.GetValueOrDefault(kind) + 1;
        }
        RaiseChanged();
        return new Lease(this, kind);
    }

    /// <summary>Пусто — запечатать и вернуть true; иначе false, реестр не тронут.</summary>
    public bool TrySeal()
    {
        lock (_gate)
        {
            if (_counts.Count != 0) return false;
            _sealed = true;
            return true;
        }
    }

    /// <summary>Переключение не состоялось — снова принимать аренды.</summary>
    public void Unseal()
    {
        lock (_gate) _sealed = false;
    }

    /// <summary>Что держит переключение, текстом для человека; null — ничего.</summary>
    public string? Describe()
    {
        KeyValuePair<WorkKind, int>[] counts;
        lock (_gate) counts = _counts.OrderBy(c => c.Key).ToArray();
        if (counts.Length == 0) return null;
        return string.Join(", ", counts.Select(c => c.Value > 1 ? $"{Text(c.Key)} ({c.Value})" : Text(c.Key)));
    }

    private static string Text(WorkKind kind) => kind switch
    {
        WorkKind.Turn => "идёт ход",
        WorkKind.Relay => "идёт запрос ретранслятора",
        WorkKind.Process => "открыт терминал или работает дев-сервер",
        _ => kind.ToString(),
    };

    private void Release(WorkKind kind)
    {
        lock (_gate)
        {
            if (!_counts.TryGetValue(kind, out var count)) return;
            if (count <= 1) _counts.Remove(kind);
            else _counts[kind] = count - 1;
        }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception) { /* подписчик — будильник обновлятора, его сбой аренде не мешает */ }
    }

    private sealed class Lease(ActivityRegistry owner, WorkKind kind) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(kind);
        }
    }
}
