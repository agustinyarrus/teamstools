using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace TeamsTools
{
    /// <summary>
    /// Palancas: los controles con los que se manejan los ajustes. Dibujados a mano, del mismo palo que el resto
    /// (placa recta, riel de acento a la izquierda, Cascadia fina) para que no se note dónde termina un control
    /// y empieza el otro.
    ///
    /// 🚨 Todos limpian con `Superficie`, no con `Tema.Fondo`: si no, apoyados sobre una tarjeta se les ven las
    /// esquinas oscuras del fondo alrededor. Y toda la métrica pasa por S(px) para respetar la densidad global.
    /// </summary>
    internal abstract class Palanca : Control
    {
        public string Etiqueta = "";
        public Color Acento = Tema.Cyan;
        public Color Superficie = Tema.Fondo;
        public string Ayuda = "";
        public event EventHandler Cambio;
        protected float esc = 1f;

        protected Palanca()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            TabStop = false;
            Cursor = Cursors.Hand;
        }

        protected int S(int px) => Dpi.S(esc, px);
        protected void Aviso() { try { Cambio?.Invoke(this, EventArgs.Empty); } catch { } }
        protected override void OnResize(EventArgs e) { esc = Dpi.Escala(this); base.OnResize(e); }
        protected override void OnHandleCreated(EventArgs e) { esc = Dpi.Escala(this); base.OnHandleCreated(e); }

        /// <summary>Cabecera común: riel de acento, etiqueta a la izquierda y valor a la derecha.</summary>
        protected int Cabecera(Graphics g, string valor, Color colorValor)
        {
            int alto = S(15);
            if (Etiqueta.Length > 0)
                Tema.Texto_(g, Etiqueta.ToUpperInvariant(), Tema.Media(7f), Tema.Apagado,
                    new Rectangle(0, 0, (int)(Width * 0.62), alto), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (valor.Length > 0)
                Tema.Texto_(g, valor, Tema.Media(9f), colorValor,
                    new Rectangle((int)(Width * 0.38), 0, Width - (int)(Width * 0.38), alto), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            return Etiqueta.Length > 0 || valor.Length > 0 ? alto + S(4) : 0;
        }
    }

    // =====================================================================================================
    /// <summary>Deslizador con riel, relleno de acento y marcas. Se arrastra, se clickea y anda con la rueda.</summary>
    internal sealed class Deslizador : Palanca, IRueda
    {
        public int Min = 0, Max = 100, Paso = 1;
        public string Sufijo = "";
        public Func<int, string> Formato;
        public int[] Marcas = new int[0];        // referencias que se dibujan en el riel
        int valor;
        bool arrastrando;
        int hover = -1;

        public int Valor
        {
            get => valor;
            set { int v = Recortar(value); if (v == valor) return; valor = v; Invalidate(); }
        }

        /// <summary>Cambia el valor sin disparar el evento: para cargar desde la config sin realimentar.</summary>
        public void Poner(int v) { valor = Recortar(v); Invalidate(); }

        int Recortar(int v)
        {
            v = Math.Max(Min, Math.Min(Max, v));
            if (Paso > 1) v = Min + (int)Math.Round((v - Min) / (double)Paso) * Paso;
            return Math.Max(Min, Math.Min(Max, v));
        }

        string Texto(int v) => Formato != null ? Formato(v) : v + (Sufijo.Length > 0 ? " " + Sufijo : "");

        Rectangle Riel()
        {
            int top = (Etiqueta.Length > 0 ? S(19) : 0) + S(4);
            return new Rectangle(S(1), top, Math.Max(S(20), Width - S(2)), S(8));
        }

        double Fraccion => Max <= Min ? 0 : (valor - Min) / (double)(Max - Min);

        protected override void OnMouseDown(MouseEventArgs e) { arrastrando = true; Mover(e.X); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { if (arrastrando) { arrastrando = false; Aviso(); } base.OnMouseUp(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (arrastrando) Mover(e.X); base.OnMouseMove(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = 1; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        public void Rueda(int delta)
        {
            int antes = valor;
            Valor = valor + Math.Sign(delta) * Math.Max(1, Paso);
            if (valor != antes) Aviso();
        }
        protected override void OnMouseWheel(MouseEventArgs e) { Rueda(e.Delta); base.OnMouseWheel(e); }

        void Mover(int x)
        {
            var r = Riel();
            double f = r.Width <= 0 ? 0 : (x - r.Left) / (double)r.Width;
            int antes = valor;
            Valor = Min + (int)Math.Round(f * (Max - Min));
            if (valor != antes) Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Superficie);
            Cabecera(g, Texto(valor), Acento);

            var r = Riel();
            int radio = S(3);
            using (var b = new SolidBrush(Tema.Alpha(Tema.Texto, 26)))
            using (var p = Tema.Redondeado(new RectangleF(r.X, r.Y, r.Width, r.Height), radio)) g.FillPath(b, p);

            int ancho = (int)Math.Round(r.Width * Fraccion);
            if (ancho > 0)
                using (var b = new SolidBrush(Tema.Alpha(Acento, hover > 0 || arrastrando ? 235 : 190)))
                using (var p = Tema.Redondeado(new RectangleF(r.X, r.Y, Math.Max(radio * 2, ancho), r.Height), radio)) g.FillPath(b, p);

            foreach (var mk in Marcas)
            {
                if (mk <= Min || mk >= Max) continue;
                float x = r.X + r.Width * ((mk - Min) / (float)(Max - Min));
                using (var pen = new Pen(Tema.Alpha(Tema.Texto, 60))) g.DrawLine(pen, x, r.Bottom + S(2), x, r.Bottom + S(5));
            }

            // el pulgar: una barrita, no un círculo — pega con las placas rectas de los botones
            float px = r.X + Math.Max(0, Math.Min(r.Width - S(4), (float)(r.Width * Fraccion) - S(2)));
            using (var b = new SolidBrush(hover > 0 || arrastrando ? Tema.Texto : Tema.Alpha(Tema.Texto, 200)))
            using (var p = Tema.Redondeado(new RectangleF(px, r.Y - S(3), S(4), r.Height + S(6)), S(2))) g.FillPath(b, p);

            if (Ayuda.Length > 0 && Height > r.Bottom + S(12))
                Tema.Texto_(g, Ayuda, Tema.Fina(7.5f), Tema.MuyApagado, new Rectangle(0, r.Bottom + S(6), Width, S(12)));
        }
    }

    // =====================================================================================================
    /// <summary>Interruptor de verdad: una pastilla con la perilla que se corre. Para los sí/no.</summary>
    internal sealed class Interruptor : Palanca
    {
        bool prendido, hover;
        public string TextoSi = "sí", TextoNo = "no";
        /// <summary>Ancho de texto compartido por un grupo, para que los switches caigan en la misma columna.</summary>
        public int AnchoTextoFijo;

        public bool Prendido { get => prendido; set { if (prendido == value) return; prendido = value; Invalidate(); } }
        public void Poner(bool v) { prendido = v; Invalidate(); }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e) { prendido = !prendido; Invalidate(); Aviso(); base.OnMouseClick(e); }

        /// <summary>Lo que mide el texto más largo (etiqueta o ayuda), para saber dónde empieza el switch.</summary>
        internal int AnchoDeTexto()
        {
            const TextFormatFlags med = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            var tam = new Size(int.MaxValue, S(15));
            int w = TextRenderer.MeasureText(Etiqueta, Tema.Fina(9f), tam, med).Width;
            if (Ayuda.Length > 0) w = Math.Max(w, TextRenderer.MeasureText(Ayuda, Tema.Fina(7.5f), tam, med).Width);
            return w;
        }

        /// <summary>
        /// Deja los switches de un grupo pegados al texto PERO todos en la misma columna. Sin esto, cada uno
        /// se pega a su propia etiqueta y una pila vertical queda con el borde derecho dentado.
        /// </summary>
        public static void Alinear(params Interruptor[] xs)
        {
            int max = 0;
            foreach (var x in xs) if (x != null) max = Math.Max(max, x.AnchoDeTexto());
            foreach (var x in xs) if (x != null && x.AnchoTextoFijo != max) { x.AnchoTextoFijo = max; x.Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Superficie);

            int alto = S(15), anchoSw = S(30), altoSw = S(14);
            bool conAyuda = Ayuda.Length > 0 && Height > alto + S(10);
            int ySw = (Math.Max(alto, conAyuda ? alto : Height) - altoSw) / 2;

            // 🚨 El switch se PEGA al texto en vez de irse al borde derecho. En una columna ancha terminaba
            //    a media pantalla de su propia etiqueta y se leía como si fuera del control de al lado.
            var fE = Tema.Fina(9f); var fA = Tema.Fina(7.5f);
            int anchoTexto = AnchoTextoFijo > 0 ? AnchoTextoFijo : AnchoDeTexto();
            int xSw = Math.Max(S(40), Math.Min(Width - anchoSw, anchoTexto + S(14)));

            // 🚨 con ayuda debajo, la etiqueta va en la banda de arriba; centrarla sobre TODO el alto la
            //    hacía caer justo encima del texto de ayuda y se leían las dos superpuestas.
            Tema.Texto_(g, Etiqueta, fE, prendido ? Tema.Texto : Tema.TextoSuave,
                new Rectangle(0, 0, xSw - S(8), conAyuda ? alto : Math.Max(alto, Height)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            var fondo = prendido ? Tema.Alpha(Acento, hover ? 220 : 180) : Tema.Alpha(Tema.Texto, hover ? 60 : 38);
            using (var b = new SolidBrush(fondo))
            using (var p = Tema.Redondeado(new RectangleF(xSw, ySw, anchoSw, altoSw), S(3))) g.FillPath(b, p);

            int d = altoSw - S(4);
            float px = prendido ? xSw + anchoSw - d - S(2) : xSw + S(2);
            using (var b = new SolidBrush(prendido ? Tema.Fondo : Tema.Alpha(Tema.Texto, 190)))
            using (var p = Tema.Redondeado(new RectangleF(px, ySw + S(2), d, d), S(2))) g.FillPath(b, p);

            if (conAyuda)
                Tema.Texto_(g, Ayuda, fA, Tema.MuyApagado, new Rectangle(0, alto + S(3), Math.Max(S(40), xSw - S(6)), S(13)),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    // =====================================================================================================
    /// <summary>Selector segmentado: N opciones, una prendida. Para lo que es excluyente y son pocas.</summary>
    internal sealed class Segmentado : Palanca
    {
        public string[] Opciones = new string[0];
        public Color[] Tintes;                       // opcional, uno por opción
        int elegido, hover = -1;
        readonly List<Rectangle> celdas = new List<Rectangle>();

        public int Elegido { get => elegido; set { if (elegido == value) return; elegido = value; Invalidate(); } }
        public string Texto => elegido >= 0 && elegido < Opciones.Length ? Opciones[elegido] : "";
        public void Poner(int i) { elegido = i; Invalidate(); }
        public void Poner(string texto)
        {
            int i = Array.FindIndex(Opciones, o => string.Equals(o, texto, StringComparison.OrdinalIgnoreCase));
            elegido = i >= 0 ? i : 0; Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e) { int h = celdas.FindIndex(r => r.Contains(e.Location)); if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = celdas.FindIndex(r => r.Contains(e.Location));
            if (i >= 0 && i != elegido) { elegido = i; Invalidate(); Aviso(); }
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Superficie);
            int top = Cabecera(g, "", Acento);
            celdas.Clear();
            if (Opciones.Length == 0) return;

            int alto = Math.Max(S(20), Height - top - S(2)), gap = S(3);
            int libre = Width - gap * (Opciones.Length - 1);
            int x = 0;
            for (int i = 0; i < Opciones.Length; i++)
            {
                int w = libre / Opciones.Length + (i < libre % Opciones.Length ? 1 : 0);
                var r = new Rectangle(x, top, w, alto);
                celdas.Add(r);
                var tinte = Tintes != null && i < Tintes.Length ? Tintes[i] : Acento;
                bool act = i == elegido;
                var relleno = act ? Tema.Alpha(tinte, 46) : i == hover ? Tema.Alpha(Tema.Texto, 26) : Tema.Alpha(Tema.Texto, 12);
                using (var b = new SolidBrush(relleno))
                using (var p = Tema.Redondeado(new RectangleF(r.X, r.Y, r.Width, r.Height), S(2))) g.FillPath(b, p);
                if (act)
                    using (var b = new SolidBrush(tinte))
                    using (var p = Tema.Redondeado(new RectangleF(r.X, r.Y, S(3), r.Height), S(1))) g.FillPath(b, p);
                Tema.Texto_(g, Opciones[i], act ? Tema.Media(8f) : Tema.Fina(8f), act ? tinte : Tema.TextoSuave,
                    new Rectangle(r.X + S(6), r.Y, r.Width - S(8), r.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                x += w + gap;
            }
        }
    }

    // =====================================================================================================
    /// <summary>Escalón −/+ con el valor en el medio. Para números chicos donde el deslizador es exagerado.</summary>
    internal sealed class Escalon : Palanca
    {
        public int Min = 0, Max = 999, Paso = 1;
        public string Sufijo = "";
        public Func<int, string> Formato;
        int valor, hover;                 // -1 menos, 1 mas
        Rectangle rMenos, rMas;

        public int Valor { get => valor; set { int v = Math.Max(Min, Math.Min(Max, value)); if (v == valor) return; valor = v; Invalidate(); } }
        public void Poner(int v) { valor = Math.Max(Min, Math.Min(Max, v)); Invalidate(); }
        string Texto => Formato != null ? Formato(valor) : valor + (Sufijo.Length > 0 ? " " + Sufijo : "");

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = rMenos.Contains(e.Location) ? -1 : rMas.Contains(e.Location) ? 1 : 0;
            if (h != hover) { hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hover = 0; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            int antes = valor;
            if (rMenos.Contains(e.Location)) Valor = valor - Paso;
            else if (rMas.Contains(e.Location)) Valor = valor + Paso;
            if (valor != antes) Aviso();
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Superficie);
            int top = Cabecera(g, "", Acento);
            int alto = Math.Max(S(20), Height - top - S(2)), bot = S(22);
            rMenos = new Rectangle(0, top, bot, alto);
            rMas = new Rectangle(Width - bot, top, bot, alto);
            var medio = new Rectangle(bot + S(3), top, Width - bot * 2 - S(6), alto);

            Boton(g, rMenos, "−", hover == -1, valor > Min);
            Boton(g, rMas, "+", hover == 1, valor < Max);
            using (var b = new SolidBrush(Tema.Alpha(Tema.Texto, 12)))
            using (var p = Tema.Redondeado(new RectangleF(medio.X, medio.Y, medio.Width, medio.Height), S(2))) g.FillPath(b, p);
            Tema.Texto_(g, Texto, Tema.Media(9f), Acento, medio, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        void Boton(Graphics g, Rectangle r, string signo, bool hov, bool activo)
        {
            using (var b = new SolidBrush(activo ? (hov ? Tema.Alpha(Acento, 60) : Tema.Alpha(Tema.Texto, 18)) : Tema.Alpha(Tema.Texto, 8)))
            using (var p = Tema.Redondeado(new RectangleF(r.X, r.Y, r.Width, r.Height), S(2))) g.FillPath(b, p);
            Tema.Texto_(g, signo, Tema.Media(10f), activo ? (hov ? Acento : Tema.Texto) : Tema.MuyApagado, r,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // =====================================================================================================
    /// <summary>Medidor de solo lectura: una barra con su valor y, si se le da, un umbral marcado.</summary>
    internal sealed class Medidor : Palanca
    {
        public double Valor, Min = 0, Max = 100;
        public double? Umbral;
        public string Texto = "";
        public Color ColorBajo = Tema.Salvia, ColorAlto = Tema.Rosa;
        public bool AltoEsMalo = true;

        public Medidor() { Cursor = Cursors.Default; }

        public void Poner(double v, string texto = null) { Valor = v; if (texto != null) Texto = texto; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Superficie);
            double f = Max <= Min ? 0 : Math.Max(0, Math.Min(1, (Valor - Min) / (Max - Min)));
            var color = AltoEsMalo ? (f > 0.75 ? ColorAlto : f > 0.45 ? Tema.Durazno : ColorBajo)
                                   : (f > 0.75 ? ColorBajo : f > 0.45 ? Tema.Durazno : ColorAlto);
            Cabecera(g, Texto, color);
            var r = new Rectangle(0, (Etiqueta.Length > 0 ? S(19) : 0) + S(3), Math.Max(S(20), Width), S(6));
            using (var b = new SolidBrush(Tema.Alpha(Tema.Texto, 22)))
            using (var p = Tema.Redondeado(new RectangleF(r.X, r.Y, r.Width, r.Height), S(2))) g.FillPath(b, p);
            int ancho = (int)Math.Round(r.Width * f);
            if (ancho > 0)
                using (var b = new SolidBrush(Tema.Alpha(color, 210)))
                using (var p = Tema.Redondeado(new RectangleF(r.X, r.Y, Math.Max(S(4), ancho), r.Height), S(2))) g.FillPath(b, p);
            if (Umbral.HasValue && Max > Min)
            {
                float x = r.X + r.Width * (float)Math.Max(0, Math.Min(1, (Umbral.Value - Min) / (Max - Min)));
                using (var pen = new Pen(Tema.Alpha(Tema.Texto, 110))) g.DrawLine(pen, x, r.Y - S(2), x, r.Bottom + S(2));
            }
        }
    }
}
