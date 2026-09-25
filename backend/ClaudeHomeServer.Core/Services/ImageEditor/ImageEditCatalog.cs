namespace ClaudeHomeServer.Services.ImageEditor;

// Каталог редактора для фронта (ADR-016, раздел 2): только поставщики, доступные СЕЙЧАС,
// их курируемые модели с caps и умолчание админа места image-editor. Чистая функция над
// драйверами — без DI и без состояния, поэтому её не нужно регистрировать.
//
// Ненастроенный поставщик скрыт, а не роняет ответ: драйвер, у которого проверка
// доступности или список моделей бросили исключение, считается недоступным.
public static class ImageEditCatalog
{
    public const string AutoModelId = "auto";
    public const string AutoModelLabel = "Авто";

    // Порядок поставщиков в списке и при умолчании «auto»; незнакомый ключ — в конец
    public static readonly string[] ProviderOrder = ["fal", "higgsfield"];

    public static readonly ImageEditLimitsDto DefaultLimits = new(MaxFileMb: 20, MaxReferences: 6, MaxCount: 4);

    // Режимы переключателя при модели «Авто»
    public static readonly IReadOnlyList<EditMode> AutoModes = [EditMode.Fast, EditMode.Precise, EditMode.Photoreal];

    // Доступные поставщики в порядке показа
    public static IReadOnlyList<IImageEditor> Available(IEnumerable<IImageEditor> editors) =>
        editors
            .Where(IsAvailable)
            .OrderBy(e => OrderOf(e.Key))
            .ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static IImageEditor? FindAvailable(IEnumerable<IImageEditor> editors, string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : Available(editors).FirstOrDefault(e => string.Equals(e.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

    // adminProvider / adminModel — настройка места image-editor (auto | ключ поставщика).
    // Умолчание админа на недоступного поставщика молча уступает первому доступному:
    // человек ещё ничего не выбрал и не потратил.
    public static ImageEditCatalogDto Build(
        IEnumerable<IImageEditor> editors, string? adminProvider, string? adminModel,
        ImageEditLimitsDto? limits = null)
    {
        var available = Available(editors);
        var providers = available.Select(Describe).ToList();

        ImageEditDefaultDto def;
        var preferred = IsAuto(adminProvider) ? null : providers.FirstOrDefault(p =>
            string.Equals(p.Key, adminProvider!.Trim(), StringComparison.OrdinalIgnoreCase));
        if (preferred is not null)
        {
            var model = !string.IsNullOrWhiteSpace(adminModel)
                        && preferred.Models.Any(m => string.Equals(m.Id, adminModel.Trim(), StringComparison.OrdinalIgnoreCase))
                ? adminModel.Trim()
                : AutoModelId;
            def = new ImageEditDefaultDto(preferred.Key, model);
        }
        else
        {
            def = new ImageEditDefaultDto(providers.FirstOrDefault()?.Key, AutoModelId);
        }

        return new ImageEditCatalogDto(def, providers, limits ?? DefaultLimits);
    }

    // Модель есть в каталоге поставщика («auto» есть всегда)
    public static bool HasModel(IImageEditor editor, string? model) =>
        !string.IsNullOrWhiteSpace(model)
        && (string.Equals(model.Trim(), AutoModelId, StringComparison.OrdinalIgnoreCase)
            || SafeModels(editor).Any(m => string.Equals(m.Id, model.Trim(), StringComparison.OrdinalIgnoreCase)));

    private static ImageEditProviderDto Describe(IImageEditor editor)
    {
        var models = new List<ImageEditModelDto>
        {
            new(AutoModelId, AutoModelLabel, AutoModes, null, null),
        };
        models.AddRange(SafeModels(editor).Select(m => new ImageEditModelDto(m.Id, m.Label, null, m.Caps, m.PriceHint)));
        return new ImageEditProviderDto(editor.Key, editor.Label, editor.PriceUnit, models);
    }

    private static bool IsAvailable(IImageEditor editor)
    {
        try
        {
            return editor.Enabled && !string.IsNullOrWhiteSpace(editor.Key);
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<ImageEditModelInfo> SafeModels(IImageEditor editor)
    {
        try
        {
            return editor.Models ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static bool IsAuto(string? provider) =>
        string.IsNullOrWhiteSpace(provider) || string.Equals(provider.Trim(), AutoModelId, StringComparison.OrdinalIgnoreCase);

    private static int OrderOf(string key)
    {
        var idx = Array.FindIndex(ProviderOrder, k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        return idx < 0 ? int.MaxValue : idx;
    }
}
