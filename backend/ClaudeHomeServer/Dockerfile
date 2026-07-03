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

# ===== 3. Сборка бэкенда (.NET 9) =====
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS backend
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
FROM node:22-bookworm AS runtime

# .NET 9 runtime из официального образа (вся раскладка /usr/share/dotnet + муксер)
COPY --from=mcr.microsoft.com/dotnet/aspnet:9.0 /usr/share/dotnet /usr/share/dotnet
RUN ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet

# CLI claude (реестр npm)
RUN npm install -g @anthropic-ai/claude-code

# Непривилегированный пользователь; HOME под volume ~/.claude
RUN useradd -m -u 1001 app
WORKDIR /app

COPY --from=backend /app/publish ./
COPY --from=mcp /src/mcp-dify/dist        ./mcp-dify/dist
COPY --from=mcp /src/mcp-dify/node_modules ./mcp-dify/node_modules
COPY --from=mcp /src/mcp-dify/package.json ./mcp-dify/package.json
# MCP-сервер задач: чистый Node без зависимостей — сборка не нужна
COPY mcp/tasks-server ./mcp/tasks-server

# Каталоги под volume (проекты, данные, креды claude)
RUN mkdir -p /projects /data /home/app/.claude \
 && chown -R app:app /app /projects /data /home/app

USER app
ENV HOME=/home/app \
    DISABLE_AUTOUPDATER=1
EXPOSE 5000
ENTRYPOINT ["dotnet", "ClaudeHomeServer.dll"]
