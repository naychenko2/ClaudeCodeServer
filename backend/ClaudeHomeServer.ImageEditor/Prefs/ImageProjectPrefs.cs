using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Threads;

namespace ClaudeHomeServer.Services.ImageEditor.Prefs;

// Выбор человека в полосе «Картинки» без выбранной картинки: поставщик, модель, число вариантов,
// «размер оригинала» и подключённый персонаж. На владельца и проект; новая нить получает из них
// свои Settings, а запуск агентом без аргументов — поставщика, модель и персонажа.
// Create и Edit — выбор режимов «Создать» и «Править» панели v5; плоские поля — запасные для
// обоих (старый фронт пишет только их). Персонаж и «размер оригинала» общие на оба режима.
// Добавлять поля можно только аддитивно: файл живёт в бэкапе.
public sealed record ImageProjectPrefs(string? Provider, string? Model, int Count, bool MatchSourceSize, string? CharacterSlug,
    ImageCreatePrefs? Create = null, ImageEditPrefs? Edit = null)
{
    public static ImageProjectPrefs Default { get; } = new(null, null, 2, true, null);

    // Есть ли выбор по режимам: без него блок хода и MCP ведут себя как до режимов
    public bool HasModes => Create is not null || Edit is not null;

    // Настройки генерации по тексту: выбор «Создать», иначе плоские поля
    public ImageThreadSettings CreateSettings() => Settings(Create?.Provider, Create?.Model, Create?.Count);

    // Настройки правки (и новой нити): выбор «Править», иначе плоские поля
    public ImageThreadSettings EditSettings() => Settings(Edit?.Provider, Edit?.Model, Edit?.Count);

    // Поставщик и модель — парой: модель плоских полей могла быть выбрана у другого поставщика
    private ImageThreadSettings Settings(string? provider, string? model, int? count) =>
        provider is null && model is null
            ? new(Provider, Model, count ?? Count, MatchSourceSize)
            : new(provider, model, count ?? Count, MatchSourceSize);
}

// Режим «Создать»: null — берётся плоское поле
public sealed record ImageCreatePrefs(string? Provider, string? Model, int? Count);

// Режим «Править»: Op — операция панели (без generate), EditMode — режим подбора модели «Авто»,
// Ratio — пропорции «Дорисовать за края». Строками, как их шлёт фронт; проверка — белыми списками
public sealed record ImageEditPrefs(string? Provider, string? Model, int? Count, string? Op, string? EditMode, string? Ratio);

// data/image-editor-prefs/{ownerId}/{projectId}.json. Нет файла или он битый — умолчания
public sealed class ImageProjectPrefsStore(string root)
{
    public const string DirName = "image-editor-prefs";

    private readonly Lock _gate = new();

    public string Root { get; } = root;

    public static ImageProjectPrefsStore FromConfig(IConfiguration config)
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        return new ImageProjectPrefsStore(Path.Combine(Path.GetDirectoryName(dataPath)!, DirName));
    }

    public ImageProjectPrefs Get(string ownerId, string projectId)
    {
        var path = PathOf(ownerId, projectId);
        lock (_gate)
        {
            try
            {
                if (File.Exists(path)
                    && JsonSerializer.Deserialize<ImageProjectPrefs>(File.ReadAllText(path), ImageThreadStore.Json) is { } prefs)
                    return prefs;
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
            return ImageProjectPrefs.Default;
        }
    }

    // Сохранял ли человек выбор явно: Get без файла отдаёт умолчания, и по нему это не отличить
    public bool Exists(string ownerId, string projectId)
    {
        var path = PathOf(ownerId, projectId);
        lock (_gate)
            return File.Exists(path);
    }

    // Через временный файл: оборванная запись не оставит битый JSON
    public void Save(string ownerId, string projectId, ImageProjectPrefs prefs)
    {
        var path = PathOf(ownerId, projectId);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(prefs, ImageThreadStore.Json));
            File.Move(tmp, path, overwrite: true);
        }
    }

    // Чтение и запись под одним замком: слияние PUT не теряет параллельную запись
    public ImageProjectPrefs Update(string ownerId, string projectId, Func<ImageProjectPrefs, ImageProjectPrefs> change)
    {
        lock (_gate)
        {
            var next = change(Get(ownerId, projectId));
            Save(ownerId, projectId, next);
            return next;
        }
    }

    public string PathOf(string ownerId, string projectId) =>
        Path.Combine(Root, Safe(ownerId), Safe(projectId) + ".json");

    // id владельца — из claim, проекта — из маршрута; маршрут приходит снаружи, отсюда белый список
    private static string Safe(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment.Contains("..")
            || segment.IndexOfAny(['/', '\\', ':']) >= 0 || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Недопустимый идентификатор", nameof(segment));
        return segment;
    }
}

// Чтение с проверкой персонажа и запись с событием image_prefs_changed владельцу. Проект —
// уже свой: владение проверяет вызывающий (ручка, тулсет, блок хода)
public sealed class ImageProjectPrefsService(
    ImageProjectPrefsStore store,
    ILogger<ImageProjectPrefsService> log,
    IProjectManager? projects = null,
    ISessionBroadcaster? broadcaster = null)
{
    // Настройки для новой нити — выбор «Править»: настройки нити нужны только правке. Персонаж в
    // них не входит, проект открывать незачем
    public ImageThreadSettings SettingsFor(string ownerId, string projectId) =>
        store.Get(ownerId, projectId).EditSettings();

    // Персонаж, которого уже нет в проекте (удалили), читается как null; у личной области
    // персонажей нет вовсе — всегда null
    public ImageProjectPrefs Get(string ownerId, ImageEditScope scope)
    {
        var prefs = store.Get(ownerId, scope.Key);
        return prefs.CharacterSlug is { } slug
               && (scope.Project is not { } project || CharacterStore.Get(project.RootPath, slug) is null)
            ? prefs with { CharacterSlug = null }
            : prefs;
    }

    public ImageProjectPrefs Get(string ownerId, Project project) => Get(ownerId, ImageEditScope.Of(project));

    // Для блока хода, где на руках только id проекта: чужой или пропавший проект — умолчания
    public ImageProjectPrefs Get(string ownerId, string projectId) =>
        projects?.GetById(projectId) is { } project && project.OwnerId == ownerId
            ? Get(ownerId, project)
            : ImageProjectPrefs.Default;

    // Выбор в полосе сохранён явно; чужой или пропавший проект — нет
    public bool HasSaved(string ownerId, string projectId) =>
        projects?.GetById(projectId) is { } project && project.OwnerId == ownerId
        && store.Exists(ownerId, projectId);

    // То же по области: у личной — файл выбора владельца (строковая версия на «personal» молча
    // false — такого проекта нет), у проекта — с проверкой владения
    public bool HasSaved(string ownerId, ImageEditScope scope) =>
        scope.IsPersonal ? store.Exists(ownerId, ImageEditScope.Personal) : HasSaved(ownerId, scope.Key);

    // Операции режима «Править»: все, кроме генерации по тексту — она живёт в «Создать»
    public static readonly IReadOnlySet<string> EditOps = Enum.GetValues<ImageEditOp>()
        .Where(op => op != ImageEditOp.Generate).Select(Camel).ToHashSet(StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> EditModes = Enum.GetValues<EditMode>()
        .Select(Camel).ToHashSet(StringComparer.Ordinal);

    // Проверка тела PUT …/prefs — общая для ручек проекта и личного чата; null — годится
    public static string? Validate(ImageProjectPrefs? req)
    {
        if (req is null || !CountOk(req.Count) || !CountOk(req.Create?.Count ?? 1) || !CountOk(req.Edit?.Count ?? 1))
            return $"Число вариантов — от 1 до {ImageEditCatalog.DefaultLimits.MaxCount}";
        if (req.CharacterSlug is { Length: > 0 } slug && !CharacterStore.IsValidSlug(slug.Trim()))
            return "Недопустимый персонаж";
        if (req.Edit?.Op is { } op && !EditOps.Contains(op))
            return $"Недопустимая операция правки: {op}";
        if (req.Edit?.EditMode is { } mode && !EditModes.Contains(mode))
            return $"Недопустимый режим подбора: {mode}";
        if (req.Edit?.Ratio is { } ratio && !ImageEditLaunchAssembler.AspectRatios.Contains(ratio))
            return $"Пропорции {ratio} не поддерживаются: только {string.Join(", ", ImageEditLaunchAssembler.AspectRatios)}";
        return null;
    }

    private static bool CountOk(int count) => count >= 1 && count <= ImageEditCatalog.DefaultLimits.MaxCount;

    private static string Camel<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    public Task<ImageProjectPrefs> SetAsync(string ownerId, Project project, ImageProjectPrefs prefs) =>
        SetAsync(ownerId, ImageEditScope.Of(project), prefs);

    // Персонаж в префы больше не пишется (он ref контекста чата, ADR-023): в теле игнорируется, а то, что
    // записано в старом файле, остаётся как есть. Запись сливается с сохранённым: нет Create/Edit в теле (старый фронт, другая вкладка) —
    // выбор режима остаётся прежним; присланный режим заменяется целиком
    public async Task<ImageProjectPrefs> SetAsync(string ownerId, ImageEditScope scope, ImageProjectPrefs prefs)
    {
        store.Update(ownerId, scope.Key, saved => prefs with
        {
            Provider = Blank(prefs.Provider),
            Model = Blank(prefs.Model),
            CharacterSlug = saved.CharacterSlug,
            Create = prefs.Create is { } create
                ? create with { Provider = Blank(create.Provider), Model = Blank(create.Model) }
                : saved.Create,
            Edit = prefs.Edit is { } edit
                ? edit with { Provider = Blank(edit.Provider), Model = Blank(edit.Model) }
                : saved.Edit,
        });
        var saved = Get(ownerId, scope);
        if (broadcaster is not null)
        {
            try
            {
                await broadcaster.ToOwner(ownerId, new ImageProjectPrefsChangedMessage(scope.Key, saved));
            }
            catch (Exception ex)
            {
                // Потерянное событие фронт догоняет GET …/prefs
                log.LogDebug(ex, "Редактор картинок: настройки области {ScopeKey} не разосланы", scope.Key);
            }
        }
        return saved;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
