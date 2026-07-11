# Дорожная карта переноса PISMO на Linux

Цель — полный паритет функций с Windows-версией **без урезки**. Ниже — что уже
сделано, что осталось, и как именно каждая Windows-зависимость заменяется на Linux.

## Принцип

Windows-версия завязана на 4 подсистемы, которых нет на Linux:

| Подсистема | Windows | Замена на Linux | Статус |
|---|---|---|---|
| UI | WinForms | **Avalonia UI** (тот же C#, кроссплатформенно) | ✅ ядро готово |
| Движок звонков | **WebView2** (JS `RTCPeerConnection`) | **CEF** (Chromium Embedded Framework) — тот же браузерный движок, тот же JS-код | 🚧 этап 1 |
| Аудио (запись/воспроизведение) | **NAudio** | **PortAudio** / **FFmpeg** / OpenAL | 🚧 этап 2 |
| Камера | **AForge.Video.DirectShow** | **V4L2** (Linux) / SIPSorcery capture | 🚧 этап 2 |

Кроссплатформенное переиспользуется как есть: **MySql.Data**, **SIPSorcery**,
`DBHelper`, `UserSession`, `TurnSettings`, вся SQL-логика.

---

## Этап 0 — фундамент ✅ (готово)

- [x] Кроссплатформенный солюшн (`PISMO.Core` + `PISMO.Desktop` на Avalonia)
- [x] Перенос ядра: `DBHelper`, `UserSession`, `TurnSettings`
- [x] Сервисы данных: `AuthService`, `MessageService` (SQL 1:1 из `MainForm`)
- [x] UI: вход, регистрация, смена пароля, главное окно (диалоги + переписка), настройки
- [x] Личные сообщения: текст + изображения, поллинг, непрочитанные, admin-режим
- [x] Интерфейсы платформенных сервисов (`ICallEngine`, `IAudioDevice`, `ICameraDevice`)
- [x] Сборка и запуск проверены на Linux (.NET 8 + Avalonia 11, X11/Wayland)

---

## Этап 1 — Звонки (аудио/видео/экран) 🚧

Это ключевая фича. В Windows звонок целиком работает **внутри WebView2**: C# отдаёт
HTML-страницу с `RTCPeerConnection` / `getUserMedia` / `getDisplayMedia`, а обмен
SDP/ICE идёт через `WebMessageReceived` ↔ `ExecuteScriptAsync`. Сигналинг между
пользователями — через локальный WebSocket-сервер + запись SDP в БД.

**План на Linux — сохранить ровно тот же JS-движок, заменив только хост браузера:**

1. Подключить кроссплатформенный CEF-контрол для Avalonia:
   - [`CefNet`](https://github.com/CefNet/CefNet) или
     [`WebViewControl-Avalonia`](https://github.com/OutSystems/WebView) (CEF под капотом).
2. Реализовать `PISMO.Platform.ICallEngine` поверх CEF:
   - загрузка того же HTML (перенести `BuildHtml()` из `WebRtcTransport.cs`);
   - мост C# ↔ JS (`CefV8` / `PostMessage`) вместо `CoreWebView2.WebMessageReceived`;
   - выдача разрешений камера/микрофон (в CEF — через `OnRequestMediaAccessPermission`).
3. Перенести сигналинг:
   - `WebSocketSignalingServer` / `WebSocketSignalingClient` (чистый C#, `System.Net.WebSockets` — переносятся почти как есть);
   - хранение/обмен offer/answer/ICE через БД (`CallSessionInfo`, таблицы звонков).
4. Перенести UI звонка `CallForm` → `Views/CallWindow` (Avalonia).
5. Зарегистрировать фабрику: `PlatformServices.CallEngineFactory = () => new CefCallEngine();`

После этого кнопки 📞/📹 в `MainWindow` откроют окно звонка (сейчас показывают статус
«на подходе»). TURN/STUN уже настраиваются в окне настроек и хранятся в `TurnSettings`.

## Этап 2 — Голосовые сообщения и видео-кружки 🚧

- Реализовать `IAudioDevice` (запись/воспроизведение WAV) на PortAudio или FFmpeg.
- Реализовать `ICameraDevice` (JPEG-кадры) через V4L2 / FFmpeg.
- Перенести `VideoCircleRecordForm`, `VideoCirclePlayer`, `VideoCircleCodec`,
  `MediaCache` → Avalonia + эти интерфейсы (кодек кадров кроссплатформенный).

## Этап 3 — Группы и расширенные сообщения 🚧

- Групповые чаты: `CreateGroupForm`, `GroupMembersForm` → Avalonia; SQL уже есть в `MainForm`.
- Схема v2 (`pismo_v2_migration.sql`): файлы, ответы (`reply_to_id`), редактирование
  (`edited_at`), удаление (`is_deleted`), блокировки, отдельные таблицы вложений.
  Расширить `MessageService` этими колонками и перенести `MainForm_MessageActions`.

## Этап 4 — Полировка 🚧

- Системный трей и уведомления (Avalonia `TrayIcon` — кроссплатформенно).
- Упаковка: AppImage / Flatpak / `.deb` для удобной установки на Linux.
- CI-сборка под Linux/Windows/macOS из одного кода.

---

## Проверка сборки

```bash
dotnet build            # весь солюшн
dotnet run --project src/PISMO.Desktop
```

Заметки по окружению для разработки без физического дисплея (headless-проверка):

```bash
xvfb-run -a dotnet run --project src/PISMO.Desktop
```
