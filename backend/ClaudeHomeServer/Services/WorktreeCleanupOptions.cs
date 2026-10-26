namespace ClaudeHomeServer.Services;

// Настройки уборки worktree и /tmp — секция «WorktreeCleanup» (машинные значения — в
// appsettings.Local.json). Уборка стирает только воспроизводимое (bin/obj/dist/node_modules,
// временные каталоги тестов); снос самого worktree ограничен «влит + чисто + простаивает».
//
// "WorktreeCleanup": {
//   "Enabled": true,
//   "IdleHours": 24,              // worktree без процессов и правок столько часов — чистим
//   "NightFromHour": 2,           // ночное окно полного прохода (локальное время)
//   "NightToHour": 6,
//   "RemoveMergedWorktrees": true,// снимать worktree, чья ветка влита и дерево чистое
//   "CleanNodeModules": true,
//   "TmpMaxAgeHours": 2,
//   "MinFreeGb": 100,             // свободно на корне меньше — уведомление админам
//   "AlertCooldownHours": 12
// }
public class WorktreeCleanupOptions
{
    public bool Enabled { get; init; } = true;
    public int IdleHours { get; init; } = 24;
    public int NightFromHour { get; init; } = 2;
    public int NightToHour { get; init; } = 6;
    public bool RemoveMergedWorktrees { get; init; } = true;
    public bool CleanNodeModules { get; init; } = true;
    public int TmpMaxAgeHours { get; init; } = 2;
    public int MinFreeGb { get; init; } = 100;
    public int AlertCooldownHours { get; init; } = 12;

    public static WorktreeCleanupOptions From(IConfiguration config)
    {
        const string s = "WorktreeCleanup:";
        return new WorktreeCleanupOptions
        {
            Enabled = config.GetValue<bool?>(s + "Enabled") ?? true,
            IdleHours = Clamp(config.GetValue<int?>(s + "IdleHours"), 1, 24 * 30, 24),
            NightFromHour = Clamp(config.GetValue<int?>(s + "NightFromHour"), 0, 23, 2),
            NightToHour = Clamp(config.GetValue<int?>(s + "NightToHour"), 0, 23, 6),
            RemoveMergedWorktrees = config.GetValue<bool?>(s + "RemoveMergedWorktrees") ?? true,
            CleanNodeModules = config.GetValue<bool?>(s + "CleanNodeModules") ?? true,
            TmpMaxAgeHours = Clamp(config.GetValue<int?>(s + "TmpMaxAgeHours"), 1, 24 * 7, 2),
            MinFreeGb = Clamp(config.GetValue<int?>(s + "MinFreeGb"), 1, 100_000, 100),
            AlertCooldownHours = Clamp(config.GetValue<int?>(s + "AlertCooldownHours"), 1, 24 * 7, 12),
        };
    }

    private static int Clamp(int? value, int min, int max, int fallback) =>
        value is { } v && v >= min && v <= max ? v : fallback;

    /// <summary>Ночное окно (может переходить через полночь).</summary>
    public bool IsNight(DateTime local) =>
        NightFromHour <= NightToHour
            ? local.Hour >= NightFromHour && local.Hour < NightToHour
            : local.Hour >= NightFromHour || local.Hour < NightToHour;
}
