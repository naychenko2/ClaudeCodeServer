using System.Text.Json;
using System.Threading.Channels;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.ImageEditor;

// Фоновая разметка ракурса и эмоции фото персонажа (ADR-027 §9). Очередь на инстанс: один исполнитель,
// персонажи строго по одному, дедупликация по (корень, slug). Ставят её ленивые поводы (загрузка фото,
// чтение списка и карточки, запуск с персонажем), обхода диска при старте нет. Флаг владельца
// выключен — ни одного вызова. Нет маршрута (модель разметки не настроена) или ComfyUI лежит — фото
// получает noroute (повторим, когда разметка станет возможна); модель ответила, но не определила —
// auto+unknown (повторно не размечаем).
public sealed class CharacterPhotoLabeling(
    CharacterLoraRuns locks,
    ILogger<CharacterPhotoLabeling> log,
    IImageLabeler? labeler = null,
    IFeatureFlagGate? flags = null,
    ILocalImageMedia? localMedia = null,
    IImageRaster? raster = null) : BackgroundService
{
    // Пачка на место за один заход
    public const int BatchSize = 10;
    // Длинная сторона фото перед отправкой в облако (ADR-027 §4)
    public const int CloudMaxSide = 512;
    // Столько неудач модели подряд по месту фото — и оно получает unknown, чтобы не уходить в облако бесконечно
    public const int MaxFailures = 3;

    private readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>();
    private readonly HashSet<(string Root, string Slug)> _pending = [];
    // Неудачи Failed по (владелец, корень, slug, файл, место) — только в памяти: после рестарта счёт с нуля
    private readonly Dictionary<(string Owner, string Root, string Slug, string File, string Place), int> _failures = [];

    public sealed record Job(string OwnerId, string Root, string Slug);

    // Нет шва разметки (подсистема Llm выключена) или флага — работы нет
    private bool Enabled(string ownerId) =>
        labeler is not null && flags?.IsEnabled(ownerId, FeatureFlagKeys.CharacterSmartPhotos) == true;

    // Поводы зовут это не глядя: флаг выключен или персонаж уже в очереди — тихо ничего
    public void Enqueue(string ownerId, string root, string slug)
    {
        if (!Enabled(ownerId)) return;
        lock (_pending)
        {
            if (!_pending.Add((root, slug))) return;
        }
        _queue.Writer.TryWrite(new Job(ownerId, root, slug));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(ct))
        {
            // Снимаем с дедупликации до работы: повод, пришедший во время разметки, поставит новый заход
            lock (_pending) _pending.Remove((job.Root, job.Slug));
            try { await ProcessAsync(job, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogWarning(ex, "Разметка фото персонажа {Slug} упала", job.Slug); }
        }
    }

    public async Task ProcessAsync(Job job, CancellationToken ct)
    {
        if (!Enabled(job.OwnerId)) return;

        var manifest = CharacterStore.Get(job.Root, job.Slug);
        if (manifest is null) return;
        PruneFailures(job, manifest);

        var angleFiles = Candidates(manifest, p => p.Angle is null);
        if (angleFiles.Count > 0)
        {
            var result = await LabelAngleAsync(job, angleFiles, ct);
            if (result is not null) await WriteAsync(job, result.Labels, result.Source, ct);
        }

        var emotionFiles = Candidates(manifest, p => p.Emotion is null);
        if (emotionFiles.Count > 0)
        {
            var result = await LabelEmotionAsync(job, emotionFiles, ct);
            if (result is not null) await WriteAsync(job, result.Labels, result.Source, ct);
        }
    }

    // Размеченное место и human не трогаем; unknown — это «пробовали», он тоже не null.
    // noroute — «разметить было нечем»: берём снова, чтобы фото размечалось, когда маршрут появится.
    // Повторы конечны: Candidates зовут только ленивые поводы, а не маршрут даёт noroute мгновенно, без модели.
    private static List<string> Candidates(CharacterManifest manifest, Func<CharacterPhoto, bool> needs) =>
        [.. manifest.Photos
            .Where(p => p.LabelSource != PhotoLabelValues.Human
                && (needs(p) || p.LabelSource == PhotoLabelValues.NoRoute))
            .Take(BatchSize)
            .Select(p => p.File)];

    // Итог разметки места: метки по файлам и источник записи (Auto — модель ответила,
    // NoRoute — разметить было нечем). null — писать нечего (временный отказ, до трёх попыток)
    private sealed record LabelingResult(Dictionary<string, CharacterPhotoLabels> Labels, string Source);

    // «Нет маршрута»: unknown для каждого фото, без счётчика Failed и трёх попыток;
    // источник — NoRoute, чтобы фото размечалось, когда маршрут появится
    private static LabelingResult NoRouteResult(
        List<string> files, CharacterPhotoLabels unknown, string labelSource)
    {
        var labels = new Dictionary<string, CharacterPhotoLabels>();
        foreach (var file in files) labels[file] = unknown;
        return new LabelingResult(labels, labelSource);
    }

    private async Task<LabelingResult?> LabelAngleAsync(
        Job job, List<string> files, CancellationToken ct)
    {
        var route = labeler!.RouteOf(LocalActionCatalog.CharacterPhotoAngle, job.OwnerId);
        switch (route.Kind)
        {
            case ImageLabelRouteKind.FacePose:
                return await FacePoseAsync(job, files, ct);
            case ImageLabelRouteKind.Vision:
                var ask = await AskAsync(job, LocalActionCatalog.CharacterPhotoAngle, AnglePrompt, files,
                    new CharacterPhotoLabels(PhotoLabelValues.Unknown, PhotoLabelValues.Unknown), ct);
                if (ask is null) return null;
                return MapVision(files, ask.Value.Answers, e => new CharacterPhotoLabels(
                    Pick(e, "angle", PhotoLabelValues.Angles), Pick(e, "pitch", PhotoLabelValues.Pitches)),
                    new CharacterPhotoLabels(PhotoLabelValues.Unknown, PhotoLabelValues.Unknown), ask.Value.Source);
            default:
                // Маршрута нет (модель разметки не настроена): сразу unknown, без трёх попыток
                return NoRouteResult(files, new CharacterPhotoLabels(PhotoLabelValues.Unknown, PhotoLabelValues.Unknown),
                    PhotoLabelValues.NoRoute);
        }
    }

    private async Task<LabelingResult?> FacePoseAsync(
        Job job, List<string> files, CancellationToken ct)
    {
        var unknown = new CharacterPhotoLabels(PhotoLabelValues.Unknown, PhotoLabelValues.Unknown);
        // Подсистемы local-media нет или тумблер выключен: разметить некем — сразу unknown,
        // без трёх попыток (не вернётся без настройки)
        if (localMedia is null || !localMedia.Configured)
            return NoRouteResult(files, unknown, PhotoLabelValues.NoRoute);
        // ComfyUI настроен, но сейчас лежит: временный сбой — до трёх попыток
        if (!localMedia.Available)
        {
            await RegisterFailureAsync(job, LocalActionCatalog.CharacterPhotoAngle, files, unknown, ct);
            return null;
        }
        var bytes = ReadPhotos(job, files);
        if (bytes.Count == 0) return null;
        var result = await localMedia.FacePoseAsync([.. bytes.Select(b => b.Bytes)], ct);
        // Busy, ошибка узла, ответ не по числу входов — «позже»: временный сбой, до трёх попыток
        if (result.Poses is null || result.Poses.Count != bytes.Count)
        {
            await RegisterFailureAsync(job, LocalActionCatalog.CharacterPhotoAngle, files, unknown, ct);
            return null;
        }
        var labels = new Dictionary<string, CharacterPhotoLabels>();
        for (var i = 0; i < bytes.Count; i++)
        {
            var (angle, pitch) = result.Poses[i] is { } pose
                ? FacePoseClassifier.Classify(pose)
                : (PhotoLabelValues.Unknown, PhotoLabelValues.Unknown);
            labels[bytes[i].File] = new CharacterPhotoLabels(angle, pitch);
        }
        return new LabelingResult(labels, PhotoLabelValues.Auto);
    }

    private async Task<LabelingResult?> LabelEmotionAsync(
        Job job, List<string> files, CancellationToken ct)
    {
        var route = labeler!.RouteOf(LocalActionCatalog.CharacterPhotoEmotion, job.OwnerId);
        if (route.Kind != ImageLabelRouteKind.Vision)
            // Маршрута нет (эмоция — только vision): сразу unknown, без трёх попыток
            return NoRouteResult(files, new CharacterPhotoLabels(Emotion: PhotoLabelValues.Unknown),
                PhotoLabelValues.NoRoute);
        var ask = await AskAsync(job, LocalActionCatalog.CharacterPhotoEmotion, EmotionPrompt, files,
            new CharacterPhotoLabels(Emotion: PhotoLabelValues.Unknown), ct);
        if (ask is null) return null;
        return MapVision(files, ask.Value.Answers,
            e => new CharacterPhotoLabels(Emotion: Pick(e, "emotion", PhotoLabelValues.Emotions)),
            new CharacterPhotoLabels(Emotion: PhotoLabelValues.Unknown), ask.Value.Source);
    }

    // null — писать нечего (временный отказ, до трёх попыток); иначе разобранный массив ответов
    // по порядку фото (элементы-не-объекты и недостающие хвосты превращаются в unknown позже, в MapVision)
    // и источник записи: Auto — модель ответила, NoRoute — Failed дошёл до порога (ComfyUI/модель лежит)
    private async Task<(List<JsonElement>? Answers, string Source)?> AskAsync(
        Job job, string actionKey, string prompt, List<string> files, CharacterPhotoLabels unknown, CancellationToken ct)
    {
        if (raster is null) return null;
        var photos = ReadPhotos(job, files);
        if (photos.Count != files.Count) return null;

        var images = new List<ImageLabelInput>();
        foreach (var photo in photos)
        {
            var shrunk = Shrink(photo.Bytes);
            if (shrunk is null) return null;
            images.Add(shrunk);
        }

        var result = await labeler!.LabelAsync(new ImageLabelRequest(actionKey, job.OwnerId, prompt, images), ct);
        switch (result.Failure)
        {
            case null:
                ResetFailures(job, actionKey, files);
                return (ParseArray(result.Text), PhotoLabelValues.Auto);
            case ImageLabelFailure.NoVision or ImageLabelFailure.Refused:
                // Модель ответила, но метки нет: «пробовали», повторно не отправляем
                return ([], PhotoLabelValues.Auto);
            case ImageLabelFailure.Failed:
                // ComfyUI/модель лежит: до трёх попыток, потом noroute (RegisterFailureAsync пишет сам)
                await RegisterFailureAsync(job, actionKey, files, unknown, ct);
                return null;
            default:
                // Busy/Unavailable/Timeout — временная занятость, не ошибка модели: без счёта
                return null;
        }
    }

    // Failed по пачке: каждому фото +1; достигшие MaxFailures получают unknown и источник NoRoute
    // (ComfyUI/модель лежит — повторим, когда поднимется), а не Auto (повторно не размечаем)
    private async Task RegisterFailureAsync(
        Job job, string place, List<string> files, CharacterPhotoLabels unknown, CancellationToken ct)
    {
        var exhausted = new Dictionary<string, CharacterPhotoLabels>();
        lock (_failures)
        {
            foreach (var file in files)
            {
                var key = (job.OwnerId, job.Root, job.Slug, file, place);
                var n = _failures.GetValueOrDefault(key) + 1;
                if (n >= MaxFailures)
                {
                    _failures.Remove(key);
                    exhausted[file] = unknown;
                }
                else _failures[key] = n;
            }
        }
        if (exhausted.Count > 0) await WriteAsync(job, exhausted, PhotoLabelValues.NoRoute, ct);
    }

    // Счётчики фото, которых в манифесте уже нет, выбрасываем: иначе они копятся до рестарта
    private void PruneFailures(Job job, CharacterManifest manifest)
    {
        var files = manifest.Photos.Select(p => p.File).ToHashSet(StringComparer.Ordinal);
        lock (_failures)
            foreach (var key in _failures.Keys
                         .Where(k => k.Owner == job.OwnerId && k.Root == job.Root && k.Slug == job.Slug && !files.Contains(k.File))
                         .ToList())
                _failures.Remove(key);
    }

    internal int FailureCount { get { lock (_failures) return _failures.Count; } }

    private void ResetFailures(Job job, string place, List<string> files)
    {
        lock (_failures)
            foreach (var file in files) _failures.Remove((job.OwnerId, job.Root, job.Slug, file, place));
    }

    private List<(string File, byte[] Bytes)> ReadPhotos(Job job, List<string> files)
    {
        var list = new List<(string, byte[])>();
        foreach (var file in files)
            if (CharacterStore.OpenPhoto(job.Root, job.Slug, file) is { } photo) list.Add((file, photo.Bytes));
        return list;
    }

    // Длинная сторона до CloudMaxSide, всегда перекодирование в JPEG (заодно AutoOrient и без метаданных)
    private ImageLabelInput? Shrink(byte[] bytes)
    {
        var probe = raster!.Probe(bytes);
        if (probe is null) return null;
        var (w, h) = (probe.DisplayWidth, probe.DisplayHeight);
        IReadOnlyList<ImageTransformOp> ops = Math.Max(w, h) <= CloudMaxSide ? []
            : w >= h ? [new ResizeOp(Width: CloudMaxSide)] : [new ResizeOp(Height: CloudMaxSide)];
        var outcome = raster.Apply(bytes, ops, new ImageEncodeSpec(ImageEncodeFormat.Jpeg, 85));
        return outcome.Ok ? new ImageLabelInput(outcome.Image!.Bytes, "image/jpeg") : null;
    }

    private static List<JsonElement>? ParseArray(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        // Модель любит оборачивать JSON в ```json … ```: берём от первой скобки до последней
        var from = text.IndexOf('[');
        var to = text.LastIndexOf(']');
        if (from < 0 || to <= from) return [];
        try
        {
            using var doc = JsonDocument.Parse(text[from..(to + 1)]);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())]
                : [];
        }
        catch (JsonException) { return []; }
    }

    private static LabelingResult MapVision(
        List<string> files, List<JsonElement>? answers,
        Func<JsonElement, CharacterPhotoLabels> map, CharacterPhotoLabels unknown, string source)
    {
        var labels = new Dictionary<string, CharacterPhotoLabels>();
        for (var i = 0; i < files.Count; i++)
            labels[files[i]] = answers is not null && i < answers.Count && answers[i].ValueKind == JsonValueKind.Object
                ? Normalize(map(answers[i]), unknown) : unknown;
        return new LabelingResult(labels, source);
    }

    // Поле, которого модель не дала или дала вне словаря, — unknown: «пробовали»
    private static CharacterPhotoLabels Normalize(CharacterPhotoLabels l, CharacterPhotoLabels unknown) => new(
        unknown.Angle is null ? null : l.Angle ?? unknown.Angle,
        unknown.Pitch is null ? null : l.Pitch ?? unknown.Pitch,
        unknown.Emotion is null ? null : l.Emotion ?? unknown.Emotion);

    private static string? Pick(JsonElement e, string name, IReadOnlySet<string> vocabulary) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && v.GetString()?.Trim().ToLowerInvariant() is { } s && vocabulary.Contains(s) ? s : null;

    private async Task WriteAsync(Job job, Dictionary<string, CharacterPhotoLabels> labels, string labelSource, CancellationToken ct)
    {
        using (await locks.LockCharacterAsync(job.Root, job.Slug, ct))
            CharacterStore.MergeAutoLabels(job.Root, job.Slug, labels, labelSource);
    }

    private const string Rules =
        "Не опознавай человека и не называй, кто это: опиши только то, что видно. " +
        "Ответь ТОЛЬКО JSON-массивом, по одному объекту на каждое фото в порядке следования, без пояснений. ";

    private const string AnglePrompt = Rules +
        "Для каждого фото опиши поворот головы: {\"angle\": \"front|three-quarter|profile|unknown\", " +
        "\"pitch\": \"level|up|down|unknown\"}. front — лицом в камеру, three-quarter — три четверти, " +
        "profile — в профиль; pitch — наклон головы (up — подбородок поднят, down — опущен). " +
        "Нет лица или не видно — unknown.";

    private const string EmotionPrompt = Rules +
        "Для каждого фото опиши выражение лица: {\"emotion\": \"neutral|smile|laugh|serious|sad|surprised|unknown\"}. " +
        "Нет лица или не видно — unknown.";
}
