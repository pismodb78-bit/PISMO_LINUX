using System;

namespace PISMO.Platform
{
    /// <summary>
    /// Потоковый ввод/вывод звука для звонков. Формат жёстко зафиксирован
    /// совместимо с Windows-версией (CallForm): <b>PCM 16-bit, 16 кГц, моно (S16LE)</b>.
    /// Микрофон отдаётся кадрами через <see cref="SamplesCaptured"/>, входящий звук
    /// собеседника проигрывается через <see cref="PlaySamples"/>.
    ///
    /// Реализация выбирается по платформе:
    ///  • Linux  — ALSA (<c>arecord</c>/<c>aplay</c>), см. AlsaAudioDevice;
    ///  • Windows — NAudio (как в оригинале), может быть добавлена позже.
    /// </summary>
    public interface IAudioDevice : IDisposable
    {
        /// <summary>Формат PCM, ожидаемый обеими сторонами звонка.</summary>
        public const int SampleRate = 16000;
        public const int Channels = 1;
        public const int BitsPerSample = 16;

        /// <summary>Захвачен кадр PCM с микрофона (готов к отправке собеседнику).</summary>
        event Action<byte[]> SamplesCaptured;

        void StartCapture();
        void StopCapture();

        void StartPlayback();
        void PlaySamples(byte[] pcm);
        void StopPlayback();

        /// <summary>Заглушить микрофон (кадры перестают отдаваться в SamplesCaptured).</summary>
        bool Muted { get; set; }
    }

    /// <summary>
    /// Фабрика платформенных сервисов. Desktop-проект регистрирует реализацию при
    /// старте (на Linux — AlsaAudioDevice). Если аудио недоступно — фабрика null,
    /// и окно звонка честно сообщает об этом, не падая.
    /// </summary>
    public static class PlatformServices
    {
        public static Func<IAudioDevice> AudioDeviceFactory { get; set; }

        public static bool AudioAvailable => AudioDeviceFactory != null;

        public static IAudioDevice CreateAudioDevice()
            => AudioDeviceFactory?.Invoke();
    }
}
