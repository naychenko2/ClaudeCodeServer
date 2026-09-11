namespace ClaudeHomeServer.Services.Images;

// Сгенерированное изображение: байты и MIME-тип (переехало из FalImageService —
// тип общий для всех генераторов).
public sealed record GeneratedImage(byte[] Bytes, string ContentType);

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

    // Сгенерировать варианты изображения. model = null → дефолтная модель драйвера.
    // Ошибка/выключенный провайдер → пустой список (вызывающие показывают внятный отказ).
    Task<IReadOnlyList<GeneratedImage>> GenerateManyAsync(
        string prompt, int count, string? model, CancellationToken ct = default);
}
