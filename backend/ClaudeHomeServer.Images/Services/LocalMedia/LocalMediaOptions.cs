namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Настройки локальной генерации через ComfyUI (секция LocalMedia). Машинные значения —
// только в appsettings.Local.json: по умолчанию сервер выключен, и инстанс без своей GPU
// о нём не узнаёт. Читаются ЖИВЬЁМ на каждый вызов (IConfiguration с reloadOnChange):
// включение не требует рестарта, а конфиг хода меняется штатно, со сменой сигнатуры.
public sealed class LocalMediaOptions
{
    public const string Section = "LocalMedia";

    public bool Enabled { get; set; }

    // Бэкенд ходит только на loopback: ComfyUI слушает без авторизации
    public string ComfyUrl { get; set; } = "http://127.0.0.1:8188";

    // Незавершённых задач одного владельца одновременно: GPU одна на всех
    public int MaxQueuedPerOwner { get; set; } = 2;

    // Общий потолок очереди ComfyUI (идущая + ждущие, включая чужие прогоны стенда):
    // выше — честный отказ, а не задача, которая ждёт полчаса
    public int MaxComfyQueue { get; set; } = 4;

    // Видео длиннее — десятки минут GPU (15 с ≈ 30 мин), в первой версии не даём
    public int MaxVideoSeconds { get; set; } = 10;

    // Ускоренный режим видео (sol-attn, режим AS бенча), когда агент не передал fast явно.
    // Временно false: значение фиксируется по замерам качества (задача d653be60)
    public bool VideoFastDefault { get; set; }

    // Донастройка апскейла до 1440p: single — одним проходом, tiled — тайлами (MMH3SplitUpscale).
    // 2K идёт только тайлами. Временно single — до итогов тех же замеров
    public string Upscale1440Mode { get; set; } = "single";

    public bool Upscale1440Tiled => string.Equals(Upscale1440Mode?.Trim(), "tiled", StringComparison.OrdinalIgnoreCase);

    // Период опроса /history ожиданием и фоновым коллектором
    public int PollIntervalMs { get; set; } = 3000;

    // Сколько дней помнить завершённые задачи (файлы в проекте остаются)
    public int JobRetentionDays { get; set; } = 7;

    public static LocalMediaOptions Read(IConfiguration config)
    {
        var options = new LocalMediaOptions();
        config.GetSection(Section).Bind(options);
        return options;
    }

    public static bool IsEnabled(IConfiguration config) =>
        config.GetValue($"{Section}:Enabled", false);
}
