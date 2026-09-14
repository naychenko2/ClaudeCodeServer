namespace ClaudeHomeServer.Services.Llm.Claude;

// SubagentRunPassport переехал в Core (`Core/Services/Llm/SubagentRunPassport.cs`,
// этап 5, шаг 2) — для разрыва цикла Llm ⇄ Turn через шину SubagentRunCompleted
// (Services/Turn/TurnEvents.cs). Побочно — импорт
// `using ClaudeHomeServer.Services.Llm.Claude;` из TurnEvents больше не нужен,
// закрывая «одно место Llm.Claude в Turn» из задачи.

/// <summary>
/// Паспорта прогонов сабагентов: последние N штук в памяти, отдаются через GET /api/subagents/runs.
///
/// ЗАЧЕМ. Сабагенты систематически «отваливаются» посреди работы, а продукт принимает обрывок
/// за готовый результат. Разбор двух прогонов вручную (транскрипты agent-*.jsonl) дал факты,
/// которые здесь и требуется набирать автоматически, вместо гадания:
///  1. stop_reason за прогон: tool_use 70 и 130 раз, end_turn 1 и 2 раза. max_tokens — ни разу.
///  2. end_turn встречается ТОЛЬКО у финальных отчётов, пришедших после ручного добивания.
///     В момент обрыва последнее сообщение агента — tool_use: инструмент вызван, результат
///     получен, продолжения от модели нет.
///  3. Момент возврата результата родителю совпадает с последней активностью агента
///     (старт + длительность = таймстемп последней записи): дальше агент молчит, пока не толкнут.
///  4. Ошибок нет ни одной: ни overloaded, ни rate_limit, ни таймаутов, ни API Error.
///  5. Наш ватчдог не при чём: ClaudeSession.IdleTimeout = 60 мин, агенты жили 9 и 17 мин.
///  6. CLAUDE_CODE_PRINT_BG_WAIT_CEILING_MS=0 не при чём: CLI документирует «=0 to wait
///     indefinitely», наш код верен.
///  7. Порог остановки НЕ определён: не время (9 и 17 мин), не число вызовов инструментов
///     (59 и 51), не токены (133k и 162k). В нашем коде такого лимита нет — решение
///     принимается вне продукта.
///  8. Контекст агента при этом цел: добивание сообщением возобновляет работу с того же места.
/// Паспорта нужны, чтобы на десятке прогонов увидеть, есть ли общая граница (токены/вызовы/
/// время/тип агента) — поэтому в записи лежат все четыре измерения разом.
///
/// КЛАССЫ ОБРЫВА по <c>FinishedBy</c>. Один и тот же tool_use-хвост без end_turn у паспорта
/// может означать три разные истории, и текст директивы добивания обязан это различать:
///  • <c>bg_done</c>/<c>bg_aborted</c> — продукт объявил фон завершённым, координатору нужна
///    пометка «обрывок НЕ итог» (двухходовая: добивание отправится отдельным systemDirective);
///  • <c>run_end</c> — ватчер сам дренажировал хвост при завершении хода (CLI умер сам);
///  • <c>interrupted</c> — прогон убит прерыванием хода (ClaudeSession.Interrupt → Kill
///    процесса). Отдельный класс потому, что причина здесь целиком в нашем коде: человек
///    нажал «Стоп», агент не «замолчал», а был убит, и слать ему директиву
///    «продолжи с места остановки» бессмысленно — транскрипт цел, добивание вернётся с
///    новым ходом если пользователь его захочет. Снимает шум в дневном jsonl и убирает
///    ложные срабатывания политики добиваний (счётчик MaxSubagentNudges этим классом
///    не расходуется — причина известна достоверно, признака неисправного агента нет).
///
/// Отдача через API — из памяти (последние MaxRuns), но паспорта ДУБЛИРУЮТСЯ на диск:
/// боевой инстанс перезапускается по нескольку раз в день, а разбор обрывов идёт как раз
/// по серии прогонов за вчера-сегодня — в памяти от них не остаётся ничего, и расследование
/// сваливается в ручное чтение agent-*.jsonl. Формат и место — по конвенции файлового лога
/// (<see cref="Diagnostics.FileLog"/>): data/logs/subagent-runs-YYYYMMDD.jsonl, дневная
/// ротация, удержание Logging:File:RetainDays.
///
/// Бэкап: data/logs целиком исключён из архива (BackupPaths, корень "logs") — так и надо,
/// это диагностика, а не пользовательские данные; восстанавливать её неоткуда и незачем
/// (см. «Новое хранилище → сверься с бэкапом» в CLAUDE.md).
/// </summary>
public sealed class SubagentRunLog
{
    // Хватает на разбор серии прогонов и не растёт бесконечно на долгоживущем процессе
    private const int MaxRuns = 200;

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string? _dir;
    private readonly int _retainDays;
    private readonly Lock _ioLock = new();
    private readonly Lock _lock = new();

    /// <summary>Только память (тесты и вызовы без конфига).</summary>
    public SubagentRunLog() { }

    /// <summary>Память + дневной jsonl в <paramref name="dir"/> (обычно data/logs).</summary>
    public SubagentRunLog(string? dir, int retainDays)
    {
        _dir = string.IsNullOrWhiteSpace(dir) ? null : dir;
        _retainDays = Math.Max(1, retainDays);
    }

    /// <summary>Приёмник паспортов по конфигу: data/logs рядом с серверным логом.</summary>
    public static SubagentRunLog Create(IConfiguration config)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        return new SubagentRunLog(
            Path.Combine(dataDir, "logs"),
            config.GetValue("Logging:File:RetainDays", 14));
    }
    // Свежие — в конце; ключ дедупликации — AgentId (добитый агент дописывает ТОТ ЖЕ транскрипт,
    // и его паспорт обновляется, а не задваивается)
    private readonly List<SubagentRunPassport> _runs = [];
    // Последний оборванный прогон по чату — сигнал для реакции «продолжить» (потребляется один раз)
    private readonly Dictionary<string, SubagentRunPassport> _truncatedBySession = [];

    /// <summary>Записать/обновить паспорт прогона.</summary>
    public void Record(SubagentRunPassport passport)
    {
        lock (_lock)
        {
            var i = _runs.FindIndex(r => r.AgentId == passport.AgentId);
            if (i >= 0)
            {
                // Счётчик добиваний ведёт не ватчер, а тот, кто добивал (NoteNudge) — при
                // обновлении паспорта его нельзя потерять
                passport = passport with { NudgeAttempts = Math.Max(passport.NudgeAttempts, _runs[i].NudgeAttempts) };
                _runs.RemoveAt(i);
            }
            _runs.Add(passport);
            while (_runs.Count > MaxRuns) _runs.RemoveAt(0);

            if (passport.SessionId is { Length: > 0 } sid)
            {
                if (passport.Truncated) _truncatedBySession[sid] = passport;
                else _truncatedBySession.Remove(sid);
            }
        }
        AppendToFile(passport);
    }

    // Дневной jsonl рядом с серверным логом. Паспорт дописывается КАЖДЫМ обновлением (добитый
    // агент завершается повторно тем же AgentId) — дедуп в памяти, а на диске нужна как раз
    // история: строки одного AgentId показывают, чем кончилась каждая попытка. Сбой записи
    // тушим: диагностика не имеет права ронять ход (и уходить в stderr — там свой файл).
    private void AppendToFile(SubagentRunPassport passport)
    {
        if (_dir is null) return;
        try
        {
            lock (_ioLock)
            {
                Directory.CreateDirectory(_dir);
                File.AppendAllText(
                    Path.Combine(_dir, $"subagent-runs-{DateTime.UtcNow:yyyyMMdd}.jsonl"),
                    System.Text.Json.JsonSerializer.Serialize(passport, JsonOpts) + Environment.NewLine);
                DeleteStaleFiles();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SubagentRunLog] паспорт не записан: {ex.Message}");
        }
    }

    // Удержание — как у FileLog: дату берём из имени файла, а не из mtime (копирование
    // каталога ломает mtime, имя стабильно). Чистка вспомогательная, ошибки глотаем.
    private void DeleteStaleFiles()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(_dir!, "subagent-runs-*.jsonl"))
            {
                var stamp = Path.GetFileNameWithoutExtension(path)["subagent-runs-".Length..];
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

    /// <summary>
    /// Забрать отметку об оборванном сабагенте чата (одноразово). Потребитель — исполнитель
    /// задач: обрыв в его ходе значит «работа не закончена», а не «задача выполнена».
    /// </summary>
    public SubagentRunPassport? TakeTruncated(string sessionId)
    {
        lock (_lock)
        {
            if (!_truncatedBySession.Remove(sessionId, out var passport)) return null;
            return passport;
        }
    }

    /// <summary>Отметить отправленное добивание агента (растёт до потолка попыток).</summary>
    public void NoteNudge(string agentId)
    {
        SubagentRunPassport? updated = null;
        lock (_lock)
        {
            var i = _runs.FindIndex(r => r.AgentId == agentId);
            if (i >= 0) updated = _runs[i] = _runs[i] with { NudgeAttempts = _runs[i].NudgeAttempts + 1 };
        }
        // Инкремент только в памяти систематически занижал nudgeAttempts в дневном jsonl
        // (паспорт с новым счётчиком туда не доезжал, если агент больше не завершался) —
        // дописываем обновлённую строку; история строк по AgentId в файле by design
        if (updated is not null) AppendToFile(updated);
    }

    /// <summary>Последний известный паспорт агента (в памяти — один на AgentId).</summary>
    public SubagentRunPassport? Latest(string agentId)
    {
        lock (_lock)
            return _runs.Find(r => r.AgentId == agentId);
    }

    /// <summary>Последние паспорта, свежие — первыми.</summary>
    public IReadOnlyList<SubagentRunPassport> Recent(int limit = 50)
    {
        lock (_lock)
            return [.. Enumerable.Reverse(_runs).Take(Math.Clamp(limit, 1, MaxRuns))];
    }

    /// <summary>Сводка: сколько прогонов и какая доля из них оборвана (по типам агентов).</summary>
    public IReadOnlyList<SubagentTypeStat> Stats()
    {
        lock (_lock)
            return [.. _runs
                .GroupBy(r => r.AgentType ?? "(без типа)")
                .Select(g => new SubagentTypeStat(
                    g.Key,
                    g.Count(),
                    g.Count(r => r.Truncated),
                    (int)g.Average(r => r.DurationSeconds),
                    (int)g.Average(r => r.ToolUses),
                    (long)g.Average(r => (double)r.ContextTokens)))
                .OrderByDescending(s => s.Truncated)
                .ThenByDescending(s => s.Runs)];
    }
}

public record SubagentTypeStat(
    string AgentType, int Runs, int Truncated, int AvgSeconds, int AvgToolUses, long AvgContextTokens);
