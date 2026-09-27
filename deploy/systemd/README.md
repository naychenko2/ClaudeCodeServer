# deploy/systemd — обвязка ccs: user-юниты и системные настройки машины

Unit-файлы, drop-in'ы и sysctl для машины, на которой работает ClaudeHomeServer
и его агенты. Хранятся в git, чтобы:

- переживать `daemon-reload` (в отличие от `systemctl --user set-property`,
  который пишет в `user.control/`);
- попадать в ревью при изменении;
- воспроизводиться при развёртывании на новой машине;
- иметь единый источник правды для бэкенда (`ccs.service`), его cgroup-контура
  (`ccs-agents.slice`) и системных порогов, от которых зависит их выживание.

## Состав

| Файл | Назначение |
|---|---|
| `ccs.service` | Основной unit-файл бэкенда (dotnet, Kestrel, `/opt/ccs/app`). |
| `ccs.service.d/50-oom-preference.conf` | `ManagedOOMPreference=omit` — oomd не выбирает бэкенд жертвой. |
| `ccs-agents.slice.d/50-memory-limits.conf` | `MemoryMax=18G`, `MemorySwapMax=0`. **Без `MemoryHigh`** — см. ниже. |
| `ccs-agents.slice.d/50-oom-preference.conf` | `ManagedOOMMemoryPressure=kill` — предохранитель на PSI самого slice. |
| `system/user@.service.d/50-ccs-oomd.conf` | **root.** Сессионный порог oomd 50 % → 80 %. |
| `../sysctl.d/60-inotify.conf` | **root.** `max_user_watches=1048576`, `max_user_instances=8192`. |
| `install-user-units.sh` | Раскладка user-части в `~/.config/systemd/user/` и очистка `user.control/`. |
| `install-system-tuning.sh` | Раскладка root-части (`/etc/systemd/system/user@.service.d/`, `/etc/sysctl.d/`). |

`ccs.slice` и `ccs-agents.slice` как файлы отсутствуют: они создаются на лету
при первом `systemd-run --slice=ccs-agents.slice`, а свойства держатся в
drop-in'ах. `ccs.service` живёт в `app.slice` (дефолт user-юнитов), то есть
scope агентов и бэкенд — сиблинги под `user@<uid>.service`, а не вложены.

Третий рычаг лежит не здесь, а в репозитории: `backend/Directory.Build.rsp`
(`-maxcpucount:6`) режет число узлов MSBuild для любой сборки под `backend/`,
включая ту, что агент запускает своим Bash внутри хода.

## Применение

```bash
./deploy/systemd/install-user-units.sh             # user-часть + daemon-reload
./deploy/systemd/install-user-units.sh --restart   # то же + restart ccs.service
sudo ./deploy/systemd/install-system-tuning.sh     # root-часть: oomd, inotify
./deploy/systemd/install-*.sh --dry-run            # показать, что будет сделано
```

Требования к user-части: user-шина systemd доступна (запуск из сессии
пользователя, под которым крутится ccs.service), не root, включён linger
(`loginctl enable-linger`) — `ccs.service` висит на `default.target` и живёт
без входа в стол. Root-часть —
наоборот, только под `sudo`; она ставит значение и уже работающему
`user@<uid>.service` через `set-property --runtime` (иначе — до перелогина).

Оба скрипта идемпотентны. User-скрипт чистит только те `user.control/`-файлы,
которые этот набор явно заменяет.

Per-scope пределы (`Execution:Isolation:MemoryHigh/MemoryMax` в
`appsettings.Local.json`) читаются бэкендом один раз на старте — их смена
требует рестарта `ccs.service`. Лимиты slice применяются на лету.

## Как устроена защита — три рубежа

1. **Ядро, `MemoryMax`** на `ccs-agents.slice` и per-scope. При достижении
   потолка ядро убивает самый большой процесс внутри cgroup (обычно `testhost`
   или узел MSBuild). Сборка в ходе падает с понятной ошибкой, сам ход и соседние
   scope живут. `MemorySwapMax=0` — память агентов не уходит в подкачку: swap-out
   на 4 GB (пики scope 21.09) даёт минуты рефолтов вместо секунды OOM.
2. **oomd на самом slice**, порог по умолчанию 60 % PSI за 20 с: настоящий
   thrash у потолка → убить самый давящий scope целиком.
3. **oomd на сессии** `user@<uid>.service`, порог 80 % (дефолт Ubuntu — 50 %):
   защита рабочего стола от собственных утечек; при таком стойле сессия и так
   неработоспособна.

### Почему нет `MemoryHigh`

Разбор 2026-09-22. Установка `MemoryHigh` 21.09 (12G per-scope, 18G на slice)
не остановила убийства: за вечер oomd убил ещё четыре scope. Механизм: дроссель
`MemoryHigh` гонит процессы в reclaim и swap; стойло в reclaim считается
PSI-давлением; PSI суммируется вверх по иерархии до `user@<uid>.service`, где
oomd с дефолтом Ubuntu (kill при 50 %) выбирает жертвой самый «давящий» потомок —
наш scope. Цифры: `memory.events high` на slice за три дня — 1 901 740; из 247 с
memory-стойла всей сессии 184 с дал slice агентов; 7 из 9 убийств триггерились
давлением на сессию, не на slice. Дроссель был не подушкой, а источником сигнала
для убийцы. `MemoryHigh` — это лимит для сред без oomd; под oomd ставить его нельзя.

## Как считать `MemoryMax` под свою машину

Значение **МАШИННО-СПЕЦИФИЧНОЕ**. В git — образец с правилом.

1. Общий объём RAM: `free -h | awk '/Mem:/ {print $2}'`.
2. Вычесть всё, что живёт ВНЕ `ccs-agents.slice` и может съесть память
   (qemu, vLLM, другие сервисы) — `systemd-cgtop` покажет правду, брать пики.
3. Вычесть бэкенд (`ccs.service`, 1–3 GB), рабочий стол (`systemd-cgtop` на
   `user@<uid>.service` минус `ccs.slice`) и системные нужды (5–10 % RAM).
4. Остаток — потолок для агентов, с запасом на рост соседей вниз, а не вверх:
   лучше OOM одного testhost внутри scope, чем глобальный reclaim и oomd.

Пример с этой машины (60 GB RAM):

| Потребитель | Объём |
|---|---|
| qemu (виртуалка Windows) | ~16 GB |
| vLLM (локальная модель) | ~11 GB |
| транскрипция + эмбеддинги | ~3 GB |
| рабочий стол | ~5 GB |
| ccs.service (бэкенд) | ~2 GB |
| система + кэш | ~3 GB |
| **итого вне агентов** | ~40 GB |
| **остаток на ccs-agents.slice** | ~20 GB |
| `MemoryMax` | **18G** |

Per-scope `MemoryMax` (16G) заведомо больше половины slice: два тяжёлых scope
разом упрутся в потолок slice раньше, чем в свой, — это ожидаемо, важен потолок
slice. Сколько параллельных сборок реально бывает — по `journalctl --user
_PID=<pid user-systemd> | grep 'memory peak'`.

## Что делать при инциденте OOM

1. Кто кого убил: `journalctl -o short-iso | grep 'systemd-oomd.*Killed'` (в
   строке — cgroup, ПО ДАВЛЕНИЮ НА КОТОРЫЙ убили: сессия или slice) и
   `journalctl -k | grep -i oom` (ядро по `MemoryMax`).
2. Счётчики slice: `cat /sys/fs/cgroup/user.slice/user-<uid>.slice/user@<uid>.service/ccs.slice/ccs-agents.slice/memory.events`
   — `high` не ноль означает, что кто-то вернул `MemoryHigh`; `max` — сколько раз
   упирались в потолок.
3. Пики scope: `journalctl --user _PID=<pid user-systemd> | grep 'ccs-run.*memory peak'`.
4. Правка в `deploy/systemd/…` → `install-user-units.sh` / `sudo install-system-tuning.sh`.
