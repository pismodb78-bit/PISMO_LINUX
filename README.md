# PISMO для Linux

Кроссплатформенный порт мессенджера **PISMO** (Discord-стиль) с Windows на Linux.
Исходная версия написана на **.NET 8 + WinForms** и работает только на Windows.
Эта версия переписана на **[Avalonia UI](https://avaloniaui.net/)** и запускается на
**Linux (CachyOS, Ubuntu, Arch, Fedora …), Windows и macOS** из одного кода.

> Цель порта — адаптировать приложение под Linux **без урезки функций**.
> Платформенно-зависимые подсистемы (звонки, кружки, аудио) выносятся за
> интерфейсы и подключаются поэтапно — см. [`docs/ROADMAP.md`](docs/ROADMAP.md).
> Ничего не выбрасывается — переносится.

---

## Что уже работает на Linux

- ✅ **Вход / регистрация / смена пароля** (та же схема `bdauth`, те же SQL-запросы)
- ✅ **Список диалогов** с превью последнего сообщения и бейджем непрочитанных
- ✅ **Личная переписка**: текст + изображения (отправка через 📎, просмотр в пузырьке)
- ✅ **Разделители по датам**, автопрокрутка, Enter — отправить, Shift+Enter — новая строка
- ✅ **Поллинг** новых сообщений в реальном времени (как в Windows-версии)
- ✅ **Admin-режим**: список всех пользователей и «войти за пользователя» (ПКМ по карточке)
- ✅ **Настройки**: строка подключения к БД (`ip.txt`) и параметры TURN/STUN
- ✅ **Discord-тема**: тёмный фон, blurple-кнопки, аватары с буквами, скруглённые пузырьки

## В работе (следующие этапы, код-«посадочные места» уже готовы)

- 🚧 **Звонки** (аудио/видео/демонстрация экрана) — через браузерный движок **CEF**
  (тот же WebRTC, что и в Windows-версии на WebView2). Интерфейс `ICallEngine` готов.
- 🚧 **Групповые чаты**
- 🚧 **Голосовые сообщения** и **видео-кружки** (интерфейсы `IAudioDevice` / `ICameraDevice`)
- 🚧 **Расширенные сообщения**: файлы, ответы, редактирование/удаление, блокировки (схема v2)

Подробный план и архитектура — [`docs/ROADMAP.md`](docs/ROADMAP.md).

---

## Установка и запуск на Linux

### 1. Установить .NET 8 SDK

```bash
# Ubuntu / Debian
sudo apt-get update && sudo apt-get install -y dotnet-sdk-8.0

# Arch / CachyOS
sudo pacman -S dotnet-sdk

# Fedora
sudo dnf install dotnet-sdk-8.0
```

### 2. Настроить подключение к MySQL

База та же, что и у Windows-версии (`bdauth`). Если ещё не создавали таблицы —
выполните `db/pismo_messenger_migration.sql`.

Отредактируйте `src/PISMO.Desktop/ip.txt`:

```
server=192.168.0.15;port=3306;uid=user1;password=ваш_пароль;database=bdauth
```

> Параметр должен быть `uid=`, а не `username=` (требование ConnectorNet).
> Строку подключения можно менять и в самом приложении: ⚙ → «Подключение к базе данных».

### 3. Собрать и запустить

```bash
dotnet run --project src/PISMO.Desktop
```

или собрать релиз:

```bash
dotnet build -c Release
./src/PISMO.Desktop/bin/Release/net8.0/PISMO
```

### Зависимости рантайма для GUI на Linux

Avalonia использует X11 (или Wayland через XWayland). На «голом» сервере поставьте
базовые библиотеки: `libx11`, `libice`, `libsm`, `libfontconfig`, `libicu`.
На обычном десктопе (CachyOS/Ubuntu с рабочим столом) всё уже есть.

---

## Структура

```
PISMO_LINUX/
├── PISMO.sln
├── db/
│   └── pismo_messenger_migration.sql   — схема messages (совместима с bdauth)
├── docs/
│   └── ROADMAP.md                      — план переноса оставшихся функций
└── src/
    ├── PISMO.Core/                     — кроссплатформенная логика (без UI)
    │   ├── DBHelper.cs                 — подключение к MySQL (перенос как есть)
    │   ├── UserSession.cs              — сессия пользователя (перенос как есть)
    │   ├── TurnSettings.cs             — TURN/STUN креды (перенос как есть)
    │   ├── Models/                     — модели сообщений/диалогов
    │   ├── Data/                       — AuthService, MessageService (SQL из MainForm)
    │   └── Platform/                   — интерфейсы звонков/аудио/камеры (ICallEngine …)
    └── PISMO.Desktop/                  — Avalonia UI
        ├── App.axaml                   — Discord-тема
        └── Views/                      — Login / Register / ChangePassword / Main / Settings
```

## Как соотносится с Windows-версией

| Windows (WinForms)            | Linux (Avalonia)                    |
|-------------------------------|-------------------------------------|
| `LoginForm`                   | `Views/LoginWindow`                 |
| `RegisterForm`                | `Views/RegisterWindow`              |
| `ChangePasswordForm`          | `Views/ChangePasswordWindow`        |
| `MainForm` (чат)              | `Views/MainWindow`                  |
| `SettingsForm`                | `Views/SettingsWindow`              |
| `DBHelper` / `UserSession` / `TurnSettings` | перенесены как есть в `PISMO.Core` |
| SQL из `MainForm`             | `PISMO.Core/Data/MessageService`    |
| `WebRtcTransport` (WebView2)  | `ICallEngine` → реализация на CEF (этап 1) |
| `NAudio` / `AForge`           | `IAudioDevice` / `ICameraDevice` (этап 2)  |
