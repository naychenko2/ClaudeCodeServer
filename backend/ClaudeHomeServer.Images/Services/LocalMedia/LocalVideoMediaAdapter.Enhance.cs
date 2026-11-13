using System.Collections.Concurrent;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// «Увеличить» готовый ролик ×2 / ×4 (RelayVSR): отдельная тяжёлая задача над mp4 версии, не часть «Макс» H3.
// Ролик целиком уходит во input ComfyUI, граф — ComfyWorkflows.RelayVsr; RelayVSR звук не переносит, поэтому
// исходник держим во временном файле хоста, и после прогона дорожка возвращается ffmpeg-ом без перекодирования.
// Всё состояние — в памяти: рестарт сервера бросает задачу (тикет пропадёт из очереди — Failed), сироты в input и
// output ComfyUI и временные файлы выметает Sweep по TTL.
public sealed partial class LocalVideoMediaAdapter
{
    private const string EnhanceInputSuffix = "-enh.mp4";

    // Записи об увеличениях, которых никто не дочитал до конца: больше самого долгого увеличения (×4 длинного ролика)
    private static readonly TimeSpan EnhanceAbandonedAfter = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<string, EnhanceJob> _enhances = new();

    private sealed record EnhanceJob(string Id, string InputName, string SourcePath, int Scale, int Width, int Height,
        bool HasSound, DateTime At);

    public int? EnhanceEtaSeconds(int scale, int width, int height, double seconds) =>
        LocalMediaService.VideoEnhanceEta(scale, width, height, seconds);

    public async Task<LocalVideoSubmitted> SubmitEnhanceAsync(LocalVideoEnhanceRequest request, CancellationToken ct)
    {
        var options = LocalMediaOptions.Read(config);
        if (!options.Enabled) return LocalVideoSubmitted.Fail("Локальные модели выключены на этом сервере.");
        if (!ComfyWorkflows.IsRelayVsrScale(request.Scale)) return LocalVideoSubmitted.Fail("Увеличение — ×2 или ×4.");
        if (request.Video is not { Length: > 0 }) return LocalVideoSubmitted.Fail("Нет ролика для увеличения.");
        if (dsp is not { Available: true })
            return LocalVideoSubmitted.Fail("Увеличение недоступно: на сервере нет ffmpeg, а без него звук не вернуть в ролик.");

        var id = IdPrefix + Guid.NewGuid().ToString("N");
        var source = EnhanceTempPath(id);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await File.WriteAllBytesAsync(source, request.Video, ct);
            if (await dsp.ProbeAsync(source, ct) is not { } info)
            {
                DeleteQuietly(source);
                return LocalVideoSubmitted.Fail("Файл не разобран как видео.");
            }
            if (!LocalMediaService.EnhanceSizeAllowed(info.Width, info.Height))
            {
                DeleteQuietly(source);
                return LocalVideoSubmitted.Fail($"Ролик {info.Width}×{info.Height} велик для увеличения: "
                    + "видеокарте хватает на кадр до 1344×768.");
            }
            var eta = LocalMediaService.VideoEnhanceEta(request.Scale, info.Width, info.Height, info.Seconds);
            if (eta is null)
            {
                DeleteQuietly(source);
                return LocalVideoSubmitted.Fail($"Ролик длиннее {LocalMediaService.EnhanceMaxSeconds} секунд — такой длины увеличение не замерено.");
            }

            var queue = await images.QueueLengthAsync(ct);
            if (queue is null)
            {
                DeleteQuietly(source);
                return LocalVideoSubmitted.Fail("ComfyUI не отвечает. Попробуйте позже.", busy: true);
            }
            if (queue >= options.MaxComfyQueue)
            {
                DeleteQuietly(source);
                return LocalVideoSubmitted.Fail($"Очередь локальной видеокарты занята ({queue} задач).", busy: true);
            }

            var seed = Random.Shared.NextInt64(1, 1L << 50);
            var inputName = await comfy.UploadImageAsync(request.Video, id + EnhanceInputSuffix, ct);
            var graph = ComfyWorkflows.RelayVsr(inputName, request.Scale, seed, $"{ComfyWorkflows.OutputFolder}/{id}");
            var queued = await comfy.QueuePromptAsync(graph, ct);
            var details = new LocalRunDetails("video_enhance", $"×{request.Scale}", new LocalRunParams(
                Width: info.Width, Height: info.Height, DurationSec: (int)Math.Round(info.Seconds), Seed: seed));
            ledger?.Register(queued.PromptId, request.Origin, eta, details);
            _enhances[queued.PromptId] = new EnhanceJob(id, inputName, source, request.Scale, info.Width, info.Height,
                info.HasAudio, Now);
            return new LocalVideoSubmitted(queued.PromptId, queue, eta, null);
        }
        catch (ComfyException ex)
        {
            DeleteQuietly(source);
            log.LogWarning("ComfyUI не принял увеличение видео: {Error}", ex.Message);
            return LocalVideoSubmitted.Fail(ex.Message, busy: true);
        }
        catch
        {
            DeleteQuietly(source);
            throw;
        }
    }

    // Конец прогона увеличения: забираем mp4, возвращаем звук исходника, убираем входы и выходы. Сбой склейки — Failed
    // с причиной: ролик без звука вместо ролика со звуком молча не отдаём
    private async Task<LocalVideoPoll> FinishEnhanceAsync(string ticket, EnhanceJob job, ComfyHistoryEntry history, CancellationToken ct)
    {
        var video = history.Files.FirstOrDefault(f =>
            LocalMediaService.ContentTypeOf(Path.GetExtension(f.FileName).ToLowerInvariant()) is { } t
            && t.StartsWith("video/", StringComparison.Ordinal));
        var options = LocalMediaOptions.Read(config);
        var raw = Path.ChangeExtension(job.SourcePath, ".up.mp4");
        var muxed = Path.ChangeExtension(job.SourcePath, ".mux.mp4");
        try
        {
            if (video is null) return EnhanceFailed(ticket, "ComfyUI не вернул видео.");
            var bytes = await comfy.DownloadAsync(video, ct);
            var path = video.Subfolder.Length == 0 ? video.FileName : $"{video.Subfolder}/{video.FileName}";
            PurgeEnhanceOutput(job, path, options);
            await File.WriteAllBytesAsync(raw, bytes, ct);
            var mux = await dsp!.MuxAudioAsync(raw, job.SourcePath, muxed, ct);
            if (!mux.Ok) return EnhanceFailed(ticket, mux.Error ?? "Не удалось вернуть звук в ролик.");
            var result = await File.ReadAllBytesAsync(muxed, ct);
            return new LocalVideoPoll(LocalVideoState.Completed, null,
                new LocalVideoFile(result, "video/mp4", ".mp4", job.HasSound, job.Width * job.Scale, job.Height * job.Scale), null);
        }
        finally
        {
            DeleteQuietly(raw);
            DeleteQuietly(muxed);
            ReleaseEnhance(ticket);
        }
    }

    private LocalVideoPoll EnhanceFailed(string ticket, string error)
    {
        ledger?.Complete(ticket, "failed", null, error: error);
        return new LocalVideoPoll(LocalVideoState.Failed, null, null, error);
    }

    // Увеличение кончилось (удача, сбой, отмена): входы в ComfyUI и временный исходник больше не нужны
    private void ReleaseEnhance(string ticket)
    {
        if (!_enhances.TryRemove(ticket, out var job)) return;
        PurgeEnhanceInputs(job, LocalMediaOptions.Read(config));
    }

    private void PurgeEnhanceInputs(EnhanceJob job, LocalMediaOptions options)
    {
        DeleteQuietly(job.SourcePath);
        if (cleanup is null || !options.CleanupEnabled) return;
        cleanup.DeleteOwn(options.ComfyInputDir.Trim(), job.InputName, job.Id, LocalMediaCleanup.InputPrefixes, '-');
    }

    private void PurgeEnhanceOutput(EnhanceJob job, string outputPath, LocalMediaOptions options)
    {
        if (cleanup is null || !options.CleanupEnabled) return;
        cleanup.DeleteOwn(options.ComfyOutputDir.Trim(), outputPath, job.Id, LocalMediaCleanup.OutputPrefixes, '_');
    }

    private void SweepEnhances(DateTime now, LocalMediaOptions options)
    {
        foreach (var (ticket, job) in _enhances)
            if (now - job.At > EnhanceAbandonedAfter && _enhances.TryRemove(ticket, out _))
                PurgeEnhanceInputs(job, options);
    }

    private static string EnhanceTempPath(string id) => Path.Combine(Path.GetTempPath(), "ccs-video-enhance", id + ".mp4");

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
