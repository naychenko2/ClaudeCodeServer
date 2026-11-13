using System.Diagnostics;
using System.Globalization;

namespace ClaudeHomeServer.Services.Backup;

// Замер снимка по фазам: время обхода, ссылок/копирования, хешей, сжатия, манифеста.
// Без этого узкое место 20-минутного шага data-backup приходилось угадывать — теперь
// выкатка пишет разбивку в свой лог (CLI печатает Format() в stdout, сервис — в ILogger).
public class BackupMetrics
{
    private readonly List<(string Phase, TimeSpan Time)> _phases = [];
    private readonly Stopwatch _total = Stopwatch.StartNew();

    /// <summary>Режим снимка для заголовка: «zip» или «snapshot».</summary>
    public string Mode { get; init; } = "";

    public int Linked { get; set; }
    public int Copied { get; set; }
    /// <summary>Ссылку создать не вышло (другая ФС, EXDEV, права) — файл ушёл честной копией.</summary>
    public int LinkFailed { get; set; }
    public long CopiedBytes { get; set; }
    /// <summary>Объём файлов, подхваченных ссылками (байт, не записанных заново).</summary>
    public long LinkedBytes { get; set; }
    /// <summary>Размер итогового zip (только режим zip).</summary>
    public long ArchiveBytes { get; set; }

    public TimeSpan Total => _total.Elapsed;

    public IReadOnlyList<(string Phase, TimeSpan Time)> Phases => _phases;

    // Фазы копятся: обход и копирование чередуются по файлам, и в журнал идёт сумма
    public void Add(string phase, TimeSpan time)
    {
        var index = _phases.FindIndex(p => p.Phase == phase);
        if (index < 0) _phases.Add((phase, time));
        else _phases[index] = (phase, _phases[index].Time + time);
    }

    /// <summary>Замерить кусок работы и записать его время в фазу.</summary>
    public void Measure(string phase, Action action)
    {
        var start = Stopwatch.GetTimestamp();
        try { action(); }
        finally { Add(phase, Stopwatch.GetElapsedTime(start)); }
    }

    public static long Now() => Stopwatch.GetTimestamp();

    public void AddSince(string phase, long startTimestamp) =>
        Add(phase, Stopwatch.GetElapsedTime(startTimestamp));

    /// <summary>Многострочная сводка для лога выкатки.</summary>
    public string Format()
    {
        var ci = CultureInfo.InvariantCulture;
        var phases = string.Join(" · ", _phases.Select(p => $"{p.Phase} {Seconds(p.Time)}"));
        var files = Mode == "snapshot"
            ? $"ссылок {Linked} ({Mb(LinkedBytes)}), копий {Copied} ({Mb(CopiedBytes)})" +
              (LinkFailed > 0 ? $", ссылок не вышло {LinkFailed}" : "")
            : $"скопировано {Copied} файлов ({Mb(CopiedBytes)})" +
              (ArchiveBytes > 0 ? $", zip {Mb(ArchiveBytes)}" : "");
        return $"  фазы: {phases} · итого {Seconds(Total)}{Environment.NewLine}  файлы: {files}";

        string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.0", ci) + " с";
        string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0.0", ci) + " МБ";
    }
}
