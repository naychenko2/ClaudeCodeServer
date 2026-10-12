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

    // Тумблер LocalMedia:Enabled включён — поставщик заведён, даже если ComfyUI сейчас лежит
    bool Configured { get; }

    // Длина общей очереди ComfyUI (идущая плюс ждущие, с чужими прогонами); null — недоступен
    Task<int?> QueueLengthAsync(CancellationToken ct);

    // Имена LoRA, которые видит ComfyUI, в порядке его ответа; null — ComfyUI недоступен или не настроен
    Task<IReadOnlyList<string>?> ListLorasAsync(CancellationToken ct);

    // Ожидаемое время одного запуска по таблице замеров local-media; null — не замерено
    int? EtaSeconds(LocalImageOp op, int count, int images);

    Task<LocalImageSubmitted> SubmitAsync(LocalImageRequest request, CancellationToken ct);

    Task<LocalImagePoll> PollAsync(string ticket, CancellationToken ct);

    // Снять задачу из ОЖИДАЮЩИХ очереди; идущую не прерываем (interrupt ComfyUI бьёт по
    // любому текущему прогону, в том числе чужому) — тогда false
    Task<bool> CancelAsync(string ticket, CancellationToken ct);

    // Углы головы по фото (ADR-027, §3): узел ComfyUI CcsFacePose, расчёт на CPU. Poses — по одному
    // на вход в том же порядке, null — лица нет. Подаётся только при пустой очереди ComfyUI;
    // занятая очередь, недоступный ComfyUI и потолок ожидания — Busy, а не исключение. Реализация
    // по умолчанию — «не поддерживается»: подсистема images ещё не подключена
    Task<LocalFacePoseResult> FacePoseAsync(IReadOnlyList<byte[]> images, CancellationToken ct) =>
        Task.FromResult(new LocalFacePoseResult(null, "Определение ракурса здесь не поддерживается."));
}

public enum LocalImageOp { Generate, Edit, FaceDetail }

// Images: у Edit первая — холст, дальше образцы (до 16 всего); у Generate — необязательные
// образцы (тогда граф правки на пустом холсте размера Aspect); у FaceDetail — ровно одна.
// Aspect — ключ таблицы размеров Qwen-Image («1:1», «16:9» …); null — по холсту или 1:1.
// EraseMask — только у Edit: белое на маске закрашивается на холсте нейтральным серым до
// модели («удали» кистью). Маску-образец Qwen-Image не понимает, а серую заливку — да.
// KeepMask — только у Edit: вне маски вернуть исходник (схожесть мерится внутри маски).
// Несовместима с EraseMask и Pad; маска — PNG того же размера, что холст
// Pad — только у Edit: холст расширяется полями нейтрального серого до модели («Дорисовать за
// края»), результат — в пропорции расширенного холста
// Lora — только у Edit и у Generate с образцами
// FaceReferences — фото эталона персонажа только для узла сходства лица (граф правки); в
// энкодер модели они не идут. У FaceDetail и у генерации без образцов узла сходства нет
public sealed record LocalImageRequest(
    LocalImageOp Op,
    string Prompt,
    IReadOnlyList<byte[]> Images,
    string? Aspect,
    int Count,
    long? Seed = null,
    byte[]? EraseMask = null,
    LocalImagePad? Pad = null,
    int? Width = null,
    int? Height = null,
    LocalImageLora? Lora = null,
    IReadOnlyList<byte[]>? FaceReferences = null,
    byte[]? KeepMask = null);

// LoRA персонажа: имя файла в ComfyUI и сила применения
public sealed record LocalImageLora(string Name, double Strength);

// Поля в пикселях холста по сторонам
public sealed record LocalImagePad(int Left, int Top, int Right, int Bottom);

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

// Similarity — сходство лица с эталоном (0..1); null — не считалось или лицо не найдено
public sealed record LocalImageFile(byte[] Bytes, string ContentType, double? Similarity = null);

// Poses — по одному на вход, в том же порядке; null — лицо не найдено.
// Busy — очередь занята или ComfyUI не ответил: «попробуй позже», фото не помечаются
public sealed record LocalFacePoseResult(IReadOnlyList<LocalFacePose?>? Poses, string? Error, bool Busy = false)
{
    public static LocalFacePoseResult Fail(string error, bool busy = false) => new(null, error, busy);
}

// Углы головы в градусах; осям соответствуют имена полей, а не позиции
public sealed record LocalFacePose(double Yaw, double Pitch, double Roll);
