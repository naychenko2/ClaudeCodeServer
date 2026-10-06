namespace ClaudeHomeServer.Services.Media;

// Шов «обработка видео без ИИ» для модуля «Видео» (ADR-022 §4): ffmpeg на хосте, реализация — FfmpegVideoDsp в
// вертикали Images; нет подсистемы images — нет и регистрации.
//
// В отличие от IAudioDsp шов ФАЙЛОВЫЙ, не байтовый: клипы весят сотни мегабайт, и гонять их через byte[] нельзя.
// Все пути — полные хостовые, их разрешает и проверяет вызывающий (границы проекта, символические ссылки);
// шов путей не доверяет только в одном: открывает входы как файлы, а не как URL ffmpeg. Отказы — значением
// с причиной для человека, а не исключением; отмена ct — OperationCanceledException, как обычно.
public interface IVideoDsp
{
    // ffmpeg и ffprobe нашлись на хосте. Проверка разовая, дальше кешируется
    bool Available { get; }

    // null — не разобрали (не видео, повреждён или ffmpeg недоступен)
    Task<VideoDspInfo?> ProbeAsync(string path, CancellationToken ct);

    // Полоса кадров по длине клипа (frames штук подряд, высота height пикселей) одним JPEG — для таймлайна
    Task<VideoDspResult> FilmstripAsync(string path, string outPath, int frames, int height, CancellationToken ct);

    // Последний кадр клипа PNG — основа «продолжить от конца»
    Task<VideoDspResult> LastFrameAsync(string path, string outPath, CancellationToken ct);

    // Сборка фильма: перекодирование всех клипов под общий размер и частоту, склейки, музыка. Пишет ТОЛЬКО в
    // outPath (его выбирает вызывающий — временный файл) и удаляет его при отказе и отмене. Запуск тяжёлый:
    // слот BuildConcurrencyGate берётся до старта процесса, ожидание слота видно в progress как «waiting».
    // Отмена убивает дерево процессов ffmpeg
    Task<VideoDspResult> AssembleAsync(FilmPlan plan, string outPath, IProgress<VideoAssembleProgress>? progress,
        CancellationToken ct);
}

// Длительность в секундах; Fps — средняя частота видеодорожки
public sealed record VideoDspInfo(double Seconds, int Width, int Height, double Fps, bool HasAudio);

public sealed record VideoDspResult(bool Ok, string? Error)
{
    public static VideoDspResult Success { get; } = new(true, null);
    public static VideoDspResult Fail(string error) => new(false, error);
}

// Stage — waiting (ждём слот тяжёлых запусков) | running; Fraction 0..1 по времени вывода
public sealed record VideoAssembleProgress(string Stage, double Fraction)
{
    public const string Waiting = "waiting";
    public const string Running = "running";
}

// План сборки (ADR-022 §4). Clips — по порядку, Cuts — ровно Clips.Count − 1, стыки между соседями.
// Клип приводится к Width×Height с чёрными полями (без растяжения) и к Fps; без звука — тишина.
// Timeout — потолок процесса (VideoEditor:AssembleTimeoutMinutes)
public sealed record FilmPlan(
    int Width,
    int Height,
    int Fps,
    IReadOnlyList<FilmPlanClip> Clips,
    IReadOnlyList<FilmPlanCut> Cuts,
    FilmPlanMusic? Music,
    TimeSpan Timeout);

// StartSeconds..EndSeconds — кусок клипа, уже в пределах его длины
public sealed record FilmPlanClip(string Path, double StartSeconds, double EndSeconds, bool HasAudio);

// Type — butt | dissolve | fade (значения FilmCutTypes модуля); Seconds — длина наплыва или затемнения
public sealed record FilmPlanCut(string Type, double Seconds);

// VolumePercent 0..100, FadeOutSeconds — затухание в конце фильма
public sealed record FilmPlanMusic(string Path, int VolumePercent, double FadeOutSeconds);
