using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Services.Llm;

/// <summary>
/// Паспорт одного хода глазами оркестратора фолбэка: чем кончился, сколько попыток стоил и на
/// какой паре «модель × провайдер» встал.
/// </summary>
/// <param name="SessionId">чат, в котором шёл ход.</param>
/// <param name="Outcome">
/// исход: success | failed | egress_down | interrupted | cancelled | crashed.
/// Отдельный egress_down — не педантизм: он отвечает на вопрос «виноват вендор или наш канал»,
/// а именно этот вопрос при разборе суток занимает больше всего времени.
/// </param>
/// <param name="Attempts">сколько попыток доставки сделано (1 — ход прошёл с первого раза).</param>
/// <param name="Substitutions">из них смен ТИПА поставщика (шаг цепочки/сторонний провайдер).</param>
/// <param name="EgressRetries">повторов той же пары из-за лежащего выхода в сеть.</param>
/// <param name="Chain">цепочка хода — модели пресета в порядке фолбэка.</param>
/// <param name="LastErrorClass">wire-имя класса последней ошибки (rate_limit, unreachable…).</param>
/// <param name="LastError">сырой текст последней ошибки, усечённый — для глаз, не для разбора.</param>
public sealed record TurnRunPassport(
    string SessionId,
    DateTime StartedAt,
    DateTime EndedAt,
    int DurationSeconds,
    string Outcome,
    string? StartModel,
    string? StartProvider,
    string? FinalModel,
    string? FinalProvider,
    int Attempts,
    int Substitutions,
    int EgressRetries,
    IReadOnlyList<string> Chain,
    string? LastErrorClass,
    string? LastError,
    int ContextTokens,
    DateTime RecordedAt)
{
    /// <summary>Ход не состоялся — человек увидел ошибку вместо ответа.</summary>
    [JsonIgnore]
    public bool Failed => Outcome is "failed" or "egress_down" or "crashed";
}

/// <summary>
/// Паспорта ходов: последние N в памяти (отдаются через GET /api/turns/runs), плюс дневной
/// jsonl на диске.
///
/// ЗАЧЕМ. У прогонов сабагентов паспорт есть, а у самих ходов — не было, и вопрос «что ломалось
/// за сутки» решался раскопками по трём несвязанным источникам: серверный лог (коды выхода
/// процессов CLI без причины), ленты чатов (тексты ошибок без контекста попыток) и sessions.json
/// (итоговый статус). Разбор 25.08.2026 занял час и дал ответ, который здесь виден одним grep:
/// 10 из 14 сбоев — общий канал наружу, а не вендор.
///
/// Пишет ОДИН источник — <see cref="FallbackLlmSessionAdapter"/>: через него идёт каждый ход
/// (фабрика оборачивает им ClaudeSession всегда), поэтому запись в его терминалах покрывает все
/// ходы и не дублируется.
///
/// Формат и место — по конвенции файлового лога (<see cref="Diagnostics.FileLog"/>):
/// data/logs/turn-runs-YYYYMMDD.jsonl, дневная ротация, удержание Logging:File:RetainDays.
/// Бэкап: data/logs целиком исключён из архива (BackupPaths, корень "logs") — это диагностика,
/// а не пользовательские данные.
/// </summary>
public sealed class TurnRunLog
{
    // Сутки активной работы — это порядка 300 ходов; держать в памяти больше незачем,
    // история за прошлые дни живёт на диске
    private const int MaxRuns = 300;

    // Сырой текст ошибки в паспорте — опознавательный знак, а не улика: полный текст уже есть
    // в ленте (Details) и в логе сервера
    private const int MaxErrorLength = 300;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string? _dir;
    private readonly int _retainDays;
    private readonly Lock _ioLock = new();
    private readonly Lock _lock = new();
    // Свежие — в конце
    private readonly List<TurnRunPassport> _runs = [];

    /// <summary>Только память (тесты и вызовы без конфига).</summary>
    public TurnRunLog() { }

    /// <summary>Память + дневной jsonl в <paramref name="dir"/> (обычно data/logs).</summary>
    public TurnRunLog(string? dir, int retainDays)
    {
        _dir = string.IsNullOrWhiteSpace(dir) ? null : dir;
        _retainDays = Math.Max(1, retainDays);
    }

    /// <summary>Приёмник паспортов по конфигу: data/logs рядом с серверным логом.</summary>
    public static TurnRunLog Create(IConfiguration config)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        return new TurnRunLog(
            Path.Combine(dataDir, "logs"),
            config.GetValue("Logging:File:RetainDays", 14));
    }

    /// <summary>Записать паспорт завершившегося хода.</summary>
    public void Record(TurnRunPassport passport)
    {
        if (passport.LastError is { Length: > MaxErrorLength } long_)
            passport = passport with { LastError = long_[..MaxErrorLength] + "…" };

        lock (_lock)
        {
            _runs.Add(passport);
            while (_runs.Count > MaxRuns) _runs.RemoveAt(0);
        }
        AppendToFile(passport);
    }

    // Сбой записи тушим: диагностика не имеет права ронять ход (и уходить в stderr — там свой файл)
    private void AppendToFile(TurnRunPassport passport)
    {
        if (_dir is null) return;
        try
        {
            lock (_ioLock)
            {
                Directory.CreateDirectory(_dir);
                File.AppendAllText(
                    Path.Combine(_dir, $"turn-runs-{DateTime.UtcNow:yyyyMMdd}.jsonl"),
                    JsonSerializer.Serialize(passport, JsonOpts) + Environment.NewLine);
                DeleteStaleFiles();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TurnRunLog] паспорт не записан: {ex.Message}");
        }
    }

    // Удержание — как у FileLog: дату берём из имени файла, а не из mtime (копирование каталога
    // ломает mtime, имя стабильно). Чистка вспомогательная, ошибки глотаем.
    private void DeleteStaleFiles()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(_dir!, "turn-runs-*.jsonl"))
            {
                var stamp = Path.GetFileNameWithoutExtension(path)["turn-runs-".Length..];
                if (!DateTime.TryParseExact(stamp, "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.AssumeUniversal
                        | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var fileDate))
                    continue;
                if ((DateTime.UtcNow - fileDate).TotalDays > _retainDays)
                {
                    try { File.Delete(path); }
                    catch { /* занят/нет прав — уйдёт на следующей чистке */ }
                }
            }
        }
        catch { /* чистка вспомогательная */ }
    }

    /// <summary>Последние паспорта, свежие — первыми.</summary>
    public IReadOnlyList<TurnRunPassport> Recent(int limit = 50)
    {
        lock (_lock)
            return [.. Enumerable.Reverse(_runs).Take(Math.Clamp(limit, 1, MaxRuns))];
    }

    /// <summary>
    /// Сводка по исходам: сколько ходов и с какой причиной встали. Ровно тот срез, ради
    /// которого журнал и заводился — «что ломалось за сутки» одним взглядом.
    /// </summary>
    public TurnRunSummary Summary()
    {
        lock (_lock)
            return new TurnRunSummary(
                _runs.Count,
                _runs.Count(r => r.Failed),
                [.. _runs.GroupBy(r => r.Outcome)
                    .Select(g => new TurnOutcomeStat(g.Key, g.Count()))
                    .OrderByDescending(s => s.Turns)],
                [.. _runs.Where(r => r.LastErrorClass is not null)
                    .GroupBy(r => r.LastErrorClass!)
                    .Select(g => new TurnErrorClassStat(g.Key, g.Count()))
                    .OrderByDescending(s => s.Turns)]);
    }
}

public record TurnRunSummary(
    int Turns,
    int Failed,
    IReadOnlyList<TurnOutcomeStat> ByOutcome,
    IReadOnlyList<TurnErrorClassStat> ByErrorClass);

public record TurnOutcomeStat(string Outcome, int Turns);

public record TurnErrorClassStat(string ErrorClass, int Turns);
