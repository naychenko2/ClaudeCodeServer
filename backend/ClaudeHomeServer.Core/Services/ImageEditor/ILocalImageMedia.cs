using ClaudeHomeServer.Services.Media;

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

    // То же для локальной модели (LocalImageModels; null — Qwen «качественно»). По умолчанию
    // вариант не различается: подсистема images переопределяет
    int? EtaSeconds(LocalImageOp op, int count, int images, string? model) => EtaSeconds(op, count, images);

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

    // Лица на картинке (ADR-028): узел ComfyUI CcsFaceDetect, ответ — bbox по порядку слева
    // направо. Faces — в порядке ответа воркера, N — номер с 1 (присваивает бэкенд); Error — сбой
    // или неожиданный ответ, Busy — очередь занята или ComfyUI не ответил. Реализация по умолчанию
    // — «не поддерживается»: подсистема images ещё не подключена
    Task<LocalFaceDetectResult> DetectFacesAsync(byte[] image, CancellationToken ct) =>
        Task.FromResult(new LocalFaceDetectResult(0, 0, [], "Детекция лиц здесь не поддерживается."));
}

public enum LocalImageOp { Generate, Edit, FaceDetail }

// Локальные модели картинок в шве. Quality — Qwen-Image 2.1, 25 шагов, int8 convrot; Turbo — его
// дистиллят на 8 шагов (замер 2026-10-13, RTX 3090: генерация 8 с против 22 с); Klein — FLUX 3
// Image Klein, 16 шагов, с апсемплером промпта (A/B 2026-10-22). Строки совпадают с id моделей
// каталога редактора; неизвестное значение шов отвергает, а не молча берёт умолчание
public static class LocalImageModels
{
    public const string Quality = "qwen-image-2.1";
    public const string Turbo = "qwen-image-2.1-turbo";
    public const string Klein = "flux3-klein";
    public const string H3Image = "minimax-h3-image";
    // Fibo 2.0 (Bria): Dev — 50 шагов с CFG, генерация и правка; Turbo — дистиллят на 4 шага для правки
    public const string Fibo2 = "fibo2";
    public const string Fibo2Turbo = "fibo2-turbo";
    // Только генерация по тексту: Krea 2 Turbo (8 шагов) и MOSS-Image SFT (25 шагов)
    public const string Krea2Turbo = "krea2-turbo";
    public const string Moss = "moss-image";

    // Больше картинок на входе Klein не проверяли (A/B и граф BFL — до четырёх)
    public const int KleinMaxImages = 10;

    // Сколько фото персонажа Klein берёт как одного человека (A/B 2026-10-22, «Андрей»: схожесть
    // 0,705 на одном фото, 0,765 на пяти; Qwen-Image на каждое фото рисует отдельного человека)
    public const int KleinCharacterPhotos = 5;

    // MiniMax-H3-Image берёт до 16 картинок (<Picture 1>…<Picture 16>, карточка модели)
    public const int H3ImageMaxImages = 16;

    // Сколько фото персонажа H3-Image берёт как одного человека (замер 2026-11-11, «Андрей», 2 сида):
    // схожесть 0,603 на одном фото, 0,602–0,644 на трёх, 0,633–0,634 на пяти при 82–134 с против 48–78 с.
    // На всех — один человек; пять фото не прибавляют сходства, а время почти удваивают
    public const int H3ImageCharacterPhotos = 3;

    // Fibo 2.0 обучена на правке по одной картинке и по образцам до четырёх (карточка модели)
    public const int Fibo2MaxImages = 4;

    public static bool IsKnown(string? model) => model is null or Quality or Turbo or Klein or H3Image or Fibo2 or Fibo2Turbo or Krea2Turbo or Moss;

    // Модели, которые берут персонажа сами — по фото, без LoRA (LoRA обучены под Qwen-Image)
    public static bool TakesCharacterByPhotos(string? model) => model is Klein or H3Image;

    public static bool IsFibo2(string? model) => model is Fibo2 or Fibo2Turbo;

    // Модели без входных картинок: правка, образцы, маски и персонаж для них — запуск Qwen
    public static bool TextOnly(string? model) => model is Krea2Turbo or Moss;

    // Модель, на которой запуск пойдёт на самом деле. Klein, H3-Image и Fibo 2.0 не проверены на
    // подстановке лица (кроп), масках, дорисовке за края и длинных наборах входов — такие запуски
    // уходят на Qwen «качественно». Персонажа Fibo 2.0 по фото не проверяли, а LoRA обучены под
    // Qwen-Image — запуск с LoRA персонажа тоже уходит на Qwen. Krea 2 и MOSS-Image картинок на
    // входе не берут: всё, кроме генерации по тексту, у них идёт на Qwen
    public static string? Effective(LocalImageRequest request) =>
        ((TakesCharacterByPhotos(request.Model) || IsFibo2(request.Model)) && NeedsQwen(request))
        || (IsFibo2(request.Model) && request.Lora is not null)
        || (TextOnly(request.Model) && (request.Op != LocalImageOp.Generate || (request.Images?.Count ?? 0) > 0
                                        || request.Lora is not null || NeedsQwen(request)))
            ? Quality
            : request.Model;

    public static bool NeedsQwen(LocalImageRequest request) =>
        request.Crop is not null
        || request.EraseMask is { Length: > 0 }
        || request.KeepMask is { Length: > 0 }
        || request.VeilMask is { Length: > 0 }
        || request.Pad is not null
        || (request.Images?.Count ?? 0) > MaxImagesOf(request.Model);

    public static int MaxImagesOf(string? model) => model switch
    {
        H3Image => H3ImageMaxImages,
        Fibo2 or Fibo2Turbo => Fibo2MaxImages,
        _ => KleinMaxImages,
    };
}

// Images: у Edit первая — холст, дальше образцы (до 16 всего); у Generate — необязательные
// образцы (тогда граф правки на пустом холсте размера Aspect); у FaceDetail — ровно одна.
// Aspect — ключ таблицы размеров Qwen-Image («1:1», «16:9» …); null — по холсту или 1:1.
// EraseMask — только у Edit: белое на маске закрашивается на холсте нейтральным серым до
// модели («удали» кистью). Маску-образец Qwen-Image не понимает, а серую заливку — да.
// KeepMask — только у Edit: вне маски вернуть исходник (схожесть мерится внутри маски).
// Несовместима с Pad; со стиранием кистью склеивается по той же маске (дефект f0903cb5);
// маска — PNG того же размера, что холст
// Crop — только у Edit, вместе с KeepMask (ADR-028, решение 12): модели не показываем маску,
// а даём кроп вокруг цели; результат вклеивается по той же маске
// VeilMask — только у Edit (ADR-028, решение 12): «вуаль» для кисти. Белую маску-образец
// Qwen-Image перерисовывает в результат (белые полосы, заливка вне мазка), поэтому модели её не
// показываем: место правки закрашивается полупрозрачным серым (128, α 0,6) на отдельной копии
// холста (modelCanvas), а склейка «вне маски — исходник» идёт по KeepMask с ЧИСТЫМ холстом.
// Несовместима с EraseMask, Pad и Crop (подстановка на лицо остаётся на кропе)
// Pad — только у Edit: холст расширяется полями нейтрального серого до модели («Дорисовать за
// края»), результат — в пропорции расширенного холста
// Model — локальная модель (LocalImageModels); null — Qwen «качественно», прежнее поведение.
// Klein в случаях из LocalImageModels.NeedsQwen идёт на Qwen «качественно»
// Lora — только у Edit и у Generate с образцами; графы Klein её не применяют
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
    byte[]? KeepMask = null,
    LocalImageCrop? Crop = null,
    byte[]? VeilMask = null,
    string? Model = null,
    // Метка происхождения прогона (шаг 23): null — вызывающая сторона ещё не передаёт (шаги 28–30).
    LocalJobOrigin? Origin = null,
    // JSON-описание сцены Fibo 2.0 (VGL, Ш8): только у Generate модели fibo2 без картинок. Задано — граф рисует
    // по нему без расширителя (Prompt — лишь подпись запуска); с VglChange расширитель сначала переписывает его
    // под одно изменение. Сид и размер — у вызывающего (те же, что у версии, от которой доводка)
    string? Vgl = null,
    LocalVglChange? VglChange = null);

// Доводка по JSON-описанию Fibo 2.0: Field — ключ Fibo2VglFields, Value — новое значение словами человека
public sealed record LocalVglChange(string Field, string Value);

// Поля панели «Доводка» (Ш8): ключ → что меняется, словами для расширителя VGL (он пишет JSON по-английски).
// Набор — из постановки: время суток и свет, ракурс и камера, одежда или цвет главного объекта, фон, стиль
public static class Fibo2VglFields
{
    public const int MaxValueLength = 300;

    public static readonly IReadOnlyDictionary<string, string> Aspects = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["light"] = "time of day and lighting",
        ["camera"] = "camera angle, shot and lens",
        ["subject"] = "clothing or colour of the main subject",
        ["background"] = "background and setting",
        ["style"] = "style and medium of the image",
    };
}

// LoRA персонажа: имя файла в ComfyUI и сила применения
public sealed record LocalImageLora(string Name, double Strength);

// Поля в пикселях холста по сторонам
public sealed record LocalImagePad(int Left, int Top, int Right, int Bottom);

// Кроп вокруг цели (ADR-028, решение 12): модель видит кусок холста (X, Y, Width×Height),
// увеличенный до ScaledWidth×ScaledHeight (только вверх, кратно 16); результат приводится
// обратно к Width×Height и вклеивается в X, Y по той же маске. Только у Edit, вместе с KeepMask
public sealed record LocalImageCrop(int X, int Y, int Width, int Height, int ScaledWidth, int ScaledHeight);

// Busy — очередь GPU занята или ComfyUI не ответил: это «попробуй позже», а не ошибка запроса
public sealed record LocalImageSubmitted(string? Ticket, int? QueuePosition, int? EtaSeconds, string? Error, bool Busy = false)
{
    public static LocalImageSubmitted Fail(string error, bool busy = false) => new(null, null, null, error, busy);
}

public enum LocalImageState { Queued, Running, Completed, Failed }

// Warning — временный сбой опроса (ComfyUI не ответил), задача при этом жива; Gpu — снимок очереди
// GPU (шаг 26; пока null)
public sealed record LocalImagePoll(
    LocalImageState State,
    int? QueuePosition,
    IReadOnlyList<LocalImageFile> Files,
    string? Error,
    string? Warning = null,
    LocalGpuQueueInfo? Gpu = null);

// Similarity — сходство лица с эталоном (0..1); null — не считалось или лицо не найдено
// Vgl — JSON-описание сцены, по которому Fibo 2.0 нарисовала картинку (Ш8); null — граф без расширителя VGL
public sealed record LocalImageFile(byte[] Bytes, string ContentType, double? Similarity = null, string? Vgl = null);

// Poses — по одному на вход, в том же порядке; null — лицо не найдено.
// Busy — очередь занята или ComfyUI не ответил: «попробуй позже», фото не помечаются
public sealed record LocalFacePoseResult(IReadOnlyList<LocalFacePose?>? Poses, string? Error, bool Busy = false)
{
    public static LocalFacePoseResult Fail(string error, bool busy = false) => new(null, error, busy);
}

// Углы головы в градусах; осям соответствуют имена полей, а не позиции
public sealed record LocalFacePose(double Yaw, double Pitch, double Roll);

// Лицо, найденное детектором (ADR-028): N — номер с 1 (индекс в списке + 1, присваивает
// бэкенд, воркер номеров не отдаёт), X1/Y1/X2/Y2 — bbox в пикселях, Score — уверенность 0..1,
// Small — короткая сторона < 64 px
public sealed record LocalDetectedFace(int N, int X1, int Y1, int X2, int Y2, double Score, bool Small);

// Результат детекции лиц: Width/Height — размер картинки по ответу воркера, Faces — в порядке
// ответа; Error — сбой или неожиданный ответ, Busy — очередь занята или ComfyUI не ответил
// («попробуй позже»)
public sealed record LocalFaceDetectResult(int Width, int Height, IReadOnlyList<LocalDetectedFace> Faces,
    string? Error = null, bool Busy = false);
