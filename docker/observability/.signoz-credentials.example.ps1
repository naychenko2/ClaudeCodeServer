# Копируй как .signoz-credentials.ps1 и впиши свои креды SigNoz.
# Файл .signoz-credentials.ps1 в .gitignore — в репо не попадает.
#
# Два варианта (PAT удобнее — не истекает):

# ── Вариант 1 (рекомендуемый): Personal Access Token ─────────────────────────
# Создаётся один раз в SigNoz UI: Settings → API Keys → New Key.
# Долгоживущий (или бессрочный), можно отзывать через UI.
$env:SIGNOZ_JWT = "signoz-pat-вставь-сюда-длинную-строку-токена"

# ── Вариант 2: email + пароль (из первого запуска setup wizard) ──────────────
# Применяется если PAT не создан. Скрипт сам логинится при каждом запуске.
# Раскомментируй если используешь этот вариант:
# $env:SIGNOZ_EMAIL    = "you@example.com"
# $env:SIGNOZ_PASSWORD = "your-signoz-password"

# Опционально: если SigNoz на другом URL. Base-path обязателен — с v0.134 под ним
# живёт ВЕСЬ API, и адрес без префикса отвечает 404 на всё, кроме /api/v1/health.
# $env:SIGNOZ_URL = "http://localhost:3301/telemetry-proxy"
