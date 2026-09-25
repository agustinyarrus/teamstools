using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TeamsTools
{
    /// <summary>
    /// Icono de bandeja con estado dibujado en vivo (color + segundos de la cuenta regresiva).
    /// ⭐ 25-sep-2026: con la reunión ya cortada, mientras el grabador la archiva, separa las voces, la transcribe o la
    /// resume, el punto se pinta en MALVA (el color de la transcripción en toda la app) con un anillo que se va
    /// LLENANDO con el avance y el punto que se llena de abajo hacia arriba. Los estados de siempre no cambian.
    /// </summary>
    internal sealed class Bandeja : IDisposable
    {
        /// <summary>Lo que el grabador está procesando de fondo, para pintarlo en el icono.</summary>
        internal sealed class Proceso
        {
            public string Reunion = "", Que = "";
            /// <summary>0..1 de la etapa; −1 si todavía no se sabe (cargando el modelo, archivando).</summary>
            public double Progreso = -1;
            /// <summary>La transcripción espera a que termine tu llamada.</summary>
            public bool EnPausa;
        }

        const int PasosDeLlenado = 48;             // el anillo avanza de a 1/48 (7,5°): se ve continuo y no se redibuja de más
        readonly NotifyIcon icono = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripMenuItem miPausa, miSimulacion, miPresencia, miAutomatico, miMostrar, miSalirReunion, miVolcar, miCarpeta, miCerrar;
        IntPtr hIconActual = IntPtr.Zero;
        string claveActual = "";
        Proceso proceso;
        public event EventHandler Mostrar, AlternarPausa, AlternarSimulacion, AlternarPresencia, AlternarAutomatico, SalirReunion, Volcar, AbrirCarpeta, Cerrar;
        /// <summary>Clic en el globo de un aviso (p. ej. «transcripción lista»): lo usa el panel para ir a esa pestaña.</summary>
        public event EventHandler AvisoClic;

        public Bandeja()
        {
            menu.Renderer = new RenderOscuro();
            menu.BackColor = Tema.Panel;
            menu.ForeColor = Tema.Texto;
            menu.Font = Tema.Fina(9.5f);
            menu.ShowImageMargin = true;
            miMostrar = Item("Mostrar el panel", (s, e) => Mostrar?.Invoke(this, EventArgs.Empty));
            miMostrar.Font = Tema.Media(9.5f);
            miPausa = Item("Pausar la vigilancia", (s, e) => AlternarPausa?.Invoke(this, EventArgs.Empty));
            miSimulacion = Item("Modo simulación (no sale de verdad)", (s, e) => AlternarSimulacion?.Invoke(this, EventArgs.Empty));
            miPresencia = Item("Mantenerme Disponible (sin Ausente automático)", (s, e) => AlternarPresencia?.Invoke(this, EventArgs.Empty));
            miAutomatico = Item("Modo automático: contestar los chats por mí", (s, e) => AlternarAutomatico?.Invoke(this, EventArgs.Empty));
            miSalirReunion = Item("Salir de la reunión ahora", (s, e) => SalirReunion?.Invoke(this, EventArgs.Empty));
            miVolcar = Item("Volcar el árbol UIA (diagnóstico)", (s, e) => Volcar?.Invoke(this, EventArgs.Empty));
            miCarpeta = Item("Abrir la carpeta de datos", (s, e) => AbrirCarpeta?.Invoke(this, EventArgs.Empty));
            miCerrar = Item("Cerrar TeamsTools", (s, e) => Cerrar?.Invoke(this, EventArgs.Empty));
            menu.Items.AddRange(new ToolStripItem[] { miMostrar, new ToolStripSeparator(), miAutomatico, miPresencia, miPausa, miSimulacion, new ToolStripSeparator(), miSalirReunion, miVolcar, miCarpeta, new ToolStripSeparator(), miCerrar });
            icono.ContextMenuStrip = menu;
            icono.Text = "TeamsTools";
            icono.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Mostrar?.Invoke(this, EventArgs.Empty); };
            icono.DoubleClick += (s, e) => Mostrar?.Invoke(this, EventArgs.Empty);
            icono.BalloonTipClicked += (s, e) => AvisoClic?.Invoke(this, EventArgs.Empty);
            Pintar(Tema.Apagado, "", false);
            icono.Visible = true;
        }

        static ToolStripMenuItem Item(string texto, EventHandler h)
        {
            var it = new ToolStripMenuItem(texto) { ForeColor = Tema.Texto };
            it.Click += h;
            return it;
        }

        public void Aviso(string titulo, string texto, ToolTipIcon tipo = ToolTipIcon.None)
        {
            try { icono.ShowBalloonTip(5000, titulo, string.IsNullOrEmpty(texto) ? " " : texto, tipo); } catch { }
        }

        public bool Automatico;

        /// <summary>Lo que está procesando el grabador (null = nada). Se ve en el próximo <see cref="Actualizar"/>.</summary>
        public void PonerProceso(Proceso p) => proceso = p;

        public void Actualizar(Vista v, bool pausado, bool simulacion, bool presencia = true)
        {
            if (v == null) return;
            miPausa.Text = pausado ? "Reanudar la vigilancia" : "Pausar la vigilancia";
            miSimulacion.Checked = simulacion;
            miPresencia.Checked = presencia;
            miAutomatico.Checked = Automatico;
            miSalirReunion.Enabled = v.Lectura != null && v.Lectura.HayLlamada;
            Color c; string num = ""; bool pulso = false;
            switch (v.Estado)
            {
                case Estado.SinTeams: c = Tema.MuyApagado; break;
                case Estado.SinLlamada: c = Tema.Apagado; break;
                case Estado.EnLlamada: c = Tema.Cyan; break;
                case Estado.EsperandoGente: c = Tema.Cielo; break;
                case Estado.SalaVacia: c = Tema.Durazno; num = v.SegundosRestantes.ToString(); pulso = true; break;
                case Estado.Pospuesto: c = Tema.Crema; break;
                case Estado.Saliendo: c = Tema.Rosa; pulso = true; break;
                case Estado.Salido: c = Tema.Salvia; break;
                case Estado.Pausado: c = Tema.Malva; break;
                default: c = Tema.Apagado; break;
            }
            if (pausado) c = Tema.Malva;
            // la llamada manda: mientras estás en una (o la vigilancia está en pausa) el icono es el de siempre, y la
            // transcripción igual espera a que cortes. Libre de llamadas, se ve el grabador trabajando.
            bool libre = !pausado && (v.Estado == Estado.SinLlamada || v.Estado == Estado.SinTeams || v.Estado == Estado.Salido);
            var p = proceso;
            if (libre && p != null) PintarProceso(p); else Pintar(c, num, pulso);
            string tip = v.EstadoTexto;
            if (v.Lectura != null && v.Lectura.HayLlamada) tip += $"\n{v.Lectura.Reunion}\n{v.Lectura.Otros} otros · {v.Lectura.Duracion}";
            if (v.Estado == Estado.SalaVacia) tip += $"\nsalgo en {v.SegundosRestantes} s";
            if (simulacion) tip += "\nSIMULACIÓN";
            if (!presencia) tip += "\npresencia: Teams decide";
            if (Automatico) tip += "\nMODO AUTOMÁTICO";
            if (p != null)
            {
                string trabajo = (p.EnPausa ? "transcripción en pausa" : p.Que) + (p.Progreso >= 0 && !p.EnPausa ? $" · {p.Progreso:P0}" : "");
                // libre de llamadas, lo primero que se lee es lo que el icono está mostrando: el grabador trabajando
                tip = libre ? trabajo + "\n«" + p.Reunion + "»" + (Automatico ? "\nMODO AUTOMÁTICO" : "") : tip + "\n" + trabajo;
            }
            if (tip.Length > 62) tip = tip.Substring(0, 62);   // NotifyIcon.Text tira excepción con más de 63
            icono.Text = tip;
        }

        /// <summary>
        /// El grabador trabajando: el disco oscuro de siempre, un anillo malva que se LLENA en sentido horario con el
        /// avance, y el punto del medio que se llena de abajo hacia arriba como un vaso. Sin avance conocido, el anillo
        /// queda en un cuarto y el punto lleno. En pausa (esperando que termines una llamada), crema.
        /// </summary>
        void PintarProceso(Proceso p)
        {
            int paso = PasoDe(p.Progreso);
            string clave = $"proceso|{paso}|{p.EnPausa}";
            if (clave == claveActual) return;
            claveActual = clave;
            using (var bmp = DibujoProceso(paso, p.EnPausa)) PonerIcono(bmp);
        }

        void Pintar(Color c, string num, bool pulso)
        {
            string clave = $"{c.ToArgb()}|{num}|{pulso}";
            if (clave == claveActual) return;
            claveActual = clave;
            using (var bmp = DibujoEstado(c, num, pulso)) PonerIcono(bmp);
        }

        /// <summary>El avance en escalones de 1/48 (−1 = no se sabe): el icono solo se redibuja cuando se ve distinto.</summary>
        internal static int PasoDe(double progreso) => progreso < 0 || double.IsNaN(progreso) ? -1 : (int)Math.Round(Math.Max(0, Math.Min(1, progreso)) * PasosDeLlenado);

        /// <summary>El dibujo de siempre de cada estado (color, cuenta regresiva, pulso). Puro: también lo usan las fotos de prueba.</summary>
        internal static Bitmap DibujoEstado(Color c, string num, bool pulso)
        {
            const int n = 32;
            var bmp = new Bitmap(n, n);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(Color.FromArgb(230, 11, 12, 16))) g.FillEllipse(b, 1, 1, n - 2, n - 2);
                if (num.Length > 0)
                {
                    using (var p = new Pen(c, 3f)) g.DrawEllipse(p, 3, 3, n - 6, n - 6);
                    using (var f = new Font(Tema.Media(8f).FontFamily, num.Length > 2 ? 9f : 12f, FontStyle.Regular, GraphicsUnit.Pixel))
                    {
                        // GDI+ centrado
                        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        using (var fb = new Font(f.FontFamily, num.Length > 2 ? 12f : 15f, FontStyle.Regular, GraphicsUnit.Pixel))
                        using (var tb = new SolidBrush(Tema.Texto)) g.DrawString(num, fb, tb, new RectangleF(0, 1, n, n), sf);
                    }
                }
                else
                {
                    using (var b = new SolidBrush(c)) g.FillEllipse(b, 9, 9, n - 18, n - 18);
                    using (var p = new Pen(Tema.Alpha(c, pulso ? 160 : 70), 2f)) g.DrawEllipse(p, 4, 4, n - 8, n - 8);
                }
            }
            return bmp;
        }

        /// <summary>El dibujo del grabador procesando (ver <see cref="PintarProceso"/>). Puro: también lo usan las fotos de prueba.</summary>
        internal static Bitmap DibujoProceso(int paso, bool enPausa)
        {
            const int n = 32;
            Color c = enPausa ? Tema.Crema : Tema.Malva;
            var bmp = new Bitmap(n, n);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(Color.FromArgb(230, 11, 12, 16))) g.FillEllipse(b, 1, 1, n - 2, n - 2);
                var anillo = new RectangleF(4.5f, 4.5f, n - 9, n - 9);
                using (var pista = new Pen(Tema.Alpha(c, 55), 3f)) g.DrawEllipse(pista, anillo);
                float barrido = paso < 0 ? 90f : Math.Max(8f, 360f * paso / PasosDeLlenado);
                using (var pen = new Pen(c, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    if (barrido >= 359.5f) g.DrawEllipse(pen, anillo); else g.DrawArc(pen, anillo, -90, barrido);
                // el punto: la parte vacía apenas insinuada y la llena de abajo hacia arriba
                var punto = new RectangleF(10, 10, n - 20, n - 20);
                using (var b = new SolidBrush(Tema.Alpha(c, 90))) g.FillEllipse(b, punto);
                float nivel = paso < 0 ? 1f : paso / (float)PasosDeLlenado;
                if (nivel > 0)
                {
                    var clip = g.Clip;
                    g.SetClip(new RectangleF(punto.X - 1, punto.Bottom - punto.Height * nivel, punto.Width + 2, punto.Height * nivel + 1));
                    using (var b = new SolidBrush(c)) g.FillEllipse(b, punto);
                    g.Clip = clip;
                }
            }
            return bmp;
        }

        /// <summary>El bitmap pasa a ser el icono de la bandeja (y se libera el HICON anterior: uno por dibujo, nunca se acumulan).</summary>
        void PonerIcono(Bitmap bmp)
        {
            IntPtr h = bmp.GetHicon();
            var ic = Icon.FromHandle(h);
            icono.Icon = (Icon)ic.Clone();
            ic.Dispose();
            if (hIconActual != IntPtr.Zero) Win32.DestroyIcon(hIconActual);
            hIconActual = h;
        }

        public void Dispose()
        {
            icono.Visible = false;
            icono.Dispose();
            menu.Dispose();
            if (hIconActual != IntPtr.Zero) { Win32.DestroyIcon(hIconActual); hIconActual = IntPtr.Zero; }
        }
    }
}
