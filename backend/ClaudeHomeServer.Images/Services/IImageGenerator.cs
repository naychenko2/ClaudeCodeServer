namespace ClaudeHomeServer.Services.Images;

// Сгенерированное изображение: байты и MIME-тип (переехало из FalImageService —
// тип общий для всех генераторов).
public sealed record GeneratedImage(byte[] Bytes, string ContentType);

// Результат вызова генератора: картинки и идентификатор запроса у поставщика.
// RequestId — по нему позже догоняется фактическая стоимость (дефект 22068e9d, S-SPEND-2).
// Он принадлежит ВЫЗОВУ, а не картинке: поставщик мог принять запрос и взять плату, но не
// отдать ни одной картинки (не-2xx, пустой images, упавшие скачивания) — запись траты тогда
// всё равно нужна. У синхронного fal.run его нет в теле ответа (он в заголовке
// x-fal-request-id, тем же id ключуется billing-events — docs fal «Request Headers»/
// «Billing Events»). У glif идентификатора, по которому спрашивается цена, в ответе нет
// (GlifJobParser берёт jobId, media и status), поэтому там RequestId = null. До ответа
// поставщика (исключение, выключен) — тоже null.
public sealed record ImageGenerationResult(IReadOnlyList<GeneratedImage> Images, string? RequestId)
{
    public static readonly ImageGenerationResult Empty = new([], null);
}

// Модель генератора для пикера в UI. Id — то, что уходит драйверу «как есть»
// (у fal это полный endpoint_id, у glif — id глифа/модели).
public sealed record ImageModelInfo(string Id, string DisplayName, string? Description = null);

// Драйвер генерации картинок. Реализации: FalImageService (fal.ai, синхронный вызов),
// GlifImageService (glif.app, асинхронный с опросом статуса). Роутер
// (ImageGenerationService) выбирает драйвер по настройке инстанса.
public interface IImageGenerator
{
    // Ключ провайдера: "fal" | "glif". Им же ключуются настройки и порядок auto-выбора.
    string Key { get; }

    // Название для UI
    string DisplayName { get; }

    // Провайдер настроен (есть ключ/токен) и может генерировать
    bool Enabled { get; }

    // Курируемый список моделей для пикера. Пустой список — модель не выбирается,
    // драйвер всегда идёт на свой дефолт.
    IReadOnlyList<ImageModelInfo> Models { get; }

    // Тот же дефолт, названный: дефолт, который драйвер объявляет своим, — уровень, который
    // он называет себе сам при model = null (нижняя граница контракта GenerateManyAsync),
    // а не обязательно модель фактического запроса: glif при model = null intelligence не
    // шлёт, уровень берёт агент (BuildComposeArgs), но в учёте стоит его DefaultTier.
    // Роутер пишет его в трату: пустая модель в учёте читается аналитикой как дефолт
    // подписки («claude-…»), и генерация картинки попадает в фильтр по модели вместе с
    // настоящими LLM-тратами. Драйвер обязан назвать свой дефолт; базовое значение — null,
    // трата остаётся как до этой правки (оно для заглушек, у которых своей модели нет).
    string? DefaultModel => null;

    // Сгенерировать варианты изображения. model = null → дефолтная модель драйвера.
    // Ошибка/выключенный провайдер → пустой Images (вызывающие показывают внятный отказ).
    Task<ImageGenerationResult> GenerateManyAsync(
        string prompt, int count, string? model, CancellationToken ct = default);
}
