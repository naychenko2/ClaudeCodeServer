using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Http;

namespace ClaudeHomeServer.Services.Git;

// Подсистема Git: реестр `GitService`, поверх него HTTP-контроллеры (`GitServerService`),
// `GitAiService` (локальный one-shot для сообщений коммита и имён stash). Реакторы
// `GitAutoCommitService`/`CommitAttributionService` живут в корне `Services.*` и
// регистрируются в Program.cs (Этап 3, уборка Git) — это сознательная связь
// «вертикаль → спинка» по прецеденту `PersonaMemoryAutolearnService`.
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
        // IGitRepoChecker — узкий шов для Deploy (HEAD + dirty-дерево), паттерн
        // вчерашних трёх швов: контракт в Core, реализация — сам GitService.
        services.AddSingleton<IGitRepoChecker>(sp => sp.GetRequiredService<GitService>());
        // IGitWorkingTree — шов для вертикали Files (git-статус дерева, дифф и откат файла).
        services.AddSingleton<IGitWorkingTree>(sp => sp.GetRequiredService<GitService>());
        services.AddSingleton<GitServerService>();

        services.AddSingleton<GitAiService>();

        // Forgejo — локальный сервис: egress-прокси ему противопоказан.
        services.AddHttpClient("forgejo").WithoutEgressProxy();
    }
}
