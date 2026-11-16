# ccs-node-stats — агент узла фермы

Read-only агент для узлов `ai` и `video`: отдаёт **сырой срез** датчиков узла по HTTP. Ничего не толкует — уровни, пороги и история остаются в CCS (бэкенд на worker).
Контракт среза v1 — [ADR-029, раздел «Узлы фермы»](../../docs/adr/ADR-029-local-model-stats.md): поле, которого там нет, агент не отдаёт.

| Файл | Что |
|---|---|
| `ccs_node_stats.py` | сам агент, python3 и только стандартная библиотека |
| `ccs-node-stats.service` | unit systemd (жёсткость как у `ccs-gddr6`, но без привилегий и с сетью) |
| `tests/test_ccs_node_stats.py` | тесты без железа: `/sys`, `/proc` — во временной папке, `nvidia-smi` — фейк |

## Как работает

- Фоновый поток раз в 2 с собирает срез; `GET /v1/slice` отдаёт последний готовый и пересчитывает `ageMs` (монотонные часы). Зависший `nvidia-smi` обрывается через 1 с (`gpuCsv: null`); если встала вся сборка — растёт `ageMs`, а не копятся запросы. Пока первого среза нет — `503`.
- Сервер однопоточный (`HTTPServer`): запросы идут строго по очереди. Медленный клиент отпускается через 5 с.
- Другой путь — `404`, другой метод — `405`, адрес вне `allow` — `403` без тела (проверяется раньше пути).
- Агент читает только перечисленное в ADR (hwmon, `/proc/{meminfo,uptime,stat,self/mountinfo}`, `/run/ccs-gddr6/temps.json`, `/run/ccs-ipmi/sdr.json`) и запускает единственную команду `nvidia-smi --query-gpu=… --format=csv,noheader,nounits` без шелла. Процессов (`--query-compute-apps`) он не видит.

- Необязательное поле среза `ipmi` — текст `/run/ccs-ipmi/sdr.json` (помощник `ccs-ipmi`) дословно, как `gddr6`: агент его не разбирает и JSON не проверяет. Нет файла или больше 16 КБ — `null`. `v` остаётся 1 (новое необязательное поле); на узлах без помощника (ai, video) — `ipmi: null`.

## Установка (шаг У1, на узле ai или video)

Нужны `python3` и, для карт, `nvidia-smi`. От root:

```bash
install -d -o root -g root -m 0755 /usr/local/lib/ccs-node-stats
install -o root -g root -m 0644 deploy/node-stats/ccs_node_stats.py /usr/local/lib/ccs-node-stats/
install -o root -g root -m 0644 deploy/node-stats/ccs-node-stats.service /etc/systemd/system/
```

Конфиг `/etc/ccs-node-stats.json` (один LAN-адрес узла — `0.0.0.0` агент не принимает; `allow` — адреса worker):

```json
{ "listen": "10.0.0.11:9105", "allow": ["10.0.0.5"] }
```

В unit замени `IPAddressAllow=10.0.0.5` на адрес worker (он же в `allow` и в правиле firewall — три места должны совпадать), затем:

```bash
systemctl daemon-reload
systemctl enable --now ccs-node-stats
```

Группы устройств NVIDIA в unit (`DeviceAllow=char-nvidia-frontend` и др.) сверь с `grep nvidia /proc/devices`.

## Проверка

```bash
# с worker (адрес из allow):
curl -s http://10.0.0.11:9105/v1/slice | python3 -m json.tool | head -40
# ожидаем: v=1, ageMs < 3000, files с /sys/class/hwmon/…, gpuCsv со строками карт (или null без драйвера)

# с чужого адреса — 403, на другой путь — 404:
curl -si http://10.0.0.11:9105/v1/slice | head -1
journalctl -u ccs-node-stats -n 50
```

Если `gpuCsv: null` при живом драйвере — смотри `journalctl` и по очереди ослабляй `SystemCallFilter` и `DeviceAllow`: `MemoryDenyWriteExecute` и `~@resources` в unit сознательно не стоят, остальную жёсткость на живом узле ещё не обкатывали (проверяется на шаге У1).

## Узел video под WSL

В WSL нет `/sys/class/hwmon` с датчиками хоста, поэтому `files` и `links` содержат только `/proc/*`, а список датчиков пуст — **это норма**, не поломка. Карты видны через `nvidia-smi` (`gpuCsv`). Нужен включённый systemd в WSL: в `/etc/wsl.conf` секция `[boot]` с `systemd=true`, затем `wsl --shutdown` и запуск заново. Адрес для `listen` — адрес интерфейса WSL, доступный с worker (при NAT-сети потребуется проброс порта на хосте Windows — отдельное решение шага С1).

## Где стоит

Установлено 2026-11-16 (шаг У1), версия среза `v=1`, worker — `192.168.7.117`. Код агента на узлах побайтно равен `ccs_node_stats.py` из ветки (сверено `sha256sum`).

| Узел | Адрес и порт | Среда | Что отдаёт |
|---|---|---|---|
| ai | `192.168.7.83:9105` | Linux, systemd 259, Python 3.14 | hwmon (asus-ec, k10temp, nct6798, nvme), `/proc`, `statvfs`, `gpuCsv` — 2× RTX 3090, `gddr6` из `/run/ccs-gddr6/temps.json` |
| video | `192.168.7.208:9105` | Windows 11 + WSL2 Ubuntu-24.04 (mirrored, systemd), Python 3.12 | только `/proc/{meminfo,stat,uptime,self/mountinfo}` (4 файла), `statvfs`, `gpuCsv` — 1× RTX 3090; `gddr6: null` |

У обоих узлов: `/etc/ccs-node-stats.json` = `{ "listen": "<адрес узла>:9105", "allow": ["192.168.7.117"] }`, в unit `IPAddressAllow=192.168.7.117` (три места совпадают с адресом worker), `systemctl enable` стоит.

### Особенности узла video

- **hwmon пуст, датчиков платы и GDDR6 нет — ожидаемо, не дефект:** в WSL нет `/sys/class/hwmon` хоста, а помощник `ccs-gddr6` не переносится (нет `/dev/mem` карты). Температура ядра, мощность, VRAM и утилизация карты приходят из `gpuCsv`.
- **Два отличия от unit по умолчанию, оформлены drop-in'ом `/etc/systemd/system/ccs-node-stats.service.d/wsl.conf`** (репозиторный unit не менялся):
  ```ini
  [Service]
  Environment=PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/usr/lib/wsl/lib
  DeviceAllow=/dev/dxg rw
  ```
  Первая строка с версии Ш2а-доработки не обязательна: агент сам ищет `nvidia-smi` в `/usr/lib/wsl/lib` после PATH (на уже настроенных узлах она безвредна); без второй `DevicePolicy=closed` закрывает `/dev/dxg`, через который WSL видит карту. Драйверных устройств `nvidia*` в `/proc/devices` WSL нет, `DeviceAllow=char-nvidia*` из unit там ничего не делают.
- **Проброс порта не нужен:** WSL в режиме `networkingMode=mirrored` делит LAN-адрес с Windows (`192.168.7.208`), порт из WSL виден в сети сразу.
- **Брандмауэр Windows — правку пришлось сделать:** без неё `curl` с worker уходил в тайм-аут. По образцу правил «ComfyUI 8188 from ai» / «Bonsai 18030-18031 from ai» заведены **два** правила (в `.wslconfig` стоит `firewall=true`, поэтому нужно и обычное, и Hyper-V правило для WSL), TCP 9105, источник только `192.168.7.117`:
  ```powershell
  New-NetFirewallRule -DisplayName 'CCS node-stats 9105 from worker' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 9105 -RemoteAddress 192.168.7.117 -Profile Any
  New-NetFirewallHyperVRule -Name 'CCS-node-stats-9105-from-worker' -DisplayName 'CCS node-stats 9105 from worker' -Direction Inbound -Action Allow -Protocol TCP -LocalPorts 9105 -RemoteAddresses 192.168.7.117 -VMCreatorId '{40E0AC32-46A5-438A-A0B2-2B479E8F2E90}'
  ```
  Откат — `Remove-NetFirewallRule` / `Remove-NetFirewallHyperVRule` по тем же именам.
- **Автозапуск после перезагрузки Windows:** WSL поднимает задача планировщика `WSL-Keepalive-Ubuntu` (триггер входа в систему, `wsl.exe -d Ubuntu-24.04 --exec /bin/sleep infinity`); systemd внутри WSL стартует все `enabled`-юниты, включая `ccs-node-stats`. Ничего отдельного не требуется.
- На ai брандмауэра нет (`ufw` неактивен, `iptables` INPUT ACCEPT) — правок не потребовалось; защита — `IPAddressDeny=any` в unit и `allow` агента.

### Проверка установки (2026-11-16)

- С worker оба узла отдают срез: `v=1`, `ageMs` 0,6–2 с; ai — 98 файлов, 2 карты, `gddr6` с температурой памяти и hotspot; video — 4 файла, 1 карта.
- Чужой адрес получает отказ: с video (`.208`) к ai и с ai (`.83`) к video и к самому себе — тайм-аут без ответа (пакеты отбрасывает `IPAddressDeny` unit'а на ai и правило брандмауэра на video, до агента не доходят). Ответ `403` самого агента (второй слой) проверяют тесты, живьём его не увидеть.
- `kill -9` процесса агента: systemd поднимает его за ~5 с (`Restart=on-failure`), срез снова отдаётся.
- **Перезагрузку узлов не проводили:** ai и video несут боевую нагрузку. Переживаемость перезагрузки — по `systemctl is-enabled` = `enabled` (ai и video) и по автозапуску WSL выше; первый реальный перезапуск проверит себя сам.

### Фрагмент `HardwareNodes` для боевого конфига

Вставляется в `/opt/ccs/app/appsettings.Local.json` **после** мержа фичи и штатной выкатки (отдельный шаг, по просьбе Андрея). Основной движок `main` (`LocalLlm`) сидит на ai, движок `gemma` (`LocalEngines`) — на video, ComfyUI — на video; на worker сейчас ни одного движка.

```json
"HardwareNodes": [
  { "Key": "worker", "Title": "worker", "Source": "local", "Engines": [], "Media": false },
  { "Key": "ai",     "Title": "ai",     "Source": "http://192.168.7.83:9105",  "Engines": ["main"],  "Media": false },
  { "Key": "video",  "Title": "video",  "Source": "http://192.168.7.208:9105", "Engines": ["gemma"], "Media": true }
]
```

## Тесты

```bash
python3 deploy/node-stats/tests/test_ccs_node_stats.py
```

Тест `test_hung_nvidia_smi` — фейковый `nvidia-smi` спит 5 с, срез собирается ≤ 1,5 с с `gpuCsv: null`; без тайм-аута он краснеет.

## Откат

```bash
systemctl disable --now ccs-node-stats
rm -f /etc/systemd/system/ccs-node-stats.service /etc/ccs-node-stats.json
rm -rf /usr/local/lib/ccs-node-stats
systemctl daemon-reload
```

Агент ничего не пишет на диск, поэтому после удаления на узле ничего не остаётся. На стороне CCS узел без ответа получает статус `down`, остальная вкладка «Железо» работает.
