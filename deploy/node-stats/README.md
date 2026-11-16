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
- Агент читает только перечисленное в ADR (hwmon, `/proc/{meminfo,uptime,stat,self/mountinfo}`, `/run/ccs-gddr6/temps.json`) и запускает единственную команду `nvidia-smi --query-gpu=… --format=csv,noheader,nounits` без шелла. Процессов (`--query-compute-apps`) он не видит.

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
