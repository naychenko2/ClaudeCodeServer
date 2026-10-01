using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.AudioEditor.Controllers;

// Где на диске лежит файл версии нити (ADR-021 §2). Исходник — файл проекта: только через
// ProjectLinkGuard.ResolveInside и только у области проекта. Версия запуска — файл рабочей папки
// задачи владельца: путь из нити сверяется с папкой задачи, чтобы испорченный или подменённый файл
// нитей не вывел за неё. Версия без задачи (монтаж без ИИ, этап 5) пока своих файлов на диске не
// имеет — null. null всегда значит «нет такого файла», причину наружу не выдаём.
public static class AudioVersionFiles
{
    public static string? Resolve(AudioEditWorkspace workspace, string ownerId, AudioEditScope scope,
        AudioThreadVersion version, AudioVersionFile file)
    {
        if (version.IsOrigin)
            return scope.Project is { } project ? ProjectLinkGuard.ResolveInside(project.RootPath, file.Path) : null;
        if (version.JobId is not { } jobId) return null;

        string jobDir;
        try { jobDir = Path.GetFullPath(workspace.JobDir(ownerId, jobId)); }
        catch (ArgumentException) { return null; }
        if (string.IsNullOrWhiteSpace(file.Path) || Path.IsPathRooted(file.Path)) return null;
        var full = Path.GetFullPath(Path.Combine(jobDir, file.Path));
        return full.StartsWith(jobDir + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    // Content-Type по расширению: плееру браузера он нужен честный, иначе перемотка и длительность врут
    public static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".flac" => "audio/flac",
        ".ogg" or ".oga" => "audio/ogg",
        ".opus" => "audio/opus",
        ".m4a" or ".aac" => "audio/mp4",
        ".webm" => "audio/webm",
        ".mid" or ".midi" => "audio/midi",
        ".txt" or ".lrc" => "text/plain; charset=utf-8",
        ".srt" => "application/x-subrip",
        ".abc" => "text/vnd.abc; charset=utf-8",
        _ => "application/octet-stream",
    };
}
