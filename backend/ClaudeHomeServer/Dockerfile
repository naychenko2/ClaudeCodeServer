# Multi-stage образ: бэкенд ASP.NET Core + CLI claude в одном Linux-контейнере.
# Контейнер служит песочницей: Claude видит только проброшенные volume, не хост.
# Контекст сборки — корень репозитория (см. docker-compose.claude.yml).

# ===== 1. Сборка фронтенда (Vite/React) =====
FROM node:22-alpine AS frontend
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build        # → /src/frontend/dist

# ===== 2. Сборка MCP-сервера Dify =====
FROM node:22-alpine AS mcp
WORKDIR /src/mcp-dify
COPY mcp-dify/package.json mcp-dify/package-lock.json ./
RUN npm ci
COPY mcp-dify/ ./
RUN npm run build && npm prune --omit=dev   # tsc → dist/, выкидываем dev-зависимости

# ===== 3. Сборка бэкенда (.NET 10) =====
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY backend/ClaudeHomeServer/ClaudeHomeServer.csproj backend/ClaudeHomeServer/
RUN dotnet restore backend/ClaudeHomeServer/ClaudeHomeServer.csproj
COPY backend/ ./backend/
RUN dotnet publish backend/ClaudeHomeServer/ClaudeHomeServer.csproj \
    -c Release -o /app/publish /p:UseAppHost=false
# Свежесобранный фронт кладём в wwwroot (исходный wwwroot исключён в .dockerignore)
COPY --from=frontend /src/frontend/dist /app/publish/wwwroot

# ===== 4. Runtime: node:22 (node+npm+git+bash) + .NET runtime + claude CLI =====
# База — официальный node-образ (на buildpack-deps): уже содержит node, npm, git, bash.
# .NET-runtime копируем из образа Microsoft, claude ставим из npm — без apt/nuget/nodesource.
# ===== PTY-bridge (C-хелпер forkpty) =====
FROM gcc:latest AS pty-bridge
WORKDIR /src
COPY backend/pty-bridge/pty-bridge.c ./
RUN gcc -O2 -static -o pty-bridge pty-bridge.c -lutil

# ===== 4б. Sandbox: песочница container-пользователей (БЕЗ бэкенда/фронта) =====
# Бэкенд работает на хосте и запускает здесь процессы изолированных пользователей
# через `docker exec`: ходы claude, pty-bridge терминала, dev-серверы, npx skills.
# Собирается отдельным target: docker build --target sandbox -t claude-sandbox .
# (последней стадией остаётся runtime — compose без target собирает её, как раньше)
FROM node:22-bookworm AS sandbox

RUN npm install -g @anthropic-ai/claude-code skills

RUN apt-get update && apt-get install -y --no-install-recommends mc \
 && rm -rf /var/lib/apt/lists/*

RUN useradd -m -u 1001 app
WORKDIR /app

COPY --from=pty-bridge /src/pty-bridge /app/pty-bridge
COPY --from=mcp /src/mcp-dify/dist        ./mcp-dify/dist
COPY --from=mcp /src/mcp-dify/node_modules ./mcp-dify/node_modules
COPY --from=mcp /src/mcp-dify/package.json ./mcp-dify/package.json
COPY mcp/tasks-server ./mcp/tasks-server
COPY mcp/notes-server ./mcp/notes-server
COPY mcp/memory-server ./mcp/memory-server
COPY mcp/personas-server ./mcp/personas-server
COPY mcp/workspace-server ./mcp/workspace-server
COPY mcp/notifications-server ./mcp/notifications-server
COPY claude-defaults ./claude-defaults
# Обвязка убиваемости ходов (см. комментарий в самом скрипте); sed снимает CRLF
# на случай Windows-чекаута
COPY backend/sandbox/run-turn.sh /app/run-turn.sh
USER root
RUN sed -i 's/\r$//' /app/run-turn.sh && chmod +x /app/run-turn.sh

# Точки bind-mount: /projects — корень проектов изолированных пользователей,
# /sandbox-profiles — per-user CLAUDE_CONFIG_DIR, /turn-tmp — временные MCP-конфиги ходов
RUN mkdir -p /projects /sandbox-profiles /turn-tmp /tmp/turns /home/app/.claude \
 && chown -R app:app /app /projects /sandbox-profiles /turn-tmp /tmp/turns /home/app

USER app
ENV HOME=/home/app \
    DISABLE_AUTOUPDATER=1 \
    LANG=C.UTF-8 \
    LC_ALL=C.UTF-8
# Контейнер живёт как idle-хост для docker exec
ENTRYPOINT ["sleep", "infinity"]

FROM node:22-bookworm AS runtime

# .NET 10 runtime из официального образа (вся раскладка /usr/share/dotnet + муксер)
COPY --from=mcr.microsoft.com/dotnet/aspnet:10.0 /usr/share/dotnet /usr/share/dotnet
RUN ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet

# CLI claude (реестр npm)
RUN npm install -g @anthropic-ai/claude-code

# CLI skills (github.com/vercel-labs/skills) — поиск и установка навыков из реестра.
# Предустанавливаем глобально, чтобы не тянуть пакет через npx в рантайме: иначе первый
# вызов скачивает пакет, и «шум» загрузки может исказить первый разбор вывода.
RUN npm install -g skills

# Консольные утилиты для веб-терминала (Midnight Commander и т.п.)
RUN apt-get update && apt-get install -y --no-install-recommends mc \
 && rm -rf /var/lib/apt/lists/*

# Непривилегированный пользователь; HOME под volume ~/.claude
RUN useradd -m -u 1001 app
WORKDIR /app

COPY --from=pty-bridge /src/pty-bridge /app/pty-bridge
COPY --from=backend /app/publish ./
COPY --from=mcp /src/mcp-dify/dist        ./mcp-dify/dist
COPY --from=mcp /src/mcp-dify/node_modules ./mcp-dify/node_modules
COPY --from=mcp /src/mcp-dify/package.json ./mcp-dify/package.json
# MCP-серверы задач, заметок, памяти, персон и рабочего пространства: чистый Node без зависимостей — сборка не нужна
COPY mcp/tasks-server ./mcp/tasks-server
COPY mcp/notes-server ./mcp/notes-server
COPY mcp/memory-server ./mcp/memory-server
COPY mcp/personas-server ./mcp/personas-server
COPY mcp/workspace-server ./mcp/workspace-server
COPY mcp/notifications-server ./mcp/notifications-server
# Дефолтная конфигурация CLI, поставляемая с приложением (workflow-скрипты и т.п.):
# entrypoint compose-файла копирует её в ~/.claude при КАЖДОМ старте с перезаписью (cp -f) —
# источник истины репозиторий, ручные правки этих файлов в volume живут до рестарта
COPY claude-defaults ./claude-defaults

# Каталоги под volume (проекты, данные, креды claude)
RUN mkdir -p /projects /data /home/app/.claude \
 && chown -R app:app /app /projects /data /home/app

USER app
# Локаль UTF-8 (базовый образ идёт с POSIX/C — некоторые инструменты чувствительны к ней)
ENV HOME=/home/app \
    DISABLE_AUTOUPDATER=1 \
    LANG=C.UTF-8 \
    LC_ALL=C.UTF-8
EXPOSE 5000
ENTRYPOINT ["dotnet", "ClaudeHomeServer.dll"]
