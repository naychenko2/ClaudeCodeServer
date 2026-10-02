using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Http;
using ClaudeHomeServer.Services.VideoEditor.Films;

namespace ClaudeHomeServer.Services.VideoEditor;

// Модуль «Видео» — динамический модуль (ADR-022 §1): грузится ModuleLoader'ом по записи
// DynamicModules[videoeditor], Main его типов не видит. Выключается двумя способами:
// DynamicModules[videoeditor].Enabled=false (dll не грузится) или
// Subsystems:VideoEditor:Enabled=false (Register не вызывается). В обоих случаях ручек нет — 404.
//
// Модуль ссылается только на Core: всё внешнее — швы оттуда, реализации регистрируют другие сборки.
public sealed class VideoEditorSubsystem : IAppSubsystem
{
    public string Key => "videoeditor";

    public string Title => "Видео";

    public string Description => "Сцены между двумя кадрами и фильм из них: съёмка, версии клипов";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // Нити сцен и фокус чата (ADR-022 §2): data/video-threads, живут и умирают вместе с чатом
        // по событиям шины session/deleted и session/branched
        services.AddSingleton(sp => Scenes.VideoThreadStore.FromConfig(sp.GetRequiredService<IConfiguration>()));
        services.AddHostedService<Scenes.VideoThreadLifecycle>();
        // Запуск, оборванный перезапуском, не висит в «Снимаем…»: сверка при старте
        services.AddHostedService<Scenes.VideoThreadRecovery>();
        // Префы области: data/video-editor-prefs, их наследуют новые сцены и цепочка запуска
        services.AddSingleton(sp => Prefs.VideoPrefsStore.FromConfig(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<Prefs.VideoPrefsService>();
        // Рабочая папка задач (7 дней, вне бэкапа); клипы версий живых сцен чистка не трогает
        services.AddSingleton(sp =>
        {
            var workspace = Jobs.VideoEditWorkspace.FromConfig(sp.GetRequiredService<IConfiguration>());
            workspace.RetainedJobs = sp.GetRequiredService<Scenes.VideoThreadStore>().ReferencedJobs;
            return workspace;
        });
        // Драйверы поставщиков: шов local-media — от отключаемой вертикали Images, поэтому nullable
        services.AddSingleton<IVideoEngine>(sp =>
            new Engines.LocalVideoEngine(sp.GetService<ClaudeHomeServer.Services.Media.ILocalVideoMedia>()));
        // Higgsfield: свой экземпляр Core-клиента; шов доступа регистрирует Main, нет его — Enabled=false
        services.AddQuietHttpClient(ClaudeHomeServer.Services.Higgsfield.HiggsfieldMcpClient.HttpClientName,
            new QuietHttpClientProfile(
                Category: "ClaudeHomeServer.VideoEditor.Higgsfield",
                Subject: "Higgsfield из модуля «Видео»",
                Consequence: "Съёмка через Higgsfield недоступна — остальные поставщики работают."));
        services.AddSingleton(sp => new Engines.HiggsfieldVideoEngine(new ClaudeHomeServer.Services.Higgsfield.HiggsfieldMcpClient(
            sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IConfiguration>(),
            sp.GetService<ClaudeHomeServer.Services.Higgsfield.IHiggsfieldAccess>())));
        services.AddSingleton<IVideoEngine>(sp => sp.GetRequiredService<Engines.HiggsfieldVideoEngine>());
        // fal: ключ инстанса Fal:ApiKey (или FAL_KEY), тихий HTTP-клиент "fal" заводит Main; нет ключа — Enabled=false
        services.AddSingleton<Engines.FalVideoEngine>();
        services.AddSingleton<IVideoEngine>(sp => sp.GetRequiredService<Engines.FalVideoEngine>());
        // Исполнитель съёмки: котировка → запуск по quoteId, потолки, траты, события video_edit_*, итог
        // версиями сцены и якоря в ленте. Швы ядра (учёт, рассылка, лента, справочник чатов) необязательны
        // Чтение кадров сцены: файлы проекта и (швом IImageFrameSource, необязательным) версии нитей «Картинок»
        services.AddSingleton<Jobs.VideoFrameReader>();
        services.AddSingleton<Jobs.VideoJobThreads>();
        services.AddSingleton<Jobs.VideoEditJobService>();
        // Гейт ручек: флаг, свой проект, свой чат — иначе 404
        services.AddSingleton<Controllers.VideoEditScopeGate>();
        // Фильм: .film под ревизией, сохранение сцены, сборка ffmpeg, подписки на «Картинки» и «Звук» (блок 2)
        services.AddFilms();
    }
}
