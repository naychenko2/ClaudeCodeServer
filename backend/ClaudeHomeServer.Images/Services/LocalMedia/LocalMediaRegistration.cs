using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Http;
using ClaudeHomeServer.Services.Media;

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
        // Журнал истории медиа-генераций (шаг 2): единственный писатель data/local-media-history/
        services.AddSingleton<ComfyRunHistory>();
        // Реестр prompt_id ComfyUI → происхождение/ETA/исход (шаг 7): «чей этот прогон».
        // Вызывает журнал: Register → enqueued, MarkStarted → started, Complete → finished
        services.AddSingleton(sp => new ComfyPromptLedger(
            time: sp.GetService<TimeProvider>(),
            history: sp.GetService<ComfyRunHistory>()));
        // Настоящие шаги семплера по WebSocket ComfyUI; без связи — оценка по ETA.
        // isOurs = активные задачи local_*/LoRA (стор) + прогоны из реестра (редакторы)
        services.AddSingleton(sp => new ComfyProgressTracker(
            sp.GetRequiredService<LocalMediaJobStore>(), sp.GetRequiredService<ComfyPromptLedger>()));
        services.AddSingleton<LocalMediaService>();
        // Шов для поставщика «Локальные модели» редактора картинок (модуль ImageEditor)
        services.AddSingleton<ClaudeHomeServer.Services.ImageEditor.ILocalImageMedia, LocalImageMediaAdapter>();
        // Шов локальных аудио-моделей для модуля «Звук» (ADR-021)
        services.AddSingleton<ClaudeHomeServer.Services.Media.ILocalAudioMedia, LocalAudioMediaAdapter>();
        // Шов локальной съёмки видео для модуля «Видео» (ADR-022)
        services.AddSingleton<LocalVideoMediaAdapter>();
        services.AddSingleton<ClaudeHomeServer.Services.Media.ILocalVideoMedia>(sp => sp.GetRequiredService<LocalVideoMediaAdapter>());
        // Шов обучения LoRA персонажа (ADR-026): потребитель берёт его необязательным параметром, без Images он null
        services.AddSingleton<ClaudeHomeServer.Services.ImageEditor.ILocalLoraTrainer, LocalLoraTrainerAdapter>();
        // Шов обработки звука без ИИ (ffmpeg на хосте) для модуля «Звук» (ADR-021)
        services.AddSingleton<ClaudeHomeServer.Services.Media.IAudioDsp, FfmpegAudioDsp>();
        // Шов обработки видео без ИИ для модуля «Видео» (ADR-022 §4): сборка фильма — тяжёлый запуск под общим
        // слотом BuildConcurrencyGate через системный local-запуск (scope ccs-agents.slice)
        services.AddSingleton<ClaudeHomeServer.Services.Media.IVideoDsp, FfmpegVideoDsp>();
        services.AddSingleton<LocalMediaCleanup>();
        services.AddGatedHostedService<LocalMediaCollector>(config, "images");
        services.AddGatedHostedService<ComfyProgressListener>(config, "images");
        // Монитор очереди ComfyUI (шаг 24): снимок running/queue/lastHour/slots/status для IComfyStats (шаг 26).
        // Shared singleton + hosted: адаптеры и ComfyStats резолвят монитор ПО ТИПУ (GetService<ComfyQueueMonitor>),
        // а не только как IHostedService — иначе GetService<ComfyQueueMonitor>() = null и ComfyStats (обязательный
        // конструктор ComfyQueueMonitor) в DI не строится (ревью шага 26б, critical).
        services.AddSingleton<ComfyQueueMonitor>();
        services.AddGatedHostedFrom<ComfyQueueMonitor>(config, sp => sp.GetRequiredService<ComfyQueueMonitor>(), "images");
        // Снимок медиа-стенда (шаг 26): IComfyStats — опциональная зависимость для Main (подсистема
        // images отключаема: без неё контроллер Main получает null и отвечает {configured:false})
        services.AddSingleton<IComfyStats, ComfyStats>();
        return services;
    }
}
