using System;
using System.Threading.Tasks;

namespace PISMO.Platform
{
    /// <summary>
    /// Абстракция WebRTC-движка звонков. В Windows-версии реализована поверх
    /// WebView2 (JavaScript RTCPeerConnection). Кроссплатформенная реализация
    /// использует Chromium Embedded Framework (CEF) — тот же браузерный движок и
    /// тот же JS-код звонка, поэтому аудио/видео/экран/камера сохраняются 1:1.
    ///
    /// Реализация подключается на этапе портирования звонков (docs/ROADMAP.md).
    /// UI-код (окно звонка) работает только с этим интерфейсом и не зависит от
    /// того, какой браузерный движок используется под капотом.
    /// </summary>
    public interface ICallEngine : IDisposable
    {
        event Action Connected;
        event Action Disconnected;
        event Action<byte, byte[]> FrameReceived;           // тип потока + данные
        event Action<string> IceCandidateReady;
        event Action GatheringComplete;

        event Action<byte[]> LocalCameraFrameReceived;
        event Action<byte[]> RemoteCameraFrameReceived;
        event Action<byte[]> RemoteScreenFrameReceived;

        Task InitializeAsync(string iceConfigJson);
        Task CreateOfferAsync();
        Task AcceptOfferAsync(string sdp);
        Task ApplyAnswerAsync(string sdp);
        Task AddIceCandidateAsync(string candidateJson);

        Task SetMicrophoneEnabledAsync(bool enabled);
        Task SetCameraEnabledAsync(bool enabled);
        Task StartScreenShareAsync();
        Task StopScreenShareAsync();

        void Hangup();
    }

    /// <summary>Устройство ввода/вывода звука (для записи голосовых и кружков).</summary>
    public interface IAudioDevice
    {
        void StartRecording();
        byte[] StopRecording();               // WAV/PCM буфер
        void Play(byte[] audio);
        void Stop();
    }

    /// <summary>Захват кадров с камеры (для превью и записи видео-кружков).</summary>
    public interface ICameraDevice : IDisposable
    {
        event Action<byte[]> FrameReady;      // JPEG-кадр
        void Start(int deviceIndex = 0);
        void Stop();
    }

    /// <summary>
    /// Фабрика платформенных сервисов. Конкретные реализации регистрируются
    /// desktop-проектом при старте (Windows: WebView2/NAudio/AForge;
    /// Linux: CEF/PortAudio-или-FFmpeg/V4L2). Пока реализации нет — свойства null,
    /// и UI показывает звонки/кружки как «в разработке», не падая.
    /// </summary>
    public static class PlatformServices
    {
        public static Func<ICallEngine> CallEngineFactory { get; set; }
        public static Func<IAudioDevice> AudioDeviceFactory { get; set; }
        public static Func<ICameraDevice> CameraDeviceFactory { get; set; }

        public static bool CallsAvailable => CallEngineFactory != null;
        public static bool AudioAvailable => AudioDeviceFactory != null;
        public static bool CameraAvailable => CameraDeviceFactory != null;
    }
}
