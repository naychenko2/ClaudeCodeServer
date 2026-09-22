using System.Text;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.LocalBench;

/// <summary>
/// Итог прогона места оси B: строка на кейс плюс сводка. Печатается в вывод теста — числа
/// переносятся в документ замеров руками, как у оси A.
///
/// Метрик ТРИ, и они считаются раздельно намеренно:
///  • recall фактов — сколько размеченных фактов доехало до выжимки. Неполнота;
///  • сохранение отказов — что стало с «решили НЕ делать X». Ложь, а не неполнота, и
///    цена у неё другая: сводка, превратившая отказ в решение, — провал независимо
///    от recall;
///  • время — медиана (среднее утянул бы одиночный выброс живого стенда).
///
/// Четвёртой — ВЫДУМОК — здесь нет и быть не может: «утверждение не выводится из входа»
/// подстрокой не считается. Их считает судья по файлу пар «вход + выжимка»
/// (<see cref="LocalBenchJudgeDump"/>), и отчёт честно печатает, что счёт выдумок в
/// таблицу не входит, вместо того чтобы подсунуть вместо него похожее число.
/// </summary>
public sealed class LocalBenchFactsReport(string place, string executor = "")
{
    private readonly List<LocalBenchFactsRow> _rows = [];

    public string Place { get; } = place;

    /// <summary>Кто исполнял ходы: без этого два прогона не отличить друг от друга.</summary>
    public string Executor { get; } = executor;

    public IReadOnlyList<LocalBenchFactsRow> Rows => _rows;

    public void Add(LocalBenchFactsRow row) => _rows.Add(row);

    public int Total => _rows.Count;

    /// <summary>Знаменатель recall — ВСЕ размеченные факты банка, а не кейсы.</summary>
    public int FactsTotal => _rows.Sum(r => r.FactsTotal);
    public int FactsRecalled => _rows.Sum(r => r.Recalled);

    public int RefusalsTotal => _rows.Sum(r => r.RefusalsTotal);
    public int RefusalsPreserved => _rows.Sum(r => r.RefusalsPreserved);
    public int RefusalsSuspect => _rows.Sum(r => r.RefusalsSuspect);
    public int RefusalsMissed => _rows.Sum(r => r.RefusalsMissed);

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
        output.WriteLine($"{"кейс",-24} {"recall",-8} {"отказы",-16} {"обрыв",-7} {"мс",-7} "
                         + $"{"ток",-5} {"симв",-6} пропущенные факты");
        output.WriteLine(new string('-', 130));
        foreach (var r in _rows)
            output.WriteLine(
                $"{Cut(r.CaseId, 24),-24} {$"{r.Recalled}/{r.FactsTotal}",-8} "
                + $"{RefusalCell(r),-16} {(r.Truncated ? "ДА" : "-"),-7} {r.DurationMs,-7} "
                + $"{r.CompletionTokens,-5} {r.SummaryChars,-6} {Cut(Missed(r), 44)}");
        output.WriteLine(new string('-', 130));
        output.WriteLine($"Recall фактов {FactsRecalled}/{FactsTotal}; "
                         + $"время медиана {MedianMs} мс (минимум {MinMs}, максимум {MaxMs}; "
                         + "прогрев в счёт не идёт); "
                         + $"обрывов {TruncatedCount}/{Total}; молчаний {SilentCount}/{Total}");
        output.WriteLine(RefusalSummary());
        output.WriteLine(
            "Выдумки в этой таблице НЕ посчитаны: «утверждение не выводится из входа» "
            + "подстрокой не проверяется. Их считает судья по файлу пар — см. строку «Пары для судьи».");
        // Доли на малом знаменателе не считаем — правило 3 спецификации Батареи I.
    }

    /// <summary>
    /// Строка сводки по ловушке на отрицания. Отдельная от recall и словами, а не числом:
    /// «подозрение» — это не приговор, и без подписи таблицу прочитают как счёт провалов.
    /// </summary>
    public string RefusalSummary()
    {
        if (RefusalsTotal == 0)
            return "Ловушка на отрицания НЕ проверена: в банке нет ни одного факта-отказа "
                   + "(kind=refusal) — а спецификация требует их в каждом банке";

        return $"Отказы: сохранены {RefusalsPreserved}/{RefusalsTotal}, "
               + $"подозрение на переворот {RefusalsSuspect}, не упомянуты вовсе {RefusalsMissed}. "
               + "«Подозрение» — тема отказа в выжимке есть, отрицания рядом машина не нашла; "
               + "переворот это или оборот вне списка маркеров, решает судья. "
               + "«Не упомянут» — пропуск, а не ложь: цена другая";
    }

    private static string RefusalCell(LocalBenchFactsRow r) =>
        r.RefusalsTotal == 0
            ? "-"
            : $"{r.RefusalsPreserved}/{r.RefusalsTotal}"
              + (r.RefusalsSuspect > 0 ? $" ?{r.RefusalsSuspect}" : "")
              + (r.RefusalsMissed > 0 ? $" —{r.RefusalsMissed}" : "");

    private static string Missed(LocalBenchFactsRow r) =>
        r.MissedFactIds.Count == 0 ? "-" : string.Join(", ", r.MissedFactIds);

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
/// Строка таблицы «кейс × recall × отказы × время».
/// <paramref name="SummaryChars"/> — длина выжимки: у мест с требованием «2–3 предложения»
/// многословие само по себе дефект, и без этой колонки оно невидимо.
/// </summary>
public sealed record LocalBenchFactsRow(
    string CaseId,
    int FactsTotal,
    int Recalled,
    IReadOnlyList<string> MissedFactIds,
    int RefusalsTotal,
    int RefusalsPreserved,
    int RefusalsSuspect,
    int RefusalsMissed,
    bool Truncated,
    bool Answered,
    long DurationMs,
    int CompletionTokens,
    int SummaryChars)
{
    /// <summary>Собрать строку по приговорам фактов кейса.</summary>
    public static LocalBenchFactsRow From(string caseId, IReadOnlyList<FactOutcome> outcomes,
        LocalBenchTurns turns, string? summary)
    {
        var refusals = outcomes.Where(o => o.Fact.IsRefusal).ToArray();
        return new LocalBenchFactsRow(
            CaseId: caseId,
            FactsTotal: outcomes.Count,
            Recalled: outcomes.Count(o => o.Recalled),
            MissedFactIds: outcomes.Where(o => !o.Recalled).Select(o => o.Fact.Id).ToArray(),
            RefusalsTotal: refusals.Length,
            RefusalsPreserved: refusals.Count(o => o.Negation == NegationVerdict.Preserved),
            RefusalsSuspect: refusals.Count(o => o.Negation == NegationVerdict.Suspect),
            RefusalsMissed: refusals.Count(o => o.Negation == NegationVerdict.Missed),
            Truncated: turns.Truncated,
            Answered: turns.Answered,
            DurationMs: turns.DurationMs,
            CompletionTokens: turns.CompletionTokens,
            SummaryChars: summary?.Length ?? 0);
    }
}
