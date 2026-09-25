using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace TeamsTools
{
    // =====================================================================================================
    // CARGA: los detallitos que dicen «algo está en camino» sin tapar nada.
    //
    //   Animacion   UN solo reloj de 30 fps para toda la app; late solo mientras alguien se anima
    //   Brillo      el esqueleto con destello que viaja (tablas y tarjetas esperando su primer dato)
    //   LineaCarga  la línea finita bajo la barra de título: cometa si no se sabe cuánto falta, barra si sí
    //   Giro        el arco que gira dentro de un chip mientras su acción corre de fondo
    //
    // Todo es pintura: si la UI está sana (y con Fondo lo está), la animación es fluida; si algo la trabara,
    // la animación se congelaría a la vista — por eso el LatidoUi existe.
    // =====================================================================================================

    /// <summary>
    /// Reloj de animación compartido. Un control se «enciende» y recibe un Invalidate cada cuadro; cuando nadie
    /// está encendido, el timer se apaga solo (una animación que corre en vano es batería tirada).
    /// </summary>
    internal static class Animacion
    {
        const int CuadroMs = 33;
        static readonly HashSet<Control> vivos = new HashSet<Control>();
        static readonly Stopwatch reloj = Stopwatch.StartNew();
        static Timer timer;

        /// <summary>Segundos desde que arrancó la app: la fase de todas las animaciones (quedan sincronizadas).</summary>
        public static double T => reloj.Elapsed.TotalSeconds;

        public static void Encender(Control c)
        {
            if (c == null) return;
            vivos.Add(c);
            if (timer == null)
            {
                timer = new Timer { Interval = CuadroMs };
                timer.Tick += (o, e) =>
                {
                    foreach (var x in vivos.ToArray())
                    {
                        if (x.IsDisposed) { vivos.Remove(x); continue; }
                        if (x.IsHandleCreated && x.Visible) x.Invalidate();
                    }
                    if (vivos.Count == 0) timer.Stop();
                };
            }
            if (!timer.Enabled) timer.Start();
        }

        public static void Apagar(Control c) { if (c != null) vivos.Remove(c); }

        /// <summary>Suavizado ease-in-out (0..1 → 0..1): lo que hace que el movimiento se vea natural y no robótico.</summary>
        public static double Suave(double x) { x = Math.Max(0, Math.Min(1, x)); return x * x * (3 - 2 * x); }
    }

    /// <summary>Esqueleto con destello: bloques tenues con una franja de luz que los cruza cada 1,6 s.</summary>
    internal static class Brillo
    {
        const double Periodo = 1.6;

        public static void Bloque(Graphics g, RectangleF r, float radio, Color superficie, Color tinte, double desfase = 0)
        {
            if (r.Width <= 1 || r.Height <= 1) return;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Tema.Redondeado(r, radio))
            {
                using (var b = new SolidBrush(Tema.Mezcla(superficie, tinte, 0.07f))) g.FillPath(b, p);
                // la franja viaja de izquierda a derecha con un poco de retraso por fila: se lee como una ola
                double fase = ((Animacion.T + desfase) % Periodo) / Periodo;
                float ancho = Math.Max(r.Height * 4, r.Width * 0.35f);
                float x = (float)(r.Left - ancho + (r.Width + 2 * ancho) * Animacion.Suave(fase));
                var rb = new RectangleF(x, r.Top, ancho, r.Height);
                var clip = g.Clip;
                g.SetClip(p, CombineMode.Intersect);
                using (var lg = new LinearGradientBrush(new RectangleF(rb.X - 1, rb.Y, rb.Width + 2, rb.Height), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal))
                {
                    var cb = new ColorBlend(3)
                    {
                        Colors = new[] { Color.FromArgb(0, tinte), Color.FromArgb(38, tinte), Color.FromArgb(0, tinte) },
                        Positions = new[] { 0f, 0.5f, 1f },
                    };
                    lg.InterpolationColors = cb;
                    g.FillRectangle(lg, rb);
                }
                g.Clip = clip;
            }
            g.SmoothingMode = old;
        }

        /// <summary>Filas de esqueleto para una tabla vacía que está esperando su primer dato.</summary>
        public static void Filas(Graphics g, Rectangle area, int[] anchosColumnas, int altoFila, float esc, Color superficie)
        {
            int S(int px) => Dpi.S(esc, px);
            int filas = Math.Max(1, Math.Min(9, area.Height / Math.Max(1, altoFila)));
            for (int f = 0; f < filas; f++)
            {
                int y = area.Top + f * altoFila;
                float x = area.Left;
                // la opacidad cae hacia abajo: las primeras filas «pesan» más, como un contenido real a medio llegar
                float peso = 1f - f / (float)(filas + 2);
                var tinte = Tema.Mezcla(superficie, Tema.Texto, 0.55f * peso + 0.2f);
                for (int c = 0; c < anchosColumnas.Length; c++)
                {
                    int w = anchosColumnas[c];
                    // largos pseudoaleatorios pero estables por (fila, columna): no «bailan» entre cuadros
                    double k = 0.35 + 0.55 * ((Math.Sin(f * 12.9898 + c * 78.233) * 43758.5453) % 1 + 1) % 1;
                    float wb = Math.Max(S(10), (float)((w - S(10)) * k));
                    Brillo.Bloque(g, new RectangleF(x, y + altoFila * 0.3f, wb, Math.Max(S(4), altoFila * 0.36f)), S(3), superficie, tinte, f * 0.07);
                    x += w;
                }
            }
        }
    }

    /// <summary>
    /// La línea finita bajo la barra de título. Aparece con un fundido cuando hay trabajo de fondo y se va igual:
    ///   · sin progreso conocido → un cometa pastel que la recorre (ease-in-out, nunca lineal);
    ///   · con progreso → una barra que avanza suave hacia el valor (no salta) con un destello encima.
    /// </summary>
    internal sealed class LineaCarga : Control
    {
        bool activo;
        double visible;          // 0..1, el fundido
        double mostrado;         // el progreso dibujado (persigue al real con suavidad)
        DateTime ultimoCuadro = DateTime.Now;
        public double? Progreso;
        public Color Acento = Tema.Cyan;
        public Color Superficie = Tema.Fondo;

        public LineaCarga()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            Enabled = false;     // no roba el mouse: se ve, no se toca
        }

        public bool Activo
        {
            get => activo;
            set
            {
                if (activo == value) return;
                activo = value;
                if (activo && visible < 0.05) mostrado = 0;
                Animacion.Encender(this);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Superficie);
            double dt = Math.Min(0.1, (DateTime.Now - ultimoCuadro).TotalSeconds);
            ultimoCuadro = DateTime.Now;
            visible = activo ? Math.Min(1, visible + dt / 0.25) : Math.Max(0, visible - dt / 0.35);
            if (!activo && visible <= 0) { Animacion.Apagar(this); return; }
            int a = (int)(255 * Animacion.Suave(visible));
            int w = Width, h = Height;
            using (var b = new SolidBrush(Color.FromArgb(a * 22 / 255, Acento))) g.FillRectangle(b, 0, 0, w, h);

            // un 0 % (whisper cargando el modelo, el primer segmento que tarda) se ve como NADA: mientras no haya un
            // avance real se muestra el cometa, que dice «trabajando» sin mentir un porcentaje
            if (Progreso.HasValue && Progreso.Value > 0.005)
            {
                double objetivo = Math.Max(0, Math.Min(1, Progreso.Value));
                mostrado += (objetivo - mostrado) * Math.Min(1, dt * 6);       // persigue el valor: nada salta
                float ancho = (float)(w * mostrado);
                if (ancho > 0)
                {
                    using (var lg = new LinearGradientBrush(new RectangleF(0, 0, Math.Max(2, ancho), h), Color.FromArgb(a * 150 / 255, Acento), Color.FromArgb(a, Acento), LinearGradientMode.Horizontal))
                        g.FillRectangle(lg, 0, 0, ancho, h);
                    double fase = (Animacion.T % 1.8) / 1.8;
                    float xb = (float)(ancho * Animacion.Suave(fase));
                    using (var lg = new LinearGradientBrush(new RectangleF(xb - 40, 0, 80, h), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal))
                    {
                        lg.InterpolationColors = new ColorBlend(3) { Colors = new[] { Color.FromArgb(0, Color.White), Color.FromArgb(a * 90 / 255, Color.White), Color.FromArgb(0, Color.White) }, Positions = new[] { 0f, 0.5f, 1f } };
                        g.FillRectangle(lg, Math.Max(0, xb - 40), 0, Math.Min(80, ancho - Math.Max(0, xb - 40)), h);
                    }
                }
            }
            else
            {
                double fase = (Animacion.T % 1.5) / 1.5;
                float cometa = Math.Max(60, w * 0.22f);
                float x = (float)(-cometa + (w + cometa) * Animacion.Suave(fase));
                using (var lg = new LinearGradientBrush(new RectangleF(x - 1, 0, cometa + 2, h), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal))
                {
                    lg.InterpolationColors = new ColorBlend(3) { Colors = new[] { Color.FromArgb(0, Acento), Color.FromArgb(a, Acento), Color.FromArgb(0, Acento) }, Positions = new[] { 0f, 0.65f, 1f } };
                    g.FillRectangle(lg, x, 0, cometa, h);
                }
            }
        }
    }

    /// <summary>El arco que gira: para chips ocupados y rincones chicos.</summary>
    internal static class Giro
    {
        public static void Pintar(Graphics g, RectangleF r, Color color, float grosor)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            double t = Animacion.T;
            float ini = (float)(t * 360 % 360);
            float barre = (float)(70 + 180 * Animacion.Suave((Math.Sin(t * 2.4) + 1) / 2));   // el arco respira mientras gira
            using (var pista = new Pen(Tema.Alpha(color, 40), grosor)) g.DrawEllipse(pista, r);
            using (var p = new Pen(color, grosor) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(p, r, ini, barre);
            g.SmoothingMode = old;
        }
    }
}
