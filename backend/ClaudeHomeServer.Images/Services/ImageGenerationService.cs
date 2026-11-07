using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Spend;

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
//
// Здесь же пишется трата серверной генерации в общий учёт расхода (дефект 22068e9d,
// шаг S-SPEND-2): роутер — единственное место, где известен ФАКТИЧЕСКИ отработавший
// провайдер (после фолбэка) и модель, с которой он пошёл. Прецедент — редактор картинок
// (ImageEditJobService.RecordSpend/WriteSpend).
public sealed class ImageGenerationService
{
    // Подписи записей в общем учёте: по ним серверная генерация отличима от правки в
    // редактора («image-editor») и от генерации в ходе чата через MCP (там Label —
    // endpoint или тип вывода).
    public const string SpendLabel = "image-generation";
    public const string SpendLabelPendingAmount = "image-generation: сумма уточняется";

    private readonly IReadOnlyList<IImageGenerator> _generators;
    private readonly ImageGenerationSettingsStore _settings;
    private readonly ILogger<ImageGenerationService>? _log;
    private readonly ISpendCollector? _spend;
    private readonly IFalRequestCost? _falCost;

    public ImageGenerationService(
        IEnumerable<IImageGenerator> generators,
        ImageGenerationSettingsStore settings,
        ILogger<ImageGenerationService>? log = null,
        ISpendCollector? spend = null,
        IFalRequestCost? falCost = null)
    {
        // Порядок фиксируем сами: он определяет auto-выбор и не должен зависеть
        // от порядка регистрации в DI.
        _generators = generators
            .OrderBy(g => ImageGenerationOptions.OrderOf(g.Key))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settings = settings;
        _log = log;
        _spend = spend;
        _falCost = falCost;
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

    // ownerId — тот, на кого пишется трата (RecordSpend): вызывающий знает владельца всегда,
    // роутер — нет. Параметр стоит ПОСЛЕ ct, чтобы существующие вызовы с токеном не
    // сдвинулись. Без владельца трата не пишется: повесить её не на кого.
    // initiator — кто подал операцию (SpendInitiators.*): его знает только точка входа
    // (REST-контроллер — человек, MCP-тулсет — агент), роутер его не вычисляет и не подменяет
    // (находка ревью R-S-SPEND-2). null — инициатор вызова не известен (фоновая очередь);
    // выдумывать человека не будем: поле остаётся null, как у записей без инициатора.
    public async Task<IReadOnlyList<GeneratedImage>> GenerateManyAsync(
        string place, string prompt, int count, CancellationToken ct = default, string? ownerId = null,
        string? initiator = SpendInitiators.Human)
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
            var model = _settings.ModelFor(place, generator.Key);
            var images = await generator.GenerateManyAsync(prompt, count, model, ct);
            // Трата — только за того, кто реально отдал картинки: пустой провайдер не стоил ничего
            if (images.Count > 0)
            {
                RecordSpend(place, generator, model, images, ownerId, initiator);
                return images;
            }
            if (i + 1 < chain.Count)
                _log?.LogWarning("Генератор {Provider} не вернул картинок для места {Place} — пробую {Next}",
                    generator.Key, place, chain[i + 1].Key);
        }
        return [];
    }

    // Одна картинка (первый вариант) — для мест, где выбора не предлагается
    public async Task<GeneratedImage?> GenerateAsync(
        string place, string prompt, CancellationToken ct = default, string? ownerId = null,
        string? initiator = SpendInitiators.Human)
    {
        var many = await GenerateManyAsync(place, prompt, 1, ct, ownerId, initiator);
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

    // ── Учёт расхода (дефект 22068e9d, шаг S-SPEND-2) ────────────────────────────────
    //
    // По прецеденту редактора картинок (ImageEditJobService.RecordSpend/WriteSpend):
    // первая запись — ВСЕГДА и сразу после того, как провайдер отдал картинки: число
    // вариантов, суммы нет (Label — SpendLabelPendingAmount). Фактическую сумму догоняет
    // ОТДЕЛЬНАЯ запись с Generations = 0. Валюты не смешиваем: у этого пути только CostUsd.
    //
    // Траты нет, когда: коллектора нет (учёт выключен), владельца нет (повесить не на
    // кого), провайдер не отдал картинок (за них не платили).

    private void RecordSpend(
        string place, IImageGenerator generator, string? model,
        IReadOnlyList<GeneratedImage> images, string? ownerId, string? initiator)
    {
        if (_spend is null)
        {
            _log?.LogWarning("Генерация картинок: учёт расхода выключен, трата {Place} ({Provider}) не записана",
                place, generator.Key);
            return;
        }
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            // Вызывающий, который ещё не назвал владельца (часть путей — следующий шаг):
            // молча класть чужую трату в «системную» не будем — её потом не разделить.
            _log?.LogWarning("Генерация картинок: вызывающий не назвал владельца, трата {Place} ({Provider}) не записана",
                place, generator.Key);
            return;
        }

        WriteSpend(ownerId, generator.Key, model, amount: null, generations: images.Count, initiator);

        // Сумму догоняет только fal: в ответах glif (compose_project / get_job_status) нет
        // идентификатора, по которому его цена спрашивается — GlifJobParser берёт jobId, media
        // и status, поля цены там отсутствуют. Трата glif остаётся счётчиком вызовов, как и в
        // пишущейся записи чата (SpendMapping.RecordGlifGeneration: CostUsd = null).
        var requestId = RequestIdOf(images);
        if (requestId is null || _falCost is null
            || !string.Equals(generator.Key, SpendSources.Fal, StringComparison.OrdinalIgnoreCase)) return;
        _ = TopUpAmountAsync(ownerId, model, requestId, initiator);
    }

    // Фактическая цена по request_id — через существующий Core-шов IFalRequestCost
    // (тот же экземпляр FalCostService, что и у чата). Свой токен: token вызывающего к этому
    // моменту уже снят вместе с его запросом, а окно опроса ограничено самим FalCostService.
    // Цены нет — штатный исход: остаётся первая запись без суммы.
    private async Task TopUpAmountAsync(string ownerId, string? model, string requestId, string? initiator)
    {
        try
        {
            var usd = await _falCost!.ResolveUsdAsync(requestId, CancellationToken.None);
            if (usd is null)
            {
                _log?.LogWarning("Учёт расхода: цена fal по {RequestId} не появилась за окно опроса, трата остаётся без суммы",
                    requestId);
                return;
            }
            // Догоняющая запись — про тот же вызов: инициатор у неё тот же, что у первой
            WriteSpend(ownerId, SpendSources.Fal, model, amount: usd, generations: 0, initiator);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Учёт расхода: опрос цены fal ({RequestId}) не удался", requestId);
        }
    }

    private void WriteSpend(string ownerId, string providerKey, string? model, double? amount, int generations,
        string? initiator)
    {
        try
        {
            _spend!.Record(new SpendRecord
            {
                Timestamp = DateTime.UtcNow,
                OwnerId = ownerId,
                // Серверная генерация не относится к проекту: «Расход» не должен получить
                // несуществующий проект (тот же приём, что у личной области редактора).
                ProjectId = null,
                Provider = providerKey,
                // null — драйвер пошёл на свою дефолтную модель: её знает только драйвер,
                // выдумывать за него не будем.
                Model = model,
                // Ключ провайдера и есть ключ источника (как в прецеденте): у fal/glif токенов
                // нет, их расход — счётчик вызовов (SpendSources.IsTokenless).
                Source = providerKey,
                CostUsd = amount,
                Generations = generations,
                // Инициатор — тот, кто подал операцию, каким его назвала точка входа
                // (находка ревью R-S-SPEND-2: агентский путь жив — personas_generate_avatar и
                // personas_create с AutoAvatar из чата доходят ровно сюда через PersonasCrudService).
                // null — инициатор не известен: фоновая очередь заявки его пока не несёт (S-SPEND-8).
                Initiator = initiator,
                Label = amount is null && generations > 0 ? SpendLabelPendingAmount : SpendLabel,
            });
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Генерация картинок: не удалось записать трату {Provider}", providerKey);
        }
    }

    // id один на весь запрос — его несёт каждая картинка этого вызова
    private static string? RequestIdOf(IReadOnlyList<GeneratedImage> images) =>
        images.Select(i => i.RequestId).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
}
