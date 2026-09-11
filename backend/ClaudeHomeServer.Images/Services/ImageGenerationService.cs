namespace ClaudeHomeServer.Services.Images;

// Роутер генерации картинок: единая точка входа вместо прямой привязки к fal. Драйверы
// приходят из DI (IEnumerable<IImageGenerator>), режим и модели — из
// ImageGenerationSettingsStore, и берутся ПО МЕСТУ (ImagePlaces.*): у иконки проекта и
// аватара персоны настройка своя.
//
// auto = первый ДОСТУПНЫЙ провайдер в порядке ImageGenerationOptions.ProviderOrder
// (glif → fal) плюс фолбэк на следующего, если первый вернул пусто (нет ключа у генерации,
// отвалился эндпоинт). Явный выбор провайдера фолбэка НЕ даёт: админ выбрал конкретный
// сервис — молча уходить на другой нельзя.
public sealed class ImageGenerationService
{
    private readonly IReadOnlyList<IImageGenerator> _generators;
    private readonly ImageGenerationSettingsStore _settings;
    private readonly ILogger<ImageGenerationService>? _log;

    public ImageGenerationService(
        IEnumerable<IImageGenerator> generators,
        ImageGenerationSettingsStore settings,
        ILogger<ImageGenerationService>? log = null)
    {
        // Порядок фиксируем сами: он определяет auto-выбор и не должен зависеть
        // от порядка регистрации в DI.
        _generators = generators
            .OrderBy(g => ImageGenerationOptions.OrderOf(g.Key))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settings = settings;
        _log = log;
    }

    // Все зарегистрированные драйверы (в порядке auto-выбора) — для UI настроек
    public IReadOnlyList<IImageGenerator> Providers => _generators;

    // Хоть один провайдер настроен. Не то же, что EnabledFor: место могло явно выбрать
    // выключенный сервис. По этому признаку догоняющая генерация решает, ждать ли ей вовсе.
    public bool AnyEnabled => _generators.Any(g => g.Enabled);

    // Текущий режим места: auto | ключ провайдера
    public string ModeFor(string place) => _settings.ProviderFor(place);

    // Провайдер, который отработает следующий запрос этого места; null — генерация недоступна
    public IImageGenerator? ActiveProviderFor(string place) => Resolve(place).FirstOrDefault();

    // Генерация доступна в этом месте
    public bool EnabledFor(string place) => ActiveProviderFor(place) is not null;

    // Модель, с которой провайдер пойдёт в этом месте (настройка → конфиг → дефолт драйвера = null)
    public string? ModelFor(string place, string providerKey) => _settings.ModelFor(place, providerKey);

    public async Task<IReadOnlyList<GeneratedImage>> GenerateManyAsync(
        string place, string prompt, int count, CancellationToken ct = default)
    {
        var chain = Resolve(place);
        if (chain.Count == 0)
        {
            _log?.LogWarning("Генерация картинок недоступна для места {Place}: нет включённого провайдера (режим {Mode})",
                place, ModeFor(place));
            return [];
        }

        for (var i = 0; i < chain.Count; i++)
        {
            var generator = chain[i];
            var images = await generator.GenerateManyAsync(prompt, count, _settings.ModelFor(place, generator.Key), ct);
            if (images.Count > 0) return images;
            if (i + 1 < chain.Count)
                _log?.LogWarning("Генератор {Provider} не вернул картинок для места {Place} — пробую {Next}",
                    generator.Key, place, chain[i + 1].Key);
        }
        return [];
    }

    // Одна картинка (первый вариант) — для мест, где выбора не предлагается
    public async Task<GeneratedImage?> GenerateAsync(string place, string prompt, CancellationToken ct = default)
    {
        var many = await GenerateManyAsync(place, prompt, 1, ct);
        return many.Count > 0 ? many[0] : null;
    }

    // Сохранить настройку места. Возвращает текст ошибки или null при успехе.
    public string? UpdateSettings(string place, string? provider, IDictionary<string, string?>? models = null)
    {
        if (Validate(place, provider, models) is { } error) return error;
        _settings.Save(ImagePlaces.Normalize(place)!, provider, models);
        return null;
    }

    // Проверка патча места без сохранения: место, ключи провайдера и моделей знает только
    // роутер. Нужна отдельно, чтобы запрос на несколько мест не применялся наполовину.
    public string? Validate(string place, string? provider, IDictionary<string, string?>? models = null)
    {
        var key = ImagePlaces.Normalize(place);
        if (key is null) return $"Неизвестное место генерации картинок: {place}";

        if (!string.IsNullOrWhiteSpace(provider) && !ImageGenerationOptions.IsAuto(provider))
        {
            var chosen = FindGenerator(provider);
            if (chosen is null) return $"Неизвестный генератор картинок: {provider}";
            // Явный выбор фолбэка не даёт, поэтому ненастроенный сервис означал бы молча
            // неработающую генерацию в этом месте — отказываем на входе.
            if (!chosen.Enabled)
                return $"Генератор «{chosen.DisplayName}» не настроен — добавьте ключ доступа в appsettings.Local.json";
        }

        if (models is not null)
        {
            foreach (var (providerKey, value) in models)
            {
                var generator = FindGenerator(providerKey);
                if (generator is null) return $"Неизвестный генератор картинок: {providerKey}";
                if (string.IsNullOrWhiteSpace(value)) continue;
                // Курируемый список пуст — драйвер принимает любой id (напр. произвольный глиф)
                if (generator.Models.Count > 0
                    && !generator.Models.Any(m => string.Equals(m.Id, value.Trim(), StringComparison.OrdinalIgnoreCase)))
                    return $"Модель «{value}» недоступна у генератора {generator.DisplayName}";
            }
        }

        return null;
    }

    private IImageGenerator? FindGenerator(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : _generators.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase));

    // Цепочка исполнения запроса места: первый элемент — кто пойдёт сейчас, остальные —
    // фолбэк (только в режиме auto). Выключенные провайдеры в цепочку не попадают.
    private IReadOnlyList<IImageGenerator> Resolve(string place)
    {
        var mode = _settings.ProviderFor(place);
        if (!ImageGenerationOptions.IsAuto(mode))
        {
            var chosen = FindGenerator(mode);
            return chosen is { Enabled: true } ? [chosen] : [];
        }
        return _generators.Where(g => g.Enabled).ToList();
    }
}
