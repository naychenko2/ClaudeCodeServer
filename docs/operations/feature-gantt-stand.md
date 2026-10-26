# Стенд приёмки фичи «Хронология» (Гант)

Живой стенд на **фикстуре данных** фичи — для приёмки Веры (GC-QA) и для любой
повторной пробы страницы «Хронологии». Рецепт проверен с нуля 26.10.26; числа
эталона ниже — из того же прогона.

**Оговорка о времени действия.** Поля эталонной таблицы `stats.timeline.intervalsByStage`,
`turns`, `run.postMergeTails` есть в коде ветки `feature-gantt-c` и попадут в master только
с мержем фичи. В master этот рецепт и эталон действительны **после** мержа: до него в
master-коде этих полей искать не надо — стенд поднимается из worktree ветки, как указано ниже.

## Что поднимать

- worktree `~/Sources/ClaudeCodeServer-feature-gantt-c`, ветка `feature-gantt-c`;
- срез данных — `~/Sources/ClaudeCodeServer-feature-gantt-c-data/data`. Это **ФИКСТУРА**:
  копия боевых данных только по этой фиче (задачи + журнал) и один `admin` без пароля.
  Боевые данные стендом не читаются и не меняются.
- страница фичи: `#/project/fedfe8c3-eaab-48ae-a5bd-1661bb68f7cc/feature/8d33d18b-c6f6-4311-8ddb-e5a627f38fb2`.

Срез перегенерируется скриптом `make-slice.py` (рядом с каталогом `data/`).
**После перегенерации вручную вернуть флаг `features-overview` в `users.json` у admin**:
скрипт пишет пустой `FeatureFlags` — флаг является ручной поправкой, без него страница
фичи не показывается.

## Конфиг (machine-specific, в git не лежит)

`backend/ClaudeHomeServer/appsettings.Local.json` (в [.gitignore](../../.gitignore)):
принцип — `DataPath` указывает на `projects.json` **фикстуры**, `Auth:DevPassword`
задаёт мастер-пароль входа для Development:

```json
{
  "DataPath": "<каталог фикстуры>/data/projects.json",
  "Auth": { "DevPassword": "12345" }
}
```

### Грабля: `Auth:DevPassword` действует только в Development

В [UserStore.cs](../../backend/ClaudeHomeServer/Services/UserStore.cs) (строки 38–47)
вне `ASPNETCORE_ENVIRONMENT=Development` секция `Auth:DevPassword` **игнорируется**
(в логе warning). У `admin` в фикстуре `PasswordHash` пустой — вне Development вход
всегда 401. Это уже стоило времени на прошлой приёмке: стенд поднимаем **только**
в Development (запуск ниже даёт его сам).

## 1. Сборка фронта — до пробы

Стенд раздаёт фронт из `frontend/dist` — только из последней сборки
(см. [dev-stand-host.md](dev-stand-host.md)); свежая сборка перед пробой обязательна:

```bash
cd ~/Sources/ClaudeCodeServer-feature-gantt-c/frontend
npm run build        # нет node_modules в свежем worktree — сначала npm ci
```

## 2. Запуск бэка

```bash
cd ~/Sources/ClaudeCodeServer-feature-gantt-c/backend/ClaudeHomeServer
dotnet run           # профиль http из launchSettings: Development + :5000
```

**cwd важен:** `launchSettings.json` читается только при запуске из каталога проекта
и только без `--no-launch-profile`. Прямой пуск dll или `dotnet run --no-launch-profile`
окружение не ставит. **`ASPNETCORE_URLS` порт не перебивает:** в
`appsettings.Production.json` прописан `Kestrel:Endpoints` (:80/:443), он сильнее
`Urls`/`ASPNETCORE_URLS` — при унаследованном Production стенд лезет на :80 и падает
(разобрано в [dev-stand-host.md](dev-stand-host.md), «Стенд лезет на :80 и падает»).

Признак правильного старта в логе: `Hosting environment: Development`, `GET /` → 200.

## 3. Вход и адрес страницы

- вход: `admin` / `12345` (тот самый `DevPassword`);
- адрес: `http://127.0.0.1:5000/#/project/fedfe8c3-eaab-48ae-a5bd-1661bb68f7cc/feature/8d33d18b-c6f6-4311-8ddb-e5a627f38fb2`;
- для `ui_probe` маршрут идёт **в `path` вместе с `#`**, а не в `url` — иначе проба
  меряет домашний экран, а не «Хронологию»:

```
mcp__tests__ui_probe
  url:      http://127.0.0.1:5000
  path:     /#/project/fedfe8c3-eaab-48ae-a5bd-1661bb68f7cc/feature/8d33d18b-c6f6-4311-8ddb-e5a627f38fb2
  viewports: [360, 1280]
  user: admin  password: 12345
```

Проверенный прогон дал `auth: "ok"` (не `skipped`), статус 200 на 360 и 1280,
`finalUrl` — адрес фичи.

## Смоук перед пробой

```bash
TOKEN=$(curl -s -X POST http://127.0.0.1:5000/api/auth/login \
  -H 'Content-Type: application/json' -d '{"username":"admin","password":"12345"}' \
  | python3 -c 'import sys,json;print(json.load(sys.stdin)["token"])')
curl -o /dev/null -w '%{http_code}\n' -H "Authorization: Bearer $TOKEN" \
  http://127.0.0.1:5000/api/projects                                    # 200
curl -o /dev/null -w '%{http_code}\n' -H "Authorization: Bearer $TOKEN" \
  http://127.0.0.1:5000/api/tasks/features/8d33d18b-c6f6-4311-8ddb-e5a627f38fb2  # 200
```

## Эталон чисел фикстуры

Сверка после подъёма (ответ `GET features/{id}`) — числа живой фикстуры на 26.10.26:

| Что | Значение | Где в ответе |
|---|---|---|
| Стадии | 8 | `stats.timeline.stages` (cut, design, plan, prod, review, qa, final, merge) |
| Интервалы этапов | 105 | сумма `stats.timeline.intervalsByStage[*]` |
| Ходы в интервалах | 228 | сумма `turns` по всем интервалам |
| Хвосты после мержа | 12 | `run.postMergeTails` |
| Задачи фичи | 201 | `run.*`: 198 ролевых + 3 безролевых (`unroled`) |

Счётчика «30 ходов» в контракте **нет** — не искать. Числа разошлись — фикстура
устарела: пересобрать `make-slice.py` и вернуть флаг admin.

## Живой случай (GC-WT6)

Чтобы приёмка видела не только закрытый вид фичы, но и живой «идёт», срез несёт живой
случай: `make-slice.py` сам открывает эпик `8d33d18b…` (Status→1, CompletedAt→null) и
финал `2cddab26…` (Status→1) сразу после выборки — случай переживает любую перегенерацию.

- закрытое состояние лежит в резервной копии `data/tasks.json.bak-live` (рядом с `tasks.json`);
- **вернуть закрытое:** `cp ~/Sources/ClaudeCodeServer-feature-gantt-c-data/data/tasks.json.bak-live ~/Sources/ClaudeCodeServer-feature-gantt-c-data/data/tasks.json`;
- **включить живое без перегенерации** (если срез только что возвращён из резерва):

```bash
python3 -I - <<'EOF'
import json
p='/home/an/Sources/ClaudeCodeServer-feature-gantt-c-data/data/tasks.json'
ts=json.load(open(p))
for t in ts:
    if t['Id']=='8d33d18b-c6f6-4311-8ddb-e5a627f38fb2': t['Status'],t['CompletedAt']=1,None
    elif t['Id']=='2cddab26-0f6d-4789-9cd3-0462be05e5ba': t['Status']=1
json.dump(ts,open(p,'w'),ensure_ascii=False)
EOF
```

Что даёт живой случай в `GET /api/tasks/features/8d33d18b…` (проверено 26.10.26):
`run.state: "running"`, `run.now` — 1 запись (финал; эпик в «сейчас» не входит),
в `stats.timeline.intervalsByStage` — интервал с `live: true` в стадии `final`
(`to` = now), `stats.timeline.to` = now.

**`tasks.json` читается только при старте сервера** (`TaskManager.Load()` в конструкторе,
[TaskManager.cs](../../backend/ClaudeHomeServer.Tasks/TaskManager.cs):112) — после замены
файла стенд нужно перезапустить.

**После любой перегенерации `make-slice.py` — вручную вернуть флаг `features-overview`
в `users.json` у admin** (скрипт пишет пустой `FeatureFlags`, см. выше) — живой случай
этого не отменяет.

## Закрытый случай

Если приёмке нужен именно закрытый вид (например, проверить, что «сейчас» отсутствует):
вернуть фичу в Done одной командой резерва — `cp …tasks.json.bak-live …tasks.json`
(эпику вернутся Status 2 и его `CompletedAt`, финалу — Status 2). После перезапуска
стенда: `run.state: "closed"`, `run.now` пуст, интервалов с `live: true` нет.
Эталонная таблица выше — про это состояние.

## Погасить стенд

Гасить **по PID своего процесса** (`$!` при запуске или PID с порта через `ss -ltnp`):
прод и стенд — один бинарь, `pkill` по имени заденет боевой сервер. После гашения
порт :5000 свободен (`ss -ltn | grep :5000` — пусто).
