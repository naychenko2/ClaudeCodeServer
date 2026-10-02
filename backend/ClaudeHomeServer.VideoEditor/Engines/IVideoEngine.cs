using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.VideoEditor;

// Контракт драйвера съёмки (ADR-022 §3) по образцу IAudioEngine. Отказ — результат с причиной
// (VideoOutcome), а не исключение: экрану ошибки нужно различать «сервис не ответил», «не хватает
// кредитов» и «в этой области поставщик не работает». Исключение наружу — только отмена снаружи.
public interface IVideoEngine
{
    // Ключ поставщика: "local", "fal", "higgsfield"
    string Key { get; }

    // Подпись поставщика в списке «Исполнитель»
    string Label { get; }

    // Единица цены поставщика: VideoPriceUnits.*
    string PriceUnit { get; }

    // Поставщик доступен СЕЙЧАС; запускать можно только доступного
    bool Enabled { get; }

    // Заведён на этой машине, даже если сейчас лежит
    bool Registered => Enabled;

    // Курируемые модели с возможностями; пункт «Авто» добавляет каталог (VideoCatalog.AutoModelId)
    IReadOnlyList<VideoModelInfo> Models { get; }

    // Причина, по которой поставщик не работает в этой области; null — работает. Вызывающий обязан
    // спросить ДО чтения файлов проекта: отказ области — до диска
    string? ScopeRefusal(VideoEditScope scope) => null;

    Task<VideoResult> RunAsync(VideoRequest req, IProgress<VideoProgress> progress, CancellationToken ct);

    // Отмена у поставщика — лучшее усилие
    Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct);

    // Поставщик с живым каталогом (Higgsfield) подтягивает его здесь; исполнитель зовёт до подбора модели.
    // Сбой не бросает: остаётся прежний список
    ValueTask RefreshModelsAsync(CancellationToken ct) => ValueTask.CompletedTask;

    // Источник траты в общем учёте (SpendSources): по умолчанию ключ поставщика
    string SpendSource => Key;

    // Ключи частных параметров (Params), которые модель принимает: по ним тулсет и ручки отказывают на
    // неизвестном ключе с именем поля. null — частных параметров у модели нет
    IReadOnlySet<string>? ParamNames(VideoModelInfo model) => null;
}

// Котировка драйвера: сумма в единицах поставщика, время и очередь. Драйвер без этого шва котируется
// по PriceHint модели
public interface IVideoQuoter
{
    // Бросает VideoEngineUnavailableException, если поставщик сейчас недоступен или не работает в области
    // запроса: котировка тогда отвечает отказом с причиной, а не цифрой
    Task<VideoEstimate> EstimateAsync(VideoModelInfo model, VideoRequest request, CancellationToken ct);

    // Ожидаемая длительность для процентов на фронте; null — неизвестно
    int? ExpectedSeconds(VideoModelInfo model, VideoRequest request);
}

public sealed class VideoEngineUnavailableException(string message) : Exception(message);

// Amount null — цена станет известна после запуска. Source: provider | catalog | unknown
public sealed record VideoEstimate(
    double? Amount,
    string Unit,
    bool Approx,
    string Source,
    int? EtaSeconds = null,
    int? QueueLength = null);

public static class VideoEstimateSources
{
    public const string Provider = "provider";
    public const string Catalog = "catalog";
    public const string Unknown = "unknown";
}

public enum VideoOutcome { Ok, Failed, InsufficientCredits, Rejected, Cancelled, Unavailable }

public enum VideoStage { Queued, Running, Downloading }

// Ограничение лицензии: Permissive — коммерческое использование по условиям поставщика, NonCommercial,
// Watermark — водяной знак в результате, Unknown — лицензия не указана
public enum VideoLicenseKind { Permissive, NonCommercial, Watermark, Unknown }

public sealed record VideoLicense(string Label, VideoLicenseKind Kind)
{
    public static readonly VideoLicense Commercial = new("коммерческая · условия поставщика", VideoLicenseKind.Permissive);
    public static readonly VideoLicense CommercialWatermark = new("коммерческая · водяной знак", VideoLicenseKind.Watermark);
    public static readonly VideoLicense NotStated = new("не указана", VideoLicenseKind.Unknown);
}

// Единицы цены: секунда ролика (fal), кредиты Higgsfield, бесплатно (local)
public static class VideoPriceUnits
{
    public const string Usd = "usd";
    public const string Sec = "sec";
    public const string Credits = "credits";
    public const string Free = "free";
}

// Возможности МОДЕЛИ: Durations — допустимые длительности, с; Aspects — пропорции ("16:9"); Sound — умеет
// звук в ролике; LastFrame — берёт и последний кадр («кадр A → кадр B»); FirstFrame — берёт ли первый
// (false — только текст); MaxFrameBytes — потолок входного кадра
public sealed record VideoCaps(
    IReadOnlyList<int> Durations,
    IReadOnlyList<string> Aspects,
    bool Sound,
    bool LastFrame,
    VideoLicense License,
    string PriceUnit,
    bool FirstFrame = true,
    long? MaxFrameBytes = null,
    bool Heavy = false);

// Ориентир цены для каталога; точная сумма — только в котировке. Per — "sec" | "run"
public sealed record VideoPriceHint(double Amount, string Unit, string Per);

// DisabledReason — модель видна в каталоге серой с этой причиной, но не подбирается и не запускается
public sealed record VideoModelInfo(string Id, string Label, VideoCaps Caps, VideoPriceHint? PriceHint = null,
    string? DisabledReason = null);

public sealed record VideoFrameBytes(byte[] Bytes, string ContentType);

// Запрос драйверу. Scope — область чата: её проверяет ScopeRefusal до чтения входов. Model — id из
// каталога поставщика (не «Авто»: его разворачивает каталог). Frames — байты кадров, которые исполнитель
// уже прочёл через шов; драйвер сам диска проекта не касается. Params — частные параметры под именами поставщика
public sealed record VideoRequest(
    string Model,
    VideoEditScope Scope,
    string Text,
    VideoFrameBytes? FrameA,
    VideoFrameBytes? FrameB,
    int DurationSec,
    string? Aspect = null,
    bool Sound = false,
    JsonObject? Params = null,
    long? Seed = null);

// Клип варианта: байты mp4 и признаки, которые драйвер знает от поставщика (null — неизвестно)
public sealed record VideoFile(byte[] Bytes, string ContentType, string Extension, double? DurationSec = null,
    bool? HasSound = null);

public sealed record VideoCost(double Amount, string Unit);

// Accepted — поставщик принял задачу: с этого момента пишется трата (исполнитель получает событие
// через IProgress с Stage=Queued/Running и RemoteId). Charged: true — списано, false — точно не списано, null — неизвестно
public sealed record VideoProgress(VideoStage Stage, int? QueuePosition = null, int? EtaSeconds = null,
    string? RemoteId = null, bool Accepted = false);

public sealed record VideoResult(
    VideoOutcome Outcome,
    VideoFile? File,
    VideoCost? ActualCost,
    bool? Charged,
    string? RemoteId,
    string? Error)
{
    public static VideoResult Fail(VideoOutcome outcome, string error, bool? charged = false) =>
        new(outcome, null, null, charged, null, error);
}
