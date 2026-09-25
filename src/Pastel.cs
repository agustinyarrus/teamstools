using System;
using System.Collections.Generic;
using System.Linq;

namespace TeamsTools
{
    /// <summary>
    /// La consola de las pruebas con el lenguaje visual del user: negro, pasteles Catppuccin, un renglón por chequeo con
    /// su ✓/✗ y una tarjeta de resumen al final. Los colores son ANSI de 24 bits; con `--sin-color` (o NO_COLOR) se
    /// apagan y sale texto limpio para leer desde otra herramienta.
    /// </summary>
    internal static class Pastel
    {
        public static bool ConColor = Environment.GetEnvironmentVariable("NO_COLOR") == null;

        static string C(string codigo) => ConColor ? codigo : "";
        public static string Rosa => C("\u001b[38;2;243;185;210m");
        public static string Salvia => C("\u001b[38;2;181;223;168m");
        public static string Cyan => C("\u001b[38;2;143;214;204m");
        public static string Durazno => C("\u001b[38;2;246;192;160m");
        public static string Malva => C("\u001b[38;2;196;181;253m");
        public static string Crema => C("\u001b[38;2;238;223;184m");
        public static string Cielo => C("\u001b[38;2;168;207;242m");
        public static string Texto => C("\u001b[38;2;211;214;223m");
        public static string Apagado => C("\u001b[38;2;88;93;110m");
        public static string Fin => C("\u001b[0m");

        /// <summary>Una batería de chequeos con su cuenta: cada uno imprime su renglón al toque y la tarjeta los resume.</summary>
        internal sealed class Bateria
        {
            readonly List<(string grupo, string que, bool ok, string detalle)> chequeos = new List<(string, string, bool, string)>();
            string grupo = "";
            public int Fallas => chequeos.Count(c => !c.ok);
            public int Total => chequeos.Count;

            public void Grupo(string nombre, string sub = "")
            {
                grupo = nombre;
                Console.WriteLine($"\n  {Malva}◆ {nombre}{Fin}{(sub.Length > 0 ? $"  {Apagado}{sub}{Fin}" : "")}");
            }

            public bool Chequeo(string que, bool ok, string detalle = "")
            {
                chequeos.Add((grupo, que, ok, detalle));
                Console.WriteLine($"    {(ok ? Salvia + "✓" : Rosa + "✗")}{Fin} {que,-58} {(ok ? Apagado : Rosa)}{detalle}{Fin}");
                return ok;
            }

            public void Dato(string que, string valor) => Console.WriteLine($"    {Cielo}·{Fin} {que,-58} {Texto}{valor}{Fin}");

            /// <summary>La tarjeta final: cuántos pasaron, por grupo, y las fallas con su detalle.</summary>
            public void Tarjeta(string titulo, IEnumerable<(string, string)> numeros = null)
            {
                const int ancho = 78;
                string borde = Fallas == 0 ? Salvia : Rosa;
                Console.WriteLine();
                Console.WriteLine($"  {borde}╭─ {titulo} {new string('─', Math.Max(2, ancho - titulo.Length - 3))}╮{Fin}");
                void Renglon(string s, string color) => Console.WriteLine($"  {borde}│{Fin} {color}{Recortar(s, ancho - 2).PadRight(ancho - 2)}{Fin} {borde}│{Fin}");
                Renglon(Fallas == 0 ? $"{Total}/{Total} chequeos bien" : $"{Total - Fallas}/{Total} bien · {Fallas} fallaron", Fallas == 0 ? Salvia : Rosa);
                foreach (var g in chequeos.GroupBy(c => c.grupo))
                    Renglon($"  {(g.All(c => c.ok) ? "✓" : "✗")} {g.Key} · {g.Count(c => c.ok)}/{g.Count()}", g.All(c => c.ok) ? Texto : Rosa);
                if (numeros != null)
                {
                    Renglon("", Texto);
                    foreach (var (k, v) in numeros) Renglon($"  {k,-34} {v}", Cielo);
                }
                foreach (var f in chequeos.Where(c => !c.ok)) Renglon($"  ✗ {f.que}: {f.detalle}", Rosa);
                Console.WriteLine($"  {borde}╰{new string('─', ancho)}╯{Fin}\n");
            }

            static string Recortar(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }
    }
}
