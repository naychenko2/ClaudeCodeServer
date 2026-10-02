using System.Buffers.Binary;
using System.Text;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Длительность звука по заголовкам, без ffprobe (как MediaProbe у mp4). Нужна для потолка
// длины входа и для ETA. Не разобрали — null: время не обещаем, потолок не проверяем.
public static class AudioProbe
{
    public static double? Seconds(byte[] bytes) => MediaProbe.DetectAudioExtension(bytes) switch
    {
        ".wav" => Wav(bytes),
        ".flac" => Flac(bytes),
        ".mp3" => Mp3(bytes),
        ".ogg" => Ogg(bytes),
        _ => null,
    };

    // RIFF: fmt (byteRate) + data (размер) — чанки идут в любом порядке
    private static double? Wav(byte[] b)
    {
        uint byteRate = 0;
        long dataSize = -1;
        var pos = 12;
        while (pos + 8 <= b.Length)
        {
            var id = Encoding.ASCII.GetString(b, pos, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(pos + 4));
            if (id == "fmt " && pos + 20 <= b.Length) byteRate = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(pos + 16));
            if (id == "data")
            {
                // Потоковая запись оставляет размер 0 или 0xFFFFFFFF — тогда до конца файла
                dataSize = size is 0 or uint.MaxValue ? b.Length - pos - 8 : Math.Min(size, b.Length - pos - 8);
                break;
            }
            pos += 8 + (int)Math.Min(size + (size & 1), int.MaxValue - pos - 8);
        }
        return byteRate > 0 && dataSize >= 0 ? dataSize / (double)byteRate : null;
    }

    // STREAMINFO — первый блок метаданных: 20 бит частоты и 36 бит числа сэмплов
    private static double? Flac(byte[] b)
    {
        if (b.Length < 42 || (b[4] & 0x7F) != 0) return null;
        var s = b.AsSpan(18);
        var rate = (s[0] << 12) | (s[1] << 4) | (s[2] >> 4);
        var total = ((long)(s[3] & 0x0F) << 32) | ((long)s[4] << 24) | ((long)s[5] << 16) | ((long)s[6] << 8) | s[7];
        return rate > 0 && total > 0 ? total / (double)rate : null;
    }

    private static readonly int[] Mp3Rates1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
    private static readonly int[] Mp3Rates2 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];

    // MPEG Layer III: число кадров из заголовка Xing/Info (VBR), иначе оценка CBR по размеру
    private static double? Mp3(byte[] b)
    {
        var pos = 0;
        if (b.Length > 10 && Encoding.ASCII.GetString(b, 0, 3) == "ID3")
            pos = 10 + ((b[6] & 0x7F) << 21 | (b[7] & 0x7F) << 14 | (b[8] & 0x7F) << 7 | (b[9] & 0x7F));
        for (; pos + 4 <= b.Length && pos < 1 << 20; pos++)
        {
            if (b[pos] != 0xFF || (b[pos + 1] & 0xE0) != 0xE0) continue;
            var version = (b[pos + 1] >> 3) & 3; // 3 — MPEG1, 2 — MPEG2, 0 — MPEG2.5
            var layer = (b[pos + 1] >> 1) & 3;   // 1 — Layer III
            if (version == 1 || layer != 1) continue;
            var bitrate = (version == 3 ? Mp3Rates1 : Mp3Rates2)[b[pos + 2] >> 4] * 1000;
            var rateIndex = (b[pos + 2] >> 2) & 3;
            if (bitrate == 0 || rateIndex == 3) continue;
            var rate = new[] { 44100, 48000, 32000 }[rateIndex] >> (version == 3 ? 0 : version == 2 ? 1 : 2);
            var samplesPerFrame = version == 3 ? 1152 : 576;
            var mono = (b[pos + 3] >> 6) == 3;
            var sideInfo = version == 3 ? (mono ? 17 : 32) : (mono ? 9 : 17);
            var xing = pos + 4 + sideInfo;
            if (xing + 12 <= b.Length)
            {
                var tag = Encoding.ASCII.GetString(b, xing, 4);
                if ((tag == "Xing" || tag == "Info") && (b[xing + 7] & 1) != 0)
                {
                    var frames = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(xing + 8));
                    return frames * samplesPerFrame / (double)rate;
                }
            }
            return (b.Length - pos) * 8.0 / bitrate;
        }
        return null;
    }

    // Ogg: гранула последней страницы / частота (Opus — всегда 48 кГц, Vorbis — из заголовка)
    private static double? Ogg(byte[] b)
    {
        int rate;
        if (b.Length > 40 && Encoding.ASCII.GetString(b, 28, 8) == "OpusHead") rate = 48000;
        else if (b.Length > 44 && Encoding.ASCII.GetString(b, 29, 6) == "vorbis") rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(40));
        else return null;
        for (var pos = b.Length - 27; pos >= 0; pos--)
        {
            if (b[pos] != (byte)'O' || Encoding.ASCII.GetString(b, pos, 4) != "OggS") continue;
            var granule = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(pos + 6));
            return granule > 0 && rate > 0 ? granule / (double)rate : null;
        }
        return null;
    }
}
