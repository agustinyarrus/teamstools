using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace TeamsTools
{
    /// <summary>
    /// Vista previa: dibuja un mensaje con formato tal como lo va a ver el otro en Teams — negrita, cursiva,
    /// subrayado, tachado, código, viñetas, numerada y cita. Es el espejo exacto de lo que se va a tipear.
    /// </summary>
    internal sealed class Burbuja : Control
    {
        public Rico Contenido = new Rico();
        public string Etiqueta = "vista previa · así le llega";
        public string Autor = "";
        public string Pie = "";
        public string Vacio = "escribí el mensaje y lo vas viendo acá";
        static readonly Dictionary<string, Font> cacheF = new Dictionary<string, Font>();

        public Burbuja()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            TabStop = false;
        }

        public void Poner(Rico r, string autor = null, string pie = null)
        {
            Contenido = r ?? new Rico();
            if (autor != null) Autor = autor;
            if (pie != null) Pie = pie;
            Invalidate();
        }

        static Font F(bool b, bool i, bool u, bool s, bool code, float pt)
        {
            string k = $"{b}{i}{u}{s}{code}|{pt:0.##}";
            lock (cacheF)
            {
                if (cacheF.TryGetValue(k, out var f)) return f;
                // con todo en ExtraLight la negrita ya no viene de otra familia: acá SÍ tiene que verse,
                // porque la burbuja muestra cómo le llega el mensaje al otro.
                var baseF = code ? Tema.Mono(pt) : Tema.Fina(pt);
                var st = FontStyle.Regular;
                if (b) st |= FontStyle.Bold;
                if (i) st |= FontStyle.Italic;
                if (u) st |= FontStyle.Underline;
                if (s) st |= FontStyle.Strikeout;
                var nf = st == FontStyle.Regular ? baseF : new Font(baseF, st);
                cacheF[k] = nf;
                return nf;
            }
        }

        /// <summary>Dibuja (o sólo mide, con dibujar=false) el contenido y devuelve el alto usado.</summary>
        int Render(Graphics g, int x0, int y0, int ancho, bool dibujar)
        {
            float esc = Dpi.Escala(this);
            int S(int px) => Dpi.S(esc, px);
            const float pt = 10f;
            int alto = Tema.Medir(g, "Ay", F(false, false, false, false, false, pt)).Height;
            int y = y0, num = 0;
            foreach (var l in Contenido.Lineas)
            {
                int sangria = 0; string prefijo = "";
                if (l.Tipo == "vineta") { prefijo = "•"; sangria = S(18); }
                else if (l.Tipo == "numerada") { prefijo = (++num) + "."; sangria = S(22); }
                else if (l.Tipo == "cita") sangria = S(14);
                else if (l.Tipo == "codigo") sangria = S(10);
                if (l.Tipo != "numerada") num = 0;
                int xIni = x0 + sangria, anchoLinea = ancho - sangria;
                int yLinea = y;

                // armar las palabras con su fuente y envolver
                var piezas = new List<Tuple<string, Font, Color>>();
                foreach (var c in l.Corridas)
                {
                    var f = F(c.B, c.I, c.U, c.S, c.Codigo || l.Tipo == "codigo", pt);
                    var col = (c.Codigo || l.Tipo == "codigo") ? Tema.Crema : l.Tipo == "cita" ? Tema.TextoSuave : Tema.Texto;
                    foreach (var pal in Partir(c.Texto)) piezas.Add(Tuple.Create(pal, f, col));
                }
                if (piezas.Count == 0) { y += alto + S(3); continue; }

                int x = xIni, filas = 1;
                foreach (var p in piezas)
                {
                    int w = Tema.Medir(g, p.Item1, p.Item2).Width;
                    if (x > xIni && x + w > x0 + anchoLinea) { x = xIni; y += alto + S(2); filas++; }
                    if (dibujar) Tema.Texto_(g, p.Item1, p.Item2, p.Item3, new Rectangle(x, y, w + S(4), alto), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    x += w;
                }
                int altoBloque = y + alto - yLinea;
                if (dibujar)
                {
                    if (prefijo.Length > 0) Tema.Texto_(g, prefijo, F(false, false, false, false, false, pt), Tema.Malva, new Rectangle(x0, yLinea, sangria - S(4), alto));
                    if (l.Tipo == "cita") using (var pn = new Pen(Tema.Alpha(Tema.Malva, 150), S(2))) g.DrawLine(pn, x0 + S(3), yLinea, x0 + S(3), yLinea + altoBloque);
                    if (l.Tipo == "codigo") { using (var b = new SolidBrush(Tema.Alpha(Tema.Crema, 14))) using (var p2 = Tema.Redondeado(new RectangleF(x0, yLinea - S(2), ancho, altoBloque + S(4)), S(4))) g.FillPath(b, p2); }
                }
                y += alto + S(5);
            }
            return y - y0;
        }

        /// <summary>Parte en palabras conservando los espacios (para envolver sin perder el espaciado).</summary>
        static IEnumerable<string> Partir(string t)
        {
            int i = 0;
            while (i < t.Length)
            {
                int j = t.IndexOf(' ', i);
                if (j < 0) { yield return t.Substring(i); yield break; }
                yield return t.Substring(i, j - i + 1);
                i = j + 1;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float esc = Dpi.Escala(this);
            int S(int px) => Dpi.S(esc, px);
            Tema.Tarjeta_(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), S(10), Tema.Panel, Tema.Panel);
            int pd = S(5);
            using (var b = new SolidBrush(Tema.Cyan)) g.FillEllipse(b, S(14), S(10) + (S(14) - pd) / 2f, pd, pd);
            Tema.Texto_(g, Etiqueta.ToUpperInvariant(), Tema.Media(7.5f), Tema.Apagado, new Rectangle(S(14) + pd + S(7), S(10), Width - S(30), S(14)));

            int top = S(32);
            if (Contenido == null || Contenido.Vacio)
            {
                Tema.Texto_(g, Vacio, Tema.Fina(9f), Tema.Apagado, new Rectangle(S(16), top, Width - S(32), S(20)));
                return;
            }
            // burbuja estilo Teams
            int margen = S(12), anchoTexto = Width - margen * 2 - S(26);
            int altoTexto = Render(g, margen + S(12), top + S(24), anchoTexto, false);
            var rb = new RectangleF(margen, top, Width - margen * 2, Math.Min(Height - top - S(26), altoTexto + S(40)));
            Tema.Tarjeta_(g, rb, S(9), Tema.Mezcla(Tema.Tarjeta, Tema.Malva, 0.05f), Tema.Alpha(Tema.Malva, 45));
            Tema.Texto_(g, (Autor.Length > 0 ? Autor : "vos") + "  ·  ahora", Tema.Media(7.5f), Tema.Apagado, new Rectangle((int)rb.X + S(12), (int)rb.Y + S(8), (int)rb.Width - S(24), S(14)));
            var clip = g.Clip;
            g.SetClip(new RectangleF(rb.X, rb.Y, rb.Width, rb.Height - S(4)));
            Render(g, margen + S(12), top + S(24), anchoTexto, true);
            g.Clip = clip;
            if (Pie.Length > 0) Tema.Texto_(g, Pie, Tema.Fina(8.5f), Tema.Apagado, new Rectangle(margen, Height - S(22), Width - margen * 2, S(16)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Una fila de la ficha: etiqueta a la izquierda, valor a la derecha. Título si Valor es null.</summary>
    internal sealed class Dato
    {
        public string Etiqueta = "", Valor;
        public Color Color = Tema.Texto;
        public static Dato D(string e, string v, Color? c = null) => new Dato { Etiqueta = e, Valor = v ?? "—", Color = c ?? Tema.Texto };
        public static Dato Titulo(string t) => new Dato { Etiqueta = t, Valor = null };
    }

    /// <summary>Ficha de datos: muchas filas etiqueta → valor, chiquitas y prolijas. Para llenar de información sin ruido.</summary>
    internal sealed class Ficha : Control
    {
        public List<Dato> Filas = new List<Dato>();
        public string Etiqueta = "";
        public Color Acento = Tema.Malva;
        public string Vacio = "sin datos";

        public Ficha()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            TabStop = false;
        }

        public void Poner(params Dato[] filas) { Filas = filas.ToList(); Invalidate(); }
        public void Poner(List<Dato> filas) { Filas = filas ?? new List<Dato>(); Invalidate(); }

        /// <summary>Escalera de tamaños de letra de las filas, de la más cómoda a la más apretada.</summary>
        static readonly float[] Escalera = { 8.5f, 8f, 7.5f, 7f };

        /// <summary>Alto de línea nominal de una fuente (en px de este control): la medida cómoda para el alto preferido.</summary>
        static int AltoLinea(Graphics g, Font f) => Tema.Medir(g, "Ág", f).Height;

        static readonly Dictionary<string, int> tinta = new Dictionary<string, int>();
        /// <summary>
        /// Alto de la TINTA de «Ág» (acento arriba, descendente abajo): lo que de verdad ocupa una línea dibujada.
        /// MeasureText devuelve el alto nominal, que en Cascadia es un 20 % más que el dibujo y obligaba a filas más
        /// flojas de lo necesario (y a tirar filas). Se mide con GraphicsPath una vez por fuente y DPI (cache).
        /// </summary>
        static int AltoTinta(Graphics g, Font f)
        {
            string k = f.Name + "|" + f.SizeInPoints.ToString("0.##") + "|" + g.DpiY.ToString("0");
            lock (tinta)
            {
                if (tinta.TryGetValue(k, out int h)) return h;
                using (var p = new GraphicsPath())
                {
                    p.AddString("Ág", f.FontFamily, (int)f.Style, f.SizeInPoints * g.DpiY / 72f, PointF.Empty, StringFormat.GenericTypographic);
                    h = (int)Math.Ceiling(p.GetBounds().Height);
                }
                tinta[k] = h;
                return h;
            }
        }

        /// <summary>Filas en unidades: un dato ocupa una, un título dos (la raya y el rótulo).</summary>
        int Unidades => Filas.Count + Filas.Count(d => d.Valor == null);

        /// <summary>
        /// Alto con el que TODAS las filas entran cómodas (letra de 8,5 pt en filas de S(17) o lo que mida la línea).
        /// Los layouts lo usan para repartir una columna según lo que cada ficha pide, en vez de por porcentajes fijos
        /// que dejaban a una con medio panel vacío y a la otra con las líneas encimadas.
        /// </summary>
        public int AltoPreferido()
        {
            float esc = Dpi.Escala(this);
            int S(int px) => Dpi.S(esc, px);
            if (Filas.Count == 0) return S(30) + S(18) + S(8);
            int fila;
            using (var g = CreateGraphics()) fila = Math.Max(S(17), AltoLinea(g, Tema.Media(Escalera[0])) + S(2));
            return S(30) + Unidades * fila + S(4);
        }

        /// <summary>
        /// Alto por debajo del cual la ficha empieza a perder filas: todas apretadas (la tinta de la letra de 7,5 pt).
        /// Es lo que una ficha cede antes de que el reparto le saque lugar a un gráfico, que no sabe apretarse.
        /// </summary>
        public int AltoMinimo()
        {
            float esc = Dpi.Escala(this);
            int S(int px) => Dpi.S(esc, px);
            if (Filas.Count == 0) return AltoPreferido();
            int fila;
            using (var g = CreateGraphics()) fila = Math.Max(S(10), AltoTinta(g, Tema.Media(Escalera[2])));   // 7,5 pt, apretada
            return S(30) + Unidades * fila + S(4);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float esc = Dpi.Escala(this);
            int S(int px) => Dpi.S(esc, px);
            Tema.Tarjeta_(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), S(10), Tema.Panel, Tema.Panel);
            int pd = S(5);
            using (var b = new SolidBrush(Acento)) g.FillEllipse(b, S(14), S(10) + (S(14) - pd) / 2f, pd, pd);
            Tema.Texto_(g, Etiqueta.ToUpperInvariant(), Tema.Media(7.5f), Tema.Apagado, new Rectangle(S(14) + pd + S(7), S(10), Width - S(30), S(14)));
            if (Filas.Count == 0) { Tema.Texto_(g, Vacio, Tema.Fina(9f), Tema.Apagado, new Rectangle(S(16), S(30), Width - S(32), S(18))); return; }
            // alto de fila que hace entrar TODO (con mínimo legible); los títulos ocupan una fila y pico
            int disponible = Height - S(30) - S(4);
            int unidades = Unidades;
            int fila = Math.Max(S(10), Math.Min(S(17), unidades > 0 ? disponible / unidades : S(17)));
            // la letra baja un escalón por cada tanto de fila que falta (los umbrales están afinados a ojo para que
            // una ficha llena se lea pareja); con muy poco lugar hay un cuarto escalón de 7 pt
            float ptF = fila >= S(16) ? Escalera[0] : fila >= S(14) ? Escalera[1] : fila >= S(12) ? Escalera[2] : Escalera[3];
            // 🚨 Los umbrales dan por hecho que la fuente escala igual que la métrica; con escalaFuente distinto de 0,8
            //    la letra puede quedar más alta que la fila y las líneas se PISAN. La fila nunca baja de la tinta
            //    medida de la letra elegida: si no entran todas, sobran filas abajo (se cortan), pero nada se encima.
            fila = Math.Max(fila, AltoTinta(g, Tema.Media(ptF)));
            int y = S(30), xE = S(16);
            var fe = Tema.Fina(ptF); var fv = Tema.Media(ptF); var ft = Tema.Media(ptF - 1f);
            // 🚨 La etiqueta se llevaba el 52 % SIEMPRE: los valores largos («Driscoll · 6.2a», «0,36-0,54×
            //    tiempo real», un nombre de archivo) salían cortados con lugar de sobra a la izquierda.
            //    Pero mirar SOLO el valor rompe al revés, y se cortan las etiquetas («días que lo viste
            //    escribir»). Hay que medir los dos lados: si entran, cada uno toma lo suyo y el hueco queda
            //    en el medio; si no entran, se reparte en proporción a lo que cada lado pide — cortar
            //    parejo es mejor que castigar siempre al mismo.
            int libreFila = Width - xE - S(16);
            int wL = 0, wV = 0;
            foreach (var d in Filas)
            {
                if (d.Valor == null) continue;
                wL = Math.Max(wL, Tema.Medir(g, d.Etiqueta, fe).Width);
                wV = Math.Max(wV, Tema.Medir(g, d.Valor, fv).Width);
            }
            wL += S(8); wV += S(6);
            int wE;
            if (wL + wV <= libreFila) wE = libreFila - wV;
            else
            {
                double pide = Math.Max(1, wL + wV);
                wE = Math.Max(S(40), (int)(libreFila * (wL / pide)));
                wV = Math.Max(S(30), libreFila - wE);
            }
            foreach (var d in Filas)
            {
                if (y + fila > Height - S(4)) break;
                if (d.Valor == null)
                {
                    using (var pl = new Pen(Tema.BordeSuave)) g.DrawLine(pl, xE, y + fila / 2, Width - S(16), y + fila / 2);
                    y += fila;
                    if (y + fila > Height - S(4)) break;
                    Tema.Texto_(g, d.Etiqueta.ToUpperInvariant(), ft, Tema.MuyApagado, new Rectangle(xE, y, Width - S(32), fila));
                    y += fila;
                    continue;
                }
                Tema.Texto_(g, d.Etiqueta, fe, Tema.Apagado, new Rectangle(xE, y, wE, fila), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                Tema.Texto_(g, d.Valor, fv, d.Color, new Rectangle(xE + wE, y, Width - xE - wE - S(14), fila), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                y += fila;
            }
        }
    }

    /// <summary>
    /// Serie temporal: área + línea con la historia de un valor. Dibuja líneas de umbral y de peligro, y marca
    /// los eventos. Sirve para ver el diente de sierra de la inactividad (cada caída a cero es un toque).
    /// </summary>
    internal sealed class Serie : Control
    {
        readonly List<float> valores = new List<float>();
        readonly List<int> eventos = new List<int>();     // índices donde pasó algo (un toque)
        public int Capacidad = 900;
        public string Etiqueta = "", Vacio = "juntando datos…";
        public Color Color = Tema.Cyan;
        public float? Umbral, Limite;
        public string EtiquetaUmbral = "", EtiquetaLimite = "";
        public Func<float, string> Formato = v => ((int)v).ToString();
        public int SegundosPorMuestra = 1;

        public Serie()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            TabStop = false;
        }

        public void Empujar(float v, bool evento = false)
        {
            valores.Add(v);
            if (evento) eventos.Add(valores.Count - 1);
            while (valores.Count > Capacidad)
            {
                valores.RemoveAt(0);
                for (int i = eventos.Count - 1; i >= 0; i--) { eventos[i]--; if (eventos[i] < 0) eventos.RemoveAt(i); }
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float esc = Dpi.Escala(this);
            int S(int px) => Dpi.S(esc, px);
            Tema.Tarjeta_(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), S(10), Tema.Panel, Tema.Panel);
            int pd = S(5);
            using (var b = new SolidBrush(Color)) g.FillEllipse(b, S(14), S(10) + (S(14) - pd) / 2f, pd, pd);
            Tema.Texto_(g, Etiqueta.ToUpperInvariant(), Tema.Media(7.5f), Tema.Apagado, new Rectangle(S(14) + pd + S(7), S(10), (int)(Width * 0.6), S(14)));
            if (valores.Count < 2) { Tema.Texto_(g, Vacio, Tema.Fina(9f), Tema.Apagado, new Rectangle(S(16), S(32), Width - S(32), S(18))); return; }

            float ult = valores[valores.Count - 1], max = valores.Max(), prom = valores.Average();
            string stats = $"ahora {Formato(ult)}  ·  máx {Formato(max)}  ·  prom {Formato(prom)}";
            Tema.Texto_(g, stats, Tema.Media(7.5f), Tema.TextoSuave, new Rectangle((int)(Width * 0.42), S(10), (int)(Width * 0.58) - S(14), S(14)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

            var r = new Rectangle(S(14), S(30), Width - S(28), Height - S(30) - S(18));
            float techo = Math.Max(1f, Math.Max(max, Limite ?? (Umbral ?? 1f) * 1.4f)) * 1.08f;
            Func<float, float> Y = v => r.Bottom - (v / techo) * r.Height;
            Func<int, float> X = i => r.Left + (valores.Count <= 1 ? 0 : (float)i / (valores.Count - 1) * r.Width);

            // líneas de referencia
            if (Umbral.HasValue && Umbral.Value < techo)
            {
                float y = Y(Umbral.Value);
                using (var p = new Pen(Tema.Alpha(Tema.Cyan, 90), 1f) { DashStyle = DashStyle.Dash }) g.DrawLine(p, r.Left, y, r.Right, y);
                if (EtiquetaUmbral.Length > 0) Tema.Texto_(g, EtiquetaUmbral, Tema.Fina(7.5f), Tema.Alpha(Tema.Cyan, 190), new Rectangle(r.Left + S(4), (int)y - S(13), r.Width, S(12)));
            }
            if (Limite.HasValue && Limite.Value < techo)
            {
                float y = Y(Limite.Value);
                using (var p = new Pen(Tema.Alpha(Tema.Rosa, 110), 1f) { DashStyle = DashStyle.Dash }) g.DrawLine(p, r.Left, y, r.Right, y);
                if (EtiquetaLimite.Length > 0) Tema.Texto_(g, EtiquetaLimite, Tema.Fina(7.5f), Tema.Alpha(Tema.Rosa, 200), new Rectangle(r.Left + S(4), (int)y - S(13), r.Width, S(12)));
            }

            // área + línea
            var pts = new List<PointF>();
            for (int i = 0; i < valores.Count; i++) pts.Add(new PointF(X(i), Y(valores[i])));
            var area = new List<PointF>(pts) { new PointF(pts[pts.Count - 1].X, r.Bottom), new PointF(pts[0].X, r.Bottom) };
            using (var br = new LinearGradientBrush(new PointF(0, r.Top), new PointF(0, r.Bottom), Tema.Alpha(Color, 85), Tema.Alpha(Color, 8)))
                g.FillPolygon(br, area.ToArray());
            using (var p = new Pen(Tema.Alpha(Color, 230), S(2)) { LineJoin = LineJoin.Round }) g.DrawLines(p, pts.ToArray());

            // marcas de los eventos (toques)
            foreach (var i in eventos)
            {
                if (i < 0 || i >= pts.Count) continue;
                using (var b = new SolidBrush(Tema.Crema)) g.FillEllipse(b, pts[i].X - S(2), pts[i].Y - S(2), S(4), S(4));
            }

            // eje de tiempo
            int segs = valores.Count * SegundosPorMuestra;
            Tema.Texto_(g, segs >= 60 ? $"hace {segs / 60} min" : $"hace {segs} s", Tema.Fina(7.5f), Tema.MuyApagado, new Rectangle(r.Left, r.Bottom + S(2), r.Width / 2, S(14)));
            Tema.Texto_(g, "ahora", Tema.Fina(7.5f), Tema.MuyApagado, new Rectangle(r.Left + r.Width / 2, r.Bottom + S(2), r.Width / 2, S(14)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }

    internal sealed class Barra { public string Etiqueta = ""; public int Valor; public Color Color = Tema.Malva; public string Extra = ""; }

    /// <summary>Gráfico de barras horizontal, minimalista: etiqueta, barra pastel y número. Para distribuciones.</summary>
    internal sealed class Barras : Control
    {
        public List<Barra> Datos = new List<Barra>();
        public string Etiqueta = "";
        public string Vacio = "sin datos todavía";
        public string Total = "";

        public Barras()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            TabStop = false;
        }

        public void Poner(List<Barra> d, string total = null) { Datos = d ?? new List<Barra>(); if (total != null) Total = total; Invalidate(); }

        /// <summary>Alto con el que entran todas las barras vivas: cabecera + una fila por barra + aire abajo.</summary>
        public int AltoPreferido()
        {
            float esc = Dpi.Escala(this);
            int vivas = Math.Max(1, Datos.Count(d => d.Valor > 0));
            return Dpi.S(esc, 32) + vivas * Dpi.S(esc, 20) + Dpi.S(esc, 6);
        }

        /// <summary>Alto con al menos las tres barras más altas (vienen ordenadas): lo que un gráfico cede como mucho.</summary>
        public int AltoMinimo()
        {
            float esc = Dpi.Escala(this);
            int vivas = Math.Max(1, Math.Min(3, Datos.Count(d => d.Valor > 0)));
            return Dpi.S(esc, 32) + vivas * Dpi.S(esc, 20) + Dpi.S(esc, 6);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float esc = Dpi.Escala(this);
            int S(int px) => Dpi.S(esc, px);
            Tema.Tarjeta_(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), S(10), Tema.Panel, Tema.Panel);
            int pd = S(5);
            using (var b = new SolidBrush(Datos.Count > 0 ? Datos[0].Color : Tema.MuyApagado)) g.FillEllipse(b, S(14), S(10) + (S(14) - pd) / 2f, pd, pd);
            Tema.Texto_(g, Etiqueta.ToUpperInvariant(), Tema.Media(7.5f), Tema.Apagado, new Rectangle(S(14) + pd + S(7), S(10), Width - S(120), S(14)));
            if (Total.Length > 0) Tema.Texto_(g, Total, Tema.Media(7.5f), Tema.Apagado, new Rectangle(Width - S(130), S(10), S(116), S(14)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            var vivos = Datos.Where(d => d.Valor > 0).ToList();
            if (vivos.Count == 0) { Tema.Texto_(g, Vacio, Tema.Fina(9f), Tema.Apagado, new Rectangle(S(16), S(30), Width - S(32), S(20))); return; }
            int max = Math.Max(1, vivos.Max(d => d.Valor));
            int y = S(32), fila = S(20);
            var fe = Tema.Fina(9f); var fv = Tema.Media(9f);
            // 🚨 el ancho de la etiqueta era 34 % fijo, y textos como «ocupado / no molestar» o
            //    «Sosa + Aguirre» salían cortados aunque sobrara lugar al lado de la barra. Ahora se mide
            //    la más larga y se le da lo que pide, con un techo para que la barra nunca desaparezca.
            int wEtq = 0;
            foreach (var d in vivos) wEtq = Math.Max(wEtq, Tema.Medir(g, d.Etiqueta, fe).Width);
            wEtq = Math.Max((int)(Width * 0.22), Math.Min((int)(Width * 0.52), wEtq + S(6)));
            int xEtq = S(16), xBar = xEtq + wEtq + S(8), wBarMax = Width - xBar - S(68);
            foreach (var d in vivos)
            {
                if (y + fila > Height - S(6)) break;
                Tema.Texto_(g, d.Etiqueta, fe, Tema.TextoSuave, new Rectangle(xEtq, y, wEtq, fila), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                int w = Math.Max(S(3), (int)Math.Round(wBarMax * (d.Valor / (double)max)));
                using (var p = Tema.Redondeado(new RectangleF(xBar, y + fila / 2f - S(4), wBarMax, S(8)), S(4)))
                using (var b = new SolidBrush(Tema.Alpha(Tema.MuyApagado, 90))) g.FillPath(b, p);
                using (var p = Tema.Redondeado(new RectangleF(xBar, y + fila / 2f - S(4), w, S(8)), S(4)))
                using (var b = new SolidBrush(Tema.Alpha(d.Color, 210))) g.FillPath(b, p);
                // 🚨 el Extra REEMPLAZA al número: es el mismo dato bien escrito («6h27» en vez de 23220).
                //    Concatenarlos desbordaba la caja y se veía el número cortado por la mitad.
                string txt = d.Extra.Length > 0 ? d.Extra : d.Valor.ToString();
                Tema.Texto_(g, txt, fv, d.Color, new Rectangle(Width - S(64), y, S(56), fila), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                y += fila;
            }
        }
    }
}
