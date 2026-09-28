# DEVROAD — Техническая документация для разработки INT VoiceToText

> **Цель этого файла:** передать весь контекст проекта новому разработчику / нейросети без потерь.
> Последнее обновление: v1.6.23 (2026-09-28).

---

## 1. Обзор проекта

**INT VoiceToText** — офлайн-приложение для Windows (диктофон → текст):
- Глобальная горячая клавиша (по умолчанию `Alt+Win+Z`) → запись с микрофона → локальное распознавание Whisper → текст в буфере обмена + опциональный автовставка (`Ctrl+V`)
- Полностью офлайн, голос не отправляется в сеть
- Photino.NET окно + React UI + SignalR для real-time событий
- .NET 9, single-file publish, WinForms tray

---

## 2. Структура файлов

```
INT_Voice_To_Text/
├── Program/                    ← ГОТОВАЯ ПРОГРАММА (НЕ в git, ~1.4 ГБ с моделью)
│   ├── INT_VoiceToText.exe     ← основной exe (single-file)
│   ├── models/
│   │   └── ggml-large-v3.bin   ← модель Whisper (2.95 ГБ) — НЕ УДАЛЯТЬ!
│   ├── runtimes/win-x64/       ← нативные DLL Whisper
│   │   ├── whisper.dll         (0.5 МБ)
│   │   ├── ggml-whisper.dll    (0.1 МБ)
│   │   ├── ggml-base-whisper.dll (0.6 МБ)
│   │   ├── ggml-cpu-whisper.dll  (0.7 МБ)
│   │   └── ggml-vulkan-whisper.dll (55 МБ)
│   ├── cuda/
│   │   └── ggml-cuda-whisper.dll (538 МБ) — копируется в runtimes ТОЛЬКО при NVIDIA режиме
│   ├── library_words/words.txt
│   ├── Install-Requirements.bat / .ps1
│   └── int_voicetext_debug.log ← debug лог
│
├── Program_Source/             ← ИСХОДНИКИ (в git)
│   ├── INT_VoiceToText.csproj  ← .NET проект
│   ├── Program.cs              ← точка входа + API endpoints + Photino окно + трей
│   ├── Services/
│   │   ├── AppState.cs         ← настройки + версия (AppVersion)
│   │   ├── SpeechService.cs    ← ЗАПИСЬ + РАСПОЗНАВАНИЕ + DLL deployment (КРИТИЧЕСКИЙ ФАЙЛ)
│   │   ├── VoiceEngine.cs      ← оркестратор (SignalR + hotkey + pipeline)
│   │   ├── AudioRecorder.cs    ← NAudio WaveInEvent запись
│   │   ├── HotkeyService.cs    ← RegisterHotKey глобальный хоткей
│   │   ├── ClipboardService.cs ← буфер обмена + автовставка
│   │   ├── ModelDownloader.cs  ← скачивание моделей (3 зеркала)
│   │   ├── UpdateService.cs    ← автообновление с GitHub Releases
│   │   └── ...
│   ├── frontend/               ← React + TypeScript UI
│   │   └── src/
│   │       ├── App.tsx         ← основной компонент
│   │       ├── i18n.ts         ← переводы RU/EN
│   │       └── styles.css      ← стили
│   ├── Updater/Program.cs      ← отдельный updater exe
│   ├── Install-Requirements.ps1 ← автоустановка WebView2 + VC++ + CUDA
│   └── wwwroot/                ← собранный фронтенд (из npm run build)
│
├── .github/workflows/release.yml ← CI/CD сборка + публикация релиза
├── README.md                   ← пользовательская документация
└── DEVROAD.md                  ← ЭТОТ ФАЙЛ
```

---

## 3. КРИТИЧЕСКИЕ ЗАМЕЧАНИЯ

### 3.1. НЕ УДАЛЯТЬ `Program/models/ggml-large-v3.bin`
Модель Whisper Large V3 (2.95 ГБ). Пользователь просил **не удалять папку Program** при деплое — иначе придётся перекачивать модель. При публикации (`dotnet publish`) модель **не перезаписывается** — она остаётся.

### 3.2. Краш `c0000409` в `ucrtbase.dll` — ТЕКУЩАЯ ПРОБЛЕМА
**Симптом:** приложение падает через 1-2 секунды после остановки записи (начало распознавания). Код исключения `0xc0000409` (STATUS_STACK_BUFFER_OVERRUN), модуль `ucrtbase.dll`. Происходит **у NVIDIA пользователей даже в CPU режиме**.

**История попыток исправления:**
1. **v1.6.19** — работало стабильно (был `Whisper.net.AllRuntimes`, но CUDA DLL не копировался в temp)
2. **v1.6.20** — добавили CUDA DLL из `whisper.net.runtime.cuda12.windows` → начались краши
3. **v1.6.21** — добавили `IsCudaAvailable()` проверку → краш остался (DLL уже была на диске в `runtimes/win-x64/`)
4. **v1.6.22** — перенесли CUDA DLL в `cuda/` папку, убираем из `runtimes/win-x64/` → **краш всё равно есть**
5. **v1.6.23** — добавили авто-скачивание CUDA DLL → **краш всё равно есть**

**Корень проблемы (по анализу):**
- `Whisper.net.NativeLibraryLoader` сканирует `runtimes/win-x64/` и пытается загрузить **ВСЕ** DLL из этой папки
- Даже если CUDA DLL **не** в `runtimes/win-x64/`, **Vulkan DLL** (`ggml-vulkan-whisper.dll`, 55 МБ) **есть** там
- У NVIDIA пользователей Vulkan driver нестабилен → `ggml-vulkan-whisper.dll` крашится при загрузке
- Краш происходит **до** того как .NET успевает обработать исключение — это нативный fail-fast

**Что НЕ пробовали:**
- Полностью убрать `ggml-vulkan-whisper.dll` из сборки (оставить только CPU)
- Использовать `Whisper.net.Runtime.NoAvx` вместо `Whisper.net.Runtime` (может помочь на старых CPU)
- Обновить Whisper.net до последней версии (сейчас `1.9.1`)
- Попробовать `whisper.net.runtime.cuda.windows` (CUDA 13) вместо `cuda12`
- Загружать WhisperFactory в отдельном процессе (изоляция краша)

**Гипотеза для следующего разработчика:**
> Краш `c0000409` скорее всего вызван **`ggml-vulkan-whisper.dll`**, а не CUDA. Этот DLL крашится на системах с NVIDIA драйверами (которые имеют свой Vulkan ICD). Решение: **полностью убрать GPU DLL из `runtimes/win-x64/`** — оставить ТОЛЬКО `whisper.dll`, `ggml-whisper.dll`, `ggml-base-whisper.dll`, `ggml-cpu-whisper.dll`. GPU поддержку добавить позже через изолированный процесс.

### 3.3. Whisper.net NativeLibraryLoader
Whisper.net ищет DLL в `runtimes/win-x64/` (НЕ `runtimes/win-x64/native/`). Пути поиска:
1. `AppContext.BaseDirectory` (temp распаковка single-file)
2. `ExeDir()` (директория реального exe)
3. `Assembly.Location`

Каждая DLL из `runtimes/win-x64/` будет загружена. Если зависимость отсутствует → нативный краш `c0000409`.

### 3.4. Single-file publish
`PublishSingleFile=true` + `IncludeNativeLibrariesForSelfExtract=true` → .NET распаковывает DLL во временную папку `%TEMP%\.net\INT_VoiceToText\<hash>\`. `AppContext.BaseDirectory` указывает на эту временную папку.

---

## 4. Системы и архитектура

### 4.1. API Endpoints (Program.cs)
| Метод | Путь | Описание |
|-------|------|----------|
| GET | `/api/state` | Полное состояние (status, settings, model, errors) |
| POST | `/api/toggle` | Начать/остановить запись (как хоткей) |
| POST | `/api/settings` | Сохранить настройки (hotkey, language, computeMode и т.д.) |
| GET | `/api/models/catalog` | Список моделей (installed/active) |
| POST | `/api/models/select` | Выбрать модель |
| POST | `/api/models/download` | Скачать модель (fire-and-forget) |
| POST | `/api/models/delete` | Удалить все модели |
| GET | `/api/cuda/status` | Статус CUDA DLL |
| POST | `/api/cuda/download` | Скачать CUDA DLL (538 МБ) |
| GET | `/api/debug/log` | Debug лог |
| POST | `/api/debug/clear` | Очистить debug лог |
| GET | `/api/runtime/status` | Диагностика нативных библиотек |
| POST | `/api/runtime/repair` | Пересоздать runtime DLL |
| GET | `/api/update/check` | Проверить обновления |
| POST | `/api/update/install` | Установить обновление |

### 4.2. SignalR события
`onState` / `onResult` / `onError` / `onLevel` / `onDebug` / `onUpdate` / `onModelProgress` / `onUpdateProgress`

### 4.3. ComputeMode (режимы распознавания)
```
cpu            → только CPU (безопасно, работает везде)
gpu-vulkan     → GPU Vulkan (AMD/Intel)
gpu-nvidia     → GPU CUDA (NVIDIA)
hybrid-vulkan  → CPU + GPU Vulkan
hybrid-nvidia  → CPU + GPU NVIDIA
```

Хранится в `%LOCALAPPDATA%\INT_VoiceToText\settings.json` → `ComputeMode`.

### 4.4. DeployNativeLibraries (SpeechService.cs)
При каждом распознавании:
1. Удаляет GPU DLL (`ggml-cuda-whisper.dll`, `ggml-vulkan-whisper.dll`) из **всех** путей
2. Копирует нужные DLL в `runtimes/win-x64/` в зависимости от режима
3. CUDA DLL копируется из `cuda/` (если NVIDIA режим)

**ЭТО ГЛАВНОЕ МЕСТО ДЛЯ ИСПРАВЛЕНИЯ КРАША.**

### 4.5. Модели Whisper
| ID | Размер | Файл |
|----|--------|------|
| tiny | 75 МБ | ggml-tiny.bin |
| base | 148 МБ | ggml-base.bin |
| small | 488 МБ | ggml-small.bin |
| medium | 1.5 ГБ | ggml-medium.bin |
| large-v3 | 2.95 ГБ | ggml-large-v3.bin |

Модели скачиваются с 3 зеркал (huggingface.co, hf-mirror.com, mirror.ghproxy.com) с 10-секундным stall detection. Ручное скачивание — URL + путь.

---

## 5. Сборка и публикация

### 5.1. Локальная сборка
```powershell
# 1. Фронтенд
cd Program_Source\frontend
npm ci
npm run build          # → Program_Source/wwwroot/

# 2. Основное приложение
cd ..
$env:DOTNET_ROLL_FORWARD = "LatestMajor"
dotnet publish INT_VoiceToText.csproj -c Release -r win-x64 `
  --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true `
  -o ..\Program

# 3. Не забыть скопировать:
#    - models/ggml-large-v3.bin (НЕ перезаписывать!)
#    - runtimes/win-x64/*.dll (whisper + ggml)
#    - cuda/ggml-cuda-whisper.dll (если нужен NVIDIA)
#    - library_words/words.txt
#    - Install-Requirements.*
#    - icon.ico, tray-blue.ico, tray-green.ico
```

### 5.2. CI/CD (GitHub Actions)
`.github/workflows/release.yml` — автоматически при push тега `v*`:
1. Node.js → `npm ci && npm run build`
2. .NET → `dotnet publish`
3. Updater → `dotnet publish`
4. Сборка `Program/` папки
5. Проверка критических DLL
6. ZIP архив (~172 МБ без модели)
7. GitHub Release

### 5.3. Публикация
```powershell
git add Program_Source .github
git commit -m "v1.6.24: описание изменений"
git push origin main
git tag v1.6.24
git push origin v1.6.24
# → GitHub Actions соберёт и опубликует релиз
```

---

## 6. Версионирование

`AppState.cs` → `AppVersion = "1.6.23"` — должно совпадать с git тегом.

| Версия | Изменения |
|--------|-----------|
| 1.6.19 | Стабильная (CPU + Vulkan) |
| 1.6.20 | Добавлена CUDA → НАЧАЛИСЬ КРАШИ |
| 1.6.21 | IsCudaAvailable проверка → краш остался |
| 1.6.22 | CUDA DLL в cuda/ → краш остался |
| 1.6.23 | Авто-скачивание CUDA DLL → краш остался |
| **1.6.24** | **??? НУЖНО ИСПРАВИТЬ КРАШ** |

---

## 7. Пользовательский интерфейс (React)

- `App.tsx` — основной компонент (~870 строк)
- `i18n.ts` — переводы RU/EN
- `styles.css` — стили
- SignalR подключение → `/hub`
- Overlay окно (запись/эквалайзер) в правом верхнем углу
- Трей (NotifyIcon + ContextMenuStrip), цветные иконки

### Ключевые компоненты UI
- `ModelCatalog` — выбор моделей с кнопками "Скачать"/"Выбрать"/"Скачать заново"
- `CudaDownloadPanel` — скачивание CUDA DLL при выборе NVIDIA
- `RecordingIndicator` — таймер + эквалайзер записи
- `HotkeyField` — настройка горячей клавиши
- `HistoryPanel` — история распознаваний

---

## 8. Тестирование

### Ручное тестирование
1. Запустить `INT_VoiceToText.exe`
2. Нажать `Alt+Win+Z` (или настроенную клавишу)
3. Говорить 3-5 секунд
4. Отпустить клавишу → подождать 2-10 секунд
5. Текст появится в буфере обмена

### API тестирование
```powershell
# Запуск → узнать port из int_voicetext_debug.log
$port = 63212  # заменить на реальный

# Состояние
Invoke-RestMethod "http://127.0.0.1:$port/api/state"

# Запись
Invoke-RestMethod "http://127.0.0.1:$port/api/toggle" -Method Post
Start-Sleep 5
Invoke-RestMethod "http://127.0.0.1:$port/api/toggle" -Method Post

# Проверить что процесс жив
Get-Process INT_VoiceToText
```

---

## 9. Чек-лист для нового разработчика

- [ ] **ИСПРАВИТЬ КРАШ `c0000409`** — главная задача
  - [ ] Попробовать полностью убрать `ggml-vulkan-whisper.dll` из `runtimes/win-x64/`
  - [ ] Попробовать `Whisper.net.Runtime.NoAvx` вместо `Whisper.net.Runtime`
  - [ ] Обновить Whisper.net до последней версии
  - [ ] Рассмотреть загрузку WhisperFactory в отдельном процессе
  - [ ] Протестировать на чистой Windows 10/11 с NVIDIA
- [ ] Убедиться что `Program/models/` не удаляется при деплое
- [ ] Проверить работу CPU + Vulkan режимов (должны быть стабильны)
- [ ] Обновить README.md (версия, статус GPU)
- [ ] Протестировать авто-скачивание CUDA DLL
- [ ] Проверить автообновление (UpdateService)

---

## 10. Контакты и ссылки

- **GitHub:** https://github.com/intenso2891/INT_Voice_To_Text
- **Ветка:** `main`
- **Текущая версия:** 1.6.23
- **Автор:** INTENSO.Dev
- **Whisper.net:** https://github.com/sandrohanea/whisper.net
- **Photino.NET:** https://github.com/tryphotino/photino.NET

---

*Файл создан для передачи контекста разработки. Обновлять при каждом крупном изменении.*
