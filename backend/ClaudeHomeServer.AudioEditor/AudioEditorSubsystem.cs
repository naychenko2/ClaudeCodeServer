using ClaudeHomeServer.Services.Composition;

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
        // Исполнитель задач: котировка → запуск по quoteId, потолки, траты, события audio_edit_*, итог
        // версиями нити и якоря в ленте. Швы ядра (учёт, рассылка, лента, справочник чатов) необязательны
        services.AddSingleton<Jobs.AudioJobThreads>();
        services.AddSingleton<Jobs.AudioEditJobService>();
        // Гейт ручек: флаг, свой проект, свой чат — иначе 404
        services.AddSingleton<Controllers.AudioEditScopeGate>();
        // Склейка без ИИ: куски → новая нить. Шов IAudioDsp — от отключаемой вертикали Images, поэтому
        // необязателен: без него операция отвечает dsp_unavailable
        services.AddSingleton<Jobs.AudioConcatService>();
    }
}
