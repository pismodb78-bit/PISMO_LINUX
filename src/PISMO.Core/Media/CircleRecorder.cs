using System;
using System.Collections.Generic;
using PISMO.Platform;

namespace PISMO.Media
{
    /// <summary>
    /// Запись видео-кружка: камера через ffmpeg даёт JPEG-кадры, ALSA пишет
    /// звук, на выходе — контейнер PSMOVID1, который читает и Windows-версия.
    ///
    /// Звук и картинка пишутся ДВУМЯ независимыми процессами и синхронизации
    /// между ними нет. Для кружка на несколько секунд расхождение незаметно,
    /// и это сознательный размен: точная синхронизация потребовала бы
    /// мультиплексора (ffmpeg с двумя входами), а значит и другого формата на
    /// выходе — того, который Windows-клиент не прочитает.
    /// </summary>
    public sealed class CircleRecorder : IDisposable
    {
        /// <summary>Кадров в секунду. Больше делает кружок тяжелее без пользы:
        /// он маленький и смотрят его секунды.</summary>
        public const int Fps = 12;

        /// <summary>Предел записи. Кружок — это реплика, а не видеописьмо.</summary>
        public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(60);

        private readonly List<byte[]> _frames = new();
        private readonly object _lock = new();
        private FfmpegVideoSource _camera;
        private VoiceNote _voice;
        private DateTime _startedAt;

        public bool IsRecording { get; private set; }
        public TimeSpan Elapsed => IsRecording ? DateTime.UtcNow - _startedAt : TimeSpan.Zero;
        public int FrameCount { get { lock (_lock) return _frames.Count; } }

        /// <summary>Последний пойманный кадр — чтобы показывать, что снимаем.</summary>
        public event Action<byte[]> Preview;

        public string LastError { get; private set; }

        public bool Start(string cameraDevice = "/dev/video0")
        {
            if (IsRecording) return true;
            LastError = null;
            lock (_lock) _frames.Clear();

            try
            {
                _camera = new FfmpegVideoSource(FfmpegVideoSource.Kind.Camera, cameraDevice);
                _camera.Error += e => LastError = e;
                _camera.JpegFrameReady += frame =>
                {
                    if (!IsRecording) return;
                    lock (_lock) _frames.Add(frame);
                    try { Preview?.Invoke(frame); } catch { }
                };
                _camera.Start();
            }
            catch (Exception ex)
            {
                LastError = "Камера недоступна: " + ex.Message;
                return false;
            }

            // Звук не обязателен: без микрофона кружок всё равно запишется,
            // просто немой. Останавливать из-за этого всю запись незачем.
            _voice = new VoiceNote();
            if (!_voice.Start()) { _voice.Dispose(); _voice = null; }

            IsRecording = true;
            _startedAt = DateTime.UtcNow;
            return true;
        }

        /// <summary>Останавливает и собирает кружок. null — записывать было нечего.</summary>
        public byte[] Stop()
        {
            if (!IsRecording) return null;
            IsRecording = false;

            try { _camera?.Stop(); } catch { }
            try { _camera?.Dispose(); } catch { }
            _camera = null;

            byte[] wav = null;
            try { wav = _voice?.Stop(); } catch { }
            try { _voice?.Dispose(); } catch { }
            _voice = null;

            List<byte[]> frames;
            lock (_lock) { frames = new List<byte[]>(_frames); _frames.Clear(); }
            if (frames.Count == 0) return null;

            return VideoCircleCodec.Encode(frames, wav, Fps);
        }

        public void Cancel()
        {
            IsRecording = false;
            try { _camera?.Stop(); _camera?.Dispose(); } catch { }
            _camera = null;
            try { _voice?.Cancel(); _voice?.Dispose(); } catch { }
            _voice = null;
            lock (_lock) _frames.Clear();
        }

        public void Dispose() => Cancel();
    }
}
