using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Ступень 2 отбора серых чатов (Ф2, ADR-035 решение 12а, N1a): модель читает урезанный отрезок
// серого чата и отвечает одним JSON {hasKnowledge, about, reason} — «есть ли тут что-то стоящее
// для вики». Вердикт пишется в стор (stage2Zone/stage2Reason/stage2HistoryCount) и показывается
// списком отбора с приоритетом nothing_new > ступень 2 > ступень 1 (WikiChatsController.ShownZone).
//
// Инварианты:
// - Отказ ступени 2 → серая зона. Любой сбой (нет маршрута, исключение модели, отмена ct, пустой
//   ответ, невалидный JSON, JSON без булева hasKnowledge, нечитаемый стор при записи) даёт
//   Refused: stage2Zone НЕ пишется, чат остаётся серым, исключение наружу не летит — ночной
//   BackgroundService от него не падает.
// - Skip побеждает балл. Модель зовётся ТОЛЬКО для записи со stage1Zone=gray: skip, исполнение
//   задач, important/закреплённый/«Итог», triage и skipped её не вызывают. Пометка и исполнение
//   задач перепроверяются по самой сессии — stage1Zone в сторе может отстать от свежей пометки.
// - Вход режет код, а не модель: начало + конец + окна вокруг сигналов (Slice), потолок
//   MaxInputChars. Профиль места — Small (NumCtx 4096), поэтому потолок символьный, не «6k токенов»:
//   6 000 символов ≈ 1 500–3 000 токенов, остаётся место под системную часть и ответ.
// - Запись вердикта — под блокировкой корня стора (UpdateStage2). stage2HistoryCount — число
//   реплик человека, замеренное ДО вызова модели: выросла история за время вызова — вердикт всё
//   равно пишется, а ночь переспросит при +3 реплики человека.
public sealed class WikiChatStage2
{
    // Бюджет входа (символы): начало и конец диалога целиком, между ними — до 4 окон вокруг сигналов.
    public const int HeadChars = 2_000;
    public const int TailChars = 2_000;
    public const int WindowChars = 500;
    public const int MaxWindows = 4;
    // Разделитель между кусками среза; входит в потолок.
    private const string Gap = "\n[…]\n";
    public const int MaxInputChars = HeadChars + TailChars + MaxWindows * WindowChars + (MaxWindows + 1) * 5;

    // Вердикт модели — недоверенный текст: about/reason режутся, прежде чем попасть в стор.
    private const int MaxVerdictTextChars = 300;

    public const string PromptHeader =
        "Ты отбираешь чаты для базы знаний. Ниже — отрывок рабочего чата (начало, места с решениями, конец). " +
        "Реши, есть ли в нём долговечное знание: решение, договорённость, факт о проекте, который стоит записать. " +
        "Пустая болтовня, отладка без вывода, пересказ без итога — это НЕТ.\n" +
        "Ответь ТОЛЬКО JSON без пояснений: {\"hasKnowledge\": true|false, \"about\": \"о чём чат, до 100 символов\", " +
        "\"reason\": \"почему так решил, до 150 символов\"}.\n\n";

    /// <summary>Итог вызова. Judged — вердикт записан в стор (Zone — triage или skipped). Иначе
    /// модель не вердиктила: NotApplicable (чат не серый или не подходит — модель не звали) либо
    /// Refused (звали или пытались, не вышло) — в обоих случаях стор не изменён, чат остаётся
    /// в прежней зоне; Detail — причина для лога и ночной хроники.</summary>
    public enum Outcome { Judged, NotApplicable, Refused }

    /// <summary>InputTokens (WC-4c) — полный вход того ответа модели, по которому вынесен
    /// вердикт (вместе с кэшем): мера нагрузки на GPU локали, а не деньги; деньги из этого
    /// поля не считать — стоимость облачного пути в OneShotUsage.CostUsd. 0 — модель не
    /// звалась (NotApplicable) либо движок метрик не отдал.</summary>
    public sealed record Stage2Result(Outcome Outcome, string? Zone, string? Detail, long InputTokens = 0);

    private readonly ICheapTextRunner _llm;
    private readonly ISessionDirectory _sessions;
    private readonly string _dataDir;
    private readonly ILogger<WikiChatStage2>? _log;

    public WikiChatStage2(ICheapTextRunner llm, ISessionDirectory sessions, IConfiguration config,
        ILogger<WikiChatStage2>? log = null)
    {
        _llm = llm;
        _sessions = sessions;
        _log = log;
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    /// <summary>Вынести вердикт по серому чату владельца. Исключения наружу нет: любой сбой —
    /// Refused, стор при этом не меняется.</summary>
    public async Task<Stage2Result> JudgeAsync(string ownerId, string sessionId, CancellationToken ct = default)
    {
        try
        {
            return await JudgeCoreAsync(ownerId, sessionId, ct);
        }
        catch (OperationCanceledException)
        {
            return Refused("отмена");
        }
        catch (Exception e)
        {
            _log?.LogWarning(e, "Ступень 2 отбора чата {Session}: сбой, чат остаётся серым", sessionId);
            return Refused($"сбой: {e.Message}");
        }
    }

    private async Task<Stage2Result> JudgeCoreAsync(string ownerId, string sessionId, CancellationToken ct)
    {
        var session = _sessions.GetById(sessionId);
        if (session is null || _sessions.ResolveOwnerId(session) != ownerId)
            return NotApplicable("чат не найден");
        // Пометка и исполнение задач — по самой сессии: stage1Zone в сторе могло отстать.
        if (session.WikiMark == WikiMarks.Skip)
            return NotApplicable("пометка человека «не разбирать»");
        if (session.TaskExecution)
            return NotApplicable("чат исполнения задачи");
        if (session.WikiMark == WikiMarks.Important)
            return NotApplicable("пометка человека «важный»");

        var root = WikiPaths.Root(_dataDir, ownerId);
        var read = WikiChatTriageStore.Get(root, _log);
        if (read.State == WikiTriageFileState.Invalid)
            return Refused("стор разбора чатов не читается");
        var entry = read.Entries.FirstOrDefault(e => e.SessionId == sessionId);
        if (entry?.Stage1Zone != WikiChatScorer.ZoneGray)
            return NotApplicable("чат не в серой зоне ступени 1");

        if (!_llm.HasRoute(LocalActionCatalog.WikiTriage))
            return Refused("у места wiki-triage нет маршрута");

        var history = await _sessions.GetHistoryAsync(sessionId);
        ct.ThrowIfCancellationRequested();
        var lines = WikiChatSlicer.Lines(history, 1, history.Count);
        var humanCount = lines.Count(l => l.IsHuman);
        if (lines.Count == 0)
            return Refused("в чате нет реплик");

        var prompt = PromptHeader + Slice(lines);
        // Решение Андрея 02.11: ступень 2 не уходит в облако. RunLocalOrChosenDetailedAsync —
        // та же цепочка без финального claude-шага при локальном маршруте; недоступность/сбой
        // локали — отказ вызова, чат остаётся в серой зоне (stage2Zone не пишется). Фактический
        // вход этого ответа (usage движка) идёт в Stage2Result.InputTokens — ночь суммирует
        // его в итоге; метрик не было → 0, вердикт от этого не меняется. Отмена — не сбой,
        // её пробрасываем.
        string? text;
        long inputTokens = 0;
        try
        {
            var answer = await _llm.RunLocalOrChosenDetailedAsync(
                LocalActionCatalog.WikiTriage, prompt, ownerId, jsonFormat: "json", ct: ct);
            text = answer?.Text;
            // Вход считается по факту вызова, а не по исходу вердикта: ответ, который модель
            // дала и который мы не разобрали, понёс расход так же, как принятый.
            inputTokens = answer?.Usage?.TotalInputTokens ?? 0;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Refused($"модель не ответила: {e.Message}");
        }
        if (string.IsNullOrWhiteSpace(text))
            return Refused("модель дала пустой ответ");
        if (!TryParseVerdict(text, out var hasKnowledge, out var about, out var why))
            return new Stage2Result(Outcome.Refused, null,
                "ответ модели не контракт {hasKnowledge, about, reason}", inputTokens);

        var zone = hasKnowledge ? WikiChatScorer.ZoneTriage : WikiChatScorer.ZoneSkipped;
        var reason = BuildReason(hasKnowledge, about, why);
        var up = WikiChatTriageStore.UpdateStage2(root, sessionId, zone, reason, humanCount, _log);
        if (!up.Ok)
            return new Stage2Result(Outcome.Refused, null, $"вердикт не записан: {up.Code}", inputTokens);
        return new Stage2Result(Outcome.Judged, zone, reason, inputTokens);
    }

    // ─── Срез входа ────────────────────────────────────────────────────────

    /// <summary>Урезанный диалог для модели: короткий чат — целиком; длинный — HeadChars начала,
    /// TailChars конца и до MaxWindows окон по WindowChars вокруг сигналов из середины. Длина
    /// результата не больше MaxInputChars.</summary>
    internal static string Slice(IReadOnlyList<WikiChatSlicer.Line> lines)
    {
        var sb = new StringBuilder();
        foreach (var l in lines)
            sb.Append(l.IsHuman ? "Пользователь:\n" : "Ассистент:\n").Append(l.Text).Append("\n\n");
        var text = sb.ToString().Trim();
        if (text.Length <= MaxInputChars)
            return text;

        var head = text[..HeadChars];
        var tail = text[^TailChars..];
        var middleStart = HeadChars;
        var middleEnd = text.Length - TailChars;

        // Позиции сигналов в середине, по порядку; окно [pos-W/2, pos+W/2) не вылезает из
        // середины и не пересекается с уже взятыми.
        var lower = text.ToLowerInvariant();
        var hits = new SortedSet<int>();
        // Сигналы для окон — общий словарь скорера: обороты решения и просьбы «запиши»/
        // «поставь задачу» (тот же словарь, что сигнал «ушло в заметки и задачи»).
        foreach (var word in WikiChatScorer.DecisionWords.Concat(WikiChatScorer.NoteTaskRequestPhrases))
        {
            var at = middleStart;
            while ((at = lower.IndexOf(word, at, middleEnd - at, StringComparison.Ordinal)) >= 0)
            {
                hits.Add(at);
                at += word.Length;
            }
        }
        var windows = new List<(int From, int To)>();
        foreach (var pos in hits)
        {
            if (windows.Count >= MaxWindows) break;
            var from = Math.Clamp(pos - WindowChars / 2, middleStart, Math.Max(middleStart, middleEnd - WindowChars));
            var to = Math.Min(from + WindowChars, middleEnd);
            if (windows.Count > 0 && from < windows[^1].To) continue;
            windows.Add((from, to));
        }

        var outSb = new StringBuilder(head);
        foreach (var (from, to) in windows)
            outSb.Append(Gap).Append(text, from, to - from);
        outSb.Append(Gap).Append(tail);
        return outSb.ToString();
    }

    // ─── Разбор ответа ────────────────────────────────────────────────────

    // Ответ мелкой модели бывает в прозе: берём первый «{» и последний «}». hasKnowledge обязан
    // быть булевым — строка "true" или отсутствие поля это отказ, а не угаданный вердикт.
    internal static bool TryParseVerdict(string raw, out bool hasKnowledge, out string? about, out string? reason)
    {
        hasKnowledge = false;
        about = reason = null;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
            return false;
        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("hasKnowledge", out var hk)
                || hk.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return false;
            hasKnowledge = hk.GetBoolean();
            about = StringOrNull(root, "about");
            reason = StringOrNull(root, "reason");
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? StringOrNull(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string BuildReason(bool hasKnowledge, string? about, string? why)
    {
        var parts = new List<string> { hasKnowledge ? "ступень 2: есть знание" : "ступень 2: знания нет" };
        if (Clean(about) is { Length: > 0 } a) parts.Add(a);
        if (Clean(why) is { Length: > 0 } w) parts.Add(w);
        return string.Join(": ", parts);
    }

    private static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var one = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return one.Length <= MaxVerdictTextChars ? one : one[..MaxVerdictTextChars];
    }

    private static Stage2Result NotApplicable(string why) => new(Outcome.NotApplicable, null, why);
    private static Stage2Result Refused(string why) => new(Outcome.Refused, null, why);
}
