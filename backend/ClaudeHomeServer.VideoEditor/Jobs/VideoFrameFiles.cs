namespace ClaudeHomeServer.Services.VideoEditor.Jobs;

// Загруженный кадр: вид изображения определяется по сигнатуре начала файла, а не по имени и Content-Type
public static class VideoFrameFiles
{
    // Расширение с точкой (.png/.jpg/.webp) или null — не картинка допустимого вида
    public static string? ExtensionBySignature(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 8 && head[..8].SequenceEqual((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return ".png";
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return ".jpg";
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return ".webp";
        return null;
    }
}
