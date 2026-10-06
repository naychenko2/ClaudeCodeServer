using System.Text.Json.Serialization;
using ClaudeHomeServer.Services.ChatContext;

namespace ClaudeHomeServer.Services.ImageEditor.Threads;

// Нити картинок чата (ADR-019 §1): что лежит в data/image-threads/{ownerId}/{sessionId}.json.
// Focus — нить «в работе» этого чата или null; Revision растёт с каждой записью, запись со
// старой ревизией — конфликт. Добавлять поля можно только аддитивно: файл живёт в бэкапе.
// Events — журнал «с прошлого сообщения» для блока хвоста хода, TurnCursor — до какого момента
// журнал уже показан ходу (сдвиг курсора ревизию не поднимает: сборка хода — не правка).
public sealed record ImageThreadsState(string? Focus, long Revision, IReadOnlyList<ImageThread> Threads)
{
    public static ImageThreadsState Empty { get; } = new(null, 0, []);

    public IReadOnlyList<ImageThreadEvent> Events { get; init; } = [];
    public DateTime? TurnCursor { get; init; }
}

// Нить — одна картинка в ленте чата. File — путь от корня проекта через «/», null у черновика
// «Новая картинка»: у него есть только DraftFolder ("" — корень). Lineage — прежние пути файла,
// по которым нить шла за сохранениями.
//
// Версии (изменение 27.09 к ADR-019): каждый вариант каждого запуска ИИ — версия нити, «Взять» нет.
// Versions — по порядку появления, первой всегда исходник (ImageThreadVersion.OriginId);
// CurrentVersionId — версия «в работе», от неё идёт следующая правка. Launches — запуски в нить:
// по завершении варианты запуска становятся версиями, а запуск без живой задачи после перезапуска
// помечается «потерян».
//
// Стопки — формат до 27.09, только у старых нитей: Stacks, CurrentStackId, CurrentStepId (шаг на
// холсте стопки), PendingJobId (варианты ждут «Взять»), InterruptedJobId (задача, оборванная
// перезапуском). Хранилище их читает и старые ручки с ними работают; новые нити стопок не заводят,
// а исходник старой нити — её текущий шаг стопки (ImageStepOf).
public sealed record ImageThread(
    string Id,
    string? File,
    IReadOnlyList<string> Lineage,
    string? DraftFolder,
    IReadOnlyList<ImageThreadStack> Stacks,
    string? CurrentStackId,
    ImageThreadSettings? Settings,
    string? PendingJobId,
    DateTime CreatedAt,
    string? CurrentStepId = null,
    string? InterruptedJobId = null)
{
    public IReadOnlyList<ImageThreadVersion> Versions { get; init; } = [];
    public string? CurrentVersionId { get; init; }
    public IReadOnlyList<ImageThreadLaunch> Launches { get; init; } = [];
    // Файлы, которые человек сохранил из нити (для «Зафиксировать только этот чат», ADR-023 §3.3)
    public IReadOnlyList<ThreadSavedFile> SavedFiles { get; init; } = [];

    [JsonIgnore]
    public ImageThreadStack? CurrentStack => Stacks.FirstOrDefault(s => s.StackId == CurrentStackId);

    [JsonIgnore]
    public ImageThreadVersion? CurrentVersion => Version(CurrentVersionId);

    // Есть что терять: шаги стопок, правки исходника или хотя бы одна версия от ИИ
    [JsonIgnore]
    public bool HasSteps => Stacks.Any(s => s.Steps.Count > 0) || Versions.Any(v => !v.IsOrigin || v.Steps.Count > 0);

    [JsonIgnore]
    public bool HasRunningLaunch => Launches.Any(l => l.Status == ImageThreadLaunchStatus.Running);

    public ImageThreadVersion? Version(string? id) => id is null ? null : Versions.FirstOrDefault(v => v.Id == id);

    // Шаг стопки (формат до 27.09): «Взять» и откат работают только с ними
    public bool Owns(string stepId) => Stacks.Any(s => s.Steps.Contains(stepId));

    // Картинка версии: её текущий шаг; у исходника без правок — текущий шаг старой стопки, иначе
    // файл (null — файл нити или пустой черновик)
    public string? ImageStepOf(ImageThreadVersion version) =>
        version.CurrentStepId ?? (version.IsOrigin ? CurrentStepId : version.BaseStepId);

    // «Версия 3» / «исходник»
    public static string Label(ImageThreadVersion version) =>
        version.IsOrigin ? "исходник" : $"версия {version.Number}";
}

// Версия картинки. Исходник: Id = OriginId, Number = 0, без JobId — сама картинка нити, правки без
// ИИ до первого запуска ложатся в него. Версия от ИИ: JobId + Variant — вариант запуска;
// BaseVersionId и BaseStepId — версия и её шаг, от которых запускали (null — от файла или пустого
// черновика). Steps — шаги ImageEditStep версии по порядку: у версии от ИИ первым идёт сам вариант,
// дальше правки без ИИ; CurrentStepId — шаг версии, который сейчас её картинка
public sealed record ImageThreadVersion(
    string Id,
    int Number,
    string? JobId,
    int? Variant,
    string? BaseVersionId,
    string? BaseStepId,
    IReadOnlyList<string> Steps,
    string? CurrentStepId,
    DateTime CreatedAt)
{
    public const string OriginId = "origin";

    [JsonIgnore]
    public bool IsOrigin => JobId is null;

    public static ImageThreadVersion Origin(DateTime at) => new(OriginId, 0, null, null, null, null, [], null, at);
}

// Запуск ИИ в нить: его якорь в ленте (image_launch_versions) — по JobId. BaseVersionId и BaseStepId —
// откуда запустили; Status — ImageThreadLaunchStatus.*; Initiator — human | agent; Prompt — для
// блока хвоста хода («версия 3 — вариант 1 запуска «синий фон»»)
public sealed record ImageThreadLaunch(
    string JobId,
    string? BaseVersionId,
    string? BaseStepId,
    DateTime At,
    string Status,
    string Initiator,
    string? Prompt);

public static class ImageThreadLaunchStatus
{
    public const string Running = "running";
    // Варианты стали версиями
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    // Задачу оборвал перезапуск сервера: реестр задач живёт в памяти, вариантов не будет
    public const string Interrupted = "interrupted";
}

// Стопка шагов карточки. Steps — id шагов ImageEditStep по порядку, от первого шага нити (у
// стопки после отката в начале — общие со старой стопкой шаги до ForkedFromStepId включительно);
// ForkedFromStepId — шаг, от которого стопка продолжилась после отката (null — от исходника);
// Old — замороженная «старая стопка»
public sealed record ImageThreadStack(
    string StackId,
    IReadOnlyList<string> Steps,
    string? ForkedFromStepId,
    bool Old);

// Настройки запуска нити: поставщик, модель, число вариантов, «размер оригинала»
public sealed record ImageThreadSettings(string? Provider, string? Model, int Count, bool MatchSourceSize);

// Запись журнала нитей: Kind — ImageThreadEventKinds.*, Text — строка для блока хвоста хода
public sealed record ImageThreadEvent(DateTime At, string Kind, string Text, string? ThreadId = null, string? JobId = null);

public static class ImageThreadEventKinds
{
    public const string Launched = "launched";
    public const string Taken = "taken";
    public const string Saved = "saved";
    public const string Interrupted = "interrupted";
    // Варианты запуска стали версиями (или запуск кончился без них)
    public const string Versions = "versions";
}

public enum ImageThreadWriteStatus
{
    Ok,
    // Ревизия устарела — State несёт актуальное состояние
    Conflict,
    // Нити с таким id в этом чате нет (чужая неотличима от несуществующей)
    ThreadNotFound,
    // Шага нет в этой нити
    StepNotFound,
    // Действие нити не подходит: удалить нить с шагами или с идущей задачей
    Invalid,
    // Версии нет в этой нити
    VersionNotFound,
}

// Forked — запись завела новую стопку после отката: её якорь и строку «Шаги … не пропали»
// вызывающий кладёт в ленту. Thread — нить после записи
public sealed record ImageThreadWrite(ImageThreadWriteStatus Status, ImageThreadsState State)
{
    public ImageThread? Thread { get; init; }
    public ImageThreadFork? Forked { get; init; }
    // Создание не завело новую нить, а взяло в работу уже существующую по тому же файлу
    public bool Existing { get; init; }
    // Версии, которые завела запись завершения запуска
    public IReadOnlyList<ImageThreadVersion> NewVersions { get; init; } = [];
}

// Откат и новая правка: FrozenStack — старая стопка, NewStack — новая. KeptFrom/KeptTo —
// номера (с 1) шагов старой стопки после точки отката, которые остались в ней; null — таких нет
public sealed record ImageThreadFork(ImageThreadStack FrozenStack, ImageThreadStack NewStack, int? KeptFrom, int? KeptTo);
