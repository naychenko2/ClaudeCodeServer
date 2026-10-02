using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.AudioEditor.Engines;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.AudioEditor.Mcp;

// Монтаж без ИИ у агента поверх поставщика «Без ИИ» (DspAudioEngine): тот же путь, что у человека в
// полосе «Звук», инициатор — агент. Схема audio_generate фиксирована, поэтому параметры операции едут
// в params и разбираются здесь; неизвестный ключ — отказ с именем поля и списком допустимых.
// Ревизию нитей агент не передаёт: основа — версия, которую выбрал тулсет по threadId/versionId.
public sealed class AudioAgentEdits(DspAudioEngine dsp) : IAudioAgentEdits
{
    private static readonly IReadOnlyDictionary<AudioOp, string[]> Keys = new Dictionary<AudioOp, string[]>
    {
        [AudioOp.Trim] = ["format"],
        [AudioOp.GainFade] = ["fadeInSeconds", "fadeOutSeconds", "gainDb", "format"],
        [AudioOp.Normalize] = ["targetLufs", "format"],
        [AudioOp.MixStems] = ["stems", "format"],
    };

    public async Task<AudioEditCallResult<AudioAgentEditDto>> ApplyAsync(string ownerId, AudioEditScope scope,
        string sessionId, string threadId, string? versionId, AudioOp op, double? startSec, double? endSec,
        JsonObject? parameters, CancellationToken ct)
    {
        if (!Keys.TryGetValue(op, out var known)) return Invalid($"Операция {op} — не монтаж без ИИ");
        var args = parameters ?? new JsonObject();
        if (args.Select(p => p.Key).FirstOrDefault(k => !known.Contains(k)) is { } unknown)
            return Invalid($"params: у операции {AudioEditJobService.OpName(op)} нет параметра «{unknown}». "
                + $"Допустимые: {string.Join(", ", known)}");
        if (!TryFormat(args, out var format)) return Invalid("params.format — wav, mp3, flac или ogg");

        AudioEditCallResult<AudioDspVersionDto> result;
        if (op == AudioOp.MixStems)
        {
            if (Stems(args) is not { } stems)
                return Invalid("params.stems — список стемов { name, gainDb?, muted? }; имена — из audio_state (vocals, drums…)");
            result = await dsp.MixAsync(ownerId, scope,
                new AudioMixInput(sessionId, threadId, stems, versionId, format, Initiator: AudioEditInitiator.Agent), ct);
        }
        else
        {
            if (op == AudioOp.Trim && startSec is null && endSec is null)
                return Invalid("Обрезка: укажи range { start, end } — кусок, который остаётся");
            if (!TryNum(args, "fadeInSeconds", out var fadeIn) || !TryNum(args, "fadeOutSeconds", out var fadeOut)
                || !TryNum(args, "gainDb", out var gain) || !TryNum(args, "targetLufs", out var lufs))
                return Invalid("params: числа — fadeInSeconds, fadeOutSeconds, gainDb, targetLufs");
            var input = new AudioDspEditInput(sessionId, threadId, op switch
                {
                    AudioOp.Trim => AudioDspEditOp.Trim,
                    AudioOp.GainFade => AudioDspEditOp.GainFade,
                    _ => AudioDspEditOp.Normalize,
                }, versionId,
                StartSec: op == AudioOp.Trim ? startSec : null,
                EndSec: op == AudioOp.Trim ? endSec : null,
                FadeInSec: fadeIn ?? 0,
                FadeOutSec: fadeOut ?? 0,
                GainDb: gain ?? 0,
                TargetLufs: lufs,
                Format: format,
                Initiator: AudioEditInitiator.Agent);
            result = await dsp.EditAsync(ownerId, scope, input, ct);
        }

        return result.Value is { } v
            ? AudioEditCallResult<AudioAgentEditDto>.Ok(new AudioAgentEditDto(v.ThreadId, v.VersionId))
            : AudioEditCallResult<AudioAgentEditDto>.Fail(result.ErrorCode ?? AudioEditErrorCodes.InvalidRequest,
                result.Error ?? "Монтаж не получился");
    }

    private static List<AudioMixStemInput>? Stems(JsonObject args)
    {
        if (args["stems"] is not JsonArray array || array.Count == 0) return null;
        var stems = new List<AudioMixStemInput>();
        foreach (var item in array)
        {
            // Стем — строкой («vocals») или объектом { name, gainDb?, muted? }
            string? name = null;
            double? gain = null;
            var muted = false;
            if (item is JsonValue v) v.TryGetValue(out name);
            else if (item is JsonObject o)
            {
                (o["name"] as JsonValue)?.TryGetValue(out name);
                if (!TryNum(o, "gainDb", out gain)) return null;
                if (o["muted"] is { } m && !(m is JsonValue mv && mv.TryGetValue(out muted))) return null;
            }
            if (string.IsNullOrWhiteSpace(name)) return null;
            var role = name.StartsWith(AudioFileRoles.StemPrefix, StringComparison.Ordinal) ? name : AudioFileRoles.Stem(name.Trim());
            stems.Add(new AudioMixStemInput(role, gain ?? 0, muted));
        }
        return stems;
    }

    private static bool TryNum(JsonObject args, string key, out double? value)
    {
        value = AudioEditorToolset.Num(args, key);
        return args[key] is null || value is { } d && double.IsFinite(d);
    }

    private static bool TryFormat(JsonObject args, out AudioFormat? format)
    {
        format = null;
        if (args["format"] is null) return true;
        if (args["format"] is JsonValue v && v.TryGetValue<string>(out var s)
            && Enum.TryParse<AudioFormat>(s, ignoreCase: true, out var f) && Enum.IsDefined(f) && !int.TryParse(s, out _))
        {
            format = f;
            return true;
        }
        return false;
    }

    private static AudioEditCallResult<AudioAgentEditDto> Invalid(string error) =>
        AudioEditCallResult<AudioAgentEditDto>.Fail(AudioEditErrorCodes.InvalidRequest, error);
}
