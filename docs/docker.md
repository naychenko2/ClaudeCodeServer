# Запуск в контейнере (песочница для Claude)

Весь бэкенд и CLI `claude` упакованы в один Linux-образ. Контейнер видит **только**
проброшенные volume — хостовая файловая система Claude недоступна. Это жёсткая граница
поверх permission-правил приложения.

```
ХОСТ                                  КОНТЕЙНЕР claude-server
  C:\ClaudeProjects        ──►  /projects            (проекты, RW)
  volume claude-data       ──►  /data                (projects.json, sessions.json, история, auth-key)
  volume claude-home       ──►  /home/app/.claude     (креды подписки claude)
  C:\certs\naychenko (прод)──►  /certs                (TLS, только чтение)
        │ host.docker.internal
        ▼
  Dify :8080  •  OnlyOffice :8090   (отдельные контейнеры, НЕ внутри образа)
```

## Что внутри образа

- ASP.NET Core 9 (`ClaudeHomeServer.dll`)
- Node.js 22 + `@anthropic-ai/claude-code` (CLI `claude` в PATH)
- `git`, `bash`
- Собранный фронтенд в `wwwroot`
- MCP-сервер Dify (`/app/mcp-dify/dist`)

Код приложения **не менялся** — все хост-зависимые пути и URL переопределены через
переменные окружения (см. compose-файлы).

## Предпосылки

- Docker Desktop (Windows, WSL2-бэкенд).
- Папка проектов на хосте, напр. `C:\ClaudeProjects` (НЕ облачный синхро-диск).
- Запущенные контейнеры Dify и OnlyOffice (если нужны их функции).
- `.mcp.docker.json` в корне репо — скопировать из `.mcp.docker.example.json` и вписать `DIFY_API_KEY`.
- `.env` в корне репо — скопировать из `.env.example` (папка проектов, прокси, серты).

## Локальный запуск

```powershell
# (опц.) своя папка проектов — иначе берётся C:/ClaudeProjects
$env:CLAUDE_PROJECTS_DIR = "C:/ClaudeProjects"

docker compose -f docker-compose.claude.yml up -d --build
```

Первый вход по подписке (один раз — креды лягут в volume `claude-home` и переживут пересоздание):

```powershell
docker exec -it claude-server claude login
# откроется ссылка/код — авторизоваться, вставить токен
```

Приложение: <http://localhost:5000> (логин в приложение: `admin` / `12345` в Dev-режиме).

Полезное:

```powershell
docker logs -f claude-server
docker exec -it claude-server claude --version   # проверить, что CLI на месте
docker compose -f docker-compose.claude.yml down # остановить (volume сохраняются)
```

## Выход в интернет через прокси

Если у контейнера нет прямого egress (сеть выпускает только через прокси) — задать
`CLAUDE_EGRESS_PROXY` в `.env`. Адрес используется **и при сборке** (шаги `npm`/`dotnet restore`),
**и в runtime** (вызовы API Claude из CLI и .NET). DNS внешних хостов резолвит сам прокси
(CONNECT), поэтому никакой ручной настройки DNS не нужно. Пусто = прямой интернет.

```
# .env
CLAUDE_EGRESS_PROXY=http://192.168.7.208:2080
```

`docker compose ... up -d --build` подхватит прокси автоматически. Проверка egress изнутри:

```powershell
docker exec claude-server curl -s -o /dev/null -w '%{http_code}\n' -x $env:CLAUDE_EGRESS_PROXY https://api.anthropic.com/v1/models
# 401 = до API достучались (ключа нет — это норма); таймаут/000 = прокси недоступен
```

## Прод-режим (naychenko.me)

Тот же образ + оверрайд с портами 80/443 и TLS-сертами:

```powershell
$env:CLAUDE_CERTS_DIR = "C:/certs/naychenko"   # fullchain.pem + privkey.pem
docker compose -f docker-compose.claude.yml -f docker-compose.claude.prod.yml up -d --build
```

`appsettings.Production.json` подхватывается автоматически (Kestrel 80/443, `AllowedHosts` с доменом).
Пути сертов переопределены на `/certs/*`.

> Примечание: при наложении файлов список `ports` суммируется, поэтому в прод-режиме
> хост-порт 5000 тоже остаётся проброшенным (слушателя на нём нет — безвреден). Если порт
> занят, убрать его из базового файла или объявить отдельный прод-compose без наследования.

## Проверка изоляции

В чате попросить Claude выполнить `ls /` и `cat /etc/hostname`, затем попробовать прочитать
что-нибудь с «хоста» (напр. `ls /mnt/c` или старый путь `M:\...`). Claude увидит только ФС
контейнера и содержимое `/projects`, `/data` — хостовые диски недоступны. Это и есть песочница.

## Заметки

- **Проекты в проброшенной папке.** Новые проекты создаются под `/projects` → реально пишутся в
  `C:\ClaudeProjects`. Старый `projects.json` с путями `M:\...` в контейнере не откроется
  (это другой `data`-volume); перенос — скопировать файлы в `C:\ClaudeProjects` и создать проекты заново.
- **Производительность.** Bind-mount Windows-папки через WSL2 медленнее нативной ФС; для крупных
  репозиториев с `node_modules` возможны задержки file-watcher (он уже исключает `node_modules/bin`).
- **OnlyOffice callback.** OnlyOffice из своего контейнера обращается к нашему бэкенду по
  `OnlyOffice__BackendUrl` (host.docker.internal:5000). Если редактор не сохраняет — проверить этот адрес.
- **Секреты.** `.mcp.docker.json` и `appsettings.Local.json` в `.gitignore` и в `.dockerignore` —
  в образ не попадают; конфиг только через env/volume.
