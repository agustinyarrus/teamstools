using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace TeamsTools
{
    internal sealed class Columna
    {
        public string Titulo = "";
        public float Peso = 1f;          // reparto del ancho
        public bool Derecha;             // alinear a la derecha (números)
        public bool Mono;                // fuente de código (patrones, regex)
        public int MinAncho;             // en px lógicos
        /// <summary>Ancho fijado por el usuario arrastrando el borde, en px lógicos. 0 = reparto automático.</summary>
        public int AnchoUsuario;
    }

    internal sealed class Celda
    {
        public string Texto = "";
        public Color? Color;
        public bool Fuerte;              // resaltado (SemiBold)
        public string Insignia = "";     // pastilla chiquita al lado
        public Color ColorInsignia = Tema.Malva;
        public static Celda C(string t, Color? c = null, bool fuerte = false) => new Celda { Texto = t ?? "", Color = c, Fuerte = fuerte };
    }

    internal sealed class FilaTabla
    {
        public List<Celda> Celdas = new List<Celda>();
        public object Tag;
        public bool Apagada;
        public Color Punto = Color.Empty;   // punto de color al inicio de la fila
        public static FilaTabla F(object tag, Color punto, params Celda[] celdas) => new FilaTabla { Tag = tag, Punto = punto, Celdas = celdas.ToList() };
    }

    /// <summary>
    /// Tabla dibujada a mano: encabezados en versalita, filas con punto de color, selección, hover y rueda.
    /// Minimalista y densa — para ver todo el detalle de un vistazo.
    /// </summary>
    internal sealed class Tabla : Control, IRueda
    {
        public List<Columna> Columnas = new List<Columna>();
        public List<FilaTabla> Filas = new List<FilaTabla>();
        public string Etiqueta = "", Vacio = "sin filas", Resumen = "";
        bool cargando;
        /// <summary>
        /// Esperando el primer dato: en vez de «sin filas» (que miente: todavía no se sabe) se dibujan filas
        /// esqueleto con un destello que las cruza. Se apaga solo con el primer <see cref="Poner"/> con filas.
        /// </summary>
        public bool Cargando
        {
            get => cargando;
            set
            {
                if (cargando == value) return;
                cargando = value;
                if (cargando) Animacion.Encender(this); else Animacion.Apagar(this);
                Invalidate();
            }
        }
        public int Seleccion = -1;
        public int AltoFila = 24;
        public bool MostrarPunto = true;
        int desplaz, hover = -1;
        public event EventHandler SeleccionCambio;
        public event EventHandler DobleClic;

        // --- columnas arrastrables: los bordes que dejó el último pintado, para poder engancharlos
        int[] bordes = new int[0];
        int arrastrando = -1, xArrastre, anchoArrastre;
        readonly ToolTip globo = new ToolTip { InitialDelay = 400, ReshowDelay = 120, AutoPopDelay = 15000, ShowAlways = true };
        string globoTexto = "";

        /// <summary>Vuelve todas las columnas al reparto automático.</summary>
        public void SoltarAnchos() { foreach (var c in Columnas) c.AnchoUsuario = 0; Invalidate(); }

        public Tabla()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            TabStop = false;
        }

        public FilaTabla Actual => Seleccion >= 0 && Seleccion < Filas.Count ? Filas[Seleccion] : null;

        public void Poner(List<FilaTabla> filas, object mantener = null, string resumen = null)
        {
            Filas = filas ?? new List<FilaTabla>();
            if (Filas.Count > 0) Cargando = false;        // llegó el primer dato: el esqueleto se va solo
            if (resumen != null) Resumen = resumen;
            if (mantener != null) Seleccion = Filas.FindIndex(f => Equals(f.Tag, mantener));
            else if (Seleccion >= Filas.Count) Seleccion = Filas.Count - 1;
            Invalidate();
        }

        public void Seleccionar(int i) { Seleccion = i; Invalidate(); SeleccionCambio?.Invoke(this, EventArgs.Empty); }

        int TopFilas => Dpi.S(Dpi.Escala(this), Etiqueta.Length > 0 ? 46 : 26);
        int FilaEn(int y)
        {
            float esc = Dpi.Escala(this);
            int fh = Dpi.S(esc, AltoFila);
            if (y < TopFilas) return -1;
            int i = (y - TopFilas + desplaz) / fh;
            return i >= 0 && i < Filas.Count ? i : -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int b = BordeEn(e.X, e.Y);
            if (e.Button == MouseButtons.Left && b >= 0)
            {
                float esc = Dpi.Escala(this);
                int libre = Width - Dpi.S(esc, 24) - (MostrarPunto ? Dpi.S(esc, 14) : 0);
                arrastrando = b; xArrastre = e.X;
                anchoArrastre = Columnas[b].AnchoUsuario > 0
                    ? Columnas[b].AnchoUsuario
                    : (int)Math.Round(Anchos(libre)[b] / Math.Max(0.1f, esc));
                Capture = true;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (arrastrando >= 0)
            {
                float esc = Dpi.Escala(this);
                int delta = (int)Math.Round((e.X - xArrastre) / Math.Max(0.1f, esc));
                Columnas[arrastrando].AnchoUsuario = Math.Max(24, anchoArrastre + delta);
                Invalidate();
                return;
            }
            Cursor = BordeEn(e.X, e.Y) >= 0 ? Cursors.SizeWE : Cursors.Default;
            int h = FilaEn(e.Y);
            if (h != hover) { hover = h; Invalidate(); }
            MostrarGlobo(e.X, e.Y, h);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (arrastrando >= 0) { arrastrando = -1; Capture = false; }
            base.OnMouseUp(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = -1; Cursor = Cursors.Default;
            if (globoTexto.Length > 0) { globo.Hide(this); globoTexto = ""; }
            Invalidate(); base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (BordeEn(e.X, e.Y) >= 0) return;   // el clic en un borde es para arrastrar, no para elegir fila
            int i = FilaEn(e.Y); if (i >= 0) Seleccionar(i);
            base.OnMouseClick(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            // doble clic en el borde: la columna toma justo el ancho de su contenido, y de nuevo la suelta
            int b = BordeEn(e.X, e.Y);
            if (b >= 0)
            {
                Columnas[b].AnchoUsuario = Columnas[b].AnchoUsuario > 0 ? 0 : AnchoIdeal(b);
                Invalidate(); return;
            }
            int i = FilaEn(e.Y); if (i >= 0) { Seleccion = i; DobleClic?.Invoke(this, EventArgs.Empty); }
            base.OnMouseDoubleClick(e);
        }

        /// <summary>
        /// Si la celda bajo el mouse no entra en su columna, muestra el texto completo en un globo. Es la red
        /// de seguridad: por más que se puedan arrastrar los bordes, ningún dato queda inaccesible.
        /// </summary>
        void MostrarGlobo(int x, int y, int filaIdx)
        {
            string texto = "";
            if (filaIdx >= 0 && filaIdx < Filas.Count && bordes.Length == Columnas.Count)
            {
                var f = Filas[filaIdx];
                float esc = Dpi.Escala(this);
                int x0 = Dpi.S(esc, 12) + (MostrarPunto ? Dpi.S(esc, 14) : 0);
                for (int k = 0; k < Columnas.Count && k < f.Celdas.Count; k++)
                {
                    int izq = k == 0 ? x0 : bordes[k - 1];
                    if (x < izq || x > bordes[k]) continue;
                    var ce = f.Celdas[k];
                    int disp = bordes[k] - izq - Dpi.S(esc, 6);
                    if (ce.Texto.Length > 0 && Medir(ce.Texto, FuenteDe(Columnas[k], ce.Fuerte)) > disp) texto = ce.Texto;
                    break;
                }
            }
            if (texto == globoTexto) return;
            globoTexto = texto;
            if (texto.Length == 0) globo.Hide(this);
            else globo.Show(texto, this, x + Dpi.S(Dpi.Escala(this), 14), y + Dpi.S(Dpi.Escala(this), 20), 15000);
        }
        public void Rueda(int delta)
        {
            float esc = Dpi.Escala(this);
            int fh = Dpi.S(esc, AltoFila);
            int utilR = Math.Max(fh, Height - TopFilas - Dpi.S(esc, 6));
            int max = Math.Max(0, Filas.Count * fh - (utilR / fh) * fh);
            desplaz = Math.Max(0, Math.Min(max, desplaz - Math.Sign(delta) * fh * 2));
            Invalidate();
        }
        protected override void OnMouseWheel(MouseEventArgs e) { Rueda(e.Delta); base.OnMouseWheel(e); }

        /// <summary>
        /// El piso de una columna: lo que declaró el que la definió, pero nunca menos de lo que mide su
        /// propio encabezado.
        /// 🚨 Sin esto, títulos como «sin conexión» o «% disponible» salían siempre cortados
        /// («SIN CON …», «% DISPO …») por más ancha que estuviera la ventana, porque el MinAncho escrito a
        /// ojo se quedó corto y el reparto por peso le daba todo el sobrante a las columnas de texto.
        /// </summary>
        int MinimoDe(int i, float esc)
        {
            int declarado = Dpi.S(esc, Columnas[i].MinAncho);
            int titulo = Medir(Columnas[i].Titulo.ToUpperInvariant(), Tema.Media(7f)) + Dpi.S(esc, 12);
            return Math.Max(declarado, titulo);
        }

        int[] Anchos(int ancho)
        {
            float esc = Dpi.Escala(this);
            int n = Columnas.Count;
            var res = new int[n];
            if (n == 0) return res;

            // 1) el piso de cada una: el declarado o su encabezado, lo que sea más grande
            var piso = new int[n];
            int usado = 0; float pesoLibre = 0;
            for (int i = 0; i < n; i++)
            {
                if (Columnas[i].AnchoUsuario > 0) { piso[i] = Dpi.S(esc, Columnas[i].AnchoUsuario); }
                else { piso[i] = MinimoDe(i, esc); pesoLibre += Columnas[i].Peso; }
                usado += piso[i];
            }

            // 2) si ni los pisos entran, se encogen todos en proporción: cortar parejo es mejor que
            //    dejar dos columnas cómodas y las otras ocho pisadas contra el borde
            if (usado > ancho && usado > 0)
            {
                double f = ancho / (double)usado;
                for (int i = 0; i < n; i++) res[i] = Math.Max(Dpi.S(esc, 18), (int)Math.Floor(piso[i] * f));
                return res;
            }

            // 3) lo que sobra se reparte por peso entre las que no fijó el usuario
            int libre = ancho - usado;
            for (int i = 0; i < n; i++)
                res[i] = piso[i] + (Columnas[i].AnchoUsuario <= 0 && pesoLibre > 0.001f
                    ? (int)Math.Round(libre * (Columnas[i].Peso / pesoLibre)) : 0);
            return res;
        }

        /// <summary>La fuente con la que se pinta una celda de esta columna. Hace falta para poder medir.</summary>
        Font FuenteDe(Columna c, bool fuerte) => c.Mono ? Tema.Mono(8f) : fuerte ? Tema.Media(8.5f) : Tema.Fina(8.5f);

        static int Medir(string t, Font f) =>
            TextRenderer.MeasureText(t ?? "", f, new Size(int.MaxValue, 40),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;

        /// <summary>Ancho que necesitaría la columna para que NADA quede cortado, en px lógicos.</summary>
        public int AnchoIdeal(int i)
        {
            if (i < 0 || i >= Columnas.Count) return 0;
            float esc = Dpi.Escala(this);
            var col = Columnas[i];
            int w = Medir(col.Titulo.ToUpperInvariant(), Tema.Media(7f));
            foreach (var f in Filas)
            {
                if (i >= f.Celdas.Count) continue;
                var ce = f.Celdas[i];
                int a = Medir(ce.Texto, FuenteDe(col, ce.Fuerte));
                if (ce.Insignia.Length > 0) a += Medir(ce.Insignia, Tema.Media(7f)) + Dpi.S(esc, 18);
                if (a > w) w = a;
            }
            return (int)Math.Ceiling((w + Dpi.S(esc, 14)) / Math.Max(0.1f, esc));
        }

        /// <summary>Le da a cada columna justo el ancho que su contenido necesita.</summary>
        public void AjustarTodas() { for (int i = 0; i < Columnas.Count; i++) Columnas[i].AnchoUsuario = AnchoIdeal(i); Invalidate(); }

        /// <summary>El borde de columna que está bajo el mouse en la banda de encabezados, o -1.</summary>
        int BordeEn(int x, int y)
        {
            if (y > TopFilas || bordes.Length == 0) return -1;
            int tol = Dpi.S(Dpi.Escala(this), 5);
            for (int i = 0; i < bordes.Length; i++) if (Math.Abs(x - bordes[i]) <= tol) return i;
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float esc = Dpi.Escala(this);
            int S(int px) => Dpi.S(esc, px);
            Tema.Tarjeta_(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), S(10), Tema.Panel, Tema.Panel);

            if (Etiqueta.Length > 0)
            {
                int pd = S(5);
                using (var b = new SolidBrush(Tema.Malva)) g.FillEllipse(b, S(14), S(10) + (S(14) - pd) / 2f, pd, pd);
                // 🚨 etiqueta y resumen se repartían por porcentaje y con la tabla angosta se pisaban letra sobre
                //    letra. Ahora se mide el resumen y se le reserva justo eso; si no queda lugar, no se dibuja.
                var fEnc = Tema.Media(7.5f);
                int xEti = S(14) + pd + S(7), derecha = Width - S(14);
                int anchoRes = Resumen.Length > 0 ? Tema.Medir(g, Resumen, fEnc).Width : 0;
                bool cabeRes = anchoRes > 0 && derecha - xEti - anchoRes >= S(70);
                if (cabeRes) Tema.Texto_(g, Resumen, fEnc, Tema.Apagado, new Rectangle(derecha - anchoRes, S(10), anchoRes, S(14)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                int anchoEti = (cabeRes ? derecha - anchoRes - S(10) : derecha) - xEti;
                Tema.Texto_(g, Etiqueta.ToUpperInvariant(), fEnc, Tema.Apagado, new Rectangle(xEti, S(10), Math.Max(S(20), anchoEti), S(14)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }

            int x0 = S(12), ancho = Width - S(24);
            int xPunto = MostrarPunto ? S(14) : 0;
            var anchos = Anchos(ancho - xPunto);
            int yEnc = TopFilas - S(17);

            // encabezados (y de paso se anotan los bordes, que son las asas para arrastrar)
            int xh = x0 + xPunto;
            var fh0 = Tema.Media(7f);
            if (bordes.Length != Columnas.Count) bordes = new int[Columnas.Count];
            for (int i = 0; i < Columnas.Count; i++)
            {
                var c = Columnas[i];
                Tema.Texto_(g, c.Titulo.ToUpperInvariant(), fh0, c.AnchoUsuario > 0 ? Tema.Apagado : Tema.MuyApagado,
                    new Rectangle(xh, yEnc, anchos[i] - S(6), S(14)),
                    (c.Derecha ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                xh += anchos[i];
                bordes[i] = xh;
                // una rayita tenue marca dónde agarrar; se enciende cuando el mouse está encima
                if (i < Columnas.Count - 1)
                    using (var pb = new Pen(Tema.Alpha(Tema.Apagado, arrastrando == i ? 200 : 60)))
                        g.DrawLine(pb, xh - S(3), yEnc + S(1), xh - S(3), yEnc + S(12));
            }
            using (var p = new Pen(Tema.BordeSuave)) g.DrawLine(p, x0, TopFilas - S(3), Width - S(12), TopFilas - S(3));

            if (Filas.Count == 0)
            {
                if (cargando) { Brillo.Filas(g, new Rectangle(x0 + xPunto, TopFilas + S(2), ancho - xPunto, Math.Max(S(20), Height - TopFilas - S(8))), anchos, S(AltoFila), esc, Tema.Panel); return; }
                Tema.Texto_(g, Vacio, Tema.Fina(9f), Tema.Apagado, new Rectangle(x0 + S(4), TopFilas + S(4), ancho, S(20)));
                return;
            }

            int fila = S(AltoFila);
            // 🚨 recortar en el BORDE de una fila, no donde caiga el alto del control: si no, la última
            //    queda dibujada por la mitad y parece que la tabla se mete debajo de los botones de abajo.
            //    Un espacio vacío es prolijo; media fila cortada es un error visual.
            int alturaUtil = Math.Max(fila, Height - TopFilas - S(6));
            int abajo = TopFilas + (alturaUtil / fila) * fila;
            g.SetClip(new Rectangle(0, TopFilas, Width, abajo - TopFilas));
            int y = TopFilas - desplaz;
            var ff = Tema.Fina(8.5f); var ffF = Tema.Media(8.5f); var fm = Tema.Mono(8f);
            for (int i = 0; i < Filas.Count; i++, y += fila)
            {
                // 🚨 no alcanza con recortar (SetClip): una fila que empieza antes del borde y termina
                //    después se dibuja IGUAL, partida al medio por el recorte. Hay que no dibujarla.
                if (y + fila > abajo) break;
                if (y + fila < TopFilas) continue;
                var f = Filas[i];
                bool sel = i == Seleccion, hov = i == hover;
                var rf = new Rectangle(x0 - S(4), y, ancho + S(8), fila - 1);
                if (sel) Tema.Tarjeta_(g, new RectangleF(rf.X, rf.Y, rf.Width, rf.Height), S(6), Tema.TarjetaHover, Tema.Alpha(Tema.Malva, 70));
                else if (hov) Tema.Tarjeta_(g, new RectangleF(rf.X, rf.Y, rf.Width, rf.Height), S(6), Tema.Tarjeta, Tema.Tarjeta);
                else if (i % 2 == 1) { using (var b = new SolidBrush(Tema.Alpha(Tema.Tarjeta, 120))) using (var p2 = Tema.Redondeado(new RectangleF(rf.X, rf.Y, rf.Width, rf.Height), S(6))) g.FillPath(b, p2); }

                if (MostrarPunto && f.Punto != Color.Empty)
                {
                    int d = S(6);
                    using (var b = new SolidBrush(f.Apagada ? Tema.MuyApagado : f.Punto)) g.FillEllipse(b, x0 + S(2), y + (fila - d) / 2f, d, d);
                }
                int xc = x0 + xPunto;
                for (int k = 0; k < Columnas.Count && k < f.Celdas.Count; k++)
                {
                    var col = Columnas[k]; var ce = f.Celdas[k];
                    var fuente = col.Mono ? fm : ce.Fuerte ? ffF : ff;
                    var color = f.Apagada ? Tema.Apagado : ce.Color ?? Tema.Texto;
                    int wIns = 0;
                    if (ce.Insignia.Length > 0)
                    {
                        var fi = Tema.Media(7f);
                        var sz = Tema.Medir(g, ce.Insignia, fi);
                        wIns = sz.Width + S(18);
                        Tema.Insignia(g, ce.Insignia, fi, f.Apagada ? Tema.Apagado : ce.ColorInsignia, xc + anchos[k] - S(6), y + (fila - sz.Height - S(6)) / 2f, esc, true);
                    }
                    Tema.Texto_(g, ce.Texto, fuente, color, new Rectangle(xc, y, anchos[k] - S(6) - wIns, fila),
                        (col.Derecha ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    xc += anchos[k];
                }
            }
            g.ResetClip();

            int maxDesp = Math.Max(0, Filas.Count * fila - (abajo - TopFilas));
            if (maxDesp > 0)
            {
                // barrita de desplazamiento fina a la derecha
                float frac = (float)(abajo - TopFilas) / (Filas.Count * fila);
                float hBarra = Math.Max(S(20), (abajo - TopFilas) * frac);
                float yBarra = TopFilas + (desplaz / (float)maxDesp) * (abajo - TopFilas - hBarra);
                using (var b = new SolidBrush(Tema.Alpha(Tema.Apagado, 110)))
                using (var p2 = Tema.Redondeado(new RectangleF(Width - S(7), yBarra, S(3), hBarra), S(2))) g.FillPath(b, p2);
            }
        }
    }
}
