using ClaudeHomeServer.Services.Http;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Versioning;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeHomeServer.Services.Images.Editing;

// Регистрация редактора картинок внутри подсистемы images (ADR-017, раздел 1): выключенная
// подсистема не регистрирует ничего, и контроллер Main получает null → 503. Шов
// IHiggsfieldAccess регистрирует Main (адаптер над HiggsfieldOAuthService); без него
// драйвер Higgsfield просто недоступен.
public static class ImageEditorRegistration
{
    public static IServiceCollection AddImageEditor(this IServiceCollection services)
    {
        services.AddQuietHttpClient(HiggsfieldMcpClient.HttpClientName, new QuietHttpClientProfile(
            Category: "ClaudeHomeServer.Images.Editor.Higgsfield",
            Subject: "Higgsfield из редактора картинок",
            Consequence: "Правка картинок через Higgsfield недоступна — остальные поставщики работают."));

        services.AddSingleton<HiggsfieldMcpClient>();
        services.AddSingleton<FalImageEditor>();
        services.AddSingleton<HiggsfieldImageEditor>();
        services.AddSingleton<IImageEditor>(sp => sp.GetRequiredService<FalImageEditor>());
        services.AddSingleton<IImageEditor>(sp => sp.GetRequiredService<HiggsfieldImageEditor>());

        // Один экземпляр на инстанс: внутри семафор на 2 одновременные операции (ADR-018 §9)
        services.AddSingleton<IImageRaster, SkiaImageRaster>();

        services.TryAddSingleton<IVersionedImageStore, VersionedImageStore>();
        services.AddSingleton<IImageEditSaver, ImageEditSaver>();
        services.AddSingleton(sp => ImageEditWorkspace.FromConfig(sp.GetRequiredService<IConfiguration>()));
        // Траты пишутся в общий учёт ISpendCollector (вертикаль Spend); выключенный Spend —
        // null, исполнитель тогда только предупреждает в лог
        services.AddSingleton<ImageEditJobService>();
        services.AddSingleton<IImageEditJobs>(sp => sp.GetRequiredService<ImageEditJobService>());
        return services;
    }
}
