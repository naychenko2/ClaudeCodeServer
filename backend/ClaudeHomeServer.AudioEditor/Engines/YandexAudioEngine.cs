using System.Globalization;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.AudioEditor.Engines;

// Поставщик «Яндекс SpeechKit» для звука (ADR-021 §2) поверх Core-шва ITtsEngine: вертикаль Tts модулю
// не видна. Только озвучка текста готовым диктором: голос, амплуа и скорость — из Params (voice, role,
// speed), список голосов и амплуа, предел текста и скоростей — из шва. Траты — в рублях, источник в
// учёте — tts, как у голосового режима чата. Нет шва (подсистема tts выключена) или ключа — Enabled=false.
//
// Кривой вход — отказ ДО запроса к Яндексу (и уже в котировке), без оплаты: человек выбрал голос явно, и
// подменить его молча значит отдать не то. Пустой голос — первый диктор МОДЕЛИ запроса.
//
// Две модели — дикторы general и живые дикторы LiveTTS: у них разная цена и разные параметры (у LiveTTS
// нет темпа), а «Чем» в строке контекста обязано показывать, за что человек платит. LiveTTS есть, только
// если шов отдаёт её голоса: тумблер модели читает вертикаль Tts, сюда он не протаскивается.
public sealed class YandexAudioEngine(ITtsEngine? tts) : IAudioEngine, IAudioQuoter
{
    public const string ProviderKey = "yandex";
    public const string ModelId = "yandex-speechkit";
    public const string LiveTtsModelId = "yandex-livetts";

    public string Key => ProviderKey;
    public string Label => "Яндекс SpeechKit";
    public string PriceUnit => AudioPriceUnits.Rub;
    public bool Enabled => tts?.Configured == true;
    public bool Registered => tts is not null;
    public string SpendSource => SpendSources.Tts;

    // yandex-speechkit — ПЕРВЫМ: «Авто» и повтор котировки у соседа берут первую подходящую модель
    // поставщика, и дорогая LiveTTS не должна стать умолчанием
    public IReadOnlyList<AudioModelInfo> Models => tts is null
        ? []
        : tts.Voices.Any(v => v.Model == TtsEngineVoice.LiveTts)
            ? [Model(ModelId, "SpeechKit · дикторы Яндекса"), Model(LiveTtsModelId, "SpeechKit LiveTTS · живые дикторы")]
            : [Model(ModelId, "SpeechKit · дикторы Яндекса")];

    private AudioModelInfo Model(string id, string label) =>
        new(id, label, new AudioCaps([AudioOp.Speak], ["ru"], [AudioVoiceKind.Preset], [AudioOutputs.Audio],
            AudioLicenses.NotStated, AudioPriceUnits.Rub, MaxTextChars: tts!.MaxChars));

    // Модель синтеза шва по модели поставщика; чужая модель — null
    private static string? VoiceModel(string? modelId) =>
        string.Equals(modelId, ModelId, StringComparison.OrdinalIgnoreCase) ? TtsEngineVoice.General
        : string.Equals(modelId, LiveTtsModelId, StringComparison.OrdinalIgnoreCase) ? TtsEngineVoice.LiveTts
        : null;

    // ── Котировка ────────────────────────────────────────────────────────────────

    public int? ExpectedSeconds(AudioModelInfo model, AudioRequest request) => null;

    // Прайс за запрос знает только вертикаль Tts, шов его не отдаёт: сумма станет известна после
    // синтеза. Здесь — проверка входа, чтобы кривой голос отказал ещё до запуска
    public Task<AudioEstimate> EstimateAsync(AudioModelInfo model, AudioRequest request, CancellationToken ct)
    {
        if (tts is null || !tts.Configured) throw new AudioEngineUnavailableException("Синтез Яндекса не настроен на этом сервере");
        if (Validate(request, requireText: false).Error is { } error) throw new AudioEngineUnavailableException(error);
        return Task.FromResult(new AudioEstimate(null, AudioPriceUnits.Rub, true, AudioEstimateSources.Unknown));
    }

    // ── Запуск ───────────────────────────────────────────────────────────────────

    public async Task<AudioResult> RunAsync(AudioRequest req, IProgress<AudioProgress> progress, CancellationToken ct)
    {
        if (tts is null || !tts.Configured) return AudioResult.Fail(AudioOutcome.Unavailable, "Синтез Яндекса не настроен на этом сервере");
        if (req.Op != AudioOp.Speak || VoiceModel(req.Model) is null)
            return AudioResult.Fail(AudioOutcome.Rejected, "Яндекс умеет только озвучку текста");
        var (choice, error) = Validate(req, requireText: true);
        if (choice is not { } c) return AudioResult.Fail(AudioOutcome.Rejected, error!);

        progress.Report(new AudioProgress(AudioStage.Running));
        var result = await tts.SynthesizeAsync(c.Text, c.Voice, c.Role, c.Speed, ct);
        var cost = new AudioCost(result.Rub, AudioPriceUnits.Rub);
        if (result.Audio is null)
            // Принятые Яндексом запросы оплачены и при отказе: рубли едут в трату
            return new AudioResult(AudioOutcome.Failed, [], result.Rub > 0 ? cost : null, result.Rub > 0, null,
                result.Error ?? "Яндекс не озвучил текст");
        return new AudioResult(AudioOutcome.Ok,
            [new AudioFile(AudioOutputs.Audio, result.Audio, TtsSynthesis.ContentType, TtsSynthesis.Extension)],
            cost, true, null, null);
    }

    public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);

    // Клонов у Яндекса нет: голос из библиотеки здесь недоступен, в каталоге — серым с этой причиной
    public const string LibraryVoicesReason = "Яндекс не умеет клонировать голос — голос из библиотеки озвучит другой поставщик";

    public string? LibraryVoicesRefusal => LibraryVoicesReason;

    // ── Параметры и дикторы ──────────────────────────────────────────────────────

    private static readonly IReadOnlySet<string> Params = new HashSet<string>(StringComparer.Ordinal) { "voice", "role", "speed" };

    // У LiveTTS темпа нет: лишний speed отвергается общим правилом params с именем поля, до денег
    private static readonly IReadOnlySet<string> LiveTtsParams = new HashSet<string>(StringComparer.Ordinal) { "voice", "role" };

    public IReadOnlySet<string>? ParamNames(AudioModelInfo model, AudioOp op) =>
        op != AudioOp.Speak ? null
        : VoiceModel(model.Id) == TtsEngineVoice.LiveTts ? LiveTtsParams
        : Params;

    public JsonObject? VoiceParams(AudioModelInfo model, AudioOp op, string voice) =>
        op == AudioOp.Speak ? new JsonObject { ["voice"] = voice } : null;

    // С моделью — только её дикторы. Без модели (audio_voices без model) — все, но у голосов LiveTTS
    // модель в подписи: иначе агент подаст голос LiveTTS в модель general и получит отказ
    public Task<IReadOnlyList<AudioVoiceInfo>?> ListVoicesAsync(string? model, string? language, CancellationToken ct)
    {
        if (tts is null || (language is not null && language != "ru"))
            return Task.FromResult<IReadOnlyList<AudioVoiceInfo>?>(null);
        var voiceModel = string.IsNullOrWhiteSpace(model) ? null : VoiceModel(model.Trim());
        IReadOnlyList<AudioVoiceInfo> voices = voiceModel is not null
            ? [.. tts.Voices.Where(v => v.Model == voiceModel).Select(v => new AudioVoiceInfo(v.Voice, v.Label, "ru", Roles: v.Roles))]
            : [.. tts.Voices.Select(v => new AudioVoiceInfo(v.Voice,
                v.Model == TtsEngineVoice.LiveTts ? $"{v.Label} · LiveTTS" : v.Label, "ru", Roles: v.Roles))];
        return Task.FromResult<IReadOnlyList<AudioVoiceInfo>?>(voices);
    }

    private sealed record Choice(string Text, string Voice, string? Role, double Speed);

    private (Choice? Choice, string? Error) Validate(AudioRequest req, bool requireText)
    {
        var text = req.Text ?? req.Prompt ?? "";
        if (requireText && string.IsNullOrWhiteSpace(text)) return (null, "Нужен текст для озвучки");
        if (text.Length > tts!.MaxChars) return (null, $"Текст длиннее {tts.MaxChars} символов ({text.Length})");

        // Голос — только из модели запроса, и умолчание — первый голос ЕЁ, а не всего шва: иначе в
        // general подставился бы голос LiveTTS (или наоборот) и ушёл бы в запрос не той моделью
        var voiceModel = VoiceModel(req.Model) ?? TtsEngineVoice.General;
        var modelVoices = tts.Voices.Where(v => v.Model == voiceModel).ToList();
        var fields = req.Params ?? new JsonObject();
        var wanted = fields["voice"]?.ToString();
        var voice = string.IsNullOrWhiteSpace(wanted)
            ? modelVoices.FirstOrDefault()
            : modelVoices.FirstOrDefault(v => string.Equals(v.Voice, wanted.Trim(), StringComparison.OrdinalIgnoreCase));
        if (voice is null && !string.IsNullOrWhiteSpace(wanted)
            && tts.Voices.FirstOrDefault(v => string.Equals(v.Voice, wanted.Trim(), StringComparison.OrdinalIgnoreCase)) is { } foreign)
            return (null, foreign.Model == TtsEngineVoice.LiveTts
                ? $"Голос «{ShortName(foreign)}» — из LiveTTS, выбери модель «SpeechKit LiveTTS»"
                : $"Голос «{ShortName(foreign)}» — из обычных дикторов, выбери модель «SpeechKit»");
        if (voice is null) return (null, $"У Яндекса нет голоса «{wanted}»");

        var role = fields["role"]?.ToString() is { Length: > 0 } r ? r.Trim() : null;
        if (role is not null && !voice.Roles.Contains(role, StringComparer.OrdinalIgnoreCase))
            return (null, $"Голос «{voice.Label}» не умеет амплуа «{role}»");

        var speed = 1.0;
        if (fields["speed"] is { } s)
        {
            if (s is not JsonValue v || !double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out speed))
                return (null, "Скорость — число");
        }
        if (voice.SpeedSupported && (double.IsNaN(speed) || speed < tts.MinSpeed || speed > tts.MaxSpeed))
            return (null, $"Скорость {speed.ToString(CultureInfo.InvariantCulture)} вне пределов " +
                          $"{tts.MinSpeed.ToString(CultureInfo.InvariantCulture)}–{tts.MaxSpeed.ToString(CultureInfo.InvariantCulture)}");
        return (new Choice(text, voice.Voice, role, speed), null);
    }

    // «София · живая, разговорная» → «София»: в тексте отказа нужно имя, а не характеристика
    private static string ShortName(TtsEngineVoice voice) =>
        voice.Label.Split(" · ", 2)[0];
}
