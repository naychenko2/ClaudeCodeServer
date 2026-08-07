# Перенос FreeLLMAPI на Host B (192.168.7.208)

Пошаговая инструкция. Подразумевается, что:

- **Host A** (192.168.7.65) — машина, где сейчас крутится FreeLLMAPI и CCS. С неё готовим артефакты.
- **Host B** (192.168.7.208) — машина, где живёт Dify, drawio, onlyoffice, open-webui. Сюда переносим FreeLLMAPI.
- Доступ к Docker на Host B — только руками оператора (нет SSH/Docker-API с Host A).

## Состав каталога

| Файл | Где живёт | Секрет? |
|---|---|---|
| `docker-compose.yml` | git | нет |
| `deploy-freellmapi-hostb.ps1` | git | нет |
| `firewall-freellmapi.ps1` | git | нет |
| `.env` | ручной перенос (под `*.env` в `.gitignore`) | **да** — содержит `ENCRYPTION_KEY` |
| `freellmapi-image.tar` | `C:\Temp\freellmapi-hostb\` (на Host A, **не в git**) | нет |
| `freellmapi-data.tar.gz` | `C:\Temp\freellmapi-hostb\` (на Host A, **не в git**) | **да** — там SQLite с зашифрованными ключами провайдеров FreeLLM |

## Шаги

### 0. Подготовка (Host A) — выполнено Марком 2026-08-07

В `C:\Temp\freellmapi-hostb\` уже лежат:

- `freellmapi-image.tar` (≈153 MB) — дамп образа `ghcr.io/tashfeenahmed/freellmapi:latest`.
- `freellmapi-data.tar.gz` (≈4.8 MB) — дамп тома `freellmapi_freellmapi-data`, включая SQLite БД с зашифрованными ключами и сгенерированным unified Bearer-ключом.

Артефакты НЕ закоммичены — `*.env` под `.gitignore`, бинари в `C:\Temp\` вне репозитория.

### 1. Перенос артефактов на Host B

Скопируйте на Host B:

```
C:\Temp\freellmapi-hostb\freellmapi-image.tar
C:\Temp\freellmapi-hostb\freellmapi-data.tar.gz
```

Удобные способы: USB-диск, SMB-шара, scp. Класть, например, в `C:\Temp\freellmapi-hostb\` (любая папка по выбору).

### 2. Перенос конфигов (этот каталог)

Скопируйте в `C:\freellmapi-hostb\` (или любую другую папку) на Host B:

- `docker-compose.yml`
- `deploy-freellmapi-hostb.ps1`
- `firewall-freellmapi.ps1`
- `.env` (тот же, что лежит рядом в этом каталоге — с `ENCRYPTION_KEY` и `PORT=3001`)

### 3. Поднять FreeLLMAPI

На Host B в PowerShell **от администратора**:

```powershell
cd C:\freellmapi-hostb
.\deploy-freellmapi-hostb.ps1
```

Скрипт:

1. Загрузит образ (`docker load`).
2. Создаст и наполнит том `freellmapi-data`.
3. Запустит контейнер через `docker compose up -d`.
4. Дождётся `/api/ping`.
5. Проверит Bearer-ключ (`/api/keys`).

### 4. Ограничить фаервол

Тоже на Host B, PowerShell **от администратора**:

```powershell
.\firewall-freellmapi.ps1
```

Создаст два правила:

- `FreeLLMAPI 3001 (Host A only)` — **Allow** с `RemoteAddress=192.168.7.65`.
- `FreeLLMAPI 3001 (deny all others)` — **Block** на тот же порт (страховка).

Проверить:

```powershell
Get-NetFirewallRule | Where-Object DisplayName -like 'FreeLLMAPI*' | Format-Table Name,DisplayName,Enabled,Direction,Action
```

### 5. Переключить CCS (Host A)

**Только после успешного деплоя на Host B.** Иначе упадёт работающий CCS на `localhost:3001`.

Правки в `appsettings.Local.json`:

- **dev** (`C:\Sources\ClaudeCodeServer\backend\ClaudeHomeServer\appsettings.Local.json`) — секции `LlmProviders.freellmapi` сейчас нет, добавить:

  ```json
  "freellmapi": {
    "ApiKey": "<ключ FREELLM_BEARER_KEY из .env на Host B>",
    "AnthropicBaseUrl": "http://192.168.7.208:3001",
    "ApiBaseUrl": "http://192.168.7.208:3001/v1"
  }
  ```

- **prod** (`C:\ClaudeServer\prod\appsettings.Local.json`) — секция `freellmapi` уже есть (там только `ApiKey`); заменить URL:

  ```json
  "freellmapi": {
    "ApiKey": "<ключ FREELLM_BEARER_KEY из .env на Host B>",
    "AnthropicBaseUrl": "http://192.168.7.208:3001",
    "ApiBaseUrl": "http://192.168.7.208:3001/v1"
  }
  ```

> ВАЖНО: `appsettings.json` в репозитории **не трогать** — там дефолт `http://localhost:3001`.
> Прод-Local.json недавно правился вручную (там `ConsoleCookie` для Alibaba) — **не затереть**.

Затем перезапустить CCS на dev (`dotnet run --project ClaudeHomeServer` или через IDE) и на prod (`C:\ClaudeServer\prod\ClaudeHomeServer.exe`).

### 6. Проверка (Host A)

1. **Карточка баланса.** Открыть в CCS окно «Поставщики моделей» → FreeLLM → карточка должна показать:
   - счётчик «Провайдеры» (`X/Y` живых),
   - Note «За 24ч: …».
2. **Чат.** Создать новый чат на провайдере `freellmapi` и убедиться, что CCS ходит на `192.168.7.208:3001` (ответ приходит).
3. **One-shot действие.** Запустить любое фоновое действие (например, тегирование или сводку), которое идёт через `freellmapi`-direct — должно сработать без ошибки.

### 7. Гашение Host A

После успешной проверки:

```powershell
# Host A
docker compose -f "C:\Sources\freellmapi\freellmapi\docker-compose.yml" down
# опционально — вычистить и том (ОСТОРОЖНО: ключи провайдеров FreeLLM)
# docker volume rm freellmapi_freellmapi-data
```

После этого можно (опционально) удалить `C:\Temp\freellmapi-hostb\` и `C:\Sources\freellmapi\` целиком, если каталог больше не нужен (а исходники FreeLLM — отдельный репо, за пределами CCS).

## Egress-прокси

`CLAUDE_EGRESS_PROXY=http://192.168.7.208:2080` живёт на Host A и не должен влиять: `NO_PROXY` в `docker-compose.claude.yml` уже включает `${DOCKER_HOST_B:-localhost}`, а `.env` проекта задаёт `DOCKER_HOST_B=192.168.7.208`. Проверено — Dify и OnlyOffice ходят напрямую.

Если бы Egress-прокси всё же вмешивался, в `Program.cs` HTTP-клиенты к локальным сервисам уже отключают прокси через `.WithoutEgressProxy()` (`QuietHttpClientExtensions`). Дополнительной правки не нужно.

## Что могло пойти не так

- **401 на `/api/keys`** после деплоя — значит `ENCRYPTION_KEY` из `.env` не совпадает с ключом, которым зашифрована БД. Проверьте, что копировали `.env` именно из этого каталога, а не шаблон.
- **Том `freellmapi-data` уже существовал с чужими данными.** Скрипт `deploy-freellmapi-hostb.ps1` стирает содержимое и заливает дамп — должно быть чисто. Если том был создан вручную раньше, удалить: `docker volume rm freellmapi-data`.
- **Healthcheck не проходит.** Смотреть `docker logs freellmapi` — чаще всего ENCRYPTION_KEY битый (см. выше) или том не подцепился (проверить `docker volume inspect freellmapi-data`).
- **Файрвол режет всё подряд.** Проверьте, что IP Host A реально `192.168.7.65` (`ipconfig` на Host A). В скрипте `firewall-freellmapi.ps1` это параметр `-HostA`, по умолчанию тот самый.