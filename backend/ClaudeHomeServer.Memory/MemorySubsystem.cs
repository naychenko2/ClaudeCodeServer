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
// - Вертикаль живёт в отдельной сборке `ClaudeHomeServer.Memory` (Этап 5, волна 3,
//   финал), единственный `ProjectReference` — на Core. Ссылок на Main нет ни одной, и
//   держит это компилятор, а не только сторож границ: allow-list в Boundaries[Memory]
//   ПУСТ поверх общей спинки.
// - Общий слой `Services.Memory` (MemoryWriteResolver, MemoryConsolidationCore и т.п.) —
//   свой же namespace вертикали; часть ядра (MemoryDify/MemoryFulltext/MemoryLlmParsing/
//   IPersonaRecallSource) живёт в Core, `Core/Services/Memory/`.
// - Всё, что нужно от остальной системы, идёт Core-швами: `ICheapTextRunner`
//   (консолидация и autolearn через локальную модель или haiku), `IPersonaResolver`/
//   `IPersonaLookup`/`IPersonaDirectory` (персоны), `ISessionDirectory`/`IProjectManager`/
//   `IUserStore`, `IProjectEventLogService`, `IKnowledgeIndex`/`IKnowledgeSyncParticipant`
//   (Dify), `INoteAccessor` (вынос записи в vault), `IDifyMetrics` (метрика синка),
//   `ISessionBroadcaster` (событие `memory_changed` в ленту сессии — раньше был прямой
//   `IHubContext<SessionHub>`), `SessionTranscript` (сборка транскрипта под autolearn).
// - `IDossierRecallSource` (Core, ОДИН метод) — пассивный recall паспортов изменений
//   в auto-recall персоны. Шов ОПЦИОНАЛЬНЫЙ: «канала паспортов нет» — штатное состояние
//   (юнит-тесты, владелец без флага), на этом стоит публичный
//   `PersonaMemoryService.DossierRecallAvailable`. До волны 3 тут была прямая ссылка
//   на `Dossiers.DossierRecallService` — последняя связь «вертикаль → вертикаль».
// - `ClaudeHomeServer.Protocol` — WS-контракт, часть общей спинки (Core):
//   `StoredMessage`/`StoredUserMessage`/`StoredTextMessage` в сигнатурах public-методов
//   `AutolearnGate.CheckContent` и `LastTurnLength`.
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
