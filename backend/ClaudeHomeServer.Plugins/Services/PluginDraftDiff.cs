using System.Text;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Plugins;

// Сравнение черновика с ПОСЛЕДНЕЙ установленной версией того же id (ADR-031, шаг 4.3b):
// человек видит, что изменится, если «Принять» черновик. Черновик — в
// data/plugins/{owner}/_drafts/{slug}/, установленный пакет — в
// data/plugins/{owner}/packages/{id}/{version}/ (PluginStore).
//
// Правила:
//   - нет установленной версии того же id → «новый плагин»: только список файлов черновика;
//   - иначе — файлы: добавлены / удалены / изменены;
//   - изменённый ТЕКСТОВЫЙ файл — построчный diff (простой: общий префикс/суффикс строк,
//     середина — удалённые + добавленные); потолок 200 строк diff на файл и 1000 суммарно,
//     при превышении truncated: true;
//   - бинарный файл (NUL-байт) или файл больше 256 КБ — «изменён, N байт», без содержимого;
//   - манифест: права добавлены/убраны, секреты добавлены/убраны/сменился признак «обязательный»;
//   - в ответе только относительные пути внутри пакета, хостовых нет.
//
// Stateless поверх PluginStore (как PluginDraftService/PluginInstaller): singleton.
public class PluginDraftDiff
{
    // Потолки diff: 200 строк на файл, 1000 суммарно (превышение → truncated: true).
    public const int MaxDiffLinesPerFile = 200;
    public const int MaxDiffLinesTotal = 1000;
    // Файл больше 256 КБ — «изменён, N байт», без содержимого (как и бинарный).
    public const int MaxTextFileBytes = 256 * 1024;

    private readonly PluginStore _store;

    public PluginDraftDiff(PluginStore store)
    {
        _store = store;
    }

    // Сравнить черновик с последней установленной версией того же id.
    // slug невалиден → ArgumentException; черновик не найден → FileNotFoundException
    // (контроллер мапит оба на 404, как Preview).
    public PluginDraftDiffResult Diff(string ownerId, string slug)
    {
        if (!PluginDraftService.IsValidSlug(slug))
            throw new ArgumentException($"Недопустимый slug: {slug}", nameof(slug));
        var draftDir = _store.DraftDir(ownerId, slug);
        if (!Directory.Exists(draftDir))
            throw new FileNotFoundException($"Черновик {slug} не найден", nameof(slug));

        // id — ТОЛЬКО из PluginIdentity.Resolve (QA-фикс N: сервер выводит; сырой manifest.Id
        // не читаем). Пустой id в манифесте → выведенный (local-{ownerShort}/{slug}): сравнение
        // идёт с установленной версией того же id, а «нового плагина» (status "new") — не 404
        // (раньше: без id в манифесте diff падал ArgumentException → 404, хотя плагин новый).
        var (manifest, _) = PluginInstaller.ReadManifest(draftDir);
        var (resolvedId, _) = PluginIdentity.Resolve(manifest, ownerId, slug);
        var id = resolvedId ?? PluginIdentity.DeriveId(ownerId, slug);

        // Последняя установленная версия того же id (installed.json: одна запись на id;
        // если записей несколько — берём максимальную по semver).
        var installed = _store.LoadInstalled(ownerId)
            .Where(r => r.Id == id)
            .OrderByDescending(r => r.Version, VersionComparer.Instance)
            .FirstOrDefault();

        if (installed is null)
            return NewPlugin(slug, id, manifest, draftDir);

        var installedDir = _store.PackageDir(ownerId, id, installed.Version);
        return Compare(slug, id, draftDir, installedDir, manifest, installed);
    }


    // «Новый плагин»: установленной версии нет → список файлов черновика (всё «добавлено»).
    // id — выведенный сервером (см. Diff): manifest.Id может быть пустым, а выведенный id
    // всегда валиден.
    private static PluginDraftDiffResult NewPlugin(string slug, string id, PluginManifest? manifest, string draftDir)
    {
        var added = EnumerateFiles(draftDir)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new PluginDraftDiffFile(kv.Key, kv.Value.Size, false, false, null))
            .ToList();
        return new PluginDraftDiffResult
        {
            Status = "new",
            Slug = slug,
            Id = id,
            Version = manifest?.Version ?? "",
            InstalledVersion = null,
            Truncated = false,
            Added = added, Removed = [], Modified = [],
            Manifest = null,
        };
    }

    // Сравнение дерева черновика с деревом установленного пакета.
    private PluginDraftDiffResult Compare(
        string slug, string id, string draftDir, string installedDir, PluginManifest? draftManifest, PluginInstall installed)
    {
        var draftFiles = EnumerateFiles(draftDir);
        var installedFiles = EnumerateFiles(installedDir);

        var added = new List<PluginDraftDiffFile>();
        var removed = new List<PluginDraftDiffFile>();
        var modified = new List<PluginDraftDiffFile>();

        int totalDiffLines = 0;
        bool totalTruncated = false;

        // Union относительных путей (только внутри пакета), отсортирован для детерминизма.
        var allPaths = draftFiles.Keys
            .Union(installedFiles.Keys, StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        foreach (var rel in allPaths)
        {
            bool inDraft = draftFiles.TryGetValue(rel, out var d);
            bool inInstalled = installedFiles.TryGetValue(rel, out var i);

            if (inDraft && !inInstalled)
            {
                // Добавлен: путь + размер, без содержимого.
                added.Add(new PluginDraftDiffFile(rel, d.Size, false, false, null));
            }
            else if (!inDraft && inInstalled)
            {
                // Удалён: путь + размер (версии пакета), без содержимого.
                removed.Add(new PluginDraftDiffFile(rel, i.Size, false, false, null));
            }
            else
            {
                // В обоих: идентичны по байтам → без изменений.
                if (d.Bytes.AsSpan().SequenceEqual(i.Bytes))
                    continue;

                // Бинарный (NUL) или больше 256 КБ → «изменён, N байт», без содержимого.
                if (IsBinary(d.Bytes) || d.Size > MaxTextFileBytes)
                {
                    modified.Add(new PluginDraftDiffFile(rel, d.Size, true, false, null));
                    continue;
                }

                // Текстовый: построчный diff с потолками. oldLines = установленный (старая
                // версия), newLines = черновик (новая): «del» = было в установленном, «add» = в черновике.
                var (lines, fileTruncated) = DiffLines(ToLines(i.Bytes), ToLines(d.Bytes), out int emitted);
                totalDiffLines += emitted;
                if (totalDiffLines > MaxDiffLinesTotal)
                {
                    totalTruncated = true;
                    // Суммарный потолок превышен: у этого файла показываем только до общего лимита.
                    int keep = Math.Max(0, MaxDiffLinesTotal - (totalDiffLines - emitted));
                    lines = lines.Take(keep).ToList();
                    fileTruncated = true;
                }
                modified.Add(new PluginDraftDiffFile(rel, d.Size, false, fileTruncated, lines));
            }
        }

        return new PluginDraftDiffResult
        {
            Status = "updated",
            Slug = slug,
            Id = id,
            Version = draftManifest?.Version ?? "",
            InstalledVersion = installed.Version,
            Truncated = totalTruncated,
            Added = added,
            Removed = removed,
            Modified = modified,
            // Манифеста черновика нет вовсе → сравнивать не с чем (null, как при битом
            // манифесте установленного пакета).
            Manifest = draftManifest is null ? null : DiffManifest(draftManifest, installedDir),
        };
    }

    // ── Манифест: права и секреты ───────────────────────────────────────────────

    // Права (requested) и секреты (имя + признак «обязательный») — черновик vs установленный
    // пакет. Сравниваем манифесты (ccs-plugin.json) обеих версий: признак «обязательный»
    // (required) живёт только в манифесте, не в записи установки. Манифест пакета не читается
    // (битый/отсутствует) → null (сравнивать не с чем).
    private static PluginDraftDiffManifest? DiffManifest(PluginManifest draft, string installedDir)
    {
        var (installedManifest, _) = PluginInstaller.ReadManifest(installedDir);
        if (installedManifest is null) return null;

        var draftRequested = draft.Permissions?.Requested ?? [];
        var installedRequested = installedManifest.Permissions?.Requested ?? [];

        var added = draftRequested.Except(installedRequested, StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        var removed = installedRequested.Except(draftRequested, StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal).ToList();

        var draftSecrets = (draft.Contributes?.Requires?.Secrets ?? [])
            .Where(s => !string.IsNullOrEmpty(s.Name)).ToList();
        var installedSecrets = (installedManifest.Contributes?.Requires?.Secrets ?? [])
            .Where(s => !string.IsNullOrEmpty(s.Name)).ToList();

        var draftNames = draftSecrets.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var installedNames = installedSecrets.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        var secretsAdded = draftSecrets
            .Where(s => !installedNames.Contains(s.Name))
            .Select(s => new PluginDraftDiffSecret(s.Name, s.Required))
            .OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        var secretsRemoved = installedSecrets
            .Where(s => !draftNames.Contains(s.Name))
            .Select(s => new PluginDraftDiffSecret(s.Name, s.Required))
            .OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

        // Сменился признак «обязательный»: имя есть в обоих, required различается.
        var requiredChanged = draftSecrets
            .Where(s => installedSecrets.Any(o => o.Name == s.Name && o.Required != s.Required))
            .Select(s => new PluginDraftDiffSecret(s.Name, s.Required))
            .OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

        return new PluginDraftDiffManifest
        {
            PermissionsAdded = added,
            PermissionsRemoved = removed,
            SecretsAdded = secretsAdded,
            SecretsRemoved = secretsRemoved,
            SecretsRequiredChanged = requiredChanged,
        };
    }

    // ── Файлы: перечисление, бинарность, строки, diff ──────────────────────────

    // Все файлы корня (рекурсивно) → словарь «относительный путь (внутри пакета) → запись».
    // Корня нет → пустой словарь (установленный пакет мог быть удалён).
    // Symlink-файл (ReparsePoint) — пропуск, содержимое не читаем (minor 14, D2): target
    // может лежать вне пакета (валидатор и так отбивает его no-symlink, diff ссылку не следует).
    private static Dictionary<string, FileEntry> EnumerateFiles(string root)
    {
        var result = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
        if (!Directory.Exists(root)) return result;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                continue;
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            result[rel] = new FileEntry(File.ReadAllBytes(file));
        }
        return result;
    }

    // Бинарный = NUL-байт в первых 8000 байтах (эвристика, как у git).
    private static bool IsBinary(byte[] bytes)
    {
        int n = Math.Min(bytes.Length, 8000);
        for (int i = 0; i < n; i++)
            if (bytes[i] == 0) return true;
        return false;
    }

    // Байты → строки: UTF-8, нормализация \r\n и \r → \n, разрыв по \n.
    // Хвостовой пустой элемент (текст заканчивается \n) отбрасываем.
    private static string[] ToLines(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n');
        if (lines.Length > 0 && lines[^1] == "")
            Array.Resize(ref lines, lines.Length - 1);
        return lines;
    }

    // Простой построчный diff: общий префикс и общий суффикс строк — контекст,
    // середина — удалённые (старые) + добавленные (новые). Не минимален, но O(n) и честен.
    // Потолок: не больше max строк diff на файл; при превышении возвращает усечённый список
    // (truncated=true) и число фактически выданных строк (emitted).
    private static (List<PluginDraftDiffLine> Lines, bool Truncated) DiffLines(
        string[] oldLines, string[] newLines, out int emitted)
    {
        int n = oldLines.Length, m = newLines.Length;

        // Общий префикс.
        int prefix = 0;
        while (prefix < n && prefix < m && oldLines[prefix] == newLines[prefix])
            prefix++;

        // Общий суффикс (не пересекается с префиксом).
        int suffix = 0;
        while (suffix < n - prefix && suffix < m - prefix
               && oldLines[n - 1 - suffix] == newLines[m - 1 - suffix])
            suffix++;

        var result = new List<PluginDraftDiffLine>();
        int oldMidStart = prefix, oldMidEnd = n - suffix;
        int newMidStart = prefix, newMidEnd = m - suffix;

        // Контекст: префикс.
        for (int i = 0; i < prefix; i++)
            result.Add(new PluginDraftDiffLine(i + 1, i + 1, "ctx", oldLines[i]));

        // Середина: сначала удалённые (старые), потом добавленные (новые).
        for (int i = oldMidStart; i < oldMidEnd; i++)
            result.Add(new PluginDraftDiffLine(i + 1, 0, "del", oldLines[i]));
        for (int i = newMidStart; i < newMidEnd; i++)
            result.Add(new PluginDraftDiffLine(0, i + 1, "add", newLines[i]));

        // Контекст: суффикс.
        for (int i = oldMidEnd; i < n; i++)
        {
            int newIdx = i - (n - m); // смещение по длине
            result.Add(new PluginDraftDiffLine(i + 1, newIdx + 1, "ctx", oldLines[i]));
        }

        // Потолок: не больше max строк diff на файл.
        bool truncated = false;
        if (result.Count > MaxDiffLinesPerFile)
        {
            result = result.Take(MaxDiffLinesPerFile).ToList();
            truncated = true;
        }
        emitted = result.Count;
        return (result, truncated);
    }

    // Запись файла: размер + сырые байты (строки считаются по требованию).
    private sealed record FileEntry(byte[] Bytes)
    {
        public long Size => Bytes.Length;
    }
}

// ── Схемы ответа ─────────────────────────────────────────────────────────────────

// Итог сравнения. Status: "new" (нет установленной версии) | "updated" (сравнение).
// Truncated — суммарный потолок diff (1000) превышен.
public sealed class PluginDraftDiffResult
{
    public string Status { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Id { get; init; } = "";
    /// <summary>Версия черновика (из манифеста).</summary>
    public string Version { get; init; } = "";
    /// <summary>Версия установленной версии (null, если статус "new").</summary>
    public string? InstalledVersion { get; init; }
    public bool Truncated { get; init; }
    public List<PluginDraftDiffFile> Added { get; init; } = [];
    public List<PluginDraftDiffFile> Removed { get; init; } = [];
    public List<PluginDraftDiffFile> Modified { get; init; } = [];
    /// <summary>Разница манифеста (null, если статус "new").</summary>
    public PluginDraftDiffManifest? Manifest { get; init; }
}

// Файл в diff. Path — относительный внутри пакета (без хостовых путей).
// Binary=true → содержимое подавлено («изменён, N байт»). Lines — только для текстовых
// изменённых (null для added/removed/бинарных). Truncated — потолок 200 строк на файл.
public sealed record PluginDraftDiffFile(
    string Path,
    long Size,
    bool Binary,
    bool Truncated,
    List<PluginDraftDiffLine>? Lines);

// Строка diff. Kind: "ctx" (контекст), "del" (удалено), "add" (добавлено).
// OldLine/NewLine — номер строки в старой/новой версии (0 — нет).
public sealed record PluginDraftDiffLine(int OldLine, int NewLine, string Kind, string Text);

// Разница манифеста: права и секреты.
public sealed class PluginDraftDiffManifest
{
    public List<string> PermissionsAdded { get; init; } = [];
    public List<string> PermissionsRemoved { get; init; } = [];
    public List<PluginDraftDiffSecret> SecretsAdded { get; init; } = [];
    public List<PluginDraftDiffSecret> SecretsRemoved { get; init; } = [];
    /// <summary>Секреты, у которых сменился признак «обязательный» (required).</summary>
    public List<PluginDraftDiffSecret> SecretsRequiredChanged { get; init; } = [];
}

// Секрет в diff: имя + признак «обязательный» (из черновика; для removed — false).
public sealed record PluginDraftDiffSecret(string Name, bool Required);

// Сопоставление версий semver для выбора «последней» установленной (MAJOR.MINOR.PATCH,
// pre-release после '-' — строковое сравнение).
internal sealed class VersionComparer : IComparer<string>
{
    public static readonly VersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var (xCore, xPre) = Split(x);
        var (yCore, yPre) = Split(y);

        for (int i = 0; i < 3; i++)
        {
            int c = xCore[i].CompareTo(yCore[i]);
            if (c != 0) return c;
        }
        // Ядро равное: без pre-release > с pre-release; иначе по строке pre-release.
        if (xPre is null && yPre is not null) return 1;
        if (xPre is not null && yPre is null) return -1;
        if (xPre is null) return 0;
        return string.CompareOrdinal(xPre, yPre);
    }

    private static (int[] Core, string? Pre) Split(string v)
    {
        string core = v, pre = null!;
        int dash = v.IndexOf('-');
        if (dash >= 0)
        {
            core = v[..dash];
            pre = v[(dash + 1)..];
        }
        var parts = core.Split('.');
        var nums = new int[3];
        for (int i = 0; i < 3 && i < parts.Length; i++)
            nums[i] = int.TryParse(parts[i], out var n) ? n : 0;
        return (nums, pre);
    }
}
