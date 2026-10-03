using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.VideoEditor.Catalog;

// Каталог fal для видео (ADR-022 §3): курируемые image-to-video эндпоинты с «кадр A → кадр B» и НАША
// раскладка общих полей запроса на поля fal. Id модели — id эндпоинта, он же уходит в подпись траты.
// Схемы и цены сверены с живым каталогом fal 2026-10-02 (get_model_schema, get_pricing); ориентир цены —
// доллары за секунду ролика, точную сумму даёт котировка по прайсу fal. Модель вне отбора — одна строка здесь.
public static class FalVideoCatalog
{
    public const string FluxDraft = "blackforestlabs/flux-3/first-last-frame-to-video/draft";
    public const string VeoLite = "fal-ai/veo3.1/lite/first-last-frame-to-video";
    public const string MiniMaxH3 = "minimax/h3/image-to-video";
    public const string KlingO1 = "fal-ai/kling-video/o1/standard/image-to-video";

    // Потолок кадра, который едет data: URI внутри JSON: base64 раздувает его на треть, а тело больше
    // ~30 МБ до fal не доезжает. Kling объявляет 50 МБ, остальные предела не объявляют
    public const long MaxFrameBytes = 20L * 1024 * 1024;

    // Форма длительности в поле fal: число (5), строка («5»), строка с суффиксом («8s»)
    public enum FalDurationForm { Int, String, Seconds }

    // Раскладка: поля кадров, длительности, пропорции, звука и сида; null — модель поле не берёт.
    // LastRequired — без последнего кадра модель не работает (FLF-эндпоинты). Defaults ставятся, если
    // поля нет в Params. ParamNames — частные параметры под именами fal, что пускаем из Params
    // (ссылки, вебхуки и sync_mode не пускаем: на их месте — наши общие поля)
    public sealed record FalVideoModel(
        VideoModelInfo Info,
        string First,
        string? Last,
        bool LastRequired,
        string Duration,
        FalDurationForm DurationForm,
        string? Aspect,
        string? Sound,
        string? Seed,
        IReadOnlySet<string> ParamNames,
        int EtaSeconds,
        bool PriceVaries,
        IReadOnlyDictionary<string, JsonNode?>? Defaults = null);

    private static VideoCaps Caps(int[] durations, string[] aspects, bool sound, bool lastFrame) =>
        new(durations, aspects, sound, lastFrame, VideoLicense.Commercial, VideoPriceUnits.Usd,
            MaxFrameBytes: MaxFrameBytes);

    private static VideoPriceHint PerSec(double usd) => new(usd, VideoPriceUnits.Usd, "sec");

    private static IReadOnlySet<string> Names(params string[] names) => new HashSet<string>(names, StringComparer.Ordinal);

    public static IReadOnlyList<FalVideoModel> All { get; } =
    [
        // Самая дешёвая: черновик FLUX 3, 0,03 $/с, кадр A и кадр B обязательны
        new(new VideoModelInfo(FluxDraft, "FLUX 3 · черновик",
                Caps([5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20],
                    ["auto", "21:9", "2:1", "16:9", "4:3", "1:1", "3:4", "9:16"], sound: true, lastFrame: true),
                PerSec(0.03)),
            First: "start_image_url", Last: "end_image_url", LastRequired: true,
            Duration: "duration", DurationForm: FalDurationForm.Int,
            Aspect: "aspect_ratio", Sound: "generate_audio", Seed: null,
            ParamNames: Names("safety_tolerance"), EtaSeconds: 60, PriceVaries: true),

        // Veo 3.1 Lite: 0,05 $/с за 720p; прайс отдаёт одну цену, а звук и 1080p её меняют
        new(new VideoModelInfo(VeoLite, "Veo 3.1 Lite",
                Caps([4, 6, 8], ["auto", "16:9", "9:16"], sound: true, lastFrame: true),
                PerSec(0.05)),
            First: "first_frame_url", Last: "last_frame_url", LastRequired: true,
            Duration: "duration", DurationForm: FalDurationForm.Seconds,
            Aspect: "aspect_ratio", Sound: "generate_audio", Seed: "seed",
            ParamNames: Names("negative_prompt", "resolution", "auto_fix", "safety_tolerance"),
            EtaSeconds: 90, PriceVaries: true),

        // MiniMax H3: последний кадр необязателен, пропорция — по кадру; по умолчанию у fal 2K-апскейл,
        // мы берём родные 768P (меньше файл, цена по разрешению меняется)
        new(new VideoModelInfo(MiniMaxH3, "MiniMax H3",
                Caps([5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15], [], sound: true, lastFrame: true),
                PerSec(0.05)),
            First: "image_url", Last: "end_image_url", LastRequired: false,
            Duration: "duration", DurationForm: FalDurationForm.Int,
            Aspect: null, Sound: null, Seed: "seed",
            ParamNames: Names("resolution", "prompt_expansion_mode", "enable_safety_checker"),
            EtaSeconds: 120, PriceVaries: true,
            Defaults: new Dictionary<string, JsonNode?> { ["resolution"] = "768P" }),

        // Kling O1 Standard: последний кадр необязателен, без звука, пропорция — по кадру
        new(new VideoModelInfo(KlingO1, "Kling O1 Standard",
                Caps([3, 4, 5, 6, 7, 8, 9, 10], [], sound: false, lastFrame: true),
                PerSec(0.084)),
            First: "start_image_url", Last: "end_image_url", LastRequired: false,
            Duration: "duration", DurationForm: FalDurationForm.String,
            Aspect: null, Sound: null, Seed: null,
            ParamNames: Names(), EtaSeconds: 150, PriceVaries: false),
    ];

    public static FalVideoModel? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(m => string.Equals(m.Info.Id, id, StringComparison.OrdinalIgnoreCase));
}
