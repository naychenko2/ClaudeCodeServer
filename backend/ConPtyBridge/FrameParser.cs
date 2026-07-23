namespace ConPtyBridge;

/// <summary>
/// Инкрементальный парсер кадров протокола моста — порт стейт-машины из pty-bridge.c.
///
/// Протокол stdin (кадры, чтобы управление не смешивалось с вводом):
///   [type:1][len:4 big-endian][payload:len]
///     type=0x00 — данные ввода: payload как есть в PTY;
///     type=0x01 — resize: payload = 4 байта (cols big-endian, rows big-endian).
///
/// Устойчив к фрагментации на любых границах read: данные (0x00) стримятся
/// кусками по мере поступления (payload целиком не буферизуется), resize копит
/// ровно 4 байта, неизвестный type молча пропускается (forward-compat, как в C-версии).
/// </summary>
internal sealed class FrameParser
{
    private enum State { Type, Len, Payload }

    private readonly Action<ReadOnlySpan<byte>> _onData;
    private readonly Action<int, int> _onResize;

    private State _state = State.Type;
    private byte _frameType;
    private readonly byte[] _lenBuf = new byte[4];
    private int _lenIdx;
    private uint _frameLen;
    private uint _frameGot;
    private readonly byte[] _resizeBuf = new byte[4];

    public FrameParser(Action<ReadOnlySpan<byte>> onData, Action<int, int> onResize)
    {
        _onData = onData;
        _onResize = onResize;
    }

    /// <summary>Обработать очередной прочитанный из stdin блок.</summary>
    public void Feed(ReadOnlySpan<byte> buffer)
    {
        var i = 0;
        while (i < buffer.Length)
        {
            switch (_state)
            {
                case State.Type:
                    _frameType = buffer[i];
                    _state = State.Len;
                    _lenIdx = 0;
                    i++;
                    break;

                case State.Len:
                    _lenBuf[_lenIdx++] = buffer[i];
                    i++;
                    if (_lenIdx == 4)
                    {
                        _frameLen = ((uint)_lenBuf[0] << 24) | ((uint)_lenBuf[1] << 16)
                                  | ((uint)_lenBuf[2] << 8) | _lenBuf[3];
                        _frameGot = 0;
                        // Пустой кадр — ничего не делаем, сразу ждём следующий
                        _state = _frameLen == 0 ? State.Type : State.Payload;
                    }
                    break;

                case State.Payload:
                    var remaining = _frameLen - _frameGot;
                    var avail = (uint)(buffer.Length - i);
                    var take = (int)Math.Min(avail, remaining);
                    if (_frameType == 0x00)
                    {
                        // Данные — стримим сразу куском в PTY
                        _onData(buffer.Slice(i, take));
                    }
                    else if (_frameType == 0x01)
                    {
                        // Resize — собираем до 4 байт (лишний хвост игнорируется, как в C-версии)
                        for (var k = 0; k < take; k++)
                        {
                            if (_frameGot + k < 4)
                                _resizeBuf[_frameGot + k] = buffer[i + k];
                        }
                    }
                    // Неизвестный type — payload молча пропускается (forward-compat)
                    _frameGot += (uint)take;
                    i += take;
                    if (_frameGot == _frameLen)
                    {
                        if (_frameType == 0x01 && _frameLen >= 4)
                        {
                            var cols = (_resizeBuf[0] << 8) | _resizeBuf[1];
                            var rows = (_resizeBuf[2] << 8) | _resizeBuf[3];
                            _onResize(cols, rows);
                        }
                        _state = State.Type;
                    }
                    break;
            }
        }
    }
}
