using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace TeamsTools
{
    /// <summary>Campo de texto de una linea con etiqueta, sobre una tarjeta. Pista (placeholder) nativa.</summary>
    internal sealed class Campo : Control
    {
        public readonly TextBox Caja = new TextBox();
        public string Etiqueta = "";
        string pista = "";
        bool foco;
        public event EventHandler Cambio;

        public Campo()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            Caja.BorderStyle = BorderStyle.None;
            Caja.BackColor = Tema.Tarjeta;
            Caja.ForeColor = Tema.Texto;
            Caja.Font = Tema.Fina(10f);
            Caja.TextChanged += (s, e) => Cambio?.Invoke(this, EventArgs.Empty);
            Caja.GotFocus += (s, e) => { foco = true; Invalidate(); };
            Caja.LostFocus += (s, e) => { foco = false; Invalidate(); };
            Caja.HandleCreated += (s, e) => { if (pista.Length > 0) Win32.SendMessage(Caja.Handle, Win32.EM_SETCUEBANNER, (IntPtr)1, pista); };
            Controls.Add(Caja);
        }

        public string Pista { get => pista; set { pista = value ?? ""; if (Caja.IsHandleCreated) Win32.SendMessage(Caja.Handle, Win32.EM_SETCUEBANNER, (IntPtr)1, pista); } }
        public string Texto { get => Caja.Text; set { Caja.Text = value ?? ""; } }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            float esc = Dpi.Escala(this);
            int top = Etiqueta.Length > 0 ? Dpi.S(esc, 24) : Dpi.S(esc, 11);
            Caja.SetBounds(Dpi.S(esc, 12), top, Width - Dpi.S(esc, 24), Height - top - Dpi.S(esc, 8));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float esc = Dpi.Escala(this);
            Tema.Tarjeta_(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Dpi.S(esc, 9), Tema.Tarjeta, foco ? Tema.Alpha(Tema.Malva, 120) : Tema.Tarjeta);
            if (Etiqueta.Length > 0) Tema.Texto_(g, Etiqueta.ToUpperInvariant(), Tema.Media(7.5f), Tema.Apagado, new Rectangle(Dpi.S(esc, 12), Dpi.S(esc, 7), Width - Dpi.S(esc, 24), Dpi.S(esc, 14)));
        }
        protected override void OnMouseClick(MouseEventArgs e) { Caja.Focus(); base.OnMouseClick(e); }
    }

    /// <summary>Editor con formato: barra de pastillas (B I U S · lista · numerada · cita · código · {nombre}) + RichTextBox oscuro + estado.</summary>
    internal sealed class EditorRico : Control
    {
        public readonly RichTextBox Caja = new RichTextBox();
        readonly List<Chip> barra = new List<Chip>();
        Chip bB, bI, bU, bS, bLista, bNum, bCita, bCod, bNombre;
        readonly Font baseFont = Tema.Fina(9.5f);   // letra chica: entra más texto y se ve más prolijo
        readonly Font codeFont;
        public string Etiqueta = "MENSAJE";
        public event EventHandler Cambio;
        bool cargando;
        bool hayLugarParaEstado = true;

        public EditorRico()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            codeFont = new Font(Tema.Mono(9.5f).FontFamily, baseFont.Size, FontStyle.Regular, GraphicsUnit.Point);
            Caja.BorderStyle = BorderStyle.None;
            Caja.BackColor = Tema.Tarjeta;
            Caja.ForeColor = Tema.Texto;
            Caja.Font = baseFont;
            Caja.DetectUrls = false;
            Caja.AcceptsTab = false;
            Caja.ScrollBars = RichTextBoxScrollBars.Vertical;
            Caja.HandleCreated += (s, e) => Win32.BarrasOscuras(Caja.Handle);
            Caja.TextChanged += (s, e) => { if (!cargando) Cambio?.Invoke(this, EventArgs.Empty); Invalidate(); };
            Caja.SelectionChanged += (s, e) => { ActualizarBarra(); };
            Controls.Add(Caja);
            bB = Nuevo("B", () => Alternar(FontStyle.Bold), Tema.Texto); bB.Font = Tema.Media(9.5f);
            bI = Nuevo("I", () => Alternar(FontStyle.Italic), Tema.Texto);
            bU = Nuevo("U", () => Alternar(FontStyle.Underline), Tema.Texto);
            bS = Nuevo("S", () => Alternar(FontStyle.Strikeout), Tema.Texto);
            bCod = Nuevo("código", Codigo, Tema.Crema);
            bLista = Nuevo("• lista", () => Prefijo("- "), Tema.Cielo);
            bNum = Nuevo("1. lista", () => Prefijo("1. "), Tema.Cielo);
            bCita = Nuevo("› cita", () => Prefijo("> "), Tema.Cielo);
            bNombre = Nuevo("{nombre}", () => { Caja.SelectedText = "{nombre}"; Caja.Focus(); }, Tema.Malva);
        }

        Chip Nuevo(string t, Action a, Color c)
        {
            var ch = new Chip { Text = t, Tipo = Chip.Modo.Toggle, Acento = c, Superficie = Tema.Tarjeta };   // van apoyados sobre la tarjeta del editor
            ch.Accion += (s, e) => a();
            Controls.Add(ch); barra.Add(ch);
            return ch;
        }

        void Alternar(FontStyle st)
        {
            var f = Caja.SelectionFont ?? baseFont;
            Caja.SelectionFont = new Font(f, f.Style ^ st);
            Caja.Focus();
            ActualizarBarra();
            Cambio?.Invoke(this, EventArgs.Empty);
        }

        void Codigo()
        {
            var f = Caja.SelectionFont ?? baseFont;
            bool es = f.FontFamily.Name.IndexOf("Mono", StringComparison.OrdinalIgnoreCase) >= 0;
            Caja.SelectionFont = new Font(es ? baseFont.FontFamily : codeFont.FontFamily, baseFont.Size, f.Style);
            Caja.SelectionColor = es ? Tema.Texto : Tema.Crema;
            Caja.Focus();
            ActualizarBarra();
            Cambio?.Invoke(this, EventArgs.Empty);
        }

        void Prefijo(string p)
        {
            int linea = Caja.GetLineFromCharIndex(Caja.SelectionStart);
            int ini = Caja.GetFirstCharIndexFromLine(linea);
            if (ini < 0) ini = 0;
            int sel = Caja.SelectionStart;
            Caja.Select(ini, 0);
            Caja.SelectedText = p;
            Caja.Select(sel + p.Length, 0);
            Caja.Focus();
        }

        void ActualizarBarra()
        {
            try
            {
                var f = Caja.SelectionFont;
                bB.Activo = f != null && f.Bold; bI.Activo = f != null && f.Italic; bU.Activo = f != null && f.Underline; bS.Activo = f != null && f.Strikeout;
                bCod.Activo = f != null && f.FontFamily.Name.IndexOf("Mono", StringComparison.OrdinalIgnoreCase) >= 0;
                foreach (var c in new[] { bB, bI, bU, bS, bCod }) c.Invalidate();
            }
            catch { }
        }

        public void Cargar(string html, string rtf)
        {
            cargando = true;
            try
            {
                bool ok = false;
                if (!string.IsNullOrEmpty(rtf)) { try { Caja.Rtf = rtf; ok = true; } catch { ok = false; } }
                if (!ok) Formato.CargarHtml(Caja, html, baseFont, codeFont);
                Caja.Select(0, 0);
            }
            finally { cargando = false; }
            Invalidate();
        }
        public void Limpiar() { cargando = true; Caja.Clear(); Caja.SelectionFont = baseFont; Caja.SelectionColor = Tema.Texto; cargando = false; Invalidate(); }

        /// <summary>
        /// 🚨 Habilita o bloquea SIN tocar Enabled. Un RichTextBox deshabilitado se pinta BLANCO: ignora su BackColor
        /// y usa el color de sistema, así que en un tema oscuro aparece un rectángulo blanco enorme donde debería
        /// haber un editor apagado. La forma correcta es dejarlo habilitado y ponerlo de SOLO LECTURA.
        /// </summary>
        public void Habilitar(bool si)
        {
            Caja.ReadOnly = !si;
            Caja.BackColor = Tema.Tarjeta;
            Caja.ForeColor = si ? Tema.Texto : Tema.Apagado;
            Caja.Cursor = si ? Cursors.IBeam : Cursors.Default;
            foreach (var c in barra) c.Enabled = si;
            Invalidate();
        }
        public Rico Rico => Rico.DesdeRtb(Caja);
        public string Html => Rico.Html();
        public string Plano => Rico.Plano();
        public string Rtf => Caja.Rtf;

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            float esc = Dpi.Escala(this);
            int x0 = Dpi.S(esc, 10), x = x0, y = Dpi.S(esc, 26), gap = Dpi.S(esc, 6);
            int filaH = 0, dispo = Width - x0 - Dpi.S(esc, 10);
            foreach (var c in barra)
            {
                c.Ajustar();
                if (x > x0 && x + c.Width > x0 + dispo) { x = x0; y += c.Height + Dpi.S(esc, 6); }   // envuelve, no se corta
                c.Location = new Point(x, y);
                x += c.Width + gap; filaH = c.Height;
            }
            int top = y + filaH + Dpi.S(esc, 8);
            int altoCaja = Math.Max(Dpi.S(esc, 40), Height - top - Dpi.S(esc, 28));
            Caja.SetBounds(Dpi.S(esc, 12), top, Width - Dpi.S(esc, 24), altoCaja);
            // 🚨 cuando el layout de arriba lo deja corto, la caja se queda con su mínimo y la línea de estado
            //    («81 caracteres · 1 línea») quedaba DEBAJO del texto, encimada: si no hay lugar, no se dibuja.
            hayLugarParaEstado = top + altoCaja <= Height - Dpi.S(esc, 24);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float esc = Dpi.Escala(this);
            Tema.Tarjeta_(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Dpi.S(esc, 9), Tema.Tarjeta, Tema.Tarjeta);
            Tema.Texto_(g, Etiqueta.ToUpperInvariant(), Tema.Media(7.5f), Tema.Apagado, new Rectangle(Dpi.S(esc, 12), Dpi.S(esc, 7), Width - Dpi.S(esc, 24), Dpi.S(esc, 14)));
            string t = Caja.Text;
            int lineas = t.Length == 0 ? 0 : t.Split('\n').Length;
            var rico = t.Length > 0 ? Rico : null;
            string estado = t.Length == 0 ? "vacío · escribí con formato o pegá texto" : $"{t.Length} caracteres · {lineas} línea/s" + (rico != null && rico.TieneFormato ? " · con formato" : " · texto plano");
            if (hayLugarParaEstado)
                Tema.Texto_(g, estado, Tema.Fina(8.5f), Tema.Apagado, new Rectangle(Dpi.S(esc, 12), Height - Dpi.S(esc, 22), Width - Dpi.S(esc, 24), Dpi.S(esc, 16)));
        }
    }

    /// <summary>
    /// Un control que sabe desplazarse con la rueda. 🚨 Estos controles NO toman el foco al pasar el mouse
    /// (si lo hicieran, se lo robarían al campo donde estás escribiendo): la rueda se la enruta la ventana.
    /// </summary>
    internal interface IRueda { void Rueda(int delta); }

    internal sealed class Fila
    {
        public string Titulo = "", Sub = "", Derecha = "", Insignia = "";
        public Color Color = Tema.Apagado;
        public Color ColorInsignia = Tema.Malva;
        // API del control que hoy nadie setea (el cron pasó de ListaBonita a Tabla): la pintura sí las lee,
        // así que se quedan. Con el valor explícito no salta el CS0649 y el proyecto sigue en 0 warnings.
        public bool Apagada = false;
        public object Tag = null;
    }

    /// <summary>Lista dibujada a mano: punto de color, titulo, subtitulo, dato a la derecha e insignia. Seleccion y rueda.</summary>
    internal sealed class ListaBonita : Control, IRueda
    {
        public List<Fila> Filas = new List<Fila>();
        public int Seleccion = -1;
        public string Vacio = "nada por acá todavía";
        public int AltoFila = 46;
        public string Etiqueta = "";
        int desplaz, hover = -1;
        public event EventHandler SeleccionCambio;
        public event EventHandler DobleClic;

        public ListaBonita()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            TabStop = false;
        }

        public Fila Actual => Seleccion >= 0 && Seleccion < Filas.Count ? Filas[Seleccion] : null;
        public void Poner(List<Fila> filas, object mantener = null)
        {
            Filas = filas;
            if (mantener != null) { int i = Filas.FindIndex(f => Equals(f.Tag, mantener)); Seleccion = i; }
            else if (Seleccion >= Filas.Count) Seleccion = Filas.Count - 1;
            Invalidate();
        }
        public void Seleccionar(int i) { Seleccion = i; Invalidate(); SeleccionCambio?.Invoke(this, EventArgs.Empty); }

        int Top0 => Etiqueta.Length > 0 ? Dpi.S(Dpi.Escala(this), 26) : Dpi.S(Dpi.Escala(this), 8);
        int FilaEn(int y)
        {
            float esc = Dpi.Escala(this);
            int fh = Dpi.S(esc, AltoFila);
            int i = (y - Top0 + desplaz) / fh;
            return i >= 0 && i < Filas.Count && y >= Top0 ? i : -1;
        }
        protected override void OnMouseMove(MouseEventArgs e) { int h = FilaEn(e.Y); if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e) { int i = FilaEn(e.Y); if (i >= 0) Seleccionar(i); base.OnMouseClick(e); }
        protected override void OnMouseDoubleClick(MouseEventArgs e) { int i = FilaEn(e.Y); if (i >= 0) { Seleccion = i; DobleClic?.Invoke(this, EventArgs.Empty); } base.OnMouseDoubleClick(e); }
        public void Rueda(int delta)
        {
            float esc = Dpi.Escala(this);
            int fh = Dpi.S(esc, AltoFila);
            int max = Math.Max(0, Filas.Count * fh - (Height - Top0 - Dpi.S(esc, 6)));
            desplaz = Math.Max(0, Math.Min(max, desplaz - Math.Sign(delta) * fh * 2));
            Invalidate();
        }
        protected override void OnMouseWheel(MouseEventArgs e) { Rueda(e.Delta); base.OnMouseWheel(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float esc = Dpi.Escala(this);
            int S(int px) => Dpi.S(esc, px);
            Tema.Tarjeta_(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), S(10), Tema.Panel, Tema.Panel);
            if (Etiqueta.Length > 0) Tema.Texto_(g, Etiqueta.ToUpperInvariant(), Tema.Media(7.5f), Tema.Apagado, new Rectangle(S(14), S(8), Width - S(28), S(14)));
            if (Filas.Count == 0) { Tema.Texto_(g, Vacio, Tema.Fina(9.5f), Tema.Apagado, new Rectangle(S(14), Top0, Width - S(28), S(40))); return; }
            int fh = S(AltoFila);
            g.SetClip(new Rectangle(0, Top0, Width, Height - Top0 - S(4)));
            int y = Top0 - desplaz;
            for (int i = 0; i < Filas.Count; i++, y += fh)
            {
                if (y + fh < Top0 || y > Height) continue;
                var f = Filas[i];
                var rf = new Rectangle(S(6), y, Width - S(12), fh - S(3));
                bool sel = i == Seleccion, hov = i == hover;
                if (sel || hov) Tema.Tarjeta_(g, new RectangleF(rf.X, rf.Y, rf.Width, rf.Height), S(8), sel ? Tema.TarjetaHover : Tema.Tarjeta, sel ? Tema.Alpha(Tema.Malva, 70) : Tema.Tarjeta);
                int d = S(7);
                using (var b = new SolidBrush(f.Apagada ? Tema.MuyApagado : f.Color)) g.FillEllipse(b, rf.X + S(11), rf.Y + (rf.Height - d) / 2f, d, d);
                int xt = rf.X + S(28);
                int wDer = 0;
                if (f.Derecha.Length > 0)
                {
                    var fd = Tema.Media(8.5f);
                    wDer = Tema.Medir(g, f.Derecha, fd).Width + S(8);
                    Tema.Texto_(g, f.Derecha, fd, f.Apagada ? Tema.Apagado : f.Color, new Rectangle(rf.Right - wDer - S(8), rf.Y, wDer, rf.Height), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }
                if (f.Insignia.Length > 0)
                {
                    var fi = Tema.Media(7.5f);
                    var sz = Tema.Medir(g, f.Insignia, fi);
                    Tema.Insignia(g, f.Insignia, fi, f.Apagada ? Tema.Apagado : f.ColorInsignia, rf.Right - wDer - S(12), rf.Y + (rf.Height - sz.Height - S(6)) / 2f, esc, true);
                    wDer += sz.Width + S(22);
                }
                int wt = rf.Width - (xt - rf.X) - wDer - S(10);
                var ct = f.Apagada ? Tema.Apagado : Tema.Texto;
                if (f.Sub.Length > 0)
                {
                    Tema.Texto_(g, f.Titulo, Tema.Fina(10f), ct, new Rectangle(xt, rf.Y + S(6), wt, S(18)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    Tema.Texto_(g, f.Sub, Tema.Fina(8.5f), Tema.Apagado, new Rectangle(xt, rf.Y + S(23), wt, S(16)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
                else Tema.Texto_(g, f.Titulo, Tema.Fina(10f), ct, new Rectangle(xt, rf.Y, wt, rf.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            g.ResetClip();
        }
    }

    /// <summary>Tira de pestañas: nombre + insignia opcional, subrayado pastel en la activa.</summary>
    internal sealed class Pestanas : Control
    {
        public string[] Nombres = new string[0];
        public string[] Insignias = new string[0];
        public int Activa;
        int hover = -1;
        readonly List<Rectangle> rects = new List<Rectangle>();
        public event EventHandler Cambio;

        public Pestanas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseMove(MouseEventArgs e) { int h = rects.FindIndex(r => r.Contains(e.Location)); if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = rects.FindIndex(r => r.Contains(e.Location));
            if (i >= 0 && i != Activa) { Activa = i; Invalidate(); Cambio?.Invoke(this, EventArgs.Empty); }
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float esc = Dpi.Escala(this);
            int S(int px) => Dpi.S(esc, px);
            rects.Clear();
            int x = 0;
            var f = Tema.Fina(10f); var fi = Tema.Media(7.5f);
            for (int i = 0; i < Nombres.Length; i++)
            {
                string ins = i < Insignias.Length ? Insignias[i] ?? "" : "";
                int w = Tema.Medir(g, Nombres[i], f).Width + S(24) + (ins.Length > 0 ? Tema.Medir(g, ins, fi).Width + S(20) : 0);
                var r = new Rectangle(x, 0, w, Height);
                rects.Add(r);
                bool act = i == Activa, hov = i == hover;
                var c = act ? Tema.Texto : hov ? Tema.TextoSuave : Tema.Apagado;
                Tema.Texto_(g, Nombres[i], f, c, new Rectangle(r.X + S(12), 0, r.Width, Height - S(4)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                if (ins.Length > 0) Tema.Insignia(g, ins, fi, act ? Tema.Malva : Tema.Apagado, r.X + S(12) + Tema.Medir(g, Nombres[i], f).Width + S(8), (Height - S(4) - Tema.Medir(g, ins, fi).Height - S(6)) / 2f, esc);
                if (act) using (var p = new Pen(Tema.Malva, S(2))) g.DrawLine(p, r.X + S(12), Height - S(3), r.Right - S(12), Height - S(3));
                x += w;
            }
        }
    }
}
