using System;

namespace PISMO.Platform
{
    /// <summary>
    /// Источник видеокадров в формате JPEG (камера или экран). Кадры отдаются
    /// готовыми к отправке собеседнику бинарным пакетом по DataChannel
    /// (тип TypeVideo для камеры, TypeScreen для демонстрации экрана) — тем же
    /// проверенным транспортом, что и голос. Так видео не может нарушить аудио.
    /// </summary>
    public interface IVideoSource : IDisposable
    {
        /// <summary>Готов очередной JPEG-кадр.</summary>
        event Action<byte[]> JpegFrameReady;

        /// <summary>Ошибка захвата (нет камеры / нет ffmpeg / Wayland без x11grab и т.п.).</summary>
        event Action<string> Error;

        void Start();
        void Stop();
    }
}
