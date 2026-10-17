using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Models.Plugins;

namespace ClaudeHomeServer.Services.Plugins;

// Валидатор пакета плагина владельца (ADR-031 §2.3, шаг 1.3): allow-list файлов пакета.
// Пакет — каталог `data/plugins/{owner}/packages/{id}/{version}` (PluginStore.PackageDir);
// валидатор не знает владельца, ему дан корень пакета. Пропускает только разрешённые файлы;
// на каждый запрещённый вход — отдельный код проблемы (RCE/обход allow-list).
//
// Allow-list (из спайка, docs/research/plugin-dir-spike-2026-10.md):
//   манифест  ccs-plugin.json / plugin.json / .claude-plugin/plugin.json
//   skills/**/SKILL.md + вспомогательные .md
//   commands/*.md
//   panel/**  (статика панели)
//
// Deny-list (RCE/обход) — подмножество «не в allow-list» с отдельным кодом:
//   hooks/ (исполняет shell), .mcp.json (регистрирует MCP-сервер), agents/ (сабагент),
//   bin/ (исполняемое), settings.json (override).
//
// Плюс проверки: без symlink, без выхода за корень, лимиты размера/числа файлов.
// Итог — sha256 пакета (для записи PluginInstall).
public class PluginPackageValidator
{
    // Лимиты пакета (ADR-031 §2.3). Константы, не конфиг: MVP без тюнинга.
    public const long MaxTotalSizeBytes = 20 * 1024 * 1024;   // 20 МБ на пакет
    public const int MaxFileCount = 500;
    public const long MaxSingleFileSizeBytes = 5 * 1024 * 1024; // 5 МБ на файл

    // Имена/каталоги allow-list.
    private static readonly string[] ManifestNames =
    [
        "ccs-plugin.json",
        "plugin.json",
        ".claude-plugin/plugin.json",
    ];

    // Имя файла навыка (нативный формат CLI): skills/**/SKILL.md.
    private const string SkillFileName = "SKILL.md";

    // Запрещённые каталоги (RCE/обход): префикс относительного пути → код проблемы.
    private static readonly (string Prefix, string Code, string Why)[] DenyDirs =
    [
        ("hooks/",  "deny-hooks",  "hooks/ исполняет shell (RCE)"),
        ("agents/", "deny-agents", "agents/ добавляет сабагента"),
        ("bin/",    "deny-bin",    "bin/ — исполняемое в корне пакета"),
    ];

    // Запрещённые файлы в корне пакета (RCE/обход).
    private static readonly (string Name, string Code, string Why)[] DenyFiles =
    [
        (".mcp.json",    "deny-mcp-json",  ".mcp.json регистрирует MCP-сервер в обход allow-list"),
        ("settings.json", "deny-settings",  "settings.json — глобальный override"),
    ];

    /// <summary>
    /// Валидирует пакет по корневому каталогу <paramref name="packageRoot"/>.
    /// Возвращает результат: <see cref="PluginPackageValidationResult.IsValid"/>, список
    /// <see cref="PluginPackageValidationResult.Problems"/> и <see cref="PluginPackageValidationResult.Sha256"/>.
    /// </summary>
    public PluginPackageValidationResult Validate(string packageRoot)
    {
        var problems = new List<PluginPackageProblem>();
        var files = new List<PackageFile>();

        // Перечисление файлов пакета (рекурсивно, только файлы).
        foreach (var full in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(packageRoot, full).Replace('\\', '/');
            files.Add(new PackageFile(rel, full));
        }

        // Лимит числа файлов.
        if (files.Count > MaxFileCount)
            problems.Add(new PluginPackageProblem(
                "file-count-limit", $"Файлов в пакете {files.Count}, лимит {MaxFileCount}"));

        long totalSize = 0;
        foreach (var f in files)
        {
            totalSize += f.Size;

            // 1) Символическая ссылка — любой symlink запрещён.
            if (f.IsSymlink)
                problems.Add(new PluginPackageProblem("no-symlink", $"Символическая ссылка: {f.RelativePath}"));

            // 2) Выход за корень пакета: «..»/абсолютный путь, либо symlink, чей target вне корня.
            if (EscapesRoot(f.RelativePath) || (f.IsSymlink && SymlinkEscapesRoot(f.FullPath, packageRoot)))
                problems.Add(new PluginPackageProblem("within-root", $"Путь выходит за корень пакета: {f.RelativePath}"));

            // 3) Запрещённые файлы (RCE/обход) — специфичный код.
            if (TryMatchDeny(f.RelativePath, out var deny))
            {
                problems.Add(new PluginPackageProblem(deny.Code, $"{deny.Why}: {f.RelativePath}"));
                continue;
            }

            // 4) Allow-list: не в списке — не разрешён.
            if (!IsAllowed(f.RelativePath))
                problems.Add(new PluginPackageProblem("not-allowed", $"Файл не в allow-list пакета: {f.RelativePath}"));

            // 5) Лимит размера отдельного файла.
            if (f.Size > MaxSingleFileSizeBytes)
                problems.Add(new PluginPackageProblem(
                    "file-size-limit", $"Файл больше {MaxSingleFileSizeBytes / 1024 / 1024} МБ: {f.RelativePath}"));
        }

        // Лимит общего размера пакета.
        if (totalSize > MaxTotalSizeBytes)
            problems.Add(new PluginPackageProblem(
                "size-limit", $"Пакет больше {MaxTotalSizeBytes / 1024 / 1024} МБ: {totalSize} байт"));

        var sha256 = ComputeSha256(files);
        return new PluginPackageValidationResult(problems.Count == 0, problems, sha256);
    }

    // Запрещённый файл/каталог? Возвращает код проблемы и причину.
    private static bool TryMatchDeny(string rel, out (string Code, string Why) deny)
    {
        foreach (var (prefix, code, why) in DenyDirs)
            if (rel.StartsWith(prefix, StringComparison.Ordinal))
            {
                deny = (code, why);
                return true;
            }

        var name = Path.GetFileName(rel);
        foreach (var (n, code, why) in DenyFiles)
            if (name == n)
            {
                deny = (code, why);
                return true;
            }

        deny = default;
        return false;
    }

    // Allow-list: манифест, навык (skills/**/SKILL.md + .md), команда (commands/*.md), панель (panel/**).
    private static bool IsAllowed(string rel)
    {
        // Манифест (в корне или в .claude-plugin/).
        foreach (var m in ManifestNames)
            if (rel == m) return true;

        // Навык: skills/**/SKILL.md и вспомогательные .md внутри skills/.
        if (rel.StartsWith("skills/", StringComparison.Ordinal))
        {
            var name = Path.GetFileName(rel);
            if (name == SkillFileName || name.EndsWith(".md", StringComparison.Ordinal)) return true;
        }

        // Команда: commands/*.md (только прямой вложенный уровень).
        if (rel.StartsWith("commands/", StringComparison.Ordinal)
            && !rel["commands/".Length..].Contains('/')
            && rel.EndsWith(".md", StringComparison.Ordinal))
            return true;

        // Панель: panel/** (статика, любые расширения).
        if (rel.StartsWith("panel/", StringComparison.Ordinal)) return true;

        return false;
    }

    // Выход за корень: «..» как компонент пути (любым разделителем) или ведущий разделитель
    // (абсолютный путь).
    private static bool EscapesRoot(string rel)
    {
        if (rel.StartsWith('/') || rel.StartsWith('\\')) return true;
        foreach (var part in rel.Split('/', '\\'))
            if (part == "..") return true;
        return false;
    }

    // Symlink, чей разрешённый target лежит вне корня пакета — выход за корень.
    private static bool SymlinkEscapesRoot(string fullPath, string root)
    {
        var target = new FileInfo(fullPath).ResolveLinkTarget(true);
        if (target is null) return false;
        var targetFull = Path.GetFullPath(target.FullName);
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return targetFull != rootFull
            && !targetFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    // SHA-256 пакета: детерминированный хеш содержимого всех файлов (путь + «\0» + байты),
    // файлы в сортированном порядке. Неизменяемость пакета = неизменность hash.
    private static string ComputeSha256(List<PackageFile> files)
    {
        using var hash = SHA256.Create();
        var buffer = new byte[8192];
        foreach (var f in files.OrderBy(x => x.RelativePath, StringComparer.Ordinal))
        {
            var pathBytes = Encoding.UTF8.GetBytes(f.RelativePath);
            hash.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);
            hash.TransformBlock([0], 0, 1, null, 0); // разделитель: конец записи пути
            using var stream = File.OpenRead(f.FullPath);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.TransformBlock(buffer, 0, read, null, 0);
        }
        hash.TransformFinalBlock([0], 0, 1);
        return Convert.ToHexString(hash.Hash).ToLowerInvariant();
    }

    // Файл пакета: относительный путь (через «/»), полный путь (для symlink), метаданные.
    private sealed class PackageFile
    {
        public string RelativePath { get; }
        public string FullPath { get; }
        public long Size { get; }
        public bool IsSymlink { get; }

        public PackageFile(string relativePath, string fullPath)
        {
            RelativePath = relativePath;
            FullPath = fullPath;
            var info = new FileInfo(fullPath);
            Size = info.Length;
            // Symlink — признак ReparsePoint (на Linux .NET так помечает ссылки; проверено прогоном).
            IsSymlink = (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0;
        }
    }
}
