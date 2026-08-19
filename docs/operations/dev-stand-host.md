# Дев-стенд на хосте рядом с боевым инстансом

На рабочей машине **одновременно живут два инстанса** ClaudeHomeServer, и это норма:

```
ХОСТ (Windows)
  C:\ClaudeServer\prod\ClaudeHomeServer.exe   :80 / :443   ← боевой, поднят треем
        data → C:\ClaudeServer\prod\data
  C:\Sources\ClaudeCodeServer\backend\...     :5000        ← дев-стенд из исходников
        data → C:/ClaudeData/dev  (DataPath из appsettings.Local.json)
```

**Порт :80 занят боевым сервером постоянно.** Это не осиротевший процесс и не мусор —
убивать его нельзя: в нём работает само приложение CCS вместе с активными чатами.

## Как поднимать дев-стенд

```powershell
cd C:\Sources\ClaudeCodeServer\backend
dotnet run --project ClaudeHomeServer      # → Hosting environment: Development, :5000
```

Фоном (чтобы не держать тул-колл/консоль):

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
Start-Process dotnet -ArgumentList 'bin\Debug\net10.0\ClaudeHomeServer.dll' `
  -WorkingDirectory 'C:\Sources\ClaudeCodeServer\backend\ClaudeHomeServer' `
  -WindowStyle Hidden -PassThru `
  -RedirectStandardOutput out.log -RedirectStandardError err.log
```

Проверка живости: `GET http://localhost:5000/` → 200, `GET /api/auth/me` без ключа → 401.

## Откуда стенд берёт фронт (единая точка правды)

Сервер раздаёт фронт ровно из одного места — логика в
[Program.cs](../../backend/ClaudeHomeServer/Program.cs), и путь пишется в лог на старте
(`«Фронтенд раздаётся из …»`):

| Режим | Откуда | Когда обновляется |
|---|---|---|
| дев `:5000` (`dotnet run` / dll из `bin/Debug`) | `frontend/dist` — **последняя `npm run build`** | только пересборкой фронта |
| прод (publish-каталог) | `wwwroot/` рядом с exe | агент выкатки (`deploy-agent.ps1`) зеркалит туда свежий `frontend/dist` |
| docker-образ | `/app/wwwroot` | stage `frontend` в Dockerfile пересобирает `dist` с нуля |

`wwwroot` — **чистый артефакт сборки, в репозитории его нет** (выкорчеван после инцидента
19.08: закоммиченный слепок от 04.08 путали с «фикс не выкатан»). Ручной `dotnet publish`
из репы статички больше не содержит: публикация без шага «dist → wwwroot» честно поднимется
без фронта (в логе будет предупреждение) — это лучше, чем молча отдавать двухнедельный слепок.

**Рецепт «я поправил .tsx — где это увидеть»:**

1. Горячая итерация: `npm run dev` (`:5173`) — проксирует `/api` и `/hubs` на `:5000`,
   HMR, service worker выключен.
2. Проверить на стенде `:5000`: `cd frontend; npm run build` → обновить страницу.
3. Увидеть на проде: закоммитить **и запушить** — агент пересобирает фронт сам; локальные
   коммиты на прод не попадают (инцидент 19.08: прод собрал `origin/master` без 21 локального коммита).

Перед приёмкой сверяй метку сборки: меню аватара → нижняя строка «сборка ДД.ММ ЧЧ:ММ · sha».
Метка старше твоих правок = ты смотришь старый бандл, а не «фикс не сделан».

### Service Worker и кеш браузера

Прод-сборка фронта регистрирует SW (`registerType: 'prompt'` — осознанно: обновление
не рвёт открытые чаты). Новый SW встаёт в ожидание; тост «Доступно обновление приложения»
появляется в течение минуты (UpdatePrompt опрашивает сервер каждые 60 с и по visibilitychange).
Чанки `/assets/**` отдаются `immutable` — по имени браузер их не перечитывает.

Если сервер отдаёт свежий бандл (проверка: `curl` по имени чанка из `index.html`), а браузер
упорно рисует старое — это кеш/SW. Лечение (рецепт Веры из прогонов F11): DevTools →
Application → Clear storage, либо в консоли
`caches.keys().then(ks => ks.forEach(k => caches.delete(k)))` + reload. Для сквозных
QA-прогонов сброс `caches` перед стартом — пункт чек-листа.

## Грабля: стенд лезет на :80 и падает

```
Unhandled exception. System.IO.IOException:
Failed to bind to address http://0.0.0.0:80: address already in use.
```

Цепочка причин:

1. Процессы, порождённые работающим CCS (в том числе агентские прогоны QA), **наследуют
   `ASPNETCORE_ENVIRONMENT=Production`** — трей выставляет его боевому серверу
   ([Tray/Program.cs](../../backend/ClaudeHomeServer.Tray/Program.cs)), дети наследуют.
2. В этом окружении подхватывается `appsettings.Production.json`, где прописан
   `Kestrel:Endpoints` → `0.0.0.0:80` и `0.0.0.0:443`.
3. :80 уже держит боевой инстанс → bind падает.

**`ASPNETCORE_URLS` тут не спасает** — это не баг конфигурации, а штатный приоритет
ASP.NET Core: заданный в конфиге `Kestrel:Endpoints` перекрывает `Urls`/`ASPNETCORE_URLS`
/`--urls`. Запуск с `ASPNETCORE_URLS=http://localhost:5099` всё равно уходит на :80.

Тот же капкан уже обойдён в CLI бэкапов — отдельным окружением `Inspection` вместо
Production (см. комментарий в [BackupCli.cs](../../backend/ClaudeHomeServer/Services/Backup/BackupCli.cs)).

## Почему `dotnet run` работает, а запуск dll — нет

`dotnet run` применяет `Properties/launchSettings.json`, где профиль ставит
`ASPNETCORE_ENVIRONMENT=Development` **поверх унаследованного** Production. Прямой запуск
собранной сборки (`dotnet bin\...\ClaudeHomeServer.dll`, `ClaudeHomeServer.exe`) или
`dotnet run --no-launch-profile` launchSettings не читают — там окружение задаём руками.

| Запуск | Окружение | Порт |
|---|---|---|
| `dotnet run --project ClaudeHomeServer` | Development | :5000 ✅ |
| `dotnet bin\Debug\net10.0\ClaudeHomeServer.dll` без env | Production | :80 ❌ |
| то же + `ASPNETCORE_URLS=...` | Production | :80 ❌ (Endpoints сильнее) |
| то же + `ASPNETCORE_ENVIRONMENT=Development` | Development | :5000 ✅ |

Правило одной строкой: **дев-стенд на хосте поднимаем только с явным
`ASPNETCORE_ENVIRONMENT=Development`**, порт :80 не трогаем.
