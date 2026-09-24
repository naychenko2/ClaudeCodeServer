namespace ClaudeHomeServer.Services.Files;

/// <summary>Сборка <see cref="FileContentView"/> и MIME отдачи — одна на сервер и агента устройства.</summary>
public static class FileContentReader
{
    /// <summary>Вид файла по расширению и содержимому. Слишком большой документ — без base64.</summary>
    public static FileContentView Read(FileService files, string root, string path)
    {
        if (files.GetDocumentInfo(path) is { } doc)
        {
            var size = files.GetFileSize(root, path);
            return new FileContentView(null, true, false, IsDocument: true, DocKind: doc.Kind, MimeType: doc.Mime,
                Base64: size > FileService.MaxDocumentBytes ? null : files.GetFileBase64(root, path), FileSize: size);
        }

        if (!files.IsBinaryFile(root, path))
            return new FileContentView(files.ReadFile(root, path), false, false);

        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (files.IsImageFile(root, path))
            return new FileContentView(null, true, true,
                MimeType: ext == "svg" ? "image/svg+xml" : $"image/{ext}", Base64: files.GetFileBase64(root, path));
        if (FileService.IsVideoFile(path))
            return new FileContentView(null, true, false, IsVideo: true, MimeType: VideoMime(ext), FileSize: files.GetFileSize(root, path));
        if (FileService.IsAudioFile(path))
            return new FileContentView(null, true, false, IsAudio: true, MimeType: AudioMime(ext), FileSize: files.GetFileSize(root, path));
        return new FileContentView(null, true, false, MimeType: "application/octet-stream", FileSize: files.GetFileSize(root, path));
    }

    /// <summary>MIME отдачи потоком: видео для плеера, картинки для &lt;img src&gt; в markdown.</summary>
    public static string StreamMime(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "mp4" or "webm" or "mov" or "avi" or "mkv" => VideoMime(ext),
            // Без типа браузер угадывает по содержимому, а SVG в таком режиме не рендерится вовсе
            "png" or "gif" or "bmp" or "webp" or "avif" => $"image/{ext}",
            "jpg" or "jpeg" => "image/jpeg",
            "svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
    }

    private static string VideoMime(string ext) => ext switch
    {
        "mp4" => "video/mp4",
        "webm" => "video/webm",
        "mov" => "video/quicktime",
        "avi" => "video/x-msvideo",
        "mkv" => "video/x-matroska",
        _ => "video/mp4",
    };

    private static string AudioMime(string ext) => ext switch
    {
        "mp3" => "audio/mpeg",
        "wav" => "audio/wav",
        "ogg" => "audio/ogg",
        "flac" => "audio/flac",
        "aac" => "audio/aac",
        "m4a" => "audio/mp4",
        "opus" => "audio/opus",
        "weba" => "audio/webm",
        _ => "audio/mpeg",
    };
}
