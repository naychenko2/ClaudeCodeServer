using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Адаптер шва ILocalAudioMedia для модуля «Звук» (ADR-021, §2). Прямо на ComfyClient, а не через
// LocalMediaService: тот пишет результат в .cc-attachments проекта, а модуль кладёт файлы в свою
// рабочую папку. Разбор аргументов, белые списки, пределы, граф и ETA — общие с инструментами
// local-media (LocalMediaService.BuildAudioGraphAsync), здесь только источник входов — байты
// запроса. Живость ComfyUI общая с картинками: её кеширует адаптер ILocalImageMedia.
public sealed class LocalAudioMediaAdapter(
    ComfyClient comfy,
    IConfiguration config,
    ILocalImageMedia images,
    ILogger<LocalAudioMediaAdapter> log) : ILocalAudioMedia
{
    private const string IdPrefix = "la_";

    // Ссылки на входы в аргументах сборки — они же подписи входов в отказах
    private const string AudioSlot = "исходный звук";
    private const string ReferenceSlot = "образец";
    private const string VoiceModelSlot = "модель голоса";
    private const string VoiceIndexSlot = "индекс голоса";
    private static string ClipSlot(int k) => $"запись {k + 1}";

    public bool Configured => IsAudioEnabled(LocalMediaOptions.Read(config));

    public bool Available => Configured && images.Available;

    private static bool IsAudioEnabled(LocalMediaOptions options) => options.Enabled && options.AudioEnabled;

    public Task<int?> QueueLengthAsync(CancellationToken ct) =>
        Configured ? images.QueueLengthAsync(ct) : Task.FromResult<int?>(null);

    // Сухая сборка: те же проверки и формулы, что у запуска, но без загрузок в ComfyUI
    public int? EtaSeconds(LocalAudioRequest request)
    {
        var options = LocalMediaOptions.Read(config);
        if (OpName(request.Op) is not { } op) return null;
        var build = LocalMediaService.BuildAudioGraphAsync(op, Args(request), new LocalMediaService.AudioBuildInfo(NewId()),
            new BytesInputs(request, null), (request.Prompt ?? "").Trim(), 1, "eta", options.MaxAudioInputSeconds,
            CancellationToken.None);
        return build.IsCompletedSuccessfully ? build.Result.EtaSeconds : null;
    }

    public async Task<LocalAudioSubmitted> SubmitAsync(LocalAudioRequest request, CancellationToken ct)
    {
        var options = LocalMediaOptions.Read(config);
        if (!options.Enabled) return LocalAudioSubmitted.Fail("Локальные модели выключены на этом сервере.");
        if (!options.AudioEnabled) return LocalAudioSubmitted.Fail("Локальные аудиомодели на этом сервере не установлены.");
        if (OpName(request.Op) is not { } op) return LocalAudioSubmitted.Fail("Неизвестная операция.");
        var prompt = (request.Prompt ?? "").Trim();
        if (prompt.Length > ComfyWorkflows.MaxPromptLength)
            return LocalAudioSubmitted.Fail($"Запрос длиннее {ComfyWorkflows.MaxPromptLength} символов.");

        var queue = await images.QueueLengthAsync(ct);
        if (queue is null)
            return LocalAudioSubmitted.Fail("ComfyUI не отвечает. Попробуйте позже или выберите другого поставщика.", busy: true);
        if (queue >= options.MaxComfyQueue)
            return LocalAudioSubmitted.Fail($"Очередь локальной видеокарты занята ({queue} задач). "
                + "Попробуйте позже или выберите другого поставщика.", busy: true);

        var id = NewId();
        var seed = request.Seed is >= 0 ? request.Seed.Value : Random.Shared.NextInt64(1, 1L << 50);
        try
        {
            var (graph, eta) = await LocalMediaService.BuildAudioGraphAsync(op, Args(request),
                new LocalMediaService.AudioBuildInfo(id), new BytesInputs(request, comfy), prompt, seed,
                $"{ComfyWorkflows.OutputFolder}/{id}", options.MaxAudioInputSeconds, ct);
            var queued = await comfy.QueuePromptAsync(graph, ct);
            return new LocalAudioSubmitted(queued.PromptId, queue, eta, null);
        }
        catch (LocalMediaInputException ex)
        {
            return LocalAudioSubmitted.Fail(ex.Message);
        }
        catch (ComfyException ex)
        {
            log.LogWarning("ComfyUI не принял аудио-задачу {Op}: {Error}", op, ex.Message);
            return LocalAudioSubmitted.Fail(ex.Message, busy: true);
        }
    }

    public async Task<LocalAudioPoll> PollAsync(string ticket, CancellationToken ct)
    {
        try
        {
            var history = await comfy.GetHistoryAsync(ticket, ct);
            if (history is null)
            {
                var position = (await comfy.GetQueueAsync(ct)).PositionOf(ticket);
                if (position is not null)
                    return new LocalAudioPoll(position == 0 ? LocalAudioState.Running : LocalAudioState.Queued, position, [], null);
                // Могла закончиться между двумя запросами — история решает
                history = await comfy.GetHistoryAsync(ticket, ct);
                if (history is null)
                    return Failed("Задача пропала из очереди ComfyUI (перезапуск или отмена на стенде).");
            }
            if (history.Failed) return Failed(history.Error ?? "ComfyUI завершил задачу ошибкой.");
            if (!history.Completed) return new LocalAudioPoll(LocalAudioState.Running, 0, [], null);

            var files = await CollectAsync(history, ct);
            return files.Count == 0
                ? Failed("ComfyUI не вернул файлов результата.")
                : new LocalAudioPoll(LocalAudioState.Completed, null, files, null);
        }
        catch (ComfyException ex)
        {
            return new LocalAudioPoll(LocalAudioState.Running, null, [], null, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            log.LogWarning(ex, "Неожиданный ответ ComfyUI по аудио-задаче {Ticket}", ticket);
            return new LocalAudioPoll(LocalAudioState.Running, null, [], null, "ComfyUI вернул неожиданный ответ.");
        }

        static LocalAudioPoll Failed(string error) => new(LocalAudioState.Failed, null, [], error);
    }

    public async Task<bool> CancelAsync(string ticket, CancellationToken ct)
    {
        try
        {
            var queue = await comfy.GetQueueAsync(ct);
            if (queue.PositionOf(ticket) is not > 0) return false;
            await comfy.DeletePendingAsync(ticket, ct);
            return true;
        }
        catch (ComfyException)
        {
            return false;
        }
    }

    // Роли — по расширению; звук один — main, несколько (стемы) — stem:<хвост имени воркера>
    private async Task<List<LocalAudioFile>> CollectAsync(ComfyHistoryEntry history, CancellationToken ct)
    {
        var wanted = history.Files
            .Select(f => (File: f, Ext: Path.GetExtension(f.FileName).ToLowerInvariant()))
            .Select(f => (f.File, f.Ext, Type: LocalMediaService.ContentTypeOf(f.Ext)))
            .Where(f => f.Type is not null && !f.Type.StartsWith("image/", StringComparison.Ordinal)
                && !f.Type.StartsWith("video/", StringComparison.Ordinal))
            .ToList();
        var sounds = wanted.Count(f => IsSound(f.Ext, f.Type!));

        var files = new List<LocalAudioFile>();
        for (var n = 0; n < wanted.Count; n++)
        {
            var (file, ext, type) = wanted[n];
            var role = RoleOf(ext) ?? (IsSound(ext, type!) && sounds > 1
                ? LocalAudioRoles.Stem(LocalMediaService.OutputSuffix(file.FileName, JobIdOf(file.FileName), n))
                : LocalAudioRoles.Main);
            files.Add(new LocalAudioFile(role, await comfy.DownloadAsync(file, ct), type!, ext));
        }
        // Партитура YuE2 приходит текстом из истории (PreviewAny)
        if (history.Texts.Count > 0)
            files.Add(new LocalAudioFile(LocalAudioRoles.Score, Encoding.UTF8.GetBytes(history.Texts[0]), "text/plain", ".abc"));
        return files;
    }

    private static bool IsSound(string ext, string type) => type.StartsWith("audio/", StringComparison.Ordinal) && ext != ".mid";

    private static string? RoleOf(string ext) => ext switch
    {
        ".abc" => LocalAudioRoles.Score,
        ".srt" => LocalAudioRoles.Subtitles,
        ".lrc" => LocalAudioRoles.Lyrics,
        ".txt" => LocalAudioRoles.Text,
        ".mid" => LocalAudioRoles.Midi,
        ".pth" => LocalAudioRoles.Model,
        ".index" => LocalAudioRoles.Index,
        _ => null,
    };

    // Имя выхода воркера — {id}_{хвост}: id адаптера восстанавливается из самого имени
    private static string JobIdOf(string fileName)
    {
        var length = IdPrefix.Length + 32;
        return fileName.Length > length && fileName.StartsWith(IdPrefix, StringComparison.Ordinal) ? fileName[..length] : "";
    }

    private static string NewId() => IdPrefix + Guid.NewGuid().ToString("N");

    internal static string? OpName(LocalAudioOp op) => op switch
    {
        LocalAudioOp.Speech => LocalMediaOps.Speech,
        LocalAudioOp.MusicGenerate => LocalMediaOps.MusicGenerate,
        LocalAudioOp.MusicEdit => LocalMediaOps.MusicEdit,
        LocalAudioOp.VoiceConvert => LocalMediaOps.VoiceConvert,
        LocalAudioOp.VoiceTrain => LocalMediaOps.VoiceTrain,
        LocalAudioOp.Separate => LocalMediaOps.AudioSeparate,
        LocalAudioOp.ToMidi => LocalMediaOps.AudioToMidi,
        LocalAudioOp.Enhance => LocalMediaOps.AudioEnhance,
        LocalAudioOp.Transcribe => LocalMediaOps.Transcribe,
        _ => null,
    };

    // Аргументы сборки: ссылки на входы ставим сами по переданным байтам — пришедшие в Args
    // выбрасываются, путём файла сюда ничего не попадёт
    private static JsonObject Args(LocalAudioRequest request)
    {
        var a = request.Args?.DeepClone().AsObject() ?? new JsonObject();
        foreach (var key in new[] { "audio", "reference", "audios", "voice_model", "voice_index" }) a.Remove(key);
        if (request.Audio is not null) a["audio"] = AudioSlot;
        if (request.Reference is not null) a["reference"] = ReferenceSlot;
        if (request.Clips is { Count: > 0 } clips)
            a["audios"] = new JsonArray([.. clips.Select((_, k) => (JsonNode)ClipSlot(k))]);
        if (request.VoiceModel is not null) a["voice_model"] = VoiceModelSlot;
        if (request.VoiceIndex is not null) a["voice_index"] = VoiceIndexSlot;
        return a;
    }

    // Входы — байты запроса. comfy == null — сухая сборка для ETA: загрузки нет
    private sealed class BytesInputs(LocalAudioRequest request, ComfyClient? comfy) : LocalMediaService.IAudioInputs
    {
        public LocalMediaService.InputFile Read(string reference, LocalMediaService.MediaKind kind)
        {
            var (bytes, name) = reference switch
            {
                AudioSlot => (request.Audio, reference),
                ReferenceSlot => (request.Reference, reference),
                VoiceModelSlot => (request.VoiceModel, "voice.pth"),
                VoiceIndexSlot => (request.VoiceIndex, "voice.index"),
                _ => (request.Clips?.Select((c, k) => (Clip: c, Slot: ClipSlot(k))).FirstOrDefault(c => c.Slot == reference).Clip,
                    reference),
            };
            if (bytes is null) throw new LocalMediaInputException($"Нет входа «{reference}».");
            return LocalMediaService.CheckInput(bytes, name, reference, kind);
        }

        public async Task<string> UploadAsync(byte[] bytes, string fileName, CancellationToken ct) =>
            comfy is null ? fileName : await comfy.UploadImageAsync(bytes, fileName, ct);
    }
}
