using System.Text.Json;

namespace ClaudeHomeServer.Telemetry.Alerts;

/// <summary>
/// Что мы помним о разосланном алерте — нужно, чтобы осмысленно сказать «восстановлено»
/// и чтобы погасший инцидент можно было разобрать позже.
///
/// <paramref name="ResolvedAt"/> — момент, когда алерт ИСЧЕЗ из выдачи SigNoz. Пока он
/// null, инцидент считается горящим. Остальные поля необязательные: файл состояния
/// существует с прошлых версий, и старые записи обязаны читаться как есть.
/// </summary>
public sealed record AlertMemo(
    string Title,
    DateTimeOffset FiredAt,
    string? Severity = null,
    string? Environment = null,
    string? RuleId = null,
    DateTimeOffset? ResolvedAt = null,
    // Заглушён человеком: инцидент остаётся видимым в разделе, но не идёт в счётчик
    // и не будит push. Поле необязательное — файлы прошлых версий читаются как есть.
    DateTimeOffset? MutedAt = null,
    // Чат из меток алерта (правила с разрезом по chat_id). Помним его вместе с памяткой:
    // у погасшего инцидента самого алерта уже нет, а карточку по диплинку из уведомления
    // открывают как раз тогда, когда всё закончилось.
    string? ChatId = null);

/// <summary>Запись истории: отпечаток и памятка по нему.</summary>
public sealed record AlertHistoryEntry(string Fingerprint, AlertMemo Memo)
{
    public DateTimeOffset? ResolvedAt => Memo.ResolvedAt;
}

/// <summary>
/// Помнит, о каких алертах уже сообщили (<c>data/alert-state.json</c>).
///
/// Без этого состояния горящий часами алерт слал бы уведомление на каждом опросе:
/// минута — уведомление, и через полчаса их отключат совсем. Переживает перезапуск
/// намеренно: после рестарта сервера повторять старые тревоги не нужно.
///
/// Погасший алерт больше не забывается, а помечается <see cref="MarkResolved"/> — из этих
/// записей раздел «Инциденты» строит секцию «Недавние». Ключевой инвариант:
/// <see cref="KnownFingerprints"/> отдаёт ТОЛЬКО горящие, иначе повторное возгорание
/// перестало бы считаться новым событием и никого бы не разбудило.
/// </summary>
public sealed class AlertStateStore
{
    /// <summary>Сколько погасших записей храним. Дальше история никому не нужна, а файл растёт.</summary>
    public const int MaxHistory = 50;

    private readonly string _path;
    private readonly ILogger<AlertStateStore> _log;
    private readonly Lock _lock = new();
    private Dictionary<string, AlertMemo> _known = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public AlertStateStore(IConfiguration config, ILogger<AlertStateStore> log)
    {
        _log = log;
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "alert-state.json");
        Load();
    }

    /// <summary>
    /// Отпечатки ГОРЯЩИХ алертов — те, о которых уже уведомили и повторять не надо.
    /// Погасшие сюда не входят: их повторное возгорание — новое событие.
    /// </summary>
    public IReadOnlySet<string> KnownFingerprints
    {
        get
        {
            lock (_lock)
                return _known.Where(kv => kv.Value.ResolvedAt is null)
                             .Select(kv => kv.Key)
                             .ToHashSet(StringComparer.Ordinal);
        }
    }

    public AlertMemo? Recall(string fingerprint)
    {
        lock (_lock) return _known.GetValueOrDefault(fingerprint);
    }

    public void Remember(string fingerprint, AlertMemo memo)
    {
        lock (_lock)
        {
            // Заглушку переносим на новую памятку: опрос зовёт Remember на каждом тике,
            // и без этого «Заглушить» слетало бы через минуту после нажатия — молча.
            if (_known.TryGetValue(fingerprint, out var prev) && prev.MutedAt is not null)
                memo = memo with { MutedAt = prev.MutedAt };
            _known[fingerprint] = memo;
            Trim();
            Save();
        }
    }

    /// <summary>
    /// Пометить алерты погасшими. Замена прежнего <c>Forget</c>: запись остаётся в файле,
    /// чтобы инцидент можно было открыть и разобрать после того, как он погас.
    /// </summary>
    public void MarkResolved(IEnumerable<string> fingerprints, DateTimeOffset? at = null)
    {
        var moment = at ?? DateTimeOffset.UtcNow;
        lock (_lock)
        {
            var changed = false;
            foreach (var fingerprint in fingerprints)
            {
                if (!_known.TryGetValue(fingerprint, out var memo) || memo.ResolvedAt is not null) continue;
                _known[fingerprint] = memo with { ResolvedAt = moment };
                changed = true;
            }
            if (!changed) return;
            Trim();
            Save();
        }
    }

    /// <summary>
    /// Заглушить/вернуть звук по отпечатку. Заглушённый инцидент ОСТАЁТСЯ в списке и
    /// открывается как обычно — глушится только шум: счётчик на кнопке и push.
    ///
    /// Памятка заводится при необходимости: заглушить можно и то, о чём мы ещё не
    /// уведомляли (рассылка выключена на этом инстансе — <c>Telemetry:Alerts:Enabled</c>),
    /// иначе кнопка «Заглушить» молча ничего не делала бы именно там, где шумит.
    /// </summary>
    public void SetMuted(string fingerprint, bool muted, AlertMemo? fallback = null)
    {
        lock (_lock)
        {
            if (!_known.TryGetValue(fingerprint, out var memo))
            {
                if (!muted) return;                    // нечего возвращать
                memo = fallback ?? new AlertMemo("Инцидент", DateTimeOffset.UtcNow);
            }
            var next = memo with { MutedAt = muted ? DateTimeOffset.UtcNow : null };
            if (next == memo) return;
            _known[fingerprint] = next;
            Trim();
            Save();
        }
    }

    public bool IsMuted(string fingerprint)
    {
        lock (_lock) return _known.GetValueOrDefault(fingerprint)?.MutedAt is not null;
    }

    /// <summary>
    /// Последние записи, свежие первыми: горящие по времени срабатывания, погасшие — по
    /// времени погасания. Больше <see cref="MaxHistory"/> не отдаём при любом limit.
    /// </summary>
    public IReadOnlyList<AlertHistoryEntry> Recent(int limit = MaxHistory)
    {
        lock (_lock)
        {
            return [.. _known
                .Select(kv => new AlertHistoryEntry(kv.Key, kv.Value))
                .OrderByDescending(e => e.Memo.ResolvedAt ?? e.Memo.FiredAt)
                .Take(Math.Clamp(limit, 1, MaxHistory))];
        }
    }

    /// <summary>
    /// Потолок файла: горящие храним все (их считанные штуки и они рабочее состояние),
    /// погасших — не больше <see cref="MaxHistory"/>, старейшие вытесняются.
    /// </summary>
    private void Trim()
    {
        var resolved = _known.Where(kv => kv.Value.ResolvedAt is not null).ToList();
        if (resolved.Count <= MaxHistory) return;
        foreach (var kv in resolved.OrderBy(kv => kv.Value.ResolvedAt).Take(resolved.Count - MaxHistory))
            _known.Remove(kv.Key);
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, AlertMemo>>(json, JsonOpts);
            if (loaded is not null) _known = new Dictionary<string, AlertMemo>(loaded, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // Битый файл состояния — не повод не стартовать. Худшее последствие:
            // однократный повтор уведомлений по горящим алертам.
            _log.LogWarning(ex, "Не удалось прочитать состояние алертов {Path}", _path);
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_known, JsonOpts));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось сохранить состояние алертов {Path}", _path);
        }
    }
}
