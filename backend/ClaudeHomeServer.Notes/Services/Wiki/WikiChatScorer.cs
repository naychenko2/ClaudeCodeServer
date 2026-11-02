using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// S6f (ADR-035, решение 12а): ступень 1 отбора чатов — балл кода, бесплатно, на каждый чат.
// ЧИСТАЯ ФУНКЦИЯ: вход — история чата (StoredMessage[]) и поля сессии, выход — балл, signals,
// зона, причина; ни файлов, ни времени (кроме переданных входе), ни моделей. Теневой режим
// шага: балл и зона считаются и видны, но ничего не запускают — разбор остаётся по кнопке (Ф1).
//
// Ступень 0 (исключения кода, ADR-035:184): чаты исполнения задач, без единой реплики человека,
// короче 1 500 символов диалога, пометка «не разбирать». Порядок проверки — силой исключения:
// «skip» и исполнение задач сильнее пометки «важный» (ADR-035:214: «важный» минуяет ступени 1–2,
// но не ступень 0 для чатов исполнения; «не разбирать» — не разбирается никогда). Остальные
// ступени 0 («нет реплик», «короткий») пометкой «важный»/закреплением/«Итогом» обходятся:
// явное решение человека важнее автоматического порога.
//
// Ступень 1 (сигналы таблицы ADR-035:192–203): имена сигналов — устойчивые идентификаторы
// (их несёт стор и список), метки — русские подписи для причины. Умолчания весов — константы
// ниже; по ADR-035:190 их подбирает пилот S8, менять их место — только здесь.
//
// Исход (ADR-035:205–208): балл ≥ 15 — «triage» (разбирается); 6 ≤ балл < 15 ИЛИ обход по
// документам/объёму — «gray» (серая зона); меньше 6 — «skipped». Границы half-open: 15 — уже
// «разбирается». Обход нужен ровно для не-разработческих проектов (ADR-035:210): «одна реплика
// + разбор документа на 20k символов» по одной вовлечённости проваливается.
// Понижение балла за три «нечего добавить» подряд (ADR-035:269) — здесь же, как часть балла:
// nothingNewStreak приходит из стора, штраф — сигнал с отрицательным весом. Итоговый
// балл не уходит ниже 0 (STORE не принимает отрицательных score, см. WikiChatTriageStore).
//
// Зона nothing_new («нечего добавить») — не выход скорера: её ставит исход разбора
// (WikiChatIngestService.ZoneNothingNew); скорер её только не перекрывает при пересчёте,
// если по ней идёт штраф.
public static class WikiChatScorer
{
    // Зоны отбора — словарь стора chat-triage.json (zone). nothing_new — у
    // WikiChatIngestService (исход разбора), здесь его нет.
    public const string ZoneExcluded = "excluded"; // ступень 0
    public const string ZoneTriage = "triage"; // разбирается
    public const string ZoneGray = "gray"; // серая зона
    public const string ZoneSkipped = "skipped"; // пропущено

    // Имена сигналов — контракт стора и списка (стабильные идентификаторы, не переводимые).
    public const string SignalHumanTurns = "human_turns";
    public const string SignalSubstantiveTurns = "substantive_turns";
    public const string SignalDecisionWords = "decision_words";
    public const string SignalCourseCorrections = "course_corrections";
    public const string SignalWentToNotesTasks = "went_to_notes_tasks";
    public const string SignalDocumentWork = "document_work";
    public const string SignalAnswerVolume = "answer_volume";
    public const string SignalTopicReturn = "topic_return";
    public const string SignalLinks = "links";
    public const string SignalNothingNewPenalty = "nothing_new_penalty";

    // ─── Умолчания весов (ADR-035:190 — «умолчания весов — в WikiChatScorer, их подбирает пилот») ───

    public const int ThresholdTriage = 15; // балл ≥ 15 — «разбирается»
    public const int ThresholdGray = 6; // 6 ≤ балл < 15 — серая зона
    public const int MinDialogChars = 1_500; // ступень 0: «короче 1 500 символов диалога»

    public const int WeightHumanTurn = 1;
    public const int CapHumanTurns = 6;
    public const int SubstantiveTurnChars = 300; // «содержательная» реплика человека
    public const int WeightSubstantiveTurn = 2;
    public const int CapSubstantiveTurns = 6;
    public const int WeightDecisionWord = 4; // за каждый найденный оборот решения (не за повтор)
    public const int CapDecisionWords = 8;
    public const int WeightCorrection = 2;
    public const int CapCorrections = 6;
    public const int WeightWentToNotesTasks = 3; // за запись инструмента или просьбу человека
    public const int CapWentToNotesTasks = 9;
    public const int WeightDocumentOp = 2; // за операцию чтения/записи документа
    public const int CapDocumentWork = 8;
    public const int AnswerVolumeCharsPerWeight = 4_000; // +1 за каждые 4k символов ответа агента
    public const int CapAnswerVolume = 6;
    public const int WeightTopicReturn = 4;
    public const int WeightLinks = 3;
    public const int CapLinks = 3;

    // Обход ступени 1 (ADR-035:207): любой из этих фактов — «серая зона» независимо от балла.
    public const int BypassAnswerChars = 10_000; // ответ агента ≥ 10k символов
    public const int BypassDocumentsRead = 3; // разбор ≥ 3 документов
    public const int BypassStructuredAnswerChars = 4_000; // структурированный ответ ≥ 4k символов
    public const int StructuredMarkerMinLines = 2; // «структурный»: заголовков/списков минимум столько строк

    // Понижение балла за три «нечего добавить» подряд (ADR-035:269).
    public const int NothingNewPenaltyStreak = 3;
    public const int NothingNewPenalty = 10;

    // «Живёт дольше суток» (сигнал возврата к теме) — по полям сессии, без часов процесса.
    public const int TopicReturnHours = 24;

    // ─── Словари сигналов ───

    internal static readonly string[] DecisionWords = ["решили", "договорились", "выбираем", "не делаем", "итог"];
    private static readonly string[] CorrectionStarts = ["нет", "не так", "стоп", "лучше", "наоборот"];
    private static readonly string[] NoteTaskToolSuffixes = ["notes_create", "notes_update", "tasks_create", "memory_remember"];
    internal static readonly string[] NoteTaskRequestPhrases = ["запиши", "поставь задачу"];
    private static readonly string[] ReadToolNames = ["read", "files_read"];
    private static readonly string[] WriteToolNames = ["write", "edit", "multiedit", "files_write"];
    private static readonly string[] DocExtensions = [".md", ".docx", ".pdf", ".xlsx", ".pptx"];

    // Метки — русские подписи для причины («балл и три главных сигнала», ADR-035:217).
    private static readonly Dictionary<string, string> SignalLabels = new()
    {
        [SignalHumanTurns] = "вовлечённость человека",
        [SignalSubstantiveTurns] = "содержательные реплики",
        [SignalDecisionWords] = "формулировки решений",
        [SignalCourseCorrections] = "правки направления",
        [SignalWentToNotesTasks] = "ушло в заметки и задачи",
        [SignalDocumentWork] = "работа с документами",
        [SignalAnswerVolume] = "объём содержательного ответа",
        [SignalTopicReturn] = "возврат к теме",
        [SignalLinks] = "связи",
        [SignalNothingNewPenalty] = "«нечего добавить» подряд",
    };

    // Ссылки человека: [[…]], task:, путь файла проекта с расширением.
    private static readonly Regex LinkPattern = new(
        @"\[\[|task:|[\w/.\-]+\.(md|docx|pdf|xlsx|pptx|cs|ts|tsx|json)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Вход скорера: поля сессии + её история. Собирает вызывающий (WikiChatsController);
    /// сам скорер ни к сессии, ни к хранилищу не обращается.</summary>
    public sealed record WikiChatInput(
        bool TaskExecution,
        string? WikiMark,
        bool IsPinned,
        bool HasSummary,
        string? ContinuedFromSessionId,
        string? BranchedFromSessionId,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        int NothingNewStreak,
        IReadOnlyList<StoredMessage> Messages);

    /// <summary>Выход: зона + балл + сигналы (по убыванию веса) + причина для человека.
    /// Для ступени 0 и обхода пометкой балл не считается (0) — причина называет правило.</summary>
    public readonly record struct WikiChatScoreResult(
        string Zone,
        int Score,
        IReadOnlyList<WikiSignalScore> Signals,
        string Reason);

    public readonly record struct WikiSignalScore(string Name, int Weight);

    /// <summary>Оценить чат. Чистая функция: тот же вход — тот же выход; исключений наружу нет.</summary>
    public static WikiChatScoreResult Score(WikiChatInput input)
    {
        var messages = input.Messages ?? [];
        // Реплика человека — user_message без auto/systemDirective/viaAgent (ADR-035:194).
        var humanTurns = messages.OfType<StoredUserMessage>()
            .Where(m => m.Auto != true && m.SystemDirective != true && m.ViaAgent != true
                && !string.IsNullOrWhiteSpace(m.Text))
            .ToList();
        // Содержательный ответ агента — текст основного агента без вызовов инструментов и без
        // текстов сабагентов (ParentToolUseId) — ADR-035:200.
        var agentTexts = messages.OfType<StoredTextMessage>()
            .Where(m => m.ParentToolUseId is null && !string.IsNullOrEmpty(m.Text))
            .ToList();

        // ── Ступень 0 ──
        if (input.WikiMark == WikiMarks.Skip)
            return Excluded("ступень 0: пометка человека «не разбирать»");
        if (input.TaskExecution)
            return Excluded("ступень 0: чат исполнения задачи");
        // Пометка человека перекрывает автомат (ADR-035:214): «важный», закреплённый и чат с
        // «Итогом» — разбирается всегда; ступень 0 для них остаётся только исполнением задач
        // и skip (проверены выше).
        if (input.WikiMark == WikiMarks.Important)
            return ByMark("пометка человека «важный» — минуяет ступень 1");
        if (input.IsPinned)
            return ByMark("чат закреплён — приравнен к «важный»");
        if (input.HasSummary)
            return ByMark("у чата есть «Итог» — приравнен к «важный»");
        if (humanTurns.Count == 0)
            return Excluded("ступень 0: нет ни одной реплики человека");
        int dialogChars = humanTurns.Sum(m => m.Text.Length) + agentTexts.Sum(m => m.Text.Length);
        if (dialogChars < MinDialogChars)
            return Excluded($"ступень 0: диалог короче {MinDialogChars} символов");

        // ── Ступень 1 ──
        var signals = new List<WikiSignalScore>();

        Add(signals, SignalHumanTurns, Math.Min(humanTurns.Count * WeightHumanTurn, CapHumanTurns));

        Add(signals, SignalSubstantiveTurns,
            Math.Min(humanTurns.Count(m => m.Text.Length > SubstantiveTurnChars) * WeightSubstantiveTurn, CapSubstantiveTurns));

        // Формулировки решений — в репликах человека и в последних ответах агента (ADR-035:196).
        // Считается наличие оборота, а не число повторов: повтор не добавляет ценности.
        var decisionHaystack = string.Join("\n", humanTurns.Select(m => m.Text))
            + "\n" + string.Join("\n", agentTexts.TakeLast(3).Select(m => m.Text));
        var decisionLower = decisionHaystack.ToLowerInvariant();
        Add(signals, SignalDecisionWords,
            Math.Min(DecisionWords.Count(w => decisionLower.Contains(w)) * WeightDecisionWord, CapDecisionWords));

        Add(signals, SignalCourseCorrections,
            Math.Min(humanTurns.Count(m => StartsAsCorrection(m.Text)) * WeightCorrection, CapCorrections));

        // Ушло в заметки и задачи: запись инструмента или просьба человека («запиши», «поставь задачу»).
        var toolUses = messages.OfType<StoredToolUseMessage>().ToList();
        int wentCount = toolUses.Count(t => NoteTaskToolSuffixes.Any(s => t.Name.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            + humanTurns.Count(m => NoteTaskRequestPhrases.Any(p => m.Text.ToLowerInvariant().Contains(p)));
        Add(signals, SignalWentToNotesTasks, Math.Min(wentCount * WeightWentToNotesTasks, CapWentToNotesTasks));

        // Работа с документами: чтение/запись .md/.docx/.pdf/.xlsx/.pptx и вложения человека.
        int docsRead = 0, docsWrite = 0;
        foreach (var t in toolUses)
        {
            var path = InputPath(t.Input);
            if (path is null || !IsDocPath(path)) continue;
            if (ReadToolNames.Any(n => t.Name.EndsWith(n, StringComparison.OrdinalIgnoreCase))) docsRead++;
            else if (WriteToolNames.Any(n => t.Name.EndsWith(n, StringComparison.OrdinalIgnoreCase))) docsWrite++;
        }
        int attachedDocs = humanTurns.SelectMany(m => m.AttachedPaths ?? []).Count(IsDocPath);
        Add(signals, SignalDocumentWork, Math.Min((docsRead + docsWrite + attachedDocs) * WeightDocumentOp, CapDocumentWork));

        // Объём содержательного ответа.
        int answerChars = agentTexts.Sum(m => m.Text.Length);
        Add(signals, SignalAnswerVolume, Math.Min(answerChars / AnswerVolumeCharsPerWeight, CapAnswerVolume));

        // Возврат к теме: чат живёт дольше суток, продолжен или ветвлён.
        bool topicReturn = (input.UpdatedAt - input.CreatedAt).TotalHours > TopicReturnHours
            || !string.IsNullOrEmpty(input.ContinuedFromSessionId)
            || !string.IsNullOrEmpty(input.BranchedFromSessionId);
        Add(signals, SignalTopicReturn, topicReturn ? WeightTopicReturn : 0);

        // Связи в репликах человека: [[…]], task:, путь файла.
        Add(signals, SignalLinks,
            humanTurns.Any(m => LinkPattern.IsMatch(m.Text)) ? WeightLinks : 0);

        // Понижение за три «нечего добавить» подряд (ADR-035:269): долгий рабочий чат без
        // решений перестаёт тратить ночь.
        if (input.NothingNewStreak >= NothingNewPenaltyStreak)
            Add(signals, SignalNothingNewPenalty, -NothingNewPenalty);

        // Балл и зона. Границы half-open: [15,∞) — triage, [6,15) — gray, [0,6) — skipped;
        // обход поднимает в gray независимо от балла (кроме triage — там и так разбирается).
        int score = Math.Max(0, signals.Sum(s => s.Weight));
        var bypass = BypassReason(answerChars, docsRead, docsWrite, agentTexts);
        string zone = score >= ThresholdTriage ? ZoneTriage
            : score >= ThresholdGray || bypass is not null ? ZoneGray
            : ZoneSkipped;

        var ordered = signals.OrderByDescending(s => s.Weight).ToList();
        string reason = BuildReason(score, ordered, bypass);
        return new WikiChatScoreResult(zone, score, ordered, reason);
    }

    // Обход ступени 1 (ADR-035:207): ответ агента ≥ 10k символов, разбор ≥ 3 документов,
    // запись документа, структурированный ответ ≥ 4k символов. Возвращает человекочитаемое
    // описание первого сработавшего факта либо null.
    private static string? BypassReason(int answerChars, int docsRead, int docsWrite, List<StoredTextMessage> agentTexts)
    {
        if (answerChars >= BypassAnswerChars)
            return $"обход: ответ агента {answerChars} символов (≥{BypassAnswerChars})";
        if (docsRead >= BypassDocumentsRead)
            return $"обход: прочитано документов {docsRead} (≥{BypassDocumentsRead})";
        if (docsWrite > 0)
            return "обход: записан документ";
        var structured = agentTexts.FirstOrDefault(m => m.Text.Length >= BypassStructuredAnswerChars && IsStructured(m.Text));
        if (structured is not null)
            return $"обход: структурированный ответ {structured.Text.Length} символов (≥{BypassStructuredAnswerChars})";
        return null;
    }

    // «Структурный ответ»: заголовки или списки (≥ StructuredMarkerMinLines строк-маркеров).
    private static bool IsStructured(string text)
    {
        int markers = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimStart();
            if (line.StartsWith("##") || line.StartsWith("- ") || line.StartsWith("* ")
                || Regex.IsMatch(line, @"^\d+\.\s"))
            {
                if (++markers >= StructuredMarkerMinLines) return true;
            }
        }
        return false;
    }

    // «Правка направления»: реплика начинается с «нет», «не так», «стоп», «лучше», «наоборот»
    // как с отдельного слова («не так» и «нет» различаются границей слова).
    private static bool StartsAsCorrection(string text)
    {
        var t = text.TrimStart().ToLowerInvariant();
        return CorrectionStarts.Any(w => t.StartsWith(w) && (t.Length == w.Length || !char.IsLetter(t[w.Length])));
    }

    private static bool IsDocPath(string path) =>
        DocExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    // Путь файла из Input инструмента: Input в истории — JsonElement (object? у StoredToolUseMessage).
    private static string? InputPath(object? input)
    {
        if (input is JsonElement { ValueKind: JsonValueKind.Object } el)
        {
            foreach (var key in new[] { "path", "file_path", "filePath" })
                if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
        }
        return null;
    }

    private static void Add(List<WikiSignalScore> signals, string name, int weight)
    {
        if (weight != 0) signals.Add(new WikiSignalScore(name, weight));
    }

    // Причина — «балл и три главных сигнала» (ADR-035:217).
    private static string BuildReason(int score, List<WikiSignalScore> ordered, string? bypass)
    {
        var top = ordered.Take(3).Select(s => $"{SignalLabels.GetValueOrDefault(s.Name, s.Name)} ({s.Weight:+0;-0})");
        var parts = new List<string> { $"балл {score}" };
        if (top.Any()) parts.Add(string.Join(", ", top));
        if (bypass is not null) parts.Add(bypass);
        return string.Join(": ", parts);
    }

    private static WikiChatScoreResult Excluded(string reason) =>
        new(ZoneExcluded, 0, [], reason);

    private static WikiChatScoreResult ByMark(string reason) =>
        new(ZoneTriage, 0, [], reason);
}
