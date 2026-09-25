using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TeamsTools
{
    // =====================================================================================================
    // LLAMADAS: grabar la reunión, archivarla, transcribirla y resumirla — y VERLO pasar.
    //
    // Toda esta pantalla pinta la FOTO del grabador (FotoGrabador): nada de I/O, procesos ni red en el hilo
    // de la interfaz. Las acciones se encolan y vuelven al instante; lo que tarda se ve en la banda en vivo,
    // en el recorrido de la grabación elegida y en la línea de carga de la barra de título.
    // =====================================================================================================
    internal sealed class VistaLlamadas : Pantalla
    {
        readonly Grabador grab;
        readonly Tabla tGrabaciones = new Tabla
        {
            Etiqueta = "reuniones grabadas", Vacio = "todavía no grabé ninguna · prendé el interruptor y entrá a una reunión", AltoFila = 20,
            Columnas =
            {
                new Columna { Titulo = "cuándo", Peso = 0, MinAncho = 76, Mono = true },
                new Columna { Titulo = "reunión", Peso = 1.8f, MinAncho = 130 },
                new Columna { Titulo = "duró", Peso = 0, MinAncho = 52, Derecha = true, Mono = true },
                new Columna { Titulo = "estado", Peso = 0.9f, MinAncho = 96 },
                new Columna { Titulo = "palabras", Peso = 0, MinAncho = 56, Derecha = true, Mono = true },
                new Columna { Titulo = "audio", Peso = 0, MinAncho = 78, Derecha = true, Mono = true },
                new Columna { Titulo = "detalle", Peso = 1.6f, MinAncho = 120 },
            }
        };
        // 🚨 CajaTexto y no TextBox: el scroll nativo es BLANCO y no se tematiza. La barrita la dibuja OnPaint.
        readonly CajaTexto txResumen = new CajaTexto { BackColor = Tema.Panel, ForeColor = Tema.Texto };
        /// <summary>
        /// La transcripción como conversación (25-sep-2026). Antes era un TextBox con «[hh:mm:ss] Persona N: …» y saltos
        /// \n que Windows no corta: se veía todo pegado. Ahora cada voz tiene su color, se escucha desde cualquier turno
        /// y se lee en grande (el visor ocupa la pantalla entera con «ampliar»).
        /// </summary>
        readonly VisorTranscripcion visor = new VisorTranscripcion();
        bool amplio;
        readonly Interruptor swActivo = new Interruptor { Etiqueta = "grabar las reuniones", Acento = Tema.Rosa, Ayuda = "arranca al entrar a una call y corta al salir" };
        readonly Interruptor swBorrar = new Interruptor { Etiqueta = "borrar los WAV al archivar", Acento = Tema.Durazno, Ayuda = "el .opus comprimido queda SIEMPRE" };
        readonly Interruptor swResumir = new Interruptor { Etiqueta = "resumir con el modelo local", Acento = Tema.Cyan, Ayuda = "arma un resumen prolijo del texto" };
        readonly Interruptor swGente = new Interruptor { Etiqueta = "solo si hay gente en la sala", Acento = Tema.Salvia, Ayuda = "saltea las calls donde estás solo" };
        readonly Interruptor swPausar = new Interruptor { Etiqueta = "pausar la transcripción en llamadas", Acento = Tema.Malva, Ayuda = "no le roba CPU a Teams mientras hablás" };
        readonly Interruptor swHablantes = new Interruptor { Etiqueta = "separar quién habla", Acento = Tema.Cielo, Ayuda = "«Persona 1: …» · tu mic es «Yo»" };
        readonly Segmentado segCalidad = new Segmentado { Etiqueta = "motor de transcripción", Opciones = MotorAsr.Todos.Select(m => m.Nombre).ToArray(), Acento = Tema.Malva };
        readonly Deslizador dMinimo = new Deslizador { Etiqueta = "descartar lo más corto que", Min = 0, Max = 600, Paso = 15, Acento = Tema.Cielo, Marcas = new[] { 60, 180, 300 }, Formato = v => v == 0 ? "no descartar" : Grabador.Fmt(v) };
        readonly BandaEnVivo banda = new BandaEnVivo();
        readonly RecorridoGrabacion recorrido = new RecorridoGrabacion();
        Chip chCortar, chEscuchar, chReintentar, chCopiar, chCopiarTexto, chCarpeta, chBorrar;
        /// <summary>
        /// Qué chips corresponden AHORA. 🚨 No se usa `Control.Visible` como fuente de verdad: WinForms devuelve false
        /// para cualquier hijo mientras la pestaña está oculta, así que la fila salía vacía al volver a la pestaña.
        /// </summary>
        readonly HashSet<Chip> mostrados = new HashSet<Chip>();
        Rectangle rCards, rResumen;
        bool cargando;
        string elegida = "";
        FotoGrabador foto;
        bool primeraFoto = true;
        bool bandaGrande;

        public VistaLlamadas(Contexto c, Grabador g) : base(c)
        {
            grab = g;
            foreach (Control x in new Control[] { banda, recorrido, tGrabaciones, txResumen, visor, swActivo, swBorrar, swResumir, swGente, swPausar, swHablantes, segCalidad, dMinimo })
                Controls.Add(x);
            foreach (var p in Controls.OfType<Palanca>()) p.Superficie = Tema.Fondo;
            txResumen.Font = Tema.Fina(9.5f);
            txResumen.Desplazado += (o, e) => Invalidate(rResumen);
            // el visor: en grande tapa toda la pantalla (y Esc lo devuelve a su lugar)
            visor.PideAmplio += si =>
            {
                amplio = si;
                // 🚨 en grande el resto de la pestaña se OCULTA de verdad, no solo queda tapado: con controles hermanos
                //    superpuestos, DrawToBitmap (las fotos) los pinta en otro orden, y tapados igual recibirían el mouse
                foreach (Control c in Controls)
                    if (!ReferenceEquals(c, visor)) c.Visible = !si && (!(c is Chip ch) || mostrados.Contains(ch));
                Acomodar();
                if (si) { visor.BringToFront(); visor.Focus(); }
                Invalidate();
            };
            visor.Aviso += (t, x) => ctx.Aviso?.Invoke(t, x);
            visor.NombresCambiaron += MostrarElegida;
            visor.CambioSonido += () => { chEscuchar.Poner(visor.FaseSonido == Reproductor.Fase.Sonando || visor.FaseSonido == Reproductor.Fase.Abriendo ? "pausar" : "escuchar"); AcomodarChips(true); };
            // 🚨 grabando una llamada, loopcap graba TODAS las salidas: lo que suene acá se mezclaría en la reunión
            visor.MotivoParaNoSonar = () => grab.Foto.Vivo != null ? "estás grabando una llamada: lo que suene ahora se mezclaría en la grabación" : "";
            tGrabaciones.Cargando = true;
            tGrabaciones.SeleccionCambio += (o, e) => { elegida = tGrabaciones.Actual?.Tag as string ?? ""; MostrarElegida(); AcomodarChips(); };

            // los ajustes: se anotan en la config y el grabador los toma solo (GuardarConfig no bloquea)
            swActivo.Cambio += (o, e) => { if (cargando) return; grab.Cfg.Activo = swActivo.Prendido; grab.GuardarConfig(); };
            swBorrar.Cambio += (o, e) => { if (cargando) return; grab.Cfg.BorrarAudio = swBorrar.Prendido; grab.GuardarConfig(); };
            swResumir.Cambio += (o, e) => { if (cargando) return; grab.Cfg.Resumir = swResumir.Prendido; grab.GuardarConfig(); };
            swGente.Cambio += (o, e) => { if (cargando) return; grab.Cfg.SoloConGente = swGente.Prendido; grab.GuardarConfig(); };
            swPausar.Cambio += (o, e) => { if (cargando) return; grab.Cfg.PausarEnLlamada = swPausar.Prendido; grab.GuardarConfig(); };
            swHablantes.Cambio += (o, e) => { if (cargando) return; grab.Cfg.Hablantes = swHablantes.Prendido; grab.GuardarConfig(); };
            segCalidad.Cambio += (o, e) => { if (cargando) return; grab.Cfg.Motor = MotorAsr.Todos[Math.Max(0, segCalidad.Elegido)].Id; grab.GuardarConfig(); Invalidate(); };
            dMinimo.Cambio += (o, e) => { if (cargando) return; grab.Cfg.MinimoSegundos = dMinimo.Valor; grab.GuardarConfig(); };

            chCortar = Nuevo("cortar la grabación ahora", Chip.Modo.Boton, Tema.Rosa, (o, e) => { chCortar.Ocupado = true; grab.CortarAhora("la cortaste a mano"); });
            chEscuchar = Nuevo("escuchar", Chip.Modo.Boton, Tema.Cyan, (o, e) => visor.Escuchar());
            chEscuchar.AccionDerecha += (o, e) => AbrirAfuera();   // clic derecho: en el reproductor de Windows, como antes
            chReintentar = Nuevo("reintentar esta", Chip.Modo.Boton, Tema.Durazno, (o, e) => { if (elegida.Length > 0) { chReintentar.Ocupado = true; grab.Reintentar(elegida); } });
            chCopiar = Nuevo("copiar el resumen", Chip.Modo.Boton, Tema.Crema, (o, e) => Copiar(true));
            chCopiarTexto = Nuevo("copiar la transcripción", Chip.Modo.Boton, Tema.Crema, (o, e) => Copiar(false));
            chCarpeta = Nuevo("abrir la carpeta", Chip.Modo.Boton, Tema.Malva, (o, e) => AbrirCarpeta());
            chBorrar = Nuevo("borrar esta grabación", Chip.Modo.Boton, Tema.Apagado, (o, e) => BorrarElegida());
            chBorrar.Sub = "a la papelera";

            banda.CortarYa += () => { chCortar.Ocupado = true; grab.CortarAhora("la cortaste a mano"); };
            banda.FuentePulso = () => grab.Pulso;   // las cintas leen el pulso en cada cuadro, sin pasar por la foto
            // el grabador avisa desde SUS hilos: se marshalea con BeginInvoke (EnUi) y la UI lee la foto nueva
            grab.Cambio += () => { try { ctx.EnUi?.Invoke(Refrescar); } catch { } };
        }

        Grabacion Elegida => foto?.Todas.FirstOrDefault(x => x.Id == elegida);

        // ------------------------------------------------------------------ acciones (ninguna espera)

        void Copiar(bool resumen)
        {
            var g = Elegida;
            if (g == null) { ctx.Aviso?.Invoke("Elegí una grabación", ""); return; }
            // la transcripción sale del visor: turnos separados, con hora y con los nombres que les pusiste
            string transcripcion = visor.IdActual == g.Id ? visor.TextoParaCopiar() : "";
            if (transcripcion.Length == 0) transcripcion = CajaTexto.Saltos(g.Texto);
            string t = resumen && g.Resumen.Length > 0 ? CajaTexto.Saltos(NombresDeVoces.Aplicar(g.Resumen, visor.IdActual == g.Id ? visor.Nombres : null)) : transcripcion;
            if (t.Length == 0) { ctx.Aviso?.Invoke("Todavía no hay texto", g.Estado); return; }
            try
            {
                Clipboard.SetText($"{g.Reunion} · {g.Desde:dd/MM/yyyy HH:mm} · {Grabador.Fmt(g.SegundosAudio)}\r\n\r\n{t}");
                ctx.Aviso?.Invoke("Copiado", resumen && g.Resumen.Length > 0 ? "el resumen" : "la transcripción entera");
            }
            catch (System.Runtime.InteropServices.ExternalException ex) { ctx.Aviso?.Invoke("No pude copiar", ex.Message); }   // el portapapeles lo tenía otra app
        }

        void AbrirAfuera()
        {
            var g = Elegida;
            if (g == null) return;
            string archivo = g.TieneArchivo ? g.Archivo : g.Wav;
            chEscuchar.Ocupado = true;
            Fondo.Correr(this, "abriendo el audio", () =>
            {
                if (!File.Exists(archivo)) return false;
                Process.Start(new ProcessStartInfo(archivo) { UseShellExecute = true });
                return true;
            }, ok => { chEscuchar.Ocupado = false; if (!ok) ctx.Aviso?.Invoke("No encuentro el audio", archivo); },
               ex => { chEscuchar.Ocupado = false; ctx.Aviso?.Invoke("No pude abrir el audio", ex.Message); }, Tema.Cyan, mostrar: false);
        }

        void AbrirCarpeta()
        {
            var g = Elegida;
            string d = g != null ? g.Carpeta : Path.Combine(Program.CarpetaDatos, "grabaciones");
            chCarpeta.Ocupado = true;
            Fondo.Correr(this, "abriendo la carpeta", () => { if (Directory.Exists(d)) Process.Start("explorer.exe", d); },
                () => chCarpeta.Ocupado = false, ex => { chCarpeta.Ocupado = false; ctx.Aviso?.Invoke("No pude abrir", ex.Message); }, mostrar: false);
        }

        DateTime armadoHasta;
        void BorrarElegida()
        {
            if (elegida.Length == 0) return;
            // dos clics: la primera vez pregunta (el chip se pone rosa «¿seguro?»), la segunda la manda a la papelera
            if (!chBorrar.Armado || DateTime.Now > armadoHasta) { chBorrar.Armado = true; armadoHasta = DateTime.Now.AddSeconds(4); chBorrar.Invalidate(); return; }
            chBorrar.Armado = false;
            chBorrar.Ocupado = true;
            grab.Borrar(elegida);
        }

        // ------------------------------------------------------------------ layout

        public override void Acomodar()
        {
            int pad = S(4), y = S(6);
            rCards = new Rectangle(pad, y, Width - pad * 2, S(74));
            y = rCards.Bottom + S(10);
            banda.SetBounds(pad, y, Width - pad * 2, S(foto?.Vivo != null ? BandaEnVivo.AltoGrabando : BandaEnVivo.AltoReposo));
            y = banda.Bottom + S(10);
            y = Flujo(chips.Where(ch => mostrados.Count == 0 || mostrados.Contains(ch)), pad, y, Width - pad * 2);

            int alto = Math.Max(S(220), Height - y - S(10));
            int anchoDer = Math.Max(S(240), (int)((Width - pad * 2) * 0.34));
            int anchoIzq = Math.Max(S(260), Width - pad * 2 - anchoDer - S(12));

            // --- izquierda: dos columnas de interruptores + una de mandos, y abajo la tabla
            int wMandos = Math.Max(S(210), (int)(anchoIzq * 0.30));
            int resto = anchoIzq - wMandos - S(18);
            int colSw = (resto - S(12)) / 2, altoSw = S(34), dch = resto - colSw - S(12);
            swActivo.SetBounds(pad, y, colSw, altoSw);
            swBorrar.SetBounds(pad + colSw + S(12), y, dch, altoSw);
            swResumir.SetBounds(pad, y + altoSw + S(8), colSw, altoSw);
            swGente.SetBounds(pad + colSw + S(12), y + altoSw + S(8), dch, altoSw);
            swPausar.SetBounds(pad, y + (altoSw + S(8)) * 2, colSw, altoSw);
            swHablantes.SetBounds(pad + colSw + S(12), y + (altoSw + S(8)) * 2, dch, altoSw);
            Interruptor.Alinear(swActivo, swResumir, swPausar);
            Interruptor.Alinear(swBorrar, swGente, swHablantes);
            int xm = pad + resto + S(18);
            segCalidad.SetBounds(xm, y, wMandos, S(42));
            dMinimo.SetBounds(xm, y + S(46), wMandos, S(42));

            int y2 = y + (altoSw + S(8)) * 3 + S(4);
            tGrabaciones.SetBounds(pad, y2, anchoIzq, Math.Max(S(90), y + alto - y2));

            // --- derecha: el recorrido de la elegida, el resumen y la transcripción
            int xd = pad + anchoIzq + S(12);
            int altoRec = Math.Min(S(250), Math.Max(S(170), (int)(alto * 0.44)));
            recorrido.SetBounds(xd, y, anchoDer, altoRec);
            int libre = Math.Max(S(160), alto - altoRec - S(16));
            // un resumen de una línea («sin resumen · …») no se lleva media columna: el lugar es para la transcripción
            int altoRes = ResumenCorto ? S(64) : Math.Max(S(72), (int)(libre * 0.38));
            rResumen = new Rectangle(xd, y + altoRec + S(8), anchoDer, altoRes);
            Encajar(txResumen, rResumen);
            var rTexto = new Rectangle(xd, rResumen.Bottom + S(8), anchoDer, Math.Max(S(96), libre - altoRes - S(8)));
            // ampliado, el visor tapa la pantalla entera; si no, va en su casillero debajo del resumen
            if (amplio) { visor.SetBounds(pad, S(4), Width - pad * 2, Height - S(8)); visor.BringToFront(); }
            else visor.SetBounds(rTexto.X, rTexto.Y, rTexto.Width, rTexto.Height);
        }

        /// <summary>El resumen entra en dos renglones: no hace falta darle una caja alta.</summary>
        bool ResumenCorto => txResumen.Text.Length < 160 && txResumen.Text.Count(ch => ch == '\n') < 2;

        /// <summary>Qué chips tienen sentido ahora; si cambió el conjunto (o un chip cambió de texto), se re-acomoda la fila.</summary>
        void AcomodarChips(bool forzar = false)
        {
            var g = Elegida;
            var quiero = new HashSet<Chip> { chCarpeta };
            if (foto?.Vivo != null) quiero.Add(chCortar);
            if (g != null && (g.TieneArchivo || (!g.AudioBorrado && g.BytesWav > 0))) quiero.Add(chEscuchar);
            if (g != null && (g.Estado == EstadoGrab.Fallo || g.Estado == EstadoGrab.Lista)) quiero.Add(chReintentar);
            if (g != null && g.Resumen.Length > 0) quiero.Add(chCopiar);
            if (g != null && g.Texto.Length > 0) quiero.Add(chCopiarTexto);
            if (g != null && !EstadoGrab.EnCurso(g.Estado)) quiero.Add(chBorrar);
            if (quiero.SetEquals(mostrados) && !forzar) return;
            mostrados.Clear();
            mostrados.UnionWith(quiero);
            foreach (var ch in chips) ch.Visible = !amplio && mostrados.Contains(ch);   // en grande, la pestaña es solo el visor
            Acomodar();
            Invalidate();
        }

        void Encajar(TextBox t, Rectangle r) =>
            t.SetBounds(r.X + S(13), r.Y + S(25), Math.Max(S(40), r.Width - S(24)), Math.Max(S(24), r.Height - S(34)));

        void PanelTexto(Graphics g, Rectangle r, string titulo, Color acento, int largo)
        {
            if (r.Width <= S(20) || r.Height <= S(20)) return;
            Tema.Tarjeta_(g, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(10), Tema.Panel, Tema.Panel);
            int pd = S(5);
            using (var b = new SolidBrush(acento)) g.FillEllipse(b, r.X + S(14), r.Y + S(9) + (S(14) - pd) / 2f, pd, pd);
            Tema.Texto_(g, titulo.ToUpperInvariant(), Tema.Media(7.5f), Tema.Apagado, new Rectangle(r.X + S(14) + pd + S(7), r.Y + S(9), r.Width - S(92), S(14)));
            if (largo > 0)
                Tema.Texto_(g, largo.ToString("N0") + " car.", Tema.Fina(7f), Tema.MuyApagado, new Rectangle(r.Right - S(80), r.Y + S(9), S(66), S(14)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        // ------------------------------------------------------------------ datos: SOLO de la foto

        public override void Refrescar()
        {
            using (Migas.Poner("llamadas · refrescar"))
            {
                foto = grab.Foto;
                cargando = true;
                swActivo.Poner(grab.Cfg.Activo);
                swBorrar.Poner(grab.Cfg.BorrarAudio);
                swResumir.Poner(grab.Cfg.Resumir);
                swGente.Poner(grab.Cfg.SoloConGente);
                swPausar.Poner(grab.Cfg.PausarEnLlamada);
                swHablantes.Poner(grab.Cfg.Hablantes);
                segCalidad.Poner(Array.FindIndex(MotorAsr.Todos, m => m.Id == MotorAsr.De(grab.Cfg.Motor).Id));
                dMinimo.Poner(grab.Cfg.MinimoSegundos);
                cargando = false;

                var filas = new List<FilaTabla>();
                foreach (var g in foto.Todas)
                    filas.Add(FilaTabla.F(g.Id, ColorEstado(g.Estado),
                        Celda.C(g.Desde.ToString("dd/MM HH:mm"), Tema.Apagado),
                        Celda.C(g.Reunion, Tema.Texto, EstadoGrab.EnCurso(g.Estado)),
                        Celda.C(Grabador.Fmt(g.SegundosAudio > 0 ? g.SegundosAudio : g.Dura.TotalSeconds), Tema.TextoSuave),
                        Celda.C(TextoEstado(g), ColorEstado(g.Estado), EstadoGrab.EnCurso(g.Estado) || g.Estado == EstadoGrab.Fallo),
                        Celda.C(g.Palabras > 0 ? g.Palabras.ToString("N0") : "", Tema.Cyan),
                        Celda.C(TextoAudio(g), g.TieneArchivo ? Tema.Salvia : g.AudioBorrado ? Tema.Apagado : Tema.Crema),
                        Celda.C(g.Detalle, Tema.TextoSuave)));
                // la primera vez se elige sola la más nueva: el recorrido nunca arranca vacío
                if (elegida.Length == 0 && foto.Todas.Length > 0) elegida = foto.Todas[0].Id;
                tGrabaciones.Poner(filas, elegida.Length > 0 ? elegida : null, foto.Todas.Length + " grabación/es");
                if (foto.Todas.Length == 0 && !primeraFoto) tGrabaciones.Cargando = false;
                primeraFoto = false;

                // los chips ocupados se liberan cuando la foto muestra que su acción ya se aplicó
                if (chCortar.Ocupado && foto.Vivo == null) chCortar.Ocupado = false;
                if (chBorrar.Ocupado && Elegida == null) { chBorrar.Ocupado = false; elegida = foto.Todas.FirstOrDefault()?.Id ?? ""; }
                if (chReintentar.Ocupado && Elegida != null && Elegida.Detalle == "reintentando") chReintentar.Ocupado = false;
                else if (chReintentar.Ocupado && Elegida != null && EstadoGrab.EnCola(Elegida.Estado)) chReintentar.Ocupado = false;

                banda.Poner(foto, grab.Cfg);
                bool grabando = foto.Vivo != null;
                if (grabando != bandaGrande) { bandaGrande = grabando; Acomodar(); Invalidate(); }   // la banda crece con las cintas
                // empezó a grabarse una llamada con la escucha andando: pausa, o el audio viejo quedaría en la reunión nueva
                if (grabando && visor.Sonando) visor.Pausar("empezó a grabarse una llamada");
                MostrarElegida();
                AcomodarChips();
                Invalidate(rCards);
            }
        }

        static readonly System.Text.RegularExpressions.Regex ReVineta = new System.Text.RegularExpressions.Regex(@"(?m)^[ \t]*[\*\-][ \t]+", System.Text.RegularExpressions.RegexOptions.Compiled);

        void MostrarElegida()
        {
            var g = Elegida;
            recorrido.Poner(g);
            visor.Poner(g, ctx.RutaCorrillos);
            bool corto = ResumenCorto;
            string resumen;
            if (g == null) resumen = "elegí una grabación de la lista";
            else if (g.Resumen.Length > 0)
                // el resumen habla de «Persona 3»: con los nombres que le pusiste a las voces, y viñetas de verdad
                resumen = ReVineta.Replace(NombresDeVoces.Aplicar(g.Resumen, visor.IdActual == g.Id ? visor.Nombres : null), "  • ");
            else resumen = g.Estado == EstadoGrab.Lista ? "(sin resumen · el modelo local estaba apagado)" : "todavía no hay resumen · " + TextoEstado(g);
            if (txResumen.Text != CajaTexto.Saltos(resumen)) txResumen.Text = resumen;     // no reescribir si no cambió: conserva el scroll
            if (ResumenCorto != corto) Acomodar();                                        // la caja del resumen se achica o crece
            Invalidate(rResumen);
        }

        /// <summary>Deja elegida esa grabación (el clic en el globo de «transcripción lista» llega acá).</summary>
        public void Elegir(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            elegida = id;
            Refrescar();
        }

        /// <summary>
        /// Para las fotos (`--foto llamadas --modo …`): «amplio», «buscar:…», «voz:…», «sonar:125», «turno:40»,
        /// «grabacion:ID», separados por «;». Vacío = volver a como estaba (lo pide la foto al terminar).
        /// </summary>
        public override void Modo(string que)
        {
            if (string.IsNullOrEmpty(que)) { visor.Probar("reiniciar"); visor.CambiarAmplio(false); return; }
            // 🚨 separados por «;»: el pedido de la foto (foto-pedido.txt) ya usa «|» entre sus campos
            foreach (var parte in que.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (parte == "amplio") visor.CambiarAmplio(true);
                else if (parte == "chico") visor.CambiarAmplio(false);
                else if (parte.StartsWith("grabacion:", StringComparison.Ordinal)) Elegir(parte.Substring(10));
                else visor.Probar(parte);
            }
        }

        internal static string TextoEstado(Grabacion g)
        {
            string p = g.Progreso >= 0 && g.Progreso <= 1 ? $" {g.Progreso:P0}" : "";
            switch (g.Estado)
            {
                case EstadoGrab.Grabando: return "grabando";
                case EstadoGrab.Cerrando: return "cerrando";
                case EstadoGrab.Grabada: return "en cola";
                case EstadoGrab.Comprimiendo: return "archivando" + p;
                case EstadoGrab.Transcribiendo: return g.EnPausa ? "transcripción en pausa" : "transcribiendo" + p;
                case EstadoGrab.Transcripta: return "falta el resumen";
                case EstadoGrab.Resumiendo: return "resumiendo";
                default: return g.Estado;
            }
        }

        static string TextoAudio(Grabacion g)
        {
            if (g.TieneArchivo) return "opus " + (g.BytesArchivo / 1048576.0).ToString(g.BytesArchivo < 10 << 20 ? "0.0" : "0") + " MB";
            if (!g.AudioBorrado && g.BytesWav > 0) return "wav " + (g.BytesWav >> 20) + " MB";
            return g.AudioBorrado ? "borrado" : "";
        }

        internal static Color ColorEstado(string e)
        {
            switch (e)
            {
                case EstadoGrab.Grabando: case EstadoGrab.Cerrando: return Tema.Rosa;
                case EstadoGrab.Grabada: case EstadoGrab.Transcripta: return Tema.Durazno;
                case EstadoGrab.Comprimiendo: case EstadoGrab.Transcribiendo: return Tema.Malva;
                case EstadoGrab.Resumiendo: return Tema.Cyan;
                case EstadoGrab.Lista: return Tema.Salvia;
                case EstadoGrab.Fallo: return Tema.Rojo;
                default: return Tema.Apagado;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            if (amplio) return;                  // en grande la pestaña es solo el visor: las tarjetas de abajo no se ven
            var f = foto ?? grab.Foto;
            var todas = f.Todas;
            int listas = todas.Count(x => x.Estado == EstadoGrab.Lista);
            double horas = todas.Where(x => x.TieneArchivo).Sum(x => x.SegundosAudio) / 3600.0;
            long mbOpus = todas.Sum(x => x.BytesArchivo) >> 20;
            var r = Tarjetas(rCards, new[] { 1.5f, 1f, 1f, 1.15f, 1.1f });
            string ahora = f.Vivo != null ? "grabando" : f.Cerrando.Length > 0 ? "cerrando" : f.EnCola > 0 ? "procesando" : f.Activo ? "esperando una call" : "apagado";
            string subAhora = f.Vivo != null ? f.Vivo.Reunion + " · " + Grabador.Fmt(f.Vivo.Segundos)
                            : f.Cerrando.Length > 0 ? "juntando los archivos de «" + f.Cerrando + "»"
                            : f.EnCola > 0 ? "archivo y transcripción de fondo"
                            : f.Activo ? "arranca sola al entrar" : "prendé el interruptor";
            Ayuda.Tarjeta(g, esc, r[0], "AHORA", ahora, subAhora, f.Vivo != null || f.Cerrando.Length > 0 ? Tema.Rosa : f.EnCola > 0 ? Tema.Malva : f.Activo ? Tema.Salvia : Tema.Apagado, Tema.Fina(15f));
            Ayuda.Tarjeta(g, esc, r[1], "EN COLA", f.EnCola.ToString(), f.EnCola > 0 ? "de fondo, una por vez" : "nada pendiente", f.EnCola > 0 ? Tema.Durazno : Tema.Salvia);
            Ayuda.Tarjeta(g, esc, r[2], "LISTAS", listas.ToString(), "texto verificado", Tema.Cyan);
            Ayuda.Tarjeta(g, esc, r[3], "ARCHIVO .OPUS", mbOpus > 0 ? mbOpus + " MB" : "—", horas > 0 ? $"{horas:0.#} h guardadas para siempre" : "se guarda cada reunión", Tema.Salvia);
            Ayuda.Tarjeta(g, esc, r[4], "PALABRAS", todas.Sum(x => x.Palabras).ToString("N0"), f.Problema.Length > 0 ? "⚠ " + f.Problema : "transcriptas en total", f.Problema.Length > 0 ? Tema.Rosa : Tema.Crema);

            PanelTexto(g, rResumen, "resumen", Tema.Cyan, txResumen.TextLength);
            txResumen.PintarBarra(g, txResumen.Bounds, esc);
            // la transcripción se pinta sola (VisorTranscripcion), con su tarjeta, su cinta y sus voces
        }
    }

    /// <summary>
    /// La banda de arriba: lo que está pasando AHORA, con movimiento.
    ///   · grabando → punto que late, reloj, vúmetro que sigue el audio (sube rápido, baja lento, retiene el pico),
    ///     salud de loopcap y, si la reunión dejó de verse, el anillo de la cuenta regresiva para cortar;
    ///   · cerrando / procesando → giro + barra de progreso con destello (o «en pausa» si estás en una llamada);
    ///   · en reposo → una línea tranquila. Solo se anima cuando hay algo vivo.
    /// </summary>
    internal sealed class BandaEnVivo : Control
    {
        /// <summary>Alto lógico de la banda: grabando lleva dos cintas (llamada y micrófono); en reposo, una línea.</summary>
        public const int AltoGrabando = 124, AltoReposo = 66;

        FotoGrabador f;
        ConfigGrabador cfg;
        /// <summary>El pulso del grabador: se lee en CADA cuadro (20 lecturas por segundo de loopcap).</summary>
        public Func<PulsoAudio> FuentePulso;
        readonly Cinta cLlamada = new Cinta(), cMic = new Cinta();
        double nivel = -60, pico = -60;          // vúmetro de respaldo (loopcap viejo, sin pulso)
        DateTime picoHasta, ultimoCuadro = DateTime.Now;
        Rectangle rCortar;
        bool hoverCortar;
        float[] buf = new float[PulsoAudio.Capacidad];
        readonly SolidBrush pincel = new SolidBrush(Color.White);   // uno solo, se le cambia el color: 30 cuadros × 400 barras
        public event Action CortarYa;

        /// <summary>
        /// Una fila de la banda: la cinta que corre y el vúmetro de su derecha. Balística de vúmetro de verdad: sube
        /// en ~50 ms, baja en ~400 ms y retiene el pico 1,2 s.
        /// </summary>
        sealed class Cinta
        {
            public double Nivel = -60, Pico = -60;
            public DateTime PicoHasta;

            public void Avanzar(double objetivo, double dt)
            {
                objetivo = Math.Max(-60, Math.Min(0, objetivo));
                Nivel += (objetivo - Nivel) * Math.Min(1, dt * (objetivo > Nivel ? 18 : 2.5));
                if (Nivel >= Pico) { Pico = Nivel; PicoHasta = DateTime.Now.AddSeconds(1.2); }
                else if (DateTime.Now > PicoHasta) Pico = Math.Max(Nivel, Pico - dt * 20);
            }
        }

        public BandaEnVivo()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
        }

        protected override void Dispose(bool disposing) { if (disposing) pincel.Dispose(); base.Dispose(disposing); }

        Grabacion Activa => f?.Todas.Where(x => x.Estado == EstadoGrab.Comprimiendo || x.Estado == EstadoGrab.Transcribiendo || x.Estado == EstadoGrab.Resumiendo)
                                   .OrderBy(x => x.Desde).FirstOrDefault()
                          ?? f?.Todas.Where(x => EstadoGrab.EnCola(x.Estado)).OrderBy(x => x.Desde).FirstOrDefault();

        public void Poner(FotoGrabador foto, ConfigGrabador c)
        {
            f = foto; cfg = c;
            bool vivo = f != null && (f.Vivo != null || f.Cerrando.Length > 0 || Activa != null);
            if (vivo) Animacion.Encender(this); else Animacion.Apagar(this);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = rCortar.Contains(e.Location);
            if (h != hoverCortar) { hoverCortar = h; Cursor = h ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && rCortar.Contains(e.Location)) CortarYa?.Invoke();
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float esc = Dpi.Escala(this);
            int S(int px) => Dpi.S(esc, px);
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            double dt = Math.Min(0.1, (DateTime.Now - ultimoCuadro).TotalSeconds);
            ultimoCuadro = DateTime.Now;
            rCortar = Rectangle.Empty;
            if (f == null) return;

            if (f.Vivo != null) { PintarGrabando(g, r, S, dt); return; }
            var act = Activa;
            if (f.Cerrando.Length > 0 || act != null) { PintarProceso(g, r, S, act); return; }

            // reposo
            Tema.Tarjeta_(g, r, S(10), Tema.Panel, Tema.Panel);
            int pd = S(6);
            using (var b = new SolidBrush(f.Activo ? Tema.Alpha(Tema.Salvia, 150) : Tema.MuyApagado)) g.FillEllipse(b, S(18), Height / 2f - pd / 2f, pd, pd);
            string t = f.Activo ? "esperando una llamada · empiezo a grabar sola al entrar y corto sola al salir" : "la grabación está apagada · prendé «grabar las reuniones» para que arranque sola";
            Tema.Texto_(g, t, Tema.Fina(10f), f.Activo ? Tema.TextoSuave : Tema.Apagado, new Rectangle(S(34), 0, Width - S(48), Height));
        }

        void PintarGrabando(Graphics g, RectangleF r, Func<int, int> S, double dt)
        {
            var v = f.Vivo;
            Color acento = v.CierraEn.HasValue ? Tema.Durazno : Tema.Rosa;
            Tema.Tarjeta_(g, r, S(10), Tema.Mezcla(Tema.Panel, acento, 0.04f), Tema.Mezcla(Tema.Panel, acento, 0.05f));

            // --- izquierda: el punto que late (con halo), la reunión y el reloj
            double lat = (Math.Sin(Animacion.T * 3.4) + 1) / 2;
            float d = S(9), cx = S(22), cy = S(22);
            float halo = d * (1.6f + 1.2f * (float)lat);
            using (var b = new SolidBrush(Tema.Alpha(acento, (int)(70 * (1 - lat))))) g.FillEllipse(b, cx - halo / 2, cy - halo / 2, halo, halo);
            using (var b = new SolidBrush(acento)) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
            int x = S(40);
            int anchoIzq = Math.Max(S(210), (int)(Width * 0.24));
            string eti = v.CierraEn.HasValue ? "LA REUNIÓN DEJÓ DE VERSE" : "GRABANDO" + (v.Tramo > 1 ? $" · TRAMO {v.Tramo}" : "");
            Tema.Texto_(g, eti, Tema.Media(7.5f), Tema.Alpha(acento, 220), new Rectangle(x, S(14), anchoIzq - x, S(16)));
            Tema.Texto_(g, v.Reunion, Tema.Fina(11f), Tema.Texto, new Rectangle(x, S(32), anchoIzq - x - S(6), S(26)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            var t = TimeSpan.FromSeconds(Math.Max(0, v.Segundos));
            string reloj = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
            Tema.Texto_(g, reloj, Tema.Fina(22f), Tema.Texto, new Rectangle(x - S(2), S(62), anchoIzq - x, S(44)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            // --- derecha: la salud (o la cuenta regresiva de corte)
            int wr = S(200), xr = Width - wr - S(12);
            PintarSalud(g, S, v, xr, wr);

            // --- centro: las dos cintas (o el vúmetro de respaldo si loopcap no manda pulso)
            int xm = anchoIzq + S(10), wm = xr - xm - S(18);
            var pu = FuentePulso?.Invoke();
            if (pu == null || pu.Escritas == 0)
            {
                PintarVumetroRespaldo(g, S, v, xm, wm, dt);
                return;
            }
            int alto = (Height - S(16)) / 2;
            PintarFila(g, S, pu, false, new Rectangle(xm, S(8), wm, alto), cLlamada, dt);
            PintarFila(g, S, pu, true, new Rectangle(xm, S(8) + alto, wm, alto), cMic, dt);
        }

        /// <summary>
        /// Una fila: etiqueta y «entrando», la cinta que corre (lo último a la derecha, las viejas se desvanecen),
        /// el nivel en dB con su pico, y debajo los dispositivos de ese lado con sus avisos. O(barras).
        /// </summary>
        void PintarFila(Graphics g, Func<int, int> S, PulsoAudio pu, bool esMic, Rectangle fr, Cinta c, double dt)
        {
            var pistas = pu.Pistas.Where(p => p.EsMic == esMic).ToArray();
            bool silenciado = esMic && pu.MicSilenciado == true;
            bool senal = pistas.Any(p => p.Estado == "grabando" && p.ConSenal(0.35));
            Color c1 = esMic ? Tema.Malva : Tema.Cyan, c2 = esMic ? Tema.Rosa : Tema.Salvia;
            if (silenciado) { c1 = Tema.Apagado; c2 = Tema.Durazno; }

            // etiqueta + «entrando»
            int wEti = S(92);
            Tema.Texto_(g, esMic ? "TU MIC" : "LLAMADA", Tema.Media(7.5f), Tema.Alpha(c1, 230), new Rectangle(fr.X, fr.Y + S(4), wEti, S(14)));
            float pd = S(6), px = fr.X + S(1), py = fr.Y + S(25);
            if (senal && !silenciado)
            {
                double lat = (Math.Sin(Animacion.T * 9) + 1) / 2;
                using (var b = new SolidBrush(Tema.Alpha(c2, (int)(60 + 60 * lat)))) g.FillEllipse(b, px - pd * 0.6f, py - pd * 0.6f, pd * 2.2f, pd * 2.2f);
            }
            using (var b = new SolidBrush(senal ? c2 : Tema.MuyApagado)) g.FillEllipse(b, px, py, pd, pd);
            string estado = silenciado ? "en silencio" : senal ? "entrando" : pistas.Length == 0 ? (esMic ? "sin mic" : "buscando") : "callado";
            Tema.Texto_(g, estado, Tema.Fina(8f), silenciado ? Tema.Durazno : senal ? Tema.Texto : Tema.Apagado, new Rectangle(fr.X + S(12), fr.Y + S(19), wEti - S(12), S(18)));

            // la cinta: n barras de 50 ms; se corre de a poco entre lectura y lectura (movimiento continuo)
            int wLect = S(78);
            var rc = new RectangleF(fr.X + wEti, fr.Y + S(1), fr.Width - wEti - wLect - S(12), fr.Height - S(19));
            int paso = Math.Max(S(5), 4);
            int n = Math.Max(24, Math.Min(buf.Length - 1, (int)(rc.Width / paso) + 1));
            pu.Copiar(esMic, buf, n);
            double frac = Math.Max(0, Math.Min(1, (DateTime.Now - pu.UltimoNivel).TotalMilliseconds / 50.0));
            PintarCinta(g, rc, buf, n, frac, c1, c2, silenciado, paso);

            // el vúmetro de la derecha: número grande, «dB» pegado, y la rayita del pico debajo
            c.Avanzar(buf[n - 1], dt);
            var rl = new Rectangle((int)rc.Right + S(12), (int)rc.Y + S(2), wLect, S(24));
            string db = c.Nivel <= -59.5 ? "—" : $"{c.Nivel:0}";
            var fNum = Tema.Fina(15f);
            int wNum = TextRenderer.MeasureText(g, db, fNum, new Size(wLect, rl.Height), TextFormatFlags.NoPadding).Width;
            Tema.Texto_(g, db, fNum, senal && !silenciado ? Tema.Texto : Tema.Apagado, rl, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            Tema.Texto_(g, "dB", Tema.Fina(7.5f), Tema.Apagado, new Rectangle(rl.X + wNum + S(3), rl.Y + S(3), S(24), rl.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            var rb = new RectangleF(rl.X, rl.Bottom + S(3), wLect - S(8), S(3));
            pincel.Color = Tema.Alpha(Tema.Texto, 18); g.FillRectangle(pincel, rb);
            float fn = (float)((c.Nivel + 60) / 60), fp = (float)((c.Pico + 60) / 60);
            pincel.Color = Tema.Alpha(c2, silenciado ? 90 : 210); g.FillRectangle(pincel, rb.X, rb.Y, rb.Width * Math.Max(0, fn), rb.Height);
            if (fp > 0.02) { pincel.Color = c2; g.FillRectangle(pincel, rb.X + rb.Width * fp - S(1), rb.Y - S(1), S(2), rb.Height + S(2)); }

            // los dispositivos de este lado, con el aviso fresco («+ se sumó …») que se desvanece
            var rd = new Rectangle(fr.X + wEti, (int)rc.Bottom + S(1), fr.Width - wEti, S(16));
            Tema.Texto_(g, TextoDispositivos(pistas, esMic, silenciado, pu), Tema.Fina(8f), silenciado ? Tema.Durazno : Tema.TextoSuave, rd,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        /// <summary>
        /// La línea de dispositivos de una fila: por dónde suena (con «recién conectado» si se sumó hace poco) y el
        /// aviso fresco de 8 s de cualquier otro cambio (volvió, en espera, se desconectó). Sin repetir nombres.
        /// </summary>
        static string TextoDispositivos(PistaEnVivo[] pistas, bool esMic, bool silenciado, PulsoAudio pu)
        {
            if (silenciado) return "silenciado en Teams · lo que digas ahora no se graba";
            if (pistas.Length == 0) return esMic ? "Teams todavía no usa ningún micrófono" : "esperando que loopcap abra las salidas";
            bool Fresca(PistaEnVivo p) => (DateTime.Now - p.Cambio).TotalSeconds < 8 && (p.UltimoAviso != "se sumó" || p.SeSumoEn > 2);
            var partes = new List<string>();
            var suenan = pistas.Where(p => p.Estado == "grabando" && p.ConSenal(3)).ToList();
            if (suenan.Count > 0)
                partes.Add((esMic ? "entra por " : "suena por ") +
                           string.Join(" + ", suenan.Select(p => Corto(p.Nombre) + (Fresca(p) && p.UltimoAviso == "se sumó" ? " (recién conectado)" : ""))));
            else
            {
                var abiertas = pistas.Where(p => p.Estado == "grabando").ToList();
                partes.Add(abiertas.Count == 0 ? "en espera"
                         : esMic ? Corto(abiertas[0].Nombre) + " abierto · no llega voz"
                         : $"{abiertas.Count} salida{(abiertas.Count == 1 ? "" : "s")} abierta{(abiertas.Count == 1 ? "" : "s")} · nada sonando");
            }
            var otra = pistas.Where(p => Fresca(p) && !suenan.Contains(p)).OrderByDescending(p => p.Cambio).FirstOrDefault();
            if (otra != null) partes.Add((otra.Estado == "grabando" ? "+ " : "") + Corto(otra.Nombre) + " " + otra.UltimoAviso);
            return string.Join("  ·  ", partes);
        }

        static readonly HashSet<string> NombresGenericos = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Speakers", "Altavoces", "Headphones", "Auriculares", "Headset", "Auriculares con micrófono", "Microphone", "Micrófono",
              "Speaker", "Altavoz", "Line", "Línea", "Digital Audio", "Audio digital", "Realtek Digital Output" };

        /// <summary>
        /// El nombre que se entiende: «Speakers (Realtek(R) Audio)» → «Realtek(R) Audio» (afuera es genérico);
        /// «Microphone Array (Intel® Smart Sound Technology…)» → «Microphone Array» (adentro es la marca del chip).
        /// </summary>
        static string Corto(string nombre)
        {
            int a = nombre.IndexOf('('), b = nombre.LastIndexOf(')');
            string afuera = (a > 0 ? nombre.Substring(0, a) : nombre).Trim();
            string adentro = a >= 0 && b > a ? nombre.Substring(a + 1, b - a - 1).Trim() : "";
            string s = adentro.Length > 0 && (NombresGenericos.Contains(afuera) || afuera.Length == 0) ? adentro : afuera;
            return s.Length > 30 ? s.Substring(0, 29) + "…" : s;
        }

        /// <summary>
        /// La cinta: barras simétricas alrededor de una línea, la más nueva a la derecha. Altura por nivel con curva
        /// (−60 dB = línea, 0 dB = alto completo), color del tono tranquilo al vivo según la altura, y las viejas se
        /// desvanecen. Un solo pincel reutilizado: sin asignaciones por barra. O(n).
        /// </summary>
        void PintarCinta(Graphics g, RectangleF r, float[] m, int n, double frac, Color c1, Color c2, bool apagada, int paso)
        {
            float cy = r.Y + r.Height / 2, semi = r.Height / 2 - 1;
            pincel.Color = Tema.Alpha(Tema.Texto, 16);
            g.FillRectangle(pincel, r.Left, cy - 0.5f, r.Width, 1f);
            float ancho = Math.Max(2f, paso * 0.6f);
            for (int i = 0; i < n; i++)
            {
                float xb = r.Right - (n - 1 - i + (float)frac) * paso - ancho;
                if (xb < r.Left || xb > r.Right) continue;
                double db = m[i];
                double a = Math.Pow(Math.Max(0, Math.Min(1, (db + 60) / 60.0)), 1.15);
                float h = (float)Math.Max(1.0, a * semi);
                double edad = (double)i / (n - 1);                   // 0 = la más vieja, 1 = la más nueva
                int alfa = (int)((apagada ? 30 : 45) + (apagada ? 90 : 200) * edad * edad);
                pincel.Color = Tema.Alpha(Tema.Mezcla(c1, c2, (float)a), Math.Min(255, alfa));
                g.FillRectangle(pincel, xb, cy - h, ancho, 2 * h);
            }
            // la cabeza: un halo suave donde entra el audio ahora
            double ultimo = m[n - 1];
            if (!apagada && ultimo > PulsoAudio.UmbralSenalDb)
            {
                float hx = r.Right - ancho;
                float hh = (float)Math.Max(4, Math.Pow((ultimo + 60) / 60.0, 1.15) * semi);
                using (var br = new SolidBrush(Tema.Alpha(c2, 40))) g.FillEllipse(br, hx - hh * 0.6f, cy - hh, hh * 1.4f, hh * 2);
            }
        }

        void PintarSalud(Graphics g, Func<int, int> S, EnVivo v, int xr, int wr)
        {
            if (v.CierraEn.HasValue && cfg != null)
            {
                int dd = S(38);
                var ra = new RectangleF(xr, (Height - dd) / 2f, dd, dd);
                double total = Math.Max(1, Math.Max(v.CierraEn.Value, cfg.SegundosParaCortar));
                Tema.Anillo(g, ra, S(3), (float)(v.CierraEn.Value / total), Tema.BordeSuave, Tema.Durazno);
                Tema.Texto_(g, ((int)Math.Ceiling(v.CierraEn.Value)).ToString(), Tema.Fina(10f), Tema.Texto, Rectangle.Round(ra), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                Tema.Texto_(g, "si no volvés, corto", Tema.Fina(8.5f), Tema.TextoSuave, new Rectangle(xr + dd + S(10), Height / 2 - S(24), wr - dd - S(10), S(18)));
                rCortar = new Rectangle(xr + dd + S(10), Height / 2 + S(2), Math.Min(S(110), wr - dd - S(10)), S(20));
                Tema.Tarjeta_(g, rCortar, S(3), Tema.Mezcla(Tema.Panel, Tema.Rosa, hoverCortar ? 0.25f : 0.12f), Tema.Alpha(Tema.Rosa, 120));
                Tema.Texto_(g, "cortar ya", Tema.Fina(8.5f), Tema.Rosa, rCortar, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            bool sano = v.Sano;
            int pd = S(6), y0 = S(18);
            using (var b = new SolidBrush(sano ? Tema.Salvia : Tema.Rosa)) g.FillEllipse(b, xr, y0 + S(5), pd, pd);
            var rt = new Rectangle(xr + pd + S(8), y0, wr - pd - S(8), S(16));
            Tema.Texto_(g, sano ? "loopcap sano" : (v.Problema.Length > 0 ? v.Problema : "esperando el primer reporte…"), Tema.Fina(8.5f), sano ? Tema.TextoSuave : Tema.Rosa,
                        rt, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            string datos = $"{v.MB:0.#} MB" + (v.DiscoLibreMB >= 0 ? $" · {v.DiscoLibreMB / 1024.0:0.#} GB libres" : "");
            Tema.Texto_(g, datos, Tema.Fina(8f), Tema.Apagado, new Rectangle(rt.X, rt.Bottom + S(4), rt.Width, S(16)));
            string abajo = v.SegundosPerdidos > 0 ? $"⚠ {v.SegundosPerdidos:0.#} s no se pudieron escribir"
                         : sano ? "estado leído hace " + Math.Max(0, (int)(DateTime.Now - v.Actualizado).TotalSeconds) + " s" : "";
            Tema.Texto_(g, abajo, Tema.Fina(8f), v.SegundosPerdidos > 0 ? Tema.Durazno : Tema.Apagado, new Rectangle(rt.X, rt.Bottom + S(22), rt.Width, S(16)));
        }

        /// <summary>El vúmetro de antes (del estado de 1 s): por si loopcap todavía no manda el pulso.</summary>
        void PintarVumetroRespaldo(Graphics g, Func<int, int> S, EnVivo v, int xm, int wm, double dt)
        {
            double objetivo = Math.Max(-60, Math.Min(0, v.NivelDb));
            nivel += (objetivo - nivel) * Math.Min(1, dt * (objetivo > nivel ? 18 : 2.2));
            if (nivel >= pico || DateTime.Now > picoHasta) { pico = Math.Max(nivel, pico - dt * 18); if (nivel >= pico) picoHasta = DateTime.Now.AddSeconds(1.2); }
            int seg = 32, gapS = S(2);
            float ws = (wm - gapS * (seg - 1)) / (float)seg, hs = S(10), ym = Height / 2f - S(14);
            for (int i = 0; i < seg; i++)
            {
                double db = -60 + 60.0 * (i + 1) / seg;
                bool lit = nivel >= db - 60.0 / seg;
                Color c = db > -6 ? Tema.Durazno : db > -18 ? Tema.Crema : Tema.Salvia;
                bool esPico = Math.Abs(pico - db) <= 60.0 / seg && pico > -58;
                pincel.Color = lit ? Tema.Alpha(c, 220) : esPico ? Tema.Alpha(c, 160) : Tema.Alpha(Tema.Texto, 16);
                g.FillRectangle(pincel, xm + i * (ws + gapS), ym, ws, hs);
            }
            string pista = v.Pista.Length > 0 ? v.Pista : "sin sonido todavía";
            Tema.Texto_(g, pista + " · esperando el pulso de loopcap", Tema.Fina(8.5f), Tema.TextoSuave, new Rectangle(xm, (int)ym + S(16), wm, S(16)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        void PintarProceso(Graphics g, RectangleF r, Func<int, int> S, Grabacion a)
        {
            Color acento = a == null ? Tema.Rosa : VistaLlamadas.ColorEstado(a.Estado);
            Tema.Tarjeta_(g, r, S(10), Tema.Panel, Tema.Panel);
            float d = S(16);
            Giro.Pintar(g, new RectangleF(S(15), Height / 2f - d / 2, d, d), a != null && a.EnPausa ? Tema.Crema : acento, S(2));
            int x = S(44), wIzq = (int)(Width * 0.40);
            string eti = a == null ? "CERRANDO" : a.EnPausa ? "EN PAUSA" : VistaLlamadas.TextoEstado(a).ToUpperInvariant();
            Tema.Texto_(g, eti, Tema.Media(7.5f), acento, new Rectangle(x, S(10), wIzq - x, S(14)));
            Tema.Texto_(g, a == null ? f.Cerrando : a.Reunion, Tema.Fina(11f), Tema.Texto, new Rectangle(x, S(26), wIzq - x, S(28)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            int xb = wIzq + S(14), wb = Width - xb - S(150);
            var rb = new RectangleF(xb, S(22), wb, S(6));
            using (var b = new SolidBrush(Tema.Alpha(Tema.Texto, 18))) using (var p = Tema.Redondeado(rb, S(3))) g.FillPath(b, p);
            if (a != null && a.Progreso >= 0)
            {
                var rl = new RectangleF(rb.X, rb.Y, Math.Max(S(6), (float)(rb.Width * Math.Min(1, a.Progreso))), rb.Height);
                using (var b = new SolidBrush(Tema.Alpha(acento, a.EnPausa ? 110 : 220))) using (var p = Tema.Redondeado(rl, S(3))) g.FillPath(b, p);
                if (!a.EnPausa) Brillo.Bloque(g, rl, S(3), Tema.Alpha(acento, 0), Color.White);
            }
            else Brillo.Bloque(g, rb, S(3), Tema.Panel, acento);
            string det = a == null ? "loopcap cerrando los WAV y reparando encabezados"
                       : a.EnPausa ? "la transcripción espera a que termine tu llamada: no le roba CPU a Teams"
                       : a.Etapa.Length > 0 ? a.Etapa : VistaLlamadas.TextoEstado(a);
            Tema.Texto_(g, det, Tema.Fina(8.5f), a != null && a.EnPausa ? Tema.Crema : Tema.TextoSuave, new Rectangle(xb, S(34), wb, S(18)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            int enCola = f.EnCola;
            Tema.Texto_(g, enCola > 1 ? $"{enCola} en cola" : "", Tema.Fina(9f), Tema.Apagado, new Rectangle(Width - S(140), 0, S(126), Height), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>
    /// El recorrido de UNA grabación: grabación → archivo .opus → transcripción → resumen → lista, cada paso con
    /// su estado (hecho, en curso con giro y %, en pausa, falló, pendiente), los datos verificados y la bitácora.
    /// </summary>
    internal sealed class RecorridoGrabacion : Control
    {
        Grabacion g;
        enum Paso { Pendiente, EnCurso, Pausa, Hecho, Fallo, Salteado }

        public RecorridoGrabacion()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
        }

        public void Poner(Grabacion x)
        {
            g = x;
            bool anima = g != null && (EstadoGrab.EnCurso(g.Estado) || EstadoGrab.EnCola(g.Estado));
            if (anima) Animacion.Encender(this); else Animacion.Apagar(this);
            Invalidate();
        }

        (string nombre, Paso paso, string nota)[] Pasos()
        {
            var e = g.Estado;
            bool fallo = e == EstadoGrab.Fallo;
            Paso grab = EstadoGrab.EnCurso(e) ? Paso.EnCurso : Paso.Hecho;
            Paso arch = g.TieneArchivo ? Paso.Hecho : e == EstadoGrab.Comprimiendo ? Paso.EnCurso : fallo && g.Texto.Length == 0 ? Paso.Fallo : Paso.Pendiente;
            if (!g.TieneArchivo && g.Version < 2 && g.Texto.Length > 0) arch = Paso.Salteado;
            Paso trans = g.Texto.Length > 0 || g.SegundosTranscriptos > 0 ? Paso.Hecho : e == EstadoGrab.Transcribiendo ? (g.EnPausa ? Paso.Pausa : Paso.EnCurso) : fallo && g.TieneArchivo ? Paso.Fallo : Paso.Pendiente;
            Paso res = g.Resumen.Length > 0 ? Paso.Hecho : e == EstadoGrab.Resumiendo ? Paso.EnCurso : e == EstadoGrab.Lista ? Paso.Salteado : Paso.Pendiente;
            Paso lista = e == EstadoGrab.Lista ? Paso.Hecho : fallo ? Paso.Fallo : Paso.Pendiente;
            string pct = g.Progreso >= 0 && g.Progreso <= 1 ? $"{g.Progreso:P0}" : "";
            return new[]
            {
                ("grabación", grab, grab == Paso.EnCurso ? (e == EstadoGrab.Cerrando ? "cerrando" : "en vivo") : Grabador.Fmt(g.SegundosAudio)),
                (".opus", arch, arch == Paso.Hecho ? (g.BytesArchivo / 1048576.0).ToString("0.0") + " MB" : arch == Paso.EnCurso ? pct : arch == Paso.Salteado ? "v1" : ""),
                ("texto", trans, trans == Paso.Hecho ? g.Palabras.ToString("N0") + " pal." : trans == Paso.EnCurso ? pct : trans == Paso.Pausa ? "pausa" : ""),
                ("resumen", res, res == Paso.Salteado ? "sin modelo" : res == Paso.EnCurso ? "pensando" : ""),
                ("lista", lista, ""),
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var gr = e.Graphics;
            gr.SmoothingMode = SmoothingMode.AntiAlias;
            gr.Clear(BackColor);
            float esc = Dpi.Escala(this);
            int S(int px) => Dpi.S(esc, px);
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Tema.Tarjeta_(gr, r, S(10), Tema.Panel, Tema.Panel);
            int pd = S(5);
            using (var b = new SolidBrush(Tema.Rosa)) gr.FillEllipse(b, S(14), S(9) + (S(14) - pd) / 2f, pd, pd);
            Tema.Texto_(gr, "RECORRIDO", Tema.Media(7.5f), Tema.Apagado, new Rectangle(S(14) + pd + S(7), S(9), S(120), S(14)));
            if (g == null)
            {
                Tema.Texto_(gr, "elegí una grabación para ver su recorrido", Tema.Fina(9f), Tema.Apagado, new Rectangle(S(14), S(30), Width - S(28), S(20)));
                return;
            }
            Tema.Texto_(gr, g.Reunion, Tema.Fina(8.5f), Tema.TextoSuave, new Rectangle(S(110), S(9), Width - S(124), S(14)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            // --- el stepper: cinco nodos unidos por un riel que se llena hasta el último hecho
            var pasos = Pasos();
            int n = pasos.Length, x0 = S(26), x1 = Width - S(26), yN = S(44);
            float paso = (x1 - x0) / (float)(n - 1), dn = S(12);
            int hechos = pasos.TakeWhile(p => p.paso == Paso.Hecho || p.paso == Paso.Salteado).Count();
            using (var p = new Pen(Tema.Alpha(Tema.Texto, 22), S(2))) gr.DrawLine(p, x0, yN, x1, yN);
            if (hechos > 0) using (var p = new Pen(Tema.Alpha(Tema.Salvia, 150), S(2))) gr.DrawLine(p, x0, yN, x0 + paso * Math.Min(n - 1, hechos - 1 + (hechos < n && pasos[hechos].paso != Paso.Pendiente ? 0.5f : 0)), yN);
            for (int i = 0; i < n; i++)
            {
                float cx = x0 + paso * i;
                var rn = new RectangleF(cx - dn / 2, yN - dn / 2, dn, dn);
                var (nombre, estado, nota) = pasos[i];
                Color c = estado == Paso.Hecho ? Tema.Salvia : estado == Paso.Fallo ? Tema.Rojo : estado == Paso.Pausa ? Tema.Crema
                        : estado == Paso.EnCurso ? VistaLlamadas.ColorEstado(g.Estado) : estado == Paso.Salteado ? Tema.Apagado : Tema.MuyApagado;
                using (var b = new SolidBrush(Tema.Panel)) gr.FillEllipse(b, rn);
                switch (estado)
                {
                    case Paso.Hecho:
                        using (var b = new SolidBrush(Tema.Alpha(c, 230))) gr.FillEllipse(b, rn);
                        using (var p = new Pen(Tema.Panel, S(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            gr.DrawLines(p, new[] { new PointF(cx - dn * 0.22f, yN), new PointF(cx - dn * 0.04f, yN + dn * 0.18f), new PointF(cx + dn * 0.24f, yN - dn * 0.16f) });
                        break;
                    case Paso.EnCurso: Giro.Pintar(gr, rn, c, S(2)); break;
                    case Paso.Pausa:
                        using (var p = new Pen(c, S(2))) { gr.DrawEllipse(p, rn); gr.DrawLine(p, cx - dn * 0.12f, yN - dn * 0.2f, cx - dn * 0.12f, yN + dn * 0.2f); gr.DrawLine(p, cx + dn * 0.12f, yN - dn * 0.2f, cx + dn * 0.12f, yN + dn * 0.2f); }
                        break;
                    case Paso.Fallo:
                        using (var b = new SolidBrush(Tema.Alpha(c, 220))) gr.FillEllipse(b, rn);
                        using (var p = new Pen(Tema.Panel, S(2))) { gr.DrawLine(p, cx - dn * 0.2f, yN - dn * 0.2f, cx + dn * 0.2f, yN + dn * 0.2f); gr.DrawLine(p, cx - dn * 0.2f, yN + dn * 0.2f, cx + dn * 0.2f, yN - dn * 0.2f); }
                        break;
                    default:
                        using (var p = new Pen(c, S(1) + 0.5f)) gr.DrawEllipse(p, rn);
                        break;
                }
                var rt = new Rectangle((int)(cx - paso / 2), yN + S(10), (int)paso, S(14));
                Tema.Texto_(gr, nombre, Tema.Fina(8.5f), estado == Paso.Pendiente ? Tema.Apagado : Tema.Texto, rt, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (nota.Length > 0) Tema.Texto_(gr, nota, Tema.Mono(7.5f), c, new Rectangle(rt.X, rt.Bottom, rt.Width, S(13)), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            // --- lo verificado, en una línea
            int y = yN + S(44);
            var hechosTxt = new List<string>();
            if (g.SegundosAudio > 0) hechosTxt.Add(Grabador.Fmt(g.SegundosAudio) + " de audio");
            if (g.SegundosTranscriptos > 0) hechosTxt.Add($"transcripto {Grabador.Fmt(g.SegundosTranscriptos)} ✓");
            if (g.SegundosPerdidos > 0) hechosTxt.Add($"⚠ {g.SegundosPerdidos:0.#} s no se pudieron grabar");
            if (g.Pistas.Length > 0) hechosTxt.Add(g.Pistas);
            if (g.Tramos > 1) hechosTxt.Add($"{g.Tramos} tramos");
            Tema.Texto_(gr, string.Join(" · ", hechosTxt), Tema.Fina(8.5f), g.SegundosPerdidos > 0 ? Tema.Durazno : Tema.TextoSuave, new Rectangle(S(14), y, Width - S(28), S(16)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            y += S(20);
            using (var p = new Pen(Tema.BordeSuave)) gr.DrawLine(p, S(14), y, Width - S(14), y);
            y += S(6);

            // --- la bitácora: lo último primero, lo más nuevo más brillante
            var bit = g.Bitacora ?? new List<string>();
            int alto = S(14), cuantas = Math.Max(0, (Height - y - S(8)) / alto);
            var ultimas = bit.Skip(Math.Max(0, bit.Count - cuantas)).Reverse().ToList();
            if (ultimas.Count == 0) Tema.Texto_(gr, "sin eventos todavía", Tema.Fina(8f), Tema.MuyApagado, new Rectangle(S(14), y, Width - S(28), alto));
            for (int i = 0; i < ultimas.Count; i++)
            {
                string l = ultimas[i];
                Color c = l.Contains("✗") || l.Contains("⚠") ? Tema.Durazno : i == 0 ? Tema.Texto : Tema.Mezcla(Tema.TextoSuave, Tema.Apagado, Math.Min(1f, i / (float)Math.Max(1, cuantas)));
                Tema.Texto_(gr, l, Tema.Mono(7.5f), c, new Rectangle(S(14), y + i * alto, Width - S(28), alto), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
