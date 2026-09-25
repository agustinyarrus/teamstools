using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace TeamsTools
{
    /// <summary>
    /// Fotos «de fábrica» para la documentación y para verificar sin esperar a que pase algo de verdad, dibujadas
    /// FUERA de pantalla y sin tocar Teams: el aviso de cuenta regresiva (--foto-overlay DIR) y todos los estados del
    /// ícono de la bandeja (--foto-bandeja DIR). Hermanas de FotoBanda y de --foto-transcripcion.
    /// </summary>
    internal static class FotoExtras
    {
        /// <summary>El overlay en sus tres momentos: la cuenta regresiva, la salida en curso y una simulación.</summary>
        public static int Overlay(string dir)
        {
            Directory.CreateDirectory(dir);
            var lectura = new Lectura { HayLlamada = true, Reunion = "Daily Equipo Dev", Titulo = "Daily Equipo Dev | Microsoft Teams", PropioVisto = true, NombrePropio = "Ferreyra, Ramiro" };
            var escenas = new (string nombre, Vista vista)[]
            {
                ("cuenta-regresiva", new Vista { Estado = Estado.SalaVacia, Lectura = lectura, SegundosRestantes = 12, VacioDesde = DateTime.Now.AddSeconds(-33), Motivo = "no queda nadie más en la sala" }),
                ("saliendo", new Vista { Estado = Estado.Saliendo, Lectura = lectura, SegundosRestantes = 0, Motivo = "no queda nadie más en la sala" }),
                ("simulacion", new Vista { Estado = Estado.SalaVacia, Lectura = lectura, SegundosRestantes = 38, Simulacion = true, Motivo = "nadie llegó en 5 min" }),
            };
            int n = 0;
            foreach (var e in escenas)
                using (var f = new CuentaForm())
                {
                    string ruta = Path.Combine(dir, "overlay-" + e.nombre + ".png");
                    f.Fotografiar(e.vista, 45, 10, ruta);
                    n++;
                    Console.WriteLine("  ✓ " + Path.GetFileName(ruta));
                }
            Console.WriteLine($"{n} fotos en {dir}");
            return 0;
        }

        /// <summary>
        /// Cada estado del ícono de la bandeja, ampliado sin suavizar (para verlo pixel por pixel) y en una hoja con su
        /// nombre, más los 49 cuadros del anillo que se llena mientras el grabador trabaja (proceso-NN.png, para animarlo).
        /// </summary>
        public static int Bandeja(string dir)
        {
            Directory.CreateDirectory(dir);
            const int zoom = 6, sep = 22;
            var estados = new (string nombre, Bitmap bmp)[]
            {
                ("sin teams", TeamsTools.Bandeja.DibujoEstado(Tema.MuyApagado, "", false)),
                ("sin llamada", TeamsTools.Bandeja.DibujoEstado(Tema.Apagado, "", false)),
                ("en llamada", TeamsTools.Bandeja.DibujoEstado(Tema.Cyan, "", false)),
                ("esperando gente", TeamsTools.Bandeja.DibujoEstado(Tema.Cielo, "", false)),
                ("sala vacia 12 s", TeamsTools.Bandeja.DibujoEstado(Tema.Durazno, "12", true)),
                ("pospuesto", TeamsTools.Bandeja.DibujoEstado(Tema.Crema, "", false)),
                ("saliendo", TeamsTools.Bandeja.DibujoEstado(Tema.Rosa, "", true)),
                ("sali", TeamsTools.Bandeja.DibujoEstado(Tema.Salvia, "", false)),
                ("pausado", TeamsTools.Bandeja.DibujoEstado(Tema.Malva, "", false)),
                ("procesando sin avance", TeamsTools.Bandeja.DibujoProceso(-1, false)),
                ("procesando 25%", TeamsTools.Bandeja.DibujoProceso(12, false)),
                ("procesando 60%", TeamsTools.Bandeja.DibujoProceso(29, false)),
                ("procesando 100%", TeamsTools.Bandeja.DibujoProceso(48, false)),
                ("transcripcion en pausa", TeamsTools.Bandeja.DibujoProceso(29, true)),
            };
            int n = 0;
            foreach (var e in estados)
            {
                using (var grande = Ampliar(e.bmp, zoom))
                    grande.Save(Path.Combine(dir, "bandeja-" + e.nombre.Replace(' ', '-').Replace("%", "") + ".png"), ImageFormat.Png);
                n++;
            }
            // la hoja: todos los estados en fila, con su nombre, sobre el negro de la app (para el README)
            using (var hoja = new Bitmap(estados.Length * (32 * zoom + sep) + sep, 32 * zoom + sep * 2 + 30, PixelFormat.Format32bppArgb))
            using (var gh = Graphics.FromImage(hoja))
            {
                gh.Clear(Tema.Fondo);
                gh.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                gh.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                for (int i = 0; i < estados.Length; i++)
                {
                    int x = sep + i * (32 * zoom + sep);
                    gh.DrawImage(estados[i].bmp, new Rectangle(x, sep, 32 * zoom, 32 * zoom));
                    TextRenderer.DrawText(gh, estados[i].nombre, Tema.Fina(8f), new Rectangle(x - sep / 2, sep + 32 * zoom + 8, 32 * zoom + sep, 22), Tema.TextoSuave,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                }
                hoja.Save(Path.Combine(dir, "bandeja-hoja.png"), ImageFormat.Png);
            }
            foreach (var e in estados) e.bmp.Dispose();
            // los cuadros del llenado (0..48) a ×4: la animación se arma afuera
            for (int paso = 0; paso <= 48; paso++)
                using (var b = TeamsTools.Bandeja.DibujoProceso(paso, false))
                using (var grande = Ampliar(b, 4))
                {
                    grande.Save(Path.Combine(dir, "proceso-" + paso.ToString("00") + ".png"), ImageFormat.Png);
                    n++;
                }
            Console.WriteLine($"{n + 1} fotos en {dir}");
            return 0;
        }

        /// <summary>Un ícono de 32 px ampliado por un entero sin suavizado: cada pixel se ve tal cual.</summary>
        static Bitmap Ampliar(Bitmap b, int zoom)
        {
            var grande = new Bitmap(b.Width * zoom, b.Height * zoom, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(grande))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(b, new Rectangle(0, 0, grande.Width, grande.Height));
            }
            return grande;
        }
    }
}
