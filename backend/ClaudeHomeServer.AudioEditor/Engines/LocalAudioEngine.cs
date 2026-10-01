using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.AudioEditor.Engines;

// Поставщик «Локальные модели» для звука (ADR-021 §2) поверх Core-шва ILocalAudioMedia: local-media на
// своей GPU, вертикаль Images модулю не видна. Денег нет — котировка ноль в единицах free плюс ETA по
// таблице замеров и длина общей очереди ComfyUI. Нет шва (Images выключена) или тумблеров — поставщик
// не заведён и Enabled=false, а не исключение.
//
// Только серверный проект: результат local-media — файлы на диске сервера, а в личной области проекта
// нет, у локального проекта (ADR-016) файлы живут на устройстве. Отказ — ScopeRefusal до чтения входов и
// до обращения к шву; локальность — только через ProjectCapabilities.
public sealed class LocalAudioEngine(ILocalAudioMedia? media) : IAudioEngine, IAudioQuoter, IAudioParamSchemas
{
    public const string ProviderKey = "local";

    public const string PersonalScopeReason =
        "Локальные модели работают только в чате проекта: в личном чате им некуда положить файлы. " +
        "Откройте чат проекта или выберите облачного поставщика";

    public const string DeviceProjectReason =
        "Локальные модели работают только с проектом на сервере, а файлы этого проекта лежат на его устройстве. " +
        "Выберите облачного поставщика";

    // Потолок одного запуска с ожиданием очереди: песня MiniMax ≈4,5 мин, обучение RVC — дольше
    private static readonly TimeSpan JobCeiling = TimeSpan.FromMinutes(60);

    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    internal TimeSpan Ceiling { get; set; } = JobCeiling;

    private static readonly IReadOnlyList<AudioModelInfo> LocalModels = [.. AudioCatalog.Local.Select(m => m.Info)];

    public string Key => ProviderKey;
    public string Label => "Локальные модели";
    public string PriceUnit => AudioPriceUnits.Free;
    public bool Enabled => media?.Available == true;
    public bool Registered => media?.Configured == true;
    public IReadOnlyList<AudioModelInfo> Models => LocalModels;

    public string? ScopeRefusal(AudioEditScope scope)
    {
        if (scope.IsPersonal || scope.Project is not { } project) return PersonalScopeReason;
        return ProjectCapabilities.FilesOnServer(project) ? null : DeviceProjectReason;
    }

    // Схема «Дополнительно» — описание движка в каталоге: сети и GPU не нужно
    public Task<AudioSchemaLookup> SchemaAsync(AudioModelInfo model, AudioOp op, CancellationToken ct) =>
        Task.FromResult(AudioCatalog.LocalSchema(model.Id, op) is { } schema
            ? AudioSchemaLookup.Ok(schema)
            : AudioSchemaLookup.Fail("Локальные модели так не умеют"));

    // ── Котировка ────────────────────────────────────────────────────────────────

    public int? ExpectedSeconds(AudioModelInfo model, AudioRequest request) =>
        media is null || ScopeRefusal(request.Scope) is not null || Compose(request) is not { } run
            ? null
            : media.EtaSeconds(run);

    public async Task<AudioEstimate> EstimateAsync(AudioModelInfo model, AudioRequest request, CancellationToken ct)
    {
        if (ScopeRefusal(request.Scope) is { } refusal) throw new AudioEngineUnavailableException(refusal);
        if (media is null || !media.Available) throw new AudioEngineUnavailableException("Локальные модели сейчас недоступны");
        var queue = await media.QueueLengthAsync(ct)
            ?? throw new AudioEngineUnavailableException("Локальная видеокарта не отвечает (ComfyUI недоступен)");
        var eta = Compose(request) is { } run ? media.EtaSeconds(run) : null;
        return new AudioEstimate(0, AudioPriceUnits.Free, false, AudioEstimateSources.Provider, eta, queue);
    }

    // ── Запуск ───────────────────────────────────────────────────────────────────

    public async Task<AudioResult> RunAsync(AudioRequest req, IProgress<AudioProgress> progress, CancellationToken ct)
    {
        if (ScopeRefusal(req.Scope) is { } refusal) return AudioResult.Fail(AudioOutcome.Rejected, refusal);
        if (media is null) return AudioResult.Fail(AudioOutcome.Unavailable, "Локальные модели недоступны на этом сервере");
        if (Compose(req) is not { } run) return AudioResult.Fail(AudioOutcome.Failed, "Локальные модели так не умеют");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Ceiling);
        var token = timeout.Token;

        string? ticket = null;
        try
        {
            var submitted = await media.SubmitAsync(run, token);
            if (submitted.Ticket is null)
                return AudioResult.Fail(submitted.Busy ? AudioOutcome.Unavailable : AudioOutcome.Failed,
                    submitted.Error ?? "Локальная видеокарта не приняла задачу");
            ticket = submitted.Ticket;
            progress.Report(new AudioProgress(AudioStage.Queued, submitted.QueuePosition, submitted.EtaSeconds));

            var done = await WaitAsync(ticket, submitted.EtaSeconds, progress, token);
            ticket = null;
            if (done.State == LocalAudioState.Failed)
                return new AudioResult(AudioOutcome.Failed, [], Free, false, submitted.Ticket,
                    "Локальная модель не справилась: " + (done.Error ?? "ComfyUI завершил задачу ошибкой"));
            progress.Report(new AudioProgress(AudioStage.Downloading));
            List<AudioFile> files = [.. done.Files.Select(f => new AudioFile(f.Role, f.Bytes, f.ContentType, f.Extension))];
            return files.Count == 0
                ? new AudioResult(AudioOutcome.Failed, [], Free, false, submitted.Ticket, "Локальная модель не вернула файлов")
                : new AudioResult(AudioOutcome.Ok, files, Free, false, submitted.Ticket, null);
        }
        catch (OperationCanceledException)
        {
            // Задачу, ещё ждущую в очереди, снимаем: чужое время GPU она не займёт
            if (ticket is not null) await media.CancelAsync(ticket, CancellationToken.None);
            if (ct.IsCancellationRequested) throw;
            return new AudioResult(AudioOutcome.Failed, [], Free, false, ticket,
                $"Локальная видеокарта не успела за {Ceiling.TotalMinutes:0} минут — очередь занята");
        }
    }

    private async Task<LocalAudioPoll> WaitAsync(string ticket, int? eta, IProgress<AudioProgress> progress, CancellationToken ct)
    {
        var stage = AudioStage.Queued;
        int? position = null;
        while (true)
        {
            var poll = await media!.PollAsync(ticket, ct);
            if (poll.State is LocalAudioState.Completed or LocalAudioState.Failed) return poll;
            var next = poll.State == LocalAudioState.Running ? AudioStage.Running : AudioStage.Queued;
            if (poll.Warning is null && (next != stage || poll.QueuePosition != position))
            {
                stage = next;
                position = poll.QueuePosition;
                progress.Report(new AudioProgress(stage, stage == AudioStage.Queued ? position : null, eta));
            }
            await Task.Delay(PollInterval, ct);
        }
    }

    public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) =>
        media is null ? Task.FromResult(false) : media.CancelAsync(remoteId, ct);

    // ── Параметры и дикторы ──────────────────────────────────────────────────────

    public IReadOnlySet<string>? ParamNames(AudioModelInfo model, AudioOp op) =>
        AudioCatalog.FindLocal(model.Id) is { } local && local.Bindings.TryGetValue(op, out var binding)
            ? AudioCatalog.LocalParamNames(binding.Op)
            : null;

    // Готовые дикторы есть только у Qwen3-TTS в озвучке
    public JsonObject? VoiceParams(AudioModelInfo model, AudioOp op, string voice) =>
        HasSpeakers(model.Id, op) ? new JsonObject { ["speaker"] = voice } : null;

    public Task<IReadOnlyList<AudioVoiceInfo>?> ListVoicesAsync(string? model, string? language, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AudioVoiceInfo>?>(model is null || HasSpeakers(model, AudioOp.Speak)
            ? [.. AudioCatalog.QwenSpeakers.Select(s => new AudioVoiceInfo(s, s.Replace('_', ' ')))]
            : null);

    private static bool HasSpeakers(string model, AudioOp op) =>
        op == AudioOp.Speak && string.Equals(model, AudioCatalog.QwenTts, StringComparison.OrdinalIgnoreCase);

    // Запрос шва: привязка модели к операции из каталога, поверх неё — поля запроса под именами
    // инструментов local-media. Фиксированные аргументы привязки (engine, task, mode) Params не
    // перебивают; белые списки и пределы проверяет адаптер шва. null — модель операцию не умеет
    internal static LocalAudioRequest? Compose(AudioRequest req)
    {
        if (AudioCatalog.FindLocal(req.Model) is not { } model || !model.Bindings.TryGetValue(req.Op, out var binding))
            return null;

        var args = req.Params?.DeepClone().AsObject() ?? new JsonObject();
        if (req.Text is not null) args["text"] = req.Text;
        if (req.Lyrics is not null) args["lyrics"] = req.Lyrics;
        if (req.Language is not null) args["language"] = req.Language;
        if (req.DurationSec is { } duration) args["duration_seconds"] = duration;
        if (req.StartSec is { } start) args["start_seconds"] = start;
        if (req.EndSec is { } end) args["end_seconds"] = end;
        foreach (var (key, value) in binding.Args) args[key] = value;

        return new LocalAudioRequest(binding.Op, req.Prompt, args,
            Audio: req.Source?.Bytes,
            Reference: req.Reference?.Bytes,
            Clips: req.Clips?.Select(c => c.Bytes).ToList(),
            VoiceModel: req.VoiceModel,
            VoiceIndex: req.VoiceIndex,
            Seed: req.Seed);
    }

    private static readonly AudioCost Free = new(0, AudioPriceUnits.Free);
}
