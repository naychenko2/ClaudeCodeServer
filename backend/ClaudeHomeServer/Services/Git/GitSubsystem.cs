using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Http;

namespace ClaudeHomeServer.Services.Git;

// Подсистема Git: реестр `GitService`, поверх него HTTP-контроллеры (`GitServerService`),
// `GitAiService` (локальный one-shot для сообщений коммита и имён stash), фоновый
// авто-commit/push (`GitAutoCommitService`) и `CommitAttributionService` (детект коммита
// по сдвигу HEAD — помечает чатам зафиксированные пути, чтобы атрибуция файлов чатам
// не врала после коммита).
//
// Границы (сознательные):
// - Источник истины — сам git: своих сторов подсистема не заводит.
// - Forgejo HTTP-клиент тихо выключен без `Forgejo:BaseUrl`/`Forgejo:AdminToken`
//   (см. `GitServerService`) — здесь только регистрация, логика включения в самом
//   сервисе.
// - Per-project настройки `Project.GitAutoCommit` / `Project.GitRemoteUrl` живут
//   в `Models/Project.cs` (доменная модель, «спина»), а не в конфиге подсистемы.
// - Записи каталога `git-commit-msg` / `git-stash-name` остаются в `LocalActionCatalog`
//   (`Services/Llm`) — это места применения локальной модели, а не регистрации Git.
//
// ⚠ Инвариант клиента: Forgejo — локальный сервис, идёт БЕЗ egress-прокси
// (`WithoutEgressProxy` обязателен). Менять на `AddQuietHttpClient` — отдельное
// решение (см. ADR-014 / разведку), не в этой задаче.
public sealed class GitSubsystem : IAppSubsystem
{
    public string Key => "git";

    public string Title => "Git";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<GitService>();
        // Generic plumbing произвольной ветки-паспорта (commit-on-plumbing) — теми же
        // методами пользуются Dossiers (ветка ccs/dossiers/v1) и любая будущая
        // вертикаль с собственной веткой-паспортом. GitService — единственная реализация,
        // отдельный экземпляр не заводим (синглтон шарится между интерфейсом и классом).
        services.AddSingleton<IGitRefSnapshotStore>(sp => sp.GetRequiredService<GitService>());
        services.AddSingleton<GitServerService>();

        // Режим документов: авто-commit/push после каждого хода Claude (Project.GitAutoCommit)
        services.AddGatedHostedService<GitAutoCommitService>(config);

        services.AddSingleton<GitAiService>();

        // Детект коммита по сдвигу HEAD: помечает чатам зафиксированные пути
        // (Session.CommittedFilePaths), чтобы атрибуция файлов чатам не врала
        // после коммита — см. CommitAttributionService.
        services.AddSingleton<CommitAttributionService>();

        // Forgejo — локальный сервис: egress-прокси ему противопоказан.
        services.AddHttpClient("forgejo").WithoutEgressProxy();
    }
}
