using ClaudeHomeServer.Services.VideoEditor.Contracts;

namespace ClaudeHomeServer.Services.VideoEditor.Catalog;

// Каталог модуля «Видео» (ADR-022 §3) — порядок поставщиков, «Авто» и подбор модели под сцену. Сами модели
// отдают драйверы (IVideoEngine.Models): local и fal курируемые, Higgsfield — живой список.
public static class VideoCatalog
{
    public const string AutoModelId = "auto";
    public const string AutoModelLabel = "Авто";

    // Порядок поставщиков в списке: local последний намеренно — доступность GPU мигает, и «Авто» с local
    // первым прыгало бы в облако. Сдвигает его вперёд только AutoCandidates по флагу владельца
    public static readonly string[] ProviderOrder = ["fal", "higgsfield", "local"];

    public const string LocalKey = "local";

    public static int OrderOf(string key)
    {
        var i = Array.FindIndex(ProviderOrder, k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? int.MaxValue : i;
    }

    public static bool IsAuto(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), AutoModelId, StringComparison.OrdinalIgnoreCase);

    // Доступные поставщики в порядке показа
    public static IReadOnlyList<IVideoEngine> Available(IEnumerable<IVideoEngine> engines) =>
        [.. engines.Where(e => Safe(() => e.Enabled)).OrderBy(e => OrderOf(e.Key)).ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase)];

    // Заведённые поставщики (в том числе лежащие сейчас) в порядке показа
    public static IReadOnlyList<IVideoEngine> Registered(IEnumerable<IVideoEngine> engines) =>
        [.. engines.Where(e => Safe(() => e.Registered)).OrderBy(e => OrderOf(e.Key)).ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase)];

    // Кандидаты «Авто» в порядке перебора: доступные и работающие в этой области. С флагом владельца
    // local-media-default локальные модели идут первыми: человек просил по умолчанию свою видеокарту.
    // ЕДИНСТВЕННАЯ точка порядка «Авто» — её зовут и котировка, и каталог для фронта
    public static IReadOnlyList<IVideoEngine> AutoCandidates(IEnumerable<IVideoEngine> engines, VideoEditScope scope,
        bool preferLocal) =>
        [.. Available(engines).Where(e => Safe(() => e.ScopeRefusal(scope) is null))
            .OrderBy(e => preferLocal && string.Equals(e.Key, LocalKey, StringComparison.OrdinalIgnoreCase) ? 0 : 1)];

    // Что нужно сцене от модели: какие кадры есть и какая длительность запрошена
    public sealed record Need(bool FrameA, bool FrameB, int DurationSec);

    // Модель поставщика под сцену: «Авто» — первая подходящая, явная — только она. Серая модель не берётся
    public static VideoModelInfo? Pick(IVideoEngine engine, string? modelId, Need need) =>
        IsAuto(modelId)
            ? engine.Models.FirstOrDefault(m => Fits(m, need))
            : engine.Models.FirstOrDefault(m => string.Equals(m.Id, modelId!.Trim(), StringComparison.OrdinalIgnoreCase) && Fits(m, need));

    // Модель подходит: нет второго кадра без первого; кадры берёт модель (нет кадров — модель «только текст»);
    // второй кадр — только модели с LastFrame; длительность — из допустимых
    public static bool Fits(VideoModelInfo model, Need need)
    {
        if (model.DisabledReason is not null) return false;
        if (need.FrameB && !need.FrameA) return false;
        if (need.FrameA && !model.Caps.FirstFrame) return false;
        if (!need.FrameA && model.Caps.FirstFrame) return false;
        if (need.FrameB && !model.Caps.LastFrame) return false;
        if (model.Caps.LastFrameRequired && !need.FrameB) return false;
        return model.Caps.Durations.Count == 0 || model.Caps.Durations.Contains(need.DurationSec);
    }

    // Сбой проверки доступности — поставщик недоступен, а не 500 у всего каталога
    internal static bool Safe(Func<bool> probe)
    {
        try { return probe(); }
        catch { return false; }
    }

    internal static string? Safe(Func<string?> probe, string fallback)
    {
        try { return probe(); }
        catch { return fallback; }
    }
}

// Каталог для полосы и панели: заведённые поставщики в порядке показа, у каждого — доступен ли он В ЭТОЙ
// области и почему нет. Недоступный остаётся в списке серым с причиной, а не пропадает
public static class VideoCatalogView
{
    public const string UnavailableReason = "Поставщик сейчас недоступен";

    public static VideoCatalogDto Build(IEnumerable<IVideoEngine> engines, VideoEditScope scope, bool preferLocal = false)
    {
        var all = engines.ToList();
        return new VideoCatalogDto(
            [.. VideoCatalog.Registered(all).Select(e =>
            {
                var reason = VideoCatalog.Safe(() => e.Enabled)
                    ? VideoCatalog.Safe(() => e.ScopeRefusal(scope), UnavailableReason)
                    : UnavailableReason;
                return new VideoProviderDto(e.Key, e.Label, e.PriceUnit, reason is null, reason,
                    [.. Models(e).Select(ToDto)]);
            })],
            VideoCatalog.AutoModelId, VideoThreadLimits.MaxCount,
            [.. VideoCatalog.AutoCandidates(all, scope, preferLocal).Select(e => e.Key)]);
    }

    private static IReadOnlyList<VideoModelInfo> Models(IVideoEngine engine)
    {
        try { return engine.Models; }
        catch { return []; }
    }

    private static VideoModelDto ToDto(VideoModelInfo m) =>
        new(m.Id, m.Label, m.Caps.Durations, m.Caps.Aspects, m.Caps.Sound, m.Caps.LastFrame, m.Caps.License.Label);
}

public static class VideoThreadLimits
{
    public const int MaxCount = Scenes.VideoThreadStore.MaxCount;
}
