using System.Text.Json;
using System.Text.Json.Serialization;
using AiHomeDesktop.Core.Protocol;

namespace AiHomeDesktop.Core.Channel;

/// <summary>Запись журнала: один вызов и его судьба на этой машине.</summary>
/// <param name="CallId">Идентификатор вызова, выданный сервером.</param>
/// <param name="Kind">Вид вызова.</param>
/// <param name="SeenAt">Когда команда пришла на устройство.</param>
/// <param name="Executed">Исполнялся ли вызов (повторно он не исполняется никогда).</param>
/// <param name="Result">Готовый результат, если вызов уже кончился.</param>
/// <param name="Delivered">Доехал ли результат до сервера.</param>
public sealed record CallJournalEntry(
    string CallId,
    string Kind,
    DateTimeOffset SeenAt,
    bool Executed = false,
    DeviceCallResultBody? Result = null,
    bool Delivered = false);

/// <summary>
/// Локальный журнал вызовов по callId (ADR-008, «Протокол канала»): обрыв связи — штатное
/// состояние, и после реконнекта клиент обязан ответить на два вопроса.
///
/// 1. «Результат не доехал?» — недоставленные записи дослать POST'ом.
/// 2. «Этот вызов уже приходил?» — повторно пришедшую команду НЕ исполнять. Авто-ретраев
///    исполнения нет нигде: клик, ввод и запуск не идемпотентны.
///
/// TTL — минуты, а не дни: журнал отвечает на вопросы реконнекта, а не хранит историю
/// (она живёт на сервере). Файл маленький, поэтому пишется целиком — очередей и слияний
/// тут не нужно.
/// </summary>
public sealed class CallJournal
{
    private readonly string _filePath;
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CallJournalEntry> _entries = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public CallJournal(string filePath, TimeProvider? time = null, TimeSpan? ttl = null)
    {
        _filePath = filePath;
        _time = time ?? TimeProvider.System;
        _ttl = ttl ?? DesktopProtocol.JournalTtl;
        Load();
    }

    /// <summary>
    /// Открыть вызов. false — команда с этим callId уже приходила: исполнять её второй раз
    /// нельзя, а вернуть надо ту же запись (в ней может лежать недоставленный результат).
    /// </summary>
    public bool TryBegin(string callId, string kind, out CallJournalEntry entry)
    {
        lock (_gate)
        {
            Prune();
            if (_entries.TryGetValue(callId, out var existing))
            {
                entry = existing;
                return false;
            }

            entry = new CallJournalEntry(callId, kind, _time.GetUtcNow());
            _entries[callId] = entry;
            Save();
            return true;
        }
    }

    /// <summary>Пометить, что вызов пошёл в исполнение: повторно он не исполняется.</summary>
    public void MarkExecuting(string callId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(callId, out var entry)) return;
            _entries[callId] = entry with { Executed = true };
            Save();
        }
    }

    /// <summary>Записать готовый результат — пока НЕ доставленный.</summary>
    public void RecordResult(string callId, DeviceCallResultBody result)
    {
        lock (_gate)
        {
            var entry = _entries.TryGetValue(callId, out var existing)
                ? existing
                : new CallJournalEntry(callId, "", _time.GetUtcNow());
            _entries[callId] = entry with { Executed = true, Result = result, Delivered = false };
            Save();
        }
    }

    /// <summary>Результат доехал до сервера (или сервер о вызове уже не помнит — досылать нечего).</summary>
    public void MarkDelivered(string callId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(callId, out var entry)) return;
            _entries[callId] = entry with { Delivered = true };
            Save();
        }
    }

    /// <summary>Что осталось дослать после реконнекта.</summary>
    public IReadOnlyList<CallJournalEntry> Undelivered()
    {
        lock (_gate)
        {
            Prune();
            return _entries.Values
                .Where(e => e is { Result: not null, Delivered: false })
                .OrderBy(e => e.SeenAt)
                .ToList();
        }
    }

    public CallJournalEntry? Find(string callId)
    {
        lock (_gate) return _entries.GetValueOrDefault(callId);
    }

    /// <summary>Выбросить всё, что старше TTL.</summary>
    public void Prune()
    {
        lock (_gate)
        {
            var edge = _time.GetUtcNow() - _ttl;
            var stale = _entries.Where(kv => kv.Value.SeenAt < edge).Select(kv => kv.Key).ToList();
            if (stale.Count == 0) return;
            foreach (var key in stale) _entries.Remove(key);
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var items = JsonSerializer.Deserialize<List<CallJournalEntry>>(
                File.ReadAllText(_filePath), JsonOptions) ?? [];
            foreach (var item in items) _entries[item.CallId] = item;
            Prune();
        }
        catch (Exception)
        {
            // Битый журнал — не повод не подняться: он вспомогательный. Потеря записи
            // означает лишь, что недоехавший результат досылать нечем, а сервер сам
            // закроет вызов исходом unknown.
            _entries.Clear();
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_entries.Values.ToList(), JsonOptions));
        }
        catch (Exception)
        {
            // Диск недоступен — работаем на памяти: журнал полезен, но не критичен.
        }
    }
}
