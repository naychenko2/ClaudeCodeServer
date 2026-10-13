namespace ClaudeHomeServer.DeviceAgent.Exec;

/// <summary>
/// Конец хода в потоке вывода CLI (<c>--output-format stream-json</c>): строка верхнего уровня
/// с <c>"type":"result"</c>. Поле <c>type</c> CLI пишет первым, поэтому строка обязана начинаться
/// ровно с <c>{"type":"result"</c>, за которым сразу идёт <c>,</c> или <c>}</c> — так значение
/// поля совпадает целиком, а <c>result</c> внутри строки другого типа (tool_result, сообщения
/// субагента с <c>parent_tool_use_id</c>) в начало строки не попадает. Без буфера строки и разбора
/// JSON на каждом куске вывода. Строка засчитывается, когда пришёл её перевод строки, — итог хода
/// к этому моменту целиком ушёл в вывод. Куски могут резать строку где угодно: состояние
/// переживает границу куска.
/// </summary>
internal sealed class ResultLineScanner
{
    private static readonly byte[] Prefix = "{\"type\":\"result\""u8.ToArray();

    // Сколько байт префикса уже совпало в текущей строке; Prefix.Length — совпал весь, ждём разделитель
    private int _matched;
    // Строка уже не та: дальше до перевода строки не смотрим
    private bool _miss;
    // Префикс и разделитель совпали: строка — конец хода, засчитаем на её переводе строки
    private bool _hit;

    /// <summary>Скормить кусок вывода; возвращает, сколько строк конца хода в нём закончилось.</summary>
    public int Feed(ReadOnlySpan<byte> chunk)
    {
        var found = 0;
        foreach (var b in chunk)
        {
            if (b == (byte)'\n')
            {
                if (_hit) found++;
                _matched = 0;
                _miss = false;
                _hit = false;
                continue;
            }
            if (_miss || _hit) continue;
            if (_matched == Prefix.Length)
            {
                // Точный разделитель после значения: иначе это не поле type со значением "result"
                if (b is (byte)',' or (byte)'}') _hit = true;
                else _miss = true;
                continue;
            }
            if (b != Prefix[_matched]) _miss = true;
            else _matched++;
        }
        return found;
    }
}
