# INT VoiceToText — prerequisite installer (UTF-8 BOM)
$ErrorActionPreference = 'Stop'
Write-Host 'INT VoiceToText — установка необходимых компонентов' -ForegroundColor Cyan
Write-Host ''

# ─── 1. WebView2 Runtime ───────────────────────────────────────────
$webViewGuid = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
$keys = @(
  "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$webViewGuid",
  "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$webViewGuid",
  "HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$webViewGuid"
)
$wvInstalled = $false
foreach ($key in $keys) { if (Test-Path $key) { $wvInstalled = $true; break } }

if ($wvInstalled) {
  Write-Host '[OK] WebView2 Runtime уже установлен.' -ForegroundColor Green
} else {
  Write-Host '[!!] WebView2 Runtime не найден. Запускаю установку...' -ForegroundColor Yellow
  $winget = Get-Command winget -ErrorAction SilentlyContinue
  if ($winget) {
    & winget install --id Microsoft.EdgeWebView2Runtime --exact --source winget --silent --accept-source-agreements --accept-package-agreements
    if ($LASTEXITCODE -eq 0) {
      Write-Host '[OK] WebView2 Runtime успешно установлен.' -ForegroundColor Green
    } else {
      Write-Host '[!!] Автоматическая установка не удалась.' -ForegroundColor Red
      Write-Host 'Скачайте вручную: https://developer.microsoft.com/microsoft-edge/webview2/'
      Start-Process 'https://developer.microsoft.com/microsoft-edge/webview2/'
    }
  } else {
    Write-Host 'winget не найден. Открываю сайт...' -ForegroundColor Yellow
    Start-Process 'https://developer.microsoft.com/microsoft-edge/webview2/'
  }
}

Write-Host ''

# ─── 2. Microsoft Visual C++ Redistributable ──────────────────────
$vcInstalled = $false
if (Test-Path "$env:SystemRoot\System32\vcruntime140.dll") { $vcInstalled = $true }
$vcKeys = @(
  'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
  'HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64'
)
foreach ($key in $vcKeys) {
  if (Test-Path $key) {
    $v = (Get-ItemProperty $key -ErrorAction SilentlyContinue).Version
    if ($v) { $vcInstalled = $true; break }
  }
}

if ($vcInstalled) {
  Write-Host '[OK] Microsoft Visual C++ Redistributable уже установлен.' -ForegroundColor Green
} else {
  Write-Host '[!!] Visual C++ Redistributable не найден. Скачиваю...' -ForegroundColor Yellow
  $vcUrl = 'https://aka.ms/vs/17/release/vc_redist.x64.exe'
  $vcPath = Join-Path $env:TEMP 'vc_redist.x64.exe'
  try {
    Invoke-WebRequest -Uri $vcUrl -OutFile $vcPath -UseBasicParsing
    Write-Host 'Запускаю установку Visual C++ Redistributable...'
    Start-Process $vcPath -ArgumentList '/install','/quiet','/norestart' -Wait
    Write-Host '[OK] Visual C++ Redistributable установлен.' -ForegroundColor Green
  } catch {
    Write-Host '[!!] Не удалось скачать автоматически.' -ForegroundColor Red
    Write-Host "Скачайте вручную: $vcUrl"
    Start-Process $vcUrl
  }
}

Write-Host ''

# ─── 3. CUDA Toolkit (для NVIDIA GPU режима) ─────────────────────
Write-Host '--- Проверка CUDA Toolkit (для режима NVIDIA GPU) ---' -ForegroundColor Cyan

$cudaFound = $false
if ($env:CUDA_PATH -and (Test-Path $env:CUDA_PATH)) {
  $cudaFound = $true
  Write-Host "[OK] CUDA Toolkit найден: $env:CUDA_PATH" -ForegroundColor Green
}
if (-not $cudaFound) {
  $cudart = Get-ChildItem "$env:SystemRoot\System32\cudart64_*.dll" -ErrorAction SilentlyContinue
  if ($cudart) {
    $cudaFound = $true
    Write-Host "[OK] CUDA Runtime найден: $($cudart[0].Name)" -ForegroundColor Green
  }
}
if (-not $cudaFound) {
  $cudaDirs = Get-ChildItem 'C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\' -Directory -ErrorAction SilentlyContinue
  if ($cudaDirs) {
    $cudaFound = $true
    Write-Host "[OK] CUDA Toolkit найден: $($cudaDirs[-1].FullName)" -ForegroundColor Green
  }
}

if ($cudaFound) {
  Write-Host 'CUDA Toolkit установлен — режим NVIDIA GPU будет работать.' -ForegroundColor Green
} else {
  Write-Host '[!!] CUDA Toolkit НЕ найден.' -ForegroundColor Yellow
  Write-Host ''
  Write-Host 'Для режима "GPU — NVIDIA (CUDA)" нужен CUDA Toolkit 12.4+.' -ForegroundColor White
  Write-Host 'Без него используйте режим CPU (работает везде).'
  Write-Host ''
  $choice = Read-Host 'Установить CUDA Toolkit сейчас? (Y/N)'
  if ($choice -eq 'Y' -or $choice -eq 'y') {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
      Write-Host 'Скачиваю и устанавливаю CUDA Toolkit через winget (~3 ГБ)...' -ForegroundColor Yellow
      & winget install --id Nvidia.CUDA --exact --source winget --silent --accept-source-agreements --accept-package-agreements
      if ($LASTEXITCODE -eq 0) {
        Write-Host '[OK] CUDA Toolkit успешно установлен!' -ForegroundColor Green
        Write-Host 'Перезапустите INT VoiceToText для применения.' -ForegroundColor Yellow
      } else {
        Write-Host '[!!] Установка через winget не удалась.' -ForegroundColor Red
        Write-Host 'Скачайте вручную:' -ForegroundColor Yellow
        Write-Host 'https://developer.nvidia.com/cuda-downloads' -ForegroundColor Cyan
        Start-Process 'https://developer.nvidia.com/cuda-downloads'
      }
    } else {
      Write-Host 'winget не найден. Открываю страницу загрузки...' -ForegroundColor Yellow
      Start-Process 'https://developer.nvidia.com/cuda-downloads'
    }
  } else {
    Write-Host 'CUDA Toolkit не установлен. Режим NVIDIA GPU работать не будет.' -ForegroundColor Yellow
    Write-Host 'Используйте режим CPU в настройках INT VoiceToText.' -ForegroundColor White
  }
}

Write-Host ''
Write-Host '=== Проверка завершена ===' -ForegroundColor Cyan
Read-Host 'Нажмите Enter для выхода'
