using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Memory;

// Подсистема Memory: долгая память персон (P3-P4) и общая память команды проекта (③-3.4).
// Регистрирует два фасада стора (per-persona и per-project), их сервисы консолидации,
// авто-память по итогам ходов и общий слой Dify-синка.
//
// Состав (ранее жил блоком в Program.cs:212-221):
// - PersonaMemoryService — фасад персональной памяти: стор `data/persona-memory.json`
//   + Dify-датасет per-persona, дебаунс 15 с, recall по relevance×recency×typeWeight;
//   реализует `IKnowledgeSyncParticipant` (форвардер регистрации остаётся в Program.cs).
// - PersonaMemoryConsolidationService (BackgroundService) — LLM-merge дублей/родственных
//   (операции merge/drop) + вытеснение по retention-скорингу. Singleton + hosted через
//   AddGatedHostedFrom (тот же инстанс, что и в DI: autolearn ставит заявки через
//   RequestConsolidation).
// - PersonaMemoryAutolearnService (IHostedService) — one-shot извлечение фактов из
//   завершённого хода персонной сессии. Singleton + hosted через AddGatedHostedFrom.
// - TeamMemoryService — фасад общей памяти команды: стор `data/team-memory.json`
//   + Dify-датасет `{username}:team:{projectName}`, та же дебаунс-семантика, recall
//   из всех сессий проекта. Реализует `IKnowledgeSyncParticipant` (форвардер в Program.cs).
// - TeamMemoryConsolidationService (BackgroundService) — командный аналог
//   Persona-консолидации: merge дублей по типу + вытеснение по TeamMemory:MaxEntries.
//   Singleton + hosted через AddGatedHostedFrom.
// - TeamMemoryAutolearnService (IHostedService) — командный аналог autolearn: one-shot
//   из хода в проектной/групповой/совещательной сессии. Singleton + hosted через
//   AddGatedHostedFrom (тот же инстанс, что и в DI — общий паттерн со всеми
//   consolidation/autolearn сервисами).
//
// ⚠ Два форвардера `IKnowledgeSyncParticipant → {PersonaMemoryService, TeamMemoryService}`
// (Program.cs:~688-691) остаются в композиционном корне сознательно — кросс-вертикальный
// клей реконсайлера error-документов Dify (KnowledgeIndexReconciler). Перенос в
// подсистему дал бы Memory прямые ссылки на чужую Knowledge-вертикаль; см. шапку
// `KnowledgeSubsystem.cs` — те же форвардеры для DossierStore/NotesKnowledgeService/
// ProjectKnowledgeSyncService живут рядом по той же причине.
//
// Границы (сознательные):
// - Источник истины — JSON-сторы `data/persona-memory.json` и `data/team-memory.json` +
//   Dify-датасеты per-persona/per-project. Бэкап идёт общим правилом `data/`, отдельно
//   ничего не прописываем.
// - Общий слой `Services.Memory` (MemoryWriteResolver, MemoryDify, MemoryDocRef и т.п.) —
//   префикс открывается самим `Services.Memory`, проверяется SubsystemBoundaryTests.
// - `ICheapTextRunner` (Services.Llm) — консолидация и autolearn через локальную модель
//   или haiku (PersonaMemoryConsolidationService, PersonaMemoryAutolearnService,
//   TeamMemoryConsolidationService, TeamMemoryAutolearnService). Префикс-шов, как
//   у Dossiers/Spend.
// - Прочие типы корня Services (PersonaManager/SessionManager/ProjectManager/
//   ProjectEventLogService) — «вертикаль → спинка» (общая инфраструктура доменных
//   моделей), как у Git/Spend/Tts/Images/Deploy.
// - `ClaudeHomeServer.Hubs` — `IHubContext<SessionHub>` для нотификации о новых записях
//   памяти команды (TeamMemoryAutolearnService шлёт `memory_changed` в ленту сессии).
// - Точечный допуск к `ClaudeHomeServer.Protocol` (по аналогии с Dossiers): типы
//   WS-событий, которые `PersonaMemoryAutolearnService`/`TeamMemoryAutolearnService`
//   разбирают из истории ходов, попадают в поля async-state-машин. Префикс
//   `ClaudeHomeServer.Protocol` снят (см. Boundaries[Knowledge]), точные имена —
//   в allow-list.
//
// Типы самих фасадов (`PersonaMemoryService`/`TeamMemoryService`/`PersonaMemoryConsolidationService`/
// `PersonaMemoryAutolearnService`/`TeamMemoryConsolidationService`/`TeamMemoryAutolearnService`)
// живут в `ClaudeHomeServer.Services` (корень) и подключаются через `AddSingleton<T>()` /
// `AddHostedService<T>()` / `AddGatedHostedFrom` без смены namespace — см. шапку
// DossiersSubsystem.cs / KnowledgeSubsystem.cs о причинах такой аранжировки
// (тип переезжает только вместе со всеми своими потребителями и тестами — отдельная задача).
public sealed class MemorySubsystem : IAppSubsystem
{
    public string Key => "memory";

    public string Title => "Память персон и команды";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // Персональная память — фасад стора + Dify-датасет, recall по типизированным
        // записям (semantic/episodic/procedural) с relevance×recency×typeWeight.
        services.AddSingleton<PersonaMemoryService>();

        // Консолидация памяти персон — singleton + hosted через AddGatedHostedFrom:
        // autolearn ставит заявки через RequestConsolidation на тот же инстанс, что
        // и в DI (см. шапку DossierAutoExporter — прецедент той же аранжировки).
        services.AddSingleton<PersonaMemoryConsolidationService>();
        services.AddGatedHostedFrom(config, sp => sp.GetRequiredService<PersonaMemoryConsolidationService>());

        // Авто-память персоны — singleton + hosted: PersonaAskService пишет память
        // после консультаций напрямую, hosted слушает SessionManager.OnSessionMessage.
        services.AddSingleton<PersonaMemoryAutolearnService>();
        services.AddGatedHostedFrom(config, sp => sp.GetRequiredService<PersonaMemoryAutolearnService>());

        // Память команды проекта — фасад стора + Dify-датасет per-project, recall
        // для всех персон команды. Тип-эталон — PersonaMemoryService.
        services.AddSingleton<TeamMemoryService>();

        // Консолидация памяти команды — singleton + hosted через AddGatedHostedFrom.
        services.AddSingleton<TeamMemoryConsolidationService>();
        services.AddGatedHostedFrom(config, sp => sp.GetRequiredService<TeamMemoryConsolidationService>());

        // Авто-память команды — singleton + hosted: добавление singleton нужно, чтобы
        // тесты подсистемы могли резолвить TeamMemoryAutolearnService из DI-графа
        // (AddGatedHostedService сам по себе регистрирует только IHostedService, без
        // singleton-дескриптора; в Testing-среде гейт хостед вообще выключен, и
        // тип оказывается отсутствующим). Прецедент — `PersonaMemoryAutolearnService`.
        services.AddSingleton<TeamMemoryAutolearnService>();
        services.AddGatedHostedFrom(config, sp => sp.GetRequiredService<TeamMemoryAutolearnService>());
    }
}
