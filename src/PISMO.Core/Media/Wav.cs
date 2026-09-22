using System;
using System.IO;
using System.Text;

namespace PISMO.Media
{
    /// <summary>
    /// Заголовок WAV для несжатого PCM. Нужен ровно затем, чтобы голосовые с
    /// Linux читались Windows-клиентом и наоборот: там их пишет NAudio
    /// (WaveFileWriter), и в базе лежит обычный WAV — 16 кГц, моно, 16 бит.
    ///
    /// Своими руками, а не библиотекой: заголовок — 44 байта по неизменной с
    /// девяностых спецификации, и тянуть ради него зависимость не за что.
    /// </summary>
    public static class Wav
    {
        public const int SampleRate = 16000;
        public const int Channels = 1;
        public const int BitsPerSample = 16;

        private const int HeaderSize = 44;

        /// <summary>Оборачивает сырой PCM в WAV.</summary>
        public static byte[] FromPcm(byte[] pcm, int sampleRate = SampleRate, int channels = Channels)
        {
            pcm ??= Array.Empty<byte>();
            int byteRate = sampleRate * channels * BitsPerSample / 8;
            short blockAlign = (short)(channels * BitsPerSample / 8);

            using var ms = new MemoryStream(HeaderSize + pcm.Length);
            using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + pcm.Length);              // размер всего дальше
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));

            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);                            // размер fmt-блока
            bw.Write((short)1);                      // PCM, без сжатия
            bw.Write((short)channels);
            bw.Write(sampleRate);
            bw.Write(byteRate);
            bw.Write(blockAlign);
            bw.Write((short)BitsPerSample);

            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(pcm.Length);
            bw.Flush();
            ms.Write(pcm, 0, pcm.Length);
            return ms.ToArray();
        }

        /// <summary>
        /// Достаёт PCM из WAV. Блоки идут не всегда подряд — между fmt и data
        /// бывает LIST с тегами, — поэтому ищем data, а не отсчитываем 44
        /// байта вслепую.
        /// </summary>
        public static byte[] ToPcm(byte[] wav)
        {
            if (wav == null || wav.Length < 12) return Array.Empty<byte>();
            try
            {
                if (Encoding.ASCII.GetString(wav, 0, 4) != "RIFF"
                    || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
                    return wav;   // не WAV — отдаём как есть, вдруг это уже PCM

                int pos = 12;
                while (pos + 8 <= wav.Length)
                {
                    string id = Encoding.ASCII.GetString(wav, pos, 4);
                    int size = BitConverter.ToInt32(wav, pos + 4);
                    int body = pos + 8;
                    if (size < 0 || body + size > wav.Length) size = wav.Length - body;

                    if (id == "data")
                    {
                        var pcm = new byte[size];
                        Buffer.BlockCopy(wav, body, pcm, 0, size);
                        return pcm;
                    }
                    pos = body + size + (size % 2);   // блоки выровнены по два байта
                }
            }
            catch { }
            return Array.Empty<byte>();
        }

        /// <summary>Длительность записи в секундах — для подписи под голосовым.</summary>
        public static double Seconds(byte[] wav)
        {
            var pcm = ToPcm(wav);
            double bytesPerSecond = SampleRate * Channels * BitsPerSample / 8.0;
            return bytesPerSecond <= 0 ? 0 : pcm.Length / bytesPerSecond;
        }
    }
}
