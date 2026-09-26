using System.Buffers.Binary;
using System.Text;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Размер и длительность mp4 без ffprobe: разбор атомов ISO BMFF (ftyp, moov/mvhd, trak/tkhd,
// mdia/hdlr). Нужен только для проверки входа до постановки: видео длиннее потолка или
// вне белого списка размеров не должно доехать до GPU
public static class MediaProbe
{
    public sealed record VideoInfo(int Width, int Height, double Seconds);

    // null — не mp4 или контейнер без видеодорожки/длительности
    public static VideoInfo? ReadMp4(byte[] bytes)
    {
        var all = new Range(0, bytes.Length);
        if (bytes.Length < 16 || Type(bytes, 4) != "ftyp") return null;
        if (Find(bytes, all, "moov") is not { } moov) return null;

        double? seconds = null;
        (int Width, int Height)? size = null;
        foreach (var (type, body) in Boxes(bytes, moov))
        {
            if (type == "mvhd") seconds = MovieSeconds(bytes.AsSpan(body.Start, body.Length));
            else if (type == "trak") size ??= VideoTrackSize(bytes, body);
        }
        return seconds is > 0 && size is { Width: > 0, Height: > 0 } wh
            ? new VideoInfo(wh.Width, wh.Height, seconds.Value)
            : null;
    }

    // Расширение для input ComfyUI по сигнатуре звука; null — не WAV/MP3/FLAC/OGG
    public static string? DetectAudioExtension(byte[] bytes)
    {
        if (bytes.Length < 12) return null;
        if (Ascii(bytes, 0, 4) == "RIFF" && Ascii(bytes, 8, 4) == "WAVE") return ".wav";
        if (Ascii(bytes, 0, 4) == "fLaC") return ".flac";
        if (Ascii(bytes, 0, 4) == "OggS") return ".ogg";
        if (Ascii(bytes, 0, 3) == "ID3" || (bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0)) return ".mp3";
        return null;
    }

    private readonly record struct Range(int Start, int Length);

    private static double? MovieSeconds(ReadOnlySpan<byte> body)
    {
        uint timescale;
        ulong duration;
        if (body.Length >= 32 && body[0] == 1)
        {
            timescale = BinaryPrimitives.ReadUInt32BigEndian(body[20..]);
            duration = BinaryPrimitives.ReadUInt64BigEndian(body[24..]);
        }
        else if (body.Length >= 20 && body[0] == 0)
        {
            timescale = BinaryPrimitives.ReadUInt32BigEndian(body[12..]);
            duration = BinaryPrimitives.ReadUInt32BigEndian(body[16..]);
        }
        else return null;
        return timescale == 0 ? null : (double)duration / timescale;
    }

    // Размер берётся из tkhd дорожки, чей hdlr — vide (у звуковой ширина нулевая)
    private static (int, int)? VideoTrackSize(byte[] bytes, Range trak)
    {
        (int, int)? size = null;
        var isVideo = false;
        foreach (var (type, body) in Boxes(bytes, trak))
        {
            if (type == "tkhd" && body.Length >= 84)
            {
                // Ширина и высота — два последних поля, 16.16 с фиксированной точкой
                var tail = bytes.AsSpan(body.Start + body.Length - 8, 8);
                size = ((int)(BinaryPrimitives.ReadUInt32BigEndian(tail) >> 16),
                    (int)(BinaryPrimitives.ReadUInt32BigEndian(tail[4..]) >> 16));
            }
            else if (type == "mdia" && Find(bytes, body, "hdlr") is { Length: >= 12 } hdlr)
            {
                isVideo = Type(bytes, hdlr.Start + 8) == "vide";
            }
        }
        return isVideo ? size : null;
    }

    private static Range? Find(byte[] bytes, Range parent, string wanted)
    {
        foreach (var (type, body) in Boxes(bytes, parent))
            if (type == wanted) return body;
        return null;
    }

    // Атомы одного уровня: [size u32][type 4][тело]; size 1 — 64-битный размер следом,
    // size 0 — до конца родителя. Битый размер обрывает разбор, а не бросает исключение
    private static List<(string Type, Range Body)> Boxes(byte[] bytes, Range parent)
    {
        var result = new List<(string, Range)>();
        var end = (long)parent.Start + parent.Length;
        long pos = parent.Start;
        while (pos + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan((int)pos));
            var type = Type(bytes, (int)pos + 4);
            var header = 8;
            if (size == 1)
            {
                if (pos + 16 > end) break;
                var large = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan((int)pos + 8));
                if (large > int.MaxValue) break;
                size = (long)large;
                header = 16;
            }
            else if (size == 0)
            {
                size = end - pos;
            }
            if (size < header || pos + size > end) break;
            result.Add((type, new Range((int)pos + header, (int)size - header)));
            pos += size;
        }
        return result;
    }

    private static string Type(byte[] bytes, int offset) =>
        offset >= 0 && offset + 4 <= bytes.Length ? Encoding.ASCII.GetString(bytes, offset, 4) : "";

    private static string Ascii(byte[] bytes, int offset, int count) => Encoding.ASCII.GetString(bytes, offset, count);
}
