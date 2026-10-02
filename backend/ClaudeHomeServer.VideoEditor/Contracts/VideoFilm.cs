namespace ClaudeHomeServer.Services.VideoEditor.Contracts;

// Формат `.film` (ADR-022 §2): JSON с версией схемы, файл проекта video/<фильм>/<фильм>.film.
// Ссылается на ФАЙЛЫ, а не на нити — живёт между чатами. Cuts.Count == Items.Count − 1, иначе film_invalid;
// неизвестная Schema — только чтение (film_schema_unsupported). Пометки «✦ Claude» и деньги в файл
// не пишутся — они в data/video-films. Чтение, запись под ревизией и сборка — блок 2

public sealed record FilmDocument(
    int Schema,
    string Aspect,
    IReadOnlyList<FilmItem> Items,
    IReadOnlyList<FilmCut> Cuts,
    FilmMusic? Music,
    IReadOnlyList<FilmBuild> Builds)
{
    public const int CurrentSchema = 1;
}

// Trim — [начало, конец] в секундах клипа. Scene — снимок сцены для «Сценария» и «Переснять» из чата,
// где этой сцены нет; кадры в снимке — только файлы проекта
public sealed record FilmItem(string File, IReadOnlyList<double> Trim, FilmSceneSnapshot? Scene);

public sealed record FilmSceneSnapshot(
    string Text,
    string? FrameA,
    string? FrameB,
    string? Provider,
    string? Model,
    int? DurationSec);

// Type — FilmCutTypes.*; Sec — длительность наплыва или затемнения (у встык 0)
public sealed record FilmCut(string Type, double Sec);

public static class FilmCutTypes
{
    public const string Butt = "butt";
    public const string Dissolve = "dissolve";
    public const string Fade = "fade";
}

// Volume — проценты 0..100, FadeOut — секунды затухания в конце
public sealed record FilmMusic(string File, int Volume, double FadeOut);

// Сборка: SourceHash — хеш того, что шло в сборку. «Устарел» и «● обновлена» вычисляются из него
public sealed record FilmBuild(string File, string SourceHash, DateTime At);

public sealed record FilmSummaryDto(string Path, string Name, int ItemCount, double DurationSec, bool Stale, bool Valid);

// Revision — хеш содержимого файла (етаг); Stale — признаки по строкам (индекс → причина) из SourceHash
public sealed record FilmStateDto(
    string Path,
    string Revision,
    FilmDocument Document,
    VideoSpentDto Spent,
    IReadOnlyList<FilmItemMarkDto> Marks,
    FilmBuildStatusDto? Build);

// Метки строки: «✦ Claude» (Claude = правил агент), «● обновлена» (Updated), «переснять» (Stale)
public sealed record FilmItemMarkDto(int Index, bool Claude, bool Updated, bool Stale);

// Счётчик «Потрачено на фильм» — отображение, не потолок: сумма версий сцен фильма по валютам
public sealed record VideoSpentDto(double Usd, double Credits, double GpuSeconds);

// Правка фильма атомарным патчем под ревизией: чужая правка между чтением и записью — 409 revision_conflict
public sealed record FilmPatch(string ExpectedRevision, IReadOnlyList<FilmPatchOp> Ops);

// Op — add | remove | move | cut | trim | music. Поля читаются по операции:
// add — File, Index?, Scene?; remove — Index; move — From, To; cut — Index (стык после строки), CutType, Sec;
// trim — Index, Trim; music — Music (null — убрать)
public sealed record FilmPatchOp(
    string Op,
    string? File = null,
    int? Index = null,
    int? From = null,
    int? To = null,
    IReadOnlyList<double>? Trim = null,
    string? CutType = null,
    double? Sec = null,
    FilmSceneSnapshot? Scene = null,
    FilmMusic? Music = null);

public static class FilmPatchOps
{
    public const string Add = "add";
    public const string Remove = "remove";
    public const string Move = "move";
    public const string Cut = "cut";
    public const string Trim = "trim";
    public const string Music = "music";
}

// «Сочинить под фильм…»: SessionId — чат, в ленте которого заводится черновик музыки в «Звуке». Ответ —
// нить звука: фильм ждёт из неё музыку, первая готовая версия сама ляжет в music/<фильм>.mp3 и станет музыкой фильма
public sealed record FilmMusicRequest(string SessionId);

public sealed record FilmMusicDraftDto(string ThreadId);

// State — waiting (ждём слот сборки) | running | done | failed | cancelled; Progress 0..1
public sealed record FilmBuildStatusDto(string State, double Progress, string? File, string? Error, DateTime? StartedAt);

public static class FilmBuildStates
{
    public const string Waiting = "waiting";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}
