using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace TeamsTools
{
    /// <summary>Boton/toggle/valor con forma de pastilla, dibujado a mano.</summary>
    internal sealed class Chip : Control
    {
        public enum Modo { Boton, Toggle, Valor }
        public Modo Tipo = Modo.Boton;
        public bool Activo;
        public Color Acento = Tema.Malva;
        public string Sub = "";
        public bool Armado;             // "¿seguro?" para acciones con peso
        public bool Destacado;          // borde siempre con acento
        /// <summary>
        /// Color de lo que hay pintado DETRÁS del chip. Por defecto el fondo de la ventana; si el padre lo apoya sobre
        /// una tarjeta (la barra del editor) tiene que decirlo, si no las cuatro esquinas fuera de la pastilla quedan
        /// del color del fondo y se ven "cortadas". El relleno en reposo se calcula un escalón más claro que esto.
        /// </summary>
        public Color Superficie = Tema.Fondo;
        bool hover, presionado, ocupado;
        public event EventHandler Accion;

        /// <summary>
        /// La acción del chip está corriendo de fondo: el riel late, un destello cruza la placa, el cursor pasa a
        /// «trabajando» y los clics se ignoran (nada de disparar dos veces lo mismo). Se apaga solo al terminar.
        /// </summary>
        public bool Ocupado
        {
            get => ocupado;
            set
            {
                if (ocupado == value) return;
                ocupado = value;
                Cursor = value ? Cursors.AppStarting : Cursors.Hand;
                if (value) Animacion.Encender(this); else Animacion.Apagar(this);
                Invalidate();
            }
        }
        public event EventHandler AccionDerecha;

        public Chip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            BackColor = Tema.Fondo;
            Font = Tema.Fina(9.5f);
            TabStop = false;
        }

        public void Poner(string texto, string sub = null)
        {
            Text = texto;
            if (sub != null) Sub = sub;
            Ajustar();
            Invalidate();
        }

        public void Ajustar()
        {
            using (var g = CreateGraphics())
            {
                // las medidas tienen que coincidir con OnPaint: riel+aire a la izquierda, aire a la derecha,
                // el cuadrado del toggle y, en modo Valor, el filete + el valor. Da 1 a 4 px MENOS que la pastilla
                // vieja, así que ninguna fila de chips que antes entraba se pasa de ancho.
                float e = g.DpiX / 96f;
                int w = Tema.Medir(g, Text, Font).Width + Dpi.S(e, 12) + Dpi.S(e, 12);
                if (Tipo == Modo.Toggle) w += Dpi.S(e, 7) + Dpi.S(e, 8);
                if (Sub.Length > 0) w += Tema.Medir(g, Sub, Tema.Media(9.5f)).Width + Dpi.S(e, 8);
                Width = w;
                Height = Dpi.S(e, 26);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; presionado = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { presionado = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool adentro = presionado && ClientRectangle.Contains(e.Location);
            presionado = false; Invalidate();
            base.OnMouseUp(e);
            if (!adentro || ocupado) return;
            if (e.Button == MouseButtons.Left) Accion?.Invoke(this, EventArgs.Empty);
            else if (e.Button == MouseButtons.Right) AccionDerecha?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Placa recta con riel de acento a la izquierda. Nada de pastillas: esquinas casi rectas, borde fino
        /// permanente y un riel de 2 px pegado al canto izquierdo que es la identidad del control y, en los toggles,
        /// también el estado (apagado = apenas insinuado; encendido = macizo + la placa teñida del acento).
        /// El marcador de los toggles es un CUADRADO: vacío cuando está apagado, lleno cuando está encendido.
        /// En modo Valor el dato va a la derecha, separado por un filete vertical.
        /// </summary>
        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float e = g.DpiX / 96f;
            int S(int px) => Dpi.S(e, px);
            g.Clear(Superficie);

            bool encendido = Tipo == Modo.Toggle && Activo;
            bool vivo = hover || presionado;
            Color acento = Armado ? Tema.Rosa : Acento;
            float radio = S(2);                                  // casi recto: es una placa, no una pastilla
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);

            // --- relleno: en reposo apenas se despega de la superficie; el color fuerte lo trae el estado
            Color fondo;
            if (Armado) fondo = Tema.Mezcla(Superficie, Tema.Rosa, 0.20f);
            else if (presionado) fondo = Tema.Mezcla(Superficie, acento, 0.24f);
            else if (encendido) fondo = Tema.Mezcla(Superficie, acento, hover ? 0.16f : 0.11f);
            else if (hover) fondo = Tema.Mezcla(Superficie, Tema.Texto, 0.075f);
            else fondo = Tema.Mezcla(Superficie, Tema.Texto, 0.025f);

            // --- borde fino SIEMPRE visible: es lo que le da el aire de placa y no de pastilla
            Color borde = Armado ? Tema.Alpha(Tema.Rosa, 190)
                        : encendido ? Tema.Alpha(acento, 145)
                        : Destacado ? Tema.Alpha(acento, 125)
                        : vivo ? Tema.Alpha(Tema.TextoSuave, 78)
                        : Tema.Alpha(Tema.TextoSuave, 28);       // en reposo apenas se insinúa: no encajona
            Tema.Tarjeta_(g, r, radio, fondo, borde);

            // --- riel de acento: la firma del control. Se pinta DESPUÉS de la placa, tapando su borde izquierdo,
            //     y recortado contra ella para que respete las esquinas. En los toggles apagados queda apenas vivo.
            int alfaRiel = Armado ? 245 : encendido ? 255 : Destacado ? 225
                         : vivo ? 205 : (Tipo == Modo.Toggle ? 80 : 150);
            if (ocupado) alfaRiel = 110 + (int)(145 * Animacion.Suave((Math.Sin(Animacion.T * 5.5) + 1) / 2));   // late
            using (var placa = Tema.Redondeado(r, radio))
            {
                var recorte = g.Clip;
                g.SetClip(placa, CombineMode.Intersect);
                using (var b = new SolidBrush(Tema.Alpha(acento, alfaRiel)))
                    g.FillRectangle(b, 0f, 0f, S(3), Height);
                g.Clip = recorte;
            }

            int x = S(12);
            if (Tipo == Modo.Toggle)
            {
                int d = S(7);
                var rm = new RectangleF(x, (float)Math.Round((Height - d) / 2f), d, d);
                if (Activo) { using (var b = new SolidBrush(acento)) g.FillRectangle(b, rm); }
                else { using (var p = new Pen(Tema.Alpha(Tema.TextoSuave, vivo ? 165 : 120), 1f)) g.DrawRectangle(p, rm.X, rm.Y, rm.Width, rm.Height); }
                x += d + S(8);
            }

            // --- el valor de la derecha se mide primero: el texto no puede invadirlo
            var fSub = Tema.Media(9.5f);
            int anchoSub = Sub.Length > 0 && !Armado ? Tema.Medir(g, Sub, fSub).Width : 0;
            int reservaDerecha = anchoSub > 0 ? anchoSub + S(20) : S(12);

            Color ct = Armado ? Tema.Rosa
                     : ocupado ? Tema.Apagado
                     : encendido ? Tema.Texto
                     : Destacado ? acento
                     : vivo ? Tema.Texto
                     : Tema.TextoSuave;
            string texto = Armado ? "¿seguro? clic de nuevo" : Text;
            var rt = new Rectangle(x, 0, Math.Max(S(10), Width - x - reservaDerecha), Height);
            // 🚨 armado NO lleva puntos suspensivos: el chip no se ensancha (rompería la fila ya acomodada) y con
            // «…» quedaba «¿se …», ilegible justo en el botón que cuelga una llamada. Cortado a lo ancho se lee
            // «¿seguro», que con el rosa del riel y del borde alcanza para entender que pide confirmación.
            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
            if (!Armado) flags |= TextFormatFlags.EndEllipsis;
            Tema.Texto_(g, texto, Font, ct, rt, flags);
            if (ocupado)
            {
                // destello que cruza la placa: «estoy en eso» sin tapar el texto
                double fase = (Animacion.T % 1.3) / 1.3;
                float banda = Math.Max(S(24), Width * 0.45f);
                float xb = (float)(-banda + (Width + banda) * Animacion.Suave(fase));
                using (var placa = Tema.Redondeado(r, radio))
                using (var lg = new LinearGradientBrush(new RectangleF(xb - 1, 0, banda + 2, Height), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal))
                {
                    lg.InterpolationColors = new ColorBlend(3) { Colors = new[] { Color.FromArgb(0, acento), Color.FromArgb(46, acento), Color.FromArgb(0, acento) }, Positions = new[] { 0f, 0.5f, 1f } };
                    var recorte = g.Clip;
                    g.SetClip(placa, CombineMode.Intersect);
                    g.FillRectangle(lg, xb, 0, banda, Height);
                    g.Clip = recorte;
                }
            }

            if (anchoSub > 0)
            {
                int sx = Width - anchoSub - S(12);
                using (var p = new Pen(Tema.Alpha(Tema.TextoSuave, vivo ? 62 : 38), 1f))
                    g.DrawLine(p, sx - S(8), S(7), sx - S(8), Height - S(7));
                Tema.Texto_(g, Sub, fSub, acento, new Rectangle(sx, 0, anchoSub + 2, Height));
            }
        }
    }

    /// <summary>Lista de eventos con riel vertical y puntos de color: la linea de tiempo del vigia.</summary>
    internal sealed class LineaTiempo : Control, IRueda
    {
        readonly List<LineaLog> lineas = new List<LineaLog>();
        int desplazamiento;   // filas desde el final; 0 = pegado al final
        public int Capacidad = 600;

        public LineaTiempo()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Panel;
            Font = Tema.Fina(9.5f);
            TabStop = false;
        }

        public void Cargar(IEnumerable<LineaLog> ls) { lineas.Clear(); lineas.AddRange(ls); Recortar(); desplazamiento = 0; Invalidate(); }
        public void Agregar(LineaLog l)
        {
            lineas.Add(l);
            Recortar();
            if (desplazamiento > 0) desplazamiento = Math.Min(desplazamiento + 1, Math.Max(0, lineas.Count - 1));
            Invalidate();
        }
        void Recortar() { while (lineas.Count > Capacidad) lineas.RemoveAt(0); }

        public void Rueda(int delta)
        {
            int fila = Dpi.S(Dpi.Escala(this), 19);
            int visibles = Math.Max(1, (Height - Dpi.S(Dpi.Escala(this), 12)) / fila);
            int max = Math.Max(0, lineas.Count - visibles);
            desplazamiento = Math.Max(0, Math.Min(max, desplazamiento + (delta > 0 ? 3 : -3)));
            Invalidate();
        }
        protected override void OnMouseWheel(MouseEventArgs e) { Rueda(e.Delta); base.OnMouseWheel(e); }

        static Color ColorDe(Nivel n)
        {
            switch (n)
            {
                case Nivel.Debug: return Tema.MuyApagado;
                case Nivel.Ok: return Tema.Salvia;
                case Nivel.Aviso: return Tema.Crema;
                case Nivel.Alerta: return Tema.Durazno;
                case Nivel.Error: return Tema.Rosa;
                default: return Tema.Cielo;
            }
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            float e = g.DpiX / 96f;
            g.Clear(BackColor);
            int fila = Dpi.S(e, 19), pad = Dpi.S(e, 6);
            int visibles = Math.Max(1, (Height - pad * 2) / fila);
            int fin = Math.Max(0, lineas.Count - desplazamiento);
            int ini = Math.Max(0, fin - visibles);
            int xHora = Dpi.S(e, 12), wHora = Dpi.S(e, 56), xRiel = xHora + wHora + Dpi.S(e, 10), xTexto = xRiel + Dpi.S(e, 16);
            var fHora = Tema.Fina(9f);
            var fTexto = Font;
            var fFuerte = Tema.Media(9.5f);
            // riel
            using (var pr = new Pen(Tema.BordeSuave, 1f)) g.DrawLine(pr, xRiel, pad, xRiel, Height - pad);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int y = pad + (visibles - (fin - ini)) * 0;
            if (lineas.Count == 0)
            {
                Tema.Texto_(g, "todavía no pasó nada · el vigía anota acá cada cosa que ve", fHora, Tema.Apagado, new Rectangle(xTexto, 0, Width - xTexto, Height));
                return;
            }
            for (int i = ini; i < fin; i++, y += fila)
            {
                var l = lineas[i];
                var c = ColorDe(l.Nivel);
                Tema.Texto_(g, l.Hora.ToString("HH:mm:ss"), fHora, Tema.Apagado, new Rectangle(xHora, y, wHora, fila));
                int d = Dpi.S(e, l.Nivel >= Nivel.Aviso ? 7 : 5);
                using (var b = new SolidBrush(c)) g.FillEllipse(b, xRiel - d / 2f, y + (fila - d) / 2f, d, d);
                if (l.Nivel >= Nivel.Alerta) { using (var p = new Pen(Tema.Alpha(c, 45), Dpi.S(e, 3))) g.DrawEllipse(p, xRiel - d / 2f - 2, y + (fila - d) / 2f - 2, d + 4, d + 4); }
                var font = l.Nivel >= Nivel.Aviso ? fFuerte : fTexto;
                var ct = l.Nivel == Nivel.Debug ? Tema.Apagado : l.Nivel == Nivel.Info ? Tema.Texto : c;
                Tema.Texto_(g, l.Texto, font, ct, new Rectangle(xTexto, y, Width - xTexto - Dpi.S(e, 12), fila), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
            if (desplazamiento > 0)
            {
                string s = $"▾ {desplazamiento} más abajo";
                var f = Tema.Media(8.5f);
                var sz = Tema.Medir(g, s, f);
                var r = new RectangleF(Width - sz.Width - Dpi.S(e, 30), Height - sz.Height - Dpi.S(e, 18), sz.Width + Dpi.S(e, 16), sz.Height + Dpi.S(e, 8));
                Tema.Tarjeta_(g, r, r.Height / 2, Tema.Mezcla(Tema.Tarjeta, Tema.Malva, 0.15f), Tema.Alpha(Tema.Malva, 160));
                Tema.Texto_(g, s, f, Tema.Malva, Rectangle.Round(r), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    /// <summary>Menu contextual oscuro para la bandeja.</summary>
    internal sealed class RenderOscuro : ToolStripProfessionalRenderer
    {
        public RenderOscuro() : base(new Colores()) { RoundedEdges = false; }
        sealed class Colores : ProfessionalColorTable
        {
            public override Color MenuItemSelected => Tema.TarjetaHover;
            public override Color MenuItemBorder => Tema.Alpha(Tema.Malva, 120);
            public override Color MenuBorder => Tema.Borde;
            public override Color ToolStripDropDownBackground => Tema.Panel;
            public override Color ImageMarginGradientBegin => Tema.Panel;
            public override Color ImageMarginGradientMiddle => Tema.Panel;
            public override Color ImageMarginGradientEnd => Tema.Panel;
            public override Color SeparatorDark => Tema.Borde;
            public override Color SeparatorLight => Tema.Panel;
            public override Color MenuItemSelectedGradientBegin => Tema.TarjetaHover;
            public override Color MenuItemSelectedGradientEnd => Tema.TarjetaHover;
            public override Color MenuItemPressedGradientBegin => Tema.Tarjeta;
            public override Color MenuItemPressedGradientEnd => Tema.Tarjeta;
            public override Color CheckBackground => Tema.Panel;
            public override Color CheckSelectedBackground => Tema.TarjetaHover;
            public override Color CheckPressedBackground => Tema.Tarjeta;
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? (e.Item.Selected ? Tema.Texto : Tema.TextoSuave) : Tema.Apagado;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            int d = Math.Max(6, r.Height / 3);
            using (var b = new SolidBrush(Tema.Cyan)) g.FillEllipse(b, r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
        }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var p = new Pen(Tema.Borde)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
    }
}
