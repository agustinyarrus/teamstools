using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace TeamsTools
{
    /// <summary>
    /// El «REC» de la barra de título: se ve desde CUALQUIER pestaña mientras se graba. Un punto que late, el reloj
    /// de la grabación y dos vúmetros finitos en vivo —la llamada (cian) y tu micrófono (malva), los mismos colores
    /// de la banda— más «mic silenciado» si Teams te tiene en silencio. Un clic lleva a la pestaña de llamadas.
    /// Lee el pulso en cada cuadro (30 por segundo, el reloj de Animacion); apagado, no cuesta nada.
    /// </summary>
    internal sealed class IndicadorGrabacion : Control
    {
        public Func<PulsoAudio> FuentePulso;
        public Func<EnVivo> FuenteVivo;
        public event Action Ir;
        double nLlamada = -60, nMic = -60;
        DateTime ultimoCuadro = DateTime.Now;
        bool hover, grabando;
        readonly ToolTip ayuda = new ToolTip { InitialDelay = 400 };

        public IndicadorGrabacion()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            Cursor = Cursors.Hand;
            Visible = false;
        }

        public void Poner(bool siGraba, string reunion)
        {
            if (siGraba == grabando) return;
            grabando = siGraba;
            Visible = siGraba;
            if (siGraba) { Animacion.Encender(this); ayuda.SetToolTip(this, $"grabando «{reunion}» · clic para ver la grabación"); }
            else Animacion.Apagar(this);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e) { if (e.Button == MouseButtons.Left) Ir?.Invoke(); base.OnMouseClick(e); }
        protected override void Dispose(bool disposing) { if (disposing) ayuda.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float esc = Dpi.Escala(this);
            int S(int px) => Dpi.S(esc, px);
            double dt = Math.Min(0.1, (DateTime.Now - ultimoCuadro).TotalSeconds);
            ultimoCuadro = DateTime.Now;
            var v = FuenteVivo?.Invoke();
            var pu = FuentePulso?.Invoke();
            Color acento = v != null && v.CierraEn.HasValue ? Tema.Durazno : Tema.Rosa;

            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Tema.Tarjeta_(g, r, r.Height / 2, Tema.Mezcla(Tema.Tarjeta, acento, hover ? 0.16f : 0.09f), Tema.Alpha(acento, hover ? 110 : 60));

            // el punto que late
            double lat = (Math.Sin(Animacion.T * 3.4) + 1) / 2;
            float d = S(7), cx = S(12), cy = Height / 2f;
            using (var b = new SolidBrush(Tema.Alpha(acento, (int)(80 * (1 - lat))))) { float h = d * (1.5f + (float)lat); g.FillEllipse(b, cx - h / 2, cy - h / 2, h, h); }
            using (var b = new SolidBrush(acento)) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);

            int x = S(22);
            Tema.Texto_(g, "REC", Tema.Media(7.5f), acento, new Rectangle(x, 0, S(26), Height));
            x += S(28);
            var t = TimeSpan.FromSeconds(Math.Max(0, v?.Segundos ?? 0));
            string reloj = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
            Tema.Texto_(g, reloj, Tema.Fina(9.5f), Tema.Texto, new Rectangle(x, 0, S(52), Height));
            x += S(50);

            // dos vúmetros finitos: la llamada arriba, tu micrófono abajo (balística de vúmetro, como la banda)
            float wm = S(48), hm = Math.Max(2, S(3));
            bool mute = pu?.MicSilenciado == true;
            float ultimoLlamada = -60, ultimoMic = -60;
            if (pu != null && pu.Escritas > 0)
            {
                ultimoLlamada = pu.Pistas.Where(p => !p.EsMic && p.Estado == "grabando").Select(p => p.Db).DefaultIfEmpty(-120f).Max();
                ultimoMic = pu.Pistas.Where(p => p.EsMic && p.Estado == "grabando").Select(p => p.Db).DefaultIfEmpty(-120f).Max();
            }
            nLlamada = Paso(nLlamada, ultimoLlamada, dt);
            nMic = Paso(nMic, ultimoMic, dt);
            Barra(g, x, cy - hm - S(1), wm, hm, nLlamada, Tema.Cyan);
            Barra(g, x, cy + S(1), wm, hm, nMic, mute ? Tema.Apagado : Tema.Malva);
            x += (int)wm + S(8);
            if (mute) Tema.Texto_(g, "silenciado", Tema.Fina(8f), Tema.Durazno, new Rectangle(x, 0, Width - x - S(8), Height));
            else if (pu == null || pu.Escritas == 0) Tema.Texto_(g, "sin pulso", Tema.Fina(8f), Tema.Apagado, new Rectangle(x, 0, Width - x - S(8), Height));
        }

        static double Paso(double nivel, double objetivo, double dt)
        {
            objetivo = Math.Max(-60, Math.Min(0, objetivo));
            return nivel + (objetivo - nivel) * Math.Min(1, dt * (objetivo > nivel ? 18 : 2.5));
        }

        static void Barra(Graphics g, float x, float y, float w, float h, double db, Color c)
        {
            using (var b = new SolidBrush(Tema.Alpha(Tema.Texto, 22))) g.FillRectangle(b, x, y, w, h);
            float f = (float)Math.Max(0, Math.Min(1, (db + 60) / 60));
            if (f > 0.01) using (var b = new SolidBrush(Tema.Alpha(c, 225))) g.FillRectangle(b, x, y, w * f, h);
        }
    }
}
