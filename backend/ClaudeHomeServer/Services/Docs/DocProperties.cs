using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Docs;

// Свойства «шапки» markdown-документа: строки «**Ключ:** значение» в начале файла.
// Формат не изобретён, а описан: ровно так уже написаны ADR проекта (docs/adr/*.md), поэтому
// продукт умеет их читать и править без миграции, а файл остаётся читаемым в git и в wiki.
//
// Разбор СЫРОЙ — без знания схемы типов: корпус не должен зависеть от .docs, иначе правку
// схемы не заметил бы кеш индекса (его отпечаток считается только по документам).
//
// Отдельный класс, а не ветка в DocsIndexService.ParseDocument: тот зовётся на каждый документ
// корпуса и покрыт полутора сотней тестов, а шапка — это первые десять строк файла.
internal static partial class DocProperties
{
    // Предохранители: шапка — десяток строк, а не полдокумента
    private const int MaxProperties = 32;
    private const int MaxFrontMatterLines = 50;

    // Строка свойства. Двоеточие обязано стоять ВНУТРИ «**…**» — на этом держится вся
    // безопасность разбора: «**ОБЯЗАТЕЛЬНО**: …» (docs/omo/translations/*.md) и
    // «**OpenTelemetry-based** observability … режимами:» (docs/observability/overview.md)
    // стоят ровно на позиции шапки, и терпимость к двоеточию снаружи сделала бы их свойствами.
    // Ключ без «*» — иначе он растянулся бы через закрывающие звёздочки на середину абзаца.
    //
    // marker — оформление шапки: голая строка (так написаны ADR этого репозитория), пункт
    // списка («- **Статус:** Предложено») или цитата («> **Статус:** Проектирование») — так
    // пишут в других проектах, и без этого фича там не работала бы вовсе. Ложных срабатываний
    // маркер не добавляет: шапку по-прежнему ищем ТОЛЬКО сразу за H1 непрерывным блоком.
    // Захвачен он для записи: правя значение, надо сохранить оформление строки как было.
    [GeneratedRegex(@"^(?<indent> {0,3})(?<marker>(?:[-*+]|\d{1,9}[.)])[ \t]+|>[ \t]?)?\*\*(?<key>[^*\r\n]{1,64}?):\*\*[ \t]*")]
    private static partial Regex PropertyRegex();

    // Строка, обрывающая шапку, даже если стоит сразу за свойством: список, цитата, таблица.
    // Заголовок и ограду кода ловят HeadingRegex/FenceRegex — они уже описаны в соседнем классе
    [GeneratedRegex(@"^ {0,3}(?:[-*+][ \t]|\d{1,9}[.)][ \t]|>|\|)")]
    private static partial Regex BlockBreakRegex();

    // Открытие/закрытие YAML front-matter в самом начале файла
    [GeneratedRegex(@"^(?:---|\.\.\.)[ \t]*$")]
    private static partial Regex FrontMatterRegex();

    // Свойство вместе с координатами в исходном тексте. Запись правит РОВНО отрезок
    // [ValueStart, End) — префикс строки («**Статус:** ») остаётся байт-в-байт, поэтому
    // переживают и регистр ключа, и отступ, и число пробелов, а дифф в git содержит одну строку.
    // End у многострочного значения указывает на конец ПОСЛЕДНЕЙ строки продолжения.
    internal readonly record struct PropertySpan(
        string Key, string Value, int Start, int End, int ValueStart);

    // Items — свойства в порядке файла (с дублями: правится и читается первый).
    // InsertOffset — куда вставлять шапку, когда её нет вовсе: сразу после H1 (или после
    // front-matter, или в начало файла).
    // BlockStart/BlockEnd — отрезок строк шапки (BlockEnd — сразу за переводом строки
    // последней из них): сюда дописывается свойство, которого в файле ещё не было.
    // PreviewEnd — то же плюс одна пустая строка за шапкой: по нему панель вырезает шапку
    // из превью, и без захвата пустой строки на её месте остались бы две подряд.
    // BlockStart < 0 — шапки нет.
    // Marker — оформление строк шапки («», «- », «> »…): новое свойство пишется так же,
    // как её соседи
    internal sealed record ParsedProperties(
        IReadOnlyList<PropertySpan> Items, int InsertOffset,
        int BlockStart, int BlockEnd, int PreviewEnd, string Marker = "")
    {
        public bool HasBlock => BlockStart >= 0;
    }

    private static readonly ParsedProperties Empty = new([], 0, -1, -1, -1);

    // Полный разбор с координатами — нужен записи. Контур шапки:
    //   front-matter → H1 → НЕПРЕРЫВНЫЙ блок строк свойств.
    //
    // Осознанный размен: если автор написал свойства НИЖЕ вводного абзаца, шапка не находится,
    // и запись создаёт свой блок сразу за заголовком — в файле окажутся две строки с одним
    // ключом, а читается верхняя. Цена альтернативы выше: разрешив свойствам стоять где угодно,
    // мы объявили бы свойствами половину прозы репозитория (см. промахи ниже).
    // Правило «непрерывный блок сразу после H1», а не «всё до первого H2»: строки вида
    // «**Ключ:** значение» встречаются и в теле доков (docs/observability/overview.md:12,
    // docs/architecture/features.md), и по второму правилу у таких документов «нашлась бы»
    // шапка в середине текста. А документ вовсе без H2 стал бы шапкой целиком.
    internal static ParsedProperties Parse(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return Empty;

        var pos = SkipFrontMatter(markdown);
        pos = SkipBlank(markdown, pos);

        // H1 необязателен: документ может начинаться прямо со строк свойств
        if (pos < markdown.Length)
        {
            var (line, _, next) = ReadLine(markdown, pos);
            var h = DocsIndexService.HeadingRegex().Match(line);
            if (h.Success && h.Groups[1].Value.Length == 1) pos = SkipBlank(markdown, next);
        }

        var insertOffset = pos;
        var items = new List<PropertySpan>();
        var blockStart = -1;
        var blockEnd = -1;
        // Оформление шапки берётся у ПЕРВОЙ её строки: новое свойство должно встать
        // в том же виде, что и соседние, а не голой строкой посреди списка
        var marker = "";

        while (pos < markdown.Length)
        {
            var (line, contentEnd, next) = ReadLine(markdown, pos);

            // Пустая строка, заголовок или ограда кода — конец шапки
            if (line.Trim().Length == 0) break;
            if (DocsIndexService.FenceRegex().IsMatch(line)) break;
            if (DocsIndexService.HeadingRegex().IsMatch(line)) break;

            var m = PropertyRegex().Match(line);

            // Список, цитата или таблица обрывают шапку — но только если это НЕ строка
            // свойства: у пункта списка и цитаты те же маркеры, а «- **Статус:** Принято»
            // это законное оформление шапки
            if (!m.Success && BlockBreakRegex().IsMatch(line)) break;

            if (m.Success)
            {
                if (items.Count >= MaxProperties) break;
                if (blockStart < 0) { blockStart = pos; marker = m.Groups["indent"].Value + m.Groups["marker"].Value; }
                items.Add(new PropertySpan(
                    Key: m.Groups["key"].Value.Trim(),
                    Value: line[m.Length..].TrimEnd(),
                    Start: pos,
                    End: contentEnd,
                    ValueStart: pos + m.Length));
            }
            else
            {
                // Первая же строка не свойство — значит шапки у документа нет вовсе
                if (items.Count == 0) break;

                // Строка-продолжение (перенос длинного значения, вторая строка цитаты).
                // Она остаётся частью блока — но НЕ частью значения: приклеив её, запись
                // при правке схлопнула бы в одну строку целый абзац, а такие шапки в
                // проектах есть. Читаем и правим ровно свою строку, соседние не трогаем.
            }

            blockEnd = next;
            pos = next;
        }

        if (blockStart < 0) return new ParsedProperties([], insertOffset, -1, -1, -1);

        // Прихватываем одну пустую строку за шапкой: без неё вырезание оставляет в превью
        // две пустые строки подряд там, где была шапка
        var previewEnd = blockEnd;
        if (previewEnd < markdown.Length)
        {
            var (tail, _, afterTail) = ReadLine(markdown, previewEnd);
            if (tail.Trim().Length == 0) previewEnd = afterTail;
        }

        return new ParsedProperties(items, insertOffset, blockStart, blockEnd, previewEnd, marker);
    }

    // Пары ключ/значение для корпуса. Дубликаты ключа схлопываются в первый — тот же, который
    // правит запись: читаемое и записываемое обязаны совпадать.
    // docPath нужен, чтобы markdown-ссылку в значении отдать уже путём от корня проекта.
    internal static IReadOnlyList<DocProperty> Values(string markdown, string docPath)
    {
        var parsed = Parse(markdown);
        if (parsed.Items.Count == 0) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<DocProperty>();
        foreach (var item in parsed.Items)
        {
            if (!seen.Add(item.Key)) continue;
            list.Add(new DocProperty(item.Key, item.Value, LinkOf(item.Value, docPath)));
        }
        return list;
    }

    // Первая ссылка на документ внутри значения → путь от корня проекта.
    // Внешние ссылки не в счёт: свойство-ссылка указывает на документ корпуса.
    private static string? LinkOf(string value, string docPath)
    {
        foreach (Match m in DocsIndexService.LinkRegex().Matches(value))
        {
            var target = m.Groups[2].Value.Trim();
            if (target.Length == 0 || DocsIndexService.IsExternal(target)) continue;
            var (path, _) = DocsIndexService.SplitAnchor(target);
            if (path.Length == 0) continue;
            return DocsIndexService.ResolveRelative(docPath, path);
        }
        return null;
    }

    // ---------- запись ----------

    // Правка: значение свойства. Value == null — снять свойство (строка уходит из файла),
    // пустая строка — оставить ключ со слотом под значение.
    internal readonly record struct Edit(string Key, string? Value);

    // Применить правки к тексту документа за ОДНУ пересборку.
    //
    // Правится ровно хвост строки после «**Ключ:** »: префикс остаётся байт-в-байт, поэтому
    // переживают регистр ключа, отступ и число пробелов, а дифф в git содержит одну строку,
    // а не весь файл. Несколько правок сразу — потому что смена свойства тянет за собой
    // «дату смены», а две отдельные записи файла дали бы два события синка базы знаний.
    //
    // order — ключи в порядке схемы типа: по нему новое свойство встаёт на своё место в
    // шапке, а не в конец. Иначе шапка превращалась бы в хронологию кликов.
    internal static string Write(string text, IReadOnlyList<Edit> edits, IReadOnlyList<string> order)
    {
        var parsed = Parse(text);
        var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        // Операция над исходным текстом: [Start, End) → Replacement.
        // Rank — место ключа в схеме: при совпадающих позициях (а так бывает, когда шапки
        // ещё нет и всё уезжает в одну точку) он задаёт порядок строк в файле
        var ops = new List<(int Start, int End, string Replacement, int Rank)>();
        var missing = new List<(string Key, string Value)>();

        foreach (var (key, value) in edits)
        {
            var span = Find(parsed, key);

            if (span is { } found)
            {
                if (value is null)
                {
                    // Снять свойство: строка уходит целиком вместе с переводом строки
                    ops.Add((found.Start, LineEndWithEol(text, found.End), "", IndexIn(order, key)));
                    continue;
                }

                // Пустое значение — убираем и пробелы после «:**», чтобы не оставлять
                // висящий пробел в конце строки
                var start = found.ValueStart;
                if (value.Length == 0)
                    while (start > found.Start && text[start - 1] is ' ' or '\t') start--;

                ops.Add((start, found.End, value, IndexIn(order, key)));
                continue;
            }

            if (value is null) continue;                    // снимать нечего
            missing.Add((key, value));
        }

        // Ключей в файле нет, а шапки нет вовсе — все новые строки уезжают ОДНИМ блоком.
        // Поштучная вставка отбила бы каждую строку пустой, а пустая строка обрывает шапку:
        // при следующем чтении в ней осталось бы одно свойство из двух
        if (missing.Count > 0 && !parsed.HasBlock)
        {
            var lines = missing
                .OrderBy(m => IndexIn(order, m.Key) is var i && i < 0 ? int.MaxValue : i)
                .Select(m => Line(m.Key, m.Value));
            var (at, block) = NewBlockAt(text, parsed, string.Join(eol, lines), eol);
            ops.Add((at, at, block, 0));
        }
        else
        {
            foreach (var (key, value) in missing)
            {
                var (at, line) = InsertAt(text, parsed, key, value, order, eol);
                ops.Add((at, at, line, IndexIn(order, key)));
            }
        }

        if (ops.Count == 0) return text;

        // Справа налево: иначе первая же правка сдвинула бы координаты остальных.
        // При равных позициях — по убыванию места в схеме: вставка в ту же точку сдвигает
        // ранее вставленное вправо, поэтому последнее по схеме кладётся первым
        ops.Sort((a, b) => a.Start != b.Start ? b.Start.CompareTo(a.Start) : b.Rank.CompareTo(a.Rank));
        var sb = new System.Text.StringBuilder(text);
        foreach (var (start, end, replacement, _) in ops)
        {
            sb.Remove(start, end - start);
            sb.Insert(start, replacement);
        }
        return sb.ToString();
    }

    private static string Line(string key, string value, string marker = "") =>
        value.Length > 0 ? $"{marker}**{key}:** {value}" : $"{marker}**{key}:**";

    // Первое вхождение ключа: правится ровно то свойство, которое читается (дубликаты
    // схлопываются в первый и там, и там)
    private static PropertySpan? Find(ParsedProperties parsed, string key)
    {
        foreach (var item in parsed.Items)
            if (item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return item;
        return null;
    }

    // Куда вставить строку нового свойства и как она выглядит
    private static (int At, string Line) InsertAt(string text, ParsedProperties parsed,
        string key, string value, IReadOnlyList<string> order, string eol)
    {
        // Оформление — как у соседей по шапке: голая строка, пункт списка или цитата
        var line = Line(key, value, parsed.Marker);

        if (parsed.HasBlock)
        {
            // Место в шапке — по порядку схемы: встаём сразу за ближайшим предшествующим
            // свойством, которое уже есть в файле, иначе перед первым имеющимся
            var index = IndexIn(order, key);
            PropertySpan? after = null;
            PropertySpan? before = null;
            foreach (var item in parsed.Items)
            {
                var i = IndexIn(order, item.Key);
                if (i < 0 || index < 0) continue;
                if (i < index) after = item;
                else if (before is null) before = item;
            }

            if (after is { } prev) return (LineEndWithEol(text, prev.End), line + eol);
            if (before is { } next) return (next.Start, line + eol);
            // В конец шапки. Файл без завершающего перевода строки — дописываем его сами,
            // иначе новое свойство прилипло бы к последней строке
            var tail = parsed.BlockEnd;
            return tail > 0 && text[tail - 1] is not ('\n' or '\r')
                ? (tail, eol + line + eol)
                : (tail, line + eol);
        }

        return NewBlockAt(text, parsed, line, eol);
    }

    // Шапки нет вовсе: создаём её сразу за заголовком (или в начале файла), отбивая пустой
    // строкой сверху и снизу, — ровно так написаны существующие ADR. block может содержать
    // несколько строк: пустая строка между ними оборвала бы шапку при следующем чтении
    private static (int At, string Block) NewBlockAt(string text, ParsedProperties parsed,
        string block, string eol)
    {
        var at = Math.Min(parsed.InsertOffset, text.Length);
        var head = BlankLineBefore(text, at) ? "" : eol;
        var foot = at >= text.Length ? eol : eol + eol;
        return (at, head + block + foot);
    }

    // Стоит ли перед позицией пустая строка (или это начало файла)
    private static bool BlankLineBefore(string text, int at)
    {
        if (at == 0) return true;
        var i = at;
        if (text[i - 1] != '\n') return false;
        i--;
        if (i > 0 && text[i - 1] == '\r') i--;
        return i == 0 || text[i - 1] == '\n';
    }

    private static int IndexIn(IReadOnlyList<string> order, string key)
    {
        for (var i = 0; i < order.Count; i++)
            if (order[i].Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // Конец строки вместе с её переводом строки — чтобы удаление не оставляло пустую строку
    private static int LineEndWithEol(string text, int contentEnd)
    {
        var i = contentEnd;
        if (i < text.Length && text[i] == '\r') i++;
        if (i < text.Length && text[i] == '\n') i++;
        return i;
    }

    // ---------- разбор по строкам с сохранением координат ----------

    // Строка без переводов строки + индекс её конца + начало следующей строки.
    // Split('\n') здесь не годится: записи нужны точные смещения в исходном тексте.
    private static (string Line, int ContentEnd, int Next) ReadLine(string text, int pos)
    {
        var nl = text.IndexOf('\n', pos);
        var next = nl < 0 ? text.Length : nl + 1;
        var end = nl < 0 ? text.Length : nl;
        if (end > pos && text[end - 1] == '\r') end--;
        return (text[pos..end], end, next);
    }

    private static int SkipBlank(string text, int pos)
    {
        while (pos < text.Length)
        {
            var (line, _, next) = ReadLine(text, pos);
            if (line.Trim().Length > 0) break;
            pos = next;
        }
        return pos;
    }

    // YAML front-matter в начале файла (так написаны docs/omo/translations/*.md).
    // Закрывающей черты нет в первых полусотне строк — значит это была не преамбула, а
    // горизонтальная линия: пропускаем одну строку и разбираем дальше как обычный документ.
    // Возврат в самое начало здесь не годится: строка «---» сама оборвала бы поиск шапки,
    // и документ с линией над заголовком лишился бы свойств.
    private static int SkipFrontMatter(string text)
    {
        var (first, _, afterFirst) = ReadLine(text, 0);
        if (!first.StartsWith("---", StringComparison.Ordinal) || !FrontMatterRegex().IsMatch(first))
            return 0;

        var pos = afterFirst;
        for (var i = 0; i < MaxFrontMatterLines && pos < text.Length; i++)
        {
            var (line, _, next) = ReadLine(text, pos);
            pos = next;
            if (FrontMatterRegex().IsMatch(line)) return pos;
        }
        return afterFirst;
    }
}
