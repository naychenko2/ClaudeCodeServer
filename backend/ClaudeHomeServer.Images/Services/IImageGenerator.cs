namespace ClaudeHomeServer.Services.Images;

// Сгенерированное изображение: байты и MIME-тип (переехало из FalImageService —
// тип общий для всех генераторов).
// RequestId — идентификатор запроса поставщика, по которому позже догоняется фактическая
// стоимость (дефект 22068e9d, шаг S-SPEND-2). У синхронного fal.run его нет в ТЕЛЕ ответа
// (фал кладёт его в заголовок x-fal-request-id, и тем же id ключуется billing-events —
// docs fal «Request Headers»/«Billing Events»). У glif идентификатора, по которому его цена
// спрашивается, в ответе нет (GlifJobParser берёт jobId, media и status — поля цены там
// отсутствуют), поэтому там RequestId = null и сумма не догоняется.
public sealed record GeneratedImage(byte[] Bytes, string ContentType, string? RequestId = null);

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

    // Тот же дефолт, названный: модель, на которую драйвер идёт при model = null (нижняя
    // граница контракта GenerateManyAsync). Роутер пишет её в трату: пустая модель в учёте
    // читается аналитикой как дефолт подписки («claude-…»), и генерация картинки попадает
    // в фильтр по модели вместе с настоящими LLM-тратами. Драйвер обязан назвать свой
    // дефолт; базовое значение — null, трата остаётся как до этой правки (оно для
    // заглушек, у которых своей модели нет).
    string? DefaultModel => null;

    // Сгенерировать варианты изображения. model = null → дефолтная модель драйвера.
    // Ошибка/выключенный провайдер → пустой список (вызывающие показывают внятный отказ).
    Task<IReadOnlyList<GeneratedImage>> GenerateManyAsync(
        string prompt, int count, string? model, CancellationToken ct = default);
}
