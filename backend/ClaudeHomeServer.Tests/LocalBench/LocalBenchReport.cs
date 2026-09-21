using System.Text;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.LocalBench;

/// <summary>
/// Итог прогона места: строка на кейс плюс сводка. Печатается в вывод теста — числа
/// переносятся в документ замеров руками, как у живого замера скорости.
///
/// Три счётчика считаются РАЗДЕЛЬНО и намеренно:
///  • валидность — ответ удовлетворяет формальному контракту места;
///  • обрыв — вывод обрезан потолком NumPredict (оборванный JSON молча деградирует место,
///    это самостоятельный риск, а не подвид невалидности);
///  • время — длительность вызова.
///
/// Время сводится МЕДИАНОЙ, а не средним: замер идёт на живом движке, где отдельный
/// выброс (сборка мусора, соседняя нагрузка на карту) утягивает среднее и превращает
/// сводку в выдумку. Минимум и максимум печатаются рядом — разброс сам по себе
/// информативен: широкий говорит, что стенду верить нельзя, а не что место медленное.
/// </summary>
public sealed class LocalBenchReport(string place, string executor = "")
{
    private readonly List<LocalBenchRow> _rows = [];

    public string Place { get; } = place;

    /// <summary>Кто исполнял ходы: без этого две таблицы прогона не отличить друг от друга.</summary>
    public string Executor { get; } = executor;

    public IReadOnlyList<LocalBenchRow> Rows => _rows;

    public void Add(LocalBenchRow row) => _rows.Add(row);

    public int Total => _rows.Count;
    public int ValidCount => _rows.Count(r => r.Valid);
    public int TruncatedCount => _rows.Count(r => r.Truncated);
    public int SilentCount => _rows.Count(r => !r.Answered);
    public long MedianMs => Median(_rows.Select(r => r.DurationMs));
    public long MinMs => _rows.Count == 0 ? 0 : _rows.Min(r => r.DurationMs);
    public long MaxMs => _rows.Count == 0 ? 0 : _rows.Max(r => r.DurationMs);

    public void WriteTo(ITestOutputHelper output)
    {
        output.WriteLine($"Место: {Place} — кейсов {Total}"
                         + (string.IsNullOrEmpty(Executor) ? "" : $"; исполнитель: {Executor}"));
        output.WriteLine("");
        output.WriteLine($"{"кейс",-24} {"валиден",-9} {"обрыв",-7} {"мс",-7} {"ток",-5} {"ход",-4} причина / результат");
        output.WriteLine(new string('-', 115));
        foreach (var r in _rows)
            output.WriteLine(
                $"{Cut(r.CaseId, 24),-24} {(r.Valid ? "да" : "НЕТ"),-9} {(r.Truncated ? "ДА" : "-"),-7} "
                + $"{r.DurationMs,-7} {r.CompletionTokens,-5} {r.Turns,-4} {Cut(r.Note ?? r.Violation ?? "", 50)}");
        output.WriteLine(new string('-', 115));
        output.WriteLine(
            $"Валидность {ValidCount}/{Total}; обрывов {TruncatedCount}/{Total}; "
            + $"молчаний {SilentCount}/{Total}; время медиана {MedianMs} мс "
            + $"(минимум {MinMs}, максимум {MaxMs}; прогрев в счёт не идёт)");
        // Доли на малом знаменателе не считаем — правило 3 спецификации Батареи I.
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
/// Строка таблицы «место × кейс × валидность × время».
/// <paramref name="Violation"/> — чем именно нарушен контракт (пусто у валидного ответа).
/// <paramref name="Note"/> — что место вернуло на выходе, для глазной проверки.
/// <paramref name="Turns"/> — сколько ходов модели стоил кейс: у двухходового места
/// (project-icon) третий ход означает сработавший повтор, и это видно только здесь.
/// </summary>
public sealed record LocalBenchRow(
    string CaseId, bool Valid, string? Violation, bool Truncated, bool Answered,
    long DurationMs, int CompletionTokens, string? Note, int Turns = 1);
