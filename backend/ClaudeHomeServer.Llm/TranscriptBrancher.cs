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
// Сопоставление сообщений истории с промптами транскрипта — fail-closed: нарушение любого
// из трёх условий (полнота / монотонность / однозначность) = отказ с причиной.
// Короткое сообщение («да», «ок») нашлось бы в чужом промпте, и граница уехала бы на ход
// вперёд — пользователь получил бы ветку с ровно тем ответом, от которого хотел уйти.
public static class TranscriptBrancher
{
    // Ниже этого порога значимых символов якорный текст не само-опознаваем:
    // кандидат принимается только при подтверждении соседом.
    private const int AnchorMinSignificantChars = 40;

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
    // или в этом файле не нашёлся — прежний текстовый путь со своими fail-closed условиями;
    // include — что входит в ветку (см. BranchInclude): граница префикса считается от
    // якорного промпта по-разному, но дальше оба пути общие.
    public static BranchResult Branch(string sourcePath, IEnumerable<string> anchorTexts,
        string newSessionId, string dstPath, string? anchorUuid = null,
        BranchInclude include = BranchInclude.Turn)
    {
        if (!File.Exists(sourcePath))
            return BranchResult.Fail($"файл-источник {sourcePath} не найден");
        if (!TranscriptMigrator.IsSafeSessionId(newSessionId))
            return BranchResult.Fail($"новый csid «{newSessionId}» не проходит проверку имени транскрипта");

        // 1. Читаем файл построчно (FileShare.ReadWrite: живой/умирающий CLI пишет в него).
        var lines = new List<string>();
        var promptLines = new List<int>();    // 0-based номер строки «человеческого» промпта в lines
        var promptTexts = new List<string>();  // его нормализованный текст
        // Строка записи с точным якорем (uuid хвоста якорного хода); -1 — якоря нет либо
        // он в этом файле не встретился. Ищем сырой подстрокой, не разбирая каждую запись:
        // у длинных сессий строк десятки тысяч. Ведущая кавычка в игле обязательна —
        // без неё игла нашлась бы внутри "parentUuid"/"leafUuid" соседних записей.
        var uuidNeedles = TranscriptMigrator.IsSafeSessionId(anchorUuid)
            ? new[] { $"\"uuid\":\"{anchorUuid}\"", $"\"uuid\": \"{anchorUuid}\"" }
            : null;
        var anchorUuidLine = -1;
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
                    anchorUuidLine = lines.Count - 1;
                if (TryExtractHumanPrompt(line, out var norm))
                {
                    promptLines.Add(lines.Count - 1);
                    promptTexts.Add(norm);
                }
            }
        }
        // Файл, не оканчивающийся переносом строки — с недописанной последней строкой: отбрасываем
        if (!endsWithNewline)
        {
            DropLastLine(lines, promptLines, promptTexts);
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

        // 3. Сопоставление: для каждого сообщения истории — первый промпт транскрипта,
        //    СОДЕРЖАЩИЙ его нормализованный текст (сравнение «содержит»: сервер клеит к
        //    промпту хвосты — recall, контекст). Три fail-closed проверки — ниже.
        var anchors = new List<string>();
        foreach (var t in anchorTexts)
        {
            var n = Normalize(t);
            if (n.Length > 0) anchors.Add(n);
        }
        if (anchors.Count == 0)
            return BranchResult.Fail("не передано ни одного якорного сообщения с текстом — шаг неопределён");

        var matched = new List<int>(); // индексы в promptTexts (строго растут)
        for (var i = 0; i < anchors.Count; i++)
        {
            var found = IndexOfContainingPrompt(promptTexts, anchors[i], 0);
            if (found < 0)
                return BranchResult.Fail(i == anchors.Count - 1
                    ? "якорный промпт не найден в транскрипте: этот шаг не удалось найти в памяти модели"
                    : $"сообщение истории №{i} из {anchors.Count} не совпало ни с одним промптом транскрипта — неполное сопоставление");

            // монотонность: позиции найденных промптов строго возрастают
            if (matched.Count > 0 && found <= matched[^1])
                return BranchResult.Fail("немонотонное сопоставление: позднее сообщение сошлось с более ранним промптом, чем предыдущее");

            // однозначность: короткий якорь (<40 значимых символов) принимается только
            // при совпадении соседа — промпт якоря обязан быть следующим за промптом соседа
            if (i == anchors.Count - 1 && SignificantLength(anchors[i]) < AnchorMinSignificantChars)
            {
                if (matched.Count == 0 || matched[^1] + 1 != found)
                    return BranchResult.Fail("текст якорного сообщения слишком короткий (<40 значимых символов) и не подтверждён предыдущим сообщением — совпадение неоднозначно");
            }
            matched.Add(found);
        }

        return CutAndWrite(lines, promptLines, promptLines[matched[^1]], promptLines[matched[^1]],
            newSessionId, dstPath, include);
    }

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
    public static bool TryExtractHumanPrompt(string line, out string normalizedText)
    {
        normalizedText = string.Empty;
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
    private static int IndexOfContainingPrompt(List<string> promptTexts, string needle, int from)
    {
        if (needle.Length == 0) return -1;
        for (var i = from; i < promptTexts.Count; i++)
            if (promptTexts[i].Contains(needle, StringComparison.Ordinal))
                return i;
        return -1;
    }

    // Недописанная последняя строка (файл без завершающего переноса) — отбрасывается вместе
    // с метками промпта, если она уже попала в списки.
    private static void DropLastLine(List<string> lines, List<int> promptLines, List<string> promptTexts)
    {
        if (lines.Count == 0) return;
        var last = lines.Count - 1;
        lines.RemoveAt(last);
        if (promptLines.Count > 0 && promptLines[^1] == last)
        {
            promptLines.RemoveAt(promptLines.Count - 1);
            promptTexts.RemoveAt(promptTexts.Count - 1);
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
