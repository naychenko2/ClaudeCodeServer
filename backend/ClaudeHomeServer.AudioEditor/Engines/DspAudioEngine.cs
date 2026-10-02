using System.Globalization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor.Controllers;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.AudioEditor.Engines;

// Правка без ИИ: обрезка, громкость и фейды, нормализация, смена формата и частоты
public enum AudioDspEditOp { Trim, GainFade, Normalize, Convert }

// Правка версии нити. BaseVersionId null — текущая. Поля читаются по операции: Trim — Start/EndSec,
// GainFade — фейды и GainDb, Normalize — TargetLufs (null — −14 LUFS), Convert — SampleRate/Channels.
// Format null — формат исходного файла (WAV/MP3/FLAC/OGG), иначе WAV. Revision — от какой ревизии
// нитей считал человек: устарела — конфликт до работы ffmpeg
public sealed record AudioDspEditInput(
    string SessionId,
    string ThreadId,
    AudioDspEditOp Op,
    string? BaseVersionId = null,
    double? StartSec = null,
    double? EndSec = null,
    double FadeInSec = 0,
    double FadeOutSec = 0,
    double GainDb = 0,
    double? TargetLufs = null,
    AudioFormat? Format = null,
    int? SampleRate = null,
    int? Channels = null,
    long? Revision = null,
    AudioEditInitiator Initiator = AudioEditInitiator.Human);

// Стем сведения: Role — роль файла версии-основы («stem:vocals»)
public sealed record AudioMixStemInput(string Role, double GainDb = 0, bool Muted = false);

// Сведение N из M стемов версии-основы: в итог идут только перечисленные стемы, Muted — молчит
public sealed record AudioMixInput(
    string SessionId,
    string ThreadId,
    IReadOnlyList<AudioMixStemInput> Stems,
    string? BaseVersionId = null,
    AudioFormat? Format = null,
    long? Revision = null,
    AudioEditInitiator Initiator = AudioEditInitiator.Human);

public sealed record AudioDspVersionDto(string ThreadId, string VersionId, int Number, string JobId, AudioThreadsState State);

public sealed record AudioPeaksDto(IReadOnlyList<float> Peaks, double Seconds);

// Поставщик «Без ИИ» (ADR-021 §2, ключ dsp) поверх Core-шва IAudioDsp: ffmpeg на хосте, бесплатно и
// без очереди GPU — без котировки и исполнителя задач, вызывающий ждёт итог. Это не IAudioEngine:
// моделей у него нет, а исполнитель задач операции без ИИ не принимает (AudioOps.NoAi).
//
// Инварианты:
// - каждая правка — НОВАЯ версия нити (у картинок правка без ИИ — шаг, у звука — версия): основа и
//   лицензия — от версии-основы, она же становится текущей;
// - файл версии лежит в рабочей папке под собственным jobId: чистка по TTL держит его, пока жива нить,
//   а склейка читает его как любую версию запуска;
// - файлы версии — только через AudioVersionFiles.Resolve: исходник — внутри проекта, версия — внутри
//   папки своей задачи; у личной области исходника проекта нет;
// - нет шва или ffmpeg — dsp_unavailable (на ручке 503), а не исключение.
public sealed class DspAudioEngine(
    AudioJobThreads threads,
    AudioEditWorkspace workspace,
    ILogger<DspAudioEngine> log,
    IAudioDsp? dsp = null)
{
    public const string ProviderKey = "dsp";
    public const string Label = "Без ИИ";

    public const string DspUnavailableText = "На сервере нет ffmpeg — правка без ИИ недоступна. Поставить его может администратор";

    // Входной файл крупнее — отказ до чтения: ffmpeg получает его целиком в памяти
    public const long MaxInputBytes = 200L * 1024 * 1024;

    public bool Available => dsp?.Available == true;

    public async Task<AudioEditCallResult<AudioDspVersionDto>> EditAsync(
        string ownerId, AudioEditScope scope, AudioDspEditInput input, CancellationToken ct)
    {
        if (dsp is null || !dsp.Available) return Fail(AudioEditErrorCodes.DspUnavailable, DspUnavailableText);
        if (!Enum.IsDefined(input.Op)) return Invalid("Неизвестная операция правки");
        if (input.Format is { } f && !Enum.IsDefined(f)) return Invalid("Неизвестный формат результата");
        var (basis, error) = Base(ownerId, scope, input.SessionId, input.ThreadId, input.BaseVersionId, input.Revision);
        if (error is not null) return error;
        var (thread, version) = basis!.Value;

        if (version.File(AudioFileRoles.Main) is not { } main) return Invalid("У версии нет звука");
        var (audio, path, denied) = await ReadAsync(ownerId, scope, version, main, ct);
        if (audio is null) return denied!;
        var format = input.Format ?? FormatOf(path!);

        var output = input.Op switch
        {
            AudioDspEditOp.Trim => input.StartSec is null && input.EndSec is null
                ? AudioDspOutput.Fail("Укажите начало или конец куска")
                : await dsp.TrimFadeGainAsync(audio, new AudioEdit(input.StartSec, input.EndSec, Format: format), ct),
            AudioDspEditOp.GainFade => input is { FadeInSec: 0, FadeOutSec: 0, GainDb: 0 }
                ? AudioDspOutput.Fail("Укажите фейд или громкость")
                : await dsp.TrimFadeGainAsync(audio,
                    new AudioEdit(FadeInSeconds: input.FadeInSec, FadeOutSeconds: input.FadeOutSec, GainDb: input.GainDb, Format: format), ct),
            AudioDspEditOp.Normalize => await dsp.NormalizeAsync(audio, input.TargetLufs ?? AudioDspLimits.DefaultLufs, format, ct),
            _ => await dsp.ConvertAsync(audio, format, input.SampleRate, input.Channels, ct),
        };
        if (output.Audio is null) return Invalid(output.Error ?? "Правка не получилась");

        return await WriteAsync(ownerId, scope, input.SessionId.Trim(), thread, version, output, input.Initiator,
            Describe(input, format), ct);
    }

    public async Task<AudioEditCallResult<AudioDspVersionDto>> MixAsync(
        string ownerId, AudioEditScope scope, AudioMixInput input, CancellationToken ct)
    {
        if (dsp is null || !dsp.Available) return Fail(AudioEditErrorCodes.DspUnavailable, DspUnavailableText);
        if (input.Format is { } f && !Enum.IsDefined(f)) return Invalid("Неизвестный формат результата");
        if (input.Stems.Count == 0) return Invalid("Выберите хотя бы один стем");
        if (input.Stems.Select(s => s.Role).Distinct(StringComparer.Ordinal).Count() != input.Stems.Count)
            return Invalid("Стем указан дважды");
        if (input.Stems.All(s => s.Muted)) return Invalid("Все стемы выключены — сводить нечего");
        var (basis, error) = Base(ownerId, scope, input.SessionId, input.ThreadId, input.BaseVersionId, input.Revision);
        if (error is not null) return error;
        var (thread, version) = basis!.Value;

        var stems = new List<AudioStem>();
        string? firstPath = null;
        foreach (var stem in input.Stems)
        {
            if (!stem.Role.StartsWith(AudioFileRoles.StemPrefix, StringComparison.Ordinal)
                || version.File(stem.Role) is not { } file)
                return Invalid($"В версии нет стема «{stem.Role}»");
            // Молчащий стем ffmpeg всё равно не услышит — с диска его не читаем
            if (stem.Muted) continue;
            var (audio, path, denied) = await ReadAsync(ownerId, scope, version, file, ct);
            if (audio is null) return denied!;
            firstPath ??= path;
            stems.Add(new AudioStem(audio, stem.GainDb));
        }

        var format = input.Format ?? FormatOf(firstPath!);
        var output = await dsp.MixAsync(stems, format, ct);
        if (output.Audio is null) return Invalid(output.Error ?? "Сведение не получилось");

        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var recipe = string.Join(", ", input.Stems.Select(s =>
            s.Role[AudioFileRoles.StemPrefix.Length..] + (s.Muted ? " выкл." : s.GainDb == 0 ? "" : $" {s.GainDb.ToString("+0.#;−0.#", ru)} дБ")));
        return await WriteAsync(ownerId, scope, input.SessionId.Trim(), thread, version, output, input.Initiator,
            $"свёл стемы ({recipe})", ct);
    }

    // Пики волны файла версии для плеера: role null — главный файл
    public async Task<AudioEditCallResult<AudioPeaksDto>> PeaksAsync(string ownerId, AudioEditScope scope,
        string sessionId, string threadId, string versionId, string? role, int points, CancellationToken ct)
    {
        if (dsp is null || !dsp.Available)
            return AudioEditCallResult<AudioPeaksDto>.Fail(AudioEditErrorCodes.DspUnavailable, DspUnavailableText);
        if (points is < 1 or > AudioDspLimits.MaxPeaks)
            return AudioEditCallResult<AudioPeaksDto>.Fail(AudioEditErrorCodes.InvalidRequest,
                $"Число точек волны — от 1 до {AudioDspLimits.MaxPeaks}");
        var thread = threads.Store.Get(ownerId, sessionId).Threads.FirstOrDefault(t => t.Id == threadId);
        if (thread is null)
            return AudioEditCallResult<AudioPeaksDto>.Fail(AudioEditErrorCodes.ThreadNotFound, "Звук не найден в этом чате");
        if (thread.Version(versionId) is not { } version)
            return AudioEditCallResult<AudioPeaksDto>.Fail(AudioEditErrorCodes.VersionNotFound, "Версии нет в этом звуке");
        if (version.File(role ?? AudioFileRoles.Main) is not { } file)
            return AudioEditCallResult<AudioPeaksDto>.Fail(AudioEditErrorCodes.FileNotFound, "Файла нет в этой версии");

        var (audio, _, denied) = await ReadAsync(ownerId, scope, version, file, ct);
        if (audio is null) return AudioEditCallResult<AudioPeaksDto>.Fail(denied!.ErrorCode!, denied.Error!);
        var peaks = await dsp.PeaksAsync(audio, points, ct);
        return peaks.Peaks is { } values
            ? AudioEditCallResult<AudioPeaksDto>.Ok(new AudioPeaksDto(values, peaks.Seconds))
            : AudioEditCallResult<AudioPeaksDto>.Fail(AudioEditErrorCodes.InvalidRequest, peaks.Error ?? "Волну не построить");
    }

    // Нить и версия-основа своего чата; ревизия, если задана, сверяется до работы ffmpeg
    private ((AudioThread Thread, AudioThreadVersion Version)?, AudioEditCallResult<AudioDspVersionDto>?) Base(
        string ownerId, AudioEditScope scope, string sessionId, string threadId, string? baseVersionId, long? revision)
    {
        if (!threads.OwnThread(ownerId, scope.Key, sessionId?.Trim(), threadId?.Trim()))
            return (null, Fail(AudioEditErrorCodes.ThreadNotFound, "Звук не найден в этом чате"));
        var state = threads.Store.Get(ownerId, sessionId!.Trim());
        if (revision is { } r && r != state.Revision)
            return (null, Fail(AudioEditErrorCodes.RevisionConflict, "Звуки чата уже поменялись — перечитайте их"));
        var thread = state.Threads.First(t => t.Id == threadId!.Trim());
        var version = string.IsNullOrWhiteSpace(baseVersionId) ? thread.CurrentVersion : thread.Version(baseVersionId.Trim());
        if (version is null) return (null, Fail(AudioEditErrorCodes.VersionNotFound, "Версии нет в этом звуке"));
        return ((thread, version), null);
    }

    private async Task<(byte[]? Audio, string? Path, AudioEditCallResult<AudioDspVersionDto>? Denied)> ReadAsync(
        string ownerId, AudioEditScope scope, AudioThreadVersion version, AudioVersionFile file, CancellationToken ct)
    {
        if (AudioVersionFiles.Resolve(workspace, ownerId, scope, version, file) is not { } path || !File.Exists(path))
            return (null, null, Fail(AudioEditErrorCodes.FileNotFound,
                scope.IsPersonal && version.IsOrigin
                    ? "В личном чате файлов проекта нет"
                    : "Файл версии не найден — рабочая папка очищена"));
        if (new FileInfo(path).Length > MaxInputBytes)
            return (null, null, Invalid($"Файл больше {MaxInputBytes / 1024 / 1024} МБ"));
        return (await File.ReadAllBytesAsync(path, ct), path, null);
    }

    // Файл — в свою папку задачи, затем новая версия нити от основы; журнал и рассылка — как у склейки
    private async Task<AudioEditCallResult<AudioDspVersionDto>> WriteAsync(string ownerId, AudioEditScope scope,
        string sessionId, AudioThread thread, AudioThreadVersion basis, AudioDspOutput output,
        AudioEditInitiator initiator, string what, CancellationToken ct)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var path = workspace.SaveFile(ownerId, jobId, 1, AudioFileRoles.Main, output.Audio!, output.Extension);
        var who = initiator == AudioEditInitiator.Agent ? "Ты" : "Человек";
        var written = threads.Store.AddEditVersion(ownerId, sessionId, thread.Id,
            [new AudioVersionFile(AudioFileRoles.Main, path)], null,
            new AudioThreadEvent(threads.Store.Now(), AudioThreadEventKinds.Edited,
                $"{who} {what} (без ИИ): {AudioJobThreads.Name(thread)}, от: {AudioThread.Label(basis)}", thread.Id, jobId),
            jobId, basis.Id);
        if (written.Status != AudioThreadWriteStatus.Ok || written.NewVersions.Count == 0)
        {
            log.LogWarning("Звук: правка без ИИ {JobId} не легла в нить {ThreadId}: {Status}", jobId, thread.Id, written.Status);
            return written.Status switch
            {
                AudioThreadWriteStatus.ThreadNotFound => Fail(AudioEditErrorCodes.ThreadNotFound, "Звук не найден в этом чате"),
                AudioThreadWriteStatus.VersionNotFound => Fail(AudioEditErrorCodes.VersionNotFound, "Версии нет в этом звуке"),
                _ => Invalid("Не удалось сохранить правку"),
            };
        }

        var version = written.NewVersions[0];
        // Своя карточка версии в ленте: каждая версия нити рисуется ровно одной карточкой
        await threads.AnchorAsync(sessionId, thread, ct, version.Id);
        await threads.BroadcastAsync(ownerId, scope.Key, sessionId, written.State);
        return AudioEditCallResult<AudioDspVersionDto>.Ok(
            new AudioDspVersionDto(thread.Id, version.Id, version.Number, jobId, written.State));
    }

    // Формат по расширению исходного файла; незнакомое — WAV
    private static AudioFormat FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => AudioFormat.Mp3,
        ".flac" => AudioFormat.Flac,
        ".ogg" or ".oga" => AudioFormat.Ogg,
        _ => AudioFormat.Wav,
    };

    private static string Describe(AudioDspEditInput input, AudioFormat format)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        string S(double? v) => v is { } x ? x.ToString("0.##", ru) : "…";
        return input.Op switch
        {
            AudioDspEditOp.Trim => $"обрезал до {S(input.StartSec ?? 0)}–{S(input.EndSec)} с",
            AudioDspEditOp.GainFade => $"поправил громкость и фейды ({S(input.GainDb)} дБ, фейды {S(input.FadeInSec)}/{S(input.FadeOutSec)} с)",
            AudioDspEditOp.Normalize => $"нормализовал громкость к {S(input.TargetLufs ?? AudioDspLimits.DefaultLufs)} LUFS",
            _ => $"перевёл в {AudioFormats.Extension(format)}"
                + (input.SampleRate is { } rate ? $", {rate} Гц" : "")
                + (input.Channels is { } ch ? $", каналов: {ch}" : ""),
        };
    }

    private static AudioEditCallResult<AudioDspVersionDto> Invalid(string error) =>
        Fail(AudioEditErrorCodes.InvalidRequest, error);

    private static AudioEditCallResult<AudioDspVersionDto> Fail(string code, string error) =>
        AudioEditCallResult<AudioDspVersionDto>.Fail(code, error);
}
