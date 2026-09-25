using System;
using System.IO;
using System.Media;
using System.Threading;

namespace TeamsTools
{
    /// <summary>Campanitas generadas en memoria (WAV 44.1 kHz mono 16 bit). Sin archivos, sin dependencias.</summary>
    internal static class Sonidos
    {
        const int Fs = 44100;

        /// <summary>Dos notas ascendentes: "la sala se vació".</summary>
        public static void Aviso() => Tocar(new[] { (659.25, 0.14, 0.55), (880.0, 0.26, 0.5) });
        /// <summary>Tres notas descendentes suaves: "salí".</summary>
        public static void Adios() => Tocar(new[] { (880.0, 0.12, 0.45), (659.25, 0.12, 0.42), (523.25, 0.3, 0.4) });
        /// <summary>Un tic corto para la cuenta regresiva.</summary>
        public static void Tic() => Tocar(new[] { (1046.5, 0.05, 0.22) });

        static void Tocar((double hz, double seg, double vol)[] notas)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var bytes = Wav(notas);
                    using (var ms = new MemoryStream(bytes))
                    using (var sp = new SoundPlayer(ms)) sp.PlaySync();
                }
                catch { }
            });
        }

        static byte[] Wav((double hz, double seg, double vol)[] notas)
        {
            int total = 0;
            foreach (var n in notas) total += (int)(n.seg * Fs);
            total += Fs / 20; // colita de silencio
            var pcm = new short[total];
            int pos = 0;
            foreach (var n in notas)
            {
                int len = (int)(n.seg * Fs);
                int ataque = Math.Min(len / 6, 400), caida = Math.Min(len / 2, 3000);
                for (int i = 0; i < len && pos < total; i++, pos++)
                {
                    double env = 1.0;
                    if (i < ataque) env = i / (double)ataque;
                    else if (i > len - caida) env = (len - i) / (double)caida;
                    double t = i / (double)Fs;
                    // fundamental + un poquito de octava para que suene a campanita y no a beep
                    double s = Math.Sin(2 * Math.PI * n.hz * t) * 0.85 + Math.Sin(2 * Math.PI * n.hz * 2 * t) * 0.15;
                    pcm[pos] = (short)(s * env * n.vol * short.MaxValue);
                }
            }
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                int datos = pcm.Length * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + datos); w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Fs); w.Write(Fs * 2); w.Write((short)2); w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(datos);
                foreach (var s in pcm) w.Write(s);
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
