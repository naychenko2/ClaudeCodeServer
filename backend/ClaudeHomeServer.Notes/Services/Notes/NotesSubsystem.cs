using System.Reflection;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Turn;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.Options;

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
// сборке и подключается к Main через `ApplicationPart` — см. метод
// `AddApplicationPart` ниже. Дефолт `Subsystems:Notes:Enabled = true`;
// при `false` сборка не подключается к MVC и маршруты `/api/notes/*` отдают
// 404 (роутер не находит action), а не 500 (DI-резолв упавшего контроллера).
public sealed class NotesSubsystem : IAppSubsystem
{
    public string Key => "notes";

    public string Title => "Заметки";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // Singleton-ы без внутреннего порядка между собой; `NoteExpiryService` —
        // hosted поверх `NotesService` (читает заметки по проектам для авто-истечения).
        services.AddSingleton<NotesService>();
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

        // Подключение MVC-ApplicationPart ТОЛЬКО при включённой подсистеме.
        // При `Enabled=false` `IConfigureOptions<MvcOptions>` не регистрируется,
        // MVC строит свой `ApplicationPartManager` без этой сборки, и
        // `NotesController` НЕ обнаруживается — запросы к `/api/notes/*` уходят
        // в общий 404, а не 500 от DI-резолва. Никаких записей об исключениях.
        //
        // Почему `IConfigureOptions<MvcOptions>` + DI-резолв `ApplicationPartManager`,
        // а не `IMvcBuilder.AddApplicationPart`:
        // `IAppSubsystem.Register` не получает `IMvcBuilder` на руки (он создаётся
        // и тут же потребляется в `AddControllers().AddJsonOptions(...)` в Program.cs).
        // Configure-options — единственный шанс добавить part к моменту построения
        // `ApplicationPartManager`, не дёргая обратную ссылку Main → вертикаль.
        // `ApplicationPartManager` — синглтон в DI, регистрируется в `AddControllers()`
        // раньше, чем сюда доходит наша `Register`; добавляем свой `AssemblyPart` в его
        // `ApplicationParts`, и MVC при первом резолве `MvcOptions` уже видит обе сборки.
        // Единая точка гейта — SubsystemGate.IsEnabled (Core), не второй инлайн-читатель
        // конфига: технически Register() и так вызывается только при пройденном гейте
        // (AddSubsystems), но дубль ключа "Subsystems:Notes:Enabled" тут был второй
        // реализацией той же проверки (блокер ревью notes-optional Б5).
        if (SubsystemGate.IsEnabled(config, Key))
        {
            services.AddSingleton<IConfigureOptions<MvcOptions>>(
                sp => new ConfigureMvcOptions(
                    sp.GetRequiredService<ApplicationPartManager>(),
                    typeof(NotesSubsystem).Assembly));
        }
    }

    // `IConfigureOptions<MvcOptions>`-обёртка, которая к моменту построения `MvcOptions`
    // дописывает нашу сборку в общий `ApplicationPartManager`. Сам `MvcOptions` не
    // отдаёт `ApplicationPartManager` как публичное свойство — идём через DI-синглтон.
    private sealed class ConfigureMvcOptions(
        ApplicationPartManager partManager, Assembly assembly) : IConfigureOptions<MvcOptions>
    {
        public void Configure(MvcOptions options) =>
            partManager.ApplicationParts.Add(new AssemblyPart(assembly));
    }
}
