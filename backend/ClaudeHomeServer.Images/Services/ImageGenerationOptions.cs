namespace ClaudeHomeServer.Services.Images;

// Места применения генератора. Место одно — аватар персоны (там лицо, SVG не годится);
// иконка проекта ушла из картинок: значок подбирается текстовым местом модели и
// собирается как SVG (ADR-009). Ключи стабильные: уходят в API и в UI и совпадают с
// ImageBackfillKinds.* — заявка очереди сама называет своё место.
public static class ImagePlaces
{
    public const string PersonaAvatar = "persona-avatar";

    public static readonly string[] All = [PersonaAvatar];

    public static string TitleOf(string? place) => place switch
    {
        PersonaAvatar => "Аватар персоны",
        _ => place ?? "",
    };

    // Приводит ключ к каноническому виду; null — место неизвестно (валидировать на входе API)
    public static string? Normalize(string? place)
    {
        if (string.IsNullOrWhiteSpace(place)) return null;
        var trimmed = place.Trim();
        return All.FirstOrDefault(p => string.Equals(p, trimmed, StringComparison.OrdinalIgnoreCase));
    }
}

// Секция конфига Images — дефолты инстанса для выбора генератора картинок. Задаются на все
// места сразу: конфиг машины отвечает на вопрос «чем рисовать по умолчанию», а разницу по
// местам человек настраивает в UI (её хранит ImageGenerationSettingsStore, data/image-generation.json).
public sealed class ImageGenerationOptions
{
    public const string SectionName = "Images";

    // Режим «выбрать самому»: первый доступный провайдер в порядке ProviderOrder
    public const string Auto = "auto";

    // Порядок предпочтения при auto: glif первый — в этом режиме человек делегировал выбор
    // модели, а подбор модели под смысл промпта у glif штатный; у fal модель одна, зашитая
    // настройкой. Нет токена glif — идёт fal.
    public static readonly string[] ProviderOrder = ["glif", "fal"];

    // auto | fal | glif. Пусто — auto.
    public string? Provider { get; set; }

    // Модель по умолчанию на провайдера: ключ провайдера → id модели.
    // Пусто — дефолт самого драйвера.
    public Dictionary<string, string> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsAuto(string? provider) =>
        string.IsNullOrWhiteSpace(provider) || string.Equals(provider, Auto, StringComparison.OrdinalIgnoreCase);

    // Индекс провайдера в порядке auto-выбора; незнакомый ключ уезжает в конец
    public static int OrderOf(string? key)
    {
        var idx = Array.FindIndex(ProviderOrder, k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        return idx < 0 ? int.MaxValue : idx;
    }
}
