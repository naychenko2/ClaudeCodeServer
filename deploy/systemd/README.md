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
| `ccs-agents.slice.d/50-oom-preference.conf` | `ManagedOOMMemoryPressure=kill` + `ManagedOOMMemoryPressureLimit=80%` — предохранитель на PSI самого slice (порог 80 % вместо 60 %, инцидент 15.10). |
| `system/user@.service.d/50-ccs-oomd.conf` | **root.** Сессионный порог oomd 50 % → 80 %. |
| `../sysctl.d/60-inotify.conf` | **root.** `max_user_watches=1048576`, `max_user_instances=8192`. |
| `install-user-units.sh` | Раскладка user-части в `~/.config/systemd/user/` и очистка `user.control/`. |
| `install-system-tuning.sh` | Раскладка root-части (`/etc/systemd/system/user@.service.d/`, `/etc/sysctl.d/`). |
| `ccs-gddr6.service` | **root.** Помощник температуры памяти GDDR6X и hotspot видеокарт — см. раздел «ccs-gddr6» ниже; ставится `../gddr6-helper/install.sh`. |

`ccs.slice` и `ccs-agents.slice` как файлы отсутствуют: они создаются на лету
при первом `systemd-run --slice=ccs-agents.slice`, а свойства держатся в
drop-in'ах. `ccs.service` живёт в `app.slice` (дефолт user-юнитов), то есть
scope агентов и бэкенд — сиблинги под `user@<uid>.service`, а не вложены.

Третий рычаг лежит не здесь, а в репозитории: `backend/Directory.Build.rsp`
(`-maxcpucount:6`) режет число узлов MSBuild для любой сборки под `backend/`,
включая ту, что агент запускает своим Bash внутри хода.

## Порядок остановки scope ↔ сервис

При **выключении машины** менеджер пользователя systemd останавливает `ccs.service`
и scope-юниты CLI (`ccs-run-*.scope`) одной транзакцией **без порядка**: CLI раньше
сервера получал SIGTERM, успевал прислать `result{subtype:error}` и чат уходил в
`Error`, а не оставался `Working` для авто-продолжения (инцидент 20.10 15:33 — ни один
чат не продолжился). При `systemctl --user restart ccs.service` порядок не важен:
остановку ведёт сам сервер (`KillAllProcesses` → `_stopping`), и CLI умирает от руки
сервера, а не от systemd.

Лечение — свойство `Before=` на scope: бэкенд определяет юнит сервера один раз при
старте по `/proc/self/cgroup` (последний сегмент, если `*.service`) и ставит
`systemd-run … --property=Before=<юнит>` (ключ `Execution:Isolation:StopAfterUnit`,
автоопределение, переопределение в конфиге; имя `ccs.service` в код не зашито).
Тогда scope гасится **ПОСЛЕ** `ccs.service`, и путь сводится к штатной остановке.
`Before=` — только порядок: членство в cgroup, OOM-лимиты slice, `KillMode` scope и
`ScopeOrphanSweeper` не меняются. Dev-стенд из `dotnet run` и Windows — без свойства.

**Container-владельцы при перезагрузке машины пока не покрыты**: CLI живёт в
`cc-sandbox`, а контейнер гасит системный `docker.service`, чей порядок относительно
`user@<uid>.service` не задан — docker может погасить ход раньше нашего сервера.
`After=docker.service`, уже стоящий в user-`ccs.service`, висячий: менеджер
пользователя системных юнитов не видит, и сегодня он не работает. Лечится
системным drop-in `user@.service.d/` с `After=docker.service` (отдельный шаг;
файл кладётся рядом с `system/user@.service.d/50-ccs-oomd.conf`).

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
2. **oomd на самом slice**, порог 80 % PSI за 20 с (длительность общая из
   `oomd.conf`): настоящий thrash у потолка → убить самый давящий scope целиком.
   Дефолтные 60 % подняты после инцидента 15.10: reclaim файлового кэша у
   `MemoryMax` давал PSI 63,7 % без настоящего thrash, и oomd убил ход.
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

## ccs-gddr6: температура памяти и hotspot видеокарт (root-помощник)

Драйвер и NVML не отдают температуру памяти GDDR6X и hotspot (`N/A`), а в панели
«Локальные модели → Железо» они нужны. Значения читаются прямо из регистров
карты через `/dev/mem`, а это требует root (`CAP_SYS_RAWIO`); бэкенд работает
от обычного пользователя. Поэтому — маленькая отдельная служба:

- `ccs-gddr6-helper.py` (Python 3, только stdlib, запуск `python3 -I`) раз в 2 с
  читает два 32-битных регистра у каждой RTX 3090 (vendor `0x10de`, device
  `0x2204` — жёсткий список в константе) и атомарно пишет
  `/run/ccs-gddr6/temps.json` (`0644`):
  `{"v":1,"at":<unix мс>,"gpus":[{"busId","deviceId","memTempC","hotspotTempC"}]}`.
  Значение вне 1…150 °C — `null`; сбой одной карты не влияет на остальные.
  Метка `at` — признак свежести: устарела (служба стоит/зависла) — данных нет.
- BAR0 берётся из первой строки `resource` в `/sys/bus/pci/devices/<bus>/`;
  регистры: память — `BAR0+0xE2A8`, `(raw & 0xfff) // 0x20`; hotspot —
  `BAR0+0x2046C`, `(raw >> 8) & 0xff`. Страница отображается `PROT_READ`.
- Служба **только читает** (`/sys/bus/pci/devices` и `/dev/mem`): ни записей в
  устройства, ни сокетов, ни запуска процессов. Unit жёсткий: единственная
  привилегия `CAP_SYS_RAWIO`, `PrivateNetwork`, `ProtectSystem=strict`,
  `DevicePolicy=closed` + `DeviceAllow=/dev/mem r`, фильтр системных вызовов.
  `systemd-analyze security ccs-gddr6` → **1.8 OK**.

Почему root, а не группа `kmem`/`setcap`: чтение `/dev/mem` и так равно
привилегии RAWIO, а отдельная узкая служба с минимальным набором прав и файлом на
выходе безопаснее, чем давать эту возможность процессу бэкенда.

**Скрипт службы исполняется от root, поэтому в `/usr/local/lib/ccs-gddr6/` он
принадлежит root и не записываем для остальных** — иначе это путь повышения
прав. Из репозитория (где файл принадлежит `an`) служба его не запускает.

```bash
sudo ./deploy/gddr6-helper/install.sh             # установка / обновление (идемпотентно)
sudo ./deploy/gddr6-helper/install.sh --dry-run   # показать шаги
sudo ./deploy/gddr6-helper/install.sh --remove    # удалить службу и скрипт
```

Проверка:

```bash
systemctl status ccs-gddr6
cat /run/ccs-gddr6/temps.json                 # «at» меняется каждые ~2 с
systemd-analyze security ccs-gddr6
python3 -I deploy/gddr6-helper/test_ccs_gddr6_helper.py   # unittest, без железа
```

Нужны `STRICT_DEVMEM=y` без `IO_STRICT_DEVMEM` и `lockdown=none` (как на этой
машине); перезагрузка и `iomem=relaxed` не требуются. Если ядро запретит
отображение BAR, служба останется живой и отдаст `null`.
