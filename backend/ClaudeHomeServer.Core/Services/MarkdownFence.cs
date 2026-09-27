using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Состояние ```-забора при построчном разборе markdown — по правилам CommonMark.
///
/// Наивное «переключить флаг на любом маркере» на документации ПРО документацию врёт
/// дважды: вложенный пример (внешний забор из четырёх бэктиков, внутри обычный ```)
/// закрывается внутренним, и остаток файла разбирается как живой текст; незакрытый
/// забор от опечатки молча выкидывает весь остаток из разбора — и отчёт рапортует
/// «всё здорово». Поэтому забор помнит СИМВОЛ и ДЛИНУ открывающего маркера и
/// закрывается только тем же символом длиной не меньше.
///
/// Живёт в спине: её зовут и сканер карты (вертикаль Docs), и раскрытие @-импортов
/// (Core) — третий комплект правил разошёлся бы с двумя первыми.
/// </summary>
public sealed class MarkdownFence
{
    private char _marker;
    private int _length;

    /// <summary>Разбор сейчас внутри забора (в конце файла — забор не закрыт).</summary>
    public bool InFence => _length > 0;

    /// <summary>
    /// Скормить очередную строку. true — строка сама является маркером забора
    /// (открывающим или закрывающим), её содержимое разбирать не нужно.
    /// </summary>
    public bool Consume(string line)
    {
        var m = Fence.Match(line);
        if (!m.Success) return false;

        var run = m.Groups[1].Value;
        var tail = line[(m.Index + m.Length)..];

        if (!InFence)
        {
            // Info-string бэктикового забора не имеет права содержать бэктик (CommonMark):
            // «``inline код`` в строке» — не открытие забора
            if (run[0] == '`' && tail.Contains('`')) return false;
            _marker = run[0];
            _length = run.Length;
            return true;
        }

        // Закрывает только тот же символ, длиной не меньше открывающего и без хвоста
        if (run[0] != _marker || run.Length < _length || tail.Trim().Length > 0) return false;
        _length = 0;
        return true;
    }

    // Ограда блока кода: ``` или ~~~ (с отступом до трёх пробелов).
    // Обычный Regex, а не [GeneratedRegex]: генератор кладёт свои типы в собственный
    // namespace, и сторож границ Core (CoreAllowedNamespaces) потребовал бы открыть
    // в спине чужое поддерево ради одной регулярки
    private static readonly Regex Fence = new(@"^ {0,3}(`{3,}|~{3,})", RegexOptions.Compiled);
}
