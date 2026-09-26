# INT VoiceToText — AI / Developer Context

> Актуальная карта проекта для ИИ-ассистентов и разработчиков. Перед изменениями сначала прочитайте этот файл и проверьте фактический код.

## 1. Назначение

**INT VoiceToText** — portable Windows-приложение для офлайн-диктовки:

```text
global hotkey
  -> NAudio microphone capture
  -> mono PCM 16 kHz
  -> Whisper.net + local Whisper model
  -> dictionary correction
  -> clipboard
  -> optional Ctrl+V into the previously focused window
```

Голос не отправляется в интернет. Интернет нужен только для первого скачивания модели и необязательной проверки GitHub Releases.

Текущая версия исходников: **1.6.5** (`Services/AppState.cs`, `AppVersion`).

## 2. Репозиторий и каталоги

Repository: `https://github.com/intenso2891/INT_Voice_To_Text`

Основная ветка: `main`

```text
INT_Voice_To_Text/
├── Program/                 # Локальная готовая сборка; ignored by Git; не коммитить
├── Program_Source/          # Единственный источник кода
├── .github/workflows/
│   └── release.yml          # GitHub Actions: build + ZIP + GitHub Release
├── README.md                # Пользовательское описание проекта
└── .gitignore
```

Папка `Program/` находится рядом с `Program_Source`, а не внутри `.git/`. Она локальная и может занимать несколько гигабайт из-за Large V3.

## 3. Важные файлы исходников

```text
Program_Source/
├── Program.cs                         # Точка входа, ASP.NET backend, Photino window, tray
├── INT_VoiceToText.csproj              # net9.0-windows, WinForms, win-x64, Whisper packages
├── Services/
│   ├── AppState.cs                    # settings.json, AppVersion, defaults
│   ├── VoiceEngine.cs                 # recording state machine and transcription pipeline
│   ├── AudioRecorder.cs               # NAudio capture, 16 kHz mono PCM
│   ├── SpeechService.cs               # Whisper.net factory/processor and model
│   ├── WordLibrary.cs                 # library_words auto-reload and whole-word replacement
│   ├── UpdateService.cs               # GitHub Releases API check/download
│   └── PrerequisiteChecker.cs         # WebView2 registry check at startup
├── Native/W32.cs                      # Win32 window, tray/overlay visibility, icon, work area
├── Services/HotkeyManager.cs           # RegisterHotKey message-only window
├── Updater/
│   ├── Program.cs                     # waits for main PID, extracts ZIP, replaces files, relaunches
│   └── INT_VoiceToText.Updater.csproj
├── frontend/
│   ├── src/App.tsx                    # React UI, compact overlay, settings, history
│   ├── src/lib/api.ts                 # REST + SignalR client
│   ├── src/i18n.ts                    # RU/EN strings
│   └── src/styles.css                 # UI and overlay styles
├── Install-Requirements.bat           # manual WebView2 installer launcher
└── Install-Requirements.ps1            # winget WebView2 installer with browser fallback
```

## 4. Startup sequence

`Program.Main` runs on an STA entry point:

1. `PrerequisiteChecker.Ensure()` checks Microsoft Edge WebView2 Runtime in registry.
2. If missing, a Yes/No MessageBox offers `Install-Requirements.bat`; the app exits until WebView2 is installed.
3. `AppState.Load()` loads `%LOCALAPPDATA%\INT_VoiceToText\settings.json`.
4. ASP.NET Core starts on localhost with random free port.
5. SignalR hub broadcasts state/result/error/level/debug events.
6. Photino window is created on a dedicated STA thread because WebView2 requires COM STA.
7. Tray icon is created from `tray-blue.ico`; while recording it switches to `tray-green.ico`.
8. The app checks GitHub latest Release silently. Network/rate-limit errors must not break offline operation.
9. Frontend connects through SignalR and also calls `/api/update/check` so an update notification is not lost before the SignalR connection exists.

## 5. Recording and transcription state machine

The engine states are:

```text
Idle
  -> Recording       (first hotkey)
  -> Transcribing    (second hotkey; recorder stops)
  -> Idle            (Whisper finished or error)
```

Compact overlay states are:

```text
recording      = red indicator + live equalizer + "Запись…"
transcribing   = yellow indicator + spinner + "Распознавание…"
ready          = green indicator + result + "Распознавание закончено, можно вставлять"
off            = overlay hidden
```

Important behavior:

- A second hotkey while `Transcribing` must not start a new recording.
- If a hotkey arrives before the Photino HWND exists, `Program.cs` stores `pendingCompactMode` and applies it after window creation.
- The frontend initializes overlay from the initial `/api/state`, so an early recording is visible even if it started before the frontend connected.
- Audio shorter than approximately 250 ms is rejected as `TOO_SHORT`.
- Recording `Stop()` must not run while holding the engine gate; this previously caused deadlocks.
- Do not call Photino `window.Invoke(...)` from the audio/recording thread; direct cross-thread window operations are used intentionally.

## 6. Whisper model and native runtimes

The model is `ggml-large-v3.bin`, approximately 2.95 GB. It is stored locally in:

```text
%LOCALAPPDATA%\INT_VoiceToText\models\ggml-large-v3.bin
```

The local portable `Program` may also contain `models/` for development/testing, but GitHub Release ZIPs deliberately exclude the model because of GitHub file-size limits. The app downloads it once on first use.

Whisper packages include CPU/CUDA/Vulkan/OpenVINO runtimes. Single-file publishing can omit native libraries, so the release workflow explicitly copies:

```text
Program_Source/bin/Release/net9.0-windows/win-x64/runtimes/
```

into `Program/runtimes/` and copies native Whisper DLLs when present. If the log says `Native Library not found`, inspect these files first.

## 7. Settings and defaults

`AppState.cs` is authoritative for defaults:

```text
AppVersion             = 1.6.5
Hotkey default         = Ctrl+Win+Alt+A
Language default       = ru
PasteResult default    = false (clipboard is always updated)
MicrophoneDevice       = 0
MicrophoneSensitivity  = 3.0
StartWithWindows       = false
MinimizeToTrayOnClose  = true
ComputeMode            = cpu | gpu | hybrid
GpuDevice              = 0
```

Existing `%LOCALAPPDATA%` settings override defaults. Therefore testing a new default may require editing or deleting:

```text
%LOCALAPPDATA%\INT_VoiceToText\settings.json
```

The user may have a different persisted hotkey/sensitivity than the source defaults.

## 8. Word library

File:

```text
Program/library_words/words.txt
```

Supported rules:

```text
wrong variant => correct text
wrong variant - correct text
```

`WordLibrary` reloads the file when it changes. Replacement uses whole-word Unicode boundaries, so short fragments are not replaced inside unrelated words.

## 9. HTTP and SignalR endpoints

Backend endpoints are localhost-only:

```text
GET  /api/state
POST /api/toggle
POST /api/settings
GET  /api/update/check
POST /api/update/install
```

SignalR events used by frontend:

```text
state       { status }
result      { text, rawText, durationMs }
error       { message }
level       number
/debug      line
update      { version, releaseUrl, assetUrl, assetName }
```

When adding an event, update both `Program.cs`/engine broadcasting and `frontend/src/lib/api.ts` plus the relevant React state/rendering.

## 10. Auto-update architecture

The app checks:

```text
https://api.github.com/repos/intenso2891/INT_Voice_To_Text/releases/latest
```

Only a remote semantic version greater than `AppState.AppVersion` is offered. The user must confirm via the UI.

The main app cannot safely replace its own running executable. Therefore:

1. main app downloads the Release ZIP to `%TEMP%`;
2. main app starts `INT_VoiceToText.Updater.exe` with PID, ZIP, target and exe arguments;
3. main app exits;
4. updater waits for the PID;
5. updater extracts ZIP to a temporary directory;
6. updater copies files over the target, preserving local `models/`;
7. updater launches the new main exe.

The updater must be included in both local `Program/` and the Release ZIP.

## 11. Building locally

Use PowerShell from the repository root. Stop a running app before publishing because it locks the single-file exe.

```powershell
$env:DOTNET_ROLL_FORWARD = 'LatestMajor'

# Frontend
cd Program_Source/frontend
npm ci
npm run build

# Main application
cd ..
dotnet publish .\INT_VoiceToText.csproj `
  -c Release -r win-x64 --self-contained true `
  /p:PublishSingleFile=true `
  /p:IncludeNativeLibrariesForSelfExtract=true `
  -o ..\Program

# Updater
dotnet publish .\Updater\INT_VoiceToText.Updater.csproj `
  -c Release -r win-x64 --self-contained true `
  /p:PublishSingleFile=true `
  /p:IncludeNativeLibrariesForSelfExtract=true `
  -o ..\Program
```

After publishing, copy/verify:

```text
icon.ico
tray-blue.ico
tray-green.ico
Install-Requirements.bat
Install-Requirements.ps1
library_words/words.txt
runtimes/                     # required native Whisper runtimes
models/ggml-large-v3.bin     # local only; not in GitHub ZIP
```

## 12. GitHub Release workflow

File: `.github/workflows/release.yml`.

A tag matching `v*.*.*` triggers Windows build, frontend build, main app publish, updater publish, native runtime copy, ZIP creation and GitHub Release publication.

Typical release flow:

```powershell
# 1. Update AppVersion and documentation
# 2. Test locally

git add .
git commit -m "Release v1.6.6"
git push origin main

git tag v1.6.6
git push origin v1.6.6
```

The generated user download is:

```text
INT_VoiceToText-v1.6.6.zip
```

Users should download the ZIP asset from GitHub Releases, not `Source code (zip)`.

## 13. What belongs in a Release

Create a new Release when the executable or its runtime behavior changes, for example:

- C# backend changes;
- React/frontend changes;
- native runtime packaging changes;
- updater changes;
- installer changes;
- version/default behavior changes.

A Release is **not required** for source-only documentation edits. Push documentation to `main`; it will be visible in the repository without rebuilding the portable application.

## 14. Diagnostics

Debug log during local runs:

```text
Program/int_voicetext_debug.log
```

Additional persisted debug output may be written under:

```text
%LOCALAPPDATA%\INT_VoiceToText\
```

Useful log markers:

```text
ENGINE_START
RECORD_START
RECORD_STOP
TRANSCRIBE_START
TRANSCRIBE_DONE
TRANSCRIBE_ERROR
CLIPBOARD_SET
```

Common failures:

```text
Native Library not found
  -> missing Program/runtimes or native Whisper DLLs

TOO_SHORT
  -> recording was shorter than approximately 250 ms

WebView2 missing
  -> run Install-Requirements.bat or install Microsoft Edge WebView2 Runtime

No visible early overlay
  -> verify pendingCompactMode and initial frontend /api/state handling
```
