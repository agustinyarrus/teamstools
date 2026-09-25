using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace TeamsTools
{
    /// <summary>Todo lo que las vistas necesitan del resto de la app.</summary>
    internal sealed class Contexto
    {
        public Config Cfg; public Logger Log; public ConfigRespuestas Reglas; public Recordatorios Recordatorios; public Personalizados Personalizados;
        public Contactos Contactos; public TeamsChat Chat; public Autocontestador Auto; public Programador Cron; public Observador Observador;
        public Corrillos Corrillos;               // quién está en call con quién
        public Historia Chats;                    // el historial de Teams leído del disco
        public string RutaPresencia = "", RutaCorrillos = "";
        public Action<string, string> Aviso;      // globo de bandeja
        public Action<Action> EnUi;               // BeginInvoke al hilo de la UI
    }

    /// <summary>Base de las vistas: escala, ayuda de layout y pintado de tarjetas.</summary>
    internal abstract class Pantalla : Control
    {
        protected readonly Contexto ctx;
        protected float esc = 1f;
        protected readonly List<Chip> chips = new List<Chip>();
        protected Pantalla(Contexto c)
        {
            ctx = c;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
        }
        protected int S(int px) => Dpi.S(esc, px);

        /// <summary>
        /// Gancho para pedirle a una pantalla que se ponga en cierto modo desde afuera (hoy: `--foto … --modo x`).
        /// Cada vista interpreta la palabra como quiera; la que no entiende nada, no hace nada.
        /// </summary>
        public virtual void Modo(string que) { }
        protected Chip Nuevo(string texto, Chip.Modo modo, Color acento, EventHandler accion, string sub = "")
        {
            var ch = new Chip { Text = texto, Tipo = modo, Acento = acento, Sub = sub };
            ch.Accion += accion; Controls.Add(ch); chips.Add(ch); return ch;
        }
        /// <summary>Acomoda una fila de chips de izquierda a derecha con salto de linea. Devuelve el y final.</summary>
        protected int Flujo(IEnumerable<Chip> lista, int x0, int y, int ancho)
        {
            int x = x0, gap = S(8), filaH = S(26) + S(8);
            foreach (var ch in lista)
            {
                ch.Ajustar();
                if (x > x0 && x + ch.Width > x0 + ancho) { x = x0; y += filaH; }
                ch.Location = new Point(x, y); x += ch.Width + gap;
            }
            return y + filaH;
        }
        /// <summary>
        /// Cuelga el guión flotante de un compositor: el botón «desplegar» lo abre sobre TODA la pantalla, y lo que se
        /// reordene, borre o agregue ahí vuelve al compositor porque los dos comparten la MISMA lista de partes.
        /// </summary>
        protected PanelEnvio Guion(EditorEnvio ed, Func<string> titulo, Action alCambiar)
        {
            var p = new PanelEnvio { Dock = DockStyle.Fill, Visible = false };
            Controls.Add(p);
            ed.Desplegar += (s, e) => { p.Mostrar(new Envio { Partes = ed.Partes }, titulo()); };
            p.Cambio += (s, e) => { ed.Resincronizar(); alCambiar?.Invoke(); };
            p.Editar += i => ed.SeleccionarParte(i);
            p.Agregar += t => ed.AgregarParte(t);
            return p;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); esc = Dpi.Escala(this); if (IsHandleCreated) Acomodar(); Invalidate(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); esc = Dpi.Escala(this); Acomodar(); }
        public abstract void Acomodar();
        public abstract void Refrescar();
        protected Rectangle[] Tarjetas(Rectangle area, float[] pesos)
        {
            int gap = S(12); float unidad = (area.Width - gap * (pesos.Length - 1)) / pesos.Sum();
            var res = new Rectangle[pesos.Length]; float x = area.Left;
            for (int i = 0; i < pesos.Length; i++) { int w = (int)Math.Round(unidad * pesos[i]); res[i] = new Rectangle((int)x, area.Top, w, area.Height); x += w + gap; }
            return res;
        }
        protected void Titulo(Graphics g, string t, Rectangle r) => Tema.Texto_(g, t.ToUpperInvariant(), Tema.Media(8f), Tema.Apagado, r);
        protected static string Rel(DateTime? t) => t.HasValue ? Tema.Relativo(t.Value) : "nunca";
    }

    // =====================================================================================================
    // MENSAJES: autocontestador
    // =====================================================================================================
    internal sealed class VistaMensajes : Pantalla
    {
        readonly Tabla tReglas = new Tabla { Etiqueta = "reglas · gana la primera que coincide, de arriba hacia abajo", Vacio = "sin reglas: creá una con «nueva»" };
        readonly Tabla tBandeja = new Tabla { Etiqueta = "bandeja · lo que entró y lo que contesté", Vacio = "todavía no entró nada en modo automático", AltoFila = 22 };
        readonly Barras gReglas = new Barras { Etiqueta = "reglas más usadas", Vacio = "ninguna regla contestó todavía" };
        readonly Campo cNombre = new Campo { Etiqueta = "nombre de la regla", Pista = "saludo" };
        readonly Campo cPatron = new Campo { Etiqueta = "patrón de entrada (regex)", Pista = @"^\s*(hola|buenas)\b" };
        readonly Campo cPersonas = new Campo { Etiqueta = "solo para (opcional, separado por ;)", Pista = "Rivas; Ortega" };
        readonly Campo cSenales = new Campo { Etiqueta = "señales del mensaje · + exige, − bloquea", Pista = "+urgencia +ticket −bot" };
        readonly Campo cPrueba = new Campo { Etiqueta = "probador · escribí un mensaje como si te llegara", Pista = "hola rama, podés mirar el ticket?" };
        readonly EditorEnvio editor = new EditorEnvio { Etiqueta = "respuesta · {nombre} {apellido} {hora} {fecha} {dia}" };
        Chip chModo, chGrupos, chSinRegla, chAuto, chEnfriamiento, chLectura, chRegex, chMayus, chPrivados, chEnfRegla, chActiva, chNueva, chDuplicar, chBorrar, chGuardar, chSubir, chBajar, chProbarEnvio, chActivarTodo;
        Chip chEspera, chTope, chHorario, chIA;
        static readonly int[] PresetsEspera = { 0, 3, 5, 10, 20, 30, 60, 120 };   // segundos antes de contestar
        static readonly int[] PresetsTope = { 0, 1, 2, 3, 5, 10, 20 };            // respuestas por día
        /// <summary>Franjas listas: vacía = a cualquier hora.</summary>
        static readonly string[][] PresetsHorario = { new[] { "", "" }, new[] { "09:00", "18:00" }, new[] { "08:00", "13:00" }, new[] { "13:00", "19:00" }, new[] { "19:00", "09:00" } };
        Regla actual;
        string resultadoPrueba = "";
        string deteccion = "";
        DateTime iaMirado = DateTime.MinValue;
        bool iaMirando;
        Color colorPrueba = Tema.Apagado;
        Rectangle rTarjetas, rModo, rProbador;
        static readonly int[] PresetsMin = { 0, 2, 3, 5, 10, 15, 30 };
        static readonly int[] PresetsEnf = { 0, 5, 10, 15, 30, 60, 120 };
        static readonly int[] PresetsSeg = { 3, 5, 6, 10, 15, 30 };

        public VistaMensajes(Contexto c) : base(c)
        {
            Controls.AddRange(new Control[] { tReglas, tBandeja, gReglas, cNombre, cPatron, cPersonas, cSenales, cPrueba, editor });
            tReglas.Columnas = new List<Columna>
            {
                new Columna { Titulo = "#", Peso = 0, MinAncho = 20, Derecha = true },
                new Columna { Titulo = "regla", Peso = 0.9f, MinAncho = 64 },
                new Columna { Titulo = "patrón de entrada (regex)", Peso = 1.9f, MinAncho = 90, Mono = true },
                new Columna { Titulo = "condiciones extra", Peso = 1.1f, MinAncho = 70 },
                new Columna { Titulo = "responde", Peso = 0.6f, MinAncho = 52 },
                new Columna { Titulo = "solo para", Peso = 0.6f, MinAncho = 46 },
                new Columna { Titulo = "espera", Peso = 0, MinAncho = 44, Derecha = true },
                new Columna { Titulo = "enfría", Peso = 0, MinAncho = 44, Derecha = true },
                new Columna { Titulo = "tope", Peso = 0, MinAncho = 40, Derecha = true },
                new Columna { Titulo = "envío", Peso = 0, MinAncho = 54, Derecha = true },
                new Columna { Titulo = "usos", Peso = 0, MinAncho = 36, Derecha = true },
                new Columna { Titulo = "fallos", Peso = 0, MinAncho = 38, Derecha = true },
                new Columna { Titulo = "ms", Peso = 0, MinAncho = 38, Derecha = true },
                new Columna { Titulo = "último uso", Peso = 0.55f, MinAncho = 56, Derecha = true },
            };
            tBandeja.Columnas = new List<Columna>
            {
                new Columna { Titulo = "hora", Peso = 0, MinAncho = 52 },
                new Columna { Titulo = "chat", Peso = 0.9f, MinAncho = 70 },
                new Columna { Titulo = "lo que me escribieron", Peso = 1.6f, MinAncho = 90 },
                new Columna { Titulo = "regla", Peso = 0.6f, MinAncho = 50 },
                new Columna { Titulo = "lo que contesté", Peso = 1.6f, MinAncho = 90 },
                new Columna { Titulo = "ms", Peso = 0, MinAncho = 38, Derecha = true },
            };
            chModo = Nuevo("modo automático", Chip.Modo.Toggle, Tema.Durazno, (s, e) => { ctx.Auto.PonerModoAutomatico(!ctx.Reglas.ModoAutomatico); Refrescar(); });
            chGrupos = Nuevo("responder en grupos", Chip.Modo.Toggle, Tema.Cielo, (s, e) => { ctx.Reglas.ResponderGrupos = !ctx.Reglas.ResponderGrupos; ctx.Reglas.Guardar(); Refrescar(); });
            chSinRegla = Nuevo("sin regla: respuesta por defecto", Chip.Modo.Toggle, Tema.Crema, (s, e) => { ctx.Reglas.ResponderSinRegla = !ctx.Reglas.ResponderSinRegla; ctx.Reglas.Guardar(); Refrescar(); });
            chAuto = Nuevo("prender solo tras", Chip.Modo.Valor, Tema.Malva, (s, e) => Ciclar(ref ctx.Reglas.ActivarSiInactivoMinutos, PresetsMin, +1), "");
            chAuto.AccionDerecha += (s, e) => Ciclar(ref ctx.Reglas.ActivarSiInactivoMinutos, PresetsMin, -1);
            chEnfriamiento = Nuevo("no repetir antes de", Chip.Modo.Valor, Tema.Salvia, (s, e) => Ciclar(ref ctx.Reglas.EnfriamientoGeneralMinutos, PresetsEnf, +1), "");
            chEnfriamiento.AccionDerecha += (s, e) => Ciclar(ref ctx.Reglas.EnfriamientoGeneralMinutos, PresetsEnf, -1);
            chLectura = Nuevo("miro los chats cada", Chip.Modo.Valor, Tema.Cielo, (s, e) => Ciclar(ref ctx.Reglas.SegundosEntreLecturas, PresetsSeg, +1), "");
            chLectura.AccionDerecha += (s, e) => Ciclar(ref ctx.Reglas.SegundosEntreLecturas, PresetsSeg, -1);

            try { cSenales.Pista = "+" + string.Join(" +", Micromodelos.Nombres.Take(4)) + " · disponibles: " + string.Join(" ", Micromodelos.Nombres); } catch { }
            cSenales.Cambio += (s, e) => Probar();
            chNueva = Nuevo("nueva", Chip.Modo.Boton, Tema.Malva, (s, e) => NuevaRegla());
            chDuplicar = Nuevo("duplicar", Chip.Modo.Boton, Tema.TextoSuave, (s, e) => Duplicar());
            chSubir = Nuevo("▲", Chip.Modo.Boton, Tema.TextoSuave, (s, e) => Mover(-1));
            chBajar = Nuevo("▼", Chip.Modo.Boton, Tema.TextoSuave, (s, e) => Mover(+1));
            chBorrar = Nuevo("borrar", Chip.Modo.Boton, Tema.Rosa, (s, e) => Borrar());
            chActivarTodo = Nuevo("todas activas", Chip.Modo.Boton, Tema.TextoSuave, (s, e) => { foreach (var r in ctx.Reglas.Reglas) r.Activa = true; ctx.Reglas.Guardar(); Refrescar(); });

            // 🚨 cada toggle tiene que actualizar SU PROPIO dibujo: si no, el chip se ve apagado con la regla activa y el próximo clic la apaga de verdad
            chActiva = Nuevo("activa", Chip.Modo.Toggle, Tema.Salvia, (s, e) => { if (actual != null) { actual.Activa = !actual.Activa; chActiva.Activo = actual.Activa; chActiva.Invalidate(); ctx.Log.Info($"Regla «{actual.Nombre}» {(actual.Activa ? "ACTIVADA" : "DESACTIVADA")}"); Guardar(false); } });
            chRegex = Nuevo("regex", Chip.Modo.Toggle, Tema.Cielo, (s, e) => { if (actual != null) { actual.EsRegex = !actual.EsRegex; chRegex.Activo = actual.EsRegex; chRegex.Invalidate(); Guardar(false); Probar(); } });
            chMayus = Nuevo("ignorar mayúsculas", Chip.Modo.Toggle, Tema.Cielo, (s, e) => { if (actual != null) { actual.IgnorarMayusculas = !actual.IgnorarMayusculas; chMayus.Activo = actual.IgnorarMayusculas; chMayus.Invalidate(); Guardar(false); Probar(); } });
            chPrivados = Nuevo("solo chats privados", Chip.Modo.Toggle, Tema.Crema, (s, e) => { if (actual != null) { actual.SoloPrivados = !actual.SoloPrivados; chPrivados.Activo = actual.SoloPrivados; chPrivados.Invalidate(); Guardar(false); } });
            chEnfRegla = Nuevo("enfriamiento", Chip.Modo.Valor, Tema.Salvia, (s, e) => { if (actual != null) { Ciclar(ref actual.EnfriamientoMinutos, PresetsEnf, +1); Guardar(false); } }, "");
            chEnfRegla.AccionDerecha += (s, e) => { if (actual != null) { Ciclar(ref actual.EnfriamientoMinutos, PresetsEnf, -1); Guardar(false); } };
            // --- condiciones nuevas de la regla
            chEspera = Nuevo("contesta", Chip.Modo.Valor, Tema.Durazno, (s, e) => { if (actual != null) Ciclar(ref actual.RetrasoSegundos, PresetsEspera, +1); }, "");
            chEspera.AccionDerecha += (s, e) => { if (actual != null) Ciclar(ref actual.RetrasoSegundos, PresetsEspera, -1); };
            chTope = Nuevo("tope", Chip.Modo.Valor, Tema.Rosa, (s, e) => { if (actual != null) Ciclar(ref actual.MaxPorDia, PresetsTope, +1); }, "");
            chTope.AccionDerecha += (s, e) => { if (actual != null) Ciclar(ref actual.MaxPorDia, PresetsTope, -1); };
            chHorario = Nuevo("solo entre", Chip.Modo.Valor, Tema.Cielo, (s, e) => CiclarHorario(+1), "");
            chHorario.AccionDerecha += (s, e) => CiclarHorario(-1);
            chIA = Nuevo("redactar con IA", Chip.Modo.Toggle, Tema.Malva, (s, e) =>
            {
                if (actual == null) return;
                actual.UsarIA = !actual.UsarIA; chIA.Activo = actual.UsarIA; chIA.Invalidate();
                if (actual.UsarIA) RevisarIA(true);
                else { chIA.Poner("redactar con IA", ""); Acomodar(); }
                Guardar(false);
            });
            chGuardar = Nuevo("guardar regla", Chip.Modo.Boton, Tema.Salvia, (s, e) => Guardar(true)); chGuardar.Destacado = true;
            chProbarEnvio = Nuevo("probar: mandar la respuesta a mi chat", Chip.Modo.Boton, Tema.Cielo, (s, e) => ProbarEnvio());

            Guion(editor, () => "respuesta de «" + (actual?.Nombre.Length > 0 ? actual.Nombre : "la regla") + "»", () => Guardar(false));
            tReglas.SeleccionCambio += (s, e) => Cargar(tReglas.Actual?.Tag as Regla);
            cPrueba.Cambio += (s, e) => Probar();
            cPatron.Cambio += (s, e) => Probar();
            editor.Cambio += (s, e) => Invalidate(rProbador);
        }

        /// <summary>
        /// Pregunta si el modelo local está levantado y lo muestra EN EL PROPIO INTERRUPTOR, con los segundos
        /// estimados por respuesta. La consulta sale por red, así que va a un hilo aparte y como mucho una vez cada
        /// 30 s: un toggle no puede congelar la pantalla ni fallar mudo.
        /// </summary>
        void RevisarIA(bool forzar)
        {
            if (iaMirando) return;
            if (!forzar && (DateTime.Now - iaMirado).TotalSeconds < 30) return;
            iaMirando = true;
            chIA.Poner("redactar con IA", "preguntando…"); Acomodar(); Invalidate();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool hay; string det;
                try { hay = AsistenteIA.Verificar(out det); }
                catch (Exception ex) { hay = false; det = ex.Message; }
                ctx.EnUi(() =>
                {
                    iaMirando = false; iaMirado = DateTime.Now;
                    chIA.Poner("redactar con IA", hay ? ResumenIA(det) : "sin modelo");
                    chIA.Acento = hay ? Tema.Malva : Tema.Durazno;
                    if (!hay) ctx.Log.Aviso("«redactar con IA» está marcado pero el modelo local no responde · " + det + " · mientras tanto contesta con el texto fijo");
                    else ctx.Log.Info("Modelo local a mano · " + det);
                    Acomodar(); Invalidate();
                });
            });
        }

        /// <summary>De «Qwen3.5-2B · 12,3 tok/s medidos · ~4 s por respuesta de 40 tokens» deja «Qwen3.5-2B · ~4 s».</summary>
        static string ResumenIA(string detalle)
        {
            if (string.IsNullOrEmpty(detalle)) return "listo";
            var partes = detalle.Split('·').Select(x => x.Trim()).ToList();
            string modelo = partes.Count > 0 ? partes[0] : "modelo";
            if (modelo.Length > 18) modelo = modelo.Substring(0, 18) + "…";
            var seg = partes.FirstOrDefault(x => x.StartsWith("~"));
            if (seg != null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(seg, @"~\s*(\d+)");
                if (m.Success) return modelo + " · ~" + m.Groups[1].Value + " s";
            }
            return modelo + (partes.Any(x => x.StartsWith("sin medir")) ? " · sin medir" : "");
        }

        /// <summary>El campo de señales como texto: "+urgencia +ticket −bot".</summary>
        static string TextoSenales(Regla r)
        {
            if (r == null) return "";
            var t = r.Exigidas.Select(x => "+" + x).Concat(r.Excluidas.Select(x => "−" + x));
            return string.Join(" ", t);
        }

        /// <summary>Lee ese texto de vuelta. Sin signo se entiende como exigida, que es lo que uno quiere escribir rápido.</summary>
        static void LeerSenales(Regla r, string texto)
        {
            var exige = new List<string>(); var bloquea = new List<string>();
            foreach (var tk in (texto ?? "").Split(' ', ',', ';'))
            {
                string t = tk.Trim();
                if (t.Length == 0) continue;
                if (t[0] == '-' || t[0] == '−') { if (t.Length > 1) bloquea.Add(t.Substring(1)); }
                else exige.Add(t.TrimStart('+'));
            }
            r.SenalesRequeridas = string.Join(";", exige);
            r.SenalesExcluidas = string.Join(";", bloquea);
        }

        /// <summary>Cicla la franja horaria de la regla entre las opciones listas (la primera es "a cualquier hora").</summary>
        void CiclarHorario(int dir)
        {
            if (actual == null) return;
            int idx = Array.FindIndex(PresetsHorario, p => p[0] == actual.DesdeHora && p[1] == actual.HastaHora);
            if (idx < 0) idx = 0;
            var nuevo = PresetsHorario[(idx + dir + PresetsHorario.Length) % PresetsHorario.Length];
            actual.DesdeHora = nuevo[0]; actual.HastaHora = nuevo[1];
            ctx.Reglas.Guardar(); ctx.Auto.Despertar(); Refrescar();
        }

        /// <summary>Cómo se ve el envío de una regla en la tabla: "IA", "3 burbujas", "2+1 img" o "texto".</summary>
        static string EtiquetaEnvio(Envio e, bool ia)
        {
            if (ia) return "IA";
            int img = e.Partes.Count(p => p.EsAdjunto);
            int txt = e.Partes.Count - img;
            if (e.Partes.Count <= 1) return img > 0 ? "1 img" : "texto";
            return img > 0 ? $"{txt}t+{img}i" : txt + " burb";
        }

        void Ciclar(ref int valor, int[] presets, int dir)
        {
            int idx = Array.IndexOf(presets, valor);
            if (idx < 0) { idx = 0; for (int i = 0; i < presets.Length; i++) if (presets[i] <= valor) idx = i; }
            valor = presets[(idx + dir + presets.Length) % presets.Length];
            ctx.Reglas.Guardar(); ctx.Auto.Despertar(); Refrescar();
        }

        void NuevaRegla()
        {
            var r = new Regla { Nombre = "nueva regla", Patron = "", Orden = ctx.Reglas.Reglas.Count + 1, RespuestaTexto = "Hola {nombre}! Estoy con el celular, ya te escribo.", RespuestaHtml = "Hola {nombre}! Estoy con el celular, ya te escribo." };
            ctx.Reglas.Reglas.Add(r); ctx.Reglas.Guardar();
            Refrescar(); tReglas.Seleccionar(tReglas.Filas.FindIndex(f => f.Tag == r));
            cPatron.Caja.Focus();
        }
        void Duplicar()
        {
            if (actual == null) return;
            var r = new Regla { Nombre = actual.Nombre + " (copia)", Patron = actual.Patron, EsRegex = actual.EsRegex, IgnorarMayusculas = actual.IgnorarMayusculas, RespuestaHtml = actual.RespuestaHtml, RespuestaTexto = actual.RespuestaTexto, RespuestaRtf = actual.RespuestaRtf, SoloPrivados = actual.SoloPrivados, EnfriamientoMinutos = actual.EnfriamientoMinutos, Personas = actual.Personas, Orden = actual.Orden + 1 };
            ctx.Reglas.Reglas.Add(r); ctx.Reglas.Guardar(); Refrescar(); tReglas.Seleccionar(tReglas.Filas.FindIndex(f => f.Tag == r));
        }
        void Borrar()
        {
            if (actual == null) return;
            if (!chBorrar.Armado) { chBorrar.Armado = true; chBorrar.Invalidate(); var t = new System.Windows.Forms.Timer { Interval = 4000 }; t.Tick += (s, e) => { chBorrar.Armado = false; chBorrar.Invalidate(); t.Stop(); }; t.Start(); return; }
            chBorrar.Armado = false;
            ctx.Reglas.Reglas.Remove(actual); ctx.Reglas.Guardar(); actual = null; Refrescar(); Cargar(null);
        }
        void Mover(int dir)
        {
            if (actual == null) return;
            var lista = ctx.Reglas.Reglas.OrderBy(r => r.Orden).ToList();
            int i = lista.IndexOf(actual), j = i + dir;
            if (j < 0 || j >= lista.Count) return;
            var tmp = lista[i]; lista[i] = lista[j]; lista[j] = tmp;
            for (int k = 0; k < lista.Count; k++) lista[k].Orden = k + 1;
            ctx.Reglas.Guardar(); Refrescar();
        }

        void Cargar(Regla r)
        {
            actual = r;
            bool hay = r != null;
            foreach (var c in new Control[] { cNombre, cPatron, cPersonas, cSenales }) c.Enabled = hay;
            editor.Habilitar(hay);
            cNombre.Texto = r?.Nombre ?? ""; cPatron.Texto = r?.Patron ?? ""; cPersonas.Texto = r?.Personas ?? ""; cSenales.Texto = TextoSenales(r);
            if (r != null) editor.CargarEnvio(r.RespuestaEfectiva()); else editor.Limpiar();
            chActiva.Activo = r?.Activa ?? false; chRegex.Activo = r?.EsRegex ?? true; chMayus.Activo = r?.IgnorarMayusculas ?? true; chPrivados.Activo = r?.SoloPrivados ?? true;
            chIA.Activo = r?.UsarIA ?? false;
            if (chIA.Activo) RevisarIA(false); else { chIA.Poner("redactar con IA", ""); chIA.Acento = Tema.Malva; }
            foreach (var ch in new[] { chActiva, chRegex, chMayus, chPrivados, chIA }) ch.Invalidate();
            chEnfRegla.Poner("enfriamiento", r == null ? "—" : r.EnfriamientoMinutos + " min");
            chEspera.Poner("contesta", r == null ? "—" : r.RetrasoSegundos == 0 ? "al toque" : "a los " + r.RetrasoSegundos + " s");
            chTope.Poner("tope", r == null ? "—" : r.MaxPorDia == 0 ? "sin límite" : r.UsosHoy + "/" + r.MaxPorDia + " hoy");
            chHorario.Poner("solo entre", r == null ? "—" : r.DesdeHora.Length == 0 ? "cualquier hora" : r.DesdeHora + "-" + r.HastaHora);
            // 🚨 Poner() cambia el ANCHO del chip (mide texto + valor). Sin volver a acomodar, los chips de esta fila
            //    conservan la posición vieja y se superponen entre sí, comiéndose el valor del de al lado.
            Acomodar();
            Probar();
            Invalidate();
        }

        void Guardar(bool avisar)
        {
            if (actual == null) return;
            actual.Nombre = cNombre.Texto.Trim(); actual.Patron = cPatron.Texto; actual.Personas = cPersonas.Texto.Trim();
            LeerSenales(actual, cSenales.Texto);
            actual.Respuesta = editor.Envio;
            actual.SincronizarCamposViejos();   // los campos viejos siguen con la primera burbuja de texto
            ctx.Reglas.Guardar();
            if (avisar) ctx.Log.Ok($"Regla «{actual.Nombre}» guardada" + (actual.Error.Length > 0 ? " · OJO: regex inválida: " + actual.Error : ""));
            Refrescar();
        }

        /// <summary>La persona con la que el probador simula que llegó el mensaje (los marcadores {nombre} y {apellido} se rellenan con ella).</summary>
        internal const string PersonaDePrueba = "Rivas, Valentina";

        /// <summary>«probar:texto» escribe un mensaje en el probador (para fotografiar la respuesta sin el mouse).</summary>
        public override void Modo(string que)
        {
            if ((que ?? "").StartsWith("probar:", StringComparison.OrdinalIgnoreCase))
            {
                if (actual == null && ctx.Reglas.Reglas.Count > 0) Cargar(ctx.Reglas.Reglas.OrderBy(r => r.Orden).First());   // el probador necesita una regla
                cPrueba.Texto = que.Substring(7); Probar();
            }
            else if (string.IsNullOrEmpty(que)) { cPrueba.Texto = ""; Probar(); }
        }

        void Probar()
        {
            if (actual == null) { resultadoPrueba = "elegí o creá una regla"; colorPrueba = Tema.Apagado; Invalidate(rProbador); return; }
            var tmp = new Regla { Patron = cPatron.Texto, EsRegex = chRegex.Activo, IgnorarMayusculas = chMayus.Activo };
            LeerSenales(tmp, cSenales.Texto);
            tmp.DesdeHora = actual.DesdeHora; tmp.HastaHora = actual.HastaHora; tmp.Dias = actual.Dias;
            ISet<string> vistas = null;
            deteccion = "";
            try
            {
                var sen = Micromodelos.Analizar(cPrueba.Texto);
                if (sen != null && cPrueba.Texto.Trim().Length > 0)
                {
                    vistas = sen.Etiquetas;
                    // ordenadas por fuerza: la línea se corta a lo ancho, así que lo importante tiene que ir primero
                    var fuertes = Micromodelos.Detectar(cPrueba.Texto).Where(x => x.Prendida)
                        .OrderByDescending(x => x.Puntaje)
                        .Select(x => $"{x.Modelo} {(int)Math.Round(x.Puntaje * 100)}")
                        .Take(6).ToList();
                    deteccion = fuertes.Count == 0
                        ? "los micromodelos no ven ninguna señal en ese mensaje"
                        : $"prioridad {sen.Prioridad}/100 · " + string.Join(" · ", fuertes) + (sen.Ticket.Length > 0 ? " · " + sen.Ticket : "");
                }
            }
            catch { }
            string err = tmp.Error;
            string texto = cPrueba.Texto;
            if (err.Length > 0) { resultadoPrueba = "regex inválida: " + err; colorPrueba = Tema.Rosa; }
            else if (texto.Length == 0) { resultadoPrueba = "escribí un mensaje de prueba arriba"; colorPrueba = Tema.Apagado; }
            else
            {
                var m = tmp.Buscar(texto);
                if (m.Success)
                {
                    // el patrón es solo la primera condición: puede estar fuera de horario, sin cupo o pidiendo señales
                    string freno = ConfigRespuestas.PorQueNo(tmp, texto, PersonaDePrueba, false, vistas, DateTime.Now);
                    if (freno != null && freno != "el patrón no coincide")
                    {
                        resultadoPrueba = $"el patrón coincide «{m.Value}» PERO no contestaría: {freno}";
                        colorPrueba = Tema.Durazno;
                    }
                    else
                    {
                        resultadoPrueba = $"coincide «{m.Value}» en la posición {m.Index} · contestaría: " + editor.Envio.Rellenar(t => ConfigRespuestas.Rellenar(t, PersonaDePrueba, ctx.Chat.MiNombre)).Resumen(90);
                        colorPrueba = Tema.Salvia;
                    }
                }
                else
                {
                    string porQue;
                    var (otra, resp) = ctx.Auto.Probar(texto, PersonaDePrueba, out porQue);
                    resultadoPrueba = "esta regla NO coincide" + (otra != null
                        ? $" · la que contestaría es «{otra.Nombre}»: {(resp.Length > 70 ? resp.Substring(0, 70) + "…" : resp)}"
                        : " · ninguna otra tampoco" + (porQue.Length > 0 ? " → " + porQue : ""));
                    colorPrueba = Tema.Durazno;
                }
            }
            Invalidate(rProbador);
        }

        void ProbarEnvio()
        {
            if (actual == null) return;
            Guardar(false);
            var envio = editor.Envio;
            ctx.Log.Info($"Prueba: mando la respuesta de la regla a mi propio chat… ({envio.Resumen(90)})");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var r = ctx.Cron.EnviarA(ctx.Chat.MiNombre, ctx.Chat.MiCorreo, envio);
                if (r.Ok) ctx.Log.Ok("Prueba enviada a mi chat: " + r.Detalle); else ctx.Log.Error("La prueba falló: " + r.Detalle);
            });
        }

        public override void Refrescar()
        {
            var R = ctx.Reglas;
            chModo.Activo = ctx.Auto.Encendido; chGrupos.Activo = R.ResponderGrupos; chSinRegla.Activo = R.ResponderSinRegla;
            chAuto.Poner("prender solo tras", R.ActivarSiInactivoMinutos == 0 ? "nunca" : R.ActivarSiInactivoMinutos + " min quieto");
            chEnfriamiento.Poner("no repetir antes de", R.EnfriamientoGeneralMinutos + " min");
            chLectura.Poner("miro los chats cada", R.SegundosEntreLecturas + " s");
            int n = 0;
            var filas = R.Reglas.OrderBy(r => r.Orden).Select(r =>
            {
                var env = r.RespuestaEfectiva();
                string cond = r.ResumenCondiciones();
                var f = FilaTabla.F(r, r.Error.Length > 0 ? Tema.Rosa : r.Activa ? Tema.Salvia : Tema.MuyApagado,
                    Celda.C((++n).ToString(), Tema.MuyApagado),
                    Celda.C(r.Nombre.Length > 0 ? r.Nombre : "(sin nombre)", null, true),
                    Celda.C(r.Error.Length > 0 ? "✗ " + r.Error : (r.EsRegex ? "" : "texto· ") + (r.Patron.Length > 0 ? r.Patron : "(sin patrón)"), r.Error.Length > 0 ? Tema.Rosa : Tema.TextoSuave),
                    Celda.C(cond.Length > 0 ? cond : "—", cond.Length > 0 ? Tema.Cielo : Tema.MuyApagado),
                    Celda.C(r.SoloPrivados ? "privados" : "y grupos", r.SoloPrivados ? Tema.TextoSuave : Tema.Cielo),
                    Celda.C(r.Personas.Length > 0 ? r.Personas : "todos", r.Personas.Length > 0 ? Tema.Malva : Tema.Apagado),
                    Celda.C(r.RetrasoSegundos > 0 ? r.RetrasoSegundos + " s" : "—", r.RetrasoSegundos > 0 ? Tema.Durazno : Tema.MuyApagado),
                    Celda.C(r.EnfriamientoMinutos + " min", Tema.TextoSuave),
                    Celda.C(r.MaxPorDia > 0 ? r.UsosHoy + "/" + r.MaxPorDia : "—", r.MaxPorDia > 0 ? (r.TieneCupoHoy() ? Tema.Salvia : Tema.Rosa) : Tema.MuyApagado),
                    Celda.C(EtiquetaEnvio(env, r.UsarIA), r.UsarIA ? Tema.Malva : env.Partes.Count > 1 ? Tema.Cielo : Tema.Apagado),
                    Celda.C(r.Usos.ToString(), r.Usos > 0 ? Tema.Salvia : Tema.Apagado, r.Usos > 0),
                    Celda.C(r.Fallos > 0 ? r.Fallos.ToString() : "—", r.Fallos > 0 ? Tema.Rosa : Tema.MuyApagado),
                    Celda.C(r.MsPromedio > 0 ? r.MsPromedio.ToString() : "—", Tema.Apagado),
                    Celda.C(r.UltimoUso.HasValue ? Rel(r.UltimoUso) : "sin uso", Tema.Apagado));
                f.Apagada = !r.Activa;
                return f;
            }).ToList();
            tReglas.Poner(filas, actual, $"{R.Reglas.Count(x => x.Activa)}/{R.Reglas.Count} activas · {R.Reglas.Sum(x => x.Usos)} respuestas" + (R.ResponderSinRegla ? " · + por defecto" : ""));

            var band = ctx.Auto.Ultimos().Reverse().Take(200).Select(en =>
            {
                var f = FilaTabla.F(en, en.Respondido ? Tema.Salvia : Tema.Durazno,
                    Celda.C(en.Hora.ToString("HH:mm:ss"), Tema.Apagado),
                    Celda.C(en.Chat, null, true),
                    Celda.C(en.Texto.Replace("\n", " ⏎ "), Tema.TextoSuave),
                    Celda.C(en.Regla.Length > 0 ? en.Regla : "—", en.Regla.Length > 0 ? Tema.Malva : Tema.Apagado),
                    Celda.C(en.Respondido ? en.Respuesta.Replace("\n", " ⏎ ") : "sin respuesta · " + en.Resultado, en.Respondido ? Tema.Texto : Tema.Durazno),
                    Celda.C(en.Ms > 0 ? en.Ms.ToString() : "—", Tema.Apagado));
                if (en.Tipo != "privado") f.Celdas[1].Insignia = en.Tipo;
                f.Celdas[1].ColorInsignia = Tema.Cielo;
                return f;
            }).ToList();
            var ult = ctx.Auto.Ultimos();
            tBandeja.Poner(band, null, ult.Length > 0 ? $"{ult.Count(x => x.Respondido)} contestados de {ult.Length}" : "");
            var colores = new[] { Tema.Malva, Tema.Cyan, Tema.Salvia, Tema.Durazno, Tema.Cielo, Tema.Rosa };
            var top = R.Reglas.Where(x => x.Usos > 0).OrderByDescending(x => x.Usos).Take(6).ToList();
            gReglas.Poner(top.Select((x, i) => new Barra { Etiqueta = x.Nombre, Valor = x.Usos, Color = colores[i % colores.Length] }).ToList(),
                R.Reglas.Sum(x => x.Usos) > 0 ? R.Reglas.Sum(x => x.Usos) + " respuestas" : "");
            Acomodar();
            Invalidate();
        }

        public override void Acomodar()
        {
            int pad = S(4), w = Width - pad * 2, botH = S(34);
            rTarjetas = new Rectangle(pad, S(4), w, S(84));
            int y = rTarjetas.Bottom + S(10);
            y = Flujo(new[] { chModo, chGrupos, chSinRegla, chAuto, chEnfriamiento, chLectura }, pad, y, w);
            rModo = new Rectangle(pad, rTarjetas.Bottom + S(6), w, y - rTarjetas.Bottom);
            int alto = Height - y - S(4);

            // --- arriba, a lo ancho: la TABLA de reglas con todo el detalle + su botonera
            int altoTabla = Math.Max(S(140), (int)(alto * 0.34));
            int yBotTabla = y + altoTabla - botH;
            tReglas.SetBounds(pad, y, w, yBotTabla - y - S(8));
            Flujo(new[] { chNueva, chDuplicar, chSubir, chBajar, chBorrar, chActivarTodo }, pad, yBotTabla, w);

            // --- abajo: editor de la regla (izq) | bandeja + gráfico (der)
            int yAbajo = y + altoTabla + S(10), altoAbajo = alto - altoTabla - S(10);
            int izqW = (int)(w * 0.52), derX = pad + izqW + S(12), derW = w - izqW - S(12);
            int yBot = yAbajo + altoAbajo - botH;
            int yy = yAbajo;
            cNombre.SetBounds(pad, yy, (int)(izqW * 0.44) - S(6), S(48));
            cPersonas.SetBounds(pad + (int)(izqW * 0.44) + S(6), yy, izqW - (int)(izqW * 0.44) - S(6), S(48));
            yy += S(52);
            cPatron.SetBounds(pad, yy, izqW, S(48)); yy += S(52);
            cSenales.SetBounds(pad, yy, izqW, S(48)); yy += S(52);
            yy = Flujo(new[] { chActiva, chRegex, chMayus, chPrivados, chEnfRegla, chEspera, chTope, chHorario, chIA }, pad, yy, izqW);
            cPrueba.SetBounds(pad, yy, izqW, S(48)); yy += S(50);
            rProbador = new Rectangle(pad, yy, izqW, S(30)); yy += S(33);
            int hEditor = Math.Max(S(70), yBot - yy - S(8));
            editor.SetBounds(pad, yy, izqW, hEditor);
            Flujo(new[] { chGuardar, chProbarEnvio }, pad, Math.Max(yBot, yy + hEditor + S(6)), izqW);   // nunca encima del editor
            int altoGraf = Math.Max(S(90), (int)(altoAbajo * 0.34));
            tBandeja.SetBounds(derX, yAbajo, derW, altoAbajo - altoGraf - S(12));
            gReglas.SetBounds(derX, yAbajo + altoAbajo - altoGraf, derW, altoGraf);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Tema.Fondo);
            var A = ctx.Auto; var R = ctx.Reglas;
            var rects = Tarjetas(rTarjetas, new[] { 1.2f, 0.9f, 0.9f, 0.9f, 1.3f });
            bool on = A.Activo;
            Tema.TarjetaDato(g, rects[0], esc, "MODO AUTOMÁTICO", on ? "encendido" : "apagado", on ? (A.ActivadoPorInactividad ? "se prendió solo por inactividad" : "manual") + (A.ActivoDesde != null ? " · desde las " + A.ActivoDesde.Value.ToString("HH:mm") : "") : R.ActivarSiInactivoMinutos > 0 ? $"se prende solo a los {R.ActivarSiInactivoMinutos} min quieto" : "prendelo y contesto los chats por vos", on ? Tema.Durazno : Tema.MuyApagado, Tema.Fina(17f));
            Tema.TarjetaDato(g, rects[1], esc, "CHATS SIN LEER", A.UltimaLectura != null ? A.ChatsNoLeidos.ToString() : "—", A.UltimaLectura != null ? $"{A.Estado} · {Rel(A.UltimaLectura)} · {A.UltimaLecturaMs} ms" : A.Estado, A.ChatsNoLeidos > 0 ? Tema.Durazno : Tema.Cyan);
            Tema.TarjetaDato(g, rects[2], esc, "RESPUESTAS HOY", A.RespuestasHoy.ToString(), $"{A.Lecturas} lecturas · {A.Errores} errores", Tema.Salvia);
            Tema.TarjetaDato(g, rects[3], esc, "REGLAS", $"{R.Reglas.Count(r => r.Activa)}/{R.Reglas.Count}", R.Reglas.Any(r => r.Error.Length > 0) ? "hay regex inválidas" : R.ResponderSinRegla ? "+ respuesta por defecto" : "solo si alguna coincide", R.Reglas.Any(r => r.Error.Length > 0) ? Tema.Rosa : Tema.Malva);
            var top = R.Reglas.OrderByDescending(r => r.Usos).FirstOrDefault(r => r.Usos > 0);
            Tema.TarjetaDato(g, rects[4], esc, "LA QUE MÁS CONTESTA", top != null ? top.Nombre : "—", top != null ? $"{top.Usos} veces · última {Rel(top.UltimoUso)}" : "todavía ninguna contestó", Tema.Cielo, Tema.Fina(15f));
            Tema.Texto_(g, resultadoPrueba, Tema.Fina(9f), colorPrueba, new Rectangle(rProbador.X, rProbador.Y, rProbador.Width, S(15)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (deteccion.Length > 0)
                Tema.Texto_(g, deteccion, Tema.Fina(8.5f), Tema.Cielo, new Rectangle(rProbador.X, rProbador.Y + S(15), rProbador.Width, S(15)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    // =====================================================================================================
    // CRON: recordatorios
    // =====================================================================================================
    internal sealed class VistaCron : Pantalla
    {
        readonly Tabla lista = new Tabla { Etiqueta = "recordatorios · el cron dispara el que le toque", Vacio = "sin recordatorios: creá uno a la derecha", AltoFila = 19 };
        readonly Tabla tHistorial = new Tabla { Etiqueta = "historial de disparos · qué salió y qué falló", Vacio = "todavía no disparó ninguno", AltoFila = 18 };
        readonly Campo cPara = new Campo { Etiqueta = "para (apodo, nombre o correo)", Pista = "Vale · Rivas · bruno.ortega@…" };
        readonly Campo cCuando = new Campo { Etiqueta = "cuándo", Pista = "mañana 10:00 · en 45 min · lunes 9:30 · cada laborable 9:15 · cada lun,mie 10:00 · 17/09 15:00" };
        readonly EditorEnvio editor = new EditorEnvio { Etiqueta = "mensaje · {nombre} se reemplaza por el nombre de pila" };
        readonly List<Chip> sugerencias = new List<Chip>();
        readonly Segmentado segRepite = new Segmentado { Etiqueta = "repetición", Acento = Tema.Cielo, Opciones = new[] { "una vez", "diario", "lun-vie", "semanal", "cada N", "del mes", "último" } };
        readonly Escalon escCada = new Escalon { Etiqueta = "cada", Sufijo = " días", Min = 1, Max = 60, Valor = 3, Acento = Tema.Malva };
        Chip chNuevo, chGuardar, chBorrar, chAhora, chParaMi, chActivo, chPausarTodo, chProbarCuando;
        Recordatorio actual;
        string lecturaCuando = "";
        Color colorCuando = Tema.Apagado;
        Contacto elegido;
        Rectangle rTarjetas, rLectura, rSugerencias, rSemana, rProximos;

        public VistaCron(Contexto c) : base(c)
        {
            Controls.AddRange(new Control[] { lista, tHistorial, cPara, cCuando, editor, segRepite, escCada });
            lista.Columnas = new List<Columna>
            {
                new Columna { Titulo = "#", Peso = 0, MinAncho = 20, Derecha = true },
                new Columna { Titulo = "para", Peso = 0.9f, MinAncho = 70 },
                new Columna { Titulo = "qué dice", Peso = 1.8f, MinAncho = 100 },
                new Columna { Titulo = "cuándo lo escribiste", Peso = 1.1f, MinAncho = 76 },
                new Columna { Titulo = "repite", Peso = 0.9f, MinAncho = 62 },
                new Columna { Titulo = "próximo", Peso = 0.9f, MinAncho = 70 },
                new Columna { Titulo = "en", Peso = 0, MinAncho = 64, Derecha = true },
                new Columna { Titulo = "envíos", Peso = 0, MinAncho = 40, Derecha = true },
                new Columna { Titulo = "fallos", Peso = 0, MinAncho = 38, Derecha = true },
                new Columna { Titulo = "sale bien", Peso = 0, MinAncho = 50, Derecha = true },
                new Columna { Titulo = "último resultado", Peso = 1.2f, MinAncho = 80 },
            };
            tHistorial.Columnas = new List<Columna>
            {
                new Columna { Titulo = "cuándo", Peso = 0, MinAncho = 78 },
                new Columna { Titulo = "para", Peso = 0.9f, MinAncho = 70 },
                new Columna { Titulo = "qué decía", Peso = 1.4f, MinAncho = 90 },
                new Columna { Titulo = "salió", Peso = 0, MinAncho = 44 },
                new Columna { Titulo = "ms", Peso = 0, MinAncho = 40, Derecha = true },
                new Columna { Titulo = "detalle", Peso = 2.0f, MinAncho = 110 },
            };
            segRepite.Cambio += (s, e) => { AplicarRepeticion(); };
            escCada.Cambio += (s, e) => { if (segRepite.Elegido == 4 || segRepite.Elegido == 5) AplicarRepeticion(); };
            chNuevo = Nuevo("nuevo recordatorio", Chip.Modo.Boton, Tema.Malva, (s, e) => NuevoRec());
            chGuardar = Nuevo("guardar", Chip.Modo.Boton, Tema.Salvia, (s, e) => Guardar(true)); chGuardar.Destacado = true;
            chAhora = Nuevo("mandar ahora", Chip.Modo.Boton, Tema.Cielo, (s, e) => MandarAhora());
            chBorrar = Nuevo("borrar", Chip.Modo.Boton, Tema.Rosa, (s, e) => Borrar());
            chParaMi = Nuevo("para mí (aviso propio)", Chip.Modo.Toggle, Tema.Crema, (s, e) => { chParaMi.Activo = !chParaMi.Activo; chParaMi.Invalidate(); });
            chActivo = Nuevo("activo", Chip.Modo.Toggle, Tema.Salvia, (s, e) => { if (actual != null) { actual.Activo = !actual.Activo; chActivo.Activo = actual.Activo; chActivo.Invalidate(); if (actual.Activo && actual.Proximo != null && actual.Proximo < DateTime.Now && actual.EsRecurrente) actual.Proximo = Agenda.Proxima(DateTime.Now, actual.Repetir, actual.Hora); ctx.Recordatorios.Guardar(); Refrescar(); } });
            chPausarTodo = Nuevo("pausar todos", Chip.Modo.Boton, Tema.TextoSuave, (s, e) => { foreach (var r in ctx.Recordatorios.Lista) r.Activo = false; ctx.Recordatorios.Guardar(); Refrescar(); });
            chProbarCuando = Nuevo("¿cuándo caería?", Chip.Modo.Boton, Tema.Cielo, (s, e) => { LeerCuando(); Invalidate(); });
            Guion(editor, () => "recordatorio para «" + (actual?.ParaMi == true ? "mí" : actual?.Para ?? "") + "»", () => Guardar(false));
            lista.SeleccionCambio += (s, e) => Cargar(lista.Actual?.Tag as Recordatorio);
            cCuando.Cambio += (s, e) => LeerCuando();
            cPara.Cambio += (s, e) => Sugerir();
        }

        void Sugerir()
        {
            foreach (var ch in sugerencias) { Controls.Remove(ch); ch.Dispose(); }
            sugerencias.Clear();
            elegido = null;
            string q = cPara.Texto.Trim();
            if (q.Length == 0) { Invalidate(rSugerencias); return; }
            var cands = ctx.Contactos.Buscar(q).Take(5).ToList();
            var exacto = cands.FirstOrDefault(x => Contactos.Normalizar(x.Nombre) == Contactos.Normalizar(q) || Contactos.Normalizar(x.Correo) == Contactos.Normalizar(q));
            if (exacto != null) elegido = exacto;
            foreach (var cnd in cands)
            {
                var ch = new Chip { Text = $"{cnd.Nombre}{(cnd.Apodo.Length > 0 ? " · " + cnd.Apodo : "")}", Tipo = Chip.Modo.Boton, Acento = Tema.Malva, Destacado = cnd == exacto };
                var cc = cnd;
                ch.Accion += (s, e) => { elegido = cc; cPara.Texto = cc.Nombre; Sugerir(); };
                Controls.Add(ch); sugerencias.Add(ch);
            }
            Flujo(sugerencias, rSugerencias.Left, rSugerencias.Top, rSugerencias.Width);
            Invalidate(rSugerencias);
        }

        /// <summary>El segmentado y el escalón escriben la repetición del recordatorio y recalculan el próximo.</summary>
        void AplicarRepeticion()
        {
            if (actual == null) return;
            string hora = actual.Hora.Length > 0 ? actual.Hora : (actual.Proximo?.ToString("HH:mm") ?? "09:00");
            switch (segRepite.Elegido)
            {
                case 0: actual.Repetir = ""; break;
                case 1: actual.Repetir = "diario"; break;
                case 2: actual.Repetir = "laborables"; break;
                case 3: actual.Repetir = actual.Repetir.StartsWith("semanal:") ? actual.Repetir : "semanal:" + (int)DateTime.Today.DayOfWeek; break;
                case 4: actual.Repetir = "cada:" + Math.Max(1, escCada.Valor); break;
                case 5: actual.Repetir = "mensual:" + Math.Max(1, Math.Min(31, escCada.Valor)); break;
                case 6: actual.Repetir = "ultimo:" + (actual.Proximo?.DayOfWeek is DayOfWeek dw ? (int)dw : 5); break;
            }
            actual.Hora = actual.Repetir.Length > 0 ? hora : "";
            if (actual.Repetir.Length > 0) actual.Proximo = Agenda.Proxima(DateTime.Now, actual.Repetir, actual.Hora);
            actual.Cuando = Agenda.Leer(actual.Repetir, actual.Hora);
            cCuando.Texto = actual.Cuando;
            escCada.Etiqueta = segRepite.Elegido == 5 ? "el día" : "cada";
            escCada.Sufijo = segRepite.Elegido == 5 ? " de cada mes" : " días";
            escCada.Enabled = segRepite.Elegido == 4 || segRepite.Elegido == 5;
            ctx.Recordatorios.Guardar(); ctx.Cron.Despertar();
            LeerCuando(); Refrescar();
        }

        /// <summary>Pone el segmentado y el escalón según lo que ya tiene el recordatorio.</summary>
        void ReflejarRepeticion(Recordatorio r)
        {
            string rep = r?.Repetir ?? "";
            int i = rep.Length == 0 ? 0 : rep == "diario" ? 1 : rep == "laborables" ? 2
                  : rep.StartsWith("semanal:") ? 3 : rep.StartsWith("cada:") ? 4
                  : rep.StartsWith("mensual:") ? 5 : rep.StartsWith("ultimo:") ? 6 : 0;
            segRepite.Poner(i);
            int v = 3;
            if (rep.StartsWith("cada:")) int.TryParse(rep.Substring(5), out v);
            else if (rep.StartsWith("mensual:")) int.TryParse(rep.Substring(8), out v);
            escCada.Poner(Math.Max(1, v));
            escCada.Etiqueta = i == 5 ? "el día" : "cada";
            escCada.Sufijo = i == 5 ? " de cada mes" : " días";
            escCada.Enabled = i == 4 || i == 5;
        }

        void LeerCuando()
        {
            var r = Agenda.Parsear(cCuando.Texto, DateTime.Now);
            if (r.Error.Length > 0 && r.Proximo == null) { lecturaCuando = r.Error; colorCuando = Tema.Rosa; }
            else if (r.Proximo == null) { lecturaCuando = "sin fecha"; colorCuando = Tema.Apagado; }
            else { lecturaCuando = $"→ {r.Proximo.Value.ToString("dddd d/MM HH:mm", Agenda.Es)} · {r.Lectura} · {Tema.Relativo(r.Proximo.Value)}" + (r.Error.Length > 0 ? " · " + r.Error : ""); colorCuando = r.Error.Length > 0 ? Tema.Durazno : Tema.Salvia; }
            Invalidate(rLectura);
        }

        void NuevoRec()
        {
            var r = new Recordatorio { Para = "", Cuando = "mañana 10:00", TextoPlano = "", TextoHtml = "" };
            var p = Agenda.Parsear(r.Cuando, DateTime.Now); r.Proximo = p.Proximo; r.Repetir = p.Repetir; r.Hora = p.Hora;
            ctx.Recordatorios.Lista.Add(r); ctx.Recordatorios.Guardar();
            Refrescar(); lista.Seleccionar(lista.Filas.FindIndex(f => f.Tag == r)); cPara.Caja.Focus();
        }

        void Cargar(Recordatorio r)
        {
            actual = r;
            bool hay = r != null;
            foreach (var c in new Control[] { cPara, cCuando }) c.Enabled = hay;
            editor.Habilitar(hay);
            cPara.Texto = r?.Para ?? ""; cCuando.Texto = r?.Cuando ?? "";
            chParaMi.Activo = r?.ParaMi ?? false; chActivo.Activo = r?.Activo ?? false;
            chParaMi.Invalidate(); chActivo.Invalidate();
            if (r != null) editor.CargarEnvio(r.MensajeEfectivo()); else editor.Limpiar();
            ReflejarRepeticion(r);
            foreach (var c in new Control[] { segRepite, escCada }) c.Enabled = hay;
            Sugerir(); LeerCuando();
            Acomodar();
            Invalidate();
        }

        bool Guardar(bool avisar)
        {
            if (actual == null) return false;
            var p = Agenda.Parsear(cCuando.Texto, DateTime.Now);
            if (p.Proximo == null) { ctx.Log.Aviso("Recordatorio: no entiendo el cuándo · " + p.Error); LeerCuando(); return false; }
            actual.ParaMi = chParaMi.Activo;
            actual.Para = chParaMi.Activo ? "yo" : (elegido?.Nombre ?? cPara.Texto.Trim());
            actual.Correo = chParaMi.Activo ? ctx.Chat.MiCorreo : (elegido?.Correo ?? (cPara.Texto.Contains("@") ? cPara.Texto.Trim() : ctx.Contactos.Mejor(cPara.Texto)?.Correo ?? ""));
            actual.Cuando = cCuando.Texto.Trim(); actual.Proximo = p.Proximo; actual.Repetir = p.Repetir; actual.Hora = p.Hora;
            actual.Mensaje = editor.Envio;
            actual.SincronizarCamposViejos();
            if (!actual.ParaMi && actual.Para.Length == 0) { ctx.Log.Aviso("Recordatorio: falta a quién"); return false; }
            if (actual.Mensaje.Vacio && !actual.Mensaje.TieneAdjuntos) { ctx.Log.Aviso("Recordatorio: falta el mensaje"); return false; }
            actual.Activo = true; chActivo.Activo = true;
            ctx.Recordatorios.Guardar(); ctx.Cron.Despertar();
            if (avisar) ctx.Log.Ok($"Recordatorio guardado: «{(actual.ParaMi ? "para mí" : actual.Para)}» · {actual.ResumenCuando()}");
            Refrescar();
            return true;
        }

        void MandarAhora()
        {
            if (actual == null || !Guardar(false)) return;
            var r = actual;
            ctx.Log.Info($"Mandando ahora a «{r.Para}»…");
            ThreadPool.QueueUserWorkItem(_ => { ctx.Cron.Ejecutar(r, true); ctx.EnUi(Refrescar); });
        }

        void Borrar()
        {
            if (actual == null) return;
            if (!chBorrar.Armado) { chBorrar.Armado = true; chBorrar.Invalidate(); var t = new System.Windows.Forms.Timer { Interval = 4000 }; t.Tick += (s, e) => { chBorrar.Armado = false; chBorrar.Invalidate(); t.Stop(); }; t.Start(); return; }
            chBorrar.Armado = false;
            ctx.Recordatorios.Lista.Remove(actual); ctx.Recordatorios.Guardar(); actual = null; Refrescar(); Cargar(null);
        }

        public override void Refrescar()
        {
            int n = 0;
            var filas = ctx.Recordatorios.Lista.OrderBy(r => !r.Activo).ThenBy(r => r.Proximo ?? DateTime.MaxValue).Select(r =>
            {
                int pc = r.PorcentajeOk();
                var f = FilaTabla.F(r, !r.Activo ? Tema.MuyApagado : r.Fallos > 0 && r.UltimoResultado.StartsWith("falló") ? Tema.Rosa : r.EsRecurrente ? Tema.Cielo : Tema.Malva,
                    Celda.C((++n).ToString(), Tema.MuyApagado),
                    Celda.C(r.ParaMi ? "para mí" : r.Para.Length > 0 ? r.Para : "(sin destinatario)", r.ParaMi ? Tema.Crema : null, true),
                    Celda.C(r.MensajeEfectivo().Partes.Count > 0 ? r.MensajeEfectivo().Resumen(90) : "(sin mensaje)", Tema.TextoSuave),
                    Celda.C(r.Cuando.Length > 0 ? r.Cuando : "—", Tema.Apagado),
                    Celda.C(r.EsRecurrente ? Agenda.Leer(r.Repetir, "") : "una vez", r.EsRecurrente ? Tema.Cielo : Tema.Apagado),
                    Celda.C(r.Proximo.HasValue ? r.Proximo.Value.ToString("ddd dd/MM HH:mm", Agenda.Es) : "sin fecha", r.Activo ? null : Tema.MuyApagado),
                    Celda.C(!r.Activo ? "pausado" : r.Proximo.HasValue ? Tema.Relativo(r.Proximo.Value) : "—", !r.Activo ? Tema.Durazno : Tema.Apagado),
                    Celda.C(r.Enviados.ToString(), r.Enviados > 0 ? Tema.Salvia : Tema.Apagado, r.Enviados > 0),
                    Celda.C(r.Fallos > 0 ? r.Fallos.ToString() : "—", r.Fallos > 0 ? Tema.Rosa : Tema.MuyApagado),
                    Celda.C(pc < 0 ? "—" : pc + " %", pc < 0 ? Tema.MuyApagado : pc >= 90 ? Tema.Salvia : pc >= 60 ? Tema.Durazno : Tema.Rosa),
                    Celda.C(r.UltimoResultado.Length > 0 ? r.UltimoResultado : "sin disparar", r.UltimoResultado.StartsWith("falló") ? Tema.Rosa : Tema.Apagado));
                f.Apagada = !r.Activo;
                if (r.EsRecurrente) { f.Celdas[1].Insignia = "cron"; f.Celdas[1].ColorInsignia = Tema.Cielo; }
                return f;
            }).ToList();
            var act = ctx.Recordatorios.Lista.Count(r => r.Activo);
            lista.Poner(filas, actual, $"{act}/{ctx.Recordatorios.Lista.Count} activos · {ctx.Recordatorios.Lista.Sum(r => r.Enviados)} enviados · {ctx.Recordatorios.Lista.Sum(r => r.Fallos)} fallos");

            // historial: todos los disparos de todos los recordatorios, el mas nuevo arriba
            var disparos = ctx.Recordatorios.Lista
                .SelectMany(r => r.Historial.Select(d => new { R = r, D = d }))
                .OrderByDescending(x => x.D.Cuando).Take(200)
                .Select(x => FilaTabla.F(x.R, x.D.Ok ? Tema.Salvia : Tema.Rosa,
                    Celda.C(x.D.Cuando.ToString("dd/MM HH:mm:ss"), Tema.Apagado),
                    Celda.C(x.R.ParaMi ? "para mí" : x.R.Para, null, true),
                    Celda.C(x.R.MensajeEfectivo().Resumen(70), Tema.TextoSuave),
                    Celda.C(x.D.Ok ? "sí" : "no", x.D.Ok ? Tema.Salvia : Tema.Rosa, true),
                    Celda.C(x.D.Ms > 0 ? x.D.Ms.ToString() : "—", Tema.Apagado),
                    Celda.C(x.D.Detalle.Length > 0 ? x.D.Detalle : "—", x.D.Ok ? Tema.Apagado : Tema.Durazno)))
                .ToList();
            int malos = ctx.Recordatorios.Lista.SelectMany(r => r.Historial).Count(d => !d.Ok);
            tHistorial.Poner(disparos, null, disparos.Count > 0 ? $"{disparos.Count} disparos · {malos} con problema" : "");
            Acomodar(); Invalidate();
        }

        public override void Acomodar()
        {
            int pad = S(4), w = Width - pad * 2, botH = S(34);
            rTarjetas = new Rectangle(pad, S(4), w, S(84));
            // --- la semana, a lo ancho: el calendario con los recordatorios ubicados por hora
            // 🚨 el calendario CRECE cuando hay pocos recordatorios. Al revés quedaba un hueco muerto abajo
            //    (tabla vacía + historial vacío) mientras la semana, que siempre tiene algo que mostrar, iba apretada.
            int nRec = ctx.Recordatorios.Lista.Count;
            int extraSemana = nRec == 0 ? S(100) : nRec <= 3 ? S(70) : 0;
            rSemana = new Rectangle(pad, rTarjetas.Bottom + S(8), w, S(118) + extraSemana);

            int y = rSemana.Bottom + S(8);
            int alto = Height - y - S(4);
            // el historial solo ocupa lugar si hay algo que mostrar: con la agenda recién creada sobraba
            // un bloque vacío abajo mientras el calendario quedaba apretado arriba.
            bool hayHistorial = ctx.Recordatorios.Lista.Any(r => r.Historial.Count > 0);
            tHistorial.Visible = hayHistorial;
            int altoHist = hayHistorial ? Math.Max(S(96), (int)(alto * 0.26)) : 0;
            int altoMedio = alto - altoHist - (hayHistorial ? S(10) : 0);

            int izqW = (int)(w * 0.56), derX = pad + izqW + S(12), derW = w - izqW - S(12);
            int yBot = y + altoMedio - botH;
            lista.SetBounds(pad, y, izqW, yBot - y - S(6));
            Flujo(new[] { chNuevo, chPausarTodo }, pad, yBot, izqW);

            int yy = y;
            cPara.SetBounds(derX, yy, derW, S(50)); yy += S(53);
            rSugerencias = new Rectangle(derX, yy, derW, S(28)); yy += S(31);
            Flujo(sugerencias, rSugerencias.Left, rSugerencias.Top, rSugerencias.Width);
            cCuando.SetBounds(derX, yy, derW, S(50)); yy += S(53);
            rLectura = new Rectangle(derX, yy, derW, S(18)); yy += S(21);
            // repetición con palancas en vez de escribirla a mano
            int anchoSeg = (int)(derW * 0.60);
            segRepite.SetBounds(derX, yy, anchoSeg, S(40));
            escCada.SetBounds(derX + anchoSeg + S(8), yy, derW - anchoSeg - S(8), S(40));
            yy += S(44);
            yy = Flujo(new[] { chParaMi, chActivo, chProbarCuando }, derX, yy, derW);
            // los próximos disparos calculados: el "qué pasa si" antes de guardar
            rProximos = new Rectangle(derX, yy, derW, S(30)); yy += S(33);
            editor.SetBounds(derX, yy, derW, Math.Max(S(80), yBot - yy - S(6)));
            Flujo(new[] { chGuardar, chAhora, chBorrar }, derX, yBot, derW);

            if (hayHistorial) tHistorial.SetBounds(pad, y + altoMedio + S(10), w, altoHist);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Tema.Fondo);
            var L = ctx.Recordatorios; var C = ctx.Cron;
            var prox = L.Proximos().FirstOrDefault();
            var rects = Tarjetas(rTarjetas, new[] { 0.8f, 1.6f, 0.8f, 0.8f, 1.0f });
            Tema.TarjetaDato(g, rects[0], esc, "PROGRAMADOS", L.Lista.Count(r => r.Activo && r.Proximo != null).ToString(), $"{L.Lista.Count(r => r.EsRecurrente && r.Activo)} repetitivos · {L.Lista.Count(r => !r.Activo)} pausados", Tema.Malva);
            Tema.TarjetaDato(g, rects[1], esc, "PRÓXIMO", prox != null ? $"{(prox.ParaMi ? "para mí" : prox.Para)} · {prox.ResumenCuando()}" : "—", prox != null ? Tema.Relativo(prox.Proximo.Value) + " · " + (prox.TextoPlano.Length > 60 ? prox.TextoPlano.Substring(0, 60) + "…" : prox.TextoPlano).Replace("\n", " ") : "nada en agenda", Tema.Cielo, Tema.Fina(14f));
            Tema.TarjetaDato(g, rects[2], esc, "ENVIADOS", L.Lista.Sum(r => r.Enviados).ToString(), C.UltimaEjecucion != null ? "último " + Rel(C.UltimaEjecucion) : "todavía ninguno", Tema.Salvia);
            Tema.TarjetaDato(g, rects[3], esc, "FALLOS", L.Lista.Sum(r => r.Fallos).ToString(), L.Lista.Sum(r => r.Fallos) > 0 ? "reintenta 3 veces cada 5 min" : "sin problemas", L.Lista.Sum(r => r.Fallos) > 0 ? Tema.Rosa : Tema.MuyApagado);
            Tema.TarjetaDato(g, rects[4], esc, "TEAMS", ctx.Chat.Hwnd != IntPtr.Zero && Win32.IsWindow(ctx.Chat.Hwnd) ? "chat listo" : ctx.Chat.TeamsCorriendo ? "en la bandeja" : "cerrado", ctx.Chat.Hwnd != IntPtr.Zero ? "ventana minimizada, escribo sin mostrarla" : "lo despierto solo cuando haga falta", ctx.Chat.Hwnd != IntPtr.Zero ? Tema.Cyan : Tema.Crema, Tema.Fina(15f));
            PintarSemana(g, rSemana);
            Tema.Texto_(g, lecturaCuando, Tema.Fina(9f), colorCuando, rLectura, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            PintarProximos(g, rProximos);
        }

        /// <summary>
        /// Calendario de la semana: 7 columnas de día y, dentro de cada una, cada recordatorio ubicado por su HORA
        /// (de 6 a 22). Se ven los huecos y los choques, que es justo lo que una lista no muestra.
        /// </summary>
        void PintarSemana(Graphics g, Rectangle r)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            Tema.Tarjeta_(g, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(6), Tema.Panel, Tema.Panel);
            Titulo(g, "la semana · cada recordatorio en su hora", new Rectangle(r.X + S(12), r.Y + S(5), S(300), S(12)));

            const int h0 = 6, h1 = 22;
            // 🚨 el titulo y los nombres de dia necesitan bandas SEPARADAS: compartiendo la misma fila, el titulo
            //    se superponia con el primer dia y se leian las dos cosas encimadas.
            int yDias = r.Y + S(19);
            int top = r.Y + S(33), bot = r.Bottom - S(6);
            int x0 = r.X + S(34), ancho = r.Width - S(44), wDia = ancho / 7;
            var hoy = DateTime.Today;

            // rejilla de horas al costado
            for (int h = h0; h <= h1; h += 4)
            {
                int y = top + (int)((bot - top) * (h - h0) / (float)(h1 - h0));
                using (var pen = new Pen(Tema.Alpha(Tema.TextoSuave, 22), 1f)) g.DrawLine(pen, x0, y, x0 + ancho, y);
                Tema.Texto_(g, h.ToString("00"), Tema.Fina(7.5f), Tema.MuyApagado, new Rectangle(r.X + S(8), y - S(6), S(24), S(12)));
            }

            for (int d = 0; d < 7; d++)
            {
                var dia = hoy.AddDays(d);
                bool esHoy = d == 0, finde = dia.DayOfWeek == DayOfWeek.Saturday || dia.DayOfWeek == DayOfWeek.Sunday;
                int xd = x0 + d * wDia;
                if (finde) using (var b = new SolidBrush(Tema.Alpha(Tema.TextoSuave, 10))) g.FillRectangle(b, xd, top, wDia - S(2), bot - top);
                Tema.Texto_(g, esHoy ? "hoy" : dia.ToString("ddd d", Agenda.Es), Tema.Fina(8f), esHoy ? Tema.Texto : Tema.Apagado,
                    new Rectangle(xd, yDias, wDia - S(4), S(12)));

                // todos los disparos de ese día, proyectando las repeticiones
                var delDia = new List<Tuple<DateTime, Recordatorio>>();
                foreach (var rec in ctx.Recordatorios.Lista.Where(x => x.Activo))
                    foreach (var t in rec.Proximas(12))
                        if (t.Date == dia) delDia.Add(Tuple.Create(t, rec));

                foreach (var it in delDia.OrderBy(x => x.Item1))
                {
                    double hh = Math.Max(h0, Math.Min(h1, it.Item1.Hour + it.Item1.Minute / 60.0));
                    int y = top + (int)((bot - top) * (hh - h0) / (h1 - h0));
                    var col = it.Item2.ParaMi ? Tema.Crema : it.Item2.EsRecurrente ? Tema.Cielo : Tema.Malva;
                    var caja = new Rectangle(xd + S(1), y - S(6), wDia - S(5), S(12));
                    Tema.Tarjeta_(g, new RectangleF(caja.X + 0.5f, caja.Y + 0.5f, caja.Width - 1, caja.Height - 1), S(2), Tema.Mezcla(Tema.Panel, col, 0.20f), Tema.Alpha(col, 120));
                    using (var b = new SolidBrush(col)) g.FillRectangle(b, caja.X, caja.Y, S(2), caja.Height);
                    string et = it.Item1.ToString("HH:mm") + " " + (it.Item2.ParaMi ? "yo" : Corto2(it.Item2.Para));
                    Tema.Texto_(g, et, Tema.Fina(7.5f), Tema.Texto, new Rectangle(caja.X + S(5), caja.Y, caja.Width - S(6), caja.Height),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
                if (delDia.Count == 0 && !finde)
                    Tema.Texto_(g, "—", Tema.Fina(8f), Tema.MuyApagado, new Rectangle(xd, (top + bot) / 2 - S(7), wDia - S(4), S(14)), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        static string Corto2(string s)
        {
            if (string.IsNullOrEmpty(s)) return "—";
            int c = s.IndexOf(',');
            return c > 0 ? s.Substring(0, c) : s;
        }

        /// <summary>Los próximos disparos del recordatorio que se está editando: el "qué pasa si" antes de guardar.</summary>
        void PintarProximos(Graphics g, Rectangle r)
        {
            if (r.Width <= 0) return;
            Tema.Tarjeta_(g, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(4), Tema.Tarjeta, Tema.Tarjeta);
            if (actual == null)
            {
                Tema.Texto_(g, "elegí un recordatorio para ver cuándo caería", Tema.Fina(8.5f), Tema.Apagado, new Rectangle(r.X + S(8), r.Y, r.Width - S(16), r.Height));
                return;
            }
            var ps = actual.Proximas(5);
            if (ps.Count == 0)
            {
                Tema.Texto_(g, "no cae en ninguna fecha con lo que está escrito", Tema.Fina(8.5f), Tema.Durazno, new Rectangle(r.X + S(8), r.Y, r.Width - S(16), r.Height));
                return;
            }
            Tema.Texto_(g, "los próximos " + ps.Count + ":", Tema.Media(7.5f), Tema.Apagado, new Rectangle(r.X + S(8), r.Y + S(2), S(70), S(12)));
            string linea = string.Join("   ", ps.Select(t => t.ToString("ddd d/MM HH:mm", Agenda.Es)));
            Tema.Texto_(g, linea, Tema.Fina(8.5f), Tema.Cielo, new Rectangle(r.X + S(8), r.Y + S(13), r.Width - S(16), S(14)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        /// <summary>Tira de 7 días con un punto por recordatorio: la agenda de la semana de un vistazo.</summary>
        void PintarAgenda(Graphics g, Rectangle r)
        {
            Tema.Tarjeta_(g, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(10), Tema.Panel, Tema.Panel);
            Titulo(g, "agenda · próximos 7 días", new Rectangle(r.X + S(14), r.Y + S(8), S(200), S(14)));
            int n = 7, x0 = r.X + S(14), wDia = (r.Width - S(28)) / n;
            var hoy = DateTime.Today;
            for (int d = 0; d < n; d++)
            {
                var dia = hoy.AddDays(d);
                var rd = new Rectangle(x0 + d * wDia, r.Y + S(24), wDia - S(6), r.Height - S(30));
                bool esHoy = d == 0;
                string nombre = (esHoy ? "hoy" : dia.ToString("ddd d", Agenda.Es));
                Tema.Texto_(g, nombre, Tema.Fina(8.5f), esHoy ? Tema.Texto : Tema.Apagado, new Rectangle(rd.X, rd.Y, rd.Width, S(14)));
                var delDia = ctx.Recordatorios.Lista.Where(x => x.Activo && x.Proximo != null && x.Proximo.Value.Date == dia).OrderBy(x => x.Proximo).ToList();
                // recurrentes: proyectar si les toca ese dia
                foreach (var rr in ctx.Recordatorios.Lista.Where(x => x.Activo && x.EsRecurrente && x.Proximo != null && x.Proximo.Value.Date != dia && x.Proximo.Value.Date < dia))
                {
                    var p = Agenda.Proxima(dia.AddSeconds(-1), rr.Repetir, rr.Hora);
                    if (p != null && p.Value.Date == dia && !delDia.Contains(rr)) delDia.Add(rr);
                }
                int px = rd.X, py = rd.Y + S(18);
                foreach (var it in delDia.Take(6))
                {
                    int dd = S(8);
                    using (var b = new SolidBrush(it.EsRecurrente ? Tema.Cielo : Tema.Malva)) g.FillEllipse(b, px, py, dd, dd);
                    px += dd + S(4);
                }
                if (delDia.Count > 0) Tema.Texto_(g, delDia.Count == 1 ? $"{delDia[0].Proximo:HH:mm} {(delDia[0].ParaMi ? "yo" : delDia[0].Para.Split(',')[0])}" : $"{delDia.Count} envíos", Tema.Fina(8f), Tema.TextoSuave, new Rectangle(rd.X, py + S(10), rd.Width, S(14)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                if (d > 0) using (var p = new Pen(Tema.BordeSuave)) g.DrawLine(p, rd.X - S(3), rd.Y, rd.X - S(3), rd.Bottom);
            }
        }
    }

    // =====================================================================================================
    // PERSONALIZADOS: mensajes propios + contactos con presencia
    // =====================================================================================================
    internal sealed class VistaPersonalizados : Pantalla
    {
        readonly Tabla lista = new Tabla { Etiqueta = "mis mensajes · listos para mandar con un clic", Vacio = "creá tu primer mensaje", AltoFila = 19 };
        readonly Tabla lContactos = new Tabla { Etiqueta = "el equipo · presencia en vivo leída de Teams", Vacio = "sin contactos", AltoFila = 19 };
        readonly Tabla lMovimientos = new Tabla { Etiqueta = "movimientos · quién se conecta y se desconecta", Vacio = "todavía no vi ningún cambio de estado", AltoFila = 19 };
        readonly Campo cNombre = new Campo { Etiqueta = "nombre", Pista = "llego tarde" };
        readonly Campo cEtiqueta = new Campo { Etiqueta = "etiqueta", Pista = "cortesía · trabajo" };
        readonly Campo cPara = new Campo { Etiqueta = "para (opcional)", Pista = "apodo, nombre o correo" };
        readonly EditorEnvio editor = new EditorEnvio { Etiqueta = "mensaje con formato" };
        readonly Campo cApodo = new Campo { Etiqueta = "apodo", Pista = "Vale" };
        readonly Campo cNombreC = new Campo { Etiqueta = "nombre como en Teams", Pista = "Rivas, Valentina" };
        readonly Campo cCorreo = new Campo { Etiqueta = "correo", Pista = "nombre.apellido@empresa.com" };
        readonly Campo cRol = new Campo { Etiqueta = "rol", Pista = "DevOps" };
        readonly Burbuja vistaPrevia = new Burbuja { Etiqueta = "vista previa · así le llega" };
        readonly Ficha fCuriosidades = new Ficha { Etiqueta = "curiosidades del equipo", Acento = Tema.Crema, Vacio = "todavía no leí la presencia" };
        readonly Barras gPresencia = new Barras { Etiqueta = "presencia del equipo ahora", Vacio = "tocá «leer presencia ahora»" };
        readonly Barras gChats = new Barras { Etiqueta = "tus chats de Teams", Vacio = "sin lectura todavía" };
        readonly List<Chip> sugerencias = new List<Chip>();
        Chip chNuevo, chGuardar, chBorrar, chMandar, chNuevoC, chGuardarC, chBorrarC, chLeerPresencia;
        Personalizado actual; Contacto actualC; Contacto elegido;
        Rectangle rTarjetas, rSugerencias;

        public VistaPersonalizados(Contexto c) : base(c)
        {
            Controls.AddRange(new Control[] { lista, lContactos, lMovimientos, cNombre, cEtiqueta, cPara, editor, cApodo, cNombreC, cCorreo, cRol, vistaPrevia, gPresencia, gChats, fCuriosidades });
            editor.Cambio += (s, e) => ActualizarVistaPrevia();
            lista.Columnas = new List<Columna>
            {
                new Columna { Titulo = "#", Peso = 0, MinAncho = 22, Derecha = true },
                new Columna { Titulo = "mensaje", Peso = 0.9f, MinAncho = 90 },
                new Columna { Titulo = "etiqueta", Peso = 0, MinAncho = 66 },
                new Columna { Titulo = "para", Peso = 0.7f, MinAncho = 70 },
                new Columna { Titulo = "qué dice", Peso = 2.6f, MinAncho = 140 },
                new Columna { Titulo = "car.", Peso = 0, MinAncho = 38, Derecha = true },
                new Columna { Titulo = "form.", Peso = 0, MinAncho = 42, Derecha = true },
                new Columna { Titulo = "usos", Peso = 0, MinAncho = 36, Derecha = true },
                new Columna { Titulo = "último uso", Peso = 0.5f, MinAncho = 58, Derecha = true },
            };
            lContactos.Columnas = new List<Columna>
            {
                new Columna { Titulo = "persona", Peso = 1.1f, MinAncho = 96 },
                new Columna { Titulo = "apodo", Peso = 0, MinAncho = 58 },
                new Columna { Titulo = "estado ahora", Peso = 0.8f, MinAncho = 76 },
                new Columna { Titulo = "hace", Peso = 0, MinAncho = 48, Derecha = true },
                new Columna { Titulo = "cambios", Peso = 0, MinAncho = 50, Derecha = true },
                new Columna { Titulo = "disponible", Peso = 0, MinAncho = 60, Derecha = true },
                new Columna { Titulo = "correo", Peso = 1.6f, MinAncho = 130 },
                new Columna { Titulo = "rol", Peso = 0.8f, MinAncho = 64 },
            };
            lMovimientos.Columnas = new List<Columna>
            {
                new Columna { Titulo = "hora", Peso = 0, MinAncho = 52 },
                new Columna { Titulo = "persona", Peso = 1f, MinAncho = 86 },
                new Columna { Titulo = "antes", Peso = 0.7f, MinAncho = 62 },
                new Columna { Titulo = "ahora", Peso = 0.7f, MinAncho = 62 },
                new Columna { Titulo = "qué pasó", Peso = 0.9f, MinAncho = 74 },
                new Columna { Titulo = "hace", Peso = 0, MinAncho = 52, Derecha = true },
            };
            chNuevo = Nuevo("nuevo mensaje", Chip.Modo.Boton, Tema.Malva, (s, e) => NuevoMsg());
            chGuardar = Nuevo("guardar", Chip.Modo.Boton, Tema.Salvia, (s, e) => Guardar(true)); chGuardar.Destacado = true;
            chMandar = Nuevo("mandar ahora", Chip.Modo.Boton, Tema.Cielo, (s, e) => Mandar()); chMandar.Destacado = true;
            chBorrar = Nuevo("borrar", Chip.Modo.Boton, Tema.Rosa, (s, e) => Borrar());
            chNuevoC = Nuevo("nuevo contacto", Chip.Modo.Boton, Tema.Malva, (s, e) => { actualC = new Contacto(); ctx.Contactos.Lista.Add(actualC); ctx.Contactos.Guardar(); Refrescar(); lContactos.Seleccionar(lContactos.Filas.FindIndex(f => f.Tag == actualC)); cApodo.Caja.Focus(); });
            chGuardarC = Nuevo("guardar contacto", Chip.Modo.Boton, Tema.Salvia, (s, e) => GuardarC());
            chBorrarC = Nuevo("borrar contacto", Chip.Modo.Boton, Tema.Rosa, (s, e) => { if (actualC != null) { ctx.Contactos.Lista.Remove(actualC); ctx.Contactos.Guardar(); actualC = null; CargarC(null); Refrescar(); } });
            chLeerPresencia = Nuevo("leer presencia ahora", Chip.Modo.Boton, Tema.Cyan, (s, e) => ThreadPool.QueueUserWorkItem(_ =>
            {
                var l = ctx.Observador != null ? ctx.Observador.LeerAhora() : (ctx.Chat.BuscarVentana() ? ctx.Chat.ListarChats() : new List<ChatItem>());
                if (l.Count > 0) ctx.EnUi(() => { presencias = l; Refrescar(); });
                else ctx.Log.Aviso("No pude leer la presencia: Teams está cerrado o no montó su ventana");
            }));
            Guion(editor, () => "mensaje «" + (actual?.Nombre.Length > 0 ? actual.Nombre : "sin nombre") + "»", () => { Guardar(false); ActualizarVistaPrevia(); });
            lista.SeleccionCambio += (s, e) => Cargar(lista.Actual?.Tag as Personalizado);
            lContactos.SeleccionCambio += (s, e) => CargarC(lContactos.Actual?.Tag as Contacto);
            lContactos.DobleClic += (s, e) => { if (lContactos.Actual?.Tag is Contacto k) { cPara.Texto = k.Nombre; elegido = k; Sugerir(); } };
            cPara.Cambio += (s, e) => Sugerir();
        }

        List<ChatItem> presencias = new List<ChatItem>();
        public void PonerPresencias(List<ChatItem> l) { presencias = l; Refrescar(); }

        void Sugerir()
        {
            foreach (var ch in sugerencias) { Controls.Remove(ch); ch.Dispose(); }
            sugerencias.Clear(); elegido = null;
            string q = cPara.Texto.Trim();
            if (q.Length == 0) { Invalidate(rSugerencias); return; }
            var cands = ctx.Contactos.Buscar(q).Take(5).ToList();
            var exacto = cands.FirstOrDefault(x => Contactos.Normalizar(x.Nombre) == Contactos.Normalizar(q));
            if (exacto != null) elegido = exacto;
            foreach (var cnd in cands)
            {
                var ch = new Chip { Text = $"{cnd.Nombre}{(cnd.Apodo.Length > 0 ? " · " + cnd.Apodo : "")}", Tipo = Chip.Modo.Boton, Acento = Tema.Malva, Destacado = cnd == exacto };
                var cc = cnd;
                ch.Accion += (s, e) => { elegido = cc; cPara.Texto = cc.Nombre; Sugerir(); };
                Controls.Add(ch); sugerencias.Add(ch);
            }
            Flujo(sugerencias, rSugerencias.Left, rSugerencias.Top, rSugerencias.Width);
            Invalidate(rSugerencias);
        }

        void NuevoMsg()
        {
            var p = new Personalizado { Nombre = "nuevo mensaje" };
            ctx.Personalizados.Lista.Add(p); ctx.Personalizados.Guardar();
            Refrescar(); lista.Seleccionar(lista.Filas.FindIndex(f => f.Tag == p)); cNombre.Caja.Focus();
        }
        void Cargar(Personalizado p)
        {
            actual = p; bool hay = p != null;
            foreach (var c in new Control[] { cNombre, cEtiqueta, cPara }) c.Enabled = hay;
            editor.Habilitar(hay);
            cNombre.Texto = p?.Nombre ?? ""; cEtiqueta.Texto = p?.Etiqueta ?? ""; cPara.Texto = p?.Para ?? "";
            if (p != null) editor.CargarEnvio(p.MensajeEfectivo()); else editor.Limpiar();
            Sugerir(); ActualizarVistaPrevia(); Invalidate();
        }

        void ActualizarVistaPrevia()
        {
            string dest = elegido?.Nombre ?? cPara.Texto.Trim();
            var rico = editor.RicoSeleccionado.Rellenar(t => ConfigRespuestas.Rellenar(t, dest.Length > 0 ? dest : VistaMensajes.PersonaDePrueba, ctx.Chat.MiNombre));
            int chars = rico.Plano().Length;
            var env = editor.Envio;
            string partes = env.Partes.Count > 1 ? $"  ·  {env.Partes.Count} burbujas" + (env.TieneAdjuntos ? " con adjunto" : "") : "";
            vistaPrevia.Poner(rico, ctx.Contactos.PorNombreTeams(ctx.Chat.MiNombre)?.NombrePila ?? "vos",
                (dest.Length > 0 ? "→ " + dest : "elegí a quién") + (chars > 0 ? $"  ·  {chars} caracteres · {rico.Lineas.Count} línea/s" + (rico.TieneFormato ? " · con formato" : "") : "") + partes);
        }
        bool Guardar(bool avisar)
        {
            if (actual == null) return false;
            actual.Nombre = cNombre.Texto.Trim(); actual.Etiqueta = cEtiqueta.Texto.Trim(); actual.Para = elegido?.Nombre ?? cPara.Texto.Trim();
            actual.Mensaje = editor.Envio; actual.SincronizarCamposViejos();
            ctx.Personalizados.Guardar();
            if (avisar) ctx.Log.Ok($"Mensaje «{actual.Nombre}» guardado");
            Refrescar(); return true;
        }
        void Mandar()
        {
            if (actual == null || !Guardar(false)) return;
            string para = actual.Para;
            if (para.Length == 0) { ctx.Log.Aviso("Escribí a quién (o hacé doble clic en un contacto)"); return; }
            var p = actual;
            var contacto = elegido ?? ctx.Contactos.Mejor(para);
            string nombre = contacto?.Nombre ?? para, correo = contacto?.Correo ?? (para.Contains("@") ? para : "");
            ctx.Log.Info($"Mandando «{p.Nombre}» a «{nombre}»…");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var r = ctx.Cron.EnviarA(nombre, correo, p.MensajeEfectivo());
                if (r.Ok) { p.Usos++; p.UltimoUso = DateTime.Now; ctx.Personalizados.Guardar(); ctx.Log.Ok($"«{p.Nombre}» enviado a «{nombre}» · {r.Detalle}"); ctx.Aviso("Mensaje enviado", $"{p.Nombre} → {nombre}"); }
                else ctx.Log.Error($"No pude mandar «{p.Nombre}» a «{nombre}»: {r.Detalle}");
                ctx.EnUi(Refrescar);
            });
        }
        void Borrar()
        {
            if (actual == null) return;
            if (!chBorrar.Armado) { chBorrar.Armado = true; chBorrar.Invalidate(); var t = new System.Windows.Forms.Timer { Interval = 4000 }; t.Tick += (s, e) => { chBorrar.Armado = false; chBorrar.Invalidate(); t.Stop(); }; t.Start(); return; }
            chBorrar.Armado = false;
            ctx.Personalizados.Lista.Remove(actual); ctx.Personalizados.Guardar(); actual = null; Refrescar(); Cargar(null);
        }
        void CargarC(Contacto k)
        {
            actualC = k; bool hay = k != null;
            foreach (var c in new Control[] { cApodo, cNombreC, cCorreo, cRol }) c.Enabled = hay;
            cApodo.Texto = k?.Apodo ?? ""; cNombreC.Texto = k?.Nombre ?? ""; cCorreo.Texto = k?.Correo ?? ""; cRol.Texto = k?.Rol ?? "";
        }
        void GuardarC()
        {
            if (actualC == null) return;
            actualC.Apodo = cApodo.Texto.Trim(); actualC.Nombre = cNombreC.Texto.Trim(); actualC.Correo = cCorreo.Texto.Trim(); actualC.Rol = cRol.Texto.Trim();
            ctx.Contactos.Guardar(); ctx.Log.Ok($"Contacto «{actualC.Nombre}» guardado"); Refrescar();
        }

        static string Corto(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m" : $"{Math.Max(1, (int)t.TotalSeconds)}s";

        static Color ColorPresencia(string p)
        {
            var n = Contactos.Normalizar(p);
            if (n.StartsWith("disponible") || n.StartsWith("available")) return Tema.Salvia;
            if (n.StartsWith("ausente") || n.StartsWith("away") || n.Contains("vuelvo") || n.Contains("right back")) return Tema.Crema;
            if (n.StartsWith("ocupado") || n.StartsWith("busy") || n.Contains("no molestar") || n.Contains("llamada") || n.Contains("present")) return Tema.Rosa;
            if (n.Length == 0) return Tema.MuyApagado;
            return Tema.Apagado;
        }

        public override void Refrescar()
        {
            int n = 0;
            var filas = ctx.Personalizados.Lista.Select(p =>
            {
                var env = p.MensajeEfectivo();
                bool varias = env.Partes.Count > 1;
                var rico = Rico.DesdeHtml(p.TextoHtml.Length > 0 ? p.TextoHtml : System.Net.WebUtility.HtmlEncode(p.TextoPlano));
                return FilaTabla.F(p, p.Usos > 0 ? Tema.Salvia : Tema.Malva,
                    Celda.C((++n).ToString(), Tema.MuyApagado),
                    Celda.C(p.Nombre.Length > 0 ? p.Nombre : "(sin nombre)", null, true),
                    Celda.C(p.Etiqueta.Length > 0 ? p.Etiqueta : "—", p.Etiqueta.Length > 0 ? Tema.Crema : Tema.MuyApagado),
                    Celda.C(p.Para.Length > 0 ? p.Para : "a quien elijas", p.Para.Length > 0 ? Tema.Cielo : Tema.Apagado),
                    Celda.C(varias ? env.Resumen(120) : p.TextoPlano.Replace("\n", " ⏎ "), varias ? Tema.Cielo : Tema.TextoSuave),
                    Celda.C(varias ? env.Partes.Count + " p." : p.TextoPlano.Length.ToString(), Tema.Apagado),
                    Celda.C(rico.TieneFormato ? "sí" : "—", rico.TieneFormato ? Tema.Malva : Tema.MuyApagado),
                    Celda.C(p.Usos.ToString(), p.Usos > 0 ? Tema.Salvia : Tema.Apagado, p.Usos > 0),
                    Celda.C(p.UltimoUso.HasValue ? Rel(p.UltimoUso) : "sin uso", Tema.Apagado));
            }).ToList();
            var etiquetas = ctx.Personalizados.Lista.Where(p => p.Etiqueta.Length > 0).Select(p => p.Etiqueta).Distinct().Count();
            lista.Poner(filas, actual, $"{ctx.Personalizados.Lista.Count} mensajes · {etiquetas} etiquetas · {ctx.Personalizados.Lista.Sum(p => p.Usos)} enviados");

            var pres = new Dictionary<string, ChatItem>();
            foreach (var x in presencias.Where(x => x.Tipo == "privado" || x.Tipo == "yo")) { string k = Contactos.Normalizar(x.Nombre); if (!pres.ContainsKey(k)) pres[k] = x; }
            var reg = ctx.Observador?.Registro;
            var fc = ctx.Contactos.Lista.Select(k =>
            {
                pres.TryGetValue(Contactos.Normalizar(k.Nombre), out var ci);
                var ep = reg?.De(k.Nombre);
                string est = ci?.Presencia ?? ep?.Presencia ?? "";
                var col = ColorPresencia(est);
                var f = FilaTabla.F(k, col,
                    Celda.C(k.Nombre, null, Estados.EsDisponible(est)),
                    Celda.C(k.Apodo.Length > 0 ? k.Apodo : "—", k.Apodo.Length > 0 ? Tema.Malva : Tema.MuyApagado),
                    Celda.C(est.Length > 0 ? est : "sin leer aún", est.Length > 0 ? col : Tema.MuyApagado, Estados.EsDisponible(est)),
                    Celda.C(ep != null && est.Length > 0 ? Corto(DateTime.Now - ep.Desde) : "—", Tema.Apagado),
                    Celda.C(ep != null && ep.Cambios > 0 ? ep.Cambios.ToString() : "—", ep != null && ep.Cambios > 0 ? Tema.Cielo : Tema.MuyApagado),
                    Celda.C(ep != null && ep.Disponible.TotalSeconds > 30 ? Corto(ep.Disponible) : "—", Tema.Salvia),
                    Celda.C(k.Correo.Length > 0 ? k.Correo : "sin correo", k.Correo.Length > 0 ? Tema.TextoSuave : Tema.MuyApagado),
                    Celda.C(k.Rol.Length > 0 ? k.Rol : "—", Tema.Apagado));
                if (ci != null && ci.NoLeido) { f.Celdas[0].Insignia = "sin leer"; f.Celdas[0].ColorInsignia = Tema.Durazno; }
                f.Apagada = est.Length == 0 || Estados.EsDesconectado(est);
                return f;
            }).OrderBy(f => f.Apagada).ThenBy(f => f.Celdas[0].Texto).ToList();
            int conectados = fc.Count(f => !f.Apagada);
            lContactos.Poner(fc, actualC, $"{conectados} conectados de {ctx.Contactos.Lista.Count}");

            var movs = reg?.Movimientos() ?? new Movimiento[0];
            var fm = movs.Reverse().Take(200).Select(m => FilaTabla.F(m,
                m.SeConecto ? Tema.Salvia : m.SeDesconecto ? Tema.MuyApagado : ColorPresencia(m.Hasta),
                Celda.C(m.Hora.ToString("HH:mm:ss"), Tema.Apagado),
                Celda.C(m.Persona, null, true),
                Celda.C(m.Desde.Length > 0 ? m.Desde : "—", Tema.Apagado),
                Celda.C(m.Hasta, ColorPresencia(m.Hasta), true),
                Celda.C(m.SeConecto ? "se conectó" : m.SeDesconecto ? "se desconectó" : "cambió de estado",
                        m.SeConecto ? Tema.Salvia : m.SeDesconecto ? Tema.Apagado : Tema.Cielo),
                Celda.C(Tema.Relativo(m.Hora), Tema.Apagado))).ToList();
            lMovimientos.Poner(fm, null, movs.Length > 0 ? $"{movs.Count(m => m.SeConecto)} conexiones · {movs.Count(m => m.SeDesconecto)} desconexiones" : "");
            // graficos
            var privados = presencias.Where(x => x.Tipo == "privado").ToList();
            int Cuenta(params string[] pref) => privados.Count(x => pref.Any(p => Contactos.Normalizar(x.Presencia).StartsWith(p)));
            gPresencia.Poner(new List<Barra>
            {
                new Barra { Etiqueta = "disponible", Valor = Cuenta("disponible", "available"), Color = Tema.Salvia },
                new Barra { Etiqueta = "ausente", Valor = Cuenta("ausente", "away", "vuelvo"), Color = Tema.Crema },
                new Barra { Etiqueta = "ocupado / no molestar", Valor = Cuenta("ocupado", "busy", "no molestar", "en una llamada", "presentando"), Color = Tema.Rosa },
                new Barra { Etiqueta = "sin conexión", Valor = Cuenta("sin conexion", "offline"), Color = Tema.MuyApagado },
            }, privados.Count > 0 ? privados.Count + " contactos" : "");
            gChats.Poner(new List<Barra>
            {
                new Barra { Etiqueta = "privados", Valor = presencias.Count(x => x.Tipo == "privado"), Color = Tema.Cyan },
                new Barra { Etiqueta = "grupos", Valor = presencias.Count(x => x.Tipo == "grupo"), Color = Tema.Malva },
                new Barra { Etiqueta = "reuniones", Valor = presencias.Count(x => x.Tipo == "reunion"), Color = Tema.Cielo },
                new Barra { Etiqueta = "canales", Valor = presencias.Count(x => x.Tipo == "canal"), Color = Tema.Salvia },
                new Barra { Etiqueta = "sin leer", Valor = presencias.Count(x => x.NoLeido), Color = Tema.Durazno },
            }, presencias.Count > 0 ? presencias.Count + " en la lista" : "");
            // curiosidades: quién madruga, quién está más disponible, quién se mueve más
            var g = reg?.Gente() ?? new EstadoPersona[0];
            var conMov = g.Where(p => p.Cambios > 0 || p.Disponible.TotalSeconds > 0).ToList();
            var madrugador = g.Where(p => p.PrimeroDisponible != null).OrderBy(p => p.PrimeroDisponible).FirstOrDefault();
            var masDisponible = g.OrderByDescending(p => p.Disponible).FirstOrDefault(p => p.Disponible.TotalSeconds > 30);
            var masInquieto = g.OrderByDescending(p => p.Cambios).FirstOrDefault(p => p.Cambios > 0);
            var ultimo = movs.LastOrDefault();
            string Pila(EstadoPersona p) => p == null ? "—" : p.Nombre.Split(',')[0];
            fCuriosidades.Poner(
                Dato.D("gente que veo", g.Length.ToString(), Tema.Texto),
                Dato.D("conectados ahora", g.Count(p => Estados.EsConectado(p.Presencia)).ToString(), Tema.Salvia),
                Dato.D("disponibles", g.Count(p => Estados.EsDisponible(p.Presencia)).ToString(), Tema.Salvia),
                Dato.D("ocupados", g.Count(p => Estados.EsOcupado(p.Presencia)).ToString(), Tema.Rosa),
                Dato.Titulo("ranking de hoy"),
                Dato.D("se conectó primero", madrugador != null ? $"{Pila(madrugador)} · {madrugador.PrimeroDisponible:HH:mm}" : "—", Tema.Cielo),
                Dato.D("más tiempo disponible", masDisponible != null ? $"{Pila(masDisponible)} · {Corto(masDisponible.Disponible)}" : "—", Tema.Salvia),
                Dato.D("el que más se mueve", masInquieto != null ? $"{Pila(masInquieto)} · {masInquieto.Cambios} cambios" : "—", Tema.Malva),
                Dato.D("último movimiento", ultimo != null ? $"{ultimo.Persona.Split(',')[0]} · {Tema.Relativo(ultimo.Hora)}" : "—", Tema.TextoSuave),
                Dato.Titulo("actividad"),
                Dato.D("cambios observados", movs.Length.ToString(), Tema.Cielo),
                Dato.D("conexiones", movs.Count(m => m.SeConecto).ToString(), Tema.Salvia),
                Dato.D("desconexiones", movs.Count(m => m.SeDesconecto).ToString(), Tema.Apagado),
                Dato.D("lecturas del equipo", ctx.Observador != null ? $"{ctx.Observador.Lecturas} · {ctx.Observador.UltimaMs} ms" : "—", Tema.TextoSuave),
                Dato.D("última", ctx.Observador?.UltimaLectura != null ? Tema.Relativo(ctx.Observador.UltimaLectura.Value) : "nunca", Tema.Apagado),
                Dato.D("observador", ctx.Observador?.Estado ?? "—", (ctx.Observador?.Estado ?? "").StartsWith("leyendo") ? Tema.Salvia : Tema.Durazno));
            ActualizarVistaPrevia();
            Acomodar(); Invalidate();
        }

        public override void Acomodar()
        {
            int pad = S(4), w = Width - pad * 2, botH = S(34);
            rTarjetas = new Rectangle(pad, S(4), w, S(84));
            int y = rTarjetas.Bottom + S(10);
            int alto = Height - y - S(4);
            int hB = Math.Max(S(150), (int)(alto * 0.34));                       // tabla de mis mensajes
            int hC = Math.Max(S(210), (int)(alto * 0.38));                       // editor + vista previa + curiosidades
            int hD = alto - hB - hC - S(20);                                     // contactos + movimientos + ficha

            // --- B: la tabla de mensajes, a lo ancho
            int yBotB = y + hB - botH;
            lista.SetBounds(pad, y, w, yBotB - y - S(8));
            Flujo(new[] { chNuevo }, pad, yBotB, w);

            // --- C: editor | vista previa | gráficos
            int yC = y + hB + S(10);
            int c1 = (int)(w * 0.50), c2 = (int)(w * 0.26), c3 = w - c1 - c2 - S(24);
            int x1 = pad, x2 = x1 + c1 + S(12), x3 = x2 + c2 + S(12);
            int yBotC = yC + hC - botH;
            int yy = yC;
            int f1 = (int)(c1 * 0.34), f2 = (int)(c1 * 0.24), f3 = c1 - f1 - f2 - S(12);
            cNombre.SetBounds(x1, yy, f1, S(46));
            cEtiqueta.SetBounds(x1 + f1 + S(6), yy, f2, S(46));
            cPara.SetBounds(x1 + f1 + f2 + S(12), yy, f3, S(46)); yy += S(50);
            rSugerencias = new Rectangle(x1, yy, c1, S(28)); yy += S(32);
            Flujo(sugerencias, rSugerencias.Left, rSugerencias.Top, rSugerencias.Width);
            int hEd = Math.Max(S(70), yBotC - yy - S(8));
            editor.SetBounds(x1, yy, c1, hEd);
            Flujo(new[] { chGuardar, chMandar, chBorrar }, x1, Math.Max(yBotC, yy + hEd + S(6)), c1);
            int hPrev = (int)((hC - S(12)) * 0.56);
            vistaPrevia.SetBounds(x2, yC, c2, hPrev);
            gChats.SetBounds(x2, yC + hPrev + S(12), c2, hC - hPrev - S(16));
            int hG = (hC - S(12)) / 2;
            gPresencia.SetBounds(x3, yC, c3, hG);
            fCuriosidades.SetBounds(x3, yC + hG + S(12), c3, hC - hG - S(16));

            // --- D: contactos | movimientos | ficha del contacto
            int yD = yC + hC + S(10);
            int d1 = (int)(w * 0.50), d3 = (int)(w * 0.19), d2 = w - d1 - d3 - S(24);
            int xd1 = pad, xd2 = xd1 + d1 + S(12), xd3 = xd2 + d2 + S(12);
            int yBotD = yD + hD - botH;
            lContactos.SetBounds(xd1, yD, d1, yBotD - yD - S(8));
            Flujo(new[] { chLeerPresencia, chNuevoC }, xd1, yBotD, d1);
            lMovimientos.SetBounds(xd2, yD, d2, yBotD - yD - S(8));
            int y2 = yD;
            cApodo.SetBounds(xd3, y2, (int)(d3 * 0.44) - S(5), S(44));
            cNombreC.SetBounds(xd3 + (int)(d3 * 0.44) + S(5), y2, d3 - (int)(d3 * 0.44) - S(5), S(44)); y2 += S(47);
            cCorreo.SetBounds(xd3, y2, d3, S(44)); y2 += S(47);
            cRol.SetBounds(xd3, y2, d3, Math.Max(S(38), Math.Min(S(44), yBotD - y2 - S(6))));
            Flujo(new[] { chGuardarC, chBorrarC }, xd3, yBotD, d3);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Tema.Fondo);
            var P = ctx.Personalizados; var K = ctx.Contactos;
            var rects = Tarjetas(rTarjetas, new[] { 0.8f, 0.8f, 1.3f, 0.9f, 1.2f });
            var top = P.Lista.OrderByDescending(p => p.Usos).FirstOrDefault(p => p.Usos > 0);
            int disp = presencias.Count(x => (x.Tipo == "privado") && Contactos.Normalizar(x.Presencia).StartsWith("disponible")), aus = presencias.Count(x => x.Tipo == "privado" && Contactos.Normalizar(x.Presencia).StartsWith("ausente")), ocu = presencias.Count(x => x.Tipo == "privado" && (Contactos.Normalizar(x.Presencia).StartsWith("ocupado") || Contactos.Normalizar(x.Presencia).Contains("no molestar")));
            Tema.TarjetaDato(g, rects[0], esc, "MENSAJES", P.Lista.Count.ToString(), $"{P.Lista.Count(p => p.Etiqueta.Length > 0)} con etiqueta", Tema.Malva);
            Tema.TarjetaDato(g, rects[1], esc, "ENVIADOS", P.Lista.Sum(p => p.Usos).ToString(), top != null ? "último " + Rel(P.Lista.Max(p => p.UltimoUso)) : "todavía ninguno", Tema.Salvia);
            Tema.TarjetaDato(g, rects[2], esc, "EL MÁS USADO", top?.Nombre ?? "—", top != null ? $"{top.Usos} veces" : "usá «mandar ahora»", Tema.Cielo, Tema.Fina(15f));
            Tema.TarjetaDato(g, rects[3], esc, "CONTACTOS", K.Lista.Count.ToString(), $"{K.Lista.Count(k => k.Correo.Length > 0)} con correo", Tema.Crema);
            Tema.TarjetaDato(g, rects[4], esc, "PRESENCIA DEL EQUIPO", presencias.Count == 0 ? "—" : $"{disp} disp · {aus} aus · {ocu} ocup", presencias.Count == 0 ? "se lee de la lista de chats de Teams" : $"{presencias.Count(x => x.NoLeido)} chats sin leer", disp > 0 ? Tema.Salvia : Tema.MuyApagado, Tema.Fina(14f));
        }
    }
}
