<#
.SYNOPSIS
  Правило Windows Firewall: разрешить входящие на 3001 ТОЛЬКО с IP Host A (192.168.7.65).
.DESCRIPTION
  Запускать на Host B от имени администратора.
  Проверяет текущие правила с тем же именем, удаляет их и создаёт одно актуальное.
#>

[CmdletBinding()]
param(
    [string]$HostA = "192.168.7.65",
    [int]$Port = 3001,
    [string]$RuleName = "FreeLLMAPI 3001 (Host A only)"
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path variable:PSDefaultParameterValues)) { $PSDefaultParameterValues = @{} }

Write-Host "==> Применяю правило firewall для FreeLLMAPI" -ForegroundColor Cyan

# Подчищаем старые копии правила, чтобы не плодить дубли
Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-NetFirewallRule -Name $_.Name -Confirm:$false }
Get-NetFirewallRule -DisplayName "$RuleName*" -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -like "$RuleName*" } |
    ForEach-Object { Remove-NetFirewallRule -Name $_.Name -Confirm:$false }

New-NetFirewallRule -DisplayName $RuleName `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort $Port `
    -RemoteAddress $HostA `
    -Profile Any `
    -Enabled True `
    -EdgeTraversalPolicy Block `
    | Out-Null

# Явный запрет всего остального на этот порт (страховка от случайного 0.0.0.0 bind)
$denyName = "FreeLLMAPI 3001 (deny all others)"
Get-NetFirewallRule -DisplayName $denyName -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-NetFirewallRule -Name $_.Name -Confirm:$false }
New-NetFirewallRule -DisplayName $denyName `
    -Direction Inbound `
    -Action Block `
    -Protocol TCP `
    -LocalPort $Port `
    -Profile Any `
    -Enabled True `
    | Out-Null

Write-Host "    OK: разрешено с $HostA, заблокировано остальное на TCP/$Port" -ForegroundColor Green
Write-Host ""
Write-Host "    Проверить:" -ForegroundColor Cyan
Write-Host "      Get-NetFirewallRule | Where-Object DisplayName -like 'FreeLLMAPI*' | Format-Table Name,DisplayName,Enabled,Direction,Action"