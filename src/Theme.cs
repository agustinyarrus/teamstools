using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace TeamsTools
{
    /// <summary>Paleta "black dark minimal" + acentos pastel, y las Cascadia finitas.</summary>
    internal static class Tema
    {
        // cromo
        public static readonly Color Fondo = Hex("#08090c");
        public static readonly Color Panel = Hex("#0b0c11");
        public static readonly Color Tarjeta = Hex("#0f1015");
        public static readonly Color TarjetaHover = Hex("#14151c");
        public static readonly Color Borde = Hex("#171922");
        public static readonly Color BordeSuave = Hex("#111319");
        public static readonly Color Texto = Hex("#d3d6df");
        public static readonly Color TextoSuave = Hex("#8b91a3");
        public static readonly Color Apagado = Hex("#585d6e");
        public static readonly Color MuyApagado = Hex("#30333f");
        // acentos pastel (familia Catppuccin, bien desaturada y clara)
        public static readonly Color Malva = Hex("#c4b5fd");
        public static readonly Color Cyan = Hex("#8fd6cc");
        public static readonly Color Crema = Hex("#eedfb8");
        public static readonly Color Salvia = Hex("#b5dfa8");
        public static readonly Color Durazno = Hex("#f6c0a0");
        public static readonly Color Rosa = Hex("#f3b9d2");
        public static readonly Color Cielo = Hex("#a8cff2");
        public static readonly Color Rojo = Hex("#eba0ac");

        public static Color Hex(string h) => ColorTranslator.FromHtml(h);
        public static Color Alpha(Color c, int a) => Color.FromArgb(a, c.R, c.G, c.B);
        public static Color Mezcla(Color a, Color b, float t) =>
            Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        /// <summary>DwmSetWindowAttribute quiere COLORREF (BGR).</summary>
        public static int Bgr(Color c) => (c.B << 16) | (c.G << 8) | c.R;

        // fuentes: cada peso es una sub-familia real (sin negrita sintetizada)
        // ⭐ Cascadia Code ExtraLight en TODOS lados: la jerarquía la dan el tamaño y el color, no el peso.
        static readonly string FamFina = ElegirFamilia("Cascadia Code ExtraLight", "Cascadia Code Light", "Cascadia Code", "Consolas");
        static readonly string FamLight = FamFina;
        static readonly string FamMedia = FamFina;
        static readonly string FamRegular = FamFina;
        // 🚨 el código necesita "Mono" en el NOMBRE: Formato.Corridas y EditorRico detectan un tramo de código por ahí.
        //    Cascadia Mono ExtraLight es el mismo dibujo que Code ExtraLight pero sin ligaduras, y conserva el "Mono".
        static readonly string FamMono = ElegirFamilia("Cascadia Mono ExtraLight", "Cascadia Mono Light", "Cascadia Mono", "Consolas");
        public static bool CascadiaInstalada => FamFina.StartsWith("Cascadia", StringComparison.OrdinalIgnoreCase);

        /// <summary>Factor global sobre todos los tamanos de fuente (config "escalaFuente"; el user pidio -20 %).</summary>
        public static float FactorFuente = 0.8f;
        /// <summary>
        /// Densidad de la interfaz: multiplica TODA la métrica (ver <see cref="Dpi.S(float,int)"/>) y ya viene
        /// aplicada en FactorFuente. 1 = tamaño original; 0,8 = todo un 20 % más chico y más concentrado.
        /// Se configura con "escalaUI" en config.json.
        /// </summary>
        public static float FactorUI = 0.8f;

        // Escalones de peso, del más fino al más sólido. Están los cuatro instalados (verificado).
        static readonly string[] PesosCode =
        {
            ElegirFamilia("Cascadia Code ExtraLight", "Cascadia Code Light", "Cascadia Code", "Consolas"),
            ElegirFamilia("Cascadia Code Light", "Cascadia Code", "Consolas"),
            ElegirFamilia("Cascadia Code SemiLight", "Cascadia Code", "Consolas"),
            ElegirFamilia("Cascadia Code", "Consolas"),
        };
        static readonly string[] PesosMono =
        {
            ElegirFamilia("Cascadia Mono ExtraLight", "Cascadia Mono Light", "Cascadia Mono", "Consolas"),
            ElegirFamilia("Cascadia Mono Light", "Cascadia Mono", "Consolas"),
            ElegirFamilia("Cascadia Mono SemiLight", "Cascadia Mono", "Consolas"),
            ElegirFamilia("Cascadia Mono", "Consolas"),
        };

        const float PisoPt = 5.0f;

        /// <summary>
        /// 🚨 El peso se elige por el TAMAÑO FINAL, no por el rol. Cascadia ExtraLight es precioso en grande y se
        /// deshace en chico: por debajo de ~6 pt los trazos quedan más finos que un píxel y ClearType los pinta
        /// grises y sucios — se ve borroso, no delgado. Cuanto más chica la letra, más cuerpo necesita.
        /// La jerarquía no se pierde: ahora la dan el tamaño, el color Y el peso, que antes era uno solo para todo.
        /// </summary>
        static int Escalon(float ptFinal) => ptFinal >= 10f ? 0 : ptFinal >= 7.5f ? 1 : ptFinal >= 6f ? 2 : 3;

        static readonly Dictionary<string, Font> cache = new Dictionary<string, Font>();
        static Font F(bool mono, float pt, int masCuerpo)
        {
            float real = Math.Max(PisoPt, pt * FactorFuente);
            string fam = (mono ? PesosMono : PesosCode)[Math.Min(3, Escalon(real) + masCuerpo)];
            string k = fam + "|" + real.ToString("0.##");
            lock (cache)
            {
                if (!cache.TryGetValue(k, out var f)) { f = new Font(fam, real, FontStyle.Regular, GraphicsUnit.Point); cache[k] = f; }
                return f;
            }
        }
        public static Font Fina(float pt) => F(false, pt, 0);
        public static Font Light(float pt) => F(false, pt, 0);
        /// <summary>Un escalón más de cuerpo que Fina al mismo tamaño: es lo que separa una etiqueta de su valor.</summary>
        public static Font Media(float pt) => F(false, pt, 1);
        public static Font Regular(float pt) => F(false, pt, 1);
        /// <summary>Fuente de código: Cascadia Mono (conserva "Mono" en el nombre, que es lo que se detecta).</summary>
        public static Font Mono(float pt) => F(true, pt, 0);

        static string ElegirFamilia(params string[] candidatas)
        {
            try
            {
                var instaladas = new HashSet<string>(new InstalledFontCollection().Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var c in candidatas) if (instaladas.Contains(c)) return c;
            }
            catch { }
            return "Consolas";
        }

        // --- dibujo ---
        public static GraphicsPath Redondeado(RectangleF r, float radio)
        {
            var p = new GraphicsPath();
            float d = Math.Max(1f, radio * 2);
            if (r.Width <= 0 || r.Height <= 0) { p.AddRectangle(new RectangleF(r.X, r.Y, Math.Max(r.Width, 1), Math.Max(r.Height, 1))); return p; }
            d = Math.Min(d, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
        public static void Tarjeta_(Graphics g, RectangleF r, float radio, Color fondo, Color borde)
        {
            using (var p = Redondeado(r, radio))
            using (var b = new SolidBrush(fondo))
            using (var pen = new Pen(borde, 1f))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
        }
        public static void Texto_(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter)
        {
            TextRenderer.DrawText(g, s ?? "", f, r, c, flags | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
        public static Size Medir(Graphics g, string s, Font f) => TextRenderer.MeasureText(g, s ?? "", f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        /// <summary>Tarjeta de dato: punto pastel + etiqueta, valor grande, subtexto.</summary>
        public static void TarjetaDato(Graphics g, Rectangle r, float esc, string etiqueta, string valor, string sub, Color acento, Font fValor = null)
        {
            int S(int px) => Dpi.S(esc, px);
            Tarjeta_(g, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(10), Tarjeta, Tarjeta);
            int px0 = r.Left + S(14), pd = S(5);
            using (var b = new SolidBrush(acento)) g.FillEllipse(b, px0, r.Top + S(11) + (S(14) - pd) / 2f, pd, pd);
            Texto_(g, etiqueta, Media(8f), Apagado, new Rectangle(px0 + pd + S(7), r.Top + S(10), r.Width - S(28), S(14)));
            int altoValor = Math.Max(S(24), r.Height - S(10) - S(14) - S(24));
            Texto_(g, valor, fValor ?? Fina(19f), Texto, new Rectangle(px0, r.Top + S(22), r.Width - S(28), altoValor), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Texto_(g, sub, Fina(9f), TextoSuave, new Rectangle(px0, r.Bottom - S(24), r.Width - S(28), S(18)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        /// <summary>Pastilla chica de texto (insignia).</summary>
        public static void Insignia(Graphics g, string s, Font f, Color c, float x, float y, float esc, bool derecha = false)
        {
            var sz = Medir(g, s, f);
            float w = sz.Width + Dpi.S(esc, 12), h = sz.Height + Dpi.S(esc, 6);
            if (derecha) x -= w;
            var r = new RectangleF(x, y, w, h);
            Tarjeta_(g, r, h / 2, Mezcla(Tarjeta, c, 0.14f), Alpha(c, 60));
            Texto_(g, s, f, c, Rectangle.Round(r), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        public static string Relativo(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalSeconds < 0) { d = -d; if (d.TotalMinutes < 1) return "en segundos"; if (d.TotalHours < 1) return $"en {(int)d.TotalMinutes} min"; if (d.TotalDays < 1) return $"en {(int)d.TotalHours} h {(int)d.Minutes} min"; return $"en {(int)d.TotalDays} d"; }
            if (d.TotalSeconds < 60) return "recién";
            if (d.TotalMinutes < 60) return $"hace {(int)d.TotalMinutes} min";
            if (d.TotalHours < 24) return $"hace {(int)d.TotalHours} h";
            return $"hace {(int)d.TotalDays} d";
        }

        /// <summary>Anillo de progreso (0..1) con punta redondeada.</summary>
        public static void Anillo(Graphics g, RectangleF r, float grosor, float fraccion, Color pista, Color color)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pp = new Pen(pista, grosor))
            using (var pc = new Pen(color, grosor) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawEllipse(pp, r);
                float sweep = Math.Max(0.001f, Math.Min(1f, fraccion)) * 360f;
                if (fraccion > 0) g.DrawArc(pc, r, -90, sweep);
            }
            g.SmoothingMode = old;
        }
    }

    /// <summary>
    /// Escala por DPI del monitor donde vive el control, multiplicada por <see cref="Tema.FactorUI"/>.
    /// ⭐ `S` es el ÚNICO punto por donde pasa toda la métrica de la app (márgenes, altos de fila, radios, altura
    /// de los chips, separaciones): tocar el factor acá encoge o agranda la interfaz entera de forma pareja.
    /// Nunca da menos de 1 px para algo que pedía al menos 1, así que los filetes de 1 px no desaparecen.
    /// </summary>
    internal static class Dpi
    {
        public static float Escala(Control c)
        {
            try { uint d = Win32.GetDpiForWindow(c.Handle); if (d > 0) return d / 96f; } catch { }
            using (var g = c.CreateGraphics()) return g.DpiX / 96f;
        }
        public static int S(this Control c, int px) => S(Escala(c), px);
        public static int S(float escala, int px)
        {
            int v = (int)Math.Round(px * escala * Tema.FactorUI);
            return px > 0 && v < 1 ? 1 : v;
        }
        /// <summary>Escala SIN el factor de densidad: para lo que no tiene que encoger, como el tamaño de la ventana.</summary>
        public static int Bruto(float escala, int px) => (int)Math.Round(px * escala);
    }
}
