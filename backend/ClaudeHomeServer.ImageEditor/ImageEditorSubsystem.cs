using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Http;
using ClaudeHomeServer.Services.ImageEditor.Versioning;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Turn;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeHomeServer.Services.ImageEditor;

// Редактор картинок — динамический модуль (ADR-018 §10.1): грузится ModuleLoader'ом по записи
// DynamicModules[image-editor], Main его типов не видит. Выключается двумя способами:
// DynamicModules[image-editor].Enabled=false (dll не грузится) или
// Subsystems:ImageEditor:Enabled=false (Register не вызывается). В обоих случаях ручек нет — 404.
//
// Что берём из спины (всё — Core-швы): IImageRaster и IImagePlaceSettings от Images,
// IHiggsfieldAccess, IProjectManager, IFeatureFlagGate, IProjectFiles, ISessionBroadcaster,
// ISpendCollector, IImageChatSessions и ISessionDirectory от Main. Растр необязателен: без Images
// ручки transform и jobs отвечают 503 raster_unavailable, а не 500.
public sealed class ImageEditorSubsystem : IAppSubsystem
{
    public string Key => "imageeditor";

    public string Title => "Редактор картинок";

    public string Description => "Правка картинок проекта моделями и без ИИ, персонажи, версии файлов";

    public void Register(IServiceCollection services, IConfiguration config)
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

        services.TryAddSingleton<IVersionedImageStore, VersionedImageStore>();
        services.AddSingleton<IImageEditSaver, ImageEditSaver>();
        services.AddSingleton(sp => ImageEditWorkspace.FromConfig(sp.GetRequiredService<IConfiguration>()));
        // Траты пишутся в общий учёт ISpendCollector (вертикаль Spend); выключенный Spend —
        // null, исполнитель тогда только предупреждает в лог
        services.AddSingleton<ImageEditJobService>();
        services.AddSingleton<IImageEditJobs>(sp => sp.GetRequiredService<ImageEditJobService>());
        // Правки без ИИ и шаги истории (ADR-018 §9). Без растра шагов нет: фабрика отдаёт null,
        // и контроллер получает его вместо исключения резолва
        services.AddSingleton(sp => sp.GetService<IImageRaster>() is { } raster
            ? new ImageEditSteps(raster, sp.GetRequiredService<ImageEditWorkspace>(), sp.GetRequiredService<IImageEditJobs>())
            : null!);
        // Чат картинки идёт за переименованным файлом (ADR-018 §1)
        services.AddHostedService<Chats.ImageChatPathTracker>();
        // Состояние редактора на сервере, общая сборка входа запуска и блок состояния хвостом
        // хода (ADR-018 §2): им же пользуется MCP-тулсет редактора
        services.AddSingleton(sp => new Chats.ImageChatStateStore(sp.GetRequiredService<ImageEditWorkspace>()));
        services.AddSingleton<ImageEditLaunchAssembler>();
        services.AddPromptSectionContributor<Chats.ImageEditorStateContributor>();
        // MCP-сервер редактора для агента чата картинки (ADR-018 §10.2): маршрут общий,
        // POST /mcp/image-editor/{sessionId}, реестр Main находит тулсет среди IMcpToolset
        services.AddSingleton<ClaudeHomeServer.Services.Mcp.Http.IMcpToolset, Mcp.ImageEditorToolset>();
    }
}
