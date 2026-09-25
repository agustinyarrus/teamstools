using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace TeamsTools
{
    /// <summary>Una cosa que la app está haciendo ahora mismo.</summary>
    internal sealed class Tarea
    {
        public string Que = "", Detalle = "";
        public DateTime Desde = DateTime.Now;
        public double? Progreso;              // 0..1 si se sabe; null si no se sabe cuánto falta
        public Color Tinte = Tema.Cyan;
        public TimeSpan Lleva => DateTime.Now - Desde;
        public override string ToString() => Que;
    }

    /// <summary>
    /// Registro central de lo que la app está haciendo. Cualquier trabajo de fondo se anota acá y así hay UN
    /// lugar que sabe si está pasando algo: sin esto, el user mira una pantalla quieta y no puede distinguir
    /// «terminó» de «se colgó».
    ///
    /// Uso: `using (Tareas.Empezar("leyendo el historial")) { ... }` — se saca sola al salir del bloque,
    /// incluso si explota.
    /// </summary>
    internal static class Tareas
    {
        static readonly List<Tarea> vivas = new List<Tarea>();
        static readonly object candado = new object();
        public static event Action Cambio;

        public static Tarea[] Vivas { get { lock (candado) return vivas.ToArray(); } }
        public static bool HayAlgo { get { lock (candado) return vivas.Count > 0; } }
        public static int Cuantas { get { lock (candado) return vivas.Count; } }

        /// <summary>La tarea más vieja: es la que conviene mostrar cuando hay varias.</summary>
        public static Tarea Principal
        {
            get { lock (candado) return vivas.OrderBy(t => t.Desde).FirstOrDefault(); }
        }

        public static Testigo Empezar(string que, string detalle = "", Color? tinte = null)
        {
            var t = new Tarea { Que = que, Detalle = detalle ?? "", Tinte = tinte ?? Tema.Cyan };
            lock (candado) vivas.Add(t);
            Avisar();
            return new Testigo(t);
        }

        internal static void Terminar(Tarea t)
        {
            if (t == null) return;
            lock (candado) vivas.Remove(t);
            Avisar();
        }

        static void Avisar() { try { Cambio?.Invoke(); } catch { } }

        /// <summary>El objeto que devuelve Empezar: al soltarlo, la tarea desaparece del registro.</summary>
        internal sealed class Testigo : IDisposable
        {
            public readonly Tarea Tarea;
            bool listo;
            public Testigo(Tarea t) { Tarea = t; }
            /// <summary>Cambia lo que se muestra mientras la tarea avanza.</summary>
            public void Paso(string detalle, double? progreso = null)
            {
                if (Tarea == null) return;
                Tarea.Detalle = detalle ?? "";
                Tarea.Progreso = progreso;
                try { Cambio?.Invoke(); } catch { }
            }
            public void Dispose() { if (listo) return; listo = true; Terminar(Tarea); }
        }
    }

    /// <summary>
    /// Indicadores de que algo está pasando, en varios estilos. Todos comparten UN solo temporizador y solo
    /// late mientras haya alguno visible y encendido: una animación que corre en vano es batería tirada.
    /// </summary>
    internal sealed class Cargador : Control
    {
        public enum Estilo
        {
            Puntos,       // tres puntitos que respiran — «pensando»
            Barra,        // una barra que va y viene — «trabajando, no sé cuánto falta»
            Progreso,     // barra con porcentaje — cuando sí se sabe
            Anillo,       // arco que gira — compacto, para una esquina
            Pulso,        // un punto que late — «sigo vivo»
            Ondas,        // barritas tipo ecualizador — «está masticando datos»
        }

        public Estilo Modo = Estilo.Puntos;
        public string Texto = "";
        public Color Acento = Tema.Cyan;
        public Color Superficie = Tema.Fondo;
        public double? Progreso;                 // 0..1 para el modo Progreso
        public bool MostrarTiempo;
        public DateTime? Desde;
        bool activo;

        public bool Activo
        {
            get => activo;
            set
            {
                if (activo == value) return;
                activo = value;
                if (activo && !Desde.HasValue) Desde = DateTime.Now;
                if (!activo) Desde = null;
                Latido(this);
                Invalidate();
            }
        }

        public Cargador()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            TabStop = false;
        }

        // ---- un solo reloj para todos
        static readonly List<Cargador> vivos = new List<Cargador>();
        static Timer reloj;
        static int cuadro;

        static void Latido(Cargador c)
        {
            lock (vivos)
            {
                if (c.activo && c.Visible) { if (!vivos.Contains(c)) vivos.Add(c); }
                else vivos.Remove(c);

                if (reloj == null)
                {
                    reloj = new Timer { Interval = 60 };
                    reloj.Tick += (o, e) =>
                    {
                        cuadro++;
                        Cargador[] copia;
                        lock (vivos) copia = vivos.ToArray();
                        foreach (var x in copia) { try { if (x.IsHandleCreated && x.Visible) x.Invalidate(); } catch { } }
                        lock (vivos) if (vivos.Count == 0) reloj.Stop();
                    };
                }
                if (vivos.Count > 0 && !reloj.Enabled) reloj.Start();
            }
        }

        protected override void OnVisibleChanged(EventArgs e) { Latido(this); base.OnVisibleChanged(e); }
        protected override void Dispose(bool disposing) { if (disposing) { lock (vivos) vivos.Remove(this); } base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Superficie);
            float esc = Dpi.Escala(this);
            int S(int px) => Dpi.S(esc, px);
            if (!activo && Texto.Length == 0) return;

            int alto = Height, medio = alto / 2;
            int x = 0, anchoInd = S(26);

            if (activo)
            {
                switch (Modo)
                {
                    case Estilo.Puntos: Puntos(g, S, medio); anchoInd = S(24); break;
                    case Estilo.Barra: Barra(g, S, medio); anchoInd = S(54); break;
                    case Estilo.Progreso: BarraProgreso(g, S, medio); anchoInd = S(54); break;
                    case Estilo.Anillo: Anillo(g, S, medio); anchoInd = S(16); break;
                    case Estilo.Pulso: Pulso(g, S, medio); anchoInd = S(14); break;
                    case Estilo.Ondas: Ondas(g, S, medio); anchoInd = S(24); break;
                }
                x = anchoInd + S(8);
            }

            string t = Texto;
            if (MostrarTiempo && Desde.HasValue)
            {
                var d = DateTime.Now - Desde.Value;
                t += (t.Length > 0 ? "  " : "") + (d.TotalSeconds < 60 ? $"{d.TotalSeconds:0} s" : $"{(int)d.TotalMinutes}:{d.Seconds:00}");
            }
            if (t.Length > 0)
                Tema.Texto_(g, t, Tema.Fina(8.5f), activo ? Tema.TextoSuave : Tema.Apagado,
                    new Rectangle(x, 0, Math.Max(S(10), Width - x), alto), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        // ---- los dibujos
        void Puntos(Graphics g, Func<int, int> S, int medio)
        {
            int d = S(4), sep = S(8);
            for (int i = 0; i < 3; i++)
            {
                double f = (Math.Sin((cuadro * 0.16) - i * 0.7) + 1) / 2;         // desfasados: se ve una ola
                int alpha = 70 + (int)(170 * f);
                float dd = d * (0.72f + 0.42f * (float)f);
                using (var b = new SolidBrush(Tema.Alpha(Acento, alpha)))
                    g.FillEllipse(b, i * sep + (d - dd) / 2f, medio - dd / 2f, dd, dd);
            }
        }

        void Barra(Graphics g, Func<int, int> S, int medio)
        {
            int w = S(54), h = S(4);
            using (var b = new SolidBrush(Tema.Alpha(Tema.Texto, 26)))
            using (var p = Tema.Redondeado(new RectangleF(0, medio - h / 2f, w, h), h / 2f)) g.FillPath(b, p);
            // lanzadera que va y vuelve con easing, no un barrido lineal que se ve robótico
            double t = (Math.Sin(cuadro * 0.06) + 1) / 2;
            int wl = (int)(w * 0.34);
            float xl = (float)((w - wl) * t);
            using (var b = new SolidBrush(Tema.Alpha(Acento, 225)))
            using (var p = Tema.Redondeado(new RectangleF(xl, medio - h / 2f, wl, h), h / 2f)) g.FillPath(b, p);
        }

        void BarraProgreso(Graphics g, Func<int, int> S, int medio)
        {
            int w = S(54), h = S(4);
            double f = Math.Max(0, Math.Min(1, Progreso ?? 0));
            using (var b = new SolidBrush(Tema.Alpha(Tema.Texto, 26)))
            using (var p = Tema.Redondeado(new RectangleF(0, medio - h / 2f, w, h), h / 2f)) g.FillPath(b, p);
            if (f > 0)
                using (var b = new SolidBrush(Tema.Alpha(Acento, 235)))
                using (var p = Tema.Redondeado(new RectangleF(0, medio - h / 2f, (float)Math.Max(h, w * f), h), h / 2f)) g.FillPath(b, p);
        }

        void Anillo(Graphics g, Func<int, int> S, int medio)
        {
            int d = S(13);
            var r = new RectangleF(1, medio - d / 2f, d, d);
            using (var pen = new Pen(Tema.Alpha(Tema.Texto, 30), S(2))) g.DrawEllipse(pen, r);
            float ini = (cuadro * 7) % 360;
            float barre = 70 + 50 * (float)((Math.Sin(cuadro * 0.07) + 1) / 2);     // el arco respira mientras gira
            using (var pen = new Pen(Acento, S(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, r, ini, barre);
        }

        void Pulso(Graphics g, Func<int, int> S, int medio)
        {
            int d = S(6);
            double f = (Math.Sin(cuadro * 0.12) + 1) / 2;
            float halo = d * (1.4f + 1.3f * (float)f);
            using (var b = new SolidBrush(Tema.Alpha(Acento, (int)(60 * (1 - f)))))
                g.FillEllipse(b, 1 + (d - halo) / 2f, medio - halo / 2f, halo, halo);
            using (var b = new SolidBrush(Tema.Alpha(Acento, 200 + (int)(55 * f))))
                g.FillEllipse(b, 1, medio - d / 2f, d, d);
        }

        void Ondas(Graphics g, Func<int, int> S, int medio)
        {
            int n = 4, w = S(3), sep = S(6), alto = S(14);
            for (int i = 0; i < n; i++)
            {
                double f = (Math.Sin(cuadro * 0.2 - i * 0.9) + 1) / 2;
                float h = (float)(alto * (0.28 + 0.72 * f));
                using (var b = new SolidBrush(Tema.Alpha(Acento, 130 + (int)(110 * f))))
                using (var p = Tema.Redondeado(new RectangleF(i * sep, medio - h / 2f, w, h), S(1))) g.FillPath(b, p);
            }
        }
    }
}
