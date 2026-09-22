using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PISMO.Media
{
    /// <summary>
    /// Контейнер видео-кружков — перенесён из PISMO/VideoCircleCodec.cs, байт
    /// в байт тот же формат, иначе кружок с Linux не откроется на Windows:
    ///
    ///   8 байт  — "PSMOVID1"
    ///   4 байта — длина блока WAV
    ///   4 байта — количество кадров
    ///   4 байта — кадров в секунду
    ///   ...     — WAV (может быть нулевой длины)
    ///   на каждый кадр: 4 байта длины JPEG + сами данные
    ///
    /// Отличие от Windows-версии одно: та собирает кадры из System.Drawing
    /// Bitmap и жмёт их сама, а здесь на вход уже приходят готовые JPEG —
    /// их даёт ffmpeg (FfmpegVideoSource). System.Drawing на Linux нет, и
    /// это к лучшему: лишнего перекодирования не происходит.
    /// </summary>
    public static class VideoCircleCodec
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PSMOVID1");

        public sealed class Circle
        {
            public List<byte[]> Frames = new();
            public byte[] Wav = Array.Empty<byte>();
            public int Fps = 12;

            public TimeSpan Duration => Fps <= 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(Frames.Count / (double)Fps);
        }

        public static byte[] Encode(IReadOnlyList<byte[]> jpegFrames, byte[] wav, int fps)
        {
            jpegFrames ??= Array.Empty<byte[]>();
            wav ??= Array.Empty<byte>();

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(Magic);
            bw.Write(wav.Length);
            bw.Write(jpegFrames.Count);
            bw.Write(fps);
            bw.Write(wav);
            foreach (var f in jpegFrames)
            {
                bw.Write(f?.Length ?? 0);
                if (f != null && f.Length > 0) bw.Write(f);
            }
            return ms.ToArray();
        }

        /// <summary>Разбирает блоб. null — это не кружок.</summary>
        public static Circle Decode(byte[] blob)
        {
            if (blob == null || blob.Length < 20) return null;
            try
            {
                for (int i = 0; i < Magic.Length; i++)
                    if (blob[i] != Magic[i]) return null;

                using var ms = new MemoryStream(blob);
                using var br = new BinaryReader(ms);
                br.ReadBytes(Magic.Length);

                int wavLen = br.ReadInt32();
                int frameCount = br.ReadInt32();
                int fps = br.ReadInt32();
                if (wavLen < 0 || frameCount < 0 || frameCount > 100000) return null;

                var circle = new Circle { Fps = fps <= 0 ? 12 : fps };
                circle.Wav = wavLen > 0 ? br.ReadBytes(wavLen) : Array.Empty<byte>();

                for (int i = 0; i < frameCount; i++)
                {
                    // Файл мог обрезаться на передаче — читаем, пока читается,
                    // и отдаём то, что есть: половина кружка лучше, чем
                    // сообщение «не открывается».
                    if (ms.Position + 4 > ms.Length) break;
                    int len = br.ReadInt32();
                    if (len <= 0 || ms.Position + len > ms.Length) break;
                    circle.Frames.Add(br.ReadBytes(len));
                }
                return circle;
            }
            catch { return null; }
        }

        /// <summary>Это кружок, а не обычное видео?</summary>
        public static bool IsCircle(byte[] blob)
        {
            if (blob == null || blob.Length < Magic.Length) return false;
            for (int i = 0; i < Magic.Length; i++)
                if (blob[i] != Magic[i]) return false;
            return true;
        }
    }
}
