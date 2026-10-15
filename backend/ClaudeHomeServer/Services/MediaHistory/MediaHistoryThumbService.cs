using System.Diagnostics;

namespace ClaudeHomeServer.Services.MediaHistory;

// Миниатюры истории (ADR-030 §5): лениво при первом запросе size=thumb. ffmpeg (уже на сервере,
// его использует FfmpegVideoDsp) → webp 256 px; видео — кадр из середины; звук миниатюры не имеет.
// Кэш — data/local-media-history/thumbs/{recordId}-{n}.webp, удаляется ротацией (7 дней), в бэкап
// не едет. Нагрузка ограничена: семафор на 2 процесса ffmpeg, таймаут 10 с. Отказ — null (фронт
// рисует плитку и не ретраит), не 500.
public sealed class MediaHistoryThumbService
{
    private const int ThumbSide = 256;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Rotation = TimeSpan.FromDays(7);

    // Семафор на 2 процесса ffmpeg: не больше двух генераций одновременно
    private readonly SemaphoreSlim _gate = new(2, 2);
    private readonly ILogger<MediaHistoryThumbService> _log;
    private readonly string _dir;
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly TimeProvider _time;
    private DateTime _lastRotation = DateTime.MinValue;

    public MediaHistoryThumbService(IConfiguration config, ILogger<MediaHistoryThumbService> log, TimeProvider? time = null)
    {
        _log = log;
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dir = Path.Combine(Path.GetDirectoryName(dataPath)!, "local-media-history", "thumbs");
        _ffmpeg = config["AudioDsp:FfmpegPath"] is { Length: > 0 } p ? p : "ffmpeg";
        _ffprobe = config["AudioDsp:FfprobePath"] is { Length: > 0 } fp ? fp : "ffprobe";
        _time = time ?? TimeProvider.System;
    }

    // Путь к миниатюре или null (звук, ffmpeg не готов, отказ). Кэшированную отдаёт сразу
    public async Task<string?> GetThumbAsync(string recordId, int variant, string sourcePath, string contentType, CancellationToken ct)
    {
        RotateIfNeeded();
        Directory.CreateDirectory(_dir);
        var thumb = Path.Combine(_dir, $"{Safe(recordId)}-{variant}.webp");
        if (File.Exists(thumb)) return thumb;

        // Звук миниатюры не имеет: фронт его не запрашивает, рисует плитку
        if (contentType.StartsWith("audio/", StringComparison.Ordinal)) return null;

        // Отмена (закрытая вкладка, медленная сеть) — не 500: миниатюра не готова, фронт рисует
        // плитку. Gate ещё не занят, Release не нужен
        try
        {
            await _gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        try
        {
            if (File.Exists(thumb)) return thumb; // соседний запрос успел
            var ok = contentType.StartsWith("video/", StringComparison.Ordinal)
                ? await GenerateVideoThumbAsync(sourcePath, thumb, ct)
                : await GenerateImageThumbAsync(sourcePath, thumb, ct);
            return ok ? thumb : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Картинка: scale до 256 px по большей стороне, webp
    private async Task<bool> GenerateImageThumbAsync(string source, string thumb, CancellationToken ct) =>
        await RunAsync([
            "-hide_banner", "-nostdin", "-nostats", "-v", "error", "-y",
            "-i", source,
            "-vf", $"scale={ThumbSide}:-1",
            thumb], ct);

    // Видео: кадр из середины (длительность — ffprobe; не удалось — первый кадр)
    private async Task<bool> GenerateVideoThumbAsync(string source, string thumb, CancellationToken ct)
    {
        var seek = await ProbeMidpointAsync(source, ct);
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-nostats", "-v", "error", "-y",
        };
        if (seek is not null) args.Add("-ss");
        if (seek is not null) args.Add(seek.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        args.AddRange(["-i", source, "-frames:v", "1", "-vf", $"scale={ThumbSide}:-1", thumb]);
        return await RunAsync(args, ct);
    }

    // Точка середины видео (с); ffprobe недоступен/сбой — null (первый кадр)
    private async Task<double?> ProbeMidpointAsync(string source, CancellationToken ct)
    {
        var run = await RunCaptureAsync(_ffprobe, [
            "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1",
            "-i", source], ct);
        if (run is null) return null;
        return double.TryParse(run.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0
            ? d / 2.0
            : null;
    }

    // Запуск ffmpeg: true — файл создан, false — отказ (фронт рисует плитку)
    private async Task<bool> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            _log.LogDebug(ex, "Миниатюра: ffmpeg не запустился");
            return false;
        }
        if (p is null) return false;
        using (p)
        {
            var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                _log.LogDebug("Миниатюра: ffmpeg не уложился в {Timeout} с", Timeout.TotalSeconds);
                return false;
            }
            if (p.ExitCode != 0)
            {
                _log.LogDebug("Миниатюра: ffmpeg завершился с кодом {Code}", p.ExitCode);
                return false;
            }
            return File.Exists(args[^1]);
        }
    }

    // Захват вывода (ffprobe): текст или null
    private async Task<string?> RunCaptureAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception) { return null; }
        if (p is null) return null;
        using (p)
        {
            var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            return stdout.Result;
        }
    }

    // Ротация кэша: webp старше 7 дней; не чаще раза в 6 часов (не на каждый запрос)
    private void RotateIfNeeded()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (now - _lastRotation < TimeSpan.FromHours(6)) return;
        _lastRotation = now;
        if (!Directory.Exists(_dir)) return;
        var cutoff = now - Rotation;
        try
        {
            foreach (var file in Directory.GetFiles(_dir, "*.webp"))
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "Ротация миниатюр: сбой — {Error}", ex.Message);
        }
    }

    private static string Safe(string id) =>
        id.Length == 0 || id.Contains("..") || id.IndexOfAny(['/', '\\', ':']) >= 0 ? "x" : id;
}
