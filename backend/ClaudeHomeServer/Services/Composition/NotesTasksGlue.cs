using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Tasks;

namespace ClaudeHomeServer.Services.Composition;

// Клей Notes↔Tasks (Этап 5, волна E; перенесён из точечного гейта Program.cs — S6-1):
// forwarder-регистрации двух Core-швов выноса Notes. Реализации (`TaskBridge` поверх
// `TaskManager` в вертикали Tasks, `NotesHubNotifier` поверх `ISessionBroadcaster`)
// живут вне Notes-сборки; Notes получает только Core-контракты.
//
// Гейт `Subsystems:Notes:Enabled`: при выключенной подсистеме и сами реализации, и
// форвардеры швов в DI не нужны — резолв `INoteTaskBridge`/`INotesHubNotifier` иначе
// свалится на первом же обращении из Notes. Условие активности модуля — как в
// NotesKnowledgeGlue: швы регистрируем только при фактически загруженном динамическом
// модуле Notes (ActiveKeys стора — по фактической загрузке, не по конфиг).
public static class NotesTasksGlue
{
    public static IServiceCollection AddNotesTaskBridge(
        this IServiceCollection services, IConfiguration config)
    {
        if (SubsystemGate.IsEnabled(config, "notes")
            && services.SubsystemsState().ActiveKeys().Contains("notes"))
        {
            services.AddSingleton<TaskBridge>();
            services.AddSingleton<NotesHubNotifier>();
            services.AddSingleton<INoteTaskBridge>(sp => sp.GetRequiredService<TaskBridge>());
            services.AddSingleton<INotesHubNotifier>(sp => sp.GetRequiredService<NotesHubNotifier>());
        }
        return services;
    }
}
