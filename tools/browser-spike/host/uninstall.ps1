# Убирает регистрацию native messaging host спайка (HKCU) и его манифест.

param([string]$HostName = 'com.ccs.browser_spike')

$ErrorActionPreference = 'Stop'
$regKey = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$HostName"
if (Test-Path $regKey) { Remove-Item -Path $regKey -Recurse -Force; Write-Host "Реестр очищен: $regKey" }
else { Write-Host 'В реестре не найдено — уже чисто.' }

$manifestPath = Join-Path $PSScriptRoot "$HostName.json"
if (Test-Path $manifestPath) { Remove-Item $manifestPath -Force; Write-Host "Удалён манифест: $manifestPath" }
