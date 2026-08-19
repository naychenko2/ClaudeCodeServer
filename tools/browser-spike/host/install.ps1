# Установка native messaging host спайка для текущего пользователя (HKCU, без админа).
# ID расширения задаётся явно: unpacked-расширение без "key" получает ID из пути
# загрузки (вычисляет Chrome; ключ в манифесте ломает загрузку unpacked — см. отчёт),
# поэтому после загрузки расширения берём фактический ID и передаём сюда.

param(
    # Имя нативного хоста, на которое подписано расширение (background.js).
    [string]$HostName = 'com.ccs.browser_spike',
    # Фактический ID загруженного расширения (32 символа a-p).
    [Parameter(Mandatory = $true)]
    [string]$ExtensionId
)

$ErrorActionPreference = 'Stop'
if ($ExtensionId -notmatch '^[a-p]{32}$') { throw "ID расширения должен быть 32 символа a-p: '$ExtensionId'" }
$batPath = Join-Path $PSScriptRoot 'host.bat'
$manifestPath = Join-Path $PSScriptRoot "$HostName.json"

$hostManifest = [ordered]@{
    name        = $HostName
    description = 'CCS browser spike: native messaging host (спайк ADR-008)'
    path        = $batPath
    type        = 'stdio'
    allowed_origins = @("chrome-extension://$ExtensionId/")
}
$hostManifest | ConvertTo-Json | Set-Content -Path $manifestPath -Encoding utf8
Write-Host "Манифест хоста: $manifestPath (origin: chrome-extension://$ExtensionId/)"

$regKey = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$HostName"
New-Item -Path $regKey -Force | Out-Null
Set-ItemProperty -Path $regKey -Name '(default)' -Value $manifestPath
Write-Host "Реестр: $regKey -> $manifestPath"
Write-Host 'Готово. Если Chrome уже запущен — перезапустите его: манифест читается при старте.'
