using System.Text;
using System.Text.Json;

namespace ClaudeHomeServer.Services.Llm;

// Резак транскрипта для «ветвления чата» (шаг 1 фичи; документ-основание
// docs/research/chat-branching-2026-09.md §3/§4, поведение CLI — chat-branching-cli-experiment-2026-09.md).
//
// Читает журнал claude CLI по ГОТОВОМУ пути (поиск самой длинной копии и фолбэк на архив —
// в SessionManager, шаг 3; сюда путь приходит параметром — ссылка Llm → Main запрещена
// сторожём границ), находит границу K по якорям истории и записывает префикс строк [0, K)
// под новым csid в файл {новый csid}.jsonl.
//
// Правило отреза (доказано экспериментом шага 0): ветка = префикс строк файла. Ничего не
// переупорядочивается и не пересобирается. Обход по parentUuid («путь от листа вверх»)
// ЗАПРЕЩЁН: CLI пишет параллельные tool_use братьями по дереву, и путь от листа теряет
// tool_result соседней ветви — следующий ход уйдёт с tool_use без результата.
//
// Адресация шага — два пути. Первый и главный: точный якорь по uuid конца хода
// (TranscriptTailUuid), он покрывает все новые ходы. Второй, для исторических чатов, —
// ТЕКСТ ЯКОРЯ ПЛЮС ЕГО ВРЕМЯ (ResolveAnchorPrompt). Прежняя цепочка «каждое сообщение
// истории обязано найтись по порядку» снята задачей f18e784c: она стояла на ложном
// инварианте (история и транскрипт штатно расходятся по составу) и отказывала на проде
// в 46 местах из 66 в живом чате.
//
// Fail-closed сохранён там, где он по делу: якорь обязан найтись, кандидат обязан попасть
// в окно лага, а короткий якорь («да», «ок») сверяется по границам слова и без времени
// требует подтверждения предыдущим сообщением. Иначе он нашёлся бы в чужом промпте и
// граница уехала бы на ход вперёд — пользователь получил бы ветку с ровно тем ответом,
// от которого хотел уйти.
public static class TranscriptBrancher
{
    // Ниже этого порога значимых символов якорный текст не само-опознаваем:
    // кандидат принимается только при подтверждении соседом.
    private const int AnchorMinSignificantChars = 40;

    // Окно лага «история → транскрипт» для адресации якоря по времени (см. ResolveAnchorPrompt).
    // StoredUserMessage.Timestamp пишется НЕ при отправке, а на диспатче хода
    // (TurnAccumulator.OnUserMessage), поэтому ожидание в очереди в лаг не превращается и
    // лаг настоящей пары мал: замер по двум живым чатам прода (955e0ba2, 862f74b7, 81 пара) —
    // 2,1–8,9 с, медиана 2,5 с. Остаток лага — сборка промпта (recall, досье, граф) и холодный
    // старт CLI (spawn, рукопожатие MCP, у container-владельцев ещё docker exec), это десятки
    // секунд, отсюда запас в 5 минут.
    //
    // Окно назад — только на люфт часов (оба времени пишет одна машина). Работа окна ВПЕРЁД
    // ровно одна: не дать сообщению, чьего промпта в файле нет вовсе, молча привязаться к
    // более позднему промпту с тем же текстом (у таких ложных пар лаг на два порядка больше:
    // наблюдались +595, +1195, +216141 с, либо он отрицателен: −12858 с). Расширять окно
    // «на всякий случай» нельзя — это возвращает тихий срез не в том месте.
    private const long AnchorLagBackToleranceMs = 5_000;
    private const long AnchorLagForwardWindowMs = 5 * 60_000;

    // Что входит в ветку (зеркало SessionManager.ChatBranchInclude — резак живёт в Llm и
    // ссылаться на Main не может): Turn — якорный ход целиком, BeforePrompt — всё ДО
    // якорного промпта, сам промпт в префикс не входит (его текст уходит черновиком
    // в композер). Без этого параметра резак всегда резал по концу хода, и у beforePrompt
    // лента ветки расходилась с её транскриптом: на экране вопроса нет, в памяти модели —
    // и вопрос, и прежний ответ на него.
    public enum BranchInclude { Turn, BeforePrompt }

    // Результат резака. Ok — с позицией отреза (0-based, количество строк префикса);
    // Fail — с причиной для 409 (шаг 3 разворачивает). Молчаливой null без причины нет.
    // AnchorTurnExcluded — последний запрошенный ход из ветки исключён: его tool_use остался
    // без tool_result, граница отступила назад к началу этого хода. Успех без отступа — false.
    // Читает признак SessionManager.BranchAsync: он обрезает историю по ту же фактическую
    // границу и возвращает текст прерванного сообщения черновиком.
    public sealed record BranchResult(bool Ok, int? CutLine, string? Reason,
        bool AnchorTurnExcluded = false)
    {
        public static BranchResult Success(int cutLine, bool anchorTurnExcluded = false) =>
            new(true, cutLine, null, anchorTurnExcluded);
        public static BranchResult Fail(string reason) => new(false, null, reason);
    }

    // sourcePath — готовый путь к файлу-источнику; anchorTexts — тексты сообщений
    // пользователя истории, до якорного включительно (последний = сам якорь);
    // newSessionId — новый csid (валидируется, имя файла и поле sessionId в записях);
    // dstPath — полный путь целевого файла (родительская папка — забота вызывающего);
    // anchorUuid — ТОЧНЫЙ якорь (шаг 5): uuid записи транскрипта, снятый на конце якорного
    // хода (StoredResultMessage.TranscriptTailUuid). Есть и найден в файле — граница берётся
    // точным сравнением, текстовое сопоставление не запускается вовсе; нет (исторический чат)
    // или в этом файле не нашёлся — текстовый путь со своими fail-closed условиями;
    // include — что входит в ветку (см. BranchInclude): граница префикса считается от
    // якорного промпта по-разному, но дальше оба пути общие;
    // anchorTimestampMs — время якорного сообщения истории (StoredUserMessage.Timestamp,
    // Unix-мс): главный ключ текстового пути, см. ResolveAnchorPrompt. null — история до
    // этого поля (≈11% сообщений прода), тогда работает цепочка предыдущих сообщений.
    public static BranchResult Branch(string sourcePath, IEnumerable<string> anchorTexts,
        string newSessionId, string dstPath, string? anchorUuid = null,
        BranchInclude include = BranchInclude.Turn, long? anchorTimestampMs = null)
    {
        if (!File.Exists(sourcePath))
            return BranchResult.Fail($"файл-источник {sourcePath} не найден");
        if (!TranscriptMigrator.IsSafeSessionId(newSessionId))
            return BranchResult.Fail($"новый csid «{newSessionId}» не проходит проверку имени транскрипта");

        // 1. Читаем файл построчно (FileShare.ReadWrite: живой/умирающий CLI пишет в него).
        var lines = new List<string>();
        var promptLines = new List<int>();    // 0-based номер строки «человеческого» промпта в lines
        var promptTexts = new List<string>();  // его нормализованный текст
        var promptTimes = new List<long?>();   // время записи промпта (Unix-мс) — ключ адресации якоря
        // Строка записи с точным якорем (uuid хвоста якорного хода); -1 — якоря нет либо
        // он в этом файле не встретился. Ищем сырой подстрокой, не разбирая каждую запись:
        // у длинных сессий строк десятки тысяч. Ведущая кавычка в игле обязательна —
        // без неё игла нашлась бы внутри "parentUuid"/"leafUuid" соседних записей.
        var uuidNeedles = TranscriptMigrator.IsSafeSessionId(anchorUuid)
            ? new[] { $"\"uuid\":\"{anchorUuid}\"", $"\"uuid\": \"{anchorUuid}\"" }
            : null;
        var anchorUuidLine = -1;
        long? anchorUuidTime = null;   // время записи точного якоря — сверяется со временем сообщения
        bool endsWithNewline;
        using (var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (fs.Length == 0) endsWithNewline = true;
            else
            {
                fs.Position = fs.Length - 1;
                var lastByte = fs.ReadByte();
                fs.Position = 0;
                endsWithNewline = lastByte == '\n' || lastByte == '\r';
            }
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                line = line.TrimEnd('\r');
                if (line.Length == 0) continue;
                lines.Add(line);
                if (anchorUuidLine < 0 && uuidNeedles is not null
                    && (line.Contains(uuidNeedles[0], StringComparison.Ordinal)
                        || line.Contains(uuidNeedles[1], StringComparison.Ordinal)))
                {
                    anchorUuidLine = lines.Count - 1;
                    anchorUuidTime = TryReadTimestamp(line);
                }
                if (TryExtractHumanPrompt(line, out var norm, out var promptTime))
                {
                    promptLines.Add(lines.Count - 1);
                    promptTexts.Add(norm);
                    promptTimes.Add(promptTime);
                }
            }
        }
        // Файл, не оканчивающийся переносом строки — с недописанной последней строкой: отбрасываем
        if (!endsWithNewline)
        {
            DropLastLine(lines, promptLines, promptTexts, promptTimes);
            // Якорь пришёлся на отброшенную недописанную строку — считаем, что его нет
            if (anchorUuidLine >= lines.Count) anchorUuidLine = -1;
        }
        if (lines.Count == 0)
            return BranchResult.Fail("файл-источник пуст: ни одной полной записи");

        // 2. Точный якорь (шаг 5): uuid конца якорного хода найден в файле — граница известна
        //    точно, текстовое сопоставление не запускается вовсе. Начало хода (оно же левая
        //    граница хвостовой проверки п. 4) — последний человеческий промпт не позже
        //    якорной записи; K считается ниже от самой якорной записи.
        //    Якорь снимается на КОНЦЕ хода, поэтому отставание записи файла на пару строк
        //    (CLI дописывает хвост асинхронно) границу не двигает: K всё равно берётся по
        //    следующему человеческому промпту, а он заведомо позже.
        //
        //    Но найденному uuid верим не безоглядно: он обязан указывать на запись НЕ РАНЬШЕ
        //    якорного сообщения. У чата, много раз мигрировавшего между провайдерами, копии
        //    транскрипта расходятся, а хвост хода снимается по ОДНОЙ из них — резак же берёт
        //    самую длинную. Живой пример (задача f18e784c, чат 955e0ba2): хвост ходов от
        //    21.09 записан как 0fa74da9…, и это последняя запись трёх коротких копий (316
        //    строк, 18.09), а в длинной копии (1257 строк) тот же uuid лежит на строке 314 —
        //    точный якорь молча резал ветку в середине позапрошлого дня. Неверная граница
        //    хуже отказа, поэтому противоречие по времени снимает доверие к uuid и работу
        //    продолжает текстовый путь, у которого свои fail-closed условия.
        if (anchorUuidLine >= 0 && anchorTimestampMs is long anchorMsgTime
            && anchorUuidTime is long uuidTime && uuidTime < anchorMsgTime - AnchorLagBackToleranceMs)
        {
            Console.Error.WriteLine(
                $"[TranscriptBrancher] Точный якорь {anchorUuid} в {sourcePath} указывает на запись "
                + $"старше самого сообщения (на {(anchorMsgTime - uuidTime) / 1000} с) — копии транскрипта "
                + "разошлись, переходим на текстовое сопоставление");
            anchorUuidLine = -1;
        }
        if (anchorUuidLine >= 0)
        {
            var turnStart = 0;
            foreach (var p in promptLines)
            {
                if (p > anchorUuidLine) break;
                turnStart = p;
            }
            return CutAndWrite(lines, promptLines, turnStart, anchorUuidLine, newSessionId, dstPath, include);
        }
        if (uuidNeedles is not null)
            Console.Error.WriteLine(
                $"[TranscriptBrancher] Точный якорь {anchorUuid} не найден в {sourcePath} — переходим на текстовое сопоставление");

        // 3. Текстовый путь: ищем промпт САМОГО якоря (см. ResolveAnchorPrompt).
        var anchors = new List<string>();
        foreach (var t in anchorTexts)
        {
            var n = Normalize(t);
            if (n.Length > 0) anchors.Add(n);
        }
        if (anchors.Count == 0)
            return BranchResult.Fail("не передано ни одного якорного сообщения с текстом — шаг неопределён");

        var (anchorPrompt, failReason) = ResolveAnchorPrompt(promptTexts, promptTimes, anchors, anchorTimestampMs);
        if (anchorPrompt < 0)
            return BranchResult.Fail(failReason!);

        return CutAndWrite(lines, promptLines, promptLines[anchorPrompt], promptLines[anchorPrompt],
            newSessionId, dstPath, include);
    }

    // Адресация якорного промпта в транскрипте — сердце текстового пути.
    //
    // Ключ — ТЕКСТ ЯКОРЯ ПЛЮС ЕГО ВРЕМЯ, а не цепочка предыдущих сообщений. Прежняя цепочка
    // (каждое неслужебное сообщение истории обязано найтись, монотонно, а короткий якорь —
    // вплотную за соседом) стояла на ложном инварианте: история и транскрипт штатно
    // расходятся по составу. Разбор двух живых чатов прода (задача f18e784c): в 955e0ba2
    // четыре подряд сообщения истории («Делай», «проверяй», «Продолжай», «продолжай») в
    // транскрипт не попали вовсе — одно такое звено делало невозможным ветвление от ЛЮБОГО
    // более позднего места чата (20 мест из 66), а требование «вплотную за соседом» рубило
    // каждое короткое сообщение, потому что между ними в насыщенном чате всегда стоят
    // служебные промпты (доклады исполнителей, тики /loop, task-notification).
    //
    // Кандидаты — промпты, СОДЕРЖАЩИЕ нормализованный текст якоря: сервер клеит к тексту хода
    // recall ПРЕФИКСОМ (ClaudeSession: joinedRecall + разделитель + text), поэтому ни равенство,
    // ни EndsWith тут не годятся. Кандидатов бывает много; разводит их лаг записи: берём
    // кандидата с минимальным лагом ВНУТРИ окна (сначала окно, потом минимум — иначе перекос
    // часов отдал бы победу лагу −3 с).
    //
    // Нет времени (история до поля Timestamp либо транскрипт без timestamp) — работает
    // запасной путь по цепочке: она ЗАДАЁТ НИЖНЮЮ ГРАНИЦУ поиска (поиск идёт от позиции
    // последнего совпавшего сообщения, а не с начала файла — прежний поиск с нуля и схлопывал
    // повторяющийся текст на первое вхождение), а несовпавшее звено пропускается молча. Путь
    // не мёртвый: 95 чатов прода из 400 не имеют Timestamp ни у одного сообщения.
    // Fail-closed остаётся там, где он по делу: якорь обязан найтись, а короткий якорь —
    // быть подтверждён совпавшим предыдущим сообщением.
    //
    // Известная деградация: повтор хода в тот же файл (egress-повтор пары через 5 с,
    // повторная попытка) пишет промпт дважды с разницей в секунды — оба в окне, минимум лага возьмёт
    // ПЕРВУЮ, провалившуюся попытку. Частично ловится хвостовой проверкой парности; менять
    // правило под этот случай нельзя — для дублей текста внутри окна минимум верен.
    //
    // Это МОСТ для исторических чатов: настоящее лечение адресации — точный якорь
    // StoredResultMessage.TranscriptTailUuid (путь выше), который покрывает все новые ходы.
    // Обвешивать текстовое правило эвристиками вместо ожидания, пока старые чаты вымоются, не надо.
    //
    // Возвращает индекс в promptTexts либо (-1, причина отказа).
    private static (int Index, string? Reason) ResolveAnchorPrompt(List<string> promptTexts,
        List<long?> promptTimes, List<string> anchors, long? anchorTimestampMs)
    {
        const string NotFound = "якорный промпт не найден в транскрипте: этот шаг не удалось найти в памяти модели";
        var anchorText = anchors[^1];
        // Короткий якорь сверяется по ГРАНИЦАМ СЛОВА: сравнение «содержит» без них принимает
        // «делай» внутри «сделай», а «да» — внутри «задача». Пока настоящий промпт на месте,
        // ошибку ловит минимум лага, но у сообщения, чьего промпта в файле нет, единственным
        // кандидатом в окне может оказаться чужой промпт с такой подстрокой — и это молча
        // неверный срез, ровно то, против чего писалось правило однозначности.
        var wordBounded = SignificantLength(anchorText) < AnchorMinSignificantChars;

        if (anchorTimestampMs is long anchorTime)
        {
            var best = -1;
            var bestLag = long.MaxValue;
            var timedCandidates = 0;
            for (var j = 0; j < promptTexts.Count; j++)
            {
                if (!ContainsAnchor(promptTexts[j], anchorText, wordBounded)) continue;
                if (promptTimes[j] is not long promptTime) continue;
                timedCandidates++;
                var lag = promptTime - anchorTime;
                if (lag < -AnchorLagBackToleranceMs || lag > AnchorLagForwardWindowMs) continue;
                if (lag < bestLag) { bestLag = lag; best = j; }
            }
            if (best >= 0)
            {
                // Фактический лаг в журнал: через месяц окно пересчитывается по замеру, а не переугадывается
                Console.Error.WriteLine(
                    $"[TranscriptBrancher] Якорь найден по времени: промпт №{best}, лаг {bestLag} мс, кандидатов со временем {timedCandidates}");
                return (best, null);
            }
            // Кандидаты со временем были, но все вне окна — это не «текст не найден», а
            // совпадение с ЧУЖИМ промптом: резать по нему нельзя (ветка ушла бы не туда).
            if (timedCandidates > 0)
                return (-1, "этот шаг не удалось надёжно найти в памяти модели: подходящий по тексту промпт записан не в то время");
            // Кандидатов со временем нет вовсе — уходим на цепочку ниже
        }

        var pos = 0;
        var previousMatched = false;
        for (var i = 0; i < anchors.Count - 1; i++)
        {
            var idx = IndexOfContainingPrompt(promptTexts, anchors[i], pos, wordBounded: false);
            if (idx < 0) { previousMatched = false; continue; }
            pos = idx + 1;
            previousMatched = true;
        }

        var found = IndexOfContainingPrompt(promptTexts, anchorText, pos, wordBounded);
        if (found < 0) return (-1, NotFound);
        if (wordBounded && !previousMatched)
            return (-1, "текст якорного сообщения слишком короткий (<40 значимых символов) и не подтверждён предыдущим сообщением — совпадение неоднозначно");
        return (found, null);
    }

    // Промпт содержит текст якоря; wordBounded — требовать границы слова с обеих сторон
    // вхождения (соседний символ не буква, не цифра и не подчёркивание).
    private static bool ContainsAnchor(string prompt, string needle, bool wordBounded)
    {
        if (!wordBounded) return prompt.Contains(needle, StringComparison.Ordinal);
        var i = prompt.IndexOf(needle, StringComparison.Ordinal);
        while (i >= 0)
        {
            var okBefore = i == 0 || !IsWordChar(prompt[i - 1]);
            var end = i + needle.Length;
            var okAfter = end >= prompt.Length || !IsWordChar(prompt[end]);
            if (okBefore && okAfter) return true;
            i = prompt.IndexOf(needle, i + 1, StringComparison.Ordinal);
        }
        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // Общий хвост обоих путей поиска границы (точного по uuid и текстового): K, хвостовая
    // проверка, запись префикса. turnStartLine — строка промпта, которым начался якорный ход
    // (левая граница проверки пар tool_use/tool_result); anchorLine — запись, ОТ которой
    // ищется следующий человеческий промпт (у текстового пути это тот же промпт якоря, у
    // точного — запись конца хода).
    private static BranchResult CutAndWrite(List<string> lines, List<int> promptLines,
        int turnStartLine, int anchorLine, string newSessionId, string dstPath,
        BranchInclude include)
    {
        int k;          // граница префикса (количество строк)
        int lastTurnStart;  // начало последнего хода префикса — левая граница хвостовой проверки
        if (include == BranchInclude.BeforePrompt)
        {
            // Ветка начинается ДО якорного промпта: он сам в префикс не входит, его текст
            // уходит черновиком в композер. Последний ход префикса — ПРЕДЫДУЩИЙ, его и
            // проверяем на парность хвоста.
            k = turnStartLine;
            lastTurnStart = -1;
            foreach (var p in promptLines)
            {
                if (p >= turnStartLine) break;
                lastTurnStart = p;
            }
            // До якоря нет ни одного человеческого промпта: в ветке не осталось бы ни одного
            // хода, а транскрипт без ходов подсовывать CLI нельзя (поведение --resume на нём
            // неизвестно) — честный отказ вместо молчаливо пустой памяти.
            if (lastTurnStart < 0)
                return BranchResult.Fail("до этого сообщения в разговоре нет ни одного завершённого хода: ветвить нечего");
        }
        else
        {
            // K = строка СЛЕДУЮЩЕГО человеческого промпта после якорного хода;
            // если ветвимся от последнего хода — конец файла.
            k = lines.Count;
            foreach (var p in promptLines)
                if (p > anchorLine) { k = p; break; }
            lastTurnStart = turnStartLine;
        }

        // Хвостовая проверка: последний ход префикса (его промпт → K) обязан завершать
        // пары tool_use/tool_result. Прерванный ход (непарные) → отступить назад к началу
        // этого хода; синтетическим result НЕ чиним.
        var anchorExcluded = false;
        if (!LastTurnIsBalanced(lines, lastTurnStart, k))
        {
            k = lastTurnStart;
            anchorExcluded = true;
            if (k == 0)
                return BranchResult.Fail("последний ход ветки обрывается на непарном tool_use, а более ранней границы хода нет: в ветке не осталось ни одного завершённого хода");
        }

        // Запись префикса [0, K) под новым id; sessionId в записях переписывается
        // (отчёт шага 0: CLI принимает оба варианта, но имя файла и содержимое — в схождение).
        WritePrefix(lines, k, newSessionId, dstPath);
        return BranchResult.Success(k, anchorExcluded);
    }
    // «Человеческий промпт» (тот же набор признаков, что у TranscriptProbe.LastUserText,
    // но для ветвления массив текстовых блоков тоже считается промптом — tool_result не считается):
    // type == "user" AND isSidechain != true AND isMeta != true AND content — строка
    // либо массив, в котором НЕТ блока tool_result. Битая (недописанная) строка — false.
    public static bool TryExtractHumanPrompt(string line, out string normalizedText) =>
        TryExtractHumanPrompt(line, out normalizedText, out _);

    // Та же проверка плюс время записи (поле timestamp, ISO-8601 от CLI) в Unix-мс —
    // ключ адресации якоря (ResolveAnchorPrompt). null — поля нет либо оно не разбирается.
    public static bool TryExtractHumanPrompt(string line, out string normalizedText,
        out long? timestampMs)
    {
        normalizedText = string.Empty;
        timestampMs = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "user") return false;
            if (root.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True) return false;
            if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return false;
            if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return false;
            if (!msg.TryGetProperty("content", out var content)) return false;

            timestampMs = TryReadTimestamp(root);

            if (content.ValueKind == JsonValueKind.String)
            {
                normalizedText = Normalize(content.GetString());
                return true;
            }
            if (content.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object) continue;
                    // массив с хотя бы одним tool_result — запись результата инструмента, не промпт
                    if (block.TryGetProperty("type", out var bt)
                        && bt.ValueKind == JsonValueKind.String && bt.GetString() == "tool_result")
                        return false;
                    if (block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                        sb.Append(txt.GetString() ?? string.Empty);
                }
                normalizedText = Normalize(sb.ToString());
                return true;
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Время записи транскрипта (поле timestamp, ISO-8601 от CLI) в Unix-мс; null — поля нет
    // либо оно не разбирается. Приведение к UTC обязательно: история хранит Unix-мс UTC, а
    // строка приходит со смещением, и разбор в местном времени сдвинул бы все лаги на часы.
    private static long? TryReadTimestamp(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? TryReadTimestamp(doc.RootElement)
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static long? TryReadTimestamp(JsonElement root) =>
        root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(ts.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal
            | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : null;

    // Нормализация текста для сравнения «содержит»: нижний регистр + схлопывание пробельных.
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        var prevSpace = true;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!prevSpace) { sb.Append(' '); prevSpace = true; }
            }
            else
            {
                sb.Append(char.ToLowerInvariant(c));
                prevSpace = false;
            }
        }
        return sb.ToString().TrimEnd();
    }

    // «Значимые» символы нормализованного текста (без пробелов) — мера самостоятельности якоря.
    public static int SignificantLength(string normalized)
    {
        var n = 0;
        foreach (var c in normalized) if (!char.IsWhiteSpace(c)) n++;
        return n;
    }

    // Первый промпт (не раньше от from), содержащий нормализованный текст; -1 — нет.
    private static int IndexOfContainingPrompt(List<string> promptTexts, string needle, int from,
        bool wordBounded)
    {
        if (needle.Length == 0) return -1;
        for (var i = from; i < promptTexts.Count; i++)
            if (ContainsAnchor(promptTexts[i], needle, wordBounded))
                return i;
        return -1;
    }

    // Недописанная последняя строка (файл без завершающего переноса) — отбрасывается вместе
    // с метками промпта, если она уже попала в списки.
    private static void DropLastLine(List<string> lines, List<int> promptLines,
        List<string> promptTexts, List<long?> promptTimes)
    {
        if (lines.Count == 0) return;
        var last = lines.Count - 1;
        lines.RemoveAt(last);
        if (promptLines.Count > 0 && promptLines[^1] == last)
        {
            promptLines.RemoveAt(promptLines.Count - 1);
            promptTexts.RemoveAt(promptTexts.Count - 1);
            promptTimes.RemoveAt(promptTimes.Count - 1);
        }
    }

    // Хвостовая проверка (fail-closed, без синтетических tool_result): все tool_use последнего
    // хода префикса [fromLine, toLine) парны с tool_result и наоборот.
    private static bool LastTurnIsBalanced(List<string> lines, int fromLine, int toLine)
    {
        var toolUse = new HashSet<string>();
        var toolResult = new HashSet<string>();
        for (var i = fromLine; i < toLine; i++)
        {
            try
            {
                using var doc = JsonDocument.Parse(lines[i]);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                if (!doc.RootElement.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object) continue;
                    if (!block.TryGetProperty("type", out var bt) || bt.ValueKind != JsonValueKind.String) continue;
                    if (bt.GetString() == "tool_use")
                    {
                        if (block.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                            toolUse.Add(id.GetString()!);
                    }
                    else if (bt.GetString() == "tool_result")
                    {
                        if (block.TryGetProperty("tool_use_id", out var tid) && tid.ValueKind == JsonValueKind.String)
                            toolResult.Add(tid.GetString()!);
                    }
                }
            }
            catch (JsonException) { /* битая строка внутри хода — уже учтена при отборе строк */ }
        }
        foreach (var id in toolUse) if (!toolResult.Contains(id)) return false;
        foreach (var id in toolResult) if (!toolUse.Contains(id)) return false;
        return true;
    }

    // Запись префикса [0, k) → dstPath. sessionId в записях переписывается на newSessionId:
    // отчёт шага 0 — CLI принимает оба варианта (не форкает по внутр. id), но переписываем,
    // чтобы имя файла и содержимое сходились (иначе файл смешанный: префикс под старым id).
    //
    // Меняется ТОЛЬКО верхнеуровневый свойство: регулярка по сырой строке (прежняя
    // реализация) матчила бы и экранированное \"sessionId\":\"…\" внутри вложенных данных
    // (tool_result с текстом чужого транскрипта) и тихо портило историю ветки.
    // Utf8JsonReader идёт по строке, находит имя свойства sessionId на глубине 1
    // (Depth == 0 — корневой объект, PropertyName — имя свойства) и берёт байтовые
    // границы (TokenStartIndex..ValueSpan) его строкового значения; замена — только
    // этих границ. Остальные байты (порядок ключей, экранирование, кириллица) не меняются:
    // пересериализация через JsonNode переэкранировала бы не-ASCII, а csid
    // (IsSafeSessionId) — чистый ASCII.
    private static void WritePrefix(List<string> lines, int k, string newSessionId, string dstPath)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < k && i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Contains("\"sessionId\"", StringComparison.Ordinal))
            {
                var replaced = ReplaceTopLevelSessionId(line, newSessionId);
                if (replaced is not null) line = replaced;
            }
            sb.Append(line).Append('\n');
        }
        File.WriteAllText(dstPath, sb.ToString(), new UTF8Encoding(false));
    }

    // Точечная замена значения верхнеуровневого sessionId; null — запись не JSON-объект
    // (битая/мусорная строка — её WritePrefix пишет как есть, прежняя регулярка тоже была
    // бессильна) или верхнеуровневого sessionId в ней нет (тогда в строке нет ни одного
    // кандидата и менять нечего).
    private static string? ReplaceTopLevelSessionId(string line, string newSessionId)
    {
        var utf8 = Encoding.UTF8.GetBytes(line);
        try
        {
            var reader = new Utf8JsonReader(utf8);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                // CurrentDepth 1 — имя/значение свойства ВЕРХНЕУРОВНЕВОГО (root) объекта:
                // Utf8JsonReader считает глубину токена, а не объекта, поэтому свойства
                // root-объекта имеют CurrentDepth == 1. sessionId на любой другой глубине
                // (вложенные объекты, tool_result-тексты) не трогаем.
                if (reader.CurrentDepth != 1) continue;
                if (!string.Equals(reader.GetString(), "sessionId", StringComparison.Ordinal)) continue;

                // Значение свойства: ждём строку
                if (!reader.Read()) return null;
                if (reader.TokenType != JsonTokenType.String) return null;

                // TokenStartIndex — смещение ОТКРЫВАЮЩЕЙ кавычки значения, ValueSpan — её
                // содержимое (без кавычек): заменяем байты содержимого, кавычки и всё
                // остальное (порядок ключей, экранирование, кириллица) остаются на месте.
                // Замена — на БАЙТАХ (не на char[]): кириллица в UTF-8 — 2 байта/символ,
                // char-модель бы её рассинхронизировала.
                // (TokenStartIndex/BytesConsumed — long, BlockCopy принимает int — строка
                //  строки .jsonl в пределах int)
                var start = (int)reader.TokenStartIndex + 1;
                var length = reader.ValueSpan.Length;
                var body = Encoding.UTF8.GetBytes(newSessionId);
                var result = new byte[utf8.Length - length + body.Length];
                Buffer.BlockCopy(utf8, 0, result, 0, start);
                Buffer.BlockCopy(body, 0, result, start, body.Length);
                Buffer.BlockCopy(utf8, start + length, result, start + body.Length, utf8.Length - start - length);
                return Encoding.UTF8.GetString(result);
            }
            // дошли до EndObject, не найдя sessionId — строка остаётся как есть
            return null;
        }
        // Parse-ошибка (битая/мусорная строка): JsonReaderException — internal-внучка
        // JsonException, ловим публичную JsonException — семантика та же (строка не объект)
        catch (JsonException) { }
        return null;
    }
}
