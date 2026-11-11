using System.Text;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Dossiers;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Нарезка чата кодом до модели (ADR-035, §12б этап 4) и выдержка для карточки источника
// (решение 3: «Выдержку чата сервер режет сам по sessionId и диапазону сообщений, без вызовов
// инструментов, через SecretRedactor, не больше 8 КБ»). Чистая функция над StoredMessage[] без
// I/O и состояния: историю приносит вызывающий через ISessionDirectory.GetHistoryAsync.
// Номер сообщения m{N} (§12б: «Номер сообщения m{N} — индекс записи в history.json») — 1-based
// индекс записи, та же контрактная форма, что разбирает WikiPage.TryParseSessionRef (a ≥ 1).
//
// Остаётся: реплики человека и текст ответов агента. Вырезается всё остальное — вызовы
// инструментов и их вывод (tool_use, тексты сабагентов с ParentToolUseId), размышления
// (thinking), file_changed, служебные события (result, error, compact_boundary, context_pruned,
// model_switched, ask_question, plan_review, team_* и прочие — всё, что не два разрешённых вида),
// системные директивы (SystemDirective), auto-реплики (Auto), реплики через агента (ViaAgent),
// служебные плашки штаба (StaffNote), записки-передачи. Всё через SecretRedactor.
// Образец фильтрации той же пары видов — DossierDiscussionService.BuildFeed; здесь к нему
// добавлены отсечения по признакам реплик (§12а считает «реплики человека, без auto,
// systemDirective, viaAgent» — тот же состав должен быть и в цитируемом тексте).
//
// Записка-передача (§12б этап 3: «Продолжение начинается с записки-передачи — это вывод модели,
// код его вырезает») в истории — первое пользовательское сообщение: ContinueAsync/ChatHandoffService
// не пишут для неё отдельного вида записи и не помечают её полем. Отличить её от реплики человека
// по структуре StoredMessage нельзя — отличает стабильная строка, которую вставляет сам сервер
// («Предыдущий чат:» у ChatContinueService.BuildFirstMessage, «Передаточная записка:» у
// ChatHandoffService.BuildDraft). Риск ложной находки осознанный и ограниченный: первая реплика
// человека, цитирующая эти строки, выпадет из выдержки — потерянная реплика лучше цитаты из
// модельного пересказа.
//
// Начало ветки — точка ветвления (§12б этап 3: «начальный курсор ветки ставится на точку
// ветвления, родитель отвечает за префикс»): плашка StoredBranchedFromMessage — всегда последний
// унаследованный запись префикса (SessionManager.BranchAsync), поэтому всё до неё включительно
// — зона ответственности родителя, слайсер начинает со следующего за ней.
//
// Анти-петля реплик (решение 12: «реплика агента, в ход которой recall принёс страницу вики,
// цитироваться может только репликой человека») здесь НЕ реализована: признака манифеста хода в
// истории нет — RecallManifestMessage уходит клиенту и живёт в памяти SessionManager
// (GetLastRecallManifest), вида записи history.json для него нет. Правило придёт вместе с
// шагом, который принесёт манифест в разбор.
public static class WikiChatSlicer
{
    // Окно дельты — 35k токенов с перекрытием в 2 реплики (§12б этап 4).
    public const int WindowTokens = 35_000;
    public const int WindowOverlapLines = 2;

    // Выдержка карточки — не больше 8 КБ (решение 3). Байт UTF-8, не символов: «КБ» — про байт,
    // кириллица в два байта делает символьную меру вдвое больше заявленной.
    public const int MaxExcerptBytes = 8 * 1024;

    // Оценка токенов — та же прикидка проекта, что у ClaudeSession.CharsPerTokenEstimate и
    // ProjectMapScanner (байт/3): точного токенизатора в бэкенде нет, и окно — про порядок
    // нарезки, а не про точную счётную модель провайдера. internal: мера проверяется тестом
    // напрямую, а не угадыванием из теста.
    internal const int CharsPerTokenEstimate = 3;

    // Строки, которые вставляет сам сервер в записку-передачу (первое сообщение чата-
    // продолжения). Две формы — два пути передачи (ContinueAsync: ход-передача;
    // ChatHandoffService: черновик композера, который человек отправляет сам).
    private const string HandoffMarkerContinue = "Предыдущий чат:";
    private const string HandoffMarkerDraft = "Передаточная записка:";

    /// <summary>Отфильтрованная реплика. Index — номер m{Index} (1-based индекс записи
    /// истории), вызывающий строит по нему ref карточки и окна.</summary>
    public sealed record Line(int Index, bool IsHuman, string Text);

    /// <summary>Окно для прохода модели: строки идут подряд, пересечение с предыдущим
    /// окном — ровно WindowOverlapLines последних строк того окна (кроме первого).</summary>
    public sealed record Window(IReadOnlyList<Line> Lines);

    /// <summary>Точка ветвления: номер m{N} последней плашки «Ветка от …» (1-based);
    /// 0 — ветки нет. Всё до этой точки включительно — префикс родителя.</summary>
    public static int BranchPoint(IReadOnlyList<StoredMessage>? history)
    {
        if (history is null) return 0;
        for (var i = history.Count - 1; i >= 0; i--)
            if (history[i] is StoredBranchedFromMessage) return i + 1;
        return 0;
    }

    /// <summary>Реплики диапазона [from..to] (1-based, включительно) с пропущенными
    /// отсечениями и редакцией секретов. from ниже 1 поднимается до 1; диапазон целиком
    /// за концом истории — пустой список. Начало ветки — точка ветвления: from, попавший
    /// в префикс, поднимается за плашку (префикс — ответственность родителя).</summary>
    public static List<Line> Lines(IReadOnlyList<StoredMessage>? history, int from, int to,
        IReadOnlyCollection<string>? exactSecrets = null)
    {
        var result = new List<Line>();
        if (history is null || history.Count == 0 || from > to) return result;

        var start = Math.Max(1, from);
        var branchPoint = BranchPoint(history);
        if (branchPoint > 0 && start <= branchPoint) start = branchPoint + 1;
        var end = Math.Min(to, history.Count);

        // Записка-передача может быть только первым пользовательским сообщением чата
        // (ContinueAsync шлёт её сразу после создания чата). Ищем его по всей истории,
        // не только в диапазоне: при from за ним записка уже вне диапазона сама.
        var firstUserIndex = -1;
        for (var i = 0; i < history.Count; i++)
        {
            if (history[i] is StoredUserMessage) { firstUserIndex = i; break; }
        }

        for (var i = start - 1; i < end; i++)
        {
            switch (history[i])
            {
                case StoredUserMessage u:
                    if (i == firstUserIndex && IsHandoffNote(u.Text)) continue;
                    if (u.Auto == true || u.ViaAgent == true || u.SystemDirective == true
                        || u.StaffNote is not null) continue;
                    if (string.IsNullOrWhiteSpace(u.Text)) continue;
                    result.Add(new Line(i + 1, true, SecretRedactor.Redact(u.Text.Trim(), exactSecrets)));
                    break;
                // Текст сабагента (ParentToolUseId ≠ null) — вывод tool_use, а не ответ
                // агента в ленте: тот же отказ, что у BuildFeed.
                case StoredTextMessage { ParentToolUseId: null } t:
                    if (string.IsNullOrWhiteSpace(t.Text)) continue;
                    result.Add(new Line(i + 1, false, SecretRedactor.Redact(t.Text.Trim(), exactSecrets)));
                    break;
                // Всё остальное — протокол хода и служебные записи: не позиция участника.
            }
        }
        return result;
    }

    /// <summary>Дельта [from..to], нарезанная на окна ≤WindowTokens с перекрытием ровно
    /// WindowOverlapLines реплик. Одна реплика длиннее окна не режется: окно из неё одной
    /// превышает меру честно, чем цитата, оборванная на середине (выдержка и QuoteMatcher
    /// работают по целым репликам).
    ///
    /// Реплика процесса (WikiProcessPatterns.IsProcessReply) понижается в тексте окна, а не
    /// выбрасывается (ADR-039, решение 1; доработка S2-5): её место — одна фиксированная
    /// строка-маркер без исходного текста (см. DemoteProcess): факт события остаётся, а
    /// текст процесса в окно не попадает — ни одна строка реплики процесса не может стать
    /// дословной цитатой (QuoteMatcher меряет дословность по тексту окна, QuoteMatcher.cs:133).
    /// Порядок и нумерация реплик сохраняются; реплики человека не понижаются никогда.
    /// Мера окна — по исходным строкам, как без фильтра: понижение происходит после нарезки
    /// и не двигает границы окон.</summary>
    public static List<Window> Windows(IReadOnlyList<StoredMessage>? history, int from, int to,
        IReadOnlyCollection<string>? exactSecrets = null)
    {
        var windows = new List<Window>();
        var lines = Lines(history, from, to, exactSecrets);
        if (lines.Count == 0) return windows;

        var current = new List<Line>();
        var tokens = 0;
        foreach (var line in lines)
        {
            var lineTokens = EstimateTokens(line.Text);
            if (current.Count > 0 && tokens + lineTokens > WindowTokens)
            {
                windows.Add(new Window(DemoteProcess(current)));
                current = new List<Line>(current.Skip(Math.Max(0, current.Count - WindowOverlapLines)));
                tokens = current.Sum(l => EstimateTokens(l.Text));
            }
            current.Add(line);
            tokens += lineTokens;
        }
        if (current.Count > 0) windows.Add(new Window(DemoteProcess(current)));
        return windows;
    }

    /// <summary>Текст выдержки для карточки: целые реплики [from..to] с подписями
    /// «Пользователь:/Ассистент:» (тот же формат ленты, что у BuildFeed), обрезанный по
    /// MaxExcerptBytes на границе реплики — оборванная реплика ломала бы дословность
    /// цитаты QuoteMatcher. Пустая строка — в диапазоне нечего цитировать.</summary>
    public static string Excerpt(IReadOnlyList<StoredMessage>? history, int from, int to,
        IReadOnlyCollection<string>? exactSecrets = null)
    {
        var sb = new StringBuilder();
        var bytes = 0;
        foreach (var line in Lines(history, from, to, exactSecrets))
        {
            var block = (line.IsHuman ? "Пользователь:" : "Ассистент:") + "\n" + line.Text + "\n\n";
            var blockBytes = Encoding.UTF8.GetByteCount(block);
            if (bytes + blockBytes > MaxExcerptBytes) break;
            sb.Append(block);
            bytes += blockBytes;
        }
        return sb.ToString().Trim();
    }

    // Понижение процесса в тексте окна (ADR-039, решение 1; доработка S2-5): не-человеческая
    // реплика, целиком состоящая из строк процесса (WikiProcessPatterns), заменяется ОДНОЙ
    // фиксированной строкой без исходного текста и без перевода строки: маркер сохраняет
    // факт события, а текст процесса в окно (то, что видит модель) не попадает — значит,
    // строка процесса цитатой разбора стать не может (Major 1 ревью 6cbc5e0a) и пометка
    // покрывает всю реплику (Major 2). Выдержка и Lines не понижаются (Excerpt — отдельный путь).
    // index и IsHuman те же — порядок и нумерация окна не двигаются. Всё остальное — как было.
    // Текст публичный: WikiPlanValidator отклоняет цитаты, составленные из маркера (хвост 78cfb639,
    // минор 1 R-S2-5) — в продуктовом коде текст маркера один, из этой константы, не литерал.
    public const string ProcessMarker = "[процесс] служебная реплика";

    private static List<Line> DemoteProcess(List<Line> lines) =>
        [.. lines.Select(l => !l.IsHuman && WikiProcessPatterns.IsProcessReply(l.Text)
            ? l with { Text = ProcessMarker }
            : l)];

    private static int EstimateTokens(string text) => text.Length / CharsPerTokenEstimate;

    private static bool IsHandoffNote(string? text) =>
        text is not null
        && (text.Contains(HandoffMarkerContinue, StringComparison.Ordinal)
            || text.Contains(HandoffMarkerDraft, StringComparison.Ordinal));
}
