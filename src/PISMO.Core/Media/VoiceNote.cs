using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PISMO.Media
{
    /// <summary>
    /// Запись и воспроизведение голосовых сообщений через ALSA — те же
    /// arecord/aplay, что и в звонках, и тот же формат: 16 кГц, моно, 16 бит.
    /// В базу кладётся WAV, потому что именно его туда кладёт Windows-версия.
    ///
    /// Пишем СЫРОЙ поток и сами надеваем заголовок. У arecord есть режим
    /// записи сразу в WAV, но он дописывает в заголовок длину при нормальном
    /// завершении, а останавливаем мы его убийством процесса — и длина
    /// осталась бы нулевой. Сырой поток такой особенности не имеет.
    /// </summary>
    public sealed class VoiceNote : IDisposable
    {
        private Process _rec;
        private MemoryStream _buffer;
        private Thread _reader;
        private volatile bool _recording;
        private DateTime _startedAt;

        public bool IsRecording => _recording;

        /// <summary>Сколько уже пишем — для счётчика на кнопке.</summary>
        public TimeSpan Elapsed => _recording ? DateTime.UtcNow - _startedAt : TimeSpan.Zero;

        /// <summary>Нет arecord — записывать нечем, и сказать об этом надо сразу.</summary>
        public static bool CanRecord => Which("arecord");

        public static bool CanPlay => Which("aplay");

        private static bool Which(string tool)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "which", Arguments = tool,
                    RedirectStandardOutput = true, UseShellExecute = false,
                });
                if (p == null) return false;
                p.WaitForExit(2000);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        public bool Start()
        {
            if (_recording) return true;
            try
            {
                _rec = Process.Start(new ProcessStartInfo
                {
                    FileName = "arecord",
                    Arguments = $"-q -t raw -f S16_LE -r {Wav.SampleRate} -c {Wav.Channels}",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                });
                if (_rec == null) return false;

                _buffer = new MemoryStream();
                _recording = true;
                _startedAt = DateTime.UtcNow;
                _reader = new Thread(ReadLoop) { IsBackground = true, Name = "VoiceNote" };
                _reader.Start();
                return true;
            }
            catch { _recording = false; return false; }
        }

        private void ReadLoop()
        {
            try
            {
                var stream = _rec.StandardOutput.BaseStream;
                var chunk = new byte[4096];
                while (_recording)
                {
                    int n = stream.Read(chunk, 0, chunk.Length);
                    if (n <= 0) break;
                    lock (_buffer) _buffer.Write(chunk, 0, n);
                }
            }
            catch { /* процесс закрыт при остановке */ }
        }

        /// <summary>Останавливает запись и отдаёт готовый WAV (null — пусто).</summary>
        public byte[] Stop()
        {
            if (!_recording) return null;
            _recording = false;
            try { if (_rec is { HasExited: false }) _rec.Kill(true); } catch { }
            try { _reader?.Join(500); } catch { }
            try { _rec?.Dispose(); } catch { }
            _rec = null;
            _reader = null;

            byte[] pcm;
            lock (_buffer) pcm = _buffer.ToArray();
            _buffer.Dispose();
            _buffer = null;

            // Меньше четверти секунды — это случайное нажатие, а не
            // сообщение. Отправлять такое незачем.
            if (pcm.Length < Wav.SampleRate / 2) return null;
            return Wav.FromPcm(pcm);
        }

        public void Cancel()
        {
            if (!_recording) return;
            _recording = false;
            try { if (_rec is { HasExited: false }) _rec.Kill(true); } catch { }
            try { _rec?.Dispose(); } catch { }
            _rec = null;
            try { _buffer?.Dispose(); } catch { }
            _buffer = null;
        }

        public void Dispose() => Cancel();

        // ── Воспроизведение ─────────────────────────────────────────────

        private static Process _playing;

        /// <summary>
        /// Играет WAV из базы. Через временный файл: aplay читает заголовок
        /// сам, и это надёжнее, чем кормить его сырым потоком в stdin и
        /// гадать про формат.
        /// </summary>
        public static void Play(byte[] wav)
        {
            StopPlayback();
            if (wav == null || wav.Length == 0) return;
            try
            {
                string path = Path.Combine(Path.GetTempPath(),
                    "pismo_voice_" + Guid.NewGuid().ToString("N") + ".wav");
                File.WriteAllBytes(path, wav);

                _playing = Process.Start(new ProcessStartInfo
                {
                    FileName = "aplay",
                    Arguments = "-q \"" + path + "\"",
                    UseShellExecute = false,
                });

                // Файл убираем за собой сами: временный каталог никто не
                // чистит, а голосовых за день набирается много.
                var p = _playing;
                if (p != null)
                {
                    p.EnableRaisingEvents = true;
                    p.Exited += (_, _) => { try { File.Delete(path); } catch { } };
                }
                else
                {
                    try { File.Delete(path); } catch { }
                }
            }
            catch { }
        }

        public static void StopPlayback()
        {
            try { if (_playing is { HasExited: false }) _playing.Kill(true); } catch { }
            try { _playing?.Dispose(); } catch { }
            _playing = null;
        }
    }
}
