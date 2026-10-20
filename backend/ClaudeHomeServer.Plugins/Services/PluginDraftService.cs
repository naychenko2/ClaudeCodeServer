using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Plugins;

// Черновики плагина build-it-yourself (ADR-031, шаг 4.1): агент собирает пакет в
// data/plugins/{owner}/_drafts/{slug}/. Логика создания из шаблона, записи/удаления файлов,
// валидации и сводки — одна; её используют и MCP-тулсет plugins (4.1), и наблюдение/«Принять»
// (4.2/4.3). Валидатор пакета (PluginPackageValidator) — единственный арбитр: сервис не
// ослабляет его правил (allow-list, deny-list, лимиты, нативный манифест), а лишь применяет
// те же коды проблем на записи, чтобы агент не копил невалидные файлы.
public class PluginDraftService
{
    // Шаблон черновика (R10-1): ccs-plugin.json (наш манифест, version 0.1.0, contributes пуст)
    // + .claude-plugin/plugin.json (нативный, name = slug — иначе CLI назовёт пакет именем каталога).
    public const string TemplateVersion = "0.1.0";

    private readonly PluginStore _store;
    private readonly PluginPackageValidator _validator;
    // Часы (TimeProvider): время отклонения черновика и расчёт 30-дневного хранения.
    // По умолчанию TimeProvider.System; тесты подставляют свои (без Task.Delay).
    private readonly TimeProvider _time;

    // Хранение отклонённых черновиков: 30 дней, затем ленивое удаление при перечислении.
    public const int RejectedRetentionDays = 30;
    // Метаданные отклонённого черновика (время отклонения) — файл в каталоге _rejected/{slug}.
    private const string RejectedMetaFile = "_rejected.json";

    public PluginDraftService(PluginStore store, PluginPackageValidator validator, TimeProvider? time = null)
    {
        _store = store;
        _validator = validator;
        _time = time ?? TimeProvider.System;
    }

    // slug — грамматика PluginId: [a-z0-9-]+ (часть id после '/'). Единый источник правды —
    // PluginId.IsValidSlug (его же проверяет параметр метки источника в PluginInstaller, minor 14).
    public static bool IsValidSlug(string? slug) => PluginId.IsValidSlug(slug);

    // Каталог черновика; null — slug невалиден (SafePath.Join бросил бы 500, а не 400).
    public string? DraftDir(string ownerId, string slug) =>
        IsValidSlug(slug) ? _store.DraftDir(ownerId, slug) : null;

    public bool Exists(string ownerId, string slug) =>
        DraftDir(ownerId, slug) is { } dir && Directory.Exists(dir);

    // ── draft_create ───────────────────────────────────────────────────────────
    // Создать черновик из шаблона. Повторный create того же slug → already-exists (не перезапись).
    // virtual: тесты гонки идемпотентности записи в ленту подменяют (всегда Ok), чтобы
    // оба параллельных draft_create дошли до записи в ленту, минуя already-exists.
    public virtual PluginDraftOpResult Create(string ownerId, string slug)
    {
        if (DraftDir(ownerId, slug) is not { } dir)
            return PluginDraftOpResult.Fail("bad-slug", $"Недопустимый slug: {slug}");
        if (Directory.Exists(dir))
            return PluginDraftOpResult.Fail("already-exists", $"Черновик {slug} уже существует");
        Directory.CreateDirectory(Path.Combine(dir, ".claude-plugin"));
        File.WriteAllText(Path.Combine(dir, "ccs-plugin.json"), CcsManifest(slug));
        File.WriteAllText(Path.Combine(dir, ".claude-plugin", "plugin.json"), NativeManifest(slug));
        return PluginDraftOpResult.OkResult($"Черновик {slug} создан из шаблона");
    }

    // ── draft_write_file ───────────────────────────────────────────────────────
    // Записать файл в черновик. Путь относительный, только через SafePath.Join; запись в
    // запрещённые места (deny-list, symlink, вне allow-list) и превышение лимитов — отказ
    // с тем же кодом проблемы, что у валидатора. Перезапись существующего файла допустима
    // (так агент правит).
    public PluginDraftOpResult WriteFile(string ownerId, string slug, string relativePath, byte[] content)
    {
        if (DraftDir(ownerId, slug) is not { } dir)
            return PluginDraftOpResult.Fail("bad-slug", $"Недопустимый slug: {slug}");
        if (!Directory.Exists(dir))
            return PluginDraftOpResult.Fail("not-found", $"Черновик {slug} не найден");

        // 1) Путь: относительный, без ведущего разделителя; «..» отбивает SafePath.Join.
        if (relativePath is null || relativePath.Length == 0)
            return PluginDraftOpResult.Fail("bad-path", "Путь файла пуст");
        if (relativePath.StartsWith('/') || relativePath.StartsWith('\\'))
            return PluginDraftOpResult.Fail("within-root", $"Абсолютный путь (должен быть относительным): {relativePath}");
        string full;
        try { full = SafePath.Join(dir, relativePath); }
        catch (Exception) { return PluginDraftOpResult.Fail("within-root", $"Путь выходит за корень черновика: {relativePath}"); }

        // 2) Symlink: запись в существующую символическую ссылку (target может быть вне корня) —
        //    no-symlink, тот же код, что у валидатора. GetAttributes бросает, если родительского
        //    каталога ещё нет, поэтому смотрим атрибуты только существующих записей.
        if ((File.Exists(full) || Directory.Exists(full))
            && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            return PluginDraftOpResult.Fail("no-symlink", $"Символическая ссылка: {relativePath}");

        // 3) Запрещённые места (deny-list) — тот же код, что у валидатора.
        var norm = relativePath.Replace('\\', '/');
        if (TryMatchDeny(norm, out var deny))
            return PluginDraftOpResult.Fail(deny.Code, $"{deny.Why}: {norm}");

        // 3) Allow-list: не в списке — не разрешён (тот же код, что у валидатора).
        if (!IsAllowed(norm))
            return PluginDraftOpResult.Fail("not-allowed", $"Файл не в allow-list пакета: {norm}");

        // 4) Проверки содержимого (rework D1): M10 (allowed-tools/backtick-exec) для исполняемых
        //    .md и deny-manifest-field для манифестов — тот же метод CheckContent, что у валидатора.
        //    ВСЕ проблемы (minor 14, D2): code — первой, message — склейка «код: текст» каждой
        //    (M10-сообщения уже несут путь; у манифеста путь добавляем, чтобы отказ был «с кодом
        //    и путём»). Запись отбивается, файл на диск не пишется.
        var contentProblems = _validator.CheckContent(norm, content);
        if (contentProblems.Count > 0)
        {
            var msg = string.Join("; ", contentProblems.Select(p =>
            {
                var m = p.Message.Contains(norm, StringComparison.Ordinal) ? p.Message : $"{p.Message} ({norm})";
                return $"{p.Code}: {m}";
            }));
            return PluginDraftOpResult.Fail(contentProblems[0].Code, msg);
        }

        // 5) Лимит размера отдельного файла.
        if (content.Length > PluginPackageValidator.MaxSingleFileSizeBytes)
            return PluginDraftOpResult.Fail("file-size-limit",
                $"Файл больше {PluginPackageValidator.MaxSingleFileSizeBytes / 1024 / 1024} МБ: {norm}");

        // 5b) Потолок файла панели (ADR-031 §3.6, QA-фикс H): файл под panel/** > 256 КиБ.
        if (norm.StartsWith("panel/", StringComparison.Ordinal)
            && content.Length > PluginPackageValidator.MaxPanelFileBytes)
            return PluginDraftOpResult.Fail("panel-file-too-large",
                $"Файл панели больше {PluginPackageValidator.MaxPanelFileBytes / 1024} КиБ: {norm}");

        // 6) Лимиты пакета: общий размер, число файлов и число каталогов (minor 2, после записи).
        var (fileCount, totalSize) = CountAndSize(dir);
        var existing = File.Exists(full);
        var existingSize = existing ? new FileInfo(full).Length : 0;
        if (totalSize - existingSize + content.Length > PluginPackageValidator.MaxTotalSizeBytes)
            return PluginDraftOpResult.Fail("size-limit",
                $"Пакет больше {PluginPackageValidator.MaxTotalSizeBytes / 1024 / 1024} МБ");
        if (!existing && fileCount >= PluginPackageValidator.MaxFileCount)
            return PluginDraftOpResult.Fail("file-count-limit",
                $"Файлов в пакете {fileCount + 1}, лимит {PluginPackageValidator.MaxFileCount}");
        var dirCount = CountDirs(dir, Path.GetDirectoryName(full));
        if (dirCount > PluginPackageValidator.MaxDirCount)
            return PluginDraftOpResult.Fail("dir-count-limit",
                $"Каталогов в пакете {dirCount}, лимит {PluginPackageValidator.MaxDirCount}");

        // 7) Запись (перезапись допустима).
        var parent = Path.GetDirectoryName(full);
        if (parent is not null) Directory.CreateDirectory(parent);
        File.WriteAllBytes(full, content);
        return PluginDraftOpResult.OkResult($"Файл записан: {norm}");
    }

    // ── draft_delete_file ──────────────────────────────────────────────────────
    // Убрать файл из черновика. Путь тем же правилам (SafePath.Join, внутри корня).
    public PluginDraftOpResult DeleteFile(string ownerId, string slug, string relativePath)
    {
        if (DraftDir(ownerId, slug) is not { } dir)
            return PluginDraftOpResult.Fail("bad-slug", $"Недопустимый slug: {slug}");
        if (!Directory.Exists(dir))
            return PluginDraftOpResult.Fail("not-found", $"Черновик {slug} не найден");
        if (relativePath is null || relativePath.Length == 0)
            return PluginDraftOpResult.Fail("bad-path", "Путь файла пуст");
        if (relativePath.StartsWith('/') || relativePath.StartsWith('\\'))
            return PluginDraftOpResult.Fail("within-root", $"Абсолютный путь: {relativePath}");
        string full;
        try { full = SafePath.Join(dir, relativePath); }
        catch (Exception) { return PluginDraftOpResult.Fail("within-root", $"Путь выходит за корень черновика: {relativePath}"); }
        if (!File.Exists(full))
            return PluginDraftOpResult.Fail("not-found", $"Файл не найден: {relativePath}");
        File.Delete(full);
        return PluginDraftOpResult.OkResult($"Файл удалён: {relativePath}");
    }

    // ── draft_validate ─────────────────────────────────────────────────────────
    // Валидатор пакета (тот же арбитр, что при установке: файлы + манифест) по корню
    // черновика. ValidateFull — одна реализация с PluginInstaller: коды проблем 1:1.
    public PluginPackageValidationResult Validate(string ownerId, string slug)
    {
        if (DraftDir(ownerId, slug) is not { } dir)
            throw new ArgumentException($"Недопустимый slug: {slug}", nameof(slug));
        if (!Directory.Exists(dir))
            throw new FileNotFoundException($"Черновик {slug} не найден", nameof(slug));
        return _validator.ValidateFull(dir);
    }

    // ── draft_preview ──────────────────────────────────────────────────────────
    // Сводка «что будет после установки» для карточки: вклады, запрошенные права и секреты
    // (с признаком обязательного), дерево файлов, результат последней проверки. Без
    // содержимого секретов, без хостовых путей (только относительные).
    public PluginDraftPreview Preview(string ownerId, string slug)
    {
        if (DraftDir(ownerId, slug) is not { } dir)
            throw new ArgumentException($"Недопустимый slug: {slug}", nameof(slug));
        if (!Directory.Exists(dir))
            throw new FileNotFoundException($"Черновик {slug} не найден", nameof(slug));

        var manifest = ReadManifest(dir);
        var files = new List<PluginDraftFile>();
        var validation = _validator.ValidateFull(dir);
        AddTree(dir, dir, files);
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        return new PluginDraftPreview(
            slug,
            manifest?.Name ?? slug,
            manifest?.Version ?? TemplateVersion,
            manifest?.Contributes?.Panels?.Select(p => new PluginDraftPanel(p.Id, p.Slot, p.Title)).ToList() ?? [],
            manifest?.Contributes?.Commands ?? [],
            manifest?.Contributes?.Skills ?? [],
            manifest?.Permissions?.Requested ?? [],
            manifest?.Contributes?.Requires?.Secrets?.Select(s => new PluginDraftSecret(s.Name, s.Required)).ToList() ?? [],
            files,
            new PluginDraftValidation(validation.IsValid, validation.Problems.Select(p => new PluginDraftProblem(p.Code, p.Message)).ToList()));
    }

    // ── draft_list (REST, шаги 4.3a/4.3c) ─────────────────────────────────────
    // Список черновиков владельца: активные (_drafts) + отклонённые (_rejected, признак
    // rejected: true + дата, когда исчезнут). Поля: slug, id, версия, время изменения, число
    // проблем последней проверки, признак «установлена такая же версия». Валидатор — тот же
    // (считаем проблемы), id/version — из манифеста, время — max mtime файлов черновика,
    // «такая же версия» — сверка id+version с записями installed.json. Просроченные отклонённые
    // (> 30 дней) удаляются лениво здесь, без фонового сервиса.
    public List<PluginDraftSummary> List(string ownerId)
    {
        var installed = _store.LoadInstalled(ownerId);
        var result = new List<PluginDraftSummary>();

        // Активные черновики: _drafts/{slug} (rejected: false, без даты исчезновения).
        var draftsRoot = SafePath.Join(_store.OwnerRoot(ownerId), PluginStore.DraftsDirName);
        if (Directory.Exists(draftsRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(draftsRoot))
            {
                var slug = Path.GetFileName(dir);
                var (manifest, _) = PluginInstaller.ReadManifest(dir);
                var id = manifest?.Id ?? "";
                var version = manifest?.Version ?? TemplateVersion;
                var validation = _validator.ValidateFull(dir);
                result.Add(new PluginDraftSummary(
                    slug, id, version, MaxFileMtimeUtc(dir), validation.Problems.Count,
                    installed.Any(r => r.Id == id && r.Version == version),
                    Rejected: false, ExpiresAt: null));
            }
        }

        // Отклонённые черновики: _rejected/{slug}; просроченные (> 30 дней) — ленивое удаление.
        var rejectedRoot = SafePath.Join(_store.OwnerRoot(ownerId), PluginStore.RejectedDirName);
        if (Directory.Exists(rejectedRoot))
        {
            var now = _time.GetUtcNow().UtcDateTime;
            foreach (var dir in Directory.EnumerateDirectories(rejectedRoot))
            {
                var slug = Path.GetFileName(dir);
                var meta = LoadRejectedMeta(dir);
                var rejectedAt = meta?.RejectedAt ?? MaxFileMtimeUtc(dir);
                var expiresAt = rejectedAt.AddDays(RejectedRetentionDays);
                if (expiresAt <= now)
                {
                    // Просрочен: ленивое удаление (каталог + метаданные), из списка не идёт.
                    try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
                    continue;
                }
                var (manifest, _) = PluginInstaller.ReadManifest(dir);
                var id = manifest?.Id ?? "";
                var version = manifest?.Version ?? TemplateVersion;
                var validation = _validator.ValidateFull(dir);
                result.Add(new PluginDraftSummary(
                    slug, id, version, MaxFileMtimeUtc(dir), validation.Problems.Count,
                    installed.Any(r => r.Id == id && r.Version == version),
                    Rejected: true, ExpiresAt: expiresAt));
            }
        }

        return result;
    }

    // ── reject (REST, шаг 4.3c) ─────────────────────────────────────────────────
    // Перенести черновик из _drafts/{slug} в служебный _rejected/{slug} (вне наблюдения)
    // и записать время отклонения. Перенос атомарный (Directory.Move), время — из часов.
    public PluginDraftOpResult Reject(string ownerId, string slug)
    {
        if (DraftDir(ownerId, slug) is not { } dir)
            return PluginDraftOpResult.Fail("bad-slug", $"Недопустимый slug: {slug}");
        if (!Directory.Exists(dir))
            return PluginDraftOpResult.Fail("not-found", $"Черновик {slug} не найден");

        var rejectedDir = _store.RejectedDir(ownerId, slug);
        if (Directory.Exists(rejectedDir))
            return PluginDraftOpResult.Fail("already-rejected", $"Отклонённый черновик {slug} уже существует");

        _store.RunExclusive(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(rejectedDir)!);
            Directory.Move(dir, rejectedDir);
            SaveRejectedMeta(rejectedDir, _time.GetUtcNow().UtcDateTime);
        });
        return PluginDraftOpResult.OkResult($"Черновик {slug} отклонён");
    }

    // ── restore (REST, шаг 4.3c) ────────────────────────────────────────────────
    // Вернуть отклонённый черновик из _rejected/{slug} обратно в _drafts/{slug}.
    // Если _drafts/{slug} уже занят (новый черновик) → conflict (409).
    public PluginDraftOpResult Restore(string ownerId, string slug)
    {
        if (DraftDir(ownerId, slug) is not { } draftDir)
            return PluginDraftOpResult.Fail("bad-slug", $"Недопустимый slug: {slug}");

        var rejectedDir = _store.RejectedDir(ownerId, slug);
        if (!Directory.Exists(rejectedDir))
            return PluginDraftOpResult.Fail("not-found", $"Отклонённый черновик {slug} не найден");
        if (Directory.Exists(draftDir))
            return PluginDraftOpResult.Fail("conflict", $"Черновик {slug} уже существует");

        _store.RunExclusive(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(draftDir)!);
            Directory.Move(rejectedDir, draftDir);
            // Служебные метаданные отклонения (_rejected.json) — не файл пакета: убираем их из
            // восстановленного черновика, иначе валидатор отбьёт файл not-allowed и «Принять»
            // заблокируется (дефект c5afd409). 30-дневная очистка и чтение времени отклонения
            // работают по _rejected/{slug} (RejectedMeta/List), а не по восстановленному
            // _drafts/{slug} — файл больше не нужен; повторный Reject создаст новый.
            var metaFile = Path.Combine(draftDir, RejectedMetaFile);
            if (File.Exists(metaFile)) File.Delete(metaFile);
        });
        return PluginDraftOpResult.OkResult($"Черновик {slug} возвращён");
    }

    // Метаданные отклонённого черновика (время отклонения); null — не отклонён или slug невалиден.
    // Публичный: контроллер использует его в ответах reject/restore.
    public PluginRejectedMeta? RejectedMeta(string ownerId, string slug) =>
        DraftDir(ownerId, slug) is { } _ ? LoadRejectedMeta(_store.RejectedDir(ownerId, slug)) : null;

    // Метаданные отклонённого черновика: записать время отклонения (UTC) в _rejected.json.
    private static void SaveRejectedMeta(string rejectedDir, DateTime rejectedAt) =>
        JsonFileStore.Save(Path.Combine(rejectedDir, RejectedMetaFile), new PluginRejectedMeta(rejectedAt));

    // Прочитать метаданные; null — файла нет или JSON бит (forward-compat).
    private static PluginRejectedMeta? LoadRejectedMeta(string rejectedDir) =>
        JsonFileStore.Load<PluginRejectedMeta>(Path.Combine(rejectedDir, RejectedMetaFile));

    // Время изменения черновика = max LastWriteTimeUtc всех файлов (рекурсивно); нет файлов → MinValue.
    private static DateTime MaxFileMtimeUtc(string dir)
    {
        var max = DateTime.MinValue;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                var t = new FileInfo(file).LastWriteTimeUtc;
                if (t > max) max = t;
            }
            catch (IOException) { }
        }
        return max;
    }

    // Дерево файлов черновика (рекурсивно): каталоги с «/» и размером 0, файлы с размером.
    // <paramref name="root"/> — корень черновика (для относительных путей), <paramref name="current"/> — текущий.
    private static void AddTree(string root, string current, List<PluginDraftFile> files)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(current))
        {
            var rel = Path.GetRelativePath(root, entry).Replace('\\', '/');
            if (Directory.Exists(entry))
            {
                files.Add(new PluginDraftFile(rel + "/", true, 0));
                AddTree(root, entry, files);
            }
            else
            {
                files.Add(new PluginDraftFile(rel, false, new FileInfo(entry).Length));
            }
        }
    }

    // Прочитать наш манифест ccs-plugin.json; null — файла нет или JSON бит (forward-compat).
    private static PluginManifest? ReadManifest(string dir)
    {
        var path = Path.Combine(dir, "ccs-plugin.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException) { return null; }
    }

    // Число файлов и суммарный размер черновика (для лимитов записи).
    private static (int FileCount, long TotalSize) CountAndSize(string dir)
    {
        int count = 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            count++;
            try { total += new FileInfo(file).Length; } catch (IOException) { }
        }
        return (count, total);
    }

    // Число каталогов черновика (рекурсивно, без корня) + новый родительский каталог файла,
    // если его ещё нет. Тот же смысл, что лимит каталогов валидатора (MaxDirCount, minor 2).
    private static int CountDirs(string dir, string? newParent)
    {
        int count = 0;
        foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
            count++;
        if (newParent is not null && newParent != dir && !Directory.Exists(newParent))
            count++;
        return count;
    }

    // ── Шаблоны ────────────────────────────────────────────────────────────────

    // Наш манифест: id/name = slug, version 0.1.0, contributes пуст (агент заполнит).
    private static string CcsManifest(string slug) => JsonSerializer.Serialize(new
    {
        id = slug,
        name = slug,
        version = TemplateVersion,
        contributes = new PluginContributes(),
    }, JsonOpts);

    // Нативный манифест CLI: name = slug (R10-1) — из него CLI берёт имя пакета.
    private static string NativeManifest(string slug) => JsonSerializer.Serialize(new { name = slug }, JsonOpts);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    // ── Правила валидатора (копии, чтобы коды совпадали 1:1) ─────────────────────
    // Запрещённые каталоги (RCE/обход): префикс относительного пути → код проблемы.
    // Те же префиксы/коды, что в PluginPackageValidator.DenyDirs.
    private static readonly (string Prefix, string Code, string Why)[] DenyDirs =
    [
        ("hooks/",  "deny-hooks",  "hooks/ исполняет shell (RCE)"),
        ("agents/", "deny-agents", "agents/ добавляет сабагента"),
        ("bin/",    "deny-bin",    "bin/ — исполняемое в корне пакета"),
    ];

    // Запрещённые файлы в корне пакета (RCE/обход). Те же имена/коды, что в PluginPackageValidator.DenyFiles.
    private static readonly (string Name, string Code, string Why)[] DenyFiles =
    [
        (".mcp.json",    "deny-mcp-json", ".mcp.json регистрирует MCP-сервер в обход allow-list"),
        ("settings.json", "deny-settings", "settings.json — глобальный override"),
    ];

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
    // Та же логика, что в PluginPackageValidator.IsAllowed.
    private static bool IsAllowed(string rel)
    {
        foreach (var m in new[] { "ccs-plugin.json", ".claude-plugin/plugin.json" })
            if (rel == m) return true;
        if (rel.StartsWith("skills/", StringComparison.Ordinal))
        {
            var name = Path.GetFileName(rel);
            if (name == "SKILL.md" || name.EndsWith(".md", StringComparison.Ordinal)) return true;
        }
        if (rel.StartsWith("commands/", StringComparison.Ordinal)
            && !rel["commands/".Length..].Contains('/')
            && rel.EndsWith(".md", StringComparison.Ordinal))
            return true;
        if (rel.StartsWith("panel/", StringComparison.Ordinal)) return true;
        return false;
    }
}

// Результат операции с черновиком: статус + машиночитаемый код + текст для человека.
public sealed record PluginDraftOpResult(bool Ok, string? Code, string? Message)
{
    public static PluginDraftOpResult OkResult(string message) => new(true, null, message);
    public static PluginDraftOpResult Fail(string code, string message) => new(false, code, message);
}

// Сводка черновика «что будет после установки» (draft_preview).
public sealed class PluginDraftPreview
{
    public string Slug { get; }
    public string Name { get; }
    public string Version { get; }
    public List<PluginDraftPanel> Panels { get; }
    public List<string> Commands { get; }
    public List<string> Skills { get; }
    public List<string> RequestedPermissions { get; }
    public List<PluginDraftSecret> Secrets { get; }
    public List<PluginDraftFile> Files { get; }
    public PluginDraftValidation Validation { get; }

    public PluginDraftPreview(
        string slug, string name, string version,
        List<PluginDraftPanel> panels, List<string> commands, List<string> skills,
        List<string> requestedPermissions, List<PluginDraftSecret> secrets,
        List<PluginDraftFile> files, PluginDraftValidation validation)
    {
        Slug = slug; Name = name; Version = version;
        Panels = panels; Commands = commands; Skills = skills;
        RequestedPermissions = requestedPermissions; Secrets = secrets;
        Files = files; Validation = validation;
    }
}

// Сводка черновика для списка (REST, шаги 4.3a/4.3c): slug, id, версия, время изменения,
// число проблем последней проверки, признак «установлена такая же версия», признак «отклонён»
// (rejected) и дата, когда отклонённый черновик исчезнет (ExpiresAt; null у активных).
public sealed record PluginDraftSummary(
    string Slug,
    string Id,
    string Version,
    DateTime UpdatedAt,
    int Problems,
    bool InstalledSameVersion,
    bool Rejected,
    DateTime? ExpiresAt);

// Метаданные отклонённого черновика (шаг 4.3c): время отклонения (UTC). Хранится в
// _rejected/{slug}/_rejected.json; из него считается 30-дневное хранение (ExpiresAt).
public sealed record PluginRejectedMeta(DateTime RejectedAt);

public sealed record PluginDraftPanel(string Id, string Slot, string Title);
public sealed record PluginDraftSecret(string Name, bool Required);
public sealed record PluginDraftFile(string Path, bool IsDir, long Size);
public sealed record PluginDraftProblem(string Code, string Message);
public sealed class PluginDraftValidation
{
    public bool IsValid { get; }
    public List<PluginDraftProblem> Problems { get; }
    public PluginDraftValidation(bool isValid, List<PluginDraftProblem> problems)
    {
        IsValid = isValid;
        Problems = problems;
    }
}
