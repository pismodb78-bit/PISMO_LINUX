using System;
using System.Diagnostics;
using System.Threading;

namespace PISMO.Platform
{
    /// <summary>
    /// Аудио для звонков на Linux через ALSA-утилиты <c>arecord</c>/<c>aplay</c>
    /// (пакет alsa-utils, есть на любом Linux-десктопе). Никаких нативных
    /// биндингов — только два дочерних процесса, читающих/пишущих сырой PCM
    /// в формате S16LE 16кГц моно (совместимо с Windows-версией).
    ///
    /// arecord -q -t raw -f S16_LE -r 16000 -c 1   → stdout (микрофон)
    /// aplay   -q -t raw -f S16_LE -r 16000 -c 1   ← stdin  (динамики)
    /// </summary>
    public sealed class AlsaAudioDevice : IAudioDevice
    {
        private const int SampleRate = 16000;
        private const int FrameBytes = 1280; // ~40 мс при 16кГц/моно/16бит

        private Process _rec;
        private Process _play;
        private Thread _readThread;
        private volatile bool _capturing;
        private readonly object _playLock = new();

        public event Action<byte[]> SamplesCaptured;
        public bool Muted { get; set; }

        public void StartCapture()
        {
            if (_capturing) return;
            _rec = Start("arecord", $"-q -t raw -f S16_LE -r {SampleRate} -c 1");
            if (_rec == null) return;

            _capturing = true;
            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "AlsaCapture" };
            _readThread.Start();
        }

        private void ReadLoop()
        {
            var stream = _rec.StandardOutput.BaseStream;
            var buffer = new byte[FrameBytes];
            try
            {
                while (_capturing)
                {
                    int read = 0;
                    while (read < FrameBytes)
                    {
                        int n = stream.Read(buffer, read, FrameBytes - read);
                        if (n <= 0) return; // процесс закрылся
                        read += n;
                    }
                    if (Muted) continue;
                    var frame = new byte[read];
                    Buffer.BlockCopy(buffer, 0, frame, 0, read);
                    try { SamplesCaptured?.Invoke(frame); } catch { }
                }
            }
            catch { /* поток закрыт при остановке */ }
        }

        public void StopCapture()
        {
            _capturing = false;
            TryKill(ref _rec);
            try { _readThread?.Join(300); } catch { }
            _readThread = null;
        }

        public void StartPlayback()
        {
            lock (_playLock)
            {
                if (_play != null) return;
                _play = Start("aplay", $"-q -t raw -f S16_LE -r {SampleRate} -c 1", redirectIn: true);
            }
        }

        public void PlaySamples(byte[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return;
            lock (_playLock)
            {
                if (_play == null || _play.HasExited) return;
                try
                {
                    _play.StandardInput.BaseStream.Write(pcm, 0, pcm.Length);
                    _play.StandardInput.BaseStream.Flush();
                }
                catch { /* конвейер закрыт */ }
            }
        }

        public void StopPlayback()
        {
            lock (_playLock)
            {
                TryKill(ref _play);
            }
        }

        public void Dispose()
        {
            StopCapture();
            StopPlayback();
        }

        private static Process Start(string exe, string args, bool redirectIn = false)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    RedirectStandardOutput = !redirectIn,
                    RedirectStandardInput = redirectIn,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                var p = Process.Start(psi);
                return p;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ALSA] Не удалось запустить {exe}: {ex.Message}. " +
                                "Установите alsa-utils (sudo apt install alsa-utils).");
                return null;
            }
        }

        private static void TryKill(ref Process p)
        {
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(true); } catch { }
            try { p.Dispose(); } catch { }
            p = null;
        }
    }
}
