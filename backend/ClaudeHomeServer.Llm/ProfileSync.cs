using System.Globalization;
using System.Text.Json;

namespace ClaudeHomeServer.Services.Llm;

// ADR-015 §1: рубильник `Claude:ProfileSync:Mirror` — off | dryRun | on. Дефолт off;
// в этой задаче включаем не дальше dryRun. Удаления (`on`) — отдельный этап с корзиной.
public enum ProfileMirrorMode
{
    Off = 0,
    DryRun = 1,
    On = 2,
}

// ADR-015 §3: манифест того, что синк САМ принёс в профиль. На основании манифеста
// решаем, какие файлы в зеркале можно удалить (три условия: путь в манифесте; в источнике
// его больше нет; в профиле он не изменился после доставки).
internal sealed class ProfileSyncManifest
{
    public int Version { get; set; } = 1;

    // Ключ — относительный путь от корня профиля через "/". Регистр сохраняется,
    // но сравнения регистронезависимы — реальный путь на диске может оказаться в любом регистре.
    public Dictionary<string, ProfileSyncEntry> Files { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class ProfileSyncEntry
{
    public string Zone { get; set; } = "";
    // "host" — из ~/.claude, "defaults" — из claude-defaults (поставка).
    public string Source { get; set; } = "host";
    public long Size { get; set; }
    public DateTime Mtime { get; set; }
}

// Сводка прохода dryRun по одному профилю: что синк удалил бы (но не удалил),
// что реально унесено в корзину (только в режиме on), где файл разошёлся с источником
// (WARN, удалять нельзя), какие зоны выключены fail-safe'ом (нет claude-defaults/{зона}).
public sealed class ProfileMirrorReport
{
    public string Profile { get; set; } = "";
    public int Delivered { get; set; }
    public List<string> WouldDelete { get; set; } = new();
    public List<string> Trashed { get; set; } = new();
    public List<string> Divergent { get; set; } = new();
    public List<string> SkippedZones { get; set; } = new();
}

// Корзина синка профилей (ADR-015 §5.3). Страховка на этап 4 — включать режим `on`
// без корзины нельзя: первый же проход сделал бы голый File.Delete и при ошибке
// троттлинга или неверной логике унёс бы файл навсегда.
//
// Хранение: <root>/{profileKey}/{отметка-UTC}/{отн. путь}. Отметка прохода — это
// ВРЕМЯ УДАЛЕНИЯ, а не mtime файла (mtime — свойство доставки из источника, не
// момента, когда мы решили убрать). Уборка идёт по отметке прохода: всё, что старше
// ретенции, уходит целым каталогом отметки. mtime вложенных файлов не учитывается.
//
// Перенос НЕ удаляет исходник при неудаче: если копия в корзину не положилась — файл
// остаётся в профиле и в манифесте, следующий проход попробует снова. Это требование
// задачи: «файл, который не удалось положить в корзину, не удаляется из профиля».
//
// Корень `.sync-trash` намеренно лежит ВНЕ `claude-profiles/`: вложенная папка сама
// была бы принята за профиль при обходе. Бэкап корзину не берёт (BackupPaths), и в
// профили она не едет (другая корневая папка).
internal sealed class SyncTrashStore
{
    // Форвард на примитив спины: имя папки нужно ещё и бэкапу (BackupPaths исключает
    // корзину из архива), а тянуть ради него internal-тип чужой вертикали нельзя —
    // источник правды переехал в Core (Services/Llm/SyncTrashPaths.cs).
    public const string RootDirName = SyncTrashPaths.RootDirName;

    // 14 дней. Зафиксировано в задаче. mtime файла в корзине мог обмануть (это mtime
    // доставки, а не удаления), поэтому ретенция — по отметке прохода.
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    // Канонический формат отметки прохода UTC: сортируется лексикографически как дата,
    // двоеточие заменено на дефис (Windows не разрешает ':' в имени файла), 'Z' в хвосте
    // маркирует UTC без двусмысленности.
    private const string StampFormat = "yyyy-MM-ddTHH-mm-ssZ";

    private readonly string _rootPath;

    public SyncTrashStore(string rootPath)
    {
        _rootPath = rootPath;
    }

    public string RootPath => _rootPath;

    public string FormatRunStamp(DateTime utc) =>
        utc.ToUniversalTime().ToString(StampFormat, CultureInfo.InvariantCulture);

    // Переносит файл из профиля в корзину. Исходник НЕ удаляется — вызывающий код
    // отдельно снимает File.Delete только при успехе (см. LlmProviderRegistry.On-ветка).
    // Возвращает true при успехе, false при любом сбое (WARN в stderr).
    public bool Move(string profileName, string profileDir, string relPath, DateTime runStampUtc)
    {
        var src = Path.Combine(profileDir, relPath);
        var dst = Path.Combine(_rootPath, profileName, FormatRunStamp(runStampUtc), relPath);
        try
        {
            if (!File.Exists(src)) return false;
            var dir = Path.GetDirectoryName(dst);
            if (string.IsNullOrEmpty(dir)) return false;
            Directory.CreateDirectory(dir);
            // overwrite: false — два прохода в одну секунду (теоретически возможно на
            // быстром контуре) не должны перетирать уже лежащий файл, лучше WARN.
            File.Copy(src, dst, overwrite: false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProfileSync] Перенос в корзину {src} → {dst} не удался: {ex.Message}");
            return false;
        }
        return true;
    }

    // Удаляет каталоги проходов, чья отметка старше cutoff (UTC). По mtime файлов
    // НЕ ориентируется — mtime тут свойство доставки, а не удаления (см. шапку).
    // Безопасно для параллельного чтения: удаляет только те отметки, что сама
    // разобрала, и только когда вся папка отметки подходит под ретенцию.
    public int PurgeOld(DateTime cutoffUtc)
    {
        if (!Directory.Exists(_rootPath)) return 0;
        var removed = 0;
        foreach (var profileDir in Directory.GetDirectories(_rootPath))
        {
            foreach (var stampDir in Directory.GetDirectories(profileDir))
            {
                var stampName = Path.GetFileName(stampDir);
                if (!TryParseStamp(stampName, out var stamp)) continue;
                if (stamp > cutoffUtc) continue;
                try
                {
                    Directory.Delete(stampDir, recursive: true);
                    removed++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ProfileSync] Уборка корзины {stampDir} не удалась: {ex.Message}");
                }
            }
        }
        return removed;
    }

    public static bool TryParseStamp(string name, out DateTime utc) =>
        DateTime.TryParseExact(name, StampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
}

// Хранилище манифеста на диске: JSON в корне профиля. Атомарная запись (через
// временный файл + переименование), чтобы падение посреди записи не оставляло
// полупустой манифест — следующий проход сделал бы усыновление заново.
internal static class ProfileSyncManifestStore
{
    public const string ManifestFileName = ".sync-manifest.json";

    // camelCase — единый стиль с остальными JSON в проекте (JsonStringEnumConverter
    // с CamelCase, см. Program.cs). Иначе поле Source уезжало бы в файле как
    // "Source", а источник задан как "host" | "defaults" по ADR-015 §3.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ProfileSyncManifest LoadOrEmpty(string profileDir)
    {
        var path = Path.Combine(profileDir, ManifestFileName);
        if (!File.Exists(path)) return new ProfileSyncManifest();
        try
        {
            var json = File.ReadAllText(path);
            var m = JsonSerializer.Deserialize<ProfileSyncManifest>(json, Options);
            if (m is null) return new ProfileSyncManifest();
            m.Files ??= new(StringComparer.OrdinalIgnoreCase);
            return m;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProfileSync] Манифест {path} повреждён, игнорируем: {ex.Message}");
            return new ProfileSyncManifest();
        }
    }

    public static void Save(string profileDir, ProfileSyncManifest manifest)
    {
        try
        {
            var path = Path.Combine(profileDir, ManifestFileName);
            var tmp = path + ".tmp";
            Directory.CreateDirectory(profileDir);
            File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProfileSync] Запись манифеста в {profileDir} не удалась: {ex.Message}");
        }
    }
}