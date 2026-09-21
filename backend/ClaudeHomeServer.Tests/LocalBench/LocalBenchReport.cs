using System.Text;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.LocalBench;

/// <summary>
/// Итог прогона места: строка на кейс плюс сводка. Печатается в вывод теста — числа
/// переносятся в документ замеров руками, как у живого замера скорости.
///
/// Четыре счётчика считаются РАЗДЕЛЬНО и намеренно:
///  • валидность — ответ удовлетворяет формальному контракту места;
///  • совпадение с эталоном — выбор места сошёлся с живыми данными продукта. Валидность
///    отвечает «соблюдён ли формат», совпадение — «разумен ли выбор»; в одном числе они
///    сложились бы в ложь: место с безупречным форматом и случайными ответами выглядело
///    бы годным;
///  • обрыв — вывод обрезан потолком NumPredict (оборванный JSON молча деградирует место,
///    это самостоятельный риск, а не подвид невалидности);
///  • время — длительность вызова.
///
/// Время сводится МЕДИАНОЙ, а не средним: замер идёт на живом движке, где отдельный
/// выброс (сборка мусора, соседняя нагрузка на карту) утягивает среднее и превращает
/// сводку в выдумку. Минимум и максимум печатаются рядом — разброс сам по себе
/// информативен: широкий говорит, что стенду верить нельзя, а не что место медленное.
/// </summary>
public sealed class LocalBenchReport(
    string place, string executor = "", LocalBenchReference? reference = null)
{
    private readonly List<LocalBenchRow> _rows = [];

    public string Place { get; } = place;

    /// <summary>Кто исполнял ходы: без этого две таблицы прогона не отличить друг от друга.</summary>
    public string Executor { get; } = executor;

    /// <summary>Чем сверяется выбор места. null — эталона у места нет, метрика не считается.</summary>
    public LocalBenchReference? Reference { get; } = reference;

    public IReadOnlyList<LocalBenchRow> Rows => _rows;

    public void Add(LocalBenchRow row) => _rows.Add(row);

    public int Total => _rows.Count;
    public int ValidCount => _rows.Count(r => r.Valid);
    public int TruncatedCount => _rows.Count(r => r.Truncated);
    public int SilentCount => _rows.Count(r => !r.Answered);

    /// <summary>
    /// Знаменатель совпадения — кейсы, У КОТОРЫХ ЭТАЛОН ЕСТЬ, а не все кейсы банка:
    /// кейс без эталона нечем сверять, и посчитай его несовпадением — метрика поехала бы
    /// вниз ровно на дырах банка, а не на промахах места.
    /// </summary>
    public int ReferencedCount => _rows.Count(r => r.Match.Kind != LocalBenchMatchKind.NoReference);
    public int MatchCount => _rows.Count(r => r.Match.Kind == LocalBenchMatchKind.Match);
    public int MismatchCount => _rows.Count(r => r.Match.Kind == LocalBenchMatchKind.Mismatch);
    public long MedianMs => Median(_rows.Select(r => r.DurationMs));
    public long MinMs => _rows.Count == 0 ? 0 : _rows.Min(r => r.DurationMs);
    public long MaxMs => _rows.Count == 0 ? 0 : _rows.Max(r => r.DurationMs);

    public void WriteTo(ITestOutputHelper output)
    {
        output.WriteLine($"Место: {Place} — кейсов {Total}"
                         + (string.IsNullOrEmpty(Executor) ? "" : $"; исполнитель: {Executor}"));
        output.WriteLine("");
        output.WriteLine($"{"кейс",-24} {"валиден",-9} {"эталон",-8} {"обрыв",-7} {"мс",-7} {"ток",-5} {"ход",-4} причина / результат");
        output.WriteLine(new string('-', 130));
        foreach (var r in _rows)
            output.WriteLine(
                $"{Cut(r.CaseId, 24),-24} {(r.Valid ? "да" : "НЕТ"),-9} {MatchCell(r.Match),-8} "
                + $"{(r.Truncated ? "ДА" : "-"),-7} "
                + $"{r.DurationMs,-7} {r.CompletionTokens,-5} {r.Turns,-4} "
                + $"{Cut(Outcome(r), 58)}");
        output.WriteLine(new string('-', 130));
        output.WriteLine(
            $"Валидность {ValidCount}/{Total}; обрывов {TruncatedCount}/{Total}; "
            + $"молчаний {SilentCount}/{Total}; время медиана {MedianMs} мс "
            + $"(минимум {MinMs}, максимум {MaxMs}; прогрев в счёт не идёт)");
        output.WriteLine(MatchSummary());
        // Доли на малом знаменателе не считаем — правило 3 спецификации Батареи I.
    }

    /// <summary>
    /// Строка сводки по второй метрике. Отдельная от валидности НАМЕРЕННО — и числом, и
    /// строкой: подпись объясняет, как читать несовпадение, и без неё таблицу прочитают
    /// приговором там, где эталон был всего лишь одним из верных ответов.
    /// </summary>
    public string MatchSummary()
    {
        if (Reference is null)
            return "Совпадение с эталоном не считается: эталона у места нет "
                   + "(в банке нет поля expect) — мерится только контракт";

        if (ReferencedCount == 0)
            return $"Совпадение с эталоном ({Reference.What}) не посчитано: "
                   + $"ни у одного из {Total} кейсов банка нет эталона";

        var head = $"Совпадение с эталоном ({Reference.What}) {MatchCount}/{ReferencedCount}";
        var gap = ReferencedCount < Total
            ? $" (без эталона {Total - ReferencedCount} кейсов — в знаменатель не идут)"
            : "";

        var count = MismatchCount == 0 ? "их нет" : $"их {MismatchCount}";
        var tail = Reference.Strength == LocalBenchReferenceStrength.Exact
            ? $"; эталон ОДНОЗНАЧЕН: несовпадение — ошибка места ({count})"
            : $"; НИЖНЯЯ ГРАНИЦА качества, не оценка: эталон — один из верных ответов, "
              + $"и несовпадение ({count}) означает «нужен взгляд человека», а не «ошибка»";

        return head + gap + tail;
    }

    // Колонка эталона: «-» у кейса без эталона — не то же самое, что несовпадение.
    private static string MatchCell(LocalBenchMatch match) => match.Kind switch
    {
        LocalBenchMatchKind.Match => "да",
        LocalBenchMatchKind.Mismatch => "НЕТ",
        _ => "-",
    };

    // Что показать в последней колонке: результат места, а следом — чем разошлось с
    // эталоном (деталь сверки, которой в самом результате не видно: у persona-voice это
    // пол голоса, у task-classify — метки).
    private static string Outcome(LocalBenchRow r)
    {
        var text = r.Note ?? r.Violation ?? "";
        return string.IsNullOrEmpty(r.Match.Detail) ? text : $"{text} | {r.Match.Detail}";
    }

    private static long Median(IEnumerable<long> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        if (v.Length == 0) return 0;
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }

    private static string Cut(string s, int max)
    {
        var flat = new StringBuilder(s.Length);
        foreach (var ch in s) flat.Append(ch is '\n' or '\r' ? ' ' : ch);
        var text = flat.ToString();
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}

/// <summary>
/// Строка таблицы «место × кейс × валидность × совпадение × время».
/// <paramref name="Violation"/> — чем именно нарушен контракт (пусто у валидного ответа).
/// <paramref name="Note"/> — что место вернуло на выходе, для глазной проверки.
/// <paramref name="Turns"/> — сколько ходов модели стоил кейс: у двухходового места
/// (project-icon) третий ход означает сработавший повтор, и это видно только здесь.
/// Свойство <see cref="Match"/> — сошёлся ли выбор с эталоном банка; у места без эталона
/// остаётся <see cref="LocalBenchMatch.None"/> и в знаменатель метрики не идёт.
/// </summary>
public sealed record LocalBenchRow(
    string CaseId, bool Valid, string? Violation, bool Truncated, bool Answered,
    long DurationMs, int CompletionTokens, string? Note, int Turns = 1)
{
    public LocalBenchMatch Match { get; init; } = LocalBenchMatch.None;
}
