using System.Text.Json.Serialization;

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
// по которым нить шла за сохранениями. Stacks — стопки шагов: текущая и «старые» после отката.
// CurrentStepId — шаг, который сейчас на холсте; null — исходник (файл) или пустой черновик.
// PendingJobId — задача, чьи варианты ждут «Взять» или «Не брать». InterruptedJobId — задача,
// которую оборвал перезапуск сервера (реестр задач живёт в памяти): карточка показывает
// «Генерация прервана перезапуском сервера», поле снимает следующий запуск или «Не брать».
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
    [JsonIgnore]
    public ImageThreadStack? CurrentStack => Stacks.FirstOrDefault(s => s.StackId == CurrentStackId);

    [JsonIgnore]
    public bool HasSteps => Stacks.Any(s => s.Steps.Count > 0);

    public bool Owns(string stepId) => Stacks.Any(s => s.Steps.Contains(stepId));
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
}

// Forked — запись завела новую стопку после отката: её якорь и строку «Шаги … не пропали»
// вызывающий кладёт в ленту. Thread — нить после записи
public sealed record ImageThreadWrite(ImageThreadWriteStatus Status, ImageThreadsState State)
{
    public ImageThread? Thread { get; init; }
    public ImageThreadFork? Forked { get; init; }
    // Создание не завело новую нить, а взяло в работу уже существующую по тому же файлу
    public bool Existing { get; init; }
}

// Откат и новая правка: FrozenStack — старая стопка, NewStack — новая. KeptFrom/KeptTo —
// номера (с 1) шагов старой стопки после точки отката, которые остались в ней; null — таких нет
public sealed record ImageThreadFork(ImageThreadStack FrozenStack, ImageThreadStack NewStack, int? KeptFrom, int? KeptTo);
