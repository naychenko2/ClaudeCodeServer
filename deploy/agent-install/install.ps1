# Установщик агента AI Home для локальных проектов (Windows, PowerShell 5.1+).
# ЗАГЛУШКА (agent-distribution AD-3): сервер уже раздаёт этот файл по /agent/install.ps1,
# само содержимое пишет задача AD-5s.
param(
    [Parameter(Mandatory = $true)][string]$Server,
    [Parameter(Mandatory = $true)][string]$Code
)
Write-Error "Установщик агента ещё не готов: этот сервер раздаёт заглушку."
exit 1
