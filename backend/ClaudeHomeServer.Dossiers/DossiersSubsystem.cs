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
// - Вертикаль живёт в отдельной сборке `ClaudeHomeServer.Dossiers` (Этап 5, волна 3,
//   финал), единственный `ProjectReference` — на Core. Ссылок на Main нет ни одной, и
//   держит это компилятор, а не только сторож границ: allow-list в Boundaries[Dossiers]
//   ПУСТ поверх общей спинки.
// - Всё, что нужно от остальной системы, идёт Core-швами: `IGitRefSnapshotStore`
//   (снапшот-реф ветки паспортов; владение константами — своё, см. `DossierBranch`),
//   `IGitCommitInspector` (инспекция коммитов при захвате), `ICodeGraphInspector`
//   (обогащение паспортов графом кода), `ICheapTextRunner` (выжимка паспортов и
//   конспектов), `ISessionDirectory`/`IProjectManager`/`IUserStore`/`ITaskLookup`,
//   `IFeatureFlagGate` (гейт флага `change-dossiers-recall`), `IKnowledgeIndex`
//   (клиент Dify) и `IKnowledgeSyncParticipant` (участие в реконсайлере),
//   `SessionIdGuard` (валидация id из трейлера коммита), `SessionTranscript`
//   (сборка транскрипта под паспорт), `SessionChangedPaths` (нормализация якорей),
//   `InstanceSecretFiles` (реестр имён секретов инстанса).
// - Общий слой Dify-синка (`MemoryDocRef`/`MemoryDifyDebouncer`/`MemorySyncItem`,
//   та же дебаунс-семантика, что у памяти персон) живёт в Core, `Core/Services/Memory/`.
// - `IDossierRecallSource` (Core) — шов НАРУЖУ: его реализует `DossierRecallService`,
//   а зовёт вертикаль Memory из auto-recall персоны. Форвардер регистрируем сами (ниже),
//   потому что контракт объявляет сторона-поставщик.
// - `ClaudeHomeServer.Protocol` — WS-контракт, часть общей спинки (Core):
//   `StoredMessage` в полях async-state-машин
//   `DossierCaptureService+<BuildTranscriptAsync>d__NN` и
//   `DossierDiscussionService+<EnsureOneAsync>d__NN`.
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
        // Форвардер Core-шва пассивного recall: его потребитель — вертикаль Memory
        // (`PersonaMemoryService.BuildRecallAsync`), и объявляет контракт сторона-поставщик,
        // иначе связь была бы прямой «вертикаль → вертикаль» (Этап 5, волна 3).
        services.AddSingleton<IDossierRecallSource>(
            sp => sp.GetRequiredService<DossierRecallService>());
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