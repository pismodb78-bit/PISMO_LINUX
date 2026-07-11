using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PISMO.Platform
{
    /// <summary>
    /// Захват видео на Linux через <c>ffmpeg</c> (подпроцесс, как ALSA для звука) с
    /// выводом MJPEG в stdout. Из потока выделяются отдельные JPEG-кадры и
    /// отдаются через <see cref="JpegFrameReady"/>. Никаких нативных биндингов —
    /// только бинарник ffmpeg (пакет ffmpeg).
    ///
    ///  • камера: -f v4l2 -i /dev/videoN
    ///  • экран:  -f x11grab -i $DISPLAY
    ///
    /// Разрешение/качество намеренно скромные, чтобы кадр помещался в один пакет
    /// DataChannel (лимит транспорта ~60 КБ) и не грузил сеть.
    /// </summary>
    public sealed class FfmpegVideoSource : IVideoSource
    {
        public enum Kind { Camera, Screen }

        private readonly Kind _kind;
        private readonly string _cameraDevice;
        private Process _proc;
        private Thread _reader;
        private volatile bool _running;

        public event Action<byte[]> JpegFrameReady;
        public event Action<string> Error;

        public FfmpegVideoSource(Kind kind, string cameraDevice = "/dev/video0")
        {
            _kind = kind;
            _cameraDevice = string.IsNullOrWhiteSpace(cameraDevice) ? "/dev/video0" : cameraDevice;
        }

        private string BuildArgs()
        {
            // q:v для mjpeg: больше число → меньше размер кадра. Держим кадр < ~60 КБ,
            // чтобы он уходил одним пакетом DataChannel.
            if (_kind == Kind.Camera)
            {
                return $"-f v4l2 -framerate 15 -video_size 640x480 -i {_cameraDevice} " +
                       "-vf scale=480:-2 -q:v 10 -f mjpeg -";
            }
            // Экран: берём дисплей из окружения (X11). Wayland — через XWayland (:0).
            string display = Environment.GetEnvironmentVariable("DISPLAY");
            if (string.IsNullOrWhiteSpace(display)) display = ":0.0";
            return $"-f x11grab -framerate 12 -i {display} " +
                   "-vf scale=800:-2 -q:v 12 -f mjpeg -";
        }

        public void Start()
        {
            if (_running) return;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = BuildArgs(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                _proc = Process.Start(psi);
                if (_proc == null) { Error?.Invoke("Не удалось запустить ffmpeg"); return; }
            }
            catch (Exception ex)
            {
                Error?.Invoke("ffmpeg не найден. Установите пакет ffmpeg. " + ex.Message);
                return;
            }

            _running = true;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "FfmpegMjpeg" };
            _reader.Start();
        }

        // Разбор MJPEG: каждый кадр начинается с SOI (FF D8) и кончается EOI (FF D9).
        private void ReadLoop()
        {
            var stream = _proc.StandardOutput.BaseStream;
            var buf = new MemoryStream();
            var chunk = new byte[16384];
            bool inFrame = false;
            int prev = -1;

            try
            {
                int read;
                while (_running && (read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    for (int i = 0; i < read; i++)
                    {
                        int b = chunk[i];
                        if (!inFrame)
                        {
                            if (prev == 0xFF && b == 0xD8) // SOI
                            {
                                inFrame = true;
                                buf.SetLength(0);
                                buf.WriteByte(0xFF);
                                buf.WriteByte(0xD8);
                            }
                        }
                        else
                        {
                            buf.WriteByte((byte)b);
                            if (prev == 0xFF && b == 0xD9) // EOI
                            {
                                inFrame = false;
                                var frame = buf.ToArray();
                                try { JpegFrameReady?.Invoke(frame); } catch { }
                            }
                        }
                        prev = b;
                    }
                }
            }
            catch { /* поток закрыт при остановке */ }
        }

        public void Stop()
        {
            _running = false;
            try { if (_proc != null && !_proc.HasExited) _proc.Kill(true); } catch { }
            try { _reader?.Join(300); } catch { }
            try { _proc?.Dispose(); } catch { }
            _proc = null;
            _reader = null;
        }

        public void Dispose() => Stop();
    }
}
