using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Http;

namespace ClaudeHomeServer.Services.Knowledge;

// Подсистема Knowledge: единая точка входа Dify RAG (ADR-013 + Knowledge.md).
// Регистрирует стор рабочего пространства, сервис Dify-моста, синк файлов проекта
// с документами Dify (hosted-мост на ход Claude), каскадную уборку при удалении
// пользователя, реконсайлер error-документов Dify и каталог баз знаний
// (общий для REST /api/knowledge/catalog и wsp-тулсета Dify).
//
// Состав (ранее жил тремя блоками в Program.cs:323, 388, 668-693 без форвардеров):
// - WorkspaceKnowledgeStore — JSON-стор `data/workspace-knowledge.json` (привязка
//   «путь проекта → {DifyDatasetId, DocumentTags}»); миграция из старых полей Project
//   выполняется ОДНОКРАТНО в блоке PostConfigure (см. ниже — причина отдельной жизни).
// - KnowledgeService — клиент Dify: список/создание документов, поиск, ретривер,
//   graceful degradation при недоступном Dify.
// - KnowledgeBaseCatalogService — менеджер Dify-датасетов под пользователя (общий
//   для REST /api/knowledge/catalog и wsp-тулсета Dify, см. ADR-014 §Knowledge).
// - ProjectKnowledgeSyncService — дебаунс-синк «файл проекта ↔ документ БЗ»:
//   singleton + мост событий хода Claude (ProjectKnowledgeTurnSync hosted подписывается
//   на FileService.OnMutated).
// - UserKnowledgeCascade — каскадная уборка знаний при удалении пользователя
//   (UsersController).
// - IKnowledgeAlertNotifier + KnowledgeAlertNotifier — шов нотификатора для
//   реконсайлера (юнит-тесты дедупа, как у ISubscriptionAlertNotifier).
// - KnowledgeIndexReconciler (hosted через AddGatedHostedFrom) — фоновая петля
//   реконсайлера error-документов Dify (Dify:Reconcile, дефолт Mode=off — dark launch).
// - HTTP-клиент `dify` (AddQuietHttpClient + WithoutEgressProxy) — Dify локальный,
//   egress-прокси ему противопоказан (инвариант, как у Forgejo).
//
// ⚠ Миграция `WorkspaceKnowledgeStore.MigrateFromProjects` НЕ переехала из Program.cs
// (PostConfigure ~строка 993). Причина: `UseSubsystems()` (`AddSubsystems(...)`) выполняется
// ДО `PostRestoreHook.RunIfNeeded`, и перенос миграции в `IAppPhaseSubsystem.ConfigureApp`
// сломал бы восстановление из бэкапа — инвариант требует, чтобы `WorkspaceKnowledgeStore`
// НЕ конструировался до хука.
//
// ⚠ Пять форвардеров `IKnowledgeSyncParticipant → {PersonaMemoryService, TeamMemoryService,
// DossierStore, NotesKnowledgeService, ProjectKnowledgeSyncService}` тоже остаются
// в Program.cs — это кросс-вертикальный клей между пятью владельцами стора «запись →
// {DocId, Hash}» (PersonaMemory, TeamMemory, Dossiers, Notes, ProjectSync). Перенос в
// `KnowledgeSubsystem` дал бы ей прямые ссылки на `DossierStore` и прочие чужие вертикали.
//
// Границы (сознательные):
// - Источник истины — Dify + локальный стор `data/workspace-knowledge.json`. Бэкап идёт
//   общим правилом `data/`; отдельно ничего не прописываем.
// - `KnowledgeService`/`ProjectKnowledgeSyncService`/`UserKnowledgeCascade`/`KnowledgeBaseCatalogService`
//   живут в КОРНЕ `ClaudeHomeServer.Services` (доменная инфраструктура) — сторож границ
//   в SubsystemBoundaryTests.Boundaries их НЕ покрывает (он сканирует только типы из
//   `NamespaceRoot`). Это сознательное ограничение: чтобы перенести их в `Services.Knowledge`
//   и закрыть проверкой, нужен отдельный шаг с переименованием namespace и правкой всех
//   импортов по проекту. Сейчас ни одна из этих ссылок на корень не нарушает границу
//   вертикали Knowledge, потому что сам сторож её не видит — но и защиты у нас нет.
//   TODO на шов: перенести типы в `Services.Knowledge`, тогда правила ниже начнут
//   работать «по-настоящему».
// - `ICheapTextRunner` (Services.Llm) — KnowledgeIndexReconciler не использует, но
//   `ProjectKnowledgeSyncService` (вне записи) при будущем расширении может; сейчас
//   допуск не нужен.
// - `NotificationService`/`NotificationStore` (Services/ корень) — `KnowledgeAlertNotifier`
//   шлёт алерт владельцу через общий нотификатор; это «вертикаль → спинка» (как у Git),
//   см. `KnowledgeAlertNotifier.cs:23-24`.
// - `IKnowledgeAlertNotifier`/`KnowledgeIndexReconciler`/`KnowledgeSyncTarget`/`IKnowledgeSyncParticipant`
//   — свои, в `Services.Knowledge` (покрыты записью Boundaries напрямую).
public sealed class KnowledgeSubsystem : IAppSubsystem
{
    public string Key => "knowledge";

    public string Title => "Знания";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // WorkspaceKnowledgeStore — стор `data/workspace-knowledge.json`, миграция из Project
        // выполняется отдельным пост-билд блоком в Program.cs (см. шапку файла).
        services.AddSingleton<WorkspaceKnowledgeStore>();

        // Каталог баз знаний Dify под пользователя — общий для REST /api/knowledge/catalog
        // и wsp-тулсета Dify (см. ADR-014 §Knowledge).
        services.AddSingleton<KnowledgeBaseCatalogService>();

        // Dify — локальный сервис, egress-прокси ему противопоказан (инвариант, как у Forgejo).
        services.AddQuietHttpClient("dify", new QuietHttpClientProfile(
            Category: "ClaudeHomeServer.Knowledge.Dify",
            Subject: "базой знаний Dify",
            Consequence: "Семантический поиск по заметкам и знаниям не работает."))
            .WithoutEgressProxy();

        // Секция DifyOptions (источник правды для KnowledgeService/ProjectKnowledgeSyncService/
        // KnowledgeIndexReconciler и других потребителей).
        services.Configure<DifyOptions>(config.GetSection(DifyOptions.Section));

        // Клиент Dify: список/создание документов, поиск, ретривер; graceful degradation
        // при недоступном Dify (см. KnowledgeService).
        services.AddSingleton<KnowledgeService>();

        // Синк «файл проекта ↔ документ БЗ»: singleton + hosted-мост событий хода Claude
        // (мост заодно гарантирует инстанцирование синка — подписку на FileService.OnMutated).
        services.AddSingleton<ProjectKnowledgeSyncService>();
        services.AddGatedHostedService<ProjectKnowledgeTurnSync>(config);

        // Каскадная уборка знаний при удалении пользователя (UsersController).
        services.AddSingleton<UserKnowledgeCascade>();

        // Шов нотификатора (для тестов дедупа KnowledgeIndexReconciler, по аналогии с
        // ISubscriptionAlertNotifier).
        services.AddSingleton<IKnowledgeAlertNotifier, KnowledgeAlertNotifier>();

        // Реконсайлер error-документов Dify (Dify:Reconcile, дефолт Mode=off — dark launch):
        // singleton + hosted, чтобы снапшот состояния был доступен видимости.
        services.AddSingleton<KnowledgeIndexReconciler>();
        services.AddGatedHostedFrom(config, sp => sp.GetRequiredService<KnowledgeIndexReconciler>());
    }
}