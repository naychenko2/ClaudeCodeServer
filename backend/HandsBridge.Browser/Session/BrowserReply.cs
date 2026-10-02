using System.Text;
using ClaudeHomeServer.HandsBridge.Browser.Cdp;

namespace ClaudeHomeServer.HandsBridge.Browser.Session;

/// <summary>Ответ инструмента <c>browser_*</c>: текст модели (по-английски) и, для снимка экрана, PNG.</summary>
/// <param name="Timing">Замер вызова для <c>hands.log</c>; модели не отдаётся.</param>
/// <param name="LogDetail">Подробность для <c>hands.log</c> (скрипт <c>browser_evaluate</c> и его итог); модели не отдаётся.</param>
public sealed record BrowserReply(
    string Text,
    bool IsError = false,
    byte[]? Png = null,
    BrowserTiming? Timing = null,
    string? LogDetail = null)
{
    private const int MaxLoggedErrorChars = 300;

    /// <summary>
    /// Строка <c>hands.log</c> на каждый вызов: где ушло время (очередь, выдача браузера, команды
    /// CDP, ожидание страницы) и сколько символов получила модель — по ней после живого прогона
    /// видно, кто тормозит: браузер или модель.
    /// </summary>
    public string LogLine(string tool)
    {
        var sb = new StringBuilder("браузер: ").Append(tool);
        if (Timing is { } t)
        {
            sb.Append(' ').Append(Ms(t.Total)).Append(" мс: очередь ").Append(Ms(t.Queue))
              .Append(", браузер ").Append(Ms(t.Acquire))
              .Append(", CDP ").Append(t.CdpCalls).Append(" выз. ").Append(Ms(t.Cdp)).Append(" мс")
              .Append(", ожидание страницы ").Append(Ms(t.Wait)).Append(" мс");
            if (t.Stages is { Count: > 0 } stages)
            {
                sb.Append(" [");
                for (var i = 0; i < stages.Count; i++)
                {
                    if (i > 0)
                        sb.Append(", ");
                    sb.Append(stages[i].Name).Append(' ').Append(Ms(stages[i].Elapsed)).Append(" мс");
                    if (stages[i].Detail is { } detail)
                        sb.Append(" (").Append(detail).Append(')');
                }
                sb.Append(']');
            }
        }

        sb.Append("; ответ ").Append(Text.Length).Append(" симв.");
        if (Png is not null)
            sb.Append(" + PNG ").Append(Png.Length / 1024).Append(" КБ");
        if (IsError)
            sb.Append("; ошибка: ").Append(Text.Length > MaxLoggedErrorChars ? Text[..MaxLoggedErrorChars] + "…" : Text);
        if (LogDetail is not null)
            sb.Append("; ").Append(LogDetail);
        return sb.ToString();
    }

    private static long Ms(TimeSpan span) => (long)span.TotalMilliseconds;
}

/// <summary>Замер одного вызова <c>browser_*</c>.</summary>
/// <param name="Total">От входа в сессию до готового ответа.</param>
/// <param name="Queue">Ожидание предыдущего вызова (сессия исполняет их по одному).</param>
/// <param name="Acquire">Выдача браузера; на первом вызове и после смерти Chrome — его запуск.</param>
/// <param name="CdpCalls">Команд CDP, включая команды запуска.</param>
/// <param name="Cdp">Время команд CDP от записи до ответа.</param>
/// <param name="Wait">Ожидание событий страницы: загрузка, начало перехода, затишье.</param>
/// <param name="Stages">
/// Этапы внутри вызова (<c>Page.navigate</c>, DOMContentLoaded, затишье, снимок): CDP и ожидание
/// выше — суммы, по ним не видно, переход тормозит или снимок.
/// </param>
public sealed record BrowserTiming(
    TimeSpan Total,
    TimeSpan Queue,
    TimeSpan Acquire,
    int CdpCalls,
    TimeSpan Cdp,
    TimeSpan Wait,
    IReadOnlyList<CdpStage>? Stages = null);
