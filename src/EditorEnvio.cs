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
    /// Compositor de un ENVÍO de varias partes: una tira de fichas arriba (1 texto · 2 imagen · 3 texto…), el editor
    /// con formato de la parte de texto seleccionada, o la ficha de la imagen seleccionada con su nota y su miniatura.
    /// Es un reemplazo "enchufable" de EditorRico: misma Etiqueta, mismo evento Cambio, y además Envio / CargarEnvio.
    /// Las imágenes se copian a datos\adjuntos\ para que el envío no se rompa si el original se mueve.
    /// </summary>
    internal sealed class EditorEnvio : Control
    {
        public string Etiqueta = "MENSAJE";
        public event EventHandler Cambio;

        readonly EditorRico editor = new EditorRico { Etiqueta = "texto de esta parte" };
        readonly Campo cNota = new Campo { Etiqueta = "nota que acompaña la imagen (opcional)", Pista = "mirá esto" };
        readonly List<Chip> fichas = new List<Chip>();
        readonly List<Chip> acciones = new List<Chip>();
        Chip chMas, chImagen, chBorrar, chIzq, chDer, chEspera, chDesplegar;
        /// <summary>Pidió abrir el guión flotante con todas las burbujas. Lo engancha la pantalla que lo contiene.</summary>
        public event EventHandler Desplegar;

        readonly List<Parte> partes = new List<Parte>();
        int sel = 0;
        bool cargando;
        Image miniatura; string miniaturaDe = "";
        Rectangle rTira, rCuerpo;

        static readonly int[] PresetsEspera = { 0, 1, 2, 3, 5, 8, 12 };

        public EditorEnvio()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            Controls.Add(editor);
            Controls.Add(cNota);
            cNota.Visible = false;
            editor.Cambio += (s, e) => { if (!cargando) { GuardarParteActual(); Avisar(); } };
            cNota.Cambio += (s, e) => { if (!cargando) { GuardarParteActual(); Avisar(); } };

            chMas = Accion("+ mensaje", Tema.Malva, () => Agregar(Parte.DeTexto("", "")));
            chImagen = Accion("+ imagen", Tema.Cyan, ElegirImagen);
            chBorrar = Accion("borrar parte", Tema.Rosa, BorrarParte);
            chIzq = Accion("◀", Tema.TextoSuave, () => Mover(-1));
            chDer = Accion("▶", Tema.TextoSuave, () => Mover(+1));
            chDesplegar = Accion("desplegar ⤢", Tema.Cielo, () => Desplegar?.Invoke(this, EventArgs.Empty));
            chEspera = Accion("espera", Tema.Crema, () => CiclarEspera(+1));
            chEspera.Tipo = Chip.Modo.Valor;
            chEspera.AccionDerecha += (s, e) => CiclarEspera(-1);

            partes.Add(Parte.DeTexto("", ""));
            Seleccionar(0);
        }

        Chip Accion(string texto, Color acento, Action a)
        {
            var ch = new Chip { Text = texto, Tipo = Chip.Modo.Boton, Acento = acento };
            ch.Accion += (s, e) => a();
            Controls.Add(ch); acciones.Add(ch);
            return ch;
        }

        void Avisar() { Cambio?.Invoke(this, EventArgs.Empty); Invalidate(); }

        // ------------------------------------------------------------------ modelo

        /// <summary>El envío tal como está en la interfaz ahora.</summary>
        public Envio Envio
        {
            get { GuardarParteActual(); var e = new Envio(); e.Partes.AddRange(partes.Select(Clonar)); return e; }
        }

        static Parte Clonar(Parte p) => new Parte { Tipo = p.Tipo, Html = p.Html, Plano = p.Plano, Rtf = p.Rtf, Ruta = p.Ruta, Nota = p.Nota, EsperaMs = p.EsperaMs };

        /// <summary>Carga un envío. Si viene vacío deja una sola parte de texto en blanco.</summary>
        public void CargarEnvio(Envio e)
        {
            partes.Clear();
            if (e != null) partes.AddRange(e.Partes.Select(Clonar));
            if (partes.Count == 0) partes.Add(Parte.DeTexto("", ""));
            // 🚨 sin guardar: el editor todavía tiene lo de la regla anterior (o está vacío) y pisaría la parte recién cargada
            Seleccionar(0, false);
            Invalidate();
        }

        public void Limpiar() { partes.Clear(); partes.Add(Parte.DeTexto("", "")); Seleccionar(0, false); Invalidate(); }

        /// <summary>Habilita o bloquea el compositor entero sin dejar el editor en blanco (ver EditorRico.Habilitar).</summary>
        public void Habilitar(bool si)
        {
            editor.Habilitar(si);
            cNota.Enabled = si;
            foreach (var ch in acciones) ch.Enabled = si;
            foreach (var ch in fichas) ch.Enabled = si;
            Invalidate();
        }

        /// <summary>Selecciona la parte i (0..n-1). La usan las pantallas y las pruebas de dibujo.</summary>
        public void SeleccionarParte(int i) => Seleccionar(i);

        /// <summary>Agrega una parte del tipo pedido ("texto" o "imagen"), como si se hubiera tocado su botón.</summary>
        public void AgregarParte(string tipo)
        {
            if (tipo == "imagen") ElegirImagen();
            else Agregar(Parte.DeTexto("", ""));
        }

        /// <summary>Vuelve a leer el envío después de que el guión flotante lo reordenó o le borró partes.</summary>
        public void Resincronizar()
        {
            if (partes.Count == 0) partes.Add(Parte.DeTexto("", ""));
            Seleccionar(Math.Min(sel, partes.Count - 1), false);
            Avisar();
        }
        public int CuantasPartes => partes.Count;

        /// <summary>
        /// La lista VIVA de partes — la misma instancia que edita el compositor, no una copia. Es lo que le pasa al
        /// guión flotante para que reordenar o borrar ahí se vea acá sin sincronizar nada a mano. Antes de devolverla
        /// vuelca lo que haya en el editor, para no perder lo último que se escribió.
        /// </summary>
        public List<Parte> Partes { get { GuardarParteActual(); return partes; } }

        /// <summary>El Rico de la parte seleccionada (para la vista previa de las pantallas).</summary>
        public Rico RicoSeleccionado => Actual != null && Actual.Tipo == TipoParte.Texto ? editor.Rico : new Rico();

        Parte Actual => sel >= 0 && sel < partes.Count ? partes[sel] : null;

        void GuardarParteActual()
        {
            var p = Actual;
            if (p == null || cargando) return;
            if (p.Tipo == TipoParte.Texto)
            {
                var r = editor.Rico;
                p.Html = r.Html(); p.Plano = r.Plano(); p.Rtf = editor.Rtf;
            }
            else p.Nota = cNota.Texto;
        }

        void Seleccionar(int i, bool guardar = true)
        {
            if (guardar) GuardarParteActual();
            sel = Math.Max(0, Math.Min(i, partes.Count - 1));
            var p = Actual;
            cargando = true;
            try
            {
                bool esTexto = p != null && p.Tipo == TipoParte.Texto;
                editor.Visible = esTexto;
                cNota.Visible = !esTexto;
                if (esTexto) editor.Cargar(p.Html.Length > 0 ? p.Html : "", p.Rtf);
                else { cNota.Texto = p?.Nota ?? ""; CargarMiniatura(p?.Ruta ?? ""); }
            }
            finally { cargando = false; }
            ActualizarFichas();
            Acomodar();
            Invalidate();
        }

        void Agregar(Parte p)
        {
            GuardarParteActual();
            partes.Insert(Math.Min(sel + 1, partes.Count), p);
            Seleccionar(Math.Min(sel + 1, partes.Count - 1));
            Avisar();
        }

        void BorrarParte()
        {
            if (partes.Count <= 1) { Limpiar(); Avisar(); return; }
            if (!chBorrar.Armado) { chBorrar.Armado = true; chBorrar.Invalidate(); var t = new Timer { Interval = 3500 }; t.Tick += (s, e) => { chBorrar.Armado = false; chBorrar.Invalidate(); t.Stop(); t.Dispose(); }; t.Start(); return; }
            chBorrar.Armado = false;
            partes.RemoveAt(sel);
            Seleccionar(Math.Min(sel, partes.Count - 1));
            Avisar();
        }

        void Mover(int dir)
        {
            int j = sel + dir;
            if (j < 0 || j >= partes.Count) return;
            GuardarParteActual();
            var tmp = partes[sel]; partes[sel] = partes[j]; partes[j] = tmp;
            Seleccionar(j);
            Avisar();
        }

        void CiclarEspera(int dir)
        {
            var p = Actual;
            if (p == null) return;
            int seg = p.EsperaMs / 1000;
            int idx = Array.IndexOf(PresetsEspera, seg);
            if (idx < 0) idx = 0;
            p.EsperaMs = PresetsEspera[(idx + dir + PresetsEspera.Length) % PresetsEspera.Length] * 1000;
            ActualizarFichas();
            Avisar();
        }

        void ElegirImagen()
        {
            try
            {
                using (var d = new OpenFileDialog { Title = "Elegí una imagen para mandar", Filter = "Imágenes|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|Todos|*.*", CheckFileExists = true })
                {
                    if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                    string destino = Guardar(d.FileName);
                    Agregar(Parte.DeImagen(destino, ""));
                }
            }
            catch (Exception ex) { MessageBox.Show("No pude abrir la imagen: " + ex.Message, "TeamsTools", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        /// <summary>Copia la imagen a datos\adjuntos\ para que el envío no dependa de dónde estaba el original.</summary>
        static string Guardar(string origen)
        {
            try
            {
                string carpeta = Path.Combine(Program.CarpetaDatos, "adjuntos");
                Directory.CreateDirectory(carpeta);
                string nombre = Guid.NewGuid().ToString("N").Substring(0, 6) + "-" + Path.GetFileName(origen);
                string destino = Path.Combine(carpeta, nombre);
                File.Copy(origen, destino, true);
                return destino;
            }
            catch { return origen; }
        }

        void CargarMiniatura(string ruta)
        {
            if (ruta == miniaturaDe) return;
            try { miniatura?.Dispose(); } catch { }
            miniatura = null; miniaturaDe = ruta;
            if (ruta.Length == 0 || !File.Exists(ruta)) return;
            try { using (var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read)) miniatura = Image.FromStream(fs); } catch { miniatura = null; }
        }

        // ------------------------------------------------------------------ fichas de las partes

        void ActualizarFichas()
        {
            foreach (var ch in fichas) { Controls.Remove(ch); ch.Dispose(); }
            fichas.Clear();
            for (int i = 0; i < partes.Count; i++)
            {
                var p = partes[i];
                string icono = p.Tipo == TipoParte.Texto ? "▤" : p.Tipo == TipoParte.Imagen ? "▣" : "▧";
                string txt = $"{i + 1} {icono} {Recorte(p)}" + (p.EsperaMs > 0 ? $" +{p.EsperaMs / 1000}s" : "");
                var ch = new Chip { Text = txt, Tipo = Chip.Modo.Toggle, Acento = p.Tipo == TipoParte.Texto ? Tema.Malva : Tema.Cyan, Activo = i == sel, Destacado = i == sel };
                int k = i;
                ch.Accion += (s, e) => Seleccionar(k);
                Controls.Add(ch); fichas.Add(ch);
                ch.BringToFront();
            }
            var a = Actual;
            chEspera.Poner("espera", a == null ? "—" : a.EsperaMs == 0 ? "sin pausa" : (a.EsperaMs / 1000) + " s");
        }

        static string Recorte(Parte p)
        {
            string s;
            if (p.Tipo == TipoParte.Texto) { s = p.Plano.Length > 0 ? p.Plano : Rico.DesdeHtml(p.Html).Plano(); s = s.Replace("\n", " "); if (s.Length == 0) s = "(vacío)"; }
            else s = p.Ruta.Length > 0 ? Path.GetFileName(p.Ruta) : "(sin archivo)";
            return s.Length > 18 ? s.Substring(0, 18) + "…" : s;
        }

        // ------------------------------------------------------------------ layout y pintado

        protected override void OnResize(EventArgs e) { base.OnResize(e); Acomodar(); }

        void Acomodar()
        {
            if (Width <= 0 || Height <= 0) return;
            float esc = Dpi.Escala(this);
            int S(int px) => Dpi.S(esc, px);
            int pad = S(10), y = S(24);

            // fila 1: fichas de las partes
            int x = pad, filaH = S(26) + S(6), ancho = Width - pad * 2;
            foreach (var ch in fichas)
            {
                ch.Ajustar();
                if (x > pad && x + ch.Width > pad + ancho) { x = pad; y += filaH; }
                ch.Location = new Point(x, y); x += ch.Width + S(6);
            }
            y += filaH;

            // fila 2: acciones
            x = pad;
            foreach (var ch in acciones)
            {
                ch.Ajustar();
                if (x > pad && x + ch.Width > pad + ancho) { x = pad; y += filaH; }
                ch.Location = new Point(x, y); x += ch.Width + S(6);
            }
            y += filaH + S(2);
            rTira = new Rectangle(pad, S(20), ancho, y - S(20));

            int top = y, alto = Math.Max(S(60), Height - top - S(8));
            rCuerpo = new Rectangle(pad, top, ancho, alto);
            if (editor.Visible) editor.SetBounds(pad, top, ancho, alto);
            if (cNota.Visible) cNota.SetBounds(pad, top, ancho, Math.Min(S(48), alto));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float esc = Dpi.Escala(this);
            int S(int px) => Dpi.S(esc, px);
            Tema.Tarjeta_(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), S(9), Tema.Tarjeta, Tema.Tarjeta);

            int imgs = partes.Count(p => p.Tipo != TipoParte.Texto);
            int txts = partes.Count - imgs;
            string cab = $"{Etiqueta.ToUpperInvariant()} · {partes.Count} parte{(partes.Count == 1 ? "" : "s")}" +
                         (imgs > 0 ? $" · {txts} texto + {imgs} adjunto" : "") +
                         (partes.Count > 1 ? " · burbujas separadas" : "");
            Tema.Texto_(g, cab, Tema.Media(7.5f), Tema.Apagado, new Rectangle(S(12), S(6), Width - S(24), S(14)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            var p2 = Actual;
            if (p2 != null && p2.Tipo != TipoParte.Texto)
            {
                // ficha de la imagen: miniatura + ruta + estado
                int topImg = rCuerpo.Top + (cNota.Visible ? cNota.Height + S(8) : 0);
                var caja = new Rectangle(rCuerpo.Left, topImg, rCuerpo.Width, Math.Max(S(40), rCuerpo.Bottom - topImg));
                Tema.Tarjeta_(g, new RectangleF(caja.X + 0.5f, caja.Y + 0.5f, caja.Width - 1, caja.Height - 1), S(8), Tema.Panel, Tema.Panel);
                bool existe = p2.Ruta.Length > 0 && File.Exists(p2.Ruta);
                if (miniatura != null && existe)
                {
                    int m = S(8);
                    var dispo = new Rectangle(caja.X + m, caja.Y + m, caja.Width - m * 2, caja.Height - m * 2 - S(16));
                    if (dispo.Width > 4 && dispo.Height > 4)
                    {
                        float k = Math.Min((float)dispo.Width / miniatura.Width, (float)dispo.Height / miniatura.Height);
                        int w = Math.Max(1, (int)(miniatura.Width * k)), h = Math.Max(1, (int)(miniatura.Height * k));
                        var dst = new Rectangle(dispo.X + (dispo.Width - w) / 2, dispo.Y, w, h);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        using (var path = Tema.Redondeado(new RectangleF(dst.X, dst.Y, dst.Width, dst.Height), S(6)))
                        {
                            var clip = g.Clip;
                            g.SetClip(path);
                            g.DrawImage(miniatura, dst);
                            g.Clip = clip;
                        }
                    }
                }
                string pie = existe
                    ? Path.GetFileName(p2.Ruta) + (miniatura != null ? $"  ·  {miniatura.Width}×{miniatura.Height}" : "") + "  ·  se pega dentro del mensaje"
                    : "falta el archivo: " + (p2.Ruta.Length > 0 ? p2.Ruta : "(sin ruta)");
                Tema.Texto_(g, pie, Tema.Fina(8.5f), existe ? Tema.Apagado : Tema.Rosa,
                    new Rectangle(caja.X + S(10), caja.Bottom - S(18), caja.Width - S(20), S(14)),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { miniatura?.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }
}
