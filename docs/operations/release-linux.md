# Выкатка на Linux-прод

Прод крутится на отдельной Linux-машине, бэкенд — systemd-юнит `ccs.service`,
публикация — в `/opt/ccs/app`. Процедура отличается от Windows-прода (там
`deploy-agent.ps1` через Task Scheduler; ADR-010); здесь — bash-скрипты в
`scripts/ops/` плюс парные им `/opt/ccs/switch-release.sh` (источник —
[`scripts/ops/switch-release.sh`](../../scripts/ops/switch-release.sh)) и
`/opt/ccs/check-release.sh` на самой машине.

> **Прод не перезапускать руками без `watch_start`** — даже если деплой
> «вроде прошёл», без серверного сторожа нет способа узнать, что
> `build-id.txt` уже содержит новый sha и health зелёный. Подробности и
> причины — в разделе [«Зачем сторож»](#зачем-сторож-watch_start) ниже.

## Артефакты на проде

| Путь | Что | Пишет |
|---|---|---|
| `/opt/ccs/app/` | рабочая папка бэкенда, обновляется `publish-linux.sh` | rsync из staging |
| `/opt/ccs/app/build-id.txt` | маркер сборки: `timestamp`, `sha=<sha8>`, `ref=`, `dirty=`, `builtAt=` | `publish-linux.sh:95-96` |
| `/opt/ccs/staging/` | временная папка сборки, удаляется после rsync | `publish-linux.sh` |
| `/opt/ccs/releases/<timestamp>/` | снимок предыдущего билда целиком (≈170 МБ); хранятся 5 последних | `switch-release.sh` (ротация — он же) |
| `/opt/ccs/switch-release.sh` | swap + systemd restart + health-гейт + автооткат + ротация снимков | копия `scripts/ops/switch-release.sh`, ставится по разделу ниже; на машине руками не правится |
| `/opt/ccs/check-release.sh` | проверка «прод на сборке X»: `exit 0` если `build-id.txt` содержит нужный sha8 и health 2xx | парный к `switch-release.sh` |
| `/opt/ccs/deploy-<timestamp>.log` | лог выкатки (фазы, health-попытки, автооткат при провале) | `switch-release.sh` |

Заголовок `X-Build` у `GET /api/health` отдаёт первую строку `build-id.txt`
([`BuildIdProvider`](../../backend/ClaudeHomeServer.Deploy/Services/Deploy/BuildIdProvider.cs));
`build-id.txt` снимается в снимок вместе с бинарниками, поэтому после
отката заголовок сам становится прежним.

## Процедура

Прод-выкатку запускает **человек** — сервер ClaudeCodeServer себя не деплоит
(это граница привилегий). Чат выкатки держит свой сценарий: пишет заявку в
журнал `data/deploy-*.json` через `deploy_start` (signal deploy), агент
`switch-release.sh` подхватывает её под systemd-scope `ccs-agents.slice`
(изоляция per-user, ADR-014). Чат при этом **живёт под бэкендом и умрёт на
шаге `stop` ccs.service** — поэтому будильник сторожа летит в чат-планировщик
выкатки, а постановщик (оператор-человек) смотрит результат в трее/логах.

1. **Чистый detached worktree** на нужный коммит:
   `~/Sources/ClaudeCodeServer-relcheck` → `git checkout --detach <sha>`.

2. **Сборка в staging** (BUILD_FRONTEND=1 — фронт; BUILD_DIFY=1 — mcp-dify):
   ```bash
   BUILD_FRONTEND=1 PUBLISH_DIR=/opt/ccs/staging scripts/ops/publish-linux.sh
   ```
   Лог — в `/opt/ccs/publish-<ts>.log`. Проверить, что в staging есть
   `modules/audio-editor/ClaudeHomeServer.AudioEditor.dll` (если модуль ещё
   не на проде), `modules/notes/`, `modules/spend/`,
   `modules/image-editor/` — без них старт падает (см. скрипт).

3. **До `switch`** поставить серверного сторожа (см. раздел ниже). Сторож
   переживает смерть CLI-процесса и рестарт бэкенда (ADR-013); обычный
   `Monitor`/`run_in_background` умирает вместе с ходом чата и тут не
   работает.

4. **Запустить `switch-release.sh`** — сам уходит в `systemd-run`,
   сохраняет снимок текущего бинарника, стопит `ccs.service`, копирует
   staging поверх `/opt/ccs/app/`, поднимает сервис, гоняет health-гейт.
   При провале гейта — автооткат на снимок и рестарт.

5. **После будильника сторожа** — прод на новой сборке, health зелёный
   (204 + `X-Build` = свежий `build-id.txt`). Лог выкатки в
   `/opt/ccs/deploy-<ts>.log` фиксирует фазы и финальный результат.

## Ротация снимков и установка скрипта

Источник правды `switch-release.sh` — репозиторий (`scripts/ops/`); `/opt/ccs/switch-release.sh`
— его установленная копия. Правка скрипта = коммит в репозитории, затем на хосте:

```bash
install -m 755 scripts/ops/switch-release.sh /opt/ccs/switch-release.sh
bash -n /opt/ccs/switch-release.sh
```

(Не во время выкатки: bash читает скрипт по мере исполнения.)

**Ротация.** После **успешного** health-гейта скрипт оставляет в `/opt/ccs/releases`
`RELEASES_KEEP` (по умолчанию **5**) самых свежих снимков, остальные удаляет:

- порядок — по имени каталога (метка `YYYYMMDD-HHMMSS`), а не по mtime: `cp -a` переносит
  mtime папки приложения, дата каталога снимка не равна моменту снимка;
- удаляются только каталоги со строгим именем-меткой прямо внутри `releases/`; симлинки и
  посторонние имена не трогаются;
- свежий снимок этой выкатки (откатная точка) защищён явно и входит в 5 всегда; при
  невозможном значении (`0`, не число) берётся 5;
- при провале health (автооткат) и при отказе до остановки ротация **не запускается**;
  ошибка удаления выкатку не валит;
- число меняется правкой константы `RELEASES_KEEP` в начале скрипта: окружение вызывающего в
  `systemd-run`-юнит не попадает.

Разовый запуск без выкатки (в т. ч. для проверки):

```bash
/opt/ccs/switch-release.sh --rotate --dry-run   # показать, что удалилось бы
/opt/ccs/switch-release.sh --rotate             # выполнить
CCS_RELEASES_DIR=/tmp/x CCS_RELEASES_KEEP=3 /opt/ccs/switch-release.sh --rotate  # проверка на другом каталоге
```

## Зачем сторож (`watch_start`)

Выкатка длится секунды (stop → rsync → start → 18 health-попыток за 90 с),
но **чат планировщика выкатки умирает на шаге `stop` ccs.service**
(процесс CLI закрывается вместе с бэкендом — он же запускал его). В этот
момент человек ещё не знает, дошёл ли `publish-linux.sh` до swap. Monitor
и `run_in_background` харнесса CLI тоже мертвы.

Серверный сторож (ADR-013) исполняет `poll_command` **в бэкенде**, а не в
процессе CLI — переживает stop, рестарт и смену хода. Условие — `exit 0`
«дождались», `exit != 0` — «ещё нет». Тик каждые 5 с, poll каждый
`interval_seconds` (30–600). Если `TimeoutMinutes` истёк — терминал
`timed_out`, будильник «не дождались».

### Готовый `poll_command` для сторожа

```bash
/opt/ccs/check-release.sh 4efd0103
```

Где `4efd0103` — `git rev-parse --short=8 HEAD` целевого коммита
(`sha=<value>` в `build-id.txt` именно в 8-символьном виде).

Скрипт `/opt/ccs/check-release.sh`:

- `grep -qF "sha=$SHA8" /opt/ccs/app/build-id.txt` — sha в маркере (используем
  `grep -F` и якорь `sha=`, чтобы случайные подстроки не дали ложный
  fired; в `build-id.txt` строка начинается с timestamp — без `sha=` могли бы
  пересечься префиксы);
- `curl -fs -o /dev/null -H 'Host: localhost' http://127.0.0.1:80/api/health`
  — health 2xx. `-f` (`--fail`) гасит 4xx/5xx с exit 22 (наш код 2), `-s`
  глушит прогресс, `-o /dev/null` оставляет только exit code. **Порт `:80`**,
  не `:5000` — прод слушает только на `:80`/`:443` (`:5000` — дев-стенд из
  docker-compose).

Exit codes скрипта:

| Код | Смысл |
|---|---|
| 0 | прод на заданной сборке, health 2xx — сторож переходит в `fired` |
| 1 | sha не совпал (сборка ещё не доехала или откатилась) |
| 2 | health не 2xx (curl вернул 22 или refused) |
| 3 | usage / файл build-id.txt не читается |

### Что НЕ использовать в `poll_command`

Эти ошибки поймал сторож выкатки 01.10 (`prod-switch-4efd0103b`,
карточка `b6656c31-ef11-460c-8b17-0ebf87adb61f`) — простоял 15 минут по
таймауту, условие формально не сработало; выкатка прошла штатно по логу и
прямому `curl health`, но без автоматического будильника:

1. **Полный SHA вместо 8-символьного.** `grep -q "4efd0103b"` (9 chars) не
   нашёл `sha=4efd0103` (8 chars) в `build-id.txt`. Используйте
   `--short=8` и **сверяйте** с содержимым `build-id.txt` перед постановкой
   сторожа.
2. **Порт `:5000`.** Прод слушает на `:80` и `:443`, не `:5000` (последний —
   дев-стенд из `docker-compose.claude.yml`). `curl -f 127.0.0.1:5000/...`
   всегда возвращает exit 7 (connection refused), команда целиком —
   non-zero, сторож не сработает.
3. **`-w '%{http_code}'` без `-f`.** Код `000` (curl error) трактуется
   как «нет ответа» — но `-w '%{http_code}'` пишет `000` и **exit 0** при
   любых ошибках транспорта; `grep -q ' 200$'` всё равно отдаст
   non-zero, но grep по подстроке не отличает «204 от нужного sha» от
   «0 и пустой ответ от другого порта». С `-fs` 4xx/5xx/transport дают
   exit ≠ 0 и сторож не сработает на ложном ответе.
4. **Голый `curl ... | grep 2xx` без `-f`.** Та же ловушка: `200 0` в
   stderr/health-ответе удовлетворит grep и даст ложный fired при
   полностью мёртвом бэкенде (отвечает nginx-кеш или старый инстанс).

Через скрипт `check-release.sh` эти грабли закрыты разом — менять только
сам скрипт, если поменяется путь к `build-id.txt`, порт health или формат
маркера.

### Параметры сторожа для выкатки

| Параметр | Значение | Почему |
|---|---|---|
| `poll_command` | `/opt/ccs/check-release.sh <sha8>` | см. выше |
| `interval_seconds` | 30 | дефолт; хватает при выкатке < 90 с |
| `timeout_minutes` | 15 | выкатка обычно < 2 мин; 15 мин — потолок для срабатывания авто-отката |
| `name` | `prod-switch-<sha8>` | для опознания в `watch_list` |

## Проверка результата

```bash
# sha на проде
head -2 /opt/ccs/app/build-id.txt
# health + заголовок X-Build
curl -fs -o /dev/null -w 'http=%{http_code} build=%header{X-Build}\n' \
     -H 'Host: localhost' http://127.0.0.1:80/api/health
# статус службы
systemctl status ccs.service --no-pager
# последний лог выкатки
ls -lt /opt/ccs/deploy-*.log | head -1
```

Что должно быть после штатной выкатки:

- `head -1 build-id.txt` — свежий timestamp вида `20261001-232053`.
- `curl` — `http=204 build=20261001-232053` (X-Build совпадает с первой
  строкой `build-id.txt`).
- `ccs.service` — `active (running)`, PID стабилен.
- В логе — `=== ГОТОВО: <ts> sha=<sha8> ===`, без `ОТКАЧЕНО`.

## Связанное

- [`scripts/ops/publish-linux.sh`](../../scripts/ops/publish-linux.sh) —
  сборка и публикация в staging (`/opt/ccs/staging/`), затем rsync в
  `/opt/ccs/app/`.
- [ADR-010](../adr/ADR-010-deploy-from-chat.md) — общая граница: сервер не
  деплоит себя, деплой — отдельный агент.
- [ADR-013](../adr/ADR-013-server-chat-watchdogs.md) — серверные сторожа
  чатов: цикл, оболочка по платформе, `ExitCode == 0` = fired.
- [`scripts/ops/README.md`](../../scripts/ops/README.md) — Windows-выкатка
  (`deploy-agent.ps1`), там же описание `build-id.txt` и `X-Build`.