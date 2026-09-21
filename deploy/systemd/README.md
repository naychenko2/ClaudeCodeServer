# deploy/systemd — user-обвязка ccs

Unit-файлы и drop-in'ы для user-инстанса systemd, под которым работает
ClaudeHomeServer и его агенты. Хранятся в git, чтобы:

- переживать `daemon-reload` (в отличие от `systemctl --user set-property`,
  который пишет в `user.control/`);
- попадать в ревью при изменении;
- воспроизводиться при развёртывании на новой машине;
- иметь единый источник правды для bэкенда (`ccs.service`) и его cgroup-контура
  (`ccs-agents.slice`).

## Состав

| Файл | Назначение |
|---|---|
| `ccs.service` | Основной unit-файл бэкенда (dotnet, Kestrel, `/opt/ccs/app`). |
| `ccs.service.d/50-oom-preference.conf` | Drop-in: `ManagedOOMPreference=omit` — oomd не выбирает бэкенд жертвой. |
| `ccs-agents.slice.d/50-memory-limits.conf` | Drop-in: `MemoryHigh=18G`, `MemoryMax=24G`. |
| `ccs-agents.slice.d/50-oom-preference.conf` | Drop-in: `ManagedOOMMemoryPressure=kill` — oomd бьёт агентов, не прод. |
| `install-user-units.sh` | Скрипт раскладки в `~/.config/systemd/user/` и очистки `user.control/`. |

`ccs.slice` и `ccs-agents.slice` как файлы отсутствуют: они создаются на лету
при первом `systemd-run --slice=ccs-agents.slice`, а свойства держатся в
drop-in'ах.

## Применение

```bash
./deploy/systemd/install-user-units.sh             # раскладка + daemon-reload
./deploy/systemd/install-user-units.sh --restart   # то же + restart ccs.service
./deploy/systemd/install-user-units.sh --dry-run   # показать, что сделает
```

Требования:

- user-шина systemd доступна (выполнять из графической сессии пользователя, под
  которым крутится ccs.service);
- не root: запуск от своего пользователя, иначе скрипт раскладывает файлы
  не туда.

Скрипт идемпотентен: повторный запуск не сносит чужие drop-in'ы, чистит только
те `user.control/`-файлы, которые этот набор явно заменяет
(`50-ManagedOOMPreference.conf`, `50-MemoryHigh.conf`, `50-MemoryMax.conf`,
`50-ManagedOOMMemoryPressure.conf`).

## Как считать значения лимитов под свою машину

`MemoryHigh` и `MemoryMax` **МАШИННО-СПЕЦИФИЧНЫЕ**. Жёстких чисел в git не
кладём — кладём образец с правилом.

Схема расчёта для ccs-agents.slice:

1. Возьмите общий объём RAM: `free -h | awk '/Mem:/ {print $2}'`.
2. Вычтите всё, что на этой же машине живёт ВНЕ ccs-agents.slice и может
   съесть память (qemu, vLLM, другие сервисы — `systemd-cgtop` покажет
   правду). Замер «как обычно нагружено» в пиках.
3. Вычтите запас на сам бэкенд (`ccs.service` — обычно 1–2 GB, но смотрите
   `systemd-cgtop` на ccs.service) и системные нужды (5–10 % RAM).
4. Остаток — потолок для агентов. `MemoryMax` ≈ 1.3 × `MemoryHigh`:
   `MemoryHigh` начинает душить аллокатор раньше, `MemoryMax` — последний
   рубеж перед OOM-killer ядра.
5. **Сначала `MemoryMax`**, потом `MemoryHigh`. Если при тесте на реальной
   нагрузке видите массовые SIGKILL от ядра — поднимайте `MemoryMax`, не
   `MemoryHigh` (иначе никогда не дойдёте до дросселирования).

Пример с этой машины (60 GB RAM):

| Потребитель | Объём |
|---|---|
| qemu (виртуалки) | ~16.7 GB |
| vLLM (модель) | ~6–7 GB |
| система + кэш | ~6 GB |
| ccs.service (бэкенд) | ~1–2 GB |
| **итого вне агентов** | ~30 GB |
| **остаток на ccs-agents.slice** | ~30 GB |
| `MemoryHigh` (дроссель) | **18G** (≈ 60 % остатка) |
| `MemoryMax` (жёсткий потолок) | **24G** |

Если запускать локальную модель в vLLM на этой же машине — вычтите её память
из остатка до подбора `MemoryMax`. Сейчас vLLM уже учтён.

## Что делать при инциденте OOM

1. Посмотреть, кто кого убил: `journalctl --user -k -p err | grep -i oom` и
   `journalctl --user -u systemd-oomd` (если включён в системе).
2. Снять замер по ccs-agents.slice: `systemd-cgtop` (top по cgroup).
3. Решить, что не так: либо превышен реальный потолок (поднять `MemoryMax`
   и/или `MemoryHigh`), либо oomd выбрал жертву по PSI при нашем же лимите
   (проверить, что `MemoryHigh` не слишком жёсткий — процессы должны успевать
   сбрасывать страницы).
4. Правка в `deploy/systemd/ccs-agents.slice.d/50-memory-limits.conf` →
   `./deploy/systemd/install-user-units.sh --restart`.
