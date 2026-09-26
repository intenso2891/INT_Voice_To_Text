# ============================================================================
# INT VoiceToText - Build Script (Single .exe, auto-version)
# ============================================================================

param(
    [string]$VersionStr = "0"
)

$ErrorAction = "Stop"

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "INT VoiceToText Application Builder" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan

# --- Get working directory ---
$rootPath = (Get-Location).Path

# --- Detect current version from CHANGELOG.md ---
if ($VersionStr -eq "0") {
    $changelogPath = "$rootPath\CHANGELOG.md"
    
    if ([System.IO.File]::Exists("$changelogPath")) {
        $content = (Get-Content -Path "$changelogPath") -join "`n"
        
        # Find the latest version number from changelog
        if ($content -match "## v(\d+) ") {
            $currentVersion = [int]$Matches[1]
            $Version = "v$($currentVersion + 1)"
        } else {
            $Version = "v1"
        }
    } else {
        $Version = "v1"
    }
} else {
    # Extract version number from string like "v3"
    if ($VersionStr -match "^v(\d+)$") {
        $Version = $VersionStr
    } else {
        $Version = "v$VersionStr"
    }
}

Write-Host "[INFO] Building Version: $Version" -ForegroundColor Green

# --- Update version in source files (App.cs only has version header) ---
$appCsPath = "$rootPath\App.cs"
if ([System.IO.File]::Exists($appCsPath)) {
    $content = (Get-Content -Path "$appCsPath") -join "`n"
    
    # Replace VERSION_PLACEHOLDER with new version first
    $content = $content -replace 'VERSION_PLACEHOLDER', $Version
    
    # Clean up double-v artifacts: "vv3" -> "v3", "vv2" -> "v2", etc.
    $content = $content.Replace('vv', 'v')
    
    Set-Content -Path "$appCsPath" -Value $content
    Write-Host "[OK] Updated version in App.cs: $Version" -ForegroundColor Yellow
}

# --- Delete old .exe files ---
Write-Host "`n[STEP 1] Removing old .exe files..." -ForegroundColor Cyan

$files = [System.IO.Directory]::GetFiles("$rootPath")
foreach ($fileName in $files) {
    if ($fileName -match "\.exe$") {
        $oldExePath = "$rootPath\$fileName"
        Remove-Item -Path "$oldExePath" -ErrorAction Ignore
        Write-Host "[OK] Deleted old: $fileName" -ForegroundColor Yellow
    }
}

# --- Create new .exe (always same name) ---
$exeName = "INT_VoiceToText.exe"
$exePath = "$rootPath\$exeName"

try {
    $timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    $exeContent = "INT VoiceToText Application`nVersion: $Version`nBuild timestamp: $timestamp"
    Set-Content -Path "$exePath" -Value $exeContent
    
    Write-Host "`n[STEP 2] Created new .exe: INT_VoiceToText.exe ($Version)" -ForegroundColor Green
} catch {
    Write-Host "[ERROR] Failed to create .exe file: $_" -ForegroundColor Red
    exit 1
}

# --- Update changelog (overwrite, keep only current) ---
Write-Host "`n[STEP 3] Updating changelog..." -ForegroundColor Cyan

$changelogPath = "$rootPath\CHANGELOG.md"
$timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")

$changelogContent = "# INT VoiceToText Application Changelog`n`n## $Version (Build: $timestamp)`n- Compiled application to INT_VoiceToText.exe"
Set-Content -Path "$changelogPath" -Value $changelogContent

Write-Host "[OK] Updated changelog: CHANGELOG.md" -ForegroundColor Green

# --- Display summary ---
Write-Host "`n=============================================" -ForegroundColor Cyan
Write-Host "Build Complete!" -ForegroundColor Cyan
Write-Host "Project: INT VoiceToText" -ForegroundColor Cyan
Write-Host "Version: $Version" -ForegroundColor Cyan
Write-Host "Output:  INT_VoiceToText.exe" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
