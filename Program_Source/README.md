# INT VoiceToText

Актуальная версия: **1.6.2**.

Офлайн-приложение для Windows: глобальная горячая клавиша → запись микрофона → локальный Whisper Large V3 → текст в буфер обмена → при необходимости автоматическая вставка `Ctrl+V`.

## Структура переносимого релиза

```text
INT_Voice_To_Text/
├── Program/
│   ├── INT_VoiceToText.exe       # готовое приложение
│   ├── models/                   # модели Whisper, переносить вместе с exe
│   ├── runtimes/                 # CPU/GPU native runtime DLL
│   └── *.dll                     # native DLL Whisper/Photino
└── Program_Source/
    ├── Program.cs
    ├── INT_VoiceToText.csproj
    ├── Services/                 # состояние, аудио, Whisper, hotkey, startup
    ├── Native/                   # Win32 clipboard/window/tray helpers
    ├── Hubs/                     # SignalR hub
    ├── frontend/                 # React + TypeScript интерфейс
    ├── icon.ico
    ├── tray-blue.ico              # трей: синяя — ожидание записи
    ├── tray-green.ico             # трей: зелёная — идёт запись
    ├── library_words/words.txt    # словарь замен после Whisper
    └── README.md
```

**Для переноса на другой ПК копируется целиком только папка `Program`.**

Требование Windows: установленный WebView2 Runtime. Интернет нужен только при первом скачивании отсутствующей модели; после скачивания распознавание выполняется локально.

## Модель Whisper

Используется `ggml-large-v3.bin`. При первом запуске/распознавании модель скачивается рядом с exe:

```text
Program\models\ggml-large-v3.bin.downloading
Program\models\ggml-large-v3.bin
```

После завершения временный файл переименовывается. Приложение сначала ищет Large V3 в `Program\models`, затем в `%LOCALAPPDATA%\INT_VoiceToText\models`.

Large V3 точнее base/small, но требует около 3 ГБ места и больше RAM/времени.

## Словарь исправлений (library_words)

Приложение поддерживает локальный словарь замен, применяемый к тексту сразу после Whisper, до буфера обмена и автоп вставки. Это помогает правильно писать слова, которые по-русски произносятся, а писать нужно латиницей (например `дейз → DayZ`, `дейзавр → DayZavr`).

Файл: `Program_Source/library_words/words.txt` (копируется в `Program/library_words/words.txt` при публикации — его можно редактировать и в собранной программе без перекомпиляции).

Формат строки — `вариант распознавания => правильное написание` (по одному на строку, `#` — комментарий). Замена чувствительна к границам слов, не ломает соседние символы. Логика — `Services/WordLibrary.cs`, вызов — в `VoiceEngine.RunTranscription`.

## Иконка в трее

- синий микрофон — ожидание/готовность к записи;
- зелёный микрофон — идёт запись.

Иконки `tray-blue.ico` и `tray-green.ico` должны лежать рядом с exe (копируются при publish / в `Program`).

## CPU/GPU

Настройка **«Устройство распознавания»**:

- `CPU` — максимальная совместимость;
- `GPU` — CUDA/Vulkan runtime, быстрее на совместимой видеокарте;
- `CPU + GPU` — режим GPU с возможностью CPU fallback на уровне native runtime.

Whisper.net подключён через `Whisper.net.AllRuntimes` 1.9.1. Выбранный режим передаётся в `WhisperFactoryOptions.UseGpu`, номер GPU — в `GpuDevice`.

Если GPU runtime или драйвер не поддерживается, в режиме GPU нужно смотреть `%LOCALAPPDATA%\INT_VoiceToText\debug.log`; для безопасного возврата выбрать CPU.

## Сборка

Требуется .NET SDK 9/10 и Node.js/npm.

```powershell
$env:DOTNET_ROLL_FORWARD="LatestMajor"
Set-Location .\Program_Source\frontend
npm install
npm run build
Set-Location ..
dotnet publish -c Release -r win-x64 --self-contained true `
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true `
  -o ..\Program
```

После publish native runtime DLL из `bin\Release\net9.0-windows\win-x64\runtimes` должны быть сохранены в `Program\runtimes`; модель — в `Program\models`.

`bin/`, `obj/`, `wwwroot/` и `frontend/node_modules/` — временные каталоги сборки. В Git их не загружать: они создаются командами выше.

## Архитектура

- `Program.cs` — ASP.NET loopback API, SignalR, Photino STA UI, системный трей.
- `Services/AppState.cs` — настройки `%LOCALAPPDATA%\INT_VoiceToText\settings.json`.
- `Services/AudioRecorder.cs` — NAudio WaveInEvent, mono 16 kHz, RMS/level.
- `Services/SpeechService.cs` — поиск/скачивание модели, `WhisperFactory`, CPU/GPU options.
- `Services/VoiceEngine.cs` — state machine Idle → Recording → Transcribing, clipboard и paste.
- `Services/StartupManager.cs` — автозапуск через HKCU Run.
- `Native/W32.cs` — Win32 clipboard, HWND resize/topmost/show, work area.
- `frontend/src/App.tsx` — интерфейс, история, настройки, компактный overlay.
- `frontend/src/lib/api.ts` — REST/SignalR API.

## API для тестов

Временный loopback URL записывается в `int_voicetext_debug.log` рядом с exe строкой `backend http://127.0.0.1:<port>/`.

- `GET /api/state`
- `POST /api/toggle`
- `POST /api/settings`
- `GET /api/debug/log`

## Правила поддержки проекта

1. Все изменения исходников делать в `Program_Source`, не в `Program`.
2. После изменения версии обновлять `AppState.AppVersion` и этот README.
3. После сборки заменять старый exe в `Program` и удалять старый exe перед копированием.
4. При переносе пользователю отдавать только `Program`.
5. Не удалять `Program_Source`: это каноническая папка разработки.
