using System.Collections.Concurrent;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Адаптер шва ILocalVideoMedia для модуля «Видео» (ADR-022, §3). Прямо на ComfyClient с шаблоном
// ComfyWorkflows.ImageToVideo (узел last_frame), а не через LocalMediaService: тот ведёт задачу в сторе
// и пишет результат в .cc-attachments проекта (LocalMediaCollector собрал бы его второй раз), а модуль
// кладёт клип в свою рабочую папку. Белый список размеров — ComfyWorkflows.VideoSizes, потолок длины —
// LocalMediaOptions.MaxVideoSeconds, проверка кадров и ETA — статика LocalMediaService. Живость ComfyUI
// общая с картинками: её кеширует адаптер ILocalImageMedia.
public sealed class LocalVideoMediaAdapter(
    ComfyClient comfy,
    IConfiguration config,
    ILocalImageMedia images,
    ILogger<LocalVideoMediaAdapter> log,
    LocalMediaCleanup? cleanup = null,
    TimeProvider? time = null,
    ComfyProgressTracker? steps = null,
    ComfyPromptLedger? ledger = null,
    ComfyQueueMonitor? monitor = null) : ILocalVideoMedia
{
    private DateTime Now => (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    private const string IdPrefix = "lv_";
    private const string FirstSlot = "первый кадр";
    private const string LastSlot = "последний кадр";

    // Контекст завершённых съёмок для апскейла: тикет → то, из чего пересобирается conditioning (промпт, первый
    // кадр в input ComfyUI, сид, число кадров) и пути латентов в output. Только в памяти: рестарт сервера
    // делает увеличение недоступным (отказ с причиной). Записи старше ILocalVideoMedia.ShotTtl выметает Sweep:
    // при новой съёмке и по таймеру фонового сборщика, вместе с их файлами в ComfyUI
    private readonly ConcurrentDictionary<string, Shot> _shots = new();

    // Входы идущих и ещё не убранных апскейлов: тикет апскейла → id, по которому строятся имена копий латентов в input.
    // Копии нужны только пока идёт задача: после конца (удача, сбой, отмена) их удаляют сразу, остаток выметает Sweep
    private readonly ConcurrentDictionary<string, UpscaleInputs> _upscales = new();

    // Максимальный процент, показанный для каждого prompt_id: у видео несколько семплеров, и процент
    // ТЕКУЩЕЙ ноды (ComfyStepProgress.Fraction) откатывается к нулю на втором — шкала не должна убывать.
    // Сбрасывается на Complete (конец прогона)
    private readonly ConcurrentDictionary<string, double> _maxPercent = new(StringComparer.Ordinal);

    // Снимок очереди GPU (шаг 26б): общий билдер — логика изоляции/процента/ETA не дублируется по адаптерам
    private readonly ComfyGpuSnapshotBuilder _gpuBuilder = new(monitor, ledger, steps, log);

    private sealed record UpscaleInputs(string Id, string ShotTicket, DateTime At)
    {
        public IEnumerable<string> Names => [UpscaleLatentName(Id, "video"), UpscaleLatentName(Id, "audio")];
    }

    private static string UpscaleLatentName(string id, string kind) => $"{ComfyClient.InputFolder}-{id}-{kind}.latent";

    private sealed class Shot(string id, string prompt, string firstName, string? lastName, (int Width, int Height) size,
        int seconds, long seed, DateTime at)
    {
        public string Id { get; } = id;
        public string Prompt { get; } = prompt;
        public string FirstName { get; } = firstName;
        public string? LastName { get; } = lastName;
        public (int Width, int Height) Size { get; } = size;
        public int Seconds { get; } = seconds;
        public long Seed { get; } = seed;
        public DateTime At { get; } = at;
        public volatile string? LatentVideo;
        public volatile string? LatentAudio;

        // Точные пути mp4-результатов в output ComfyUI (съёмки и её апскейлов) вместе с id задачи, по которому
        // они названы: 100–300 МБ на ролик, убираются тем же Sweep по TTL
        public ConcurrentBag<(string OwnerId, string Path)> Outputs { get; } = [];
    }

    // Имя вида lv_ + 32 строчных hex — только такие id считаются нашими при удалении файлов
    public static bool LooksLikeShotId(string value) =>
        value.StartsWith(IdPrefix, StringComparison.Ordinal) && value.Length == IdPrefix.Length + 32
        && value[IdPrefix.Length..].All(char.IsAsciiHexDigitLower);

    // Выметает контекст съёмок старше ShotTtl и убирает их файлы в ComfyUI: латенты в output, входы (кадры, копии
    // латентов апскейла) в input. Удаляются только имена, построенные из id самой съёмки, по точному имени — через
    // LocalMediaCleanup.DeleteOwn (SafePath, ссылки, каталоги не трогаем); масок нет. Чистка файлов выключена
    // (не заданы каталоги ComfyUI) — выметается одна память. Безопасен при гонках: удаляет тот, кто убрал запись
    public void Sweep() => Sweep(Now);

    internal void Sweep(DateTime now)
    {
        var options = LocalMediaOptions.Read(config);
        foreach (var (ticket, shot) in _shots)
        {
            // Пока апскейл съёмки в полёте, ComfyUI ещё может читать латенты и копии в input: не трогаем ни шот,
            // ни апскейл — дочистит следующий проход после завершения задачи
            if (now - shot.At <= ILocalVideoMedia.ShotTtl || HasUpscaleInFlight(ticket) || !_shots.TryRemove(ticket, out _)) continue;
            PurgeShot(shot, options);
            foreach (var (upTicket, up) in _upscales)
                if (up.ShotTicket == ticket && _upscales.TryRemove(upTicket, out _)) PurgeUpscaleInputs(up, options);
        }
        foreach (var (upTicket, up) in _upscales)
            if (now - up.At > UpscaleAbandonedAfter && _upscales.TryRemove(upTicket, out _))
                PurgeUpscaleInputs(up, options);
    }

    // Запись об увеличении, которое никто не дочитал до конца (процесс оборвался), держит съёмку не дольше этого срока:
    // честное увеличение укладывается в потолок движка, а не в сутки
    private static readonly TimeSpan UpscaleAbandonedAfter = ILocalVideoMedia.ShotTtl * 2;

    // Запись в _upscales живёт до конца увеличения (ReleaseUpscale). Сокет прогресса для этого не годится: он необязателен
    // и тихо закрывается при любом сбое подключения, а увеличение при этом идёт
    private bool HasUpscaleInFlight(string shotTicket) => _upscales.Any(u => u.Value.ShotTicket == shotTicket);

    private void PurgeShot(Shot shot, LocalMediaOptions options)
    {
        if (cleanup is null || !options.CleanupEnabled) return;
        var input = options.ComfyInputDir.Trim();
        var output = options.ComfyOutputDir.Trim();
        foreach (var latent in new[] { shot.LatentVideo, shot.LatentAudio })
            if (latent is not null) cleanup.DeleteOwn(output, latent, shot.Id, LocalMediaCleanup.OutputPrefixes, '_');
        foreach (var frame in new[] { shot.FirstName, shot.LastName })
            if (frame is not null) cleanup.DeleteOwn(input, frame, shot.Id, LocalMediaCleanup.InputPrefixes, '-');
        foreach (var (ownerId, path) in shot.Outputs)
            cleanup.DeleteOwn(output, path, ownerId, LocalMediaCleanup.OutputPrefixes, '_');
    }

    private void PurgeUpscaleInputs(UpscaleInputs up, LocalMediaOptions options)
    {
        if (cleanup is null || !options.CleanupEnabled) return;
        foreach (var name in up.Names)
            cleanup.DeleteOwn(options.ComfyInputDir.Trim(), name, up.Id, LocalMediaCleanup.InputPrefixes, '-');
    }

    // Увеличение кончилось (удача, сбой, отмена): его копии латентов больше не нужны
    private void ReleaseUpscale(string ticket)
    {
        if (_upscales.TryRemove(ticket, out var up)) PurgeUpscaleInputs(up, LocalMediaOptions.Read(config));
    }

    private bool Expired(Shot shot) => Now - shot.At > ILocalVideoMedia.ShotTtl;

    public bool CanUpscale(string ticket) =>
        _shots.TryGetValue(ticket, out var shot) && !Expired(shot) && shot.LatentVideo is not null && shot.LatentAudio is not null;

    public bool Configured => LocalMediaOptions.IsEnabled(config);

    public bool Available => Configured && images.Available;

    public Task<int?> QueueLengthAsync(CancellationToken ct) =>
        Configured ? images.QueueLengthAsync(ct) : Task.FromResult<int?>(null);

    // Время считается по размеру, длине и режиму: кадры и запрос для него не нужны, поэтому оценку можно получить
    // при котировке, до выбора кадров (портрет даёт те же секунды, что пейзаж)
    public int? EtaSeconds(LocalVideoRequest request)
    {
        var shape = Shape(request, LocalMediaOptions.Read(config));
        return shape is null ? null : LocalMediaService.ImageToVideoEta(shape.Value.Size, shape.Value.Seconds, shape.Value.Fast);
    }

    private static ((int Width, int Height) Size, int Seconds, bool Fast)? Shape(LocalVideoRequest request, LocalMediaOptions options)
    {
        var sizeKey = string.IsNullOrWhiteSpace(request.Size) ? "full" : request.Size.Trim();
        if (!ComfyWorkflows.VideoSizes.TryGetValue(sizeKey, out var size)) return null;
        if (request.Seconds < 1 || request.Seconds > options.MaxVideoSeconds) return null;
        return (size, request.Seconds, request.Fast ?? options.VideoFastDefault);
    }

    public async Task<LocalVideoSubmitted> SubmitAsync(LocalVideoRequest request, CancellationToken ct)
    {
        var options = LocalMediaOptions.Read(config);
        if (!options.Enabled) return LocalVideoSubmitted.Fail("Локальные модели выключены на этом сервере.");

        Run plan;
        try
        {
            plan = Plan(request, options);
        }
        catch (LocalMediaInputException ex)
        {
            return LocalVideoSubmitted.Fail(ex.Message);
        }

        var queue = await images.QueueLengthAsync(ct);
        if (queue is null)
            return LocalVideoSubmitted.Fail("ComfyUI не отвечает. Попробуйте позже или выберите другого поставщика.", busy: true);
        if (queue >= options.MaxComfyQueue)
            return LocalVideoSubmitted.Fail($"Очередь локальной видеокарты занята ({queue} задач). "
                + "Попробуйте позже или выберите другого поставщика.", busy: true);

        var id = IdPrefix + Guid.NewGuid().ToString("N");
        var seed = request.Seed is >= 0 ? request.Seed.Value : Random.Shared.NextInt64(1, 1L << 50);
        try
        {
            var firstName = await comfy.UploadImageAsync(plan.First.Bytes, $"{id}-in1{plan.First.Extension}", ct);
            string? lastName = null;
            if (plan.Last is { } last)
                lastName = await comfy.UploadImageAsync(last.Bytes, $"{id}-last{last.Extension}", ct);

            var graph = ComfyWorkflows.ImageToVideo(firstName, lastName, plan.Prompt, plan.Size.Width, plan.Size.Height,
                ComfyWorkflows.FramesFor(plan.Seconds), seed, plan.Fast, $"{ComfyWorkflows.OutputFolder}/{id}",
                $"{ComfyWorkflows.OutputFolder}/latents/{id}");
            var queued = await comfy.QueuePromptAsync(graph, ct);
            // Реестр прогонов (шаг 7): метка происхождения из запроса (шаг 23); null — неопределённое
            ledger?.Register(queued.PromptId, request.Origin, LocalMediaService.ImageToVideoEta(plan.Size, plan.Seconds, plan.Fast));
            Sweep();
            _shots[queued.PromptId] = new Shot(id, plan.Prompt, firstName, lastName, plan.Size, plan.Seconds, seed, Now);
            return new LocalVideoSubmitted(queued.PromptId, queue,
                LocalMediaService.ImageToVideoEta(plan.Size, plan.Seconds, plan.Fast), null);
        }
        catch (ComfyException ex)
        {
            log.LogWarning("ComfyUI не принял видеозадачу: {Error}", ex.Message);
            return LocalVideoSubmitted.Fail(ex.Message, busy: true);
        }
    }

    public async Task<LocalVideoPoll> PollAsync(string ticket, CancellationToken ct)
    {
        // Опрос задачи — панель активна: быстрый опрос очереди (2 с) на время TouchWindow
        monitor?.Touch();
        try
        {
            var history = await comfy.GetHistoryAsync(ticket, ct);
            if (history is null)
            {
                // Позиция для статистики — из снимка монитора (может быть старше отправки);
                // статус задачи решает живой /queue: снимок не знает о промпте, отправленном позже него
                var position = monitor?.Snapshot is { } snap && !snap.Down
                    ? ComfyGpuSnapshotBuilder.PositionInSnapshot(snap, ticket)
                    : null;
                if (position is null)
                    position = (await comfy.GetQueueAsync(ct)).PositionOf(ticket);
                if (position is not null)
                    return new LocalVideoPoll(position == 0 ? LocalVideoState.Running : LocalVideoState.Queued, position, null, null,
                        Percent: position == 0 ? PercentOf(ticket) : null,
                        Gpu: _gpuBuilder.Build(ticket, position));
                // Могла закончиться между двумя запросами — история решает
                history = await comfy.GetHistoryAsync(ticket, ct);
                if (history is null)
                    return Failed("Задача пропала из очереди ComfyUI (перезапуск или отмена на стенде).");
            }
            if (history.Failed) return Failed(history.Error ?? "ComfyUI завершил задачу ошибкой.");
            if (!history.Completed) return new LocalVideoPoll(LocalVideoState.Running, 0, null, null,
                Percent: PercentOf(ticket), Gpu: _gpuBuilder.Build(ticket, 0));
            ledger?.Complete(ticket, "completed", null);
            ForgetPercent(ticket);
            var upscale = _upscales.TryGetValue(ticket, out var upInputs) ? upInputs : null;
            ReleaseUpscale(ticket);
            if (_shots.TryGetValue(ticket, out var shot))
            {
                shot.LatentVideo = LatentPath(history.Latents, "_video_");
                shot.LatentAudio = LatentPath(history.Latents, "_audio_");
            }

            foreach (var file in history.Files)
            {
                var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (LocalMediaService.ContentTypeOf(ext) is not { } type || !type.StartsWith("video/", StringComparison.Ordinal))
                    continue;
                // Звук H3 генерирует сам, CreateVideo склеивает его с кадрами: дорожка есть всегда
                RememberOutput(upscale, ticket, file);
                var bytes = await comfy.DownloadAsync(file, ct);
                return new LocalVideoPoll(LocalVideoState.Completed, null, new LocalVideoFile(bytes, type, ext, HasSound: true), null);
            }
            return Failed("ComfyUI не вернул видео.");
        }
        catch (ComfyException ex)
        {
            // M1: стенд недоступен — снимок несёт downSince (фронт isStandDown видит «стенд недоступен»)
            // Minor 3: позиция из снимка (не хардкод 0): если задачи нет в queue_running снимка,
            // билдер не ставит ложный Running=true (монитор ещё не заметил падение, снимок stale).
            var position = monitor?.Snapshot is { } snap
                ? ComfyGpuSnapshotBuilder.PositionInSnapshot(snap, ticket)
                : null;
            return new LocalVideoPoll(LocalVideoState.Running, null, null, null, ex.Message,
                Gpu: _gpuBuilder.Build(ticket, position));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            log.LogWarning(ex, "Неожиданный ответ ComfyUI по видеозадаче {Ticket}", ticket);
            return new LocalVideoPoll(LocalVideoState.Running, null, null, null, "ComfyUI вернул неожиданный ответ.");
        }

        LocalVideoPoll Failed(string error)
        {
            ledger?.Complete(ticket, "failed", null);
            ForgetPercent(ticket);
            ReleaseUpscale(ticket);
            // Съёмка не удалась — латента нет и увеличивать нечего: контекст и входы убираем сразу
            if (_shots.TryRemove(ticket, out var failedShot)) PurgeShot(failedShot, LocalMediaOptions.Read(config));
            return new(LocalVideoState.Failed, null, null, error);
        }
    }

    // Имя mp4 запоминается точным путём в Shot: свой id у съёмки (её ticket) или у апскейла (по нему назван префикс)
    private void RememberOutput(UpscaleInputs? upscale, string ticket, ComfyOutputFile file)
    {
        var owner = upscale is null ? _shots.GetValueOrDefault(ticket) : _shots.GetValueOrDefault(upscale.ShotTicket);
        if (owner is null) return;
        var path = file.Subfolder.Length == 0 ? file.FileName : $"{file.Subfolder}/{file.FileName}";
        owner.Outputs.Add((upscale?.Id ?? owner.Id, path));
    }

    // Доля шагов текущего сэмплера из общего трекера (ComfyProgressTracker слушает сокет под ClientId):
    // потолок 0.99, «100%» говорит только история. У видео несколько семплеров, и процент ТЕКУЩЕЙ ноды
    // откатывается к нулю на втором — шкала не должна убывать: запоминаем максимум на prompt_id.
    // Сбрасывается на Complete (конец прогона). Нет записи — шагов не знаем, шкала не показывается
    private double? PercentOf(string ticket)
    {
        if (steps?.Get(ticket) is not { } progress) return null;
        var current = Math.Min(0.99, progress.Fraction);
        _maxPercent.AddOrUpdate(ticket, current, (_, prev) => Math.Max(prev, current));
        return _maxPercent[ticket];
    }

    // Конец прогона: запомненный максимум не нужен
    private void ForgetPercent(string ticket) => _maxPercent.TryRemove(ticket, out _);

    private static string? LatentPath(IReadOnlyList<ComfyOutputFile> latents, string marker) =>
        latents.FirstOrDefault(l => l.FileName.Contains(marker, StringComparison.Ordinal)) is { } file
            ? (file.Subfolder.Length == 0 ? file.FileName : $"{file.Subfolder}/{file.FileName}")
            : null;

    public int? UpscaleEtaSeconds(LocalVideoRequest request)
    {
        var options = LocalMediaOptions.Read(config);
        var shape = Shape(request, options);
        return shape is null ? null : LocalMediaService.Upscale1440Eta(ComfyWorkflows.FramesFor(shape.Value.Seconds), options.Upscale1440Tiled);
    }

    // Апскейл 1440p по латенту завершённой съёмки full (стороны портрета меняются местами). Входы копий латентов
    // кладутся в корень input под именем с id апскейла — LoadLatent видит только корень
    public async Task<LocalVideoSubmitted> SubmitUpscaleAsync(string ticket, string target, CancellationToken ct)
    {
        var options = LocalMediaOptions.Read(config);
        if (!options.Enabled) return LocalVideoSubmitted.Fail("Локальные модели выключены на этом сервере.");
        if (!string.Equals(target?.Trim(), "1440p", StringComparison.OrdinalIgnoreCase))
            return LocalVideoSubmitted.Fail("Увеличение — только до 1440p.");
        if (!_shots.TryGetValue(ticket, out var shot) || Expired(shot))
            return LocalVideoSubmitted.Fail("Съёмка больше не доступна для увеличения: сервер перезапускался или прошло много времени.");
        if (shot.LatentVideo is null || shot.LatentAudio is null)
            return LocalVideoSubmitted.Fail("У съёмки нет сохранённого латента — увеличить её нельзя.");
        var full = ComfyWorkflows.VideoSizes["full"];
        var portrait = shot.Size == (full.Height, full.Width);
        if (shot.Size != full && !portrait)
            return LocalVideoSubmitted.Fail("Увеличение — только для съёмки размера full (1344×768).");

        var queue = await images.QueueLengthAsync(ct);
        if (queue is null)
            return LocalVideoSubmitted.Fail("ComfyUI не отвечает. Попробуйте позже.", busy: true);
        if (queue >= options.MaxComfyQueue)
            return LocalVideoSubmitted.Fail($"Очередь локальной видеокарты занята ({queue} задач).", busy: true);

        var id = IdPrefix + Guid.NewGuid().ToString("N");
        var size = ComfyWorkflows.Upscale1440;
        if (portrait) size = (size.Height, size.Width);
        var inputs = new UpscaleInputs(id, ticket, Now);
        try
        {
            var latentVideo = await CopyLatentAsync(shot.LatentVideo, UpscaleLatentName(id, "video"), ct);
            var latentAudio = await CopyLatentAsync(shot.LatentAudio, UpscaleLatentName(id, "audio"), ct);
            var frames = ComfyWorkflows.FramesFor(shot.Seconds);
            var graph = ComfyWorkflows.UpscaleVideo(latentVideo, latentAudio, shot.FirstName, shot.Prompt, size.Width, size.Height,
                frames, shot.Seed, options.Upscale1440Tiled, $"{ComfyWorkflows.OutputFolder}/{id}");
            var queued = await comfy.QueuePromptAsync(graph, ct);
            // Реестр прогонов (шаг 7): метка происхождения придёт в шаге 23, сейчас origin = null
            ledger?.Register(queued.PromptId, null, LocalMediaService.Upscale1440Eta(frames, options.Upscale1440Tiled));
            _upscales[queued.PromptId] = inputs;
            return new LocalVideoSubmitted(queued.PromptId, queue,
                LocalMediaService.Upscale1440Eta(frames, options.Upscale1440Tiled), null);
        }
        catch (ComfyException ex)
        {
            PurgeUpscaleInputs(inputs, options);
            log.LogWarning("ComfyUI не принял апскейл видео: {Error}", ex.Message);
            return LocalVideoSubmitted.Fail(ex.Message, busy: true);
        }
    }

    private async Task<string> CopyLatentAsync(string outputPath, string inputName, CancellationToken ct)
    {
        var slash = outputPath.LastIndexOf('/');
        var file = new ComfyOutputFile(outputPath[(slash + 1)..], slash < 0 ? "" : outputPath[..slash], "output");
        var bytes = await comfy.DownloadAsync(file, ct);
        return await comfy.UploadInputAsync(bytes, inputName, "", ct);
    }

    public async Task<bool> CancelAsync(string ticket, CancellationToken ct)
    {
        // Съёмку не трогаем: после её конца отмену могут звать по её тикету, а латент ещё нужен увеличению
        try
        {
            var queue = await comfy.GetQueueAsync(ct);
            switch (queue.PositionOf(ticket))
            {
                case null:
                    return false;
                // Идёт — прерываем адресно: иначе граф доигрывает и держит GPU
                case 0:
                    await comfy.InterruptAsync(ticket, ct);
                    ledger?.Complete(ticket, "cancelled", null);
                    ForgetPercent(ticket);
                    return true;
                default:
                    await comfy.DeletePendingAsync(ticket, ct);
                    ledger?.Complete(ticket, "cancelled", null);
                    ForgetPercent(ticket);
                    return true;
            }
        }
        catch (ComfyException)
        {
            return false;
        }
        finally
        {
            ReleaseUpscale(ticket);
        }
    }

    private sealed record Run(string Prompt, LocalMediaService.InputFile First, LocalMediaService.InputFile? Last,
        (int Width, int Height) Size, int Seconds, bool Fast);

    // Проверка запроса и размеры кадра — общие с local_image_to_video: портретный первый кадр даёт
    // портретное видео (стороны переставляются, кадр не режется)
    private static Run Plan(LocalVideoRequest request, LocalMediaOptions options)
    {
        var prompt = (request.Prompt ?? "").Trim();
        if (prompt.Length == 0) throw new LocalMediaInputException("Нужен запрос — что происходит в кадре.");
        if (prompt.Length > ComfyWorkflows.MaxPromptLength)
            throw new LocalMediaInputException($"Запрос длиннее {ComfyWorkflows.MaxPromptLength} символов.");
        if (request.Seconds < 1 || request.Seconds > options.MaxVideoSeconds)
            throw new LocalMediaInputException($"Длительность — от 1 до {options.MaxVideoSeconds} секунд.");
        var sizeKey = string.IsNullOrWhiteSpace(request.Size) ? "full" : request.Size.Trim();
        if (!ComfyWorkflows.VideoSizes.TryGetValue(sizeKey, out var size))
            throw new LocalMediaInputException("size — full (1344×768) или half (864×480).");
        if (request.FirstFrame is not { Length: > 0 })
            throw new LocalMediaInputException("Нужен первый кадр.");

        var first = LocalMediaService.CheckInput(request.FirstFrame, FirstSlot, FirstSlot, LocalMediaService.MediaKind.Image);
        var last = request.LastFrame is { } lastBytes
            ? LocalMediaService.CheckInput(lastBytes, LastSlot, LastSlot, LocalMediaService.MediaKind.Image)
            : null;
        if (ImageDimensions.Read(first.Bytes) is { } dims && dims.Height > dims.Width)
            size = (size.Height, size.Width);
        return new Run(prompt, first, last, size, request.Seconds, request.Fast ?? options.VideoFastDefault);
    }
}
