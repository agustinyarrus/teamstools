using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TeamsTools
{
    /// <summary>
    /// Lector simulado para --demo: cuenta una historia completa sin tocar Teams.
    ///   0-8 s   sin llamada
    ///   8-30 s  reunión con 3 personas (y una comparte pantalla)
    ///   30 s+   se van todos -> cuenta regresiva -> salida (simulada por el propio lector)
    ///   luego   sin llamada; la historia se cuenta UNA sola vez (para no confundirla con una reunion real)
    /// </summary>
    internal sealed class LectorDemo : ILector
    {
        readonly Stopwatch reloj = Stopwatch.StartNew();
        readonly Logger log;
        bool salido, avisadoFin;
        double finSalida;
        int vuelta = 1;

        public LectorDemo(Logger l) { log = l; log.Aviso("MODO DEMO: lector simulado, Teams no se toca"); }

        public Lectura Leer()
        {
            double t = reloj.Elapsed.TotalSeconds;
            var L = new Lectura { TeamsCorriendo = true, VentanasTeams = 2, Ms = 42 };
            if (salido)
            {
                if (!avisadoFin && t - finSalida > 5) { avisadoFin = true; log.Ok("DEMO terminada: la historia se contó una vez. Cerrá esta ventana cuando quieras."); }
                return L;
            }
            if (t < 8) return L;
            L.HayLlamada = true;
            L.Hwnd = new IntPtr(0x1234);
            L.Titulo = $"DEMO · reunión simulada {vuelta} | Microsoft Teams";
            L.Reunion = $"DEMO · reunión simulada {vuelta}";
            L.Duracion = TimeSpan.FromSeconds(t - 8).ToString(@"m\:ss");
            L.PropioVisto = true; L.NombrePropio = "Ferreyra, Ramiro";
            L.Pagina = "1/1";
            if (t < 30)
            {
                L.Otros = t < 15 ? 3 : t < 22 ? 2 : 1;
                var todos = new[] { "Rivas, Valentina", "Ortega, Bruno", "Sosa, Florencia" };
                for (int i = 0; i < L.Otros; i++) L.Nombres.Add(todos[i]);
                L.HayCompartido = t < 20;
                L.FichasTotal = L.Otros + (L.HayCompartido ? 1 : 0);
            }
            return L;
        }

        public ResultadoSalida Salir(IntPtr hwnd, string metodo)
        {
            System.Threading.Thread.Sleep(1200);
            salido = true; finSalida = reloj.Elapsed.TotalSeconds;
            return new ResultadoSalida { Ok = true, Detalle = "demo (" + metodo + ")" };
        }

        public int? VerificarRoster(IntPtr hwnd, out string detalle) { detalle = "demo"; return 1; }

        public List<string> Volcar(string carpeta) { log.Aviso("Demo: no hay árbol UIA que volcar"); return new List<string>(); }
    }
}
