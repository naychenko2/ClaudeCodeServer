using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

public sealed partial class LocalMediaService
{
    // Шаги и скорость обучения пресета standard (ADR-026 §3): 2000 шагов, ≈2,35 с на шаг
    public const int LoraTrainSteps = 2000;
    public const double LoraTrainSecondsPerStep = 2.35;

    // Верхняя оценка до замера 1г: 1 ч 51 мин ручного прогона с пробными картинками и остановленным ComfyUI
    private const int LoraTrainStandardEta = 6660;

    // Полное время обучения по пресету; неизвестный пресет — null
    public static int? LoraTrainEta(string preset) => preset == LoraTrainPresets.Standard ? LoraTrainStandardEta : null;

    // Остаток по известному шагу
    public static int LoraTrainRemaining(int step, int totalSteps) =>
        (int)Math.Ceiling(Math.Max(0, totalSteps - step) * LoraTrainSecondsPerStep);
}
