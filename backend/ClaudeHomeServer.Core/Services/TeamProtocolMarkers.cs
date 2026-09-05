using System.Text;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Инструментарий разбора маркеров протокола «Командной реализации». Зовёт и штаб
// (ParseEscalationMarker / ParseWorkMarker / HasTalkMarker в HandleTeamTurnEndAsync), и
// живая трансляция ленты любого чата (StripTeamProtocolMarkers / TrimAmbiguousMarkerTail /
// TrimUnresolvedMarkerOpen в OnMessageAsync), и TaskExecutionService (HasNoReplyMarker /
// NoReplyMarker). По этой причине — в спине, а не в вертикали штаба.

public static class TeamProtocolMarkers
{
    // Маркер эскалации в ответе координатора: `<escalate:deviation>суть</escalate>`.
    // Инструмента для этого не заводим — состав tools/list не должен зависеть от режима хода
    // (перезапуск CLI со всеми MCP), а маркер в тексте у нас уже работает в цикле «до готово».
    // Как и там, ищем вне код-блоков: модель часто цитирует протокол, прежде чем им пользоваться.
    // `decision` в протоколе координатора больше нет — вопрос в живом ходу задаётся ASK;
    // парсер терпит маркер как фолбэк (старые транскрипты, привычка модели) — карточка с полем
    // лучше молчаливого зависания.
    public static (TeamEscalationKind Kind, string Text)? ParseEscalationMarker(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Теги маркера ищем ВНЕ код-блоков (модель любит цитировать протокол примером), а
        // содержимое между ними берём из ОРИГИНАЛЬНОГО текста — со всем вложенным кодом.
        // Закрытие по имени (</escalate:check>) терпит close-регэксп: строгое сравнение роняло
        // маркер в молчаливый тупик (модель по XML-привычке закрывает тег по имени при генерации).
        var found = FindPairedMarkerOutsideCode(text, EscalateOpenTagRegex, EscalateCloseTagRegex);
        if (found is null) return null;
        var (openEnd, closeStart, _, openMatch) = found.Value;
        var kind = openMatch.Groups[1].Value switch
        {
            "deviation" => TeamEscalationKind.PlanDeviation,
            "check" => TeamEscalationKind.CheckFailed,
            // Тупик в волне (Э8): не остановка «жду решения», а возврат в интервью
            "clarify" => TeamEscalationKind.NeedsClarification,
            _ => TeamEscalationKind.ProductDecision,
        };
        return (kind, text[openEnd..closeStart].Trim());
    }

    // Маркер работы в ответе координатора (Э5): `<team:work>постановка</team>`. Им координатор
    // говорит, что вводная человека требует правки файлов — бэкенд разложит её планировщиком
    // и развернёт волну. Разговорный ответ маркера не несёт и не стоит ничего.
    // Разбор — как у эскалации: вне код-блоков, потому что протокол модель любит цитировать.
    public static string? ParseWorkMarker(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Теги маркера ищем ВНЕ код-блоков (модель любит цитировать протокол примером), а
        // содержимое между ними берём из ОРИГИНАЛЬНОГО текста — со всем вложенным кодом. Без
        // этого код-блок внутри <team:work> (дамп компонента в разведке) вырезался до извлечения,
        // и планировщик получал постановку без кода (P19). Закрытие по имени (</team:work>)
        // терпит close-регэксп — фикс инцидента 2026-07-31 сохранён.
        var found = FindPairedMarkerOutsideCode(text, WorkOpenTagRegex, WorkCloseTagRegex);
        if (found is null) return null;
        var (openEnd, closeStart, _, _) = found.Value;
        var request = text[openEnd..closeStart].Trim();
        return request.Length == 0 ? null : request;
    }

    // Маркер разговора (M6): `<team:talk/>` — координатор честно разобрал сообщение человека:
    // работы нет, файлы менять не нужно. Легальный выход из интервью без плана — по голому
    // тексту бэкенд не отличит такой ответ от молчаливого тупика (stall-гард). Разбор — как
    // у прочих маркеров: вне код-блоков, потому что протокол модель любит цитировать.
    public static bool HasTalkMarker(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        // Самодостаточный тег — хотя бы один match целиком вне код-блоков (как у парных маркеров:
        // процитированный в ```-примере маркер не считается активным вызовом).
        var ranges = GetCodeBlockRanges(text);
        foreach (Match m in TalkMarkerRegex.Matches(text))
            if (IsRangeOutsideCode(ranges, m.Index, m.Index + m.Length)) return true;
        return false;
    }

    // Маркер молчания (B4 «Доклада о завершении задачи»): `<no-reply/>` — ходу нечего сказать
    // человеку. Ответ ровно этим маркером не должен оставить в ленте ни реплики, ни следа
    // пустого хода: стрижка ниже вырезает маркер, а «после стрижки пусто» нигде не создаёт
    // запись (ни в живой трансляции, ни в истории — TurnAccumulator.FlushBuffers).
    // В отличие от маркеров штаба живёт в ЛЮБОМ чате: им отвечает обычная персона постановщика.
    public const string NoReplyMarker = "<no-reply/>";

    public static bool HasNoReplyMarker(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        // Самодостаточный тег — как <team:talk/>: процитированный в ```-примере не считается
        var ranges = GetCodeBlockRanges(text);
        foreach (Match m in NoReplyMarkerRegex.Matches(text))
            if (IsRangeOutsideCode(ranges, m.Index, m.Index + m.Length)) return true;
        return false;
    }

    // Волна 6 (живая приёмка волны 5): маркеры протокола — внутренняя договорённость между
    // координатором и бэкендом (их же разбирают Parse*/Has* выше), в реплике, которую видит
    // человек, им не место. Модель периодически закрывает тег по имени длинного маркера
    // (`</team:work>`, `</escalate:check>`) — парсер это уже терпит, а сырой текст хода
    // раньше уходил в ленту/историю как есть, и закрывающий тег протекал буквально.
    // Код-блоки не трогаем — симметрично тому, что их же исключают Parse*/Has* выше:
    // модель вправе процитировать протокол примером, это не активный вызов.
    private static readonly Regex CodeSpanOrFenceRegex =
        new("```[\\s\\S]*?```|`[^`\n]*`", RegexOptions.Compiled);
    private static readonly Regex TalkMarkerRegex =
        new(@"<team:talk\s*/>", RegexOptions.Compiled);
    private static readonly Regex NoReplyMarkerRegex =
        new(@"<no-reply\s*/>", RegexOptions.Compiled);

    // Открывающие/закрывающие теги маркеров — и для РАЗБОРА (Parse*/Has* выше), и для
    // зачистки ленты (RemovePairedMarkers ниже) один и тот же позиционный поиск пары:
    // зачистка и разбор находят границы маркера одним способом и не расходятся. Найти
    // закрывающий тег ВНЕ кода одним lazy-регэкспом нельзя — он свернётся на закрывающем
    // теге, процитированном внутри код-блока, и настоящий маркер с вложенным кодом (P19)
    // не соберётся. Поэтому ищем теги по отдельности и проверяем, что оба лежат вне
    // код-блоков, а содержимое между ними берём из оригинала.
    private static readonly Regex WorkOpenTagRegex =
        new("<team:work>", RegexOptions.Compiled);
    private static readonly Regex WorkCloseTagRegex =
        new(@"</team(?::work)?>", RegexOptions.Compiled);
    private static readonly Regex EscalateOpenTagRegex =
        new(@"<escalate:(deviation|check|decision|clarify)>", RegexOptions.Compiled);
    private static readonly Regex EscalateCloseTagRegex =
        new(@"</escalate(?::\w+)?>", RegexOptions.Compiled);

    // Осиротевший закрывающий тег без пары (прод 2026-08-02, находка Веры): в длинном
    // структурированном ответе модель иногда закрывает маркер повторно или цитирует закрытие
    // отдельно от открытия, которое уже вырезано парным поиском выше (например тем же именем
    // маркера двумя абзацами раньше). Такой закрывающий тег — всегда служебный синтаксис
    // нашего протокола (`</team>`/`</team:work>`, `</escalate>`/`</escalate:kind>`), человеку
    // он не нужен ни в какой форме — вырезаем и его.
    private static readonly Regex OrphanCloserRegex =
        new(@"</escalate(?::\w+)?>|</team(?::work)?>", RegexOptions.Compiled);

    public static string StripTeamProtocolMarkers(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('<')) return text;
        // Парные маркеры (эскалация/работа) вырезаем из ИСХОДНОГО текста позиционно — тем же
        // поиском пары тегов вне код-блоков, что и разбор. Рез по код-блокам здесь не годится:
        // маркер с fenced-блоком внутри (хвост P19) разрезался на сегменты, сегмент до фенса
        // оставался в ленте с буквальным <team:work> и всей постановкой, а закрывающий тег
        // съедался как осиротевший. Диапазон [openStart, closeEnd) уносит и вложенный код.
        text = RemovePairedMarkers(text, EscalateOpenTagRegex, EscalateCloseTagRegex);
        text = RemovePairedMarkers(text, WorkOpenTagRegex, WorkCloseTagRegex);
        // Остальное (самозакрывающиеся маркеры, осиротевшие закрывающие теги) — по-прежнему
        // посегментно: только вне код-блоков, процитированный в примере протокол не трогаем.
        var sb = new StringBuilder(text.Length);
        var pos = 0;
        foreach (Match code in CodeSpanOrFenceRegex.Matches(text))
        {
            sb.Append(StripUnpairedMarkers(text[pos..code.Index]));
            sb.Append(text, code.Index, code.Length);
            pos = code.Index + code.Length;
        }
        sb.Append(StripUnpairedMarkers(text[pos..]));
        return sb.ToString();
    }

    // Вырезает из текста каждый парный маркер openTag...closeTag, у которого ОБА тега лежат
    // вне код-блоков. После каждого удаления поиск начинается заново — позиции сдвинулись.
    private static string RemovePairedMarkers(string text,
        Regex openTagRegex, Regex closeTagRegex)
    {
        while (FindPairedMarkerOutsideCode(text, openTagRegex, closeTagRegex) is { } found)
        {
            var openStart = found.OpenMatch.Index;
            text = text.Remove(openStart, found.CloseEnd - openStart);
        }
        return text;
    }

    private static string StripUnpairedMarkers(string text)
    {
        if (text.Length == 0 || !text.Contains('<')) return text;
        text = TalkMarkerRegex.Replace(text, "");
        text = NoReplyMarkerRegex.Replace(text, "");
        text = OrphanCloserRegex.Replace(text, "");
        return text;
    }

    // Диапазоны fenced- (```...```) и инлайн- (`...`) код-блоков в порядке появления. В отличие
    // от прежнего вырезания кода перед разбором маркера, позиции позволяют найти теги ВНЕ кода
    // и вернуть содержимое маркера из оригинала — со всем вложенным кодом (фикс P19: раньше
    // код-блок внутри <team:work> вырезался до извлечения, и планировщик получал пустую постановку).
    private static List<(int Start, int End)> GetCodeBlockRanges(string text)
    {
        var ranges = new List<(int Start, int End)>();
        foreach (Match code in CodeSpanOrFenceRegex.Matches(text))
            ranges.Add((code.Index, code.Index + code.Length));
        return ranges;
    }

    // Целиком ли диапазон [start, end) лежит вне код-блоков (не пересекается ни с одним).
    private static bool IsRangeOutsideCode(List<(int Start, int End)> ranges, int start, int end)
    {
        foreach (var (s, e) in ranges)
            if (start < e && end > s) return false;   // пересечение с код-блоком
        return true;
    }

    // Первый парный маркер (openTag...closeTag), у которого ОБА тега целиком лежат вне код-блоков.
    // Возвращает границы в оригинальном тексте (включая конец закрывающего тега — зачистка ленты
    // вырезает диапазон [openStart, closeEnd) целиком) и match открывающего тега (для групп —
    // напр. тип эскалации). Содержимое между тегами (включая вложенный код) вызывающий берёт из
    // оригинала через text[openEnd..closeStart]. Так процитированный в ```-примере маркер не
    // сработает (тег внутри код-блока), а код внутри настоящей постановки не потеряется.
    private static (int OpenEnd, int CloseStart, int CloseEnd, Match OpenMatch)?
        FindPairedMarkerOutsideCode(
            string text,
            Regex openTagRegex,
            Regex closeTagRegex)
    {
        var ranges = GetCodeBlockRanges(text);
        for (var om = openTagRegex.Match(text); om.Success; om = om.NextMatch())
        {
            var openEnd = om.Index + om.Length;
            if (!IsRangeOutsideCode(ranges, om.Index, openEnd)) continue;   // открывающий в коде — цитата
            for (var cm = closeTagRegex.Match(text, openEnd); cm.Success; cm = cm.NextMatch())
            {
                if (IsRangeOutsideCode(ranges, cm.Index, cm.Index + cm.Length))
                    return (openEnd, cm.Index, cm.Index + cm.Length, om);   // закрывающий вне кода — настоящий маркер
            }
        }
        return null;
    }

    // Полные открывающие теги маркеров (без вариативных \s* — тем, которые их допускают,
    // соответствует отдельная проверка ниже). Хвост текста, совпадающий с СОБСТВЕННЫМ
    // префиксом одного из них, ещё может дорасти до настоящего маркера следующей дельтой —
    // до этого момента показывать его нельзя (иначе полтега мелькнёт в стриме раньше, чем
    // мы поймём, что это протокол).
    private static readonly string[] MarkerOpenTags =
    [
        "<escalate:deviation>", "<escalate:check>", "<escalate:decision>", "<escalate:clarify>",
        "<team:work>",
        // Самозакрывающийся маркер молчания целиком: любой его префикс («<n», «<no-repl»,
        // «<no-reply/») ещё может дорасти до маркера — до этого показывать хвост нельзя
        NoReplyMarker,
    ];

    public static bool IsAmbiguousMarkerTail(string tail)
    {
        if (tail.Length == 0 || tail[0] != '<') return false;
        foreach (var open in MarkerOpenTags)
            if (open.Length > tail.Length && open.StartsWith(tail, StringComparison.Ordinal))
                return true;
        // У `<team:talk/>` и `<no-reply/>` пробелы перед `/>` не фиксированы регэкспом разбора —
        // сюда попадает только незавершённый префикс (полный маркер уже вырезан
        // StripTeamProtocolMarkers)
        return Regex.IsMatch(tail, @"^<(?:team:talk|no-reply)\s*/?$");
    }

    // Обрезает с хвоста текста потенциально незавершённый маркер (см. IsAmbiguousMarkerTail).
    // Используется только при живой трансляции хода — на финальном тексте хода обрезка не
    // нужна: дальше дельт не будет, и придержанный хвост можно просто показать как есть.
    public static string TrimAmbiguousMarkerTail(string text)
    {
        var idx = text.LastIndexOf('<');
        if (idx < 0) return text;
        var tail = text[idx..];
        return IsAmbiguousMarkerTail(tail) ? text[..idx] : text;
    }

    // Полностью открытый маркер (открывающий тег уже целиком напечатан), у которого просто
    // ЕЩЁ НЕ пришло закрытие, — IsAmbiguousMarkerTail его пропускает (он больше не префикс
    // открывающего тега, он им равен), а StripTeamProtocolMarkers его не трогает (регэксп
    // требует закрывающую часть). Раз открывающий тег буквально присутствует в уже очищенном
    // от ЗАВЕРШЁННЫХ маркеров тексте — значит, этот конкретный маркер ещё не закрылся: прячем
    // с его начала и до конца буфера (тело маркера — постановка для планировщика, не для
    // человека, и в любом случае может дописываться следующими дельтами).
    private static readonly string[] MarkerOpenLiterals =
    [
        "<escalate:deviation>", "<escalate:check>", "<escalate:decision>", "<escalate:clarify>",
        "<team:work>", "<team:talk", "<no-reply",
    ];

    public static string TrimUnresolvedMarkerOpen(string strippedText)
    {
        var cut = strippedText.Length;
        foreach (var open in MarkerOpenLiterals)
        {
            var idx = strippedText.IndexOf(open, StringComparison.Ordinal);
            if (idx >= 0 && idx < cut) cut = idx;
        }
        return cut == strippedText.Length ? strippedText : strippedText[..cut];
    }
}
