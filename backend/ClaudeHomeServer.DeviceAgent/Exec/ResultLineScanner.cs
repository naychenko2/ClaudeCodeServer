namespace ClaudeHomeServer.DeviceAgent.Exec;

/// <summary>
/// Конец хода в потоке вывода CLI (<c>--output-format stream-json</c>): строка, начинающаяся с
/// <c>{"type":"result"</c>. Поле <c>type</c> CLI пишет первым, поэтому хватает сравнения префикса
/// строки — без буфера строки и разбора JSON на каждом куске вывода. Куски могут резать строку где
/// угодно: состояние переживает границу куска.
/// </summary>
internal sealed class ResultLineScanner
{
    private static readonly byte[] Prefix = "{\"type\":\"result\""u8.ToArray();

    // Сколько байт префикса уже совпало в текущей строке; -1 — строка уже не та
    private int _matched;

    /// <summary>Скормить кусок вывода; возвращает, сколько строк конца хода в нём закончилось.</summary>
    public int Feed(ReadOnlySpan<byte> chunk)
    {
        var found = 0;
        foreach (var b in chunk)
        {
            if (b == (byte)'\n')
            {
                _matched = 0;
                continue;
            }
            if (_matched < 0) continue;
            if (b != Prefix[_matched])
            {
                _matched = -1;
                continue;
            }
            if (++_matched == Prefix.Length)
            {
                found++;
                _matched = -1;
            }
        }
        return found;
    }
}
