using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TeamsTools
{
    /// <summary>
    /// El guión del envío, flotando por encima de la pantalla: todas las burbujas una debajo de la otra, en el orden
    /// en que le van a llegar al otro, con su pausa, y se pueden reordenar, borrar o abrir para editar.
    /// Se dibuja entero a mano (sin controles hijos) para que entren muchas partes sin costo y para poder
    /// superponerlo sobre la vista sin romperle el layout: se hace Dock=Fill, BringToFront y listo.
    /// El detalle fino del formato sigue estando en la vista previa de la pantalla; acá manda el ORDEN y el ritmo.
    /// </summary>
    internal sealed class PanelEnvio : Control, IRueda
    {
        public Envio Contenido = new Envio();
        public string Titulo = "el envío";
        /// <summary>Cambió algo (orden, pausa, se borró o se agregó una parte).</summary>
        public event EventHandler Cambio;
        /// <summary>El user quiere editar la parte i: la pantalla la selecciona y cierra el panel.</summary>
        public event Action<int> Editar;
        public event EventHandler Cerrado;
        /// <summary>Pidió agregar: "texto" o "imagen".</summary>
        public event Action<string> Agregar;

        static readonly int[] PresetsEspera = { 0, 1, 2, 3, 5, 8, 12 };

        int desplazamiento, hover = -1, hoverBoton = -1, hoverPie = -1;
        readonly List<Rectangle> filas = new List<Rectangle>();
        readonly List<Rectangle[]> botones = new List<Rectangle[]>();   // por fila: subir, bajar, borrar, espera
        readonly Rectangle[] pie = new Rectangle[3];                    // + mensaje, + imagen, listo
        Rectangle tarjeta, lista;
        readonly Dictionary<string, Image> minis = new Dictionary<string, Image>();

        public PanelEnvio()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            Visible = false;
            TabStop = false;
        }

        public void Mostrar(Envio e, string titulo = null)
        {
            Contenido = e ?? new Envio();
            if (titulo != null) Titulo = titulo;
            desplazamiento = 0;
            Visible = true;
            BringToFront();
            Acomodar();
            Invalidate();
        }

        public void Ocultar() { Visible = false; Cerrado?.Invoke(this, EventArgs.Empty); }

        int S(int px) => Dpi.S(Dpi.Escala(this), px);

        // ------------------------------------------------------------------ medidas

        int AltoFila(Parte p) => p.Tipo == TipoParte.Texto ? S(46) : S(58);

        void Acomodar()
        {
            if (Width <= 0 || Height <= 0) return;
            int pad = S(18);
            int ancho = Math.Min(S(760), Width - pad * 2);
            int altoNecesario = S(54) + Contenido.Partes.Sum(AltoFila) + Contenido.Partes.Count * S(6) + S(52);
            int alto = Math.Min(altoNecesario, Height - pad * 2);
            tarjeta = new Rectangle((Width - ancho) / 2, Math.Max(pad, (Height - alto) / 2), ancho, alto);
            lista = new Rectangle(tarjeta.X + S(12), tarjeta.Y + S(42), tarjeta.Width - S(24), tarjeta.Height - S(42) - S(46));

            filas.Clear(); botones.Clear();
            int y = lista.Top - desplazamiento;
            foreach (var p in Contenido.Partes)
            {
                int h = AltoFila(p);
                var r = new Rectangle(lista.X, y, lista.Width, h);
                filas.Add(r);
                int bw = S(22), bh = S(18), bx = r.Right - S(8) - bw, by = r.Y + S(6);
                var espera = new Rectangle(r.Right - S(10) - S(76), r.Bottom - S(23), S(76), S(18));
                botones.Add(new[]
                {
                    new Rectangle(bx - (bw + S(4)) * 2, by, bw, bh),   // subir
                    new Rectangle(bx - (bw + S(4)), by, bw, bh),       // bajar
                    new Rectangle(bx, by, bw, bh),                     // borrar
                    espera,
                });
                y += h + S(6);
            }
            int pw = S(92), ph = S(24), py = tarjeta.Bottom - S(34);
            pie[0] = new Rectangle(tarjeta.X + S(12), py, pw, ph);
            pie[1] = new Rectangle(pie[0].Right + S(6), py, pw, ph);
            pie[2] = new Rectangle(tarjeta.Right - S(12) - S(70), py, S(70), ph);
        }

        int AltoTotal => Contenido.Partes.Sum(AltoFila) + Contenido.Partes.Count * S(6);

        protected override void OnResize(EventArgs e) { base.OnResize(e); Acomodar(); Invalidate(); }

        public void Rueda(int delta)
        {
            int max = Math.Max(0, AltoTotal - lista.Height);
            desplazamiento = Math.Max(0, Math.Min(max, desplazamiento - Math.Sign(delta) * S(40)));
            Acomodar(); Invalidate();
        }

        // ------------------------------------------------------------------ interacción

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = filas.FindIndex(r => r.Contains(e.Location));
            int hb = -1;
            if (h >= 0) hb = Array.FindIndex(botones[h], r => r.Contains(e.Location));
            int hp = Array.FindIndex(pie, r => r.Contains(e.Location));
            if (h != hover || hb != hoverBoton || hp != hoverPie) { hover = h; hoverBoton = hb; hoverPie = hp; Invalidate(); }
            Cursor = (h >= 0 || hp >= 0) ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = hoverBoton = hoverPie = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            // clic fuera de la tarjeta = cerrar, como cualquier panel flotante
            if (!tarjeta.Contains(e.Location)) { Ocultar(); return; }

            int p = Array.FindIndex(pie, r => r.Contains(e.Location));
            if (p == 0) { Agregar?.Invoke("texto"); Acomodar(); Invalidate(); return; }
            if (p == 1) { Agregar?.Invoke("imagen"); Acomodar(); Invalidate(); return; }
            if (p == 2) { Ocultar(); return; }

            int i = filas.FindIndex(r => r.Contains(e.Location));
            if (i < 0) return;
            int b = Array.FindIndex(botones[i], r => r.Contains(e.Location));
            switch (b)
            {
                case 0: Mover(i, -1); return;
                case 1: Mover(i, +1); return;
                case 2: Borrar(i); return;
                case 3: CiclarEspera(i, e.Button == MouseButtons.Right ? -1 : +1); return;
                default: Editar?.Invoke(i); Ocultar(); return;   // clic en el cuerpo: abrir esa parte para editarla
            }
        }

        void Mover(int i, int dir)
        {
            int j = i + dir;
            if (j < 0 || j >= Contenido.Partes.Count) return;
            var t = Contenido.Partes[i]; Contenido.Partes[i] = Contenido.Partes[j]; Contenido.Partes[j] = t;
            Avisar();
        }

        void Borrar(int i)
        {
            if (i < 0 || i >= Contenido.Partes.Count) return;
            Contenido.Partes.RemoveAt(i);
            if (Contenido.Partes.Count == 0) Contenido.Partes.Add(Parte.DeTexto("", ""));
            Avisar();
        }

        void CiclarEspera(int i, int dir)
        {
            var p = Contenido.Partes[i];
            int seg = p.EsperaMs / 1000;
            int idx = Array.IndexOf(PresetsEspera, seg);
            if (idx < 0) idx = 0;
            p.EsperaMs = PresetsEspera[(idx + dir + PresetsEspera.Length) % PresetsEspera.Length] * 1000;
            Avisar();
        }

        void Avisar() { Acomodar(); Invalidate(); Cambio?.Invoke(this, EventArgs.Empty); }

        // ------------------------------------------------------------------ dibujo

        Image Mini(string ruta)
        {
            if (ruta.Length == 0) return null;
            if (minis.TryGetValue(ruta, out var im)) return im;
            Image r = null;
            try { if (File.Exists(ruta)) using (var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read)) r = Image.FromStream(fs); } catch { }
            minis[ruta] = r;
            return r;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // velo: deja ver la pantalla de atrás pero la manda al fondo
            using (var b = new SolidBrush(Tema.Alpha(Tema.Fondo, 232))) g.FillRectangle(b, ClientRectangle);
            if (tarjeta.Width <= 0) Acomodar();

            Tema.Tarjeta_(g, new RectangleF(tarjeta.X + 0.5f, tarjeta.Y + 0.5f, tarjeta.Width - 1, tarjeta.Height - 1), S(6), Tema.Panel, Tema.Alpha(Tema.Malva, 90));

            int imgs = Contenido.Partes.Count(p => p.EsAdjunto);
            string cab = $"{Titulo.ToUpperInvariant()} · {Contenido.Partes.Count} burbuja{(Contenido.Partes.Count == 1 ? "" : "s")}" +
                         (imgs > 0 ? $" · {imgs} con adjunto" : "") + " · llegan en este orden";
            Tema.Texto_(g, cab, Tema.Media(8f), Tema.Apagado, new Rectangle(tarjeta.X + S(14), tarjeta.Y + S(12), tarjeta.Width - S(28), S(16)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            var recorte = g.Clip;
            g.SetClip(lista);
            for (int i = 0; i < filas.Count && i < Contenido.Partes.Count; i++)
            {
                var r = filas[i];
                if (r.Bottom < lista.Top || r.Top > lista.Bottom) continue;
                Fila(g, i, r, Contenido.Partes[i]);
            }
            g.Clip = recorte;

            // barrita de desplazamiento, si hay más de lo que entra
            int total = AltoTotal;
            if (total > lista.Height)
            {
                int hb = Math.Max(S(18), (int)(lista.Height * (lista.Height / (float)total)));
                int y = lista.Top + (int)((lista.Height - hb) * (desplazamiento / (float)Math.Max(1, total - lista.Height)));
                using (var b = new SolidBrush(Tema.Alpha(Tema.TextoSuave, 70)))
                    g.FillRectangle(b, lista.Right + S(3), y, S(2), hb);
            }

            Boton(g, pie[0], "+ mensaje", Tema.Malva, hoverPie == 0);
            Boton(g, pie[1], "+ imagen", Tema.Cyan, hoverPie == 1);
            Boton(g, pie[2], "listo", Tema.Salvia, hoverPie == 2, true);
        }

        void Fila(Graphics g, int i, Rectangle r, Parte p)
        {
            bool enc = hover == i;
            var acento = p.Tipo == TipoParte.Texto ? Tema.Malva : Tema.Cyan;
            Tema.Tarjeta_(g, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(2),
                enc ? Tema.Mezcla(Tema.Tarjeta, acento, 0.10f) : Tema.Tarjeta,
                enc ? Tema.Alpha(acento, 130) : Tema.Alpha(Tema.TextoSuave, 30));
            // riel del acento, igual que los botones: la familia visual se mantiene
            using (var placa = Tema.Redondeado(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(2)))
            {
                var c = g.Clip;
                g.SetClip(placa, CombineMode.Intersect);
                using (var b = new SolidBrush(Tema.Alpha(acento, enc ? 235 : 150))) g.FillRectangle(b, r.X, r.Y, S(3), r.Height);
                g.Clip = c;
            }

            int x = r.X + S(11);
            Tema.Texto_(g, (i + 1).ToString(), Tema.Media(8.5f), Tema.Apagado, new Rectangle(x, r.Y + S(6), S(14), S(14)));
            x += S(16);

            if (p.Tipo == TipoParte.Texto)
            {
                string t = p.Plano.Length > 0 ? p.Plano : Rico.DesdeHtml(p.Html).Plano();
                if (t.Trim().Length == 0) t = "(burbuja vacía)";
                Tema.Texto_(g, t.Replace("\n", "  ⏎  "), Tema.Fina(9f), t.StartsWith("(") ? Tema.Apagado : Tema.Texto,
                    new Rectangle(x, r.Y + S(6), r.Width - (x - r.X) - S(86), r.Height - S(26)),
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            }
            else
            {
                var im = Mini(p.Ruta);
                int th = r.Height - S(14), tw = S(58);
                var caja = new Rectangle(x, r.Y + S(7), tw, th);
                if (im != null)
                {
                    float k = Math.Min(tw / (float)im.Width, th / (float)im.Height);
                    int w2 = Math.Max(1, (int)(im.Width * k)), h2 = Math.Max(1, (int)(im.Height * k));
                    var dst = new Rectangle(caja.X, caja.Y + (th - h2) / 2, w2, h2);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    using (var path = Tema.Redondeado(new RectangleF(dst.X, dst.Y, dst.Width, dst.Height), S(3)))
                    {
                        var c = g.Clip; g.SetClip(path); g.DrawImage(im, dst); g.Clip = c;
                    }
                }
                else Tema.Texto_(g, "sin archivo", Tema.Fina(8f), Tema.Rosa, caja);
                int xt = x + tw + S(10);
                string nom = p.Ruta.Length > 0 ? Path.GetFileName(p.Ruta) : "(sin ruta)";
                Tema.Texto_(g, nom, Tema.Fina(9f), im != null ? Tema.Texto : Tema.Rosa,
                    new Rectangle(xt, r.Y + S(8), r.Width - (xt - r.X) - S(86), S(14)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                if (p.Nota.Length > 0)
                    Tema.Texto_(g, p.Nota, Tema.Fina(8.5f), Tema.TextoSuave,
                        new Rectangle(xt, r.Y + S(24), r.Width - (xt - r.X) - S(86), S(14)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }

            var bs = botones[i];
            Mini_(g, bs[0], "▲", i > 0, hoverBoton == 0 && enc);
            Mini_(g, bs[1], "▼", i < Contenido.Partes.Count - 1, hoverBoton == 1 && enc);
            Mini_(g, bs[2], "✕", true, hoverBoton == 2 && enc, Tema.Rosa);
            string esp = p.EsperaMs == 0 ? "sin pausa" : "+" + (p.EsperaMs / 1000) + " s";
            Boton(g, bs[3], esp, p.EsperaMs > 0 ? Tema.Crema : Tema.MuyApagado, hoverBoton == 3 && enc, false, 8f);
        }

        void Mini_(Graphics g, Rectangle r, string glifo, bool habil, bool enc, Color? color = null)
        {
            var c = !habil ? Tema.MuyApagado : enc ? (color ?? Tema.Texto) : Tema.Apagado;
            if (enc && habil) Tema.Tarjeta_(g, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(2), Tema.Mezcla(Tema.Tarjeta, c, 0.14f), Tema.Alpha(c, 110));
            Tema.Texto_(g, glifo, Tema.Fina(8f), c, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        void Boton(Graphics g, Rectangle r, string texto, Color acento, bool enc, bool destacado = false, float pt = 9f)
        {
            var rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
            Tema.Tarjeta_(g, rf, S(2),
                enc ? Tema.Mezcla(Tema.Panel, acento, 0.18f) : Tema.Mezcla(Tema.Panel, Tema.Texto, 0.03f),
                destacado || enc ? Tema.Alpha(acento, 150) : Tema.Alpha(Tema.TextoSuave, 34));
            using (var placa = Tema.Redondeado(rf, S(2)))
            {
                var c = g.Clip;
                g.SetClip(placa, CombineMode.Intersect);
                using (var b = new SolidBrush(Tema.Alpha(acento, enc ? 235 : destacado ? 215 : 140))) g.FillRectangle(b, r.X, r.Y, S(3), r.Height);
                g.Clip = c;
            }
            Tema.Texto_(g, texto, Tema.Fina(pt), enc || destacado ? Tema.Texto : Tema.TextoSuave,
                new Rectangle(r.X + S(9), r.Y, r.Width - S(12), r.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var im in minis.Values) { try { im?.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }
}
