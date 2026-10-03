using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Jobs;

namespace ClaudeHomeServer.Services.VideoEditor.Films;

// Тихая строка правки фильма в ленте: серия частых правок одного фильма от одного инициатора в одном чате — ОДНА строка.
// Обновляемой записи в ленте нет (история пишется только вперёд, живая пара module_record — только добавление), поэтому
// строка пишется ПОСЛЕ серии: правки копятся, пока человек не затих на Quiet секунд, но не дольше Window с от первой
// правки (так лента не молчит при непрерывной работе). Остаток при остановке сервера дописывается в DisposeAsync.
// Тексты — FilmPatchText: «Вы переставили сцены», «Вы поправили фильм: переставили сцены, подрезали сцену 1».
public sealed class FilmPatchFeed(
    VideoJobThreads threads,
    ILogger<FilmPatchFeed> log,
    TimeProvider? time = null,
    TimeSpan? quiet = null,
    TimeSpan? window = null) : IAsyncDisposable
{
    public static readonly TimeSpan DefaultQuiet = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _quiet = quiet ?? DefaultQuiet;
    private readonly TimeSpan _window = window ?? DefaultWindow;
    private readonly object _gate = new();
    private readonly Dictionary<Key, Pending> _pending = [];

    private sealed record Key(string SessionId, string FilmPath, string Initiator);

    private sealed class Pending(DateTimeOffset first)
    {
        public DateTimeOffset First { get; } = first;
        public List<FilmPatchText.Change> Changes { get; } = [];
        public List<string> Ops { get; } = [];
        public ITimer? Timer { get; set; }
    }

    // Правка записана в фильм; changes — готовые обороты (FilmPatchText.Describe), ops — имена операций
    public async Task AddAsync(string sessionId, string filmPath, string initiator, IReadOnlyList<FilmPatchText.Change> changes,
        IReadOnlyList<string> ops)
    {
        Pending? expired = null;
        lock (_gate)
        {
            var key = new Key(sessionId, filmPath, initiator);
            var now = _time.GetUtcNow();
            if (_pending.TryGetValue(key, out var old) && now - old.First >= _window)
            {
                // Серия дольше окна: закрываем её отдельной строкой, новая правка начинает следующую
                _pending.Remove(key);
                old.Timer?.Dispose();
                expired = old;
            }
            if (!_pending.TryGetValue(key, out var pending))
                _pending[key] = pending = new Pending(now);
            foreach (var change in changes)
                if (!pending.Changes.Contains(change)) pending.Changes.Add(change);
            foreach (var op in ops)
                if (!pending.Ops.Contains(op)) pending.Ops.Add(op);

            var due = _quiet;
            var left = pending.First + _window - now;
            if (left < due) due = left < TimeSpan.Zero ? TimeSpan.Zero : left;
            pending.Timer?.Dispose();
            pending.Timer = _time.CreateTimer(_ => _ = FlushAsync(key), null, due, Timeout.InfiniteTimeSpan);
        }
        if (expired is not null) await WriteAsync(new Key(sessionId, filmPath, initiator), expired);
    }

    // Дописать все накопленные серии сразу (тесты, остановка сервера)
    public async Task FlushAllAsync()
    {
        List<(Key, Pending)> all;
        lock (_gate)
        {
            all = _pending.Select(p => (p.Key, p.Value)).ToList();
            _pending.Clear();
            foreach (var (_, pending) in all) pending.Timer?.Dispose();
        }
        foreach (var (key, pending) in all) await WriteAsync(key, pending);
    }

    private async Task FlushAsync(Key key)
    {
        Pending? pending;
        lock (_gate)
        {
            if (!_pending.Remove(key, out pending)) return;
            pending.Timer?.Dispose();
        }
        await WriteAsync(key, pending);
    }

    private async Task WriteAsync(Key key, Pending pending)
    {
        try
        {
            await threads.NoteAsync(key.SessionId, VideoFeedTexts.FilmPatched(key.Initiator, pending.Changes),
                new { kind = "film_patch", filmPath = key.FilmPath, ops = pending.Ops, initiator = key.Initiator });
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Видео: строка правки фильма {Path} не записана", key.FilmPath);
        }
    }

    public ValueTask DisposeAsync() => new(FlushAllAsync());
}
