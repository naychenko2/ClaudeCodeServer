using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Notes;

namespace ClaudeHomeServer.Services.Composition;

// Клей Notes↔Knowledge (S6-1): участие заметок в реконсайлере Dify — форвардер
// `IKnowledgeSyncParticipant` на `INoteSemanticIndex` — перенесён из точечного гейта
// в Program.cs. Гейт и условия — те же, включая предупреждение.
//
// Гейт подсистемы Notes: `NotesKnowledgeService` не попадёт в DI при выключенной
// подсистеме (NotesSubsystem.Register не вызывается) — безусловный форвардер уронил бы
// резолв ВСЕЙ коллекции IKnowledgeSyncParticipant (а не только заметки), блокер ревью
// notes-optional Б2. `NotesKnowledgeService` реализует `INoteSemanticIndex` (Core) и
// `IKnowledgeSyncParticipant`: резолвим через `INoteSemanticIndex` и кастим — не тянем
// конкретный тип Notes-сборки в Main (динамический модуль).
//
// Кроме гейта требуется ФАКТ загрузки модуля: Notes — динамическая сборка, и dll может
// не доехать до деплоя (пробел publish-копирования). Тогда гейт говорит «включено»,
// а `INoteSemanticIndex` в DI нет — безусловный резолв ронял старт ВСЕГО хоста
// (KnowledgeIndexReconciler тянет коллекцию IKnowledgeSyncParticipant). Активность
// модуля берётся из стора состава: ActiveKeys записывается ModuleLoader'ом по
// фактической загрузке.
public static class NotesKnowledgeGlue
{
    public static IServiceCollection AddNotesKnowledgeParticipant(
        this IServiceCollection services, IConfiguration config)
    {
        var notesModuleActive = services.SubsystemsState().ActiveKeys().Contains("notes");
        if (SubsystemGate.IsEnabled(config, "notes") && notesModuleActive)
        {
            services.AddSingleton<IKnowledgeSyncParticipant>(
                sp => (IKnowledgeSyncParticipant)sp.GetRequiredService<INoteSemanticIndex>());
        }
        else if (SubsystemGate.IsEnabled(config, "notes"))
        {
            // Гейт говорит «включено», но dll динамического модуля не загрузилась. Тихо не оставляем:
            // заметки без `NotesKnowledgeService` НЕ попадут в реконсайлер Dify (KnowledgeIndexReconciler
            // тянет коллекцию IKnowledgeSyncParticipant для error-документов и пересборки) и не будут
            // синхронизированы в базе знаний вообще — это уже не «форвардер не зарегистрирован», а
            // потерянный контур данных. Модуль выключен намеренно (`Subsystems:Notes:Enabled=false`)
            // случай выше не покрывает — там Notes нет в списке, предупреждение избыточно.
            // Используем Console.Error вместо ILogger: builder.Build() ещё не вызван, а
            // IL-логгер ModuleLoader резолвится из временного SP, который закрыт в using-блоке
            // Program.cs (его LoggerFactory уже disposed). Тот же префикс, что у других стартовых
            // предупреждений в Program.cs (`[HandleMigration]`, `[TranscriptRoots]`).
            Console.Error.WriteLine(
                "[Knowledge] WARNING: подсистема Notes включена (`Subsystems:Notes:Enabled=true`), " +
                "но её сборка не загружена — заметки не будут синхронизироваться в Knowledge " +
                "(Dify-датасет) и не появятся среди участников реконсайлера. Проверьте путь " +
                "`DynamicModules:1:Backend:AssemblyPath` и факт копирования `ClaudeHomeServer.Notes.dll` " +
                "в publish.");
        }
        return services;
    }
}
