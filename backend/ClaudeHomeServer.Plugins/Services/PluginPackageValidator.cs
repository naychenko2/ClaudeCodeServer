using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeHomeServer.Services.Plugins;

// Валидатор пакета плагина владельца (ADR-031 §2.3, шаг 1.3, rework C3/C4/M9): allow-list
// файлов пакета. Пакет — каталог `data/plugins/{owner}/packages/{id}/{version}`
// (PluginStore.PackageDir); валидатор не знает владельца, ему дан корень пакета.
// Пропускает только разрешённые файлы; на каждый запрещённый вход — отдельный код проблемы
// (RCE/обход allow-list).
//
// Allow-list (из спайка, docs/research/plugin-dir-spike-2026-10.md):
//   манифест  ccs-plugin.json (наш) / .claude-plugin/plugin.json (нативный CLI, обязателен)
//   skills/**/SKILL.md + вспомогательные .md
//   commands/*.md
//   panel/**  (статика панели)
// R10-1: корневой plugin.json — НЕ нативный манифест (CLI имя пакета берёт только из
// .claude-plugin/plugin.json, спайк plugin-dir-name-spike-2026-10.md), из allow-list убран.
//
// Deny-list (RCE/обход) — подмножество «не в allow-list» с отдельным кодом:
//   hooks/ (исполняет shell), .mcp.json (регистрирует MCP-сервер), agents/ (сабагент),
//   bin/ (исполняемое), settings.json (override).
//
// Rework:
//   C4  symlink-каталог — своя рекурсия: на каталоге с ReparsePoint → no-symlink, внутрь
//       не спускаемся (Directory.EnumerateFiles(AllDirectories) symlink-каталог не видит).
//   M9  лимиты MaxFileCount/MaxTotalSizeBytes проверяются ВО время перечисления (прерывают
//       обход), не после; хеш — только валидного пакета.
//   C3  поля манифеста (plugin.json/ccs-plugin.json) — через PluginManifestReader
//       (deny-manifest-field).
//   minor  битая symlink → читаемая проблема, не 500; EscapesRoot на «..» в имени.
public class PluginPackageValidator
{
    // Лимиты пакета (ADR-031 §2.3). Константы, не конфиг: MVP без тюнинга.
    public const long MaxTotalSizeBytes = 20 * 1024 * 1024;   // 20 МБ на пакет
    public const int MaxFileCount = 500;
    public const long MaxSingleFileSizeBytes = 5 * 1024 * 1024; // 5 МБ на файл
    // minor 8: лимит числа каталогов (пустые каталоги не считаются в лимит файлов).
    public const int MaxDirCount = 200;

    // Имена/каталоги allow-list. R10-1: корневой plugin.json убран (не нативный манифест);
    // нативный — .claude-plugin/plugin.json (обязателен, см. CheckNativeManifest).
    private static readonly string[] ManifestNames =
    [
        "ccs-plugin.json",
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

    // Единая точка разбора манифеста (rework C3): поля ccs-plugin.json/.claude-plugin/plugin.json.
    private readonly PluginManifestReader _manifestReader = new();

    // M10 (rework пакета 6): allowed-tools — Contains по всему тексту (M10.1) ИЛИ
    // обратный слеш во фронтматтере (M10.2, экранированный ключ). Regex с «:» после
    // ключа обходился explicit-key (`allowed-tools # c\n: Bash` → PyYAML парсит в
    // `allowed-tools: Bash`, regex = false); Contains дешевле и не обходится.

    /// <summary>
    /// Валидирует пакет по корневому каталогу <paramref name="packageRoot"/>.
    /// Возвращает результат: <see cref="PluginPackageValidationResult.IsValid"/>, список
    /// <see cref="PluginPackageValidationResult.Problems"/>, <see cref="PluginPackageValidationResult.Sha256"/>
    /// и <see cref="PluginPackageValidationResult.ScannedFileCount"/> (M9: обрыв по лимиту).
    /// </summary>
    public PluginPackageValidationResult Validate(string packageRoot)
    {
        // Файловые проверки (allow/deny, symlink, лимиты, нативный манифест, name==slug, content).
        var (problems, files, limitHit) = ValidateFiles(packageRoot);

        // M9: хеш — только валидного пакета (нет проблем), до лимита (limitHit → null).
        // Невалидный пакет хеш не несёт: sha256 = null.
        var sha256 = limitHit || problems.Count > 0 ? null : ComputeSha256(files);
        return new PluginPackageValidationResult(
            problems.Count == 0, problems, sha256, files.Count);
    }

    /// <summary>
    /// Полная валидация пакета: файловые проверки (<see cref="Validate"/>) + структурная
    /// валидация манифеста (<see cref="ClaudeHomeServer.Models.PluginManifest.Validate"/>:
    /// <c>no-contribution</c>, <c>unknown-permission</c>, required secrets). Единый арбитр:
    /// установка (<see cref="PluginInstaller"/>) и черновик (<see cref="PluginDraftService"/>)
    /// зовут этот метод, чтобы коды проблем совпадали 1:1 (инвариант «Валидатор пакета —
    /// единственный арбитр»). Манифест читаем ТОЛЬКО если файловая валидация прошла (нет
    /// файловых проблем): иначе symlink-манифест (no-symlink) не дойдёт до чтения — тот же
    /// порядок «read после validate», что в <see cref="PluginInstaller"/>.
    /// </summary>
    public PluginPackageValidationResult ValidateFull(string packageRoot)
    {
        var (problems, files, limitHit) = ValidateFiles(packageRoot);

        // Манифестная валидация — та же, что в установщике: ccs-plugin.json → модель →
        // PluginManifest.Validate (no-contribution / unknown-permission / required secrets).
        if (problems.Count == 0)
        {
            var (manifest, _) = PluginInstaller.ReadManifest(packageRoot);
            if (manifest is not null)
                foreach (var p in manifest.Validate().Problems)
                    problems.Add(new PluginPackageProblem(p.Code, p.Message));
        }

        var sha256 = limitHit || problems.Count > 0 ? null : ComputeSha256(files);
        return new PluginPackageValidationResult(
            problems.Count == 0, problems, sha256, files.Count);
    }

    // Файловые проверки пакета по корню <paramref name="packageRoot"/> (C4+M9+minor8):
    //   * symlink-каталог (ReparsePoint) → no-symlink, внутрь не заходить (C4);
    //   * каталог: лимит числа каталогов (minor 8, прерывает обход);
    //   * файл: лимиты (прерывают обход) + size + IsSymlink + EscapesRoot + deny + allow + size;
    //   * обычные подкаталоги — рекурсия.
    // Возвращает (проблемы, файлы, limitHit) — для Validate (только файлы) и ValidateFull (+ манифест).
    private (List<PluginPackageProblem> Problems, List<PackageFile> Files, bool LimitHit) ValidateFiles(string packageRoot)
    {
        var problems = new List<PluginPackageProblem>();
        var files = new List<PackageFile>();
        long totalSize = 0;
        int dirCount = 0;
        bool limitHit = false;

        // C4+M9: своя рекурсия — лимиты (число/размер/каталоги) прерывают обход,
        // symlink-каталог даёт no-symlink и внутрь не спускаемся. Directory.EnumerateFiles
        // (AllDirectories) не годится: не помечает symlink-каталог как ReparsePoint и
        // перечисляет всё до конца.
        Walk(packageRoot, packageRoot, ref files, ref totalSize, ref dirCount, problems, ref limitHit);

        // C3: сырые JSON манифестов — для сверки name == slug (CheckNameEqualsSlug). Поля
        // (deny-manifest-field) теперь проверяет CheckContent в Walk (общий метод с записью черновика).
        var (raws, _) = _manifestReader.Read(packageRoot);

        // R10-1: нативный манифест .claude-plugin/plugin.json обязателен (CLI берёт из него
        // имя пакета, спайк plugin-dir-name-spike-2026-10.md): нет файла → deny-native-manifest-missing,
        // пустой/отсутствующий name → deny-name-missing.
        CheckNativeManifest(packageRoot, problems);

        // M3: name == slug. CLI берёт имя пакета из .claude-plugin/plugin.json (спайк
        // plugin-dir-name-spike-2026-10.md), а Skill-привязка адресует навык по slug из id;
        // name != slug → привязка указывает в никуда.
        CheckNameEqualsSlug(raws, problems);

        return (problems, files, limitHit);
    }

    // Своя рекурсия (C4+M9+minor8): <paramref name="current"/> — текущий каталог,
    // <paramref name="root"/> — корень пакета (для относительных путей).
    //   * symlink-каталог (ReparsePoint) → no-symlink, внутрь не спускаемся;
    //   * каталог: лимит числа каталогов (minor 8, прерывает обход);
    //   * файл: лимиты (прерывают обход) + size + IsSymlink + EscapesRoot + deny + allow + size;
    //   * обычные подкаталоги — рекурсия.
    private void Walk(
        string current,
        string root,
        ref List<PackageFile> files,
        ref long totalSize,
        ref int dirCount,
        List<PluginPackageProblem> problems,
        ref bool limitHit)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(current))
        {
            var rel = Path.GetRelativePath(root, entry).Replace('\\', '/');

            // Каталог: symlink-каталог → no-symlink, внутрь не заходить (C4).
            if (Directory.Exists(entry))
            {
                // minor 8: лимит числа каталогов (пустые не считаются в лимит файлов).
                dirCount++;
                if (dirCount > MaxDirCount)
                {
                    problems.Add(new PluginPackageProblem(
                        "dir-count-limit", $"Каталогов в пакете {dirCount}, лимит {MaxDirCount}"));
                    limitHit = true;
                    return;
                }
                if (IsReparsePoint(entry))
                    problems.Add(new PluginPackageProblem("no-symlink", $"Символическая ссылка (каталог): {rel}"));
                continue; // обычный каталог — в цикле рекурсии ниже
            }

            // Файл (или symlink-файл / битая ссылка).
            var file = new PackageFile(rel, entry);

            // M9: лимит числа файлов — прерывает обход (не после).
            if (files.Count >= MaxFileCount)
            {
                problems.Add(new PluginPackageProblem(
                    "file-count-limit", $"Файлов в пакете {files.Count + 1}, лимит {MaxFileCount}"));
                limitHit = true;
                return;
            }

            // M9: лимит общего размера — прерывает обход, до чтения байтов следующего файла.
            totalSize += file.Size;
            if (totalSize > MaxTotalSizeBytes)
            {
                problems.Add(new PluginPackageProblem(
                    "size-limit", $"Пакет больше {MaxTotalSizeBytes / 1024 / 1024} МБ: {totalSize} байт"));
                limitHit = true;
                return;
            }

            files.Add(file);

            // 1) Символическая ссылка (файл) — любой symlink запрещён.
            if (file.IsSymlink)
                problems.Add(new PluginPackageProblem("no-symlink", $"Символическая ссылка: {file.RelativePath}"));

            // 2) Выход за корень пакета: «..»/абсолютный путь, либо symlink, чей target вне корня.
            if (EscapesRoot(file.RelativePath) || (file.IsSymlink && SymlinkEscapesRoot(file.FullPath, root)))
                problems.Add(new PluginPackageProblem("within-root", $"Путь выходит за корень пакета: {file.RelativePath}"));

            // 3) Запрещённые файлы (RCE/обход) — специфичный код.
            if (TryMatchDeny(file.RelativePath, out var deny))
            {
                problems.Add(new PluginPackageProblem(deny.Code, $"{deny.Why}: {file.RelativePath}"));
                continue;
            }

            // 4) Allow-list: не в списке — не разрешён.
            if (!IsAllowed(file.RelativePath))
                problems.Add(new PluginPackageProblem("not-allowed", $"Файл не в allow-list пакета: {file.RelativePath}"));

            // 5) M10 + манифест: проверки содержимого (allowed-tools/backtick-exec для
            //    исполняемых .md, deny-manifest-field для манифестов) — общий метод CheckContent
            //    (его же зовёт запись черновика; спайк 82fecb37).
            if (IsExecutableMd(file.RelativePath) || IsManifest(file.RelativePath))
            {
                var content = ReadFileBytes(file.FullPath);
                if (content is not null)
                    problems.AddRange(CheckContent(file.RelativePath, content));
            }

            // 6) Лимит размера отдельного файла.
            if (file.Size > MaxSingleFileSizeBytes)
                problems.Add(new PluginPackageProblem(
                    "file-size-limit", $"Файл больше {MaxSingleFileSizeBytes / 1024 / 1024} МБ: {file.RelativePath}"));
        }

        // Рекурсия в обычные подкаталоги (symlink-каталоги уже отсечены: no-symlink + continue).
        foreach (var dir in Directory.EnumerateDirectories(current))
        {
            if (limitHit) return;
            if (IsReparsePoint(dir)) continue;
            Walk(dir, root, ref files, ref totalSize, ref dirCount, problems, ref limitHit);
        }
    }

    // Признак symlink (каталог или файл): ReparsePoint в атрибутах (Linux .NET так помечает ссылки).
    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

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

    // M10: .md, который CLI исполняет: skills/**/SKILL.md (навык) и commands/*.md
    // (slash-команда, прямой уровень). Вспомогательные .md под skills/ не исполняются.
    private static bool IsExecutableMd(string rel)
    {
        if (rel.StartsWith("skills/", StringComparison.Ordinal)
            && Path.GetFileName(rel) == SkillFileName)
            return true;
        if (rel.StartsWith("commands/", StringComparison.Ordinal)
            && !rel["commands/".Length..].Contains('/')
            && rel.EndsWith(".md", StringComparison.Ordinal))
            return true;
        return false;
    }

    // M10: исполняемый .md (skills/**/SKILL.md, commands/*.md) — два независимых механизма
    // дают агенту Bash, нарушая «навык не даёт новых возможностей» (ADR §4, спайк
    // plugin-dir-allowed-tools):
    //   M10.1 allowed-tools — Contains по ВСЕМУ тексту (не только фронтматтер): парсер
    //         CLI 2.1.301 расходится с нашим (fenced ```!, `--- ` с пробелом, кавычечные
    //         ключи, `---` посреди строки), и ключ может спрятаться в любой части файла.
    //         Regex с «:» после ключа обходился explicit-key, поэтому Contains. Ложное
    //         срабатывание на упоминание в описании — дешёвая цена (ключ и упоминание дают
    //         одно и то же: Bash без запроса).
    //   M10.2 обратный слеш во фронтматтере — deny: экранированный ключ (YAML-эскейп,
    //         напр. `\x2dllowed-tools` / `allowed-\x74ools`) обходит и Contains, и regex.
    //         Ужесточение: ЛЮБОЙ `\` во фронтматтере (включая description) отбивает
    //         (ложное срабатывание, цена низкая).
    //   M10.3 backtick-исполнение: `!`cmd` / ```! и `!$` (маркер живого исполнения после
    //         подстановки аргумента $ARGUMENTS/$1, CLI сам считает его исполняемым).
    // Работает с ТЕКСТОМ (не читает файл): общий для валидатора и записи черновика.
    private static void CheckExecutableMdContent(string rel, string content, List<PluginPackageProblem> problems)
    {
        // allowed-tools: Contains по всему тексту (M10.1) ИЛИ обратный слеш во
        // фронтматтере (M10.2, экранированный ключ). Одно срабатывание — один problem;
        // формулировки раздельные: «allowed-tools» и «обратный слеш» — разные причины
        // отказа (ревью пачки A: единое сообщение «Ключ allowed-tools» вводило в заблуждение,
        // когда сработал только слеш).
        var hasAllowedTools = content.Contains("allowed-tools", StringComparison.OrdinalIgnoreCase);
        var frontmatter = ExtractFrontmatter(content);
        var hasEscapedKey = frontmatter is not null && frontmatter.Contains('\\');
        if (hasAllowedTools)
            problems.Add(new PluginPackageProblem("deny-allowed-tools",
                $"Ключ allowed-tools (Bash без запроса): {rel}"));
        else if (hasEscapedKey)
            problems.Add(new PluginPackageProblem("deny-allowed-tools",
                $"Обратный слеш во фронтматтере (экранированный ключ, M10.2): {rel}"));

        // !`cmd` / ```! (backtick-исполнение) и !$ (маркер подстановки аргумента, M10.3).
        if (content.Contains("!`", StringComparison.Ordinal)
            || content.Contains("```!", StringComparison.Ordinal)
            || content.Contains("!$", StringComparison.Ordinal))
            problems.Add(new PluginPackageProblem("deny-backtick-exec",
                $"Backtick-команда !`…` (исполняет shell): {rel}"));
    }

    // Манифест пакета (ccs-plugin.json / .claude-plugin/plugin.json) — для deny-manifest-field.
    private static bool IsManifest(string rel) =>
        rel == "ccs-plugin.json" || rel == ".claude-plugin/plugin.json";

    // Общий метод проверок содержимого ОДНОГО файла (rework D1): M10 (allowed-tools/backtick-exec)
    // для исполняемых .md и deny-manifest-field для манифестов. Вызывается И валидатором (Walk),
    // И записью черновика (PluginDraftService.WriteFile) — одна реализация, коды 1:1, регулярки
    // и списки не копируются. <paramref name="relPath"/> — относительный путь (через «/»).
    public IReadOnlyList<PluginPackageProblem> CheckContent(string relPath, byte[] bytes)
    {
        var problems = new List<PluginPackageProblem>();
        var rel = relPath.Replace('\\', '/');
        var content = Encoding.UTF8.GetString(bytes);

        if (IsExecutableMd(rel))
            CheckExecutableMdContent(rel, content, problems);

        if (IsManifest(rel))
            problems.AddRange(_manifestReader.ValidateFields(content));

        return problems;
    }

    // Прочитать байты файла; null — IOException (битая symlink — уже no-symlink, не читаем).
    private static byte[]? ReadFileBytes(string fullPath)
    {
        try { return File.ReadAllBytes(fullPath); }
        catch (IOException) { return null; }
    }

    // M10.2: фронтматтер — между первым «---» (начало файла) и следующим «---»
    // (IndexOf("---", 3), как у CLI). null — фронтматтера нет (файл не начинается с
    // «---» или закрывающего «---» нет).
    private static string? ExtractFrontmatter(string content)
    {
        if (!content.StartsWith("---", StringComparison.Ordinal)) return null;
        var end = content.IndexOf("---", 3, StringComparison.Ordinal);
        if (end < 0) return null;
        return content.Substring(3, end - 3);
    }

    // Выход за корень: «..» как компонент пути (любым разделителем) или ведущий разделитель
    // (абсолютный путь). minor: «..» в ИМЕНИ файла/каталога (не отдельный компонент) —
    // НЕ выход за корень; тест это фиксирует.
    private static bool EscapesRoot(string rel)
    {
        if (rel.StartsWith('/') || rel.StartsWith('\\')) return true;
        foreach (var part in rel.Split('/', '\\'))
            if (part == "..") return true;
        return false;
    }

    // Symlink, чей разрешённый target лежит вне корня пакета — выход за корень.
    // minor: битая ссылка (target не существует) → null, не выход за корень.
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
    // minor: битая symlink (target не существует) → IOException → null (читаемая, не 500).
    private static string? ComputeSha256(List<PackageFile> files)
    {
        using var hash = SHA256.Create();
        var buffer = new byte[8192];
        foreach (var f in files.OrderBy(x => x.RelativePath, StringComparer.Ordinal))
        {
            var pathBytes = Encoding.UTF8.GetBytes(f.RelativePath);
            hash.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);
            hash.TransformBlock([0], 0, 1, null, 0); // разделитель: конец записи пути
            try
            {
                using var stream = File.OpenRead(f.FullPath);
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    hash.TransformBlock(buffer, 0, read, null, 0);
            }
            catch (IOException)
            {
                // Битая symlink: target не существует. Хеш прерываем: пакет невалиден, sha не несём.
                return null;
            }
        }
        hash.TransformFinalBlock([0], 0, 1);
        return Convert.ToHexString(hash.Hash).ToLowerInvariant();
    }

    // R10-1: нативный манифест .claude-plugin/plugin.json обязателен. CLI берёт имя пакета
    // ТОЛЬКО из него (спайк plugin-dir-name-spike-2026-10.md); без него имя = имя каталога
    // (в проде {version}), и Skill-привязка по slug ломается. Корневой plugin.json — НЕ
    // нативный манифест (из allow-list убран), проверяем только .claude-plugin/plugin.json:
    //   * файла нет → deny-native-manifest-missing;
    //   * name пуст/отсутствует (или JSON бит) → deny-name-missing (name обязателен;
    //     равенство name == slug сверяет CheckNameEqualsSlug).
    private static void CheckNativeManifest(string packageRoot, List<PluginPackageProblem> problems)
    {
        const string native = ".claude-plugin/plugin.json";
        var path = Path.Combine(packageRoot, native);
        if (!File.Exists(path))
        {
            problems.Add(new PluginPackageProblem("deny-native-manifest-missing",
                $"В пакете нет нативного манифеста {native} (CLI берёт из него имя пакета; без него имя = имя каталога)"));
            return;
        }

        string? raw;
        try { raw = File.ReadAllText(path); }
        catch (IOException) { return; } // битая symlink — уже no-symlink
        var name = ReadStringProp(raw, "name");
        if (string.IsNullOrEmpty(name))
            problems.Add(new PluginPackageProblem("deny-name-missing",
                $"В нативном манифесте {native} нет name (CLI берёт из него имя пакета)"));
    }

    // M3: name == slug. CLI берёт имя пакета ТОЛЬКО из .claude-plugin/plugin.json (спайк
    // plugin-dir-name-spike-2026-10.md: ccs-plugin.json CLI не читает; без нативного манифеста
    // имя = имя каталога), а Skill-привязка адресует навык по slug из id ({publisher}/{slug}).
    // name != slug → привязка указывает в никуда. Код deny-name-not-equal-slug.
    // M3a: сверяем `name` у КАЖДОГО манифеста, где оно есть (не только у ccs-plugin.json):
    // пакет с расхождением (name в ccs-plugin.json ≠ name в .claude-plugin/plugin.json)
    // раньше проходил, т.к. проверялся только первый. slug — из id первого манифеста, несущего
    // id в формате {publisher}/{slug}; без извлекаемого slug проверка не применима (не отказ).
    private static void CheckNameEqualsSlug(IReadOnlyList<string> rawJsons, List<PluginPackageProblem> problems)
    {
        if (rawJsons.Count == 0) return;

        string? slug = null;
        foreach (var raw in rawJsons)
        {
            var id = ReadStringProp(raw, "id");
            if (id is null) continue;
            var s = SlugFromId(id);
            if (s is not null) { slug = s; break; }
        }
        if (slug is null) return;

        foreach (var raw in rawJsons)
        {
            var name = ReadStringProp(raw, "name");
            if (string.IsNullOrEmpty(name)) continue;
            if (!string.Equals(name, slug, StringComparison.Ordinal))
                problems.Add(new PluginPackageProblem(
                    "deny-name-not-equal-slug",
                    $"name манифеста «{name}» не равен slug пакета «{slug}» (CLI берёт имя пакета из .claude-plugin/plugin.json, привязка — по slug)"));
        }
    }

    // Прочитать строковое top-level поле из сырого JSON. null — поля нет, JSON бит или не объект
    // (deny-manifest-field/модель отчитаются отдельно).
    private static string? ReadStringProp(string? rawJson, string prop)
    {
        if (rawJson is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(rawJson, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (doc.RootElement.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    // slug — часть id после последнего '/'; null, если id не по формату {publisher}/{slug}.
    private static string? SlugFromId(string id)
    {
        var slash = id.LastIndexOf('/');
        if (slash < 0 || slash == id.Length - 1) return null;
        return id[(slash + 1)..];
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
            // minor: битая symlink — FileInfo.Length бросает; размер 0 (файл не читается).
            try { Size = info.Length; }
            catch (IOException) { Size = 0; }
            // Symlink — признак ReparsePoint (на Linux .NET так помечает ссылки; проверено прогоном).
            IsSymlink = (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0;
        }
    }
}
