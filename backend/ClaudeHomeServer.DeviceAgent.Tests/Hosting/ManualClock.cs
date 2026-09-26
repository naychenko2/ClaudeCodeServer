namespace ClaudeHomeServer.DeviceAgent.Tests.Hosting;

/// <summary>
/// Часы с ручным ходом и таймерами: <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
/// срабатывает только от <see cref="Advance"/>. Периодические таймеры не нужны — не поддержаны.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly TaskCompletionSource _armed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset _now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Завершается, когда кто-то завёл первый таймер.</summary>
    public Task TimerArmed => _armed.Task;

    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.DueAt <= _now).ToList();
            foreach (var t in due) _timers.Remove(t);
        }
        foreach (var t in due) t.Fire();
    }

    private void Arm(ManualTimer timer, DateTimeOffset? at)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
            timer.DueAt = at;
            if (at is null) return;
            _timers.Add(timer);
        }
        _armed.TrySetResult();
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            clock.Arm(this, dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetUtcNow() + dueTime);
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => clock.Arm(this, null);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
