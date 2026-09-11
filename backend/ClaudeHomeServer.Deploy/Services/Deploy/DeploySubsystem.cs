using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Deploy;

// Подсистема «Выкатка на бой» — ДВА контура, живущих в одной подсистеме, потому что
// по отдельности они слишком тонкие, чтобы держать отдельные IAppSubsystem.
//
// 1) ADR-010: выкатка прода из чата. Принимает заявку, проверяет git-guard,
//    будит задачу планировщика и пишет журнал `deploy-state.json` в `Deploy:ReleasesDir`
//    как ШОВ с внешним агентом (оба процесса пишут под мьютексом `Global\ccs-deploy`).
//    Доклад об итоге присылает уже новый инстанс (`DeployReportService`).
//    Сервисы: `BuildIdProvider`, `IDeployHost` / `DeployHost`, `DeployService`,
//    hosted `DeployReportService`.
//
// 2) Выкатка на бой пунктом меню (трей-раннер): сигнал трею через именованное
//    событие Windows, чтение итога из `deploy-status.json` (чужой формат раннера,
//    только чтение). Сервисы: `TrayDeployOptions`, `ITrayGate` / `WindowsTrayGate`,
//    `DeployLauncher`.
//
// Известные границы (сознательные, см. CLAUDE.md «Внутренные подсистемы»):
// - `DeployHost` использует `IGitRepoChecker` (Services.Composition, Core) — узкий шов
//   на `GitService` (HEAD + dirty-дерево). Связь «вертикаль → вертикаль» снята:
//   `Services.Git` больше не в allow-list `Deploy`.
// - `DeployHost` использует `ILauncherFactory` (Services.Execution) — легально: Git-нижний
//   слой, и Deployment как нижний слой для любой будущей вертикали «Deploy-anything».
// - `DeployHost.TryLockAgent` берёт мьютекс `Global\ccs-deploy` через `Backup.InstanceLock`
//   (Services.Backup, статический класс). Это инфраструктурный примитив общего назначения
//   (мьютекс деплоя), а не зависимость от логики Backup, и TODO на шов: выделить мьютекс
//   в общий примитив (например, `DeployAgentLock` в `Services.Composition`) и убрать из
//   `Deploy` ссылку на `Services.Backup`.
// - `DeployReportService` → `SessionManager` + `NotificationService` (корень Services):
//   это «вертикаль → спинка» (общая инфраструктура), аналогично `Tts`/`Images`/`Git`.
// - `BuildIdProvider` живёт в Deploy, а читается `HealthController` (Controllers/).
//   Контроллер — внешняя зависимость подсистемы, шов через DI, отдельного шва не требует.
// - MCP-тулы `deploy_*` остаются в `WorkspaceToolset` (входящая зависимость,
//   переезд в подсистему — отдельное решение).
// - `AdminByStoreRequirement` (Program.cs ~743-752) — auth-инфраструктура, не переезжает
//   (политика для deploy_* MCP-тулов живёт на уровне авторизации, а не фичи).
//
// Сторы вне `data/`:
// - `deploy-state.json` в `Deploy:ReleasesDir` — ШОВ с внешним агентом (ADR-010),
//   пишут оба процесса под мьютексом `Global\ccs-deploy`.
// - `deploy-status.json` — чужой формат трей-раннера, читаем, не пишем.
// - `reported-{deployId}` рядом с `deploy-state.json` — отметка «доложено», только сервер.
// - `build-id.txt` рядом с exe — кладёт агент, читает `BuildIdProvider` на старте.
public sealed class DeploySubsystem : IAppSubsystem
{
    public string Key => "deploy";

    public string Title => "Выкатка";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // Контур 1: выкатка прода из чата (ADR-010).
        // BuildIdProvider читает `build-id.txt` ОДИН РАЗ при старте: оно описывает
        // именно этот экземпляр, и перечитывание отдавало бы чужой идентификатор.
        services.AddSingleton<BuildIdProvider>();
        // IDeployHost — шов для тестов: проверять надо guard'ы и коды ответа, а не
        // наличие git и Task Scheduler на раннере CI. Реализация (DeployHost) лежит
        // на Git + Execution — см. границы в комментарии выше.
        services.AddSingleton<IDeployHost, DeployHost>();
        services.AddSingleton<DeployService>();
        // Доклад об итоге выкатки (ADR-010): новый инстанс читает журнал и, если
        // итог есть и не доложен, шлёт уведомление и сообщение в чат-инициатор.
        // Singleton + hosted: подписки в StartAsync встают на тот же объект, что и в DI
        // (нужно, чтобы `ReportPendingAsync` могли звать и тесты, и сам hosted-цикл).
        services.AddGatedHostedService<DeployReportService>(config);

        // Контур 2: выкатка на бой из веб-морды (трей-раннер).
        // TrayDeployOptions живёт в Models (доменная модель, «спинка»), а не здесь —
        // это настройка, а не сервисная логика.
        services.Configure<TrayDeployOptions>(config.GetSection(TrayDeployOptions.Section));
        // ITrayGate — шов ради тестов: реализация опирается на именованные объекты
        // ядра Windows, а бэкенд собирается и тестируется на Linux (CI).
        services.AddSingleton<ITrayGate, WindowsTrayGate>();
        services.AddSingleton<DeployLauncher>();
    }
}