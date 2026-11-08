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
    // Платный запрос fal без единой картинки: отличает эту запись от догоняющей записи
    // удачного запроса (у обеих Generations = 0).
    public const string SpendLabelNoImages = "image-generation: без картинок";

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

    // ownerId и initiator — обязательные параметры ПЕРЕД ct (S-SPEND-8): их знает только
    // вызывающий, и компилятор не даст забыть их оба. Пустая строка — осознанный выбор
    // вызывающего («не знаю»), а не забытый аргумент: роутер пишет об этом в лог и
    // генерацию не роняет (из-за учёта расхода нельзя отказать в аватаре).
    // ownerId — тот, на кого пишется трата (RecordSpend): без владельца записи нет, чужому
    // трата не приписывается. Для персоны проекта это арендатор — владелец проекта, как в
    // чатовом пути (SessionManager.ResolveOwnerId), а не «владелец персоны по совпадению»
    // (п.3 ревью R-S-SPEND-2).
    // initiator — кто подал операцию (SpendInitiators.*): точка входа (REST — человек,
    // MCP-тулсет — агент, фоновая заявка — сохранённый в ней), роутер его не вычисляет и
    // не подменяет (находка ревью R-S-SPEND-2); пустой — записи остаётся null (не размечено).
    // actorUserId — кто исполнил операцию (п.3): ключ хранилища остаётся владелец, актор —
    // разрез (SpendRecord.ActorUserId). В отличие от владельца его отсутствие запись не
    // портит: «нет поля → владелец» — штатное чтение стора. Поэтому параметр необязательный:
    // называет его тот, кто знает актора (интерактивный вызов; заявка очереди — из своих
    // полей атрибуции); заявка, которая их не несёт (поставленная до этих полей), не выдумывает.
    // projectId — проект траты (п.4): аватар персоны проекта — достояние проекта, и его трата
    // должна быть видна в разрезе по проекту. null — генерация вне проекта; осиротевший
    // проект не называется: «Расход» не должен получить несуществующий проект.
    public async Task<IReadOnlyList<GeneratedImage>> GenerateManyAsync(
        string place, string prompt, int count, string ownerId, string initiator,
        string? actorUserId = null, string? projectId = null, CancellationToken ct = default)
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
            var generated = await generator.GenerateManyAsync(prompt, count, model, ct);
            var images = generated.Images;
            // Трата — только за того, кто реально отдал картинки: пустой провайдер не стоил ничего
            if (images.Count > 0)
            {
                RecordSpend(place, generator, model, images, generated.RequestId, ownerId, initiator,
                    actorUserId, projectId);
                return images;
            }
            // Пустой ответ fal с id запроса мог быть платным (скачивания упали, 4xx после работы
            // GPU): цену спрашиваем ДО перехода к следующему провайдеру, чтобы фолбэк не съел трату
            RecordNoImagesSpend(place, generator, model, generated.RequestId, ownerId, initiator,
                actorUserId, projectId);
            // Отмена вызывающего доходит до роутера пустым результатом с id: так её отдаёт драйвер
            // после принятого ответа (см. catch в FalImageService). Трату по принятому запросу мы
            // уже оформили строкой выше — теперь цепочку фолбэка не продолжаем и отмену отдаём
            // выше: проглотить её здесь значит после отмены человека вызвать следующего провайдера
            // и вернуть [] как «генерация не удалась» (п.1 хвоста R-S-SPEND-9a).
            ct.ThrowIfCancellationRequested();
            if (i + 1 < chain.Count)
                _log?.LogWarning("Генератор {Provider} не вернул картинок для места {Place} — пробую {Next}",
                    generator.Key, place, chain[i + 1].Key);
        }
        return [];
    }

    // Одна картинка (первый вариант) — для мест, где выбора не предлагается
    public async Task<GeneratedImage?> GenerateAsync(
        string place, string prompt, string ownerId, string initiator,
        string? actorUserId = null, string? projectId = null, CancellationToken ct = default)
    {
        var many = await GenerateManyAsync(place, prompt, 1, ownerId, initiator, actorUserId, projectId, ct);
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
    // вариантов, суммы нет. Фактическую сумму догоняет ОТДЕЛЬНАЯ запись с Generations = 0.
    // Валюты не смешиваем: у этого пути только CostUsd.
    //
    // Два отличия от прецедента видны только здесь: есть ли ручка, по которой цену спросят
    // вообще (у glif её нет ни в каком ответе — подпись «уточняется» ему не ставят) и какой
    // моделью провайдер пошёл (это знает только драйвер, у него и спрашиваем).
    //
    // Траты нет, когда: коллектора нет (учёт выключен), владельца нет (повесить не на
    // кого), провайдер не отдал картинок (за них не платили) — кроме пустого запроса fal с
    // id: там единственная запись с SpendLabelNoImages, и только при цене > 0 (RecordNoImagesSpend);
    // она несёт ту же атрибуцию (владелец, актор, проект, инициатор), что и обычная.

    private void RecordSpend(
        string place, IImageGenerator generator, string? model,
        IReadOnlyList<GeneratedImage> images, string? requestId, string ownerId, string initiator,
        string? actorUserId, string? projectId)
    {
        if (_spend is null)
        {
            _log?.LogWarning("Генерация картинок: учёт расхода выключен, трата {Place} ({Provider}) не записана",
                place, generator.Key);
            return;
        }
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            // Вызывающий назвал пустого владельца: молча класть чужую трату в «системную»
            // не будем — её потом не разделить. Генерация при этом остаётся удавшейся.
            _log?.LogWarning("Генерация картинок: вызывающий не назвал владельца, трата {Place} ({Provider}) не записана",
                place, generator.Key);
            return;
        }

        // Пустой инициатор — то же «не размечено», что и отсутствие поля: отдельного
        // значения у пустой строки нет, иначе у одного факта стало бы два признака (S-SPEND-8)
        var named = string.IsNullOrWhiteSpace(initiator) ? null : initiator;
        if (named is null)
            _log?.LogWarning("Генерация картинок: вызывающий не назвал инициатора, трата {Place} ({Provider}) записана без него",
                place, generator.Key);

        // Фактическая модель вызова: названная настройкой места или та, на которую пошёл
        // сам драйвер (контракт IImageGenerator.GenerateManyAsync «model = null → дефолтная
        // модель драйвера», она же IImageGenerator.DefaultModel). Драйверу model так и не
        // навязываем: он сам решает свой дефолт, правка только в том, что несёт запись.
        // Пустая модель в учёте резолвится аналитикой в дефолт подписки («claude-…»), и
        // fal/glif-генерация попадает в фильтр по модели вместе с настоящими LLM-тратами.
        var actualModel = string.IsNullOrWhiteSpace(model) ? generator.DefaultModel : model;

        // «цена ещё не пришла» от «цена не придёт» отличается наличием ручки, по которой её
        // спросят: идентификатора, который дал сам ответ (у fal — заголовок x-fal-request-id,
        // контракт fal.run — см. ResponseRequestId у FalImageService), и средства спросить по
        // нему (IFalRequestCost). Обещание выводится из этой ручки, а не из ключа провайдера:
        // fal без идентификатора в ответе или без средства опроса не начнёт его так же, как
        // glif, у которого такой ручки нет вовсе (GlifJobParser берёт jobId, media и status,
        // поля цены в ответе отсутствуют). Обещание уточнения ставят только там, где его
        // будут исполнять: трата glif остаётся счётчиком вызовов навсегда — как и в
        // пишущейся записи чата (SpendMapping.RecordGlifGeneration: CostUsd = null).
        var pendingAmount = requestId is not null && _falCost is not null;

        WriteSpend(ownerId, generator.Key, actualModel, amount: null, generations: images.Count, named,
            pendingAmount, actorUserId, projectId);

        if (!pendingAmount) return;
        // Нормализованный инициатор, а не сырой: пустая строка дала бы второй записи «""» при
        // первой с null — у одного факта два признака (комментарий выше, S-SPEND-8)
        _ = TopUpAmountAsync(ownerId, actualModel, requestId!, named, actorUserId, projectId);
    }

    // Пустой результат с id запроса у fal: первую запись (pending) НЕ пишем — неизвестно,
    // платный ли запрос, а ложная «сумма уточняется» хуже отсутствия записи. Запись появится
    // одна и только если догонялка нашла цену > 0. Остаток: наш таймаут после приёма запроса
    // fal — id нет, тарифицируется ли он, неизвестно.
    private void RecordNoImagesSpend(
        string place, IImageGenerator generator, string? model, string? requestId, string ownerId, string initiator,
        string? actorUserId, string? projectId)
    {
        // Ранние выходы пишут лог так же, как RecordSpend, но записи не обещают: её здесь нет
        if (_spend is null || string.IsNullOrWhiteSpace(ownerId))
        {
            _log?.LogWarning("Генерация картинок: {Provider} не вернул картинок для места {Place}, учёт расхода недоступен (нет коллектора или владельца), записи нет",
                generator.Key, place);
            return;
        }
        if (requestId is null || _falCost is null
            || !string.Equals(generator.Key, SpendSources.Fal, StringComparison.OrdinalIgnoreCase))
        {
            _log?.LogInformation("Генерация картинок: {Provider} не вернул картинок для места {Place}, цену спросить не по чему (нет id запроса, источника цены или это не fal), записи нет",
                generator.Key, place);
            return;
        }
        _log?.LogInformation("Генерация картинок: {Provider} не вернул картинок для места {Place}, уточняю цену по {RequestId}",
            generator.Key, place, requestId);
        _ = TopUpAmountAsync(ownerId, string.IsNullOrWhiteSpace(model) ? generator.DefaultModel : model, requestId, string.IsNullOrWhiteSpace(initiator) ? null : initiator,
            actorUserId, projectId, noImages: true);
    }

    // Фактическая цена по request_id — через существующий Core-шов IFalRequestCost
    // (тот же экземпляр FalCostService, что и у чата). Свой токен: token вызывающего к этому
    // моменту уже снят вместе с его запросом, а окно опроса ограничено самим FalCostService.
    // Цены нет — штатный исход: остаётся первая запись без суммы.
    private async Task TopUpAmountAsync(string ownerId, string? model, string requestId, string? initiator,
        string? actorUserId, string? projectId, bool noImages = false)
    {
        try
        {
            var usd = await _falCost!.ResolveUsdAsync(requestId, CancellationToken.None);
            if (usd is null)
            {
                if (noImages)
                    _log?.LogWarning("Учёт расхода: цена fal по {RequestId} без картинок не появилась за окно опроса, цены нет — записи нет",
                        requestId);
                else
                    _log?.LogWarning("Учёт расхода: цена fal по {RequestId} не появилась за окно опроса, трата остаётся без суммы",
                        requestId);
                return;
            }
            if (noImages && usd <= 0)
            {
                _log?.LogInformation("Учёт расхода: запрос fal {RequestId} без картинок не тарифицирован, записи нет", requestId);
                return;
            }
            // Догоняющая запись — про тот же вызов: инициатор, актор, проект и модель у неё
            // те же, что были у самого вызова
            WriteSpend(ownerId, SpendSources.Fal, model, amount: usd, generations: 0, initiator,
                pendingAmount: false, actorUserId, projectId, label: noImages ? SpendLabelNoImages : null);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Учёт расхода: опрос цены fal ({RequestId}) не удался", requestId);
        }
    }

    // pendingAmount — «сумму догонят»: право на подпись «сумма уточняется» даёт не отсутствие
    // суммы в этой записи, а то, что у провайдера есть по чему её спрашивать (см. RecordSpend).
    private void WriteSpend(string ownerId, string providerKey, string? model, double? amount, int generations,
        string? initiator, bool pendingAmount, string? actorUserId, string? projectId, string? label = null)
    {
        try
        {
            _spend!.Record(new SpendRecord
            {
                Timestamp = DateTime.UtcNow,
                OwnerId = ownerId,
                // Проект называет вызывающий: аватар персоны проекта — достояние проекта, и его
                // трата должна быть видна в разрезе по проекту (п.4 ревью R-S-SPEND-2). null —
                // генерация вне проекта или проект осиротел: «Расход» не должен получить
                // несуществующий проект (тот же приём, что у личной области редактора).
                ProjectId = projectId,
                // Актор — разрез, не ключ (SpendRecord.ActorUserId): кто исполнил операцию.
                // Его называет вызывающий; null читается стором как «нет поля → владелец» (п.3).
                ActorUserId = actorUserId,
                Provider = providerKey,
                // Фактическая модель вызова: названная настройкой места или дефолт самого
                // драйвера (его спрашиваем у него, выдумывать за него не будем). Пустая здесь
                // читается аналитикой как дефолт подписки и смешивает генерацию картинок
                // с LLM-тратами в фильтре по модели.
                Model = model,
                // Ключ провайдера и есть ключ источника (как в прецеденте): у fal/glif токенов
                // нет, их расход — счётчик вызовов (SpendSources.IsTokenless).
                Source = providerKey,
                CostUsd = amount,
                Generations = generations,
                // Инициатор — тот, кто подал операцию, каким его назвала точка входа:
                // REST/онбординг — человек, MCP-тулсет — агент, фоновая заявка — тот, кто её
                // поставил (значение сохранено в заявке, S-SPEND-8). Роутер его не вычисляет
                // (находка ревью R-S-SPEND-2). null — вызывающий назвал пустого.
                Initiator = initiator,
                // Обещание уточнения звучит только там, где его будут исполнять: у провайдера,
                // у которого цены не будет никогда, подпись остаётся обычным счётчиком вызовов.
                // Явная подпись (пустой запрос fal) перекрывает обе.
                Label = label ?? (pendingAmount ? SpendLabelPendingAmount : SpendLabel),
            });
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Генерация картинок: не удалось записать трату {Provider}", providerKey);
        }
    }
}
