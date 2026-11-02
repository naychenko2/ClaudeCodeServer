using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Plugins;
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
        // Форвардер на один синглтон, а не `AddSingleton<INoteSummaryReader, NotesService>`:
        // вторая регистрация типа давала ВТОРОЙ экземпляр сервиса (рассинхрон кэша модели).
        services.AddSingleton<INoteSummaryReader>(sp => sp.GetRequiredService<NotesService>());
        // Узкий шов наружу — Core-контракт `INoteAccessor` (7 методов по фактическим
        // вызовам спины) реализует сам `NotesService` (как и `INoteSummaryReader`).
        // Форвардер на уже зарегистрированный синглтон, а не `AddSingleton<INoteAccessor,
        // NotesService>()`: вторая регистрация типа дала бы ВТОРОЙ экземпляр сервиса. Гейт
        // подсистемы — структурный: при `Subsystems:Notes:Enabled=false` `Register` не
        // зовётся вовсе, шва в DI нет, а потребители держат `INoteAccessor?` и деградируют в null.
        services.AddSingleton<INoteAccessor>(sp => sp.GetRequiredService<NotesService>());
        // Тот же приём для Core-шва `INoteSemanticIndex` (семантический индекс заметок):
        // форвардер на уже зарегистрированный синглтон `NotesKnowledgeService`, а не
        // `AddSingleton<INoteSemanticIndex, NotesKnowledgeService>()` — иначе второй экземпляр.
        services.AddSingleton<INoteSemanticIndex>(sp => sp.GetRequiredService<NotesKnowledgeService>());
        services.AddSingleton<NotesKnowledgeService>();
        services.AddSingleton<NotesAiService>();
        services.AddSingleton<NoteTaskSyncService>();
        // Узкий Core-шов обратной записи (Main → Notes): TasksController/TasksToolset
        // держат `INoteTaskSync?` и при выключенной подсистеме деградируют в null.
        // Форвардер на уже зарегистрированный синглтон, а не `AddSingleton<INoteTaskSync,
        // NoteTaskSyncService>()` — иначе второй экземпляр.
        services.AddSingleton<INoteTaskSync>(sp => sp.GetRequiredService<NoteTaskSyncService>());
        // Хранитель — единственный писатель data/wiki/{ownerId}/pages/ (ADR-035, реш. 2):
        // зовётся применением пакетов (S2c) и REST (S2f); наружу его контракт не выходит —
        // vertical-внутренний тип, как остальные Services/Wiki.
        // Factory, а не type-activation: здесь же навешивается внутренний шов relink —
        // NotesService вызывает Хранителя при переименовании/истечении заметки, на которую
        // ссылаются страницы вики (ADR-035, решения 2 и 6). Конструкторной зависимостью это
        // быть не может: WikiKeeper строится поверх NotesService, обратная — замкнула бы
        // цикл DI. Порядок безопасен: резолв NotesService не трогает WikiKeeper, цикл
        // был бы только у конструктора.
        services.AddSingleton<WikiKeeper>(sp =>
        {
            var notes = sp.GetRequiredService<NotesService>();
            var keeper = new WikiKeeper(notes, sp.GetRequiredService<IConfiguration>(),
                sp.GetService<IProjectEventLogService>());
            notes.WikiRelink = keeper;
            return keeper;
        });
        // Проверки и запись применения пакета (S4b-1/S4b-2): vertical-внутренний тип, как Хранитель.
        services.AddSingleton<WikiApplyService>();
        // Очередь заявок разбора: два прохода модели → проверки → пакет → применение (S6b-3).
        services.AddSingleton<WikiIngestService>();
        // Захват источников (S6h) и разбор чатов «сейчас» (S6e-2): vertical-внутренние типы,
        // как Хранитель; захват раньше в DI не стоял — без него не резолвится его потребитель.
        services.AddSingleton<WikiSourceCapture>();
        services.AddSingleton<WikiChatIngestService>();
        // Откат применённого пакета по прообразам (S4b-3, решение 12г мера 4): вызывается только
        // REST-контроллером («Журнал», кнопка человека); наружу его контракт не выходит.
        services.AddSingleton<WikiPackageRevert>();
        // Откат по источнику — «Откатить всё из этого чата» (S4f-1, решение 12г мера 5): системная
        // операция поверх откатов пакетов; вызывается только REST, наружу его контракт не выходит.
        services.AddSingleton<WikiRetractService>();
        // Здоровье классов и настройки применения (S4c, §12г меры 7–8): vertical-внутренний тип; settings.json пишет он и ручка настроек.
        services.AddSingleton<WikiClassHealth>();
        // Чтение журнала для человека (S7c-0, §12г меры 4–6): vertical-внутренний тип, только читает; зовёт его REST.
        services.AddSingleton<WikiJournalReader>();
        // Линт вики (S3-3): ночное окно + запуск по кнопке. Singleton + hosted с него же:
        // REST-кнопка (WikiController) вызывает ТУ ЖЕ службу — второй экземпляр дал бы
        // вторые слоты прогона, и «уже идёт» не берег бы от двойной записи файлов.
        services.AddSingleton<WikiLintService>();
        services.AddGatedHostedFrom<WikiLintService>(config, sp => sp.GetRequiredService<WikiLintService>(), Key);
        // Пересчёт ступени 1 отбора чатов (NA-3): тело прежнего WikiChatsController.Recalculate,
        // вынесенное в службу без изменения логики, — тот же пересчёт зовут REST-кнопка
        // («пересчитать») и ночной разбор (Ф2). Vertical-внутренний тип, как остальные Services/Wiki;
        // запись — через UpdateScoring под блокировкой корня стора (конкурентность в сторе).
        services.AddSingleton<WikiChatRecalc>();
        // Сторож старта: пакеты, застрявшие в «applying» после падения процесса, откатывает по
        // прообразам (ADR-035, решение 4). Гейт по подсистеме и по Testing — общий хелпер.
        services.AddGatedHostedService<WikiApplyStartupGuard>(config, Key);
        services.AddGatedHostedService<NoteExpiryService>(config);
        // Этап 5, шаг 6 (инверсия контрибьюторов промпта): контрибьютор секции
        // «recall-notes» зарегистрирован в своей вертикали, Turn собирает
        // IEnumerable<IPromptSectionContributor> и натравливает на шину (см.
        // PromptSectionContributorsRegistration.RegisterAll). Гейт подсистемы —
        // структурный: при `Subsystems:Notes:Enabled=false` `AddSubsystems` не зовёт
        // `Register`, и контрибьютор (он тянет `NotesKnowledgeService`) в
        // `IEnumerable<IPromptSectionContributor>` не попадает вовсе.
        services.AddPromptSectionContributor<NotesRecallContributor>();
        // MCP-over-HTTP-тулсет (ADR-012, фаза 2 волна 2): динамический модуль
        // регистрирует собственный тулсет (раньше — в Program.cs Main).
        services.AddSingleton<ClaudeHomeServer.Services.Mcp.Http.IMcpToolset,
            ClaudeHomeServer.Services.Mcp.Http.NotesToolset>();
        // Обработчик права notes:read моста панели (ADR-031 §2.4, §3.4, шаг 3.5):
        // операции notes.search / notes.read. Регистрирует сама вертикаль-владелец
        // (направление Notes → Core ← Plugins, прямой ссылки на Plugins нет). Мост
        // (Plugins) резолвит IEnumerable<IPluginCapability> и находит этот обработчик
        // по домену «notes». Гейт подсистемы — структурный: при Subsystems:Notes:Enabled=false
        // Register не зовётся, шва в DI нет, и notes:read → unavailable (503) у моста.
        services.AddSingleton<IPluginCapability, NotesPluginCapability>();
    }
}
