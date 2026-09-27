# INT VoiceToText — prerequisite installer
$ErrorActionPreference = 'Stop'
Write-Host 'INT VoiceToText — установка необходимых компонентов' -ForegroundColor Cyan
Write-Host ''

$webViewGuid = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
$keys = @(
  "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$webViewGuid",
  "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$webViewGuid",
  "HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$webViewGuid"
)
$installed = $false
foreach ($key in $keys) { if (Test-Path $key) { $installed = $true; break } }

if ($installed) {
  Write-Host 'WebView2 Runtime уже установлен.' -ForegroundColor Green
  Read-Host 'Нажмите Enter для выхода'
  exit 0
}

Write-Host 'WebView2 Runtime не найден. Запускаю официальный установщик через winget...' -ForegroundColor Yellow
$winget = Get-Command winget -ErrorAction SilentlyContinue
if ($winget) {
  & winget install --id Microsoft.EdgeWebView2Runtime --exact --source winget --silent --accept-source-agreements --accept-package-agreements
  if ($LASTEXITCODE -eq 0) {
    Write-Host 'WebView2 Runtime успешно установлен.' -ForegroundColor Green
    Read-Host 'Нажмите Enter для выхода'
    exit 0
  }
}

Write-Host 'Автоматическая установка недоступна.' -ForegroundColor Red
Write-Host 'Откройте официальный сайт: https://developer.microsoft.com/microsoft-edge/webview2/'
Start-Process 'https://developer.microsoft.com/microsoft-edge/webview2/'
Read-Host 'Нажмите Enter для выхода'
