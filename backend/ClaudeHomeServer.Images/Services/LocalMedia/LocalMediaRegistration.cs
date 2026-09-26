using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Http;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

// Регистрация локальной генерации внутри подсистемы images. Сервисы регистрируются всегда
// (они дешёвые), а тумблер LocalMedia:Enabled читается живьём на каждый вызов: включение на
// машине с GPU не требует рестарта. Шов ILocalMediaProjectAccess регистрирует Main.
public static class LocalMediaRegistration
{
    public static IServiceCollection AddLocalMedia(this IServiceCollection services, IConfiguration config)
    {
        services.AddQuietHttpClient(ComfyClient.HttpClientName, new QuietHttpClientProfile(
                Category: "ClaudeHomeServer.Images.LocalMedia",
                Subject: "ComfyUI локальной генерации",
                Consequence: "Локальная генерация картинок и видео недоступна — облачные генераторы работают."))
            // ComfyUI — наш сервис на loopback: системный прокси его не обслуживает
            .WithoutEgressProxy()
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(60));

        services.AddSingleton<ComfyClient>();
        services.AddSingleton<LocalMediaJobStore>();
        services.AddSingleton<LocalMediaService>();
        services.AddGatedHostedService<LocalMediaCollector>(config, "images");
        return services;
    }
}
