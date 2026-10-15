# Выкатка прода из чата на Linux-контуре

Включение и настройка контура выкатки из чата на Linux-инстансе (ADR-010, раздел
«Контур Linux»). До этого прод выкатывается по старой ручной процедуре —
[release-linux.md](release-linux.md); она остаётся штатной первое время, потом
включается секция `Deploy` в `appsettings.Local.json` и `deploy_start` берёт её
на себя.

## Включение после первой рабочей выкатки

Порядок осознанный: новый серверный код (включая сам контур `deploy_start` →
`deploy-linux.sh`) доезжает до прода через ручную выкатку, и только после этого
секция `Deploy.Enabled` ставится в `true`. Иначе заявка `deploy_start` из чата
отказала бы: сервер ещё не умеет платформо-чувствительную ветку, агента на
`/opt/ccs` нет, и любая попытка провалилась бы `Misconfigured` или `LaunchFailed`.

Шаги:

1. Штатная выкатка по [release-linux.md](release-linux.md), чтобы на проде
   оказался код с готовой Linux-веткой `IDeployHost.WakeAgentAsync` →
   `BuildLinuxWakeSpec` и оркестратор `scripts/ops/deploy-linux.sh`. На этом
   этапе `Deploy.Enabled=false` — `POST /api/deploy/start` отвечает
   `Disabled`, всё остальное работает по-прежнему.
2. Раскидать `scripts/ops/` с прод-копией (`/home/an/Sources/ClaudeCodeServer`
   для `AgentDir`) и подготовить relcheck (`/home/an/Sources/ClaudeCodeServer-relcheck`
   рядом — тот же репозиторий отдельным worktree, берётся для чистого
   `git checkout --detach` без правки основного дерева). Проверить, что
   `scripts/ops/deploy-linux.sh` исполняемый.
3. Вписать секцию `Deploy` в `appsettings.Local.json` (см. [«Секция Deploy»]
   (#секция-deploy-в-appsettingslocaljson) ниже) — **точные** имена полей и
   дефолты — в [`DeployOptions.cs`](../../backend/ClaudeHomeServer.Deploy/Services/Deploy/DeployOptions.cs).
   `appsettings.Local.json` — единственное место машинно-специфичных путей и
   секретов, в `appsettings.json` их нет (корневая карта, раздел
   «Конфигурация»).
4. Рестарт бэкенда (см. `systemctl --user restart ccs.service`, рядом с
   наблюдением по release-linux.md): новая конфигурация подхватывается при
   старте; `Deploy.Enabled=true` до рестарта ни на что не влияет — `From`
   зовётся в bootstrap.
5. Проверить, что эндпоинт `POST /api/deploy/start` перестал отвечать
   `Disabled` (admin-токен): первая проба с `ref` = текущий sha и
   `allowDirty=false` ОБЯЗАНА упасть guard'ом `DirtyTree` или пройти — но НЕ
   `Disabled`/`Misconfigured`/`GitFailed`. Дальше — штатный цикл:
   `deploy_start` (чат) → журнал → `systemd-run` → оркестратор → health → отчёт.

## Секция `Deploy` в `appsettings.Local.json`

```jsonc
"Deploy": {
  "Enabled": true,
  "RepoDir":    "/home/an/Sources/ClaudeCodeServer-relcheck",
  "AgentDir":   "/home/an/Sources/ClaudeCodeServer",
  "PublishDir": "/opt/ccs/app",
  "StagingDir": "/opt/ccs/staging",
  "ReleasesDir":"/opt/ccs/releases",
  "KeepReleases": 5,
  "HealthTimeoutSec": 90,
  "StaleQueuedMinutes": 15,
  "IsLinux": true
}
```

Сверка по [`DeployOptions.cs`](../../backend/ClaudeHomeServer.Deploy/Services/Deploy/DeployOptions.cs)
(имена и дефолты, те же дефолты читаются сервером, если ключ не задан):

| Ключ | Тип | Дефолт | Зачем |
|---|---|---|---|
| `Enabled` | `bool` | `false` | Главный рубильник контура. `false` — `POST /api/deploy/start` отвечает 400 (`Disabled`). |
| `RepoDir` | `string` | — | relcheck c `git checkout --detach`. Без него `Misconfigured`. |
| `AgentDir` | `string` | — | Каталог репозитория с `scripts/ops/deploy-linux.sh`. На Linux проверяется существование скрипта — без него `Misconfigured`. |
| `PublishDir` | `string` | — | Куда указывает `ccs.service`. Без него `Misconfigured`. |
| `StagingDir` | `string` | — | Сборка идёт сюда при живом сервере. |
| `ReleasesDir` | `string` | — | Снимки релизов + `deploy-state.json` + `deploy.lock`. |
| `KeepReleases` | `int` (1..20) | `3` | Сколько снимков держим. На Linux-контуре **не используется** — `switch-release.sh` хранит жёсткие 5 (`RELEASES_KEEP=5` в начале скрипта) и `Deploy.KeepReleases` не пробрасывается; на Windows-контуре (`deploy-agent.ps1`) — `3`. |
| `HealthTimeoutSec` | `int` (1..600) | `90` | Потолок гейта (см. `HEALTH_TRIES × HEALTH_SLEEP` = `18 × 5 c`). |
| `StaleQueuedMinutes` | `int` (1..1440) | `15` | TTL заявки, не сдвинутой агентом с `queued` — иначе 409 навсегда. |
| `TaskName` | `string` | `"CCS-Deploy"` | Только для Windows (`schtasks`); на Linux **не читается** — `DeployOptions.Misconfiguration` спрашивает только `AgentDir` и сам скрипт. Оставлять не нужно. |
| `IsLinux` | `bool` | `IsLinux()` из `OperatingSystem` | Тестам нужен явный ключ, на боевой машине совпадёт с реальной ОС. |

Каталоги `AgentDir` и `RepoDir` намеренно разделены: `AgentDir` — основная
рабочая копия (там же публикуется `Backend`); `RepoDir` — отдельный relcheck,
в него `git checkout --detach` ездит без касания основного дерева. Они рядом:
`AgentDir` без суффикса, `RepoDir` с `-relcheck`. `ReleasesDir`, `StagingDir`
и `PublishDir` лежат в `/opt/ccs/...`, как требует `switch-release.sh` (его
пути жёстко прописаны в константах скрипта; меняются только env при ручном
запуске).

## Что контур делает и чего не делает

- `deploy_start` из чата (MCP или REST) проверяет права, грязное дерево,
  пишет заявку в `<ReleasesDir>/deploy-state.json` и будит оркестратор через
  `systemd-run --user --collect --quiet --unit=ccs-deploy-<id> -p
  ManagedOOMPreference=avoid --setenv=CCS_RELEASES_DIR=<dir>
  <AgentDir>/scripts/ops/deploy-linux.sh`. Контракт побудки и почему именно
  так — ADR-010 § «Контур Linux».
- `deploy_status` отдаёт текущее состояние журнала (фазы, шаги с ms, итог);
  новый инстанс перечитывает, пока не доложит.
- Оркестратор `deploy-linux.sh` в фазе сборки **пересобирает образ песочницы**
  (`docker build --target sandbox --build-arg APP_UID=$(id -u) -t claude-sandbox`,
  шаг `sandbox-image`; `skipSandbox` из заявки пропускает его), а `switch-release.sh`
  в окне переключения **сносит контейнер песочницы** (`stop-sandbox` до снимка,
  `sandbox-container` после swap; имя из `Sandbox:ContainerName` прод-конфига,
  сервер поднимет его заново из свежего образа). Перед снимком `switch-release.sh`
  делает **бэкап данных** (`data-backup`, `dotnet ClaudeHomeServer.dll --backup`,
  таймаут `BACKUP_TIMEOUT`, провал = отказ без изменений) и **записывает снимок в
  список `releases`** журнала.
- `deploy_rollback` на Linux **отвечает отказом** (`Unsupported`) — автооткат
  уже встроен в `switch-release.sh`, ручной кейс через `ccs-deploy-<id>` по
  явной метке делается скриптом, а не из чата. См. ADR-010 § «Отказ отката».
  Список `releases` теперь **заполняется** (снимки пишутся после `snapshot` и
  чистятся при ротации), так что `deploy_status` видит цели отката; сам откат
  из чата на Linux пока не поднят — доложить, не изобретать.
- Ручная выкатка (Bash + `watch_start`) остаётся на случай отказа контура —
  [release-linux.md](release-linux.md) не перестаёт работать, потому что
  orchestrator и `switch-release.sh` разделены.

## Когда лучше НЕ включать

- Машина, на которой ещё ни разу не было штатной выкатки: новый код,
  включающий сам `DeployHost.WakeAgentAsync` и `BuildLinuxWakeSpec`, на ней
  ещё не доехал. Сначала ручная выкатка по [release-linux.md](release-linux.md),
  потом секция `Deploy`.
- Прод с несколькими владельцами и разными `AgentDir` — контур поднимает
  ровно один процесс на одного владельца, второй просто не помешает, но
  следить за путями и локами придётся руками.
- Машина без `systemd-run --user` (нет шины): `systemd-run` сам провалится на
  `WakeTimeoutMs=15s`, сервер ответит `LaunchFailed` с текстом отказа —
  контур не поднимется. Диагностика — та же команда из shell владельца.

## Проверки после включения

1. `journalctl --user -u ccs-deploy-<id>` — поднялся ли юнит, какие фазы
   прошёл, на каком шаге остановился.
2. `tail -n 50 $CCS_RELEASES_DIR/deploy-<id>.log` — текстовый лог `switch-release.sh`,
   включает health-попытки и итоговую строку `=== ГОТОВО ===` или
   `=== ОТКАЧЕНО ===`. По умолчанию `CCS_RELEASES_DIR=/opt/ccs/releases`, то есть
   фактически путь `/opt/ccs/releases/deploy-<id>.log`; путь передаётся в скрипт
   переменной `CCS_RELEASES_DIR` из секции `Deploy.ReleasesDir`.
3. `cat /opt/ccs/releases/deploy-state.json | python3 -m json.tool` — фазы
   и `result.releaseId` при откате.
4. `curl -fs -o /dev/null -w 'http=%{http_code} build=%header{X-Build}\n'
   -H 'Host: localhost' http://127.0.0.1:80/api/health` — заголовок
   `X-Build` отдаёт первую строку `build-id.txt` (`BuildIdProvider`);
   обновляется вместе с публикацией.

## Связанное

- [ADR-010 раздел «Контур Linux»](../adr/ADR-010-deploy-from-chat.md) — контракт
  побудки, лок, формат шагов, инцидент 28.09.
- [release-linux.md](release-linux.md) — ручная выкатка + серверный сторож
  `watch_start`, через который проходит любая публикация, пока не включён
  контур.
- [scripts/ops/README.md](../../scripts/ops/README.md), раздел «Linux: выкатка
  через `deploy_start`» — детали оркестратора, `deploy-journal.py`,
  тест-прогон на песочнице.
- [sandbox.md](../architecture/sandbox.md) — cgroup-потолок
  `ccs-agents.slice`, почему оркестратор снаружи.
