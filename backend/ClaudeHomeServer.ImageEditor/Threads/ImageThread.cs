namespace ClaudeHomeServer.Services.ImageEditor.Threads;

// Нити картинок чата (ADR-019 §1): что лежит в data/image-threads/{ownerId}/{sessionId}.json.
// Focus — нить «в работе» этого чата или null; Revision растёт с каждой записью, запись со
// старой ревизией — конфликт. Добавлять поля можно только аддитивно: файл живёт в бэкапе.
public sealed record ImageThreadsState(string? Focus, long Revision, IReadOnlyList<ImageThread> Threads)
{
    public static ImageThreadsState Empty { get; } = new(null, 0, []);
}

// Нить — одна картинка в ленте чата. File — путь от корня проекта через «/», null у черновика
// «Новая картинка»: у него есть только DraftFolder ("" — корень). Lineage — прежние пути файла,
// по которым нить шла за сохранениями. Stacks — стопки шагов: текущая и «старые» после отката.
public sealed record ImageThread(
    string Id,
    string? File,
    IReadOnlyList<string> Lineage,
    string? DraftFolder,
    IReadOnlyList<ImageThreadStack> Stacks,
    string? CurrentStackId,
    ImageThreadSettings? Settings,
    string? PendingJobId,
    DateTime CreatedAt);

// Стопка шагов карточки. Steps — id шагов ImageEditStep по порядку; ForkedFromStepId — шаг, от
// которого стопка продолжилась после отката; Old — замороженная «старая стопка»
public sealed record ImageThreadStack(
    string StackId,
    IReadOnlyList<string> Steps,
    string? ForkedFromStepId,
    bool Old);

// Настройки запуска нити: поставщик, модель, число вариантов, «размер оригинала»
public sealed record ImageThreadSettings(string? Provider, string? Model, int Count, bool MatchSourceSize);

public enum ImageThreadWriteStatus
{
    Ok,
    // Ревизия устарела — State несёт актуальное состояние
    Conflict,
    // Нити с таким id в этом чате нет (чужая неотличима от несуществующей)
    ThreadNotFound,
}

public sealed record ImageThreadWrite(ImageThreadWriteStatus Status, ImageThreadsState State);
