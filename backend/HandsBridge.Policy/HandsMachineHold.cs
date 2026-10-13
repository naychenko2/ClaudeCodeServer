using System.Collections.Concurrent;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.HandsBridge.Policy;

/// <summary>Замок машины, общий для всех мостов сеанса входа: взять без ожидания и отпустить.</summary>
public interface IHandsMachineMutex
{
    /// <summary>true — замок у нас (в том числе брошенный умершим владельцем); false — его держит другой.</summary>
    bool TryEnter();

    void Exit();
}

/// <summary>
/// Одни руки на машину (ADR-016 §7, <see cref="HandsMachineLock"/>): замок держится на время
/// действий руками в пределах хода. Берёт его мост при первом действии ввода или управления
/// окнами и программами, отпускает по концу хода (событие агента, <see cref="Release"/>) или
/// смертью своего процесса. Подключённые, но бездействующие руки замок не держат, поэтому два
/// чата с руками стартуют одновременно; отказ получает только вызов инструмента второго.
/// </summary>
public sealed class HandsMachineHold(IHandsMachineMutex mutex)
{
    /// <summary>Ответ модели на действие, пока руками действует другой ход этой машины.</summary>
    public const string BusyMessage =
        "Hands are busy: another chat on this device is acting with hands right now. The action was not performed. " +
        "Try again later or tell the user that the computer is in use by another chat.";

    private readonly Lock _lock = new();
    private bool _held;

    /// <summary>Замок сейчас у этого моста.</summary>
    public bool Held
    {
        get { lock (_lock) return _held; }
    }

    /// <summary>
    /// Нужен ли вызову замок: всё, что трогает машину (<see cref="HandsActivity.TouchesMachine"/>),
    /// кроме снимков и браузерных чтений (<see cref="HandsVision.ReadOnlyTools"/>) — они не трогают
    /// ввод и соседу не мешают.
    /// </summary>
    public static bool Needs(string tool, string? action = null) =>
        HandsActivity.TouchesMachine(tool, action) && !HandsVision.ReadOnlyTools.Contains(tool);

    /// <summary>
    /// Вызов прошёл гейт и сейчас подействует. false — замок у другого хода, действовать нельзя.
    /// Уже взятый замок держится до <see cref="Release"/>.
    /// </summary>
    public bool TryHold(string tool, string? action = null)
    {
        if (!Needs(tool, action)) return true;
        lock (_lock)
        {
            if (!_held) _held = mutex.TryEnter();
            return _held;
        }
    }

    /// <summary>Ход окончен: отпустить замок. false — его и не было.</summary>
    public bool Release()
    {
        lock (_lock)
        {
            if (!_held) return false;
            _held = false;
            mutex.Exit();
            return true;
        }
    }
}

/// <summary>
/// Именованный мьютекс машины. Мьютекс, а не семафор: при смерти владельца (KillTree по «Стопу»
/// трея или по концу хода) он становится брошенным, и следующий захват успешен
/// (<see cref="AbandonedMutexException"/>), а счётчик семафора не вернул бы никто. Мьютекс привязан
/// к потоку, поэтому все операции с ним идут через один поток-владелец.
/// </summary>
public sealed class NamedHandsMachineMutex : IHandsMachineMutex, IDisposable
{
    private readonly string _name;
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _owner;
    // Трогает только поток-владелец
    private Mutex? _mutex;
    private bool _owned;

    public NamedHandsMachineMutex(string name)
    {
        _name = name;
        _owner = new Thread(Run) { IsBackground = true, Name = "hands-machine-lock" };
        _owner.Start();
    }

    /// <summary>Сообщения о захвате брошенного замка и о чужом объекте с тем же именем.</summary>
    public Action<string>? Log { get; init; }

    public bool TryEnter() => Invoke(() =>
    {
        if (_owned) return true;
        if (_mutex is null)
        {
            try { _mutex = new Mutex(false, _name); }
            catch (Exception e) when (e is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
            {
                // Имя занято объектом другого типа — семафором агента старой версии, который держит руки
                Log?.Invoke($"замок машины не открыт: {e.Message}");
                return false;
            }
        }
        try
        {
            _owned = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            Log?.Invoke("замок машины брошен умершим владельцем — взят");
            _owned = true;
        }
        return _owned;
    });

    public void Exit() => Invoke(() =>
    {
        if (!_owned) return false;
        _owned = false;
        _mutex!.ReleaseMutex();
        return true;
    });

    /// <summary>Отпустить замок и остановить поток-владелец.</summary>
    public void Dispose()
    {
        if (_work.IsAddingCompleted) return;
        Exit();
        _work.CompleteAdding();
        _owner.Join();
    }

    private T Invoke<T>(Func<T> action)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try { done.SetResult(action()); }
            catch (Exception e) { done.SetException(e); }
        });
        return done.Task.GetAwaiter().GetResult();
    }

    private void Run()
    {
        foreach (var work in _work.GetConsumingEnumerable())
            work();
        _mutex?.Dispose();
    }
}

/// <summary>Замок внутри процесса: тесты и мост до <c>Configure</c>.</summary>
public sealed class InProcessHandsMachineMutex : IHandsMachineMutex
{
    private int _taken;

    public bool TryEnter() => Interlocked.CompareExchange(ref _taken, 1, 0) == 0;

    public void Exit() => Volatile.Write(ref _taken, 0);
}
