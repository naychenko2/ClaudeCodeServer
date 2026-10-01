namespace ClaudeHomeServer.Services.Media;

// Шов «обработка звука без ИИ» для модуля «Звук» (ADR-021, §2 «Швы в Core»): ffmpeg на хосте,
// реализация — FfmpegAudioDsp в вертикали Images; нет подсистемы images — нет и регистрации.
//
// Шов байтовый: на входе байты WAV/MP3/FLAC/OGG, на выходе байты файла; в папку проекта он ничего
// не пишет. Склейка (concat) добавлена решением Андрея от 01.10 поверх решения 3 ADR-021. Отказы —
// значением с причиной для человека, а не исключением; отмена ct — OperationCanceledException, как обычно.
public interface IAudioDsp
{
    // ffmpeg и ffprobe нашлись на хосте. Проверка разовая, дальше кешируется
    bool Available { get; }

    // null — не разобрали (не звук, неизвестный контейнер или ffmpeg недоступен)
    Task<AudioDspInfo?> ProbeAsync(byte[] audio, CancellationToken ct);

    // Пики волны для плеера: points значений 0..1 — максимум модуля сэмпла в своём отрезке
    Task<AudioPeaks> PeaksAsync(byte[] audio, int points, CancellationToken ct);

    // known — результат ProbeAsync этих же байтов, если он уже есть у вызывающего: без него разбор
    // идёт заново отдельным процессом ffprobe
    Task<AudioDspOutput> TrimFadeGainAsync(byte[] audio, AudioEdit edit, CancellationToken ct, AudioDspInfo? known = null);

    // Нормализация громкости по EBU R128 (два прохода loudnorm), истинный пик не выше −1 dBTP
    Task<AudioDspOutput> NormalizeAsync(byte[] audio, double targetLufs = AudioDspLimits.DefaultLufs,
        AudioFormat format = AudioFormat.Wav, CancellationToken ct = default, AudioDspInfo? known = null);

    // Сведение стемов в один файл без автоприглушения: длина — по самому длинному звучащему стему
    Task<AudioDspOutput> MixAsync(IReadOnlyList<AudioStem> stems, AudioFormat format, CancellationToken ct);

    // Склейка кусков по порядку в один файл. joints — стыки между соседями, ровно pieces.Count − 1.
    // Частоту и каналы сводим сами: к наибольшей частоте, стерео — если хоть один кусок не моно.
    // normalizeLufs — выровнять громкость каждого куска до этой цели перед склейкой (тихий кусок не трогаем)
    Task<AudioDspOutput> ConcatAsync(IReadOnlyList<byte[]> pieces, IReadOnlyList<AudioJoint> joints,
        double? normalizeLufs, AudioFormat format, CancellationToken ct);

    // sampleRate/channels — null: как у исходника
    Task<AudioDspOutput> ConvertAsync(byte[] audio, AudioFormat format, int? sampleRate, int? channels, CancellationToken ct);
}

public enum AudioFormat { Wav, Mp3, Flac, Ogg }

// Format — расширение с точкой по сигнатуре («.wav»)
public sealed record AudioDspInfo(double Seconds, int SampleRate, int Channels, string Format);

public sealed record AudioPeaks(IReadOnlyList<float>? Peaks, double Seconds, string? Error)
{
    public static AudioPeaks Fail(string error) => new(null, 0, error);
}

public sealed record AudioDspOutput(byte[]? Audio, AudioFormat Format, string? Error)
{
    public string ContentType => AudioFormats.ContentType(Format);
    public string Extension => AudioFormats.Extension(Format);

    public static AudioDspOutput Fail(string error) => new(null, AudioFormat.Wav, error);
}

// Интервал [StartSeconds; EndSeconds) — null с любой стороны: от начала / до конца. Фейды считаются
// от краёв уже вырезанного куска, усиление — в децибелах ко всему куску
public sealed record AudioEdit(
    double? StartSeconds = null,
    double? EndSeconds = null,
    double FadeInSeconds = 0,
    double FadeOutSeconds = 0,
    double GainDb = 0,
    AudioFormat Format = AudioFormat.Wav);

public sealed record AudioStem(byte[] Audio, double GainDb = 0, bool Muted = false);

// Встык; пауза тишиной Seconds; плавный переход — куски звучат внахлёст Seconds, итог на столько короче
public enum AudioJointKind { Butt, Pause, Crossfade }

public sealed record AudioJoint(AudioJointKind Kind, double Seconds = 0)
{
    public static readonly AudioJoint Butt = new(AudioJointKind.Butt);
    public static AudioJoint Pause(double seconds) => new(AudioJointKind.Pause, seconds);
    public static AudioJoint Crossfade(double seconds) => new(AudioJointKind.Crossfade, seconds);
}

public static class AudioDspLimits
{
    public const double DefaultLufs = -14;
    public const double MinLufs = -70;
    public const double MaxLufs = -5;
    public const double MinGainDb = -60;
    public const double MaxGainDb = 30;
    public const int MaxPeaks = 10_000;
    public const int MaxStems = 16;
    public const int MinSampleRate = 8_000;
    public const int MaxSampleRate = 192_000;
    public const int MaxChannels = 8;

    // Склейка: от двух до двадцати кусков, итог не длиннее часа, стык — до пяти секунд (ползунок
    // макета 0,1–5 с). Громкость кусков по умолчанию выравнивается к −16 LUFS — уровень речи и подкастов
    public const int MinConcatPieces = 2;
    public const int MaxConcatPieces = 20;
    public const double MaxConcatSeconds = 3600;
    public const double MaxJointSeconds = 5;
    public const double ConcatLufs = -16;
}

public static class AudioFormats
{
    public static string Extension(AudioFormat f) => f switch
    {
        AudioFormat.Mp3 => ".mp3",
        AudioFormat.Flac => ".flac",
        AudioFormat.Ogg => ".ogg",
        _ => ".wav",
    };

    public static string ContentType(AudioFormat f) => f switch
    {
        AudioFormat.Mp3 => "audio/mpeg",
        AudioFormat.Flac => "audio/flac",
        AudioFormat.Ogg => "audio/ogg",
        _ => "audio/wav",
    };
}
