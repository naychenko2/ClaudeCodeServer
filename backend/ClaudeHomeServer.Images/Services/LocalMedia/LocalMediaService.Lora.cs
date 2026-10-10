using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

public sealed partial class LocalMediaService
{
    // Шаги и скорость обучения пресета standard (ADR-026 §3): 2000 шагов, ≈2,35 с на шаг
    public const int LoraTrainSteps = 2000;
    public const double LoraTrainSecondsPerStep = 2.35;

    // Полное время обучения по пресету: шаги × время шага + старт (LocalMediaOptions.LoraTrainStartSeconds);
    // неизвестный пресет — null. Пробные картинки ai-toolkit в продукте выключены (ADR-026)
    public static int? LoraTrainEta(string preset, int startSeconds) =>
        preset == LoraTrainPresets.Standard ? LoraTrainRemaining(0, LoraTrainSteps) + startSeconds : null;

    // Остаток по известному шагу
    public static int LoraTrainRemaining(int step, int totalSteps) =>
        (int)Math.Ceiling(Math.Max(0, totalSteps - step) * LoraTrainSecondsPerStep);
}
