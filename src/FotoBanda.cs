using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TeamsTools
{
    /// <summary>
    /// `TeamsTools.exe --foto-banda DIR`: la banda en vivo dibujada FUERA de pantalla con un pulso sintético (una
    /// reunión real no hace falta) y guardada en PNG — la app se saca la foto sola, sin tocar el escritorio. Tres
    /// escenas: la llamada entrando por unos auriculares recién conectados con tu micrófono hablando, tu micrófono
    /// en silencio de Teams, y la banda en reposo. Cada una en dos anchos (ventana chica y ancha).
    /// </summary>
    internal static class FotoBanda
    {
        public static int Sacar(string dir)
        {
            Directory.CreateDirectory(dir);
            var rnd = new Random(7);
            int n = 0;
            foreach (var (nombre, silenciado) in new[] { ("hablando", false), ("mic-en-silencio", true) })
                foreach (int ancho in new[] { 1100, 1500 })
                {
                    var pu = Pulso(rnd, silenciado);
                    var banda = Armar(pu, true, ancho);
                    int k = 188;
                    Guardar(banda, Path.Combine(dir, $"banda-{nombre}-{ancho}.png"), () => pu.Linea(Lectura(k++, rnd)));
                    n++;
                }
            var reposo = Armar(null, false, 1100);
            Guardar(reposo, Path.Combine(dir, "banda-reposo-1100.png"), () => { });
            foreach (var silenciado in new[] { false, true })
            {
                var pu = Pulso(rnd, silenciado);
                var vivo = new EnVivo { Reunion = "Daily Equipo Dev", Desde = DateTime.Now.AddMinutes(-12.4), Segundos = 12.4 * 60, Actualizado = DateTime.Now };
                var rec = new IndicadorGrabacion { Width = 268, Height = 22, FuentePulso = () => pu, FuenteVivo = () => vivo };
                rec.CreateControl();
                rec.Poner(true, vivo.Reunion);
                int k = 188;
                Guardar(rec, Path.Combine(dir, silenciado ? "rec-mic-silenciado.png" : "rec.png"), () => pu.Linea(Lectura(k++, rnd)));
                n++;
            }
            Console.WriteLine($"{n + 1} fotos en {dir}");
            return 0;
        }

        /// <summary>Un pulso que parece una reunión: la voz de la llamada con sílabas y pausas, y tu micrófono a ratos.</summary>
        static PulsoAudio Pulso(Random rnd, bool silenciado)
        {
            var pu = new PulsoAudio { MicSilenciado = silenciado };
            pu.Linea("DEV + 0 salida 0.1 Speakers (Realtek(R) Audio)");
            pu.Linea("DEV + 1 salida 0.1 LG ULTRAFINE (HD Audio Driver for Display Audio)");
            pu.Linea("DEV + 2 mic 3.4 Microphone Array (Intel® Smart Sound Technology for Digital Microphones)");
            pu.Linea("DEV + 3 salida 21.6 Headphones (JBL TUNE FLEX)");
            for (int i = 0; i < 188; i++) pu.Linea(Lectura(i, rnd));   // termina en plena frase (t = 9,4 s)
            return pu;
        }

        /// <summary>La lectura i (50 ms cada una): frases de ~2 s con sílabas en la llamada; tu mic habla del segundo 8 al 11,5.</summary>
        static string Lectura(int i, Random rnd)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            double t = i * 0.05;
            bool frase = (t % 3.1) < 2.2;
            double silaba = Math.Abs(Math.Sin(t * Math.PI * 5.3)) * 0.7 + rnd.NextDouble() * 0.3;
            double dbLlamada = frase ? -44 + 30 * silaba : -70 + rnd.NextDouble() * 6;
            bool hablas = t > 8 && t < 11.5;
            double dbMic = hablas ? -40 + 26 * (Math.Abs(Math.Sin(t * Math.PI * 4.7)) * 0.8 + rnd.NextDouble() * 0.2) : -62 + rnd.NextDouble() * 5;
            string dev3 = t < 1.2 ? "-120.0" : dbLlamada.ToString("0.0", ci);
            return $"LV {i * 50} 0:-120.0 1:-120.0 2:{dbMic.ToString("0.0", ci)} 3:{dev3}";
        }

        static BandaEnVivo Armar(PulsoAudio pu, bool grabando, int ancho)
        {
            var banda = new BandaEnVivo { Width = ancho, Height = grabando ? BandaEnVivo.AltoGrabando : BandaEnVivo.AltoReposo };
            banda.FuentePulso = () => pu;
            var foto = new FotoGrabador { Activo = true };
            if (grabando)
                foto.Vivo = new EnVivo
                {
                    Id = "demo", Reunion = "Daily Equipo Dev", Desde = DateTime.Now.AddMinutes(-12.4), Segundos = 12.4 * 60,
                    NivelDb = -18, PicoTotalDb = -6, MB = 214.6, DiscoLibreMB = 31_400, Pista = "Headphones (JBL TUNE FLEX)",
                    Actualizado = DateTime.Now, Tramo = 1,
                };
            banda.CreateControl();
            banda.Poner(foto, new ConfigGrabador());
            return banda;
        }

        /// <summary>Varios cuadros seguidos antes de la foto: la aguja del vúmetro tiene balística (sube en ~50 ms).</summary>
        static void Guardar(Control c, string ruta, Action lecturaNueva)
        {
            using (var bmp = new Bitmap(c.Width, c.Height, PixelFormat.Format32bppArgb))
            {
                for (int i = 0; i < 12; i++)
                {
                    lecturaNueva();                    // el pulso sigue llegando mientras se anima, como en vivo
                    c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height));
                    System.Threading.Thread.Sleep(35);
                }
                bmp.Save(ruta, ImageFormat.Png);
            }
        }
    }
}
