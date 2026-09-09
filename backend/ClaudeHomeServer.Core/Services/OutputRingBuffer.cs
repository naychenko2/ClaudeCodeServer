using System.Text;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Кольцевой буфер вывода процесса: держит хвост последних символов, старое вытесняет.
///
/// Общий для терминалов (<see cref="TerminalService"/>) и дев-серверов
/// (<see cref="DevServerService"/>) — обоим нужно одно и то же: реплей накопленного вывода
/// новому вьюеру, потому что xterm при ремоунте пуст и свою историю теряет.
///
/// Ограничение — по символам, а не по строкам: сборщики и дев-серверы умеют печатать
/// одну строку в мегабайт (прогресс-бары с \r), и лимит в строках от неё не спасает.
/// </summary>
public sealed class OutputRingBuffer
{
    private readonly object _lock = new();
    private readonly StringBuilder _buffer = new();
    private readonly int _maxChars;
    // Порог, после которого режем хвост. Резать ровно по _maxChars нельзя: обрезка
    // сдвигает ВЕСЬ буфер (O(n)), и на полном буфере это происходило бы на каждой
    // строке — дев-сервер во время сборки печатает их тысячами. С запасом в четверть
    // сдвиг случается раз в ~50 тысяч символов, то есть амортизированно O(1).
    private readonly int _trimAtChars;

    public OutputRingBuffer(int maxChars = 200_000)
    {
        _maxChars = maxChars;
        _trimAtChars = maxChars + Math.Max(1, maxChars / 4);
    }

    public void Append(string chunk)
    {
        lock (_lock)
        {
            _buffer.Append(chunk);
            if (_buffer.Length > _trimAtChars)
                _buffer.Remove(0, _buffer.Length - _maxChars);
        }
    }

    /// <summary>Всё накопленное — реплей новому подписчику.</summary>
    public string GetAll()
    {
        lock (_lock) return _buffer.ToString();
    }

    /// <summary>
    /// Последние непустые строки без CR — текст ошибки, когда процесс не поднялся.
    /// </summary>
    public string TailLines(int count)
    {
        string all;
        lock (_lock) all = _buffer.ToString();
        var lines = all.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .TakeLast(count);
        return string.Join("\n", lines);
    }
}
