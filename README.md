# 🎙️ INT VoiceToText

> Говори — текст сам появится там, где нужен.

Офлайн-приложение для Windows: нажал глобальную горячую клавишу → сказал → распознанный текст автоматически в буфере обмена (и при желании вставлен `Ctrl+V` в любое окно). Работает полностью локально — голос никуда не отправляется.

![Version](https://img.shields.io/badge/версия-1.6.4-8b7cff?style=for-the-badge&labelColor=0d1221)
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
| 🚀 **CPU / GPU** | Выбор устройства распознавания: CPU, GPU или CPU+GPU |
| 🔄 **Автообновление** | При запуске проверяет GitHub Releases; при наличии новой версии предлагает обновить одним кликом |

---

## 📁 Структура репозитория

```text
INT_Voice_To_Text/
├── Program_Source/   ← исходный код и сборка
├── README.md         ← этот файл
└── .gitignore
```

Готовая программа `Program` (~5 ГБ с моделями) **не хранится в репозитории** — она распространяется через [Releases](#-releases).

---

## 🚀 Быстрый старт

1. Скачайте последнюю версию в разделе **Releases**.
2. Распакуйте архив — внутри папка `Program`.
3. Запустите `INT_VoiceToText.exe`.
4. Нажмите горячую клавишу (по умолчанию `Ctrl+Win+Alt+A`), скажите фразу.
5. Готово — текст уже в буфере обмена, вставьте `Ctrl+V` в любое место.

> [!NOTE]
> Требуется Windows 10/11 и установленный **WebView2 Runtime** (обычно уже есть).
>
> При первом запуске программа один раз скачает модель Whisper Large V3 (~3 ГБ). После этого всё работает полностью офлайн.

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

> [!TIP]
> После публикации папки `bin/`, `obj/`, `wwwroot/` и `frontend/node_modules/` создаются автоматически и в Git не загружаются.

Подробная документация по архитектуре — в [Program_Source/README.md](Program_Source/README.md).

---

## 📦 Releases

Релизы собираются автоматически через [GitHub Actions](.github/workflows/release.yml). Чтобы выпустить новую версию:

```powershell
git add .
git commit -m "Release v1.5.3"
git tag v1.5.3
git push origin main --tags
```

После отправки тега GitHub соберёт `Program` из `Program_Source` и опубликует архив в разделе **Releases**. Готовые сборки не хранятся в обычных файлах репозитория — они слишком большие из-за native runtime.

Готовые сборки публикуются в разделе **Releases** — не в файлы репозитория (они слишком большие из-за моделей).

```text
v1.5.2
└── INT_VoiceToText-1.5.2.zip   (Program без модели Large V3)
```

Модель Large V3 (~3 ГБ) не входит в архив из-за ограничений GitHub — приложение скачивает её само при первом запуске.

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
