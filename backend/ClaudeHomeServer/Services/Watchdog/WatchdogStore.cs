using System.Collections.Concurrent;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Watchdog;

// Серверные сторожа чатов: in-memory + data/watchdogs.json (по образцу TaskManager).
// Логики опроса здесь нет — только CRUD, лимиты и персистентность; цикл живёт в WatchdogService.
// Запись атомарна (JsonFileStore), повреждённый файл уезжает в .corrupt-*.bak — стартуем пустыми.
public class WatchdogStore
{
    private readonly ConcurrentDictionary<string, WatchdogRecord> _items = new();
    private readonly string _storePath;
    private readonly Lock _saveLock = new();

    public WatchdogStore(IConfiguration config)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        _storePath = Path.Combine(dataDir, "watchdogs.json");
        foreach (var w in JsonFileStore.Load<List<WatchdogRecord>>(_storePath) ?? [])
            _items[w.Id] = w;
    }

    /// <summary>
    /// Активный сторож переведён в Cancelled (watch_cancel или гашение чата) — id для
    /// слушателей. Сервис цикла по нему гасит CancellationToken идущего опроса: poll-процесс
    /// убивается немедленно, а не собственным per-poll таймаутом (сирота до 60 с — дефект
    /// smoke 01.09). Стреляет ТОЛЬКО на реальном переводе Active→Cancelled.
    /// </summary>
    public event Action<string>? ActiveCancelled;

    public WatchdogRecord? GetById(string id) => _items.GetValueOrDefault(id);

    public IReadOnlyList<WatchdogRecord> GetBySession(string sessionId) =>
        _items.Values.Where(w => w.SessionId == sessionId)
            .OrderBy(w => w.CreatedAt).ToList();

    public IReadOnlyList<WatchdogRecord> GetByOwner(string ownerId) =>
        _items.Values.Where(w => w.OwnerId == ownerId)
            .OrderBy(w => w.CreatedAt).ToList();

    // Активные сторожа — кандидаты цикла опроса и предмет лимитов постановки
    public IReadOnlyList<WatchdogRecord> GetActive() =>
        _items.Values.Where(w => w.Status == WatchdogStatus.Active).ToList();

    /// <summary>
    /// Постановка сторожа. Валидация и лимиты — здесь (шаг 1 плана): интервал 30–600 с,
    /// TTL ≤ 1440 мин, ≤ 5 активных на чат, ≤ 20 на владельца. null + error — модель
    /// получает текст и исправляет параметры, молчаливый клэмп скрыл бы её ошибку.
    /// PollTimeoutSeconds сервер считает сам: min(60, интервал).
    /// </summary>
    public WatchdogRecord? Create(string ownerId, string sessionId, string? projectId,
        string name, string pollCommand, int? intervalSeconds, int? timeoutMinutes,
        out string? error)
    {
        error = ValidateNew(ownerId, sessionId, name, pollCommand, intervalSeconds, timeoutMinutes);
        if (error is not null) return null;

        var item = new WatchdogRecord
        {
            OwnerId = ownerId,
            SessionId = sessionId,
            ProjectId = string.IsNullOrEmpty(projectId) ? null : projectId,
            Name = name.Trim(),
            PollCommand = pollCommand.Trim(),
            IntervalSeconds = intervalSeconds ?? WatchdogLimits.DefaultIntervalSeconds,
            TimeoutMinutes = timeoutMinutes ?? WatchdogLimits.DefaultTimeoutMinutes,
        };
        item.PollTimeoutSeconds = WatchdogLimits.PollTimeoutFor(item.IntervalSeconds);
        _items[item.Id] = item;
        Save();
        return item;
    }

    private string? ValidateNew(string ownerId, string sessionId,
        string name, string pollCommand, int? intervalSeconds, int? timeoutMinutes)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Нужно имя сторожа";
        if (name.Trim().Length > 80) return "Имя сторожа длиннее 80 символов";
        if (string.IsNullOrWhiteSpace(pollCommand)) return "Нужна команда опроса";
        if (pollCommand.Trim().Length > 2000) return "Команда опроса длиннее 2000 символов";
        if (intervalSeconds is { } interval
            && (interval < WatchdogLimits.MinIntervalSeconds || interval > WatchdogLimits.MaxIntervalSeconds))
            return $"Интервал опроса — от {WatchdogLimits.MinIntervalSeconds} до {WatchdogLimits.MaxIntervalSeconds} секунд";
        if (timeoutMinutes is { } ttl && ttl is < 1 or > WatchdogLimits.MaxTimeoutMinutes)
            return $"Потолок жизни сторожа — от 1 минуты до {WatchdogLimits.MaxTimeoutMinutes}";
        var activeChat = _items.Values.Count(w =>
            w.SessionId == sessionId && w.Status == WatchdogStatus.Active);
        if (activeChat >= WatchdogLimits.MaxPerChat)
            return $"У чата уже {WatchdogLimits.MaxPerChat} активных сторожей — снимите один через watch_cancel";
        var activeOwner = _items.Values.Count(w =>
            w.OwnerId == ownerId && w.Status == WatchdogStatus.Active);
        if (activeOwner >= WatchdogLimits.MaxPerOwner)
            return $"У владельца уже {WatchdogLimits.MaxPerOwner} активных сторожей — снимите лишние через watch_cancel";
        return null;
    }

    /// <summary>
    /// Снятие сторожа (терминал cancelled). Только активного: терминальный исход единственный
    /// и затирать его нельзя — watch_cancel по fired с недоставленным будильником отнял бы у
    /// него ретраи доставки (п.2 ревью). Чужой id — null без текста (тулсет отдаёт 404-текст),
    /// уже завершённый — null + error с фактическим статусом.
    /// </summary>
    public WatchdogRecord? Cancel(string id, string ownerId, out string? error)
    {
        error = null;
        if (!_items.TryGetValue(id, out var item) || item.OwnerId != ownerId)
            return null;
        if (item.Status != WatchdogStatus.Active)
        {
            error = $"Сторож уже завершён ({item.Status.ToString().ToLowerInvariant()}) — снимать нечего";
            return null;
        }
        item.Status = WatchdogStatus.Cancelled;
        Save();
        ActiveCancelled?.Invoke(id);
        return item;
    }

    /// <summary>
    /// Гашение активных сторожей чата (удаление/архивация): без будильника — будить
    /// несуществующий/архивный чат нельзя. Возвращает погашенное число (для лога).
    /// </summary>
    public int CancelBySession(string sessionId)
    {
        var doomed = _items.Values
            .Where(w => w.SessionId == sessionId && w.Status == WatchdogStatus.Active)
            .Select(w => w.Id).ToList();
        foreach (var id in doomed) _items[id].Status = WatchdogStatus.Cancelled;
        if (doomed.Count > 0)
        {
            Save();
            foreach (var id in doomed) ActiveCancelled?.Invoke(id);
        }
        return doomed.Count;
    }

    /// <summary>
    /// Терминальные сторожа с недоставленным будильником — очередь ретраев сервиса.
    /// cancelled не входит: его будильника не бывает.
    /// </summary>
    public IReadOnlyList<WatchdogRecord> GetPendingDelivery() =>
        _items.Values.Where(w => w.Status is not (WatchdogStatus.Active or WatchdogStatus.Cancelled)
                && w.DeliveredAt is null)
            .ToList();

    /// <summary>
    /// Уборка отработавших записей: доставленный/отменённый терминальный сторож старше
    /// суток не нужен никому (watch_list показывает свежие), а стор без уборки рос бы
    /// вечно — лимиты постановки его не касаются. Недоставленные (DeliveredAt = null)
    /// и активные не трогаем.
    /// </summary>
    public void PruneDelivered(DateTime nowUtc)
    {
        var stale = _items.Values
            .Where(w => w.Status == WatchdogStatus.Cancelled
                && nowUtc - w.CreatedAt > TimeSpan.FromDays(1))
            .Concat(_items.Values.Where(w => w.Status is not (WatchdogStatus.Active or WatchdogStatus.Cancelled)
                && w.DeliveredAt is { } delivered
                && nowUtc - delivered > TimeSpan.FromDays(1)))
            .Select(w => w.Id).ToList();
        if (stale.Count == 0) return;
        foreach (var id in stale) _items.TryRemove(id, out _);
        Save();
    }

    // Снимок под локом: сериализация посреди мутации отдала бы полузаписанную запись
    public void Save()
    {
        lock (_saveLock)
        {
            JsonFileStore.Save(_storePath, _items.Values.OrderBy(w => w.CreatedAt).ToList());
        }
    }
}
