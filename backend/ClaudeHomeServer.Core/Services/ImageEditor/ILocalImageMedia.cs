namespace ClaudeHomeServer.Services.ImageEditor;

// Шов «локальные модели картинок» для редактора (ADR-018, раздел «Локальные модели»): движок
// local-media (ComfyUI на своей GPU) живёт в вертикали Images, модуль редактора на неё не
// ссылается. Реализация — адаптер в Images; нет подсистемы images — нет и регистрации, драйвер
// тогда просто скрыт в каталоге.
//
// Шов байтовый: входы — байты холста и образцов, выход — байты вариантов. В папку проекта он
// ничего не пишет (результат редактора живёт в его рабочей папке до «Сохранить»), граф — только
// из шаблонов ComfyWorkflows. Ticket — prompt_id ComfyUI: он приходит только от драйвера в
// процессе, снаружи его не передают.
public interface ILocalImageMedia
{
    // Тумблер LocalMedia:Enabled включён и ComfyUI отвечал недавно. Не блокирует надолго:
    // проверка живости кешируется
    bool Available { get; }

    // Длина общей очереди ComfyUI (идущая плюс ждущие, с чужими прогонами); null — недоступен
    Task<int?> QueueLengthAsync(CancellationToken ct);

    // Ожидаемое время одного запуска по таблице замеров local-media; null — не замерено
    int? EtaSeconds(LocalImageOp op, int count, int images);

    Task<LocalImageSubmitted> SubmitAsync(LocalImageRequest request, CancellationToken ct);

    Task<LocalImagePoll> PollAsync(string ticket, CancellationToken ct);

    // Снять задачу из ОЖИДАЮЩИХ очереди; идущую не прерываем (interrupt ComfyUI бьёт по
    // любому текущему прогону, в том числе чужому) — тогда false
    Task<bool> CancelAsync(string ticket, CancellationToken ct);
}

public enum LocalImageOp { Generate, Edit, FaceDetail }

// Images: у Edit первая — холст, дальше образцы (до 16 всего); у Generate — необязательные
// образцы (тогда граф правки на пустом холсте размера Aspect); у FaceDetail — ровно одна.
// Aspect — ключ таблицы размеров Qwen-Image («1:1», «16:9» …); null — по холсту или 1:1.
// EraseMask — только у Edit: белое на маске закрашивается на холсте нейтральным серым до
// модели («удали» кистью). Маску-образец Qwen-Image не понимает, а серую заливку — да
public sealed record LocalImageRequest(
    LocalImageOp Op,
    string Prompt,
    IReadOnlyList<byte[]> Images,
    string? Aspect,
    int Count,
    long? Seed = null,
    byte[]? EraseMask = null);

// Busy — очередь GPU занята или ComfyUI не ответил: это «попробуй позже», а не ошибка запроса
public sealed record LocalImageSubmitted(string? Ticket, int? QueuePosition, int? EtaSeconds, string? Error, bool Busy = false)
{
    public static LocalImageSubmitted Fail(string error, bool busy = false) => new(null, null, null, error, busy);
}

public enum LocalImageState { Queued, Running, Completed, Failed }

// Warning — временный сбой опроса (ComfyUI не ответил), задача при этом жива
public sealed record LocalImagePoll(
    LocalImageState State,
    int? QueuePosition,
    IReadOnlyList<LocalImageFile> Files,
    string? Error,
    string? Warning = null);

public sealed record LocalImageFile(byte[] Bytes, string ContentType);
