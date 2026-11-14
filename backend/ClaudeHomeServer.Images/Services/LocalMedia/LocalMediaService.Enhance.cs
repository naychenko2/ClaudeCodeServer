namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Время «Увеличить» (RelayVSR) по замеру 12–13.11.2026 на RTX 3090 (ролик 5 с): ×2 — 480p 14–17 с, 768p 37 с;
// ×4 — 480p 47–52 с, 768p 140 с (пик VRAM 21,2 ГБ, потому выше 768p не берём — не мерено). Между точками — по площади кадра,
// по длине — пропорционально 5 с
public sealed partial class LocalMediaService
{
    public const int EnhanceMaxSeconds = 10;

    private const double EnhanceBaseSeconds = 5.0;
    private const long Area480 = 864L * 480;
    private const long Area768 = 1344L * 768;

    // Потолок площади кадра — 1344×768 с запасом 2 %: больше VRAM у ×4 не хватит на 24 ГБ карте
    public static bool EnhanceSizeAllowed(int width, int height) =>
        width >= 64 && height >= 64 && (long)width * height <= Area768 * 102 / 100;

    public static int? VideoEnhanceEta(int scale, int width, int height, double seconds)
    {
        if (!ComfyWorkflows.IsRelayVsrScale(scale) || !EnhanceSizeAllowed(width, height)) return null;
        if (!(seconds > 0) || seconds > EnhanceMaxSeconds) return null;
        var (at480, at768) = scale == 2 ? (16.0, 37.0) : (50.0, 140.0);
        var area = (double)width * height;
        var perFive = area <= Area480
            ? at480 * area / Area480
            : at480 + (Math.Min(area, Area768) - Area480) * (at768 - at480) / (Area768 - Area480);
        // Загрузка весов и нод — не меньше нескольких секунд даже у крошечного ролика
        return (int)Math.Ceiling(Math.Max(5.0, perFive) * Math.Max(1.0, seconds / EnhanceBaseSeconds));
    }
}
