using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Http;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.AudioEditor;

// Модуль «Звук» — динамический модуль (ADR-021 §1): грузится ModuleLoader'ом по записи
// DynamicModules[audioeditor], Main его типов не видит. Выключается двумя способами:
// DynamicModules[audioeditor].Enabled=false (dll не грузится) или
// Subsystems:AudioEditor:Enabled=false (Register не вызывается). В обоих случаях ручек нет — 404.
//
// Модуль ссылается только на Core: всё внешнее — швы оттуда, реализации регистрируют другие сборки.
// Регистрируются хранилища нитей и префов, рабочая папка, их жизненный цикл, драйверы, исполнитель
// задач и гейт ручек; сами ручки — Controllers/ (проект и личный чат).
public sealed class AudioEditorSubsystem : IAppSubsystem
{
    public string Key => "audioeditor";

    public string Title => "Звук";

    public string Description => "Озвучка, музыка и правка звука проекта, версии файлов";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // Нити звука и фокус чата (ADR-021 §2): data/audio-threads, живут и умирают вместе с чатом
        // по событиям шины session/deleted и session/branched
        services.AddSingleton(sp => Threads.AudioThreadStore.FromConfig(sp.GetRequiredService<IConfiguration>()));
        services.AddHostedService<Threads.AudioThreadLifecycle>();
        // Вид «audio» контекста чата (ADR-023): Validate/Describe и засев из фокуса звука
        services.AddContextKindProvider<ChatContext.AudioContextKind>();
        // Сохранённые человеком файлы чата для «Зафиксировать только этот чат» (ADR-023 §3.3)
        services.AddSingleton<ClaudeHomeServer.Services.ChatContext.IChatSavedFiles, ChatContext.AudioSavedFiles>();
        // Запуск, сведение и склейка по ревизии контекста (КТ-3): единственная точка, где вход берётся из стора
        services.AddSingleton<ChatContext.AudioContextLaunch>();
        // Запуск, оборванный перезапуском, не висит в «Генерируем…»: сверка при старте
        services.AddHostedService<Threads.AudioThreadRecovery>();
        // Префы режима области: data/audio-editor-prefs, их наследуют новые нити и цепочка запуска
        services.AddSingleton(sp => Prefs.AudioPrefsStore.FromConfig(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<Prefs.AudioPrefsService>();
        // Рабочая папка задач (7 дней, вне бэкапа); файлы версий живых нитей чистка не трогает
        services.AddSingleton(sp =>
        {
            var workspace = Jobs.AudioEditWorkspace.FromConfig(sp.GetRequiredService<IConfiguration>());
            workspace.RetainedJobs = sp.GetRequiredService<Threads.AudioThreadStore>().ReferencedJobs;
            return workspace;
        });
        // Драйверы поставщиков: шов local-media — от отключаемой вертикали Images, поэтому nullable
        services.AddSingleton<IAudioEngine>(sp =>
            new Engines.LocalAudioEngine(sp.GetService<ClaudeHomeServer.Services.Media.ILocalAudioMedia>()));
        // Higgsfield: свой экземпляр Core-клиента; шов доступа регистрирует Main, нет его — Enabled=false
        services.AddQuietHttpClient(ClaudeHomeServer.Services.Higgsfield.HiggsfieldMcpClient.HttpClientName,
            new QuietHttpClientProfile(
                Category: "ClaudeHomeServer.AudioEditor.Higgsfield",
                Subject: "Higgsfield из модуля «Звук»",
                Consequence: "Озвучка через Higgsfield недоступна — остальные поставщики работают."));
        services.AddSingleton(sp => new Engines.HiggsfieldAudioEngine(new ClaudeHomeServer.Services.Higgsfield.HiggsfieldMcpClient(
            sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IConfiguration>(),
            sp.GetService<ClaudeHomeServer.Services.Higgsfield.IHiggsfieldAccess>())));
        services.AddSingleton<IAudioEngine>(sp => sp.GetRequiredService<Engines.HiggsfieldAudioEngine>());
        // Яндекс: шов ITtsEngine — от отключаемой вертикали Tts, поэтому nullable
        services.AddSingleton<IAudioEngine>(sp =>
            new Engines.YandexAudioEngine(sp.GetService<ClaudeHomeServer.Services.Media.ITtsEngine>()));
        // fal: ключ инстанса Fal:ApiKey (или FAL_KEY), тихий HTTP-клиент "fal" заводит Main; нет ключа — Enabled=false
        services.AddSingleton<Engines.FalAudioEngine>();
        services.AddSingleton<IAudioEngine>(sp => sp.GetRequiredService<Engines.FalAudioEngine>());
        // Исполнитель задач: котировка → запуск по quoteId, потолки, траты, события audio_edit_*, итог
        // версиями нити и якоря в ленте. Швы ядра (учёт, рассылка, лента, справочник чатов) необязательны
        services.AddSingleton<Jobs.AudioJobThreads>();
        services.AddSingleton<Jobs.AudioEditJobService>();
        // Гейт ручек: флаг, свой проект, свой чат — иначе 404
        services.AddSingleton<Controllers.AudioEditScopeGate>();
        // Шов «Видео → Звук» (ADR-022 §3): черновик музыки и основной файл версии для фильма
        services.AddSingleton<ClaudeHomeServer.Services.Media.IAudioTrackSource, Threads.AudioTrackSource>();
        // Склейка без ИИ: куски → новая нить. Шов IAudioDsp — от отключаемой вертикали Images, поэтому
        // необязателен: без него операция отвечает dsp_unavailable
        services.AddSingleton<Jobs.AudioConcatService>();
        // Правки без ИИ (обрезка, фейды, громкость, нормализация, формат, сведение стемов) — новые версии
        // нити, тот же необязательный шов IAudioDsp
        services.AddSingleton<Engines.DspAudioEngine>();
        // Монтаж без ИИ у агента (audio_generate с op trim/gainFade/normalize/mixStems) — поверх того же движка
        services.AddSingleton<Mcp.IAudioAgentEdits, Mcp.AudioAgentEdits>();
        // Библиотека «Голоса»: voices/<slug>/ серверного проекта, кеш id у поставщиков и признак
        // «клон MiniMax протух» по подменяемым часам
        services.AddSingleton(sp => new Voices.VoiceLibrary(sp.GetService<TimeProvider>()));
        services.AddSingleton<Mcp.IAudioVoiceLibrary>(sp => sp.GetRequiredService<Voices.VoiceLibrary>());
        // Инструменты агента (MCP audio-editor, ADR-021 §5): в ход их везёт Main, когда тулсет есть в реестре
        services.AddSingleton<ClaudeHomeServer.Services.Mcp.Http.IMcpToolset, Mcp.AudioEditorToolset>();
        // Агент позвал local_* напрямую: звук результата получает ту же карточку, что запуск через audio_*
        services.AddSingleton<ClaudeHomeServer.Services.Media.ILocalMediaAdopter, Threads.LocalAudioAdopter>();
        // Блок хвоста хода «Звук в этом чате»: правило приоритета audio_* над прямыми local_*
        services.AddPromptSectionContributor<Chats.AudioEditorStateContributor>();
    }
}
