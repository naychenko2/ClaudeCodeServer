using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Plugins;

// Ядро установки плагина (ADR-031 §2.3, шаг 1.4, C1/C2/M5/M6). Вынесено из контроллера,
// чтобы тот оставался тонким: контроллер — гейты + маппинг на HTTP, а здесь — вся логика:
//   C1  метка источника (draft:/upload:/catalog:) → каталог через SafePath.Join;
//   M6  повторная установка (совпал sha) — no-op, Granted/Secrets не сбрасываются;
//   M5  атомарность: копирование во временный .staging-{guid} → Directory.Move;
//   C2  requested — только из манифеста, granted = [] (человек выдаёт через grant).
public class PluginInstaller
{
    // Метки источника (белый список, C1).
    private const string DraftPrefix = "draft:";
    private const string UploadPrefix = "upload:";
    private const string CatalogPrefix = "catalog:";

    private readonly PluginStore _store;
    private readonly PluginPackageValidator _validator;

    public PluginInstaller(PluginStore store, PluginPackageValidator validator)
    {
        _store = store;
        _validator = validator;
    }

    // Установить пакет владельца <paramref name="ownerId"/> по метке источника
    // <paramref name="sourceLabel"/>. Возвращает результат (успех / no-op / 409 / ошибка).
    public InstallResult Install(string ownerId, string sourceLabel)
    {
        // C1: метка из белого списка, не произвольный путь.
        var (kind, param) = ParseSource(sourceLabel);
        if (kind is null)
            return InstallResult.Fail("Source — метка из белого списка (draft:/upload:/catalog:)");

        // minor 2: параметр метки — по грамматике slug (не путь): «draft:../../x» → 400,
        // а не UnauthorizedAccessException (500) от SafePath.Join; пустой «draft:» → 400.
        if (!IsLabelParamValid(param))
            return InstallResult.Fail($"Недопустимый параметр метки источника: {sourceLabel}");

        // Каталог для чтения — из метки через SafePath.Join (не путь из тела).
        string? sourceDir = kind switch
        {
            "draft" => _store.DraftDir(ownerId, param),
            "upload" => _store.UploadDir(ownerId, param),
            // catalog: метка валидна, но каталог каталога появится позже (MVP — draft/upload).
            _ => null,
        };
        if (sourceDir is null)
            return InstallResult.Fail("Источник catalog: ещё не подключён (MVP — draft:/upload:)");
        if (!Directory.Exists(sourceDir))
            return InstallResult.Fail($"Каталог источника не найден: {sourceLabel}");

        // M5: атомарная установка — сначала копирование во временный .staging-{guid}
        // (своя рекурсия, не заходит в reparse-point'ы), затем валидация и хеш КОПИИ,
        // затем Directory.Move поверх старого каталога (нет «половинного» пакета на диске).
        // TOCTOU: staging-каталог — в packages/ (сibling целевого), чтобы id/version из
        // staging-манифеста не требовали PackageDir ДО копирования.
        var packagesRoot = SafePath.Join(_store.OwnerRoot(ownerId), PluginStore.PackagesDirName);
        var stagingDir = Path.Combine(packagesRoot, $".staging-{Guid.NewGuid():N}");
        string? sha256 = null;
        PluginManifest? manifest = null;
        string? id = null;
        string? version = null;
        try
        {
            Directory.CreateDirectory(stagingDir);
            CopyDirectoryNoSymlinks(sourceDir, stagingDir);

            // 1) Валидация КОПИИ (staging) — ДО чтения манифеста: symlink-манифест
            // (ccs-plugin.json/.claude-plugin/plugin.json → чужой файл) отбивается no-symlink валидатором,
            // и JsonException с куском содержимого чужого файла не уходит в ответ 400
            // (non-blocker: leak через symlink manifest). sha256 — того, что будет перемещено.
            var validation = _validator.Validate(stagingDir);
            if (!validation.IsValid)
                return InstallResult.FailValidation(validation.Problems);
            sha256 = validation.Sha256!;

            // 2) Манифест — из STAGING-копии (не источника): запись и пакет не разойдутся
            // (TOCTOU: источник мог измениться между чтением манифеста и копированием).
            // Читается ПОСЛЕ валидации: symlink-манифест уже отбит (no-symlink).
            var (readManifest, manifestError) = ReadManifest(stagingDir);
            if (manifestError is not null)
                return InstallResult.Fail(manifestError);
            if (readManifest is null || string.IsNullOrWhiteSpace(readManifest.Id) || string.IsNullOrWhiteSpace(readManifest.Version))
                return InstallResult.Fail("Манифест пакета не найден или без id/version");
            PluginManifest m = readManifest; // non-null после проверки выше

            // M3: формат id/version из манифеста — 400 ДО PackageDir (иначе «..» в id/version
            // дал бы UnauthorizedAccessException = 500, а не 400). Единый метод PluginId/PluginVersion.
            if (!PluginId.IsValid(m.Id))
                return InstallResult.Fail($"Недопустимый формат id пакета: {m.Id}");
            if (!PluginVersion.IsValid(m.Version))
                return InstallResult.Fail($"Недопустимый формат version пакета: {m.Version}");
            id = m.Id;
            version = m.Version;

            // manifest.Validate(): неизвестное право / нет вклада → 400, и пакет ещё не
            // перемещён (раньше валидировался только в grant; установка могла принять битый
            // манифест, а проверка после Move дала бы «половинную» установку).
            var manifestValidation = m.Validate();
            if (!manifestValidation.IsValid)
                return InstallResult.FailValidation(manifestValidation.Problems
                    .Select(p => new PluginPackageProblem(p.Code, p.Message)).ToList());

            manifest = m;

            var targetDir = _store.PackageDir(ownerId, id, version);

            // M6: повторная установка того же пакета (совпал sha) — no-op: вернуть
            // существующую запись, Granted/Secrets не сбрасываем.
            var existing = _store.LoadInstalled(ownerId).FirstOrDefault(r => r.Id == id);
            if (existing is not null && existing.Sha256 == sha256)
                return InstallResult.NoOp(existing);

            // 409: запись с тем же id, но sha256 пакета изменился → «обновить».
            if (existing is not null && existing.Sha256 != sha256)
                return InstallResult.Conflict(id, version);

            // Атомарная замена: удалить старый, переместить копию. Родитель target
            // (packages/{id}) создаём явно: staging лежит в packages/, а не в packages/{id}/.
            // RunExclusive: delete+move под lock-ем стора — два параллельных accept одного id
            // не гоняются за targetDir (иначе Directory.Move → IOException «Directory not empty»).
            _store.RunExclusive(() =>
            {
                if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
                Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
                Directory.Move(stagingDir, targetDir);
            });
        }
        finally
        {
            if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
        }

        // Запись PluginInstall: requested из манифеста, granted = [] (C2), source = метка (C1).
        // sha256 здесь не null: валидация копии прошла (иначе return FailValidation внутри try).
        var record = new PluginInstall
        {
            Id = id!,
            Version = version!,
            Sha256 = sha256!,
            Requested = manifest!.Permissions?.Requested ?? [],
            Granted = [],
            Enabled = true,
            Source = sourceLabel,
        };
        // minor 1: load→save под одной блокировкой (PluginStore.Update), не два отдельных вызова.
        _store.Update(ownerId, records =>
        {
            records.RemoveAll(r => r.Id == id);
            records.Add(record);
            return records;
        });

        return InstallResult.Ok(record);
    }

    // Разобрать метку источника: (вид, параметр). null — метка не из белого списка (C1).
    private static (string? Kind, string Param) ParseSource(string source)
    {
        if (source.StartsWith(DraftPrefix, StringComparison.Ordinal))
            return ("draft", source[DraftPrefix.Length..].Trim());
        if (source.StartsWith(UploadPrefix, StringComparison.Ordinal))
            return ("upload", source[UploadPrefix.Length..].Trim());
        if (source.StartsWith(CatalogPrefix, StringComparison.Ordinal))
            return ("catalog", source[CatalogPrefix.Length..].Trim());
        return (null, source);
    }

    // minor 2: параметр метки (slug после «draft:»/«upload:») — по грамматике slug
    // (как slug в PluginId, [a-z0-9-]+): непустой, без разделителей пути, без «..» и «.»
    // (иначе SafePath.Join даст 500, а не 400). Единый источник правды — PluginId.IsValidSlug
    // (minor 14, D2): та же проверка, что у черновиков (PluginDraftService.IsValidSlug).
    private static bool IsLabelParamValid(string? param) =>
        !string.IsNullOrWhiteSpace(param) && PluginId.IsValidSlug(param);

    // Манифест пакета: ccs-plugin.json (наш) → .claude-plugin/plugin.json (нативный) (порядок
    // allow-list валидатора). R10-1: корневой plugin.json — не нативный манифест, из порядка убран.
    // (null, null) — нет; (null, текст) — ошибка разбора (minor: не
    // глотаем, отдаём текстом, чтобы битый манифест не выглядел как «не найден»).
    // Публичный: контроллер использует его в grant (проверка requires.secrets).
    public static (PluginManifest? Manifest, string? Error) ReadManifest(string packageRoot)
    {
        foreach (var name in new[] { "ccs-plugin.json", ".claude-plugin/plugin.json" })
        {
            var path = Path.Combine(packageRoot, name);
            if (!System.IO.File.Exists(path)) continue;
            try
            {
                return (JsonSerializer.Deserialize<PluginManifest>(
                    System.IO.File.ReadAllText(path),
                    new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }), null);
            }
            catch (Exception ex)
            {
                return (null, $"Ошибка разбора манифеста {name}: {ex.Message}");
            }
        }
        return (null, null);
    }

    // M5: рекурсивное копирование пакета (source → target), которое НЕ заходит в
    // reparse-point'ы (symlink'и): обычный файл копируется, обычный каталог — рекурсия,
    // symlink (файл или каталог) воссоздаётся как ссылка (валидатор копии видит и отбивает
    // его как no-symlink), внутрь symlink-каталога не спускаемся. Старый
    // Directory.EnumerateFiles(AllDirectories) заходил в symlink-каталоги и копировал
    // содержимое target'а как файлы пакета (TOCTOU + обход).
    private static void CopyDirectoryNoSymlinks(string source, string target)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var rel = Path.GetRelativePath(source, entry);
            var dest = Path.Combine(target, rel);
            var isLink = (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0;

            if (Directory.Exists(entry))
            {
                if (isLink)
                {
                    // Symlink-каталог: воссоздать ссылку, внутрь не спускаться.
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    CreateSymlink(entry, dest);
                }
                else
                {
                    Directory.CreateDirectory(dest);
                    CopyDirectoryNoSymlinks(entry, dest);
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (isLink)
                    CreateSymlink(entry, dest); // symlink-файл: воссоздать, не следовать
                else
                    System.IO.File.Copy(entry, dest, overwrite: true);
            }
        }
    }

    // Воссоздать symlink <paramref name="link"/> → target'у <paramref name="entry"/>
    // (target берём как он задан, resolveFinalComponent=false). Битая/непубличная ссылка —
    // не фатально: просто не создаём (валидатор и так отбьёт no-symlink по исходнику).
    // Ловим и UnauthorizedAccessException (noexec/ACL на staging-каталоге): иначе 500,
    // а битая ссылка молча выпадала бы из копии.
    private static void CreateSymlink(string entry, string dest)
    {
        try
        {
            var target = new FileInfo(entry).ResolveLinkTarget(false)?.FullName;
            if (target is not null) File.CreateSymbolicLink(dest, target);
        }
        catch (IOException) { /* битая ссылка — см. выше */ }
        catch (UnauthorizedAccessException) { /* нет прав на создание ссылки — см. выше */ }
    }
}

// Статус результата установки.
public enum InstallStatus
{
    /// <summary>Установлено (новая запись).</summary>
    Ok,
    /// <summary>Повторная установка (совпал sha) — no-op, существующая запись возвращена.</summary>
    NoOp,
    /// <summary>Пакет изменился (sha256) — «обновить» (409).</summary>
    Conflict,
    /// <summary>Ошибка (400): метка, манифест, валидация, каталог.</summary>
    Fail,
}

// Результат установки: статус + (запись | текст ошибки | проблемы валидации).
public sealed class InstallResult
{
    public InstallStatus Status { get; }
    public PluginInstall? Record { get; }
    public string? Error { get; }
    public IReadOnlyList<PluginPackageProblem>? Problems { get; }

    private InstallResult(InstallStatus status, PluginInstall? record, string? error, IReadOnlyList<PluginPackageProblem>? problems)
    {
        Status = status;
        Record = record;
        Error = error;
        Problems = problems;
    }

    public static InstallResult Ok(PluginInstall record) => new(InstallStatus.Ok, record, null, null);
    public static InstallResult NoOp(PluginInstall record) => new(InstallStatus.NoOp, record, null, null);
    public static InstallResult Conflict(string id, string version) =>
        new(InstallStatus.Conflict, null, "Пакет изменился (sha256 не совпадает с записью) — обновите", null);
    public static InstallResult Fail(string error) => new(InstallStatus.Fail, null, error, null);
    public static InstallResult FailValidation(IReadOnlyList<PluginPackageProblem> problems) =>
        new(InstallStatus.Fail, null, "Пакет не прошёл валидацию", problems);
}
