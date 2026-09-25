using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TeamsTools
{
    /// <summary>
    /// Texto corrido para las Cascadia, que son MONOESPACIADAS (como toda la app): el ancho de un renglón es
    /// caracteres × avance, así que envolver es aritmética y no medición. Se mide UNA vez por fuente y después todo es
    /// O(n) sin tocar GDI: las 6 000 palabras de una reunión de una hora se reacomodan en un par de milisegundos cada
    /// vez que cambia el ancho. Y como la posición de cada carácter es exacta, resaltar una palabra (una búsqueda, el
    /// karaoke) es pintar un rectángulo en x = columna × avance, sin medir nada.
    /// </summary>
    internal static class Prosa
    {
        /// <summary>Un renglón: [Ini, Ini + Largo) del texto, sin el espacio donde se cortó.</summary>
        internal readonly struct Renglon
        {
            public readonly int Ini, Largo;
            /// <summary>Con este renglón termina un párrafo: después viene un respiro.</summary>
            public readonly bool FinDeParrafo;
            public Renglon(int ini, int largo, bool finDeParrafo) { Ini = ini; Largo = largo; FinDeParrafo = finDeParrafo; }
            public int Fin => Ini + Largo;
        }

        /// <summary>Un tramo pintado distinto: [Ini, Fin) con su tinta y, si <see cref="Fondo"/> no es transparente, un resaltado detrás.</summary>
        internal readonly struct Tinta
        {
            public readonly int Ini, Fin;
            public readonly Color Color, Fondo;
            public Tinta(int ini, int fin, Color color, Color fondo) { Ini = ini; Fin = fin; Color = color; Fondo = fondo; }
        }

        /// <summary>Un resaltado pedido por quien dibuja (una búsqueda): [Ini, Fin) y si es el elegido.</summary>
        internal readonly struct Marcado
        {
            public readonly int Ini, Fin;
            public readonly bool Actual;
            public Marcado(int ini, int fin, bool actual) { Ini = ini; Fin = fin; Actual = actual; }
        }

        // 🚨 PreserveGraphicsClipping: sin esto TextRenderer IGNORA el recorte del Graphics y un renglón a medio salir del
        //    cuerpo se dibuja encima del encabezado al desplazar suave.
        const TextFormatFlags Banderas = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping;
        const int MuestraAvance = 64;
        static readonly Dictionary<Font, float> avances = new Dictionary<Font, float>();
        static readonly Dictionary<Font, int> altos = new Dictionary<Font, int>();

        /// <summary>El avance de UN carácter (todas las letras miden lo mismo). Una medición por fuente, cacheada.</summary>
        public static float Avance(Font f)
        {
            lock (avances)
            {
                if (avances.TryGetValue(f, out var a)) return a;
                a = TextRenderer.MeasureText(new string('M', MuestraAvance), f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width / (float)MuestraAvance;
                avances[f] = a;
                return a;
            }
        }

        /// <summary>El alto de un renglón de esa fuente con acentos y descendentes («Ág»). Cacheado.</summary>
        public static int Alto(Font f)
        {
            lock (altos)
            {
                if (altos.TryGetValue(f, out var h)) return h;
                h = TextRenderer.MeasureText("ÁÉÍÓÚÑgjpqy|", f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height;
                altos[f] = h;
                return h;
            }
        }

        /// <summary>
        /// Envuelve en renglones de a lo sumo <paramref name="columnas"/> caracteres; '\n' corta párrafo. El primero
        /// puede arrancar corrido <paramref name="sangria"/> columnas (el nombre de la voz va adelante, en la misma línea).
        /// Se corta en el último espacio que entra; una palabra más larga que el renglón se parte. O(n).
        /// </summary>
        public static List<Renglon> Envolver(string s, int columnas, int sangria = 0)
        {
            var res = new List<Renglon>();
            if (string.IsNullOrEmpty(s)) return res;
            columnas = Math.Max(4, columnas);
            int disponible = Math.Max(4, columnas - Math.Max(0, sangria));
            int n = s.Length, inicio = 0;
            while (inicio <= n)
            {
                int finP = s.IndexOf('\n', inicio);
                if (finP < 0) finP = n;
                int pos = inicio;
                bool alguno = false;
                while (pos < finP)
                {
                    while (pos < finP && s[pos] == ' ') pos++;
                    if (pos >= finP) break;
                    int limite = pos + disponible;
                    int corte, siguiente;
                    if (limite >= finP) { corte = finP; siguiente = finP; }
                    else
                    {
                        // el último espacio en (pos, limite]: si justo después del renglón hay un espacio, entra entero
                        int esp = s.LastIndexOf(' ', limite, limite - pos);
                        if (esp > pos) { corte = esp; siguiente = esp + 1; }
                        else { corte = limite; siguiente = limite; }    // una palabra que no entra en ningún renglón: se parte
                    }
                    int fin = corte;
                    while (fin > pos && s[fin - 1] == ' ') fin--;
                    res.Add(new Renglon(pos, fin - pos, false));
                    alguno = true;
                    pos = siguiente;
                    disponible = columnas;
                }
                if (!alguno) res.Add(new Renglon(inicio, 0, false));   // párrafo vacío: igual ocupa su lugar
                if (finP >= n) break;
                var ult = res[res.Count - 1];
                res[res.Count - 1] = new Renglon(ult.Ini, ult.Largo, true);
                inicio = finP + 1;
            }
            return res;
        }

        /// <summary>
        /// Combina lo que cambia el color dentro de un renglón en tramos ordenados y SIN solaparse. Manda la búsqueda,
        /// después el karaoke (lo dicho / lo que falta) y si no, la tinta de base. Los tramos vecinos iguales se funden.
        /// O(k log k) con k = bordes del renglón.
        /// </summary>
        public static List<Tinta> Componer(Renglon r, Color baseColor, int cursor, Color dicho, Color porDecir,
                                           IReadOnlyList<Marcado> marcados, Color fgMarca, Color bgMarca, Color fgActual, Color bgActual)
        {
            var res = new List<Tinta>();
            int a = r.Ini, b = r.Fin;
            if (b <= a) return res;
            var bordes = new List<int> { a, b };
            if (cursor > a && cursor < b) bordes.Add(cursor);
            if (marcados != null)
                foreach (var m in marcados)
                {
                    if (m.Fin <= a || m.Ini >= b) continue;
                    bordes.Add(Math.Max(a, m.Ini));
                    bordes.Add(Math.Min(b, m.Fin));
                }
            bordes.Sort();
            for (int i = 0; i + 1 < bordes.Count; i++)
            {
                int p = bordes[i], q = bordes[i + 1];
                if (q <= p) continue;
                Color fg = baseColor, bg = Color.Empty;
                bool marcado = false;
                if (marcados != null)
                    foreach (var m in marcados)
                        if (p >= m.Ini && p < m.Fin) { fg = m.Actual ? fgActual : fgMarca; bg = m.Actual ? bgActual : bgMarca; marcado = true; if (m.Actual) break; }
                if (!marcado && cursor >= 0) fg = p < cursor ? dicho : porDecir;
                if (res.Count > 0 && res[res.Count - 1].Fin == p && res[res.Count - 1].Color == fg && res[res.Count - 1].Fondo == bg)
                    res[res.Count - 1] = new Tinta(res[res.Count - 1].Ini, q, fg, bg);
                else res.Add(new Tinta(p, q, fg, bg));
            }
            return res;
        }

        /// <summary>
        /// Dibuja un renglón en (x, y). Primero los fondos resaltados, después el texto tramo por tramo: cada carácter se
        /// pinta UNA sola vez (pintar texto encima de texto con ClearType lo engorda y lo ensucia). O(tramos).
        /// </summary>
        public static void Dibujar(Graphics g, string s, Renglon r, Font f, float avance, int x, int y, int alto, Color baseColor, List<Tinta> tintas)
        {
            int a = r.Ini, b = r.Fin;
            if (b <= a) return;
            if (tintas == null || tintas.Count == 0)
            {
                TextRenderer.DrawText(g, s.Substring(a, b - a), f, new Point(x, y), baseColor, Banderas);
                return;
            }
            int X(int i) => x + (int)Math.Round((i - a) * avance);
            var suave = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var t in tintas)
            {
                if (t.Fondo.A == 0) continue;
                int i0 = Math.Max(a, t.Ini), i1 = Math.Min(b, t.Fin);
                if (i1 <= i0) continue;
                var rf = new RectangleF(X(i0) - 1, y, X(i1) - X(i0) + 2, alto);
                using (var br = new SolidBrush(t.Fondo))
                using (var p = Tema.Redondeado(rf, Math.Min(3f, alto / 4f))) g.FillPath(br, p);
            }
            g.SmoothingMode = suave;
            int cur = a;
            foreach (var t in tintas)
            {
                int i0 = Math.Max(a, t.Ini), i1 = Math.Min(b, t.Fin);
                if (i1 <= i0 || i0 < cur) continue;
                if (i0 > cur) TextRenderer.DrawText(g, s.Substring(cur, i0 - cur), f, new Point(X(cur), y), baseColor, Banderas);
                TextRenderer.DrawText(g, s.Substring(i0, i1 - i0), f, new Point(X(i0), y), t.Color, Banderas);
                cur = i1;
            }
            if (cur < b) TextRenderer.DrawText(g, s.Substring(cur, b - cur), f, new Point(X(cur), y), baseColor, Banderas);
        }

        /// <summary>Dibuja un texto suelto de una línea en (x, y), respetando el recorte del Graphics.</summary>
        public static void Linea(Graphics g, string s, Font f, int x, int y, Color c)
        {
            if (!string.IsNullOrEmpty(s)) TextRenderer.DrawText(g, s, f, new Point(x, y), c, Banderas);
        }

        /// <summary>
        /// Un LOTE de textos que se dibujan juntos con UN solo HDC y el recorte puesto UNA vez. TextRenderer sobre un
        /// Graphics pide y suelta el HDC en cada llamada (y con PreserveGraphicsClipping arma una región cada vez): con
        /// decenas de renglones por cuadro, eso se lleva casi todo el tiempo de pintar (medido en PruebaTranscripcion).
        /// 🚨 Mientras el HDC está tomado no se puede dibujar con GDI+: el lote se descarga DESPUÉS de los fondos.
        /// </summary>
        internal sealed class Lote
        {
            struct Orden { public string S; public Font F; public Rectangle R; public bool EnRect; public Color C; public TextFormatFlags Banderas; }
            const TextFormatFlags Base = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            readonly List<Orden> ordenes = new List<Orden>(160);
            public int Cuantos => ordenes.Count;

            /// <summary>Un texto de una línea que arranca en (x, y).</summary>
            public void Agregar(string s, Font f, int x, int y, Color c)
            {
                if (!string.IsNullOrEmpty(s)) ordenes.Add(new Orden { S = s, F = f, R = new Rectangle(x, y, 0, 0), C = c, Banderas = Base | TextFormatFlags.SingleLine });
            }

            /// <summary>Un texto dentro de un rectángulo, con su alineación (como Tema.Texto_).</summary>
            public void Agregar(string s, Font f, Rectangle r, Color c, TextFormatFlags banderas = TextFormatFlags.Left | TextFormatFlags.VerticalCenter)
            {
                if (!string.IsNullOrEmpty(s)) ordenes.Add(new Orden { S = s, F = f, R = r, EnRect = true, C = c, Banderas = banderas | Base });
            }

            /// <summary>Descarga el lote: un HDC, un recorte, N textos. O(N) sin idas y vueltas entre GDI+ y GDI.</summary>
            public void Dibujar(Graphics g, Rectangle recorte)
            {
                if (ordenes.Count == 0) return;
                IntPtr hdc = g.GetHdc();
                try
                {
                    int guardado = SaveDC(hdc);
                    IntersectClipRect(hdc, recorte.Left, recorte.Top, recorte.Right, recorte.Bottom);
                    var dc = new DcCrudo(hdc);
                    foreach (var o in ordenes)
                    {
                        if (o.EnRect) TextRenderer.DrawText(dc, o.S, o.F, o.R, o.C, o.Banderas);
                        else TextRenderer.DrawText(dc, o.S, o.F, o.R.Location, o.C, o.Banderas);
                    }
                    RestoreDC(hdc, guardado);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                    ordenes.Clear();
                }
            }
        }

        /// <summary>Un HDC ya tomado, presentado como IDeviceContext: TextRenderer no lo pide ni lo suelta en cada texto.</summary>
        sealed class DcCrudo : IDeviceContext
        {
            readonly IntPtr hdc;
            public DcCrudo(IntPtr h) { hdc = h; }
            public IntPtr GetHdc() => hdc;
            public void ReleaseHdc() { }
            public void Dispose() { }
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern int SaveDC(IntPtr hdc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool RestoreDC(IntPtr hdc, int n);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern int IntersectClipRect(IntPtr hdc, int izq, int arr, int der, int aba);
    }
}
