using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Dossiers;

// Подсистема Dossiers: паспорта изменений (ADR-004). Регистрирует стор, hosted-захват
// коммитов и обсуждений, автовыгрузку в локальную ветку ccs/dossiers/v1 и автоимпорт
// по новому tip, плюс recall-сервис для пассивного канала паспортов в промпте персон.
//
// Состав (ранее жил одним блоком в Program.cs:166-183):
// - InstanceSecretsProvider — точные значения секретов инстанса для SecretRedactor;
// - DossierStore — JSON-стор паспортов (data/dossiers/{owner}/{project}.json),
//   плюс Dify-датасет для семантического recall (graceful degradation);
// - DossierCaptureState — последний HEAD рабочего дерева (не проекта, ADR-004 §2
//   major-правка Глеба №3) и tip ветки паспортов;
// - DossierRecallService — пассивный recall в промпт персон (до 3 паспортов × ≤300
//   символов, суммарно ≤1 КБ);
// - DossierDiscussionStore/Service — конспекты обсуждений по ADR-004 §6,
//   снимаются ТОЛЬКО по явной команде человека (ручная выгрузка);
// - DossierCaptureService (hosted) — захват паспортов по коммитам + по
//   завершении хода (подписка на SessionManager.OnSessionMessage);
// - DossierAutoExporter (singleton + hosted) — автовыгрузка в ветку ccs/dossiers/v1
//   с дебаунсом 90 с, hosted создаётся через `AddGatedHostedFrom` чтобы подписки
//   в StartAsync встали на ТОТ ЖЕ экземпляр, что и в DI;
// - DossierAutoImporter (hosted) — наблюдение за tip ветки, тик 60 с.
//
// ⚠ Форвардер `IKnowledgeSyncParticipant → DossierStore` живёт в блоке Knowledge
// (Program.cs:~688-689) — участник реконсайлера error-документов Dify. После
// выделения `KnowledgeSubsystem` (волна 3, шаг 3) он ОСТАЁТСЯ в композиционном
// корне сознательно: это кросс-вертикальный клей пяти владельцев стора
// «запись → {DocId, Hash}», и перенос в Knowledge дал бы ей прямую ссылку на
// `DossierStore`. См. шапку `KnowledgeSubsystem.cs`.
//
// Границы (сознательные):
// - Источник истины — свой стор `data/dossiers/*`. Бэкап идёт общим правилом `data/`,
//   отдельно ничего не прописываем.
// - `Git.GitService` (Services.Git) — захват коммитов (DossierCaptureService.cs:53),
//   recall (DossierRecallService), автовыгрузка/автоимпорт (DossierAutoExporter/
//   Importer). TODO на шов: завести `IGitGuard` в Services.Git и перевести вертикаль
//   на него — по аналогии с Deploy→Git.
// - `CodeGraph.CodeGraphService` (Services.CodeGraph) — обогащение паспортов графом
//   кода (DossierCaptureService.cs:54, DossierRecallService). TODO на шов —
//   `ICodeGraphSnapshot` или аналог.
// - `ICheapTextRunner` (Services.Llm) — выжимка паспортов и конспектов через локальную
//   модель или haiku (DossierCaptureService, DossierDiscussionService).
// - `Services.Memory` — общий слой Dify-синка: `MemoryDocRef`/`MemoryDifyDebouncer`
//   используются в `DossierStore`/`DossierAutoExporter` для той же дебаунс-семантики,
//   что и у `PersonaMemoryService`/`TeamMemoryService`. Префикс-шов, как у `Git`/`Deploy`.
// - Прочие типы корня Services (SessionManager/ProjectManager/TaskManager/FileService/
//   UserStore/FeatureFlagService) — «вертикаль → спинка» (общая инфраструктура
//   доменных моделей), как у `Git`/`Spend`/`Tts`/`Images`/`Deploy`.
// - `Knowledge.KnowledgeService`/`Knowledge.KnowledgeSyncTarget` (Services.Knowledge
//   после переноса шага 6) — клиент Dify в `DossierStore` и участие в реконсайлере;
//   точечный allow-list, см. Boundaries[Dossiers].
// - `Protocol.StoredMessage` (точечный allow-list) — поля async-state-машин
//   `DossierCaptureService+<BuildTranscriptAsync>d__39` и
//   `DossierDiscussionService+<EnsureOneAsync>d__10` (сами методы private/internal —
//   сторож их сигнатуры не читает). `ServerMessage` тут НЕ нужен — присутствует только в
//   private-методе `OnSessionMessageAsync`, рефлексия private не сканирует.
//
//
public sealed class DossiersSubsystem : IAppSubsystem
{
    public string Key => "dossiers";

    public string Title => "Паспорта изменений";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<InstanceSecretsProvider>();
        services.AddSingleton<DossierStore>();
        services.AddSingleton<DossierCaptureState>();
        services.AddSingleton<DossierRecallService>();
        services.AddSingleton<DossierDiscussionStore>();
        services.AddSingleton<DossierDiscussionService>();
        services.AddGatedHostedService<DossierCaptureService>(config);
        // Автовыгрузка паспортов в локальную ветку ccs/dossiers/v1 после захвата —
        // singleton + hosted (подписка на стор в StartAsync): тот же экземпляр, что в DI.
        services.AddSingleton<DossierAutoExporter>();
        services.AddGatedHostedFrom(config, sp => sp.GetRequiredService<DossierAutoExporter>());
        // Автоимпорт паспортов по новому tip ветки ccs/dossiers/v1 (тумблер проекта
        // AutoImportDossiers): наблюдение за веткой тиком 60 с, без fetch/pull.
        services.AddGatedHostedService<DossierAutoImporter>(config);
    }
}