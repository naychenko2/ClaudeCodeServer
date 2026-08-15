using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Services.Images;

// Регистрация слоя генерации картинок: драйверы + настройка + роутер.
// Новый провайдер — одна строка AddImageDriver<T>() здесь, остальное подхватится само
// (роутер получает IEnumerable<IImageGenerator>).
public static class ImageGenerationRegistration
{
    public static IServiceCollection AddImageGeneration(this IServiceCollection services)
    {
        services.AddSingleton<ImageGenerationSettingsStore>();
        // fal остаётся доступен и как конкретный тип: на него завязаны существующие
        // точки генерации, которые переезжают на роутер отдельной волной.
        services.AddImageDriver<FalImageService>();
        services.AddImageDriver<GlifImageGenerator>();
        services.AddSingleton<ImageGenerationService>();
        // Догоняющая генерация: очередь + прогон + триггеры по времени. Гейт Testing-среды
        // сидит внутри самого hosted-сервиса (здесь окружения не видно).
        services.AddSingleton<ImageBackfillStore>();
        services.AddSingleton<ImageBackfillService>();
        services.AddHostedService<ImageBackfillHostedService>();
        return services;
    }

    // Драйвер регистрируется один раз и отдаётся и по своему типу, и в набор IImageGenerator
    public static IServiceCollection AddImageDriver<T>(this IServiceCollection services)
        where T : class, IImageGenerator
    {
        services.AddSingleton<T>();
        services.AddSingleton<IImageGenerator>(sp => sp.GetRequiredService<T>());
        return services;
    }
}
