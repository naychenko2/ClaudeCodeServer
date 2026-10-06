using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.AudioEditor;

// Контракт драйвера звука (ADR-021 §2) по образцу IImageEditor. Отказ — результат с причиной
// (AudioOutcome), а не исключение: экрану ошибки нужно различать «сервис не ответил», «не хватает
// кредитов» и «в этой области поставщик не работает». Исключение наружу — только отмена снаружи.
public interface IAudioEngine
{
    // Ключ поставщика: "local", позже "fal", "higgsfield", "yandex", "dsp"
    string Key { get; }

    // Подпись поставщика в списке «Поставщик ▾»
    string Label { get; }

    // Единица цены поставщика: AudioPriceUnits.*
    string PriceUnit { get; }

    // Поставщик доступен СЕЙЧАС; запускать можно только доступного
    bool Enabled { get; }

    // Заведён на этой машине, даже если сейчас лежит (смысл — как у IImageEditor.Registered)
    bool Registered => Enabled;

    // Курируемые модели с возможностями; пункт «Авто» добавляет каталог (AudioCatalog.AutoModelId)
    IReadOnlyList<AudioModelInfo> Models { get; }

    // Причина, по которой поставщик не работает в этой области; null — работает. Вызывающий обязан
    // спросить ДО чтения файлов проекта: отказ области — до диска
    string? ScopeRefusal(AudioEditScope scope) => null;

    Task<AudioResult> RunAsync(AudioRequest req, IProgress<AudioProgress> progress, CancellationToken ct);

    // Отмена у поставщика — лучшее усилие
    Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct);

    // Поставщик с живым каталогом (Higgsfield) подтягивает его здесь; исполнитель зовёт до подбора модели.
    // Сбой не бросает: остаётся прежний список
    ValueTask RefreshModelsAsync(CancellationToken ct) => ValueTask.CompletedTask;

    // Источник траты в общем учёте (SpendSources): по умолчанию ключ поставщика
    string SpendSource => Key;

    // Ключи частных параметров (Params), которые модель принимает в операции: по ним тулсет агента
    // отказывает на неизвестном ключе с именем поля (ADR-021 §2). Значения проверяет сам драйвер.
    // null — схемы нет: частных параметров у модели нет
    IReadOnlySet<string>? ParamNames(AudioModelInfo model, AudioOp op) => null;

    // Готовый диктор в параметры модели (у каждого поставщика свои имена: speaker, voice, voice_id).
    // null — у модели в этой операции готовых дикторов нет
    JsonObject? VoiceParams(AudioModelInfo model, AudioOp op, string voice) => null;

    // Дикторы поставщика для выбора голоса (инструмент агента audio_voices). null — списка нет
    Task<IReadOnlyList<AudioVoiceInfo>?> ListVoicesAsync(string? model, string? language, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AudioVoiceInfo>?>(null);

    // Голоса из библиотеки «Голоса» (ADR-021 §5): причина, по которой поставщик не берёт их ни в одной
    // модели (каталог показывает его серым для голоса); null — берёт
    string? LibraryVoicesRefusal => $"Поставщик «{Label}» не умеет голоса из библиотеки";

    // Причина, по которой модель не возьмёт этот голос в операции; null — возьмёт (голос едет в AudioRequest.Voice)
    string? LibraryVoiceRefusal(AudioModelInfo model, AudioOp op, AudioVoiceUse voice) =>
        LibraryVoicesRefusal ?? $"Модель «{model.Label}» не берёт голос из библиотеки";

    // Клон, который живёт у поставщика ограниченно (MiniMax удаляет его через 7 дней) и создаётся только
    // кнопкой человека с ценой: ключ кеша (VoiceProviders.*) или null. Creates — модель сама и создаёт клон
    (string Key, bool Creates)? StoredClone(AudioModelInfo model, AudioOp op) => null;
}

// Диктор поставщика: Id — то, что передаётся в voice у audio_generate; Roles — амплуа (у Яндекса)
public sealed record AudioVoiceInfo(string Id, string Name, string? Language = null, string? Gender = null,
    IReadOnlyList<string>? Roles = null);

// Операции модуля. Операции без ИИ (Trim и дальше) — монтаж за швом IAudioDsp (этап 5):
// новая версия, а не шаг версии; моделей у них нет, список — AudioOps.NoAi. Concat — склейка кусков
// в новый файл: результат — новая нить, а не версия одного из кусков (AudioConcatService)
public enum AudioOp
{
    // Голос
    Speak, DesignVoice, CloneVoice, ConvertVoice, TrainVoice, Dialogue,
    // Музыка
    Song, Cover, Repaint, Outpaint, Extract, Lego, Complete, Sfx,
    // Обработка
    Separate, Denoise, Upsample, Master, Transcribe, ToMidi, Align,
    // Без ИИ
    Trim, GainFade, Normalize, MixStems, Concat,
}

public static class AudioOps
{
    public static readonly IReadOnlySet<AudioOp> NoAi =
        new HashSet<AudioOp> { AudioOp.Trim, AudioOp.GainFade, AudioOp.Normalize, AudioOp.MixStems, AudioOp.Concat };

    public static bool IsNoAi(AudioOp op) => NoAi.Contains(op);
}

// Вид голоса модели: готовый диктор, голос по описанию словами, клон по образцу, голос-элемент
// воркспейса поставщика, обученная модель RVC (.pth + .index)
public enum AudioVoiceKind { Preset, Description, Clone, Element, Rvc }

public enum AudioOutcome { Ok, Failed, InsufficientCredits, Rejected, Cancelled, Unavailable }

public enum AudioStage { Queued, Running, Downloading }

// Ограничение лицензии, которое показывает значок: NonCommercial — CC BY-NC, Copyleft — GPL,
// Watermark — водяной знак в результате, Unknown — лицензия не указана
public enum AudioLicenseKind { Permissive, NonCommercial, Copyleft, Watermark, Unknown }

public sealed record AudioLicense(string Label, AudioLicenseKind Kind);

public static class AudioLicenses
{
    public static readonly AudioLicense Apache2 = new("Apache-2.0", AudioLicenseKind.Permissive);
    public static readonly AudioLicense Mit = new("MIT", AudioLicenseKind.Permissive);
    public static readonly AudioLicense MitApache2 = new("MIT / Apache-2.0", AudioLicenseKind.Permissive);
    public static readonly AudioLicense CcByNc4 = new("CC BY-NC 4.0", AudioLicenseKind.NonCommercial);
    public static readonly AudioLicense Gpl3 = new("GPL-3.0", AudioLicenseKind.Copyleft);
    public static readonly AudioLicense NotStated = new("не указана", AudioLicenseKind.Unknown);
    // Закрытая модель за API поставщика: результат можно использовать коммерчески по его условиям
    public static readonly AudioLicense Commercial = new("коммерческая · условия поставщика", AudioLicenseKind.Permissive);
    public static readonly AudioLicense CommercialWatermark = new("коммерческая · водяной знак", AudioLicenseKind.Watermark);
    public static readonly AudioLicense MitWatermark = new("MIT · водяной знак Perth", AudioLicenseKind.Watermark);
    public static readonly AudioLicense SynthId = new("коммерческая · водяной знак SynthID", AudioLicenseKind.Watermark);
}

// Единицы цены: за символ, секунду, минуту, запуск; кредиты Higgsfield, рубли Яндекса, бесплатно.
// Usd — единица поставщика с ценой в долларах и разной единицей у моделей (fal)
public static class AudioPriceUnits
{
    public const string Usd = "usd";
    public const string Chars = "chars";
    public const string Sec = "sec";
    public const string Min = "min";
    public const string Run = "run";
    public const string Credits = "credits";
    public const string Rub = "rub";
    public const string Free = "free";
}

// Какие файлы даёт модель — роли версии звука (LocalAudioRoles: main, stem:<имя>, score …)
public static class AudioOutputs
{
    public const string Audio = "main";
    public const string Stems = "stem";
    public const string Score = "score";
    public const string Subtitles = "subtitles";
    public const string Lyrics = "lyrics";
    public const string Text = "text";
    public const string Midi = "midi";
    public const string Model = "model";
    public const string Index = "index";
}

// Что разделяет модель операции separate: вокал и минус, 4 или 6 стемов, караоке (основной вокал
// отдельно от бэк-вокала и музыки). Панель «Звук» выбирает модель по этому набору, а не по имени
public static class AudioStemSets
{
    public const string Vocals = "vocals";
    public const string Four = "4";
    public const string Six = "6";
    public const string Karaoke = "karaoke";
}

// Возможности МОДЕЛИ. MaxTextChars — текст озвучки или слова песни; Min/MaxDurationSec — длина
// результата, который модель порождает; InputMaxSec — потолок входного звука (образец, трек).
// Languages — коды ISO; LanguageNeutral — модель работает со звуком, а не с речью (стемы, денойз),
// язык ей безразличен. HeavyOps — операции, которые держат GPU «одна за раз»; StemSet — набор стемов
// (AudioStemSets) у моделей разделения, у остальных null
public sealed record AudioCaps(
    IReadOnlyList<AudioOp> Ops,
    IReadOnlyList<string> Languages,
    IReadOnlyList<AudioVoiceKind> VoiceKinds,
    IReadOnlyList<string> ProducesFiles,
    AudioLicense License,
    string PriceUnit,
    int? MaxTextChars = null,
    int? MinDurationSec = null,
    int? MaxDurationSec = null,
    int? InputMaxSec = null,
    bool LanguageNeutral = false,
    IReadOnlyList<AudioOp>? HeavyOps = null,
    string? StemSet = null)
{
    // Признак «умеет ru» для каталога: русский в языках модели или язык ей безразличен
    public bool SpeaksRu => LanguageNeutral || Languages.Contains("ru");

    public bool IsHeavy(AudioOp op) => HeavyOps?.Contains(op) == true;
}

// Ориентир цены для каталога; точная сумма — только в котировке
public sealed record AudioPriceHint(double Amount, string Unit, string Per);

// DisabledReason — модель видна в каталоге серой с этой причиной, но не подбирается и не запускается
public sealed record AudioModelInfo(string Id, string Label, AudioCaps Caps, AudioPriceHint? PriceHint = null,
    string? DisabledReason = null);

public sealed record AudioBytes(byte[] Bytes, string ContentType);

// Запрос драйверу. Scope — область чата: её проверяет ScopeRefusal до чтения входов. Model — id из
// каталога поставщика (не «Авто»: его разворачивает каталог). Params — частные параметры операции под
// именами поставщика (у local — как у инструментов local-media: speaker, voice, track, bpm …).
// Source — исходный звук, Reference — образец голоса или трек-эталон мастеринга, Clips — записи для
// обучения голоса, VoiceModel/VoiceIndex — .pth и .index RVC; Start/EndSec — кусок для repaint
public sealed record AudioRequest(
    AudioOp Op,
    string Model,
    AudioEditScope Scope,
    string? Text = null,
    string? Prompt = null,
    string? Lyrics = null,
    string? Language = null,
    int? DurationSec = null,
    double? StartSec = null,
    double? EndSec = null,
    JsonObject? Params = null,
    AudioBytes? Source = null,
    AudioBytes? Reference = null,
    IReadOnlyList<AudioBytes>? Clips = null,
    byte[]? VoiceModel = null,
    byte[]? VoiceIndex = null,
    long? Seed = null,
    AudioVoiceUse? Voice = null);

// Role — роль файла версии (LocalAudioRoles.*); Extension — с точкой
public sealed record AudioFile(string Role, byte[] Bytes, string ContentType, string Extension);

public sealed record AudioCost(double Amount, string Unit);

public sealed record AudioProgress(AudioStage Stage, int? QueuePosition = null, int? EtaSeconds = null);

// Charged: true — списано, false — точно не списано, null — неизвестно. VoiceCache — привязки голоса из
// библиотеки, созданные этим запуском (кешируются и при сбое: клон уже оплачен)
public sealed record AudioResult(
    AudioOutcome Outcome,
    IReadOnlyList<AudioFile> Files,
    AudioCost? ActualCost,
    bool? Charged,
    string? RemoteId,
    string? Error,
    IReadOnlyList<AudioVoiceCacheEntry>? VoiceCache = null)
{
    public static AudioResult Fail(AudioOutcome outcome, string error, bool? charged = false) =>
        new(outcome, [], null, charged, null, error);
}
