namespace ClaudeHomeServer.Services.ImageEditor;

// Каталог редактора для фронта (ADR-017, раздел 2): заведённые поставщики с пометкой, доступны ли
// они СЕЙЧАС, их курируемые модели с caps и умолчание админа места image-editor. Чистая функция
// над драйверами — без DI и без состояния, поэтому её не нужно регистрировать.
//
// Ненастроенный поставщик скрыт, а не роняет ответ: драйвер, у которого проверка
// доступности или список моделей бросили исключение, считается недоступным.
public static class ImageEditCatalog
{
    public const string AutoModelId = "auto";
    public const string AutoModelLabel = "Авто";

    // Операции «со своей моделью»: модель подбирает сервер, вариант один, персонаж не едет.
    // Зеркалит quickUsesOwnModel в frontend/src/features/imageEditor/editorInputs.ts — менять вместе
    public static readonly IReadOnlySet<ImageEditOp> OwnModelOps = new HashSet<ImageEditOp> { ImageEditOp.EnhanceFaces };

    // Порядок поставщиков в списке и при умолчании «auto»; незнакомый ключ — в конец
    private const string LocalProviderKey = "local";

    public static readonly string[] ProviderOrder = ["fal", "higgsfield", "local"];

    public static readonly ImageEditLimitsDto DefaultLimits = new(MaxFileMb: 20, MaxReferences: 6, MaxCount: 4);

    // Лимиты входа моделей для автоуменьшения (ADR-018 §9): длинная сторона, мегапиксели, вес.
    // Курируются здесь, а не в драйверах, как и остальные caps каталога. Значения
    // консервативные: 2048 px по длинной стороне модели правки держат без потери качества
    // результата, а 48 Мп с телефона гонять к поставщику незачем. Модели улучшения качества
    // (Topaz) нет в таблице намеренно: ужатие входа съело бы смысл операции.
    private static readonly (int Side, double Megapixels, double Mb) EditInput = (2048, 4.2, 10);

    public static readonly IReadOnlyDictionary<string, (int Side, double Megapixels, double Mb)> InputLimits =
        new Dictionary<string, (int, double, double)>(StringComparer.OrdinalIgnoreCase)
        {
            // fal
            ["fal-ai/nano-banana-2/edit"] = EditInput,
            ["fal-ai/nano-banana-pro/edit"] = EditInput,
            ["fal-ai/flux-pro/kontext"] = EditInput,
            ["fal-ai/flux-pro/kontext/max/multi"] = EditInput,
            ["fal-ai/flux-pro/v1/fill"] = EditInput,
            ["fal-ai/bria/expand"] = EditInput,
            ["fal-ai/bria/background/remove"] = EditInput,
            ["fal-ai/nano-banana-2"] = EditInput,
            // Higgsfield
            ["nano_banana_2"] = EditInput,
            ["nano_banana_2_lite"] = EditInput,
            ["nano_banana_pro"] = EditInput,
            ["gpt_image_2_5"] = EditInput,
            ["flux_kontext"] = EditInput,
            ["seedream_v5_pro"] = EditInput,
            ["flux_2_pro_outpaint"] = EditInput,
            ["image_background_remover"] = EditInput,
            // Локальные модели. Энкодер правки Qwen-Image берёт холст в его размере (resolution 0),
            // поэтому держим его у верхней границы родной сетки модели: 1664 по длинной стороне
            // (16:9 — 1664×928), 1,8 Мп (1:1 — 1328×1328). FaceDetailer работает кропами лиц, ему
            // крупный холст по силам; 30 МБ — потолок входа local-media
            ["qwen-image-2.1"] = (1664, 1.8, 30),
            ["face-detailer"] = (4096, 16.8, 30),
        };

    // Модель с курируемыми лимитами входа; лимит, заданный драйвером явно, не перетирается
    public static ImageEditModelInfo WithInputLimits(ImageEditModelInfo model)
    {
        if (!InputLimits.TryGetValue(model.Id, out var limit)) return model;
        var caps = model.Caps;
        return model with
        {
            Caps = caps with
            {
                MaxInputSide = caps.MaxInputSide ?? limit.Side,
                MaxInputMegapixels = caps.MaxInputMegapixels ?? limit.Megapixels,
                MaxInputMb = caps.MaxInputMb ?? limit.Mb,
            },
        };
    }

    // Режимы переключателя при модели «Авто»
    public static readonly IReadOnlyList<EditMode> AutoModes = [EditMode.Fast, EditMode.Precise, EditMode.Photoreal];

    // Доступные поставщики в порядке показа
    public static IReadOnlyList<IImageEditor> Available(IEnumerable<IImageEditor> editors) =>
        editors
            .Where(IsAvailable)
            .OrderBy(e => OrderOf(e.Key))
            .ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Заведённые поставщики (в том числе лежащие сейчас) в порядке показа
    public static IReadOnlyList<IImageEditor> Registered(IEnumerable<IImageEditor> editors) =>
        editors
            .Where(IsRegistered)
            .OrderBy(e => OrderOf(e.Key))
            .ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static IImageEditor? FindAvailable(IEnumerable<IImageEditor> editors, string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : Available(editors).FirstOrDefault(e => string.Equals(e.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

    // Текст отказа provider_unavailable: заведённый, но лежащий поставщик — «сейчас недоступен»,
    // а не «не настроен»: админ его настроил, и человеку незачем идти к нему
    public static string UnavailableError(IEnumerable<IImageEditor> editors, string? key)
    {
        var registered = string.IsNullOrWhiteSpace(key)
            ? null
            : Registered(editors).FirstOrDefault(e => string.Equals(e.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
        return registered is null
            ? $"Поставщик «{key}» не настроен или отключён администратором"
            : $"Поставщик «{registered.Label}» сейчас недоступен";
    }

    // adminProvider / adminModel — настройка места image-editor (auto | ключ поставщика).
    // Явный выбор админа не подменяется соседом (инвариант модуля «явно выбранного поставщика не
    // подменяем», ImageEditor/CLAUDE.md): он ищется среди ЗАВЕДЁННЫХ, и лежащий local остаётся
    // умолчанием — запуск на него честно отказывает с котировкой облака (retryQuote), а не уходит
    // в облако молча. Прежняя уступка первому доступному тратила деньги там, где админ выбрал
    // бесплатное. «Авто» по-прежнему берёт первого ДОСТУПНОГО по ProviderOrder; local в нём
    // последний намеренно: доступность мигает, и «Авто» с local первым молча уходило бы в облако.
    public static ImageEditCatalogDto Build(
        IEnumerable<IImageEditor> editors, string? adminProvider, string? adminModel,
        ImageEditLimitsDto? limits = null, bool preferLocal = false)
    {
        var providers = Registered(editors).Select(e => Describe(e, IsAvailable(e))).ToList();

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
            // «Авто» при local-media-default (флаг владельца) идёт на свою видеокарту первой, если она доступна:
            // облако — только явным выбором. Явный выбор админа сюда не попадает (ветка выше)
            var local = preferLocal ? providers.FirstOrDefault(p => p.Available && p.Key == LocalProviderKey) : null;
            def = new ImageEditDefaultDto((local ?? providers.FirstOrDefault(p => p.Available))?.Key, AutoModelId);
        }

        var reason = providers.Count == 0 ? ImageEditCatalogReasons.NoProviderConfigured : null;
        return new ImageEditCatalogDto(def, providers, limits ?? DefaultLimits, reason);
    }

    // Модель есть в каталоге поставщика («auto» есть всегда)
    public static bool HasModel(IImageEditor editor, string? model) =>
        !string.IsNullOrWhiteSpace(model)
        && (string.Equals(model.Trim(), AutoModelId, StringComparison.OrdinalIgnoreCase)
            || SafeModels(editor).Any(m => string.Equals(m.Id, model.Trim(), StringComparison.OrdinalIgnoreCase)));

    private static ImageEditProviderDto Describe(IImageEditor editor, bool available)
    {
        var models = new List<ImageEditModelDto>
        {
            new(AutoModelId, AutoModelLabel, AutoModes, null, null),
        };
        models.AddRange(SafeModels(editor).Select(WithInputLimits)
            .Select(m => new ImageEditModelDto(m.Id, m.Label, null, m.Caps, m.PriceHint)));
        return new ImageEditProviderDto(editor.Key, editor.Label, editor.PriceUnit, models, available);
    }

    private static bool IsAvailable(IImageEditor editor)
    {
        try
        {
            return IsRegistered(editor) && editor.Enabled;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRegistered(IImageEditor editor)
    {
        try
        {
            return editor.Registered && !string.IsNullOrWhiteSpace(editor.Key);
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
