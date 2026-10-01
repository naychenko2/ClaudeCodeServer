using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Prefs;
using ClaudeHomeServer.Services.AudioEditor.Threads;

namespace ClaudeHomeServer.Services.AudioEditor.Controllers;

// Каталог для полосы и панели: заведённые поставщики в порядке показа, у каждого — доступен ли он
// В ЭТОЙ области и почему нет. Недоступный остаётся в списке серым с причиной, а не пропадает
public sealed record AudioCatalogDto(IReadOnlyList<AudioProviderDto> Providers, string AutoModelId, int MaxCount);

public sealed record AudioProviderDto(
    string Key, string Label, string PriceUnit, bool Available, string? Reason, IReadOnlyList<AudioModelInfo> Models);

public static class AudioCatalogView
{
    public const string UnavailableReason = "Поставщик сейчас недоступен";

    public static AudioCatalogDto Build(IEnumerable<IAudioEngine> engines, AudioEditScope scope) =>
        new([.. AudioCatalog.Registered(engines).Select(e =>
        {
            var reason = Safe(() => e.Enabled) ? Safe(() => e.ScopeRefusal(scope), UnavailableReason) : UnavailableReason;
            return new AudioProviderDto(e.Key, e.Label, e.PriceUnit, reason is null, reason, e.Models);
        })], AudioCatalog.AutoModelId, AudioModePrefs.MaxCount);

    private static bool Safe(Func<bool> probe)
    {
        try { return probe(); }
        catch { return false; }
    }

    private static string? Safe(Func<string?> probe, string fallback)
    {
        try { return probe(); }
        catch { return fallback; }
    }
}

// Префы трёх режимов области: null у режима — человек его ещё не настраивал
public sealed record AudioPrefsDto(AudioModePrefs? Voice, AudioModePrefs? Music, AudioModePrefs? Process);

// Состояние модуля для чата одним запросом: нити (версии, запуски, фокус, ревизия), каталог, префы
public sealed record AudioStateDto(AudioThreadsState Threads, AudioCatalogDto Catalog, AudioPrefsDto Prefs);

// Нить: ровно одно из File (путь в проекте) и DraftFolder (черновик «Новый звук», "" — корень).
// Mode — режим новой нити: её настройки берутся из префов этого режима
public sealed record AudioThreadOpenRequest(string? File, string? DraftFolder, string? Mode, long Revision);

public sealed record AudioThreadFocusRequest(string? ThreadId, long Revision);

public sealed record AudioThreadSettingsRequest(AudioThreadSettings? Settings, long Revision);

public sealed record AudioThreadCurrentRequest(string? VersionId, long Revision);

// Сохранение версии в проект: Mode — nextVersion (по умолчанию) или as; VersionId не задан — текущая
public sealed record AudioSaveRequest(string? VersionId, string? Mode, string? Folder, string? FileName);

// Правка без ИИ: Op — trim | gainFade | normalize | convert, Format — wav | mp3 | flac | ogg (null —
// как у исходного файла). Поля читаются по операции (AudioDspEditInput); BaseVersionId null — текущая
public sealed record AudioDspEditRequest(
    string? Op,
    string? BaseVersionId = null,
    double? StartSec = null,
    double? EndSec = null,
    double? FadeInSec = null,
    double? FadeOutSec = null,
    double? GainDb = null,
    double? TargetLufs = null,
    string? Format = null,
    int? SampleRate = null,
    int? Channels = null,
    long? Revision = null);

// Сведение N из M стемов версии-основы: Stems — роли «stem:<имя>» с громкостью и выключением
public sealed record AudioMixRequest(
    IReadOnlyList<Engines.AudioMixStemInput>? Stems, string? BaseVersionId = null, string? Format = null, long? Revision = null);

// Стык склейки: Kind — butt | pause | crossfade
public sealed record AudioJointRequest(string? Kind, double Seconds = 0);

// Склейка кусков в новый файл (AudioConcatInput): куски — версии нитей этого чата и файлы проекта
public sealed record AudioConcatRequest(
    IReadOnlyList<AudioConcatPiece>? Pieces,
    AudioJointRequest? Joint = null,
    IReadOnlyList<AudioJointRequest?>? Joints = null,
    bool? NormalizeLoudness = null,
    string? Name = null,
    string? Format = null,
    string? Folder = null);

// Поля multipart запуска. Исходный звук сервер берёт сам — главный файл версии-основы нити
// (BaseVersionId, не задана — текущая). Образец голоса или эталон мастеринга — загрузкой Reference
// или путём ReferencePath в проекте; записи для обучения голоса — Clips и ClipPaths; модель RVC —
// VoiceModelPath и VoiceIndexPath. Пути есть только у проекта. Params — JSON-объект частных параметров
public sealed class AudioStartJobForm
{
    public string? QuoteId { get; set; }
    public string? SessionId { get; set; }
    public string? ThreadId { get; set; }
    public string? BaseVersionId { get; set; }
    public string? Text { get; set; }
    public string? Prompt { get; set; }
    public string? Lyrics { get; set; }
    public string? Language { get; set; }
    public int? DurationSec { get; set; }
    public double? StartSec { get; set; }
    public double? EndSec { get; set; }
    public string? Params { get; set; }
    public long? Seed { get; set; }
    public IFormFile? Reference { get; set; }
    public string? ReferencePath { get; set; }
    public List<IFormFile>? Clips { get; set; }
    public List<string>? ClipPaths { get; set; }
    public string? VoiceModelPath { get; set; }
    public string? VoiceIndexPath { get; set; }
}
