# Дорожная карта переноса PISMO на Linux

Цель — полный паритет функций с Windows-версией **без урезки**. Ниже — что уже
сделано, что осталось, и как именно каждая Windows-зависимость заменяется на Linux.

## Принцип

Windows-версия завязана на 4 подсистемы, которых нет на Linux:

| Подсистема | Windows | Замена на Linux | Статус |
|---|---|---|---|
| UI | WinForms | **Avalonia UI** (тот же C#, кроссплатформенно) | ✅ готово |
| Транспорт звонка | WebRTC (DataChannel) | **SIPSorcery** (чистый C#) — уже был в Windows-версии (`CallTransport.cs`) | ✅ перенесён |
| Аудио звонка | **NAudio** | **ALSA** (`arecord`/`aplay`) через `IAudioDevice` | ✅ голос готов |
| Видео/экран в звонке | WebView2 video-track | SIPSorcery media + V4L2/FFmpeg | 🚧 этап видео |
| Камера (кружки) | **AForge.Video.DirectShow** | **V4L2** (Linux) / FFmpeg | 🚧 |

> Примечание: изначально планировался CEF (как хост браузерного WebRTC), но
> оказалось, что аудиозвонок в Windows-версии идёт **не** через медиа-треки
> WebView2, а бинарными PCM-кадрами по **DataChannel** поверх SIPSorcery
> (`CallTransport.cs`). Значит браузер не нужен вовсе: SIPSorcery — чистый C#,
> ставится из NuGet, кроссплатформенный. Это и надёжнее, и легче CEF.

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

## Этап 1 — Голосовые звонки ✅ (готово)

- [x] Перенос `CallTransport` (SIPSorcery, DataChannel) → `PISMO.Core/Call`
- [x] Сигналинг через БД `call_sessions` (`CallSignaling`): offer/answer/ICE,
      статусы, детект входящих — протокол 1:1 с Windows-версией
- [x] Схема БД звонков — `db/pismo_calls_migration.sql`
- [x] Аудио на Linux — `AlsaAudioDevice` (ALSA `arecord`/`aplay`, PCM 16кГц/моно)
- [x] `Views/CallWindow` — исходящий/входящий звонок, mute, завершение
- [x] `Views/IncomingCallWindow` — приём/отклонение
- [x] Кнопки 📞/📹 в чате и опрос входящих звонков в `MainWindow`
- [x] STUN в ICE-конфиге; TURN добавляется только если он включён и задан
      адрес. TURN-сервера у проекта нет, и раньше relay прописывался всегда —
      каждый звонок ждал ответа от машины, где на 3478 никто не слушает.

**Нужно проверить на реальных устройствах** (в этом окружении нет 2 пиров и
микрофона): установить `alsa-utils`, выполнить обе миграции БД, запустить на двух
машинах и позвонить. Формат аудио и сигналинг совместимы с Windows-клиентами.

## Этап видео — камера/экран/плитка ✅ (Linux ↔ Linux)

- [x] Захват камеры (v4l2) и экрана (x11grab) через `ffmpeg` → MJPEG → JPEG-кадры
      (`FfmpegVideoSource`), кадр ужат под лимит одного пакета DataChannel
- [x] Передача видео/экрана типами `TypeVideo`/`TypeScreen` по тому же транспорту,
      что и голос — видео физически не влияет на аудио
- [x] «Плитка» тайлов в `CallWindow`: своя камера, камера и экран собеседника;
      декод JPEG → Avalonia Bitmap
- [x] Кнопки камеры/экрана, изоляция ошибок (нет камеры/ffmpeg → откат на голос)
- [x] Захват (ffmpeg→MJPEG→кадры) проверен на реальном X-дисплее (валидные JPEG)

Осталось для видео:
- Совместимость видео с **текущим Windows-клиентом** (там видео на настоящих
  WebRTC-медиа-треках WebView2, а не JPEG-по-DataChannel). Варианты: добавить
  media-track путь на SIPSorcery, либо обновить Windows-клиент на приём кадров.
- Wayland без XWayland — нативный захват через PipeWire.
- Чанкинг кадров > 60 КБ (сейчас держим размер малым качеством/масштабом).

## Этап 2 — Голосовые сообщения и видео-кружки 🚧

- Переиспользовать `IAudioDevice` для записи/воспроизведения голосовых.
- Реализовать `ICameraDevice` (JPEG-кадры) через V4L2 / FFmpeg.
- Перенести `VideoCircleRecordForm`, `VideoCirclePlayer`, `VideoCircleCodec`,
  `MediaCache` → Avalonia + эти интерфейсы (кодек кадров кроссплатформенный).

## Этап 3 — Группы и расширенные сообщения 🚧

- Групповые чаты: `CreateGroupForm`, `GroupMembersForm` → Avalonia; SQL уже есть в `MainForm`.
- Схема v2 (`pismo_v2_migration.sql`): файлы, ответы (`reply_to_id`), редактирование
  (`edited_at`), удаление (`is_deleted`), блокировки, отдельные таблицы вложений.
  Расширить `MessageService` этими колонками и перенести `MainForm_MessageActions`.

## Шифрование сообщений ✅

- [x] `PISMO.Core/Crypto.cs` — перенесён дословно из Windows-версии:
      AES-256-GCM (`enc:v2:`) на запись, чтение ещё и старого `enc:v1:`
      (AES-CBC). Ключ общий с ПК и Android — иначе клиенты не читают
      переписку друг друга.
- [x] `MessageService`: шифруем на отправке, расшифровываем в переписке и в
      превью списка диалогов.

## Этап 4 — Полировка 🚧

- Системный трей и уведомления (Avalonia `TrayIcon` — кроссплатформенно).
- [x] CI-сборка на каждый пуш (`.github/workflows/build.yml`) и выпуск
      релиза кнопкой (`release.yml`): самодостаточный архив со средой .NET
      внутри, ставить её отдельно не нужно.
- [x] Автообновление (`PISMO.Core/Updater.cs`): при запуске тихо спрашивает
      GitHub про свежий релиз, предлагает обновиться, скачивает `tar.gz` и
      перезапускается. Установку из пакета не трогает.
- Упаковка в AppImage / Flatpak / `.deb` — архива хватает для запуска,
  но не даёт значка в меню.
- Сборка под Windows/macOS из того же кода.

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
