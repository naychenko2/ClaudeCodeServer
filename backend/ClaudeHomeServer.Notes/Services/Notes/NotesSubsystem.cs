using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.Notes;

// Вертикаль заметок (Этап 5, волна 5): Obsidian-совместимый vault — `[[wikilinks]]`,
// backlinks, граф, комментарии к документам, синк с Dify; AI-сводки и теги
// (`NotesAiService`); двусторонняя синхронизация чекбоксов заметок и задач
// (`NoteTaskSyncService`); авто-истечение заметок (`NoteExpiryService`).
// `UnifiedSearchService` остаётся в корне Services — он фасад поперёк Notes+Task,
// класть его в Notes нельзя (потянет Task), в Task нельзя (потянет Notes);
// отдельная задача на разрез.
//
// Известные границы (сознательные):
// 1) `ClaudeHomeServer.Services.Tasks` — префикс-шов через `INoteTaskBridge`
//    (Core) и `NoteTaskSyncService` (Notes): мост чекбоксов заметок и карточек
//    задач. После Этапа 5 Notes → Tasks идёт через узкий Core-контракт
//    `INoteTaskBridge` (`GetBySourceNote`/`Create`/`Update`/`SpawnNextOccurrence`),
//    реализация `TaskBridge` живёт в Main (Services/Tasks/TaskBridge.cs).
// 2) `ClaudeHomeServer.Services.Knowledge` — префикс-шов через `IKnowledgeIndex`
//    (Core, Этап 5, волна 5) и `NotesKnowledgeService` (Notes): синхронизация
//    заметок с Dify-датасетом per-owner.
// 3) `ClaudeHomeServer.Services.Llm` — префикс-шов через `ICheapTextRunner`
//    (Core) и `NotesAiService` (Notes) для тегов/сводок заметок.
// 4) `ClaudeHomeServer.Hubs` — префикс-шов через `INotesHubNotifier` (Core,
//    Этап 5, волна 5) и `NoteTaskSyncService`/`NoteExpiryService` (Notes):
//    рассылка `notes_changed`. Реализация `NotesHubNotifier` живёт в Main
//    (Services/Composition/NotesHubNotifier.cs).
// 5) Точечные Core-интерфейсы `IProjectManager`/`IUserStore`/`IProjectEventLogService`
//    (Этап 5, волна 5) — замена прямых ссылок на Main-типы. Реализации
//    (`ProjectManager`/`UserStore`/`ProjectEventLogService` в Main) подписаны
//    через стандартный паттерн forwarder в DI.
//
// Регистрация шовных реализаций (`TaskBridge`/`NotesHubNotifier`) — в Main
// (Program.cs), потому что эти типы сами живут в Main и недоступны из
// Notes.csproj: обратной ссылки нет.
//
// Контроллер `NotesController` (Controllers/NotesController.cs) живёт В ЭТОЙ
// сборке. Дефолт `Subsystems:Notes:Enabled = true`. Гейт вертикали
// (закрыть контроллеры при `Enabled=false` — 404 на `/api/notes/*` вместо 500
// от DI-резолва) — забота КОМПОЗИЦИИ Main (`ConfigureApplicationPartManager` в
// Program.cs): MSBuild генерирует `[assembly: ApplicationPart("ClaudeHomeServer.Notes")]`
// в `obj/*/ClaudeHomeServer.MvcApplicationPartsAssemblyInfo.cs` благодаря тому,
// что Notes.csproj собран под `Microsoft.NET.Sdk.Web`, и подсистема этот атрибут
// обойти не может. Сравнение по имени сборки; обратной ссылки Main → Notes
// избежать нельзя, но она узкая (один метод `ConfigureApplicationPartManager`).
public sealed class NotesSubsystem : IAppSubsystem
{
    public string Key => "notes";

    public string Title => "Заметки";

    // Для админского экрана «Подсистемы»: одно предложение, попадает в REST как `description`.
    public string Description => "Личный vault, заметки проектов, связи [[…]] и граф.";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // Singleton-ы без внутреннего порядка между собой; `NoteExpiryService` —
        // hosted поверх `NotesService` (читает заметки по проектам для авто-истечения).
        services.AddSingleton<NotesService>();
        services.AddSingleton<INoteSummaryReader, NotesService>();
        services.AddSingleton<NotesKnowledgeService>();
        services.AddSingleton<NotesAiService>();
        services.AddSingleton<NoteTaskSyncService>();
        services.AddGatedHostedService<NoteExpiryService>(config);
        // Этап 5, шаг 6 (инверсия контрибьюторов промпта): контрибьютор секции
        // «recall-notes» зарегистрирован в своей вертикали, Turn собирает
        // IEnumerable<IPromptSectionContributor> и натравливает на шину (см.
        // PromptSectionContributorsRegistration.RegisterAll). Гейт подсистемы —
        // структурный: при `Subsystems:Notes:Enabled=false` `AddSubsystems` не зовёт
        // `Register`, и контрибьютор (он тянет `NotesKnowledgeService`) в
        // `IEnumerable<IPromptSectionContributor>` не попадает вовсе.
        services.AddPromptSectionContributor<NotesRecallContributor>();
    }
}
