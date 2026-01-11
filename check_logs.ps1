#!/usr/bin/env pwsh
param(
    [string]$GameRoot = "C:\Program Files (x86)\Steam\steamapps\common\Valheim",
    [int]$LastLines = 200
)

$logFile = "$GameRoot\BepInEx\LogOutput.log"

if (-not (Test-Path $logFile)) {
    Write-Error "Лог-файл не найден: $logFile"
    exit 1
}

Write-Host "=== ПОСЛЕДНИЕ ОШИБКИ ===" -ForegroundColor Yellow
Get-Content $logFile | Select-String -Pattern "(Error|Exception|error|NullReference)" | Select-Object -Last 30 | ForEach-Object {
    Write-Host $_ -ForegroundColor Red
}

Write-Host "`n=== CACHEFORM СООБЩЕНИЯ ===" -ForegroundColor Cyan
Get-Content $logFile | Select-String -Pattern "CacheFork" | Select-Object -Last 30 | ForEach-Object {
    Write-Host $_
}

Write-Host "`n=== JEWELCRAFTING ===" -ForegroundColor Magenta
Get-Content $logFile | Select-String -Pattern "Jewelcrafting|matchLocalizedItem" | Select-Object -Last 30 | ForEach-Object {
    Write-Host $_
}

Write-Host "`n=== ПОСЛЕДНИЕ СТРОКИ ===" -ForegroundColor Green
Get-Content $logFile | Select-Object -Last $LastLines
