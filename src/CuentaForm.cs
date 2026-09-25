using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TeamsTools
{
    /// <summary>Overlay de cuenta regresiva: abajo a la derecha, siempre visible, no roba el foco.</summary>
    internal sealed class CuentaForm : Form
    {
        readonly Chip bSalir, bQuedar, bCancelar;
        readonly Timer fade = new Timer { Interval = 16 };
        Vista vista = new Vista();
        int gracia = 45;
        float esc = 1f;
        public event EventHandler SalirAhora, Quedarse, CancelarEstaVez;

        public CuentaForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Tema.Fondo;
            DoubleBuffered = true;
            Opacity = 0;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            bSalir = new Chip { Text = "Salir ahora", Acento = Tema.Rosa, Destacado = true };
            bQuedar = new Chip { Text = "Quedarme 10 min", Acento = Tema.Malva };
            bCancelar = new Chip { Text = "No salir esta vez", Acento = Tema.Apagado };
            bSalir.Accion += (s, e) => SalirAhora?.Invoke(this, EventArgs.Empty);
            bQuedar.Accion += (s, e) => Quedarse?.Invoke(this, EventArgs.Empty);
            bCancelar.Accion += (s, e) => CancelarEstaVez?.Invoke(this, EventArgs.Empty);
            Controls.AddRange(new Control[] { bSalir, bQuedar, bCancelar });
            fade.Tick += (s, e) => { Opacity = Math.Min(1, Opacity + 0.12); if (Opacity >= 1) fade.Stop(); };
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOPMOST;
                return cp;
            }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Win32.EsquinasRedondas(Handle);
            Win32.ModoOscuro(Handle);
            Win32.BordeColor(Handle, Tema.Bgr(Tema.Mezcla(Tema.Borde, Tema.Durazno, 0.3f)));
        }

        public void Mostrar(Vista v, int graciaSegundos, int posponerMinutos)
        {
            vista = v; gracia = Math.Max(1, graciaSegundos);
            bQuedar.Poner($"Quedarme {posponerMinutos} min");
            esc = Dpi.Escala(this);
            Acomodar();
            if (!Visible) { Opacity = 0; Show(); fade.Start(); }
            Invalidate();
        }
        public void Actualizar(Vista v) { vista = v; Invalidate(); }

        /// <summary>
        /// Se dibuja a sí misma en un PNG SIN aparecer en el escritorio: se acomoda como siempre, se muda a (20000, 20000) y se
        /// muestra sin activar. Para la documentación y para revisar el aviso sin esperar a que una sala se vacíe.
        /// </summary>
        internal void Fotografiar(Vista v, int graciaSegundos, int posponerMinutos, string ruta)
        {
            vista = v; gracia = Math.Max(1, graciaSegundos);
            bQuedar.Poner($"Quedarme {posponerMinutos} min");
            esc = Dpi.Escala(this);
            Acomodar();
            Location = new Point(20000, 20000);
            Opacity = 1;
            Win32.ShowWindow(Handle, Win32.SW_SHOWNOACTIVATE);
            try
            {
                Application.DoEvents();
                using (var bmp = new Bitmap(Width, Height))
                {
                    DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                    bmp.Save(ruta, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            finally { Win32.ShowWindow(Handle, Win32.SW_HIDE); }
        }
        public void Ocultar() { fade.Stop(); if (Visible) Hide(); }

        void Acomodar()
        {
            int w = Dpi.S(esc, 500), h = Dpi.S(esc, 150);
            var area = Screen.PrimaryScreen.WorkingArea;
            Bounds = new Rectangle(area.Right - w - Dpi.S(esc, 18), area.Bottom - h - Dpi.S(esc, 18), w, h);
            foreach (var c in new[] { bSalir, bQuedar, bCancelar }) c.Ajustar();
            int y = h - bSalir.Height - Dpi.S(esc, 16);
            int x = Dpi.S(esc, 118);
            bSalir.Location = new Point(x, y); x += bSalir.Width + Dpi.S(esc, 8);
            bQuedar.Location = new Point(x, y); x += bQuedar.Width + Dpi.S(esc, 8);
            bCancelar.Location = new Point(x, y);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Tema.Fondo);
            var rc = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Tema.Tarjeta_(g, rc, Dpi.S(esc, 12), Tema.Panel, Tema.Alpha(Tema.Durazno, 90));
            // anillo con el tiempo que queda
            int d = Dpi.S(esc, 78);
            var ra = new RectangleF(Dpi.S(esc, 22), (Height - d) / 2f, d, d);
            float frac = gracia > 0 ? vista.SegundosRestantes / (float)gracia : 0;
            Tema.Anillo(g, ra, Dpi.S(esc, 5), frac, Tema.BordeSuave, Tema.Durazno);
            Tema.Texto_(g, vista.SegundosRestantes.ToString(), Tema.Fina(22f), Tema.Texto, Rectangle.Round(ra), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            int x = Dpi.S(esc, 118), y = Dpi.S(esc, 18);
            bool esDemo = (vista.Lectura?.Reunion ?? "").StartsWith("DEMO", StringComparison.OrdinalIgnoreCase);
            Tema.Texto_(g, (esDemo ? "DEMO · " : "") + (vista.Estado == Estado.Saliendo ? "SALIENDO DE LA REUNIÓN" : "LA SALA SE VACIÓ"), Tema.Media(9f), esDemo ? Tema.Crema : Tema.Durazno, new Rectangle(x, y, Width - x, Dpi.S(esc, 18)));
            y += Dpi.S(esc, 22);
            string reunion = vista.Lectura?.Reunion ?? "";
            string linea = vista.Estado == Estado.Saliendo ? $"Dejando «{reunion}»…" : $"Salgo de «{reunion}» en {vista.SegundosRestantes} s";
            Tema.Texto_(g, linea, Tema.Fina(12.5f), Tema.Texto, new Rectangle(x, y, Width - x - Dpi.S(esc, 12), Dpi.S(esc, 24)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            y += Dpi.S(esc, 26);
            string motivo = vista.Motivo.Length > 0 ? vista.Motivo : "no queda nadie más en la sala";
            Tema.Texto_(g, motivo + (vista.Simulacion ? " · SIMULACIÓN: no salgo de verdad" : ""), Tema.Fina(9f), Tema.Apagado, new Rectangle(x, y, Width - x - Dpi.S(esc, 12), Dpi.S(esc, 18)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}
