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
