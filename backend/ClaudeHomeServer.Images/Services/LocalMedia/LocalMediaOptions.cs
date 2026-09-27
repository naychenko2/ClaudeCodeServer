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
    // По замеру d653be60: картинка та же на глаз, в 1,4–1,6 раза быстрее; меняет тайминг
    // движения, поэтому для точного повтора ролика по seed нужен fast=false
    public bool VideoFastDefault { get; set; } = true;

    // Донастройка апскейла до 1440p: single — одним проходом, tiled — тайлами (MMH3SplitUpscale).
    // 2K идёт только тайлами. По замеру d653be60 tiled быстрее (915 с против 1065) при той же
    // картинке, а single влезает в видеопамять впритык
    public string Upscale1440Mode { get; set; } = "tiled";

    public bool Upscale1440Tiled => string.Equals(Upscale1440Mode?.Trim(), "tiled", StringComparison.OrdinalIgnoreCase);

    // Период опроса /history ожиданием и фоновым коллектором
    public int PollIntervalMs { get; set; } = 3000;

    // Сколько дней помнить завершённые задачи (файлы в проекте остаются)
    public int JobRetentionDays { get; set; } = 7;

    // Хостовые каталоги input и output ComfyUI — для чистки наших промежуточных файлов
    // (входы задач, копии результатов, латенты). Пусто хотя бы одно — чистка выключена
    public string ComfyInputDir { get; set; } = "";
    public string ComfyOutputDir { get; set; } = "";

    public bool CleanupEnabled => !string.IsNullOrWhiteSpace(ComfyInputDir) && !string.IsNullOrWhiteSpace(ComfyOutputDir);

    public static LocalMediaOptions Read(IConfiguration config)
    {
        var options = new LocalMediaOptions();
        config.GetSection(Section).Bind(options);
        return options;
    }

    public static bool IsEnabled(IConfiguration config) =>
        config.GetValue($"{Section}:Enabled", false);
}
