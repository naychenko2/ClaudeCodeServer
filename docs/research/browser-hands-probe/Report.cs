using System.Text;

namespace BrowserHandsProbe;

sealed class Report
{
    readonly StringBuilder _text = new();
    readonly StringBuilder _appendix = new();
    readonly List<(string Item, string Verdict, string Summary)> _summary = [];

    public void Line(string s = "")
    {
        Console.WriteLine(s);
        _text.AppendLine(s);
    }

    public void Section(string title)
    {
        Line();
        Line("== " + title);
    }

    public void Verdict(string item, string verdict, string summary)
    {
        Line($"  ИТОГ: {verdict} — {summary}");
        _summary.Add((item, verdict, summary));
    }

    /// <summary>Длинные сырые данные — только в файл, не в консоль.</summary>
    public void Appendix(string title, string body)
    {
        _appendix.AppendLine().AppendLine("--- " + title).AppendLine(body.TrimEnd());
    }

    public void PrintSummary()
    {
        Section("СВОДКА");
        foreach (var (item, verdict, summary) in _summary)
            Line($"  [{verdict,-4}] {item}: {summary}");
    }

    public string Save(string dir)
    {
        var name = $"BrowserHandsProbe-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
        foreach (var d in new[] { dir, Path.GetTempPath() })
        {
            try
            {
                var path = Path.Combine(d, name);
                File.WriteAllText(path, _text.ToString() + (_appendix.Length > 0 ? "\n\nПРИЛОЖЕНИЯ\n" + _appendix : ""),
                    new UTF8Encoding(true));
                return path;
            }
            catch { }
        }
        return "(не удалось сохранить)";
    }
}
