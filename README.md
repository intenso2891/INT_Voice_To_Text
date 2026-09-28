# 🎙️ INT VoiceToText

> Говори — текст сам появится там, где нужен.

Офлайн-приложение для Windows: нажал глобальную горячую клавишу → сказал → распознанный текст автоматически в буфере обмена (и при желании вставлен `Ctrl+V` в любое окно). Работает полностью локально — голос никуда не отправляется.

![Version](https://img.shields.io/badge/версия-1.6.23-8b7cff?style=for-the-badge&labelColor=0d1221)
![Platform](https://img.shields.io/badge/Windows-10%2F11-5ee7d1?style=for-the-badge&labelColor=0d1221)
![Offline](https://img.shields.io/badge/работает-офлайн-63e6a6?style=for-the-badge&labelColor=0d1221)
![Made by](https://img.shields.io/badge/Created%20by-INTENSO.Dev-ff6680?style=for-the-badge&labelColor=0d1221)

---

## ✨ Возможности

| | |
|---|---|
| 🎙️ **Полностью офлайн** | Локальный [Whisper](https://github.com/sandrohanea/whisper.net) (Large V3), голос не покидает компьютер |
| ⌨️ **Глобальный хоткей** | Настраиваемая комбинация, работает из любой игры или программы |
| 🪟 **Оверлей** | Маленькое окно в правом верхнем углу: эквалайзер при записи → «Готово, можно вставлять» → само исчезает через 3 секунды |
| 🎨 **Трей** | Сворачивается в трей при закрытии; иконка синяя — готовность, зелёная — идёт запись |
| 🌐 **RU / EN** | Распознавание и интерфейс на двух языках |
| 🗣️ **Словарь исправлений** | Русское произношение превращается в латиницу (`дейз → DayZ`, `дейзавр → DayZavr`); редактируется без перекомпиляции |
| 🚀 **CPU / GPU** | Выбор устройства распознавания: CPU, Vulkan (AMD) или CUDA (NVIDIA) |
| 🔄 **Автообновление** | При запуске проверяет GitHub Releases; при наличии новой версии предлагает обновить одним кликом |
| 📦 **Модели** | Выбор модели (Tiny → Large V3), скачивание с зеркал, удаление |

---

## 📁 Структура репозитория

```text
INT_Voice_To_Text/
├── Program_Source/   ← исходный код и сборка
├── README.md         ← этот файл
├── DEVROAD.md        ← техническая документация для разработки
└── .gitignore
```

Готовая программа `Program` (~1.4 ГБ с моделью) **не хранится в репозитории** — она распространяется через [Releases](#-releases).

---

## 🚀 Быстрый старт

1. Скачайте последнюю версию в разделе **Releases**.
2. Распакуйте архив — внутри папка `Program`.
3. Запустите `INT_VoiceToText.exe`.
4. Выберите модель и дождитесь загрузки.
5. Нажмите горячую клавишу (по умолчанию `Alt+Win+Z`), скажите фразу.
6. Готово — текст уже в буфере обмена, вставьте `Ctrl+V` в любое место.

> [!NOTE]
> Требуется Windows 10/11 и установленный **WebView2 Runtime** (обычно уже есть).
>
> Запустите **Install-Requirements.bat** для автоматической установки всех зависимостей.

---

## 💻 Системные требования

### Обязательно (для всех режимов)
| Компонент | Требование |
|-----------|-----------|
| ОС | Windows 10 / 11 (x64) |
| [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) | Уже установлен в Windows 11; для Windows 10 — скачать |
| [Microsoft Visual C++ Redistributable 2022](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist) (x64) | Уже установлен у большинства; если нет — скачать |
| Оперативная память | от 4 ГБ (модель Tiny) до 8 ГБ (Large V3) |

> [!TIP]
> Запустите `Install-Requirements.bat` — он автоматически проверит и установит WebView2, VC++ Redistributable и CUDA Toolkit (если нужно).

### Режимы распознавания
| Режим | Для кого | Скорость | Статус |
|-------|----------|----------|--------|
| **CPU** | Все ПК (безопасно) | Медленнее | ✅ Стабильно |
| **GPU — Vulkan** | AMD / Intel | Быстро | ✅ Стабильно |
| **GPU — NVIDIA (CUDA)** | GeForce / RTX | Быстро | ⚠️ Требует CUDA Toolkit |
| **CPU + GPU — Vulkan** | Гибридный режим | Средне | ✅ Стабильно |
| **CPU + GPU — NVIDIA** | Гибридный режим | Средне | ⚠️ Требует CUDA Toolkit |

### Для GPU — NVIDIA (CUDA)
| Компонент | Требование |
|-----------|-----------|
| Видеокарта | NVIDIA с поддержкой CUDA (GeForce GTX 10xx и новее) |
| Драйвер NVIDIA | Последний с сайта nvidia.ru |
| [CUDA Toolkit 12.4+](https://developer.nvidia.com/cuda-downloads) | **Обязательно** — скачать и установить |

При первом выборе NVIDIA режима программа предложит скачать CUDA DLL (538 МБ) автоматически.

> [!IMPORTANT]
> Без CUDA Toolkit режим **NVIDIA (CUDA)** не будет работать. Если CUDA Toolkit не установлен — выберите **CPU** в настройках.

---

## 🛠️ Сборка из исходников

Требуется: [.NET SDK](https://dotnet.microsoft.com/) 9+, [Node.js](https://nodejs.org/) + npm.

```powershell
$env:DOTNET_ROLL_FORWARD = "LatestMajor"

# 1. Интерфейс (React)
Set-Location .\Program_Source\frontend
npm install
npm run build

# 2. Приложение
Set-Location ..
dotnet publish -c Release -r win-x64 --self-contained true `
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true `
  -o ..\Program
```

Подробнее — в [DEVROAD.md](DEVROAD.md).

---

## 📦 Releases

Релизы собираются автоматически через [GitHub Actions](.github/workflows/release.yml). Чтобы выпустить новую версию:

```powershell
git add .
git commit -m "Release v1.6.24"
git tag v1.6.24
git push origin main --tags
```

```text
v1.6.23
└── INT_VoiceToText-v1.6.23.zip   (~172 МБ, без модели)
```

Модель Large V3 (~3 ГБ) не входит в архив — выберите и скачайте её в настройках программы.

---

## 🧰 Технологии

| Технология | Назначение |
|---|---|
| [.NET 9](https://dotnet.microsoft.com/) + C# | Бэкенд, аудио, Win32 |
| [Photino.NET](https://github.com/tryphotino/photino.NET) | Нативное WebView2-окно |
| [Whisper.net](https://github.com/sandrohanea/whisper.net) | Локальное распознавание речи (CPU / CUDA / Vulkan) |
| [NAudio](https://github.com/naudio/NAudio) | Захват микрофона |
| [React](https://react.dev/) + TypeScript | Интерфейс |

---

## 📄 Лицензия

Создано с ❤️ **INTENSO.Dev**
