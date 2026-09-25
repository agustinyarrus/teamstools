using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TeamsTools
{
    /// <summary>El panel: ventana sin marco, negra, ancha, con tarjetas, linea de tiempo y pastillas de ajuste.</summary>
    internal sealed class MainForm : Form
    {
        readonly Config cfg;
        readonly Logger log;
        readonly ILector scanner;
        readonly Watcher vigia;
        readonly Presencia presencia;
        readonly LectorPresenciaLog logTeams;
        readonly Bandeja bandeja;
        readonly CuentaForm cuenta;
        readonly LineaTiempo linea;
        readonly bool demo;
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly List<Chip> chips = new List<Chip>();
        Chip chGracia, chIntervalo, chPosponer, chNadie, chSimulacion, chPresencia, chSonido, chAvisos, chRoster, chCuenta, chInicio, chMinimizado, chHorario, chPausa, chLeer, chSalir, chVolcar, chCarpeta;
        readonly List<(DateTime t, int otros, bool llamada)> historia = new List<(DateTime, int, bool)>();
        // mensajeria: autocontestador, cron y personalizados
        readonly TeamsChat chat;
        readonly ConfigRespuestas reglas;
        readonly Recordatorios recordatorios;
        readonly Personalizados personalizados;
        readonly Contactos contactos;
        readonly Autocontestador auto;
        readonly Programador cron;
        readonly Observador observador;
        readonly Ficha fSistema = new Ficha { Etiqueta = "sistema", Acento = Tema.Cielo };
        readonly Ficha fPresencia = new Ficha { Etiqueta = "permanencia online", Acento = Tema.Cyan };
        readonly Ficha fVentanas = new Ficha { Etiqueta = "ventanas de Teams", Acento = Tema.Malva, Vacio = "Teams no está corriendo" };
        readonly Ficha fMensajeria = new Ficha { Etiqueta = "mensajería", Acento = Tema.Salvia };
        /// <summary>El indicador global: vive al lado del título y dice qué está haciendo la app ahora mismo.</summary>
        readonly Cargador cargaGlobal = new Cargador { Modo = Cargador.Estilo.Ondas, MostrarTiempo = true, Acento = Tema.Cyan };
        readonly IndicadorGrabacion indicadorRec = new IndicadorGrabacion();   // el «REC» de la barra de título, en vivo
        /// <summary>La línea finita bajo la barra de título: se enciende con cualquier trabajo de fondo.</summary>
        readonly LineaCarga lineaCarga = new LineaCarga();
        /// <summary>Las ventanas de Teams se miran DE FONDO; la ficha pinta la última foto (antes: UIA en el hilo de la UI).</summary>
        Sondeo<FotoTeams> sondeoTeams;
        volatile bool panelVigiaVisible;
        DateTime ultimaBusquedaChat = DateTime.MinValue;
        readonly Tabla tCrono = new Tabla
        {
            Etiqueta = "cronología de presencias",
            Vacio = "todavía no vi ningún cambio",
            AltoFila = 18,
            Columnas =
            {
                new Columna { Titulo = "hora", Peso = 0, MinAncho = 44, Mono = true },
                new Columna { Titulo = "quién", Peso = 1f, MinAncho = 40 },
                new Columna { Titulo = "pasó a", Peso = 1.3f, MinAncho = 34 },
                new Columna { Titulo = "hace", Peso = 0, MinAncho = 46, Derecha = true, Mono = true },
            }
        };
        readonly Serie sIdle = new Serie { Etiqueta = "inactividad · cada caída a cero es un toque", Color = Tema.Cyan, Formato = v => Presencia.Fmt((int)v), Umbral = 60, EtiquetaUmbral = "toco acá", Limite = 300, EtiquetaLimite = "Teams te marca Ausente" };
        int toquesVistos;
        readonly DateTime arranque = DateTime.Now;
        int tics;
        bool recordada;
        readonly Pestanas pestanas = new Pestanas();
        readonly Pantalla[] pantallas;
        VistaMensajes pMensajes; VistaCron pCron; VistaPersonalizados pPers;
        VistaPatrones pPatrones; VistaEquipo pEquipo; VistaDia pDia; VistaSalud pSalud; VistaHistoria pHistoria; VistaPerfil pPerfil; VistaIA pIA;
        GuionPresencia guion; Actor actor; Radar radar; Grabador grabador; VistaLlamadas pLlamadas;
        VigiaMic vigiaMic;                        // ¿Teams te tiene en silencio? (para no grabar lo que no salió)
        volatile IntPtr hwndLlamada;              // la ventana de la reunión, según la última lectura (o Zero)
        DiarioPresencia diario;
        Historia chats;                 // el historial de Teams leído del disco ("historia" ya es la ocupación de la sala)
        readonly Corrillos corrillos = new Corrillos();   // quién está en call con quién
        string rutaCorrillos = "";
        DateTime ultimoRoster = DateTime.MinValue;
        Vista vista = new Vista();
        DateTime ultimaHoraLectura = DateTime.MinValue;
        float esc = 1f;
        bool permitirVisible, cerrarDeVerdad, hoverMin, hoverCerrar;
        DateTime armadoHasta = DateTime.MinValue;
        Rectangle rTitulo, rMin, rCerrar, rHero, rTarjetas, rLogTitulo, rBarra;
        int filasBarra = 2;
        bool tarjetasDosFilas;
        // anchos relativos: la de la reunión y la de presencia necesitan más lugar que las demás
        static readonly float[] PesosTarjetas = { 0.72f, 1.55f, 0.78f, 0.9f, 1.0f, 1.25f };
        static readonly int[] PresetsGracia = { 0, 3, 5, 10, 20, 30, 45, 60, 90, 120, 180, 300 };
        static readonly int[] PresetsIntervalo = { 2, 3, 5, 10 };
        static readonly int[] PresetsPosponer = { 5, 10, 15, 30, 60 };
        static readonly int[] PresetsNadie = { 0, 5, 10, 15, 30, 60 };

        public MainForm(Config c, Logger l, ILector s, bool arrancarMinimizado, bool modoDemo = false, bool forzarVisible = false)
        {
            cfg = c; log = l; scanner = s; demo = modoDemo;
            // la demo se ve aunque la config diga «arrancar en bandeja»; con --min explícito (fotos, pruebas) se esconde igual que siempre
            permitirVisible = forzarVisible || (!arrancarMinimizado && (!cfg.IniciarMinimizado || demo));
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Tema.Fondo;
            ForeColor = Tema.Texto;
            Text = "TeamsTools";
            KeyPreview = true;
            StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) esc = g.DpiX / 96f; } catch { }
            // ventana grande de entrada: hay mucha tabla y mucho dato que mostrar
            var libre = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(Dpi.Bruto(esc, 1600), (int)(libre.Width * 0.82)), Math.Min(Dpi.Bruto(esc, 1000), (int)(libre.Height * 0.88)));
            MinimumSize = new Size(Dpi.Bruto(esc, 900), Dpi.Bruto(esc, 580));
            // ...salvo que ya la hayas movido o redimensionado vos
            if (cfg.VentanaAncho >= 600 && cfg.VentanaAlto >= 400)
            {
                var r = new Rectangle(cfg.VentanaX, cfg.VentanaY, cfg.VentanaAncho, cfg.VentanaAlto);
                if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r))) { StartPosition = FormStartPosition.Manual; Bounds = r; recordada = true; }
            }
            if (!recordada)
            {
                var area = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            }

            linea = new LineaTiempo();
            Controls.Add(linea);
            Controls.AddRange(new Control[] { fSistema, fPresencia, fVentanas, fMensajeria, sIdle, tCrono, cargaGlobal, lineaCarga, indicadorRec });
            cargaGlobal.Visible = false;
            cargaGlobal.Superficie = Tema.Fondo;
            cargaGlobal.BringToFront();
            Tareas.Cambio += () => Hilo(ActualizarCarga);
            CrearChips();

            // --- mensajeria
            string datos = Program.CarpetaDatos;
            chat = new TeamsChat(cfg, log);
            if (!string.IsNullOrWhiteSpace(cfg.MiNombre)) chat.MiNombre = cfg.MiNombre;
            if (!string.IsNullOrWhiteSpace(cfg.MiCorreo)) chat.MiCorreo = cfg.MiCorreo;
            contactos = Contactos.Cargar(Path.Combine(datos, "contactos.json"));
            var yo = contactos.Lista.FirstOrDefault(k => k.Apodo == "yo");
            if (yo != null && string.IsNullOrWhiteSpace(cfg.MiNombre)) { chat.MiNombre = yo.Nombre; if (yo.Correo.Length > 0) chat.MiCorreo = yo.Correo; }
            reglas = ConfigRespuestas.Cargar(Path.Combine(datos, "respuestas.json"));
            recordatorios = Recordatorios.Cargar(Path.Combine(datos, "recordatorios.json"));
            personalizados = Personalizados.Cargar(Path.Combine(datos, "personalizados.json"));
            auto = new Autocontestador(cfg, log, reglas, chat, contactos, Path.Combine(datos, "bandeja.jsonl"));
            cron = new Programador(cfg, log, recordatorios, contactos, chat, auto);
            observador = new Observador(chat, log, Path.Combine(datos, "presencia.jsonl"));
            // los cambios de HOY vuelven del historial: la tabla del equipo no arranca vacía después de un reinicio
            int retomados = observador.Registro.Cargar();
            if (retomados > 0) log.Info($"Presencia del equipo: retomé {retomados} cambios de hoy de presencia.jsonl");
            rutaCorrillos = Path.Combine(datos, "corrillos.jsonl");
            // presencia y el lector del log van ANTES que las pantallas: las pestañas nuevas los reciben por constructor
            logTeams = new LectorPresenciaLog(log, cfg.CarpetaLogsTeamsONull);
            presencia = new Presencia(cfg, log) { Chat = chat, LogTeams = logTeams, Gate = chat.Gate };
            presencia.Cambio += () => Hilo(() => { if (Visible) Invalidate(rTarjetas); });
            // 🚨🚨 El historial se crea ANTES del Contexto. Estaba después, así que `Chats = chats` copiaba
            //    un null al contexto y quedaba null PARA SIEMPRE: la pestaña patrones nunca veía el corpus
            //    y «quién está en call con quién» se quedaba solo con lo deducido (8 llamadas en vez de 813).
            //    Un campo de un inicializador de objeto se evalúa en ese momento, no después.
            chats = new Historia(log);

            var ctx = new Contexto { Cfg = cfg, Log = log, Reglas = reglas, Recordatorios = recordatorios, Personalizados = personalizados, Contactos = contactos, Chat = chat, Auto = auto, Cron = cron, Observador = observador, Corrillos = corrillos, Chats = chats, RutaPresencia = Path.Combine(datos, "presencia.jsonl"), RutaCorrillos = rutaCorrillos, Aviso = (t, x) => { if (cfg.Notificaciones) bandeja?.Aviso(t, x); }, EnUi = a => Hilo(a) };
            pMensajes = new VistaMensajes(ctx); pCron = new VistaCron(ctx); pPers = new VistaPersonalizados(ctx);
            diario = new DiarioPresencia(Path.Combine(datos, "mi-presencia.jsonl"));
            pPatrones = new VistaPatrones(ctx, presencia, logTeams, diario);
            pEquipo = new VistaEquipo(ctx, chats);
            pDia = new VistaDia(ctx, diario);
            pSalud = new VistaSalud(ctx, presencia, logTeams);
            pHistoria = new VistaHistoria(ctx, chats);
            guion = GuionPresencia.Cargar(Path.Combine(datos, "guion.json"));
            actor = new Actor(cfg, log, guion, chat) { Presencia = presencia };
            presencia.EstadoDeseado = () => actor.EstadoDeseado;      // 🚨 la jerarquía: manda el guión
            pPerfil = new VistaPerfil(ctx, presencia, logTeams, guion, actor);
            radar = new Radar(Marcas.Cargar(Path.Combine(datos, "marcas.json")));
            pIA = new VistaIA(ctx, chats, radar);
            grabador = new Grabador(cfg, log, datos);
            pLlamadas = new VistaLlamadas(ctx, grabador);
            grabador.Cambio += () => Hilo(AlCambiarGrabador);
            AsistenteIA.Cambio += () => Hilo(() => { if (pestanas.Activa == 5) pIA.Refrescar(); else if (pestanas.Activa == 6) pLlamadas.Refrescar(); });
            pantallas = new Pantalla[] { pMensajes, pCron, pPers, pPerfil, pIA, pLlamadas, pPatrones, pEquipo, pDia, pSalud, pHistoria };
            indicadorRec.FuentePulso = () => grabador?.Pulso;
            indicadorRec.FuenteVivo = () => grabador?.Foto.Vivo;
            indicadorRec.Ir += () => { pestanas.Activa = 6; Acomodar(); RefrescarPantalla(); };
            foreach (var p in pantallas) { p.Visible = false; Controls.Add(p); }
            // en la demo nadie lee la lista de chats: la presencia del equipo sale de lo retomado del historial
            if (demo) pPers.PonerPresencias(observador.Registro.Gente().Where(p => p.Tipo != "yo").Select(p => new ChatItem { Nombre = p.Nombre, Presencia = p.Presencia, Tipo = p.Tipo }).ToList());
            pestanas.Nombres = new[] { "vigía", "mensajes", "cron", "personalizados", "perfil", "ia", "llamadas", "patrones", "equipo", "día", "salud", "historia" };
            pestanas.Insignias = new[] { "", "", "", "", "", "", "", "", "", "", "", "" };
            pestanas.Cambio += (o, e) => { Acomodar(); RefrescarPantalla(); Invalidate(); };
            Controls.Add(pestanas);
            auto.Cambio += () => Hilo(() => { ActualizarInsignias(); if (pestanas.Activa == 1) pMensajes.Refrescar(); if (pestanas.Activa == 3 && auto.UltimosChats.Count > 0) pPers.PonerPresencias(auto.UltimosChats); bandeja.Automatico = auto.Activo; });
            auto.Respondido += en => Hilo(() => { if (cfg.Sonido) Sonidos.Aviso(); if (cfg.Notificaciones) bandeja.Aviso(en.Respondido ? "Le contesté a " + en.Chat : "No pude contestar a " + en.Chat, en.Respondido ? en.Respuesta : en.Resultado); if (pestanas.Activa == 1) pMensajes.Refrescar(); });
            cron.Cambio += () => Hilo(() => { ActualizarInsignias(); if (pestanas.Activa == 2) pCron.Refrescar(); });
            cron.Ejecutado += (r, ok, det) => Hilo(() => { if (cfg.Notificaciones) bandeja.Aviso(ok ? "Recordatorio enviado a " + r.Para : "Recordatorio falló", ok ? r.TextoPlano : det); if (cfg.Sonido && ok) Sonidos.Adios(); if (pestanas.Activa == 2) pCron.Refrescar(); });
            cron.AvisoPropio += r => Hilo(() => { Sonidos.Aviso(); bandeja.Aviso("Recordatorio", r.TextoPlano, ToolTipIcon.Info); Mostrar(); pestanas.Activa = 2; Acomodar(); RefrescarPantalla(); });
            observador.Cambio += () => Hilo(() => { if (pestanas.Activa == 3) pPers.PonerPresencias(observador.Ultimos); });

            cuenta = new CuentaForm();
            cuenta.SalirAhora += (o, e) => { log.Info("Overlay: salir ahora"); vigia.SalirAhora(); };
            cuenta.Quedarse += (o, e) => { vigia.Posponer(); cuenta.Ocultar(); };
            cuenta.CancelarEstaVez += (o, e) => { vigia.CancelarEstaVez(); cuenta.Ocultar(); };

            bandeja = new Bandeja();
            bandeja.Mostrar += (o, e) => Mostrar();
            bandeja.AlternarPausa += (o, e) => AlternarPausa();
            bandeja.AlternarSimulacion += (o, e) => { AlternarToggle(chSimulacion); };
            bandeja.AlternarPresencia += (o, e) => { AlternarToggle(chPresencia); };
            bandeja.AlternarAutomatico += (o, e) => { auto.PonerModoAutomatico(!auto.Encendido); bandeja.Automatico = reglas.ModoAutomatico; if (pestanas.Activa == 1) pMensajes.Refrescar(); ActualizarInsignias(); };
            bandeja.SalirReunion += (o, e) => { log.Info("Bandeja: salir de la reunión ahora"); vigia.SalirAhora(); };
            // clic en el globo de «transcripción lista»: abre esa grabación en la pestaña llamadas
            bandeja.AvisoClic += (o, e) =>
            {
                if (DateTime.Now > avisoLlamadasHasta || avisoLlamadasId.Length == 0) return;
                Mostrar();
                pestanas.Activa = 6; Acomodar();
                pLlamadas.Elegir(avisoLlamadasId);
                RefrescarPantalla();
            };
            bandeja.Volcar += (o, e) => Volcar();
            bandeja.AbrirCarpeta += (o, e) => AbrirCarpeta();
            bandeja.Cerrar += (o, e) => CerrarDeVerdad();

            vigia = new Watcher(cfg, log, scanner, Path.Combine(Program.CarpetaDatos, demo ? "estado-demo.json" : "estado.json")) { Presencia = presencia };
            vigia.Extras = () => new Dictionary<string, object>
            {
                ["modoAutomatico"] = auto.Activo, ["modoAutomaticoEstado"] = auto.Estado, ["chatsNoLeidos"] = auto.ChatsNoLeidos, ["respuestasHoy"] = auto.RespuestasHoy,
                ["reglasActivas"] = reglas.Reglas.Count(r => r.Activa), ["recordatoriosProgramados"] = recordatorios.Lista.Count(r => r.Activo && r.Proximo != null),
                ["proximoRecordatorio"] = recordatorios.Proximos().FirstOrDefault()?.Proximo, ["chatTeamsListo"] = chat.Hwnd != IntPtr.Zero && Win32.IsWindow(chat.Hwnd),
                ["ui"] = SaludUi(), ["disco"] = new Dictionary<string, object> { ["escrituras"] = Disco.Escrituras, ["fallos"] = Disco.Fallos, ["ultimoError"] = Disco.UltimoError },
                ["grabador"] = SaludGrabador(),
            };
            vigia.Cambio += v => Hilo(() => AplicarVista(v));
            vigia.SalaVaciada += v => Hilo(() => AlVaciarse(v));
            vigia.CuentaCancelada += v => Hilo(() => cuenta.Ocultar());
            vigia.SalidaHecha += (v, ok, det) => Hilo(() => AlSalir(v, ok, det));
            log.LineaNueva += ln => Hilo(() => linea.Agregar(ln));

            tick.Tick += (o, e) => Latido();

            CreateHandle();                       // para poder recibir BeginInvoke aunque arranque escondido
            LatidoUi.Iniciar(this, log);          // sismógrafo: mide cuánto tarda la UI en atender, y quién la traba
            sondeoTeams = new Sondeo<FotoTeams>("teams", 2000, MedirTeams, log);
            sondeoTeams.Cambio += () => Hilo(() => { if (Visible && pestanas.Activa == 0) LlenarVentanas(); });
            sondeoTeams.Iniciar();
            linea.Cargar(log.Ultimas());
            log.Info($"TeamsTools listo · datos en {Program.CarpetaDatos}" + (Tema.CascadiaInstalada ? "" : " · sin Cascadia Code, uso Consolas"));
            if (!demo && Autoarranque.Migrar(Application.ExecutablePath)) log.Info("Arranque con Windows: la entrada ahora se llama TeamsTools y apunta a este exe");
            if (Autoarranque.Activo() != cfg.IniciarConWindows) { cfg.IniciarConWindows = Autoarranque.Activo(); cfg.Guardar(); chInicio.Activo = cfg.IniciarConWindows; }
            vigia.Iniciar();
            if (!demo) { presencia.Iniciar(); auto.Iniciar(); cron.Iniciar(); observador.Iniciar(); actor.Iniciar(); grabador.Iniciar(); }
            if (!demo) vigiaMic = new VigiaMic(() => hwndLlamada, e => grabador?.MicDeTeams(e), log);
            bandeja.Automatico = reglas.ModoAutomatico;
            ActualizarInsignias();
            tick.Start();
        }

        int S(int px) => Dpi.S(esc, px);

        // ------------------------------------------------------------ ciclo de vida

        protected override void SetVisibleCore(bool value)
        {
            if (!permitirVisible) { value = false; if (!IsHandleCreated) CreateHandle(); }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Win32.EsquinasRedondas(Handle);
            Win32.ModoOscuro(Handle);
            Win32.BordeColor(Handle, Tema.Bgr(Tema.Borde));
            if (cfg.AtajoGlobal && !demo)
            {
                atajoPuesto = Win32.RegisterHotKey(Handle, IdAtajo, Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, (uint)'C');
                log.Info(atajoPuesto ? "Atajo global Ctrl+Alt+C: prende y apaga el modo automático desde cualquier lado" : "No pude registrar Ctrl+Alt+C (ya lo usa otra app)");
            }
        }

        protected override void OnLoad(EventArgs e) { base.OnLoad(e); Acomodar(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); if (IsHandleCreated) Acomodar(); Invalidate(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!cerrarDeVerdad && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            base.OnFormClosing(e);
            GuardarVentana();
            if (atajoPuesto) { try { Win32.UnregisterHotKey(Handle, IdAtajo); } catch { } }
            tick.Stop();
            vigia.Detener();
            presencia.Detener();
            auto.Detener();
            cron.Detener();
            observador.Detener();
            if (actor != null) actor.Detener();
            if (grabador != null) grabador.Detener();
            vigiaMic?.Dispose();
            sondeoTeams?.Detener();
            cuenta.Ocultar();
            bandeja.Dispose();
            Disco.Vaciar(3000);                   // config, índice y log: que no quede nada en el aire
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        public void Mostrar()
        {
            permitirVisible = true;
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            Win32.TraerAlFrente(Handle);
            Invalidate();
        }

        /// <summary>
        /// Se retrata a sí misma en un PNG. Si la ventana está guardada en la bandeja la muestra LEJOS del
        /// escritorio (20000,20000) y sin robar el foco, saca la foto y la vuelve a esconder: el usuario no ve
        /// nada parpadear. Sirve para revisar cómo quedó una pestaña sin tener que pelear por el mouse.
        /// </summary>
        void TomarFoto()
        {
            using (Migas.Poner("foto de verificación (--foto)"))
                TomarFotoYa();
        }

        /// <summary>
        /// 🚨 Las fichas y la cronología del vigía se llenan en el latido, y sólo con la ventana VISIBLE (tic par):
        /// con la app escondida en la bandeja, la foto salía con «sin datos» según cayera el tic. Para la foto se
        /// llenan a mano, como haría el latido.
        /// </summary>
        void RefrescarVigia() { if (pestanas.Activa == 0) LlenarFichas(); }

        void TomarFotoYa()
        {
            string pedido = Path.Combine(Program.CarpetaDatos, "foto-pedido.txt");
            string[] p;
            try { if (!File.Exists(pedido)) return; p = File.ReadAllText(pedido).Split('|'); }
            catch { return; }
            if (p.Length < 2) return;

            int tab; int.TryParse(p[0].Trim(), out tab);
            string salida = p[1].Trim();
            int w = 0, h = 0, espera = 0;
            if (p.Length > 3) { int.TryParse(p[2].Trim(), out w); int.TryParse(p[3].Trim(), out h); }
            string modo = p.Length > 4 ? p[4].Trim() : "";
            if (p.Length > 5) int.TryParse(p[5].Trim(), out espera);

            bool eraVisible = Visible;
            var posAntes = Location; var tamAntes = Size; int tabAntes = pestanas.Activa;
            try
            {
                if (!eraVisible)
                {
                    permitirVisible = true;
                    StartPosition = FormStartPosition.Manual;
                    Location = new Point(20000, 20000);
                    if (w > S(400) && h > S(300)) Size = new Size(w, h);
                    Win32.ShowWindow(Handle, Win32.SW_SHOWNOACTIVATE);
                }
                pestanas.Activa = Math.Max(0, Math.Min(pestanas.Nombres.Length - 1, tab));
                Acomodar(); RefrescarPantalla(); RefrescarVigia(); Invalidate(true);
                if (modo.Length > 0 && pestanas.Activa > 0 && pestanas.Activa - 1 < pantallas.Length)
                    pantallas[pestanas.Activa - 1].Modo(modo);
                Application.DoEvents();
                // bombear mensajes un rato: las vistas que cargan de fondo (historial, cruces) llegan tarde
                var hasta = DateTime.Now.AddMilliseconds(Math.Max(0, Math.Min(30000, espera)));
                while (DateTime.Now < hasta) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                if (espera > 0) { RefrescarPantalla(); RefrescarVigia(); Invalidate(true); Application.DoEvents(); }
                using (var bmp = new Bitmap(Math.Max(1, Width), Math.Max(1, Height)))
                {
                    DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                    string dir = Path.GetDirectoryName(salida);
                    if (dir.Length > 0) Directory.CreateDirectory(dir);
                    bmp.Save(salida, System.Drawing.Imaging.ImageFormat.Png);
                }
                log.Info($"--foto: pestaña «{pestanas.Nombres[pestanas.Activa]}» ({Width}x{Height}) → {salida}");
            }
            catch (Exception ex) { try { log.Error("--foto falló: " + ex.Message); } catch { } }
            finally
            {
                try
                {
                    // el --modo era para la foto: la pantalla vuelve a como estaba (sin vista en grande, sin karaoke simulado)
                    if (modo.Length > 0 && pestanas.Activa > 0 && pestanas.Activa - 1 < pantallas.Length) pantallas[pestanas.Activa - 1].Modo("");
                    pestanas.Activa = tabAntes;
                    if (!eraVisible)
                    {
                        Win32.ShowWindow(Handle, Win32.SW_HIDE);
                        permitirVisible = false;
                        Size = tamAntes; Location = posAntes;
                    }
                    Acomodar(); RefrescarPantalla();
                }
                catch { }
                try { File.Delete(pedido); } catch { }
            }
        }

        /// <summary>--reintentar &lt;id&gt;: el id viene en un archivo (un mensaje de ventana no lleva texto). No bloquea.</summary>
        void PedidoReintentar()
        {
            string pedido = Path.Combine(Program.CarpetaDatos, "reintentar-pedido.txt");
            try
            {
                if (!File.Exists(pedido) || grabador == null) return;
                string id = File.ReadAllText(pedido).Trim();
                File.Delete(pedido);
                log.Info("Me piden reintentar (--reintentar): " + id);
                grabador.Reintentar(id);
            }
            catch (Exception ex) { log.Aviso("--reintentar: " + ex.Message); }
        }

        void CerrarDeVerdad()
        {
            cerrarDeVerdad = true;
            log.Info("Cerrando TeamsTools");
            Close();
        }

        void Hilo(Action a)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
        }

        /// <summary>Recuerda dónde y de qué tamaño dejaste la ventana (no guarda si está minimizada).</summary>
        void GuardarVentana()
        {
            try
            {
                if (WindowState != FormWindowState.Normal || Width < 600 || Height < 400) return;
                if (cfg.VentanaX == Left && cfg.VentanaY == Top && cfg.VentanaAncho == Width && cfg.VentanaAlto == Height) return;
                cfg.VentanaX = Left; cfg.VentanaY = Top; cfg.VentanaAncho = Width; cfg.VentanaAlto = Height;
                cfg.Guardar();
            }
            catch { }
        }

        void ActualizarInsignias()
        {
            int prog = recordatorios.Lista.Count(r => r.Activo && r.Proximo != null);
            int alertas = pPatrones != null ? pPatrones.ParaMirar : 0;
            int online = observador != null ? observador.Registro.Gente().Count(p => Estados.EsConectado(p.Presencia)) : 0;
            pestanas.Insignias = new[]
            {
                "",
                auto.Activo ? (auto.ChatsNoLeidos > 0 ? $"automático · {auto.ChatsNoLeidos}" : "automático") : "",
                prog > 0 ? prog.ToString() : "",
                personalizados.Lista.Count > 0 ? personalizados.Lista.Count.ToString() : "",
                guion != null && guion.Activo ? "guión" : "",
                radar != null && radar.Avisos.Count(a => a.Prioridad >= 70) > 0 ? radar.Avisos.Count(a => a.Prioridad >= 70).ToString() : "",
                grabador != null ? (grabador.Foto.Vivo != null ? "REC" : grabador.Foto.EnCola > 0 ? "…" : "") : "",
                alertas > 0 ? alertas.ToString() : "",
                online > 0 ? online.ToString() : "",
                "",
                presencia != null && presencia.Rendido ? "!" : "",
                chats != null && chats.Hay ? (chats.Mensajes.Count / 1000) + "k" : "",
            };
            pestanas.Invalidate();
        }

        readonly Dictionary<string, string> estadosGrabacion = new Dictionary<string, string>(StringComparer.Ordinal);
        string avisoLlamadasId = "";
        DateTime avisoLlamadasHasta = DateTime.MinValue;

        /// <summary>
        /// Cada cambio del grabador: las insignias, el icono de la bandeja (malva que se llena mientras archiva,
        /// separa voces, transcribe o resume) y el aviso de «transcripción lista» cuando una grabación termina su
        /// recorrido. O(grabaciones), y el icono solo se redibuja si cambió lo que muestra.
        /// </summary>
        void AlCambiarGrabador()
        {
            ActualizarInsignias();
            if (grabador == null || bandeja == null) return;
            var f = grabador.Foto;
            var activa = f.Todas.Where(x => x.Estado == EstadoGrab.Comprimiendo || x.Estado == EstadoGrab.Transcribiendo || x.Estado == EstadoGrab.Resumiendo)
                                .OrderBy(x => x.Desde).FirstOrDefault();
            bandeja.PonerProceso(activa == null ? null : new Bandeja.Proceso
            {
                Reunion = activa.Reunion,
                Que = activa.Estado == EstadoGrab.Comprimiendo ? "archivando" : activa.Estado == EstadoGrab.Resumiendo ? "resumiendo"
                    : activa.Etapa.StartsWith("separando", StringComparison.Ordinal) ? "separando las voces" : "transcribiendo",
                Progreso = activa.Progreso,
                EnPausa = activa.EnPausa,
            });
            if (vista != null && vigia != null) bandeja.Actualizar(vista, vigia.Pausado, cfg.Simulacion, cfg.PresenciaSiempreOnline);
            foreach (var g in f.Todas)
            {
                // «lista» viniendo de la cola = terminó ahora (no al arrancar la app, que la encuentra ya lista)
                if (estadosGrabacion.TryGetValue(g.Id, out var antes) && antes != g.Estado && g.Estado == EstadoGrab.Lista && EstadoGrab.EnCola(antes) && g.Palabras > 0)
                {
                    log.Ok($"Transcripción lista: «{g.Reunion}» · {g.Palabras:N0} palabras");
                    if (cfg.Notificaciones)
                    {
                        bandeja.Aviso("Transcripción lista", $"«{g.Reunion}» · {g.Palabras:N0} palabras · tocá acá para leerla");
                        avisoLlamadasId = g.Id;
                        avisoLlamadasHasta = DateTime.Now.AddSeconds(30);
                    }
                }
                estadosGrabacion[g.Id] = g.Estado;
            }
        }

        /// <summary>
        /// Pone al día el indicador global. Muestra la tarea más vieja (la que de verdad está tardando) y, si hay
        /// varias, cuántas más. Sin esto la app se ve quieta y no se distingue «terminó» de «se colgó».
        /// </summary>
        void ActualizarCarga()
        {
            var t = Tareas.Principal;
            int n = Tareas.Cuantas;
            if (t == null)
            {
                if (cargaGlobal.Activo) { cargaGlobal.Activo = false; cargaGlobal.Texto = ""; cargaGlobal.Visible = false; }
                lineaCarga.Activo = false;
                return;
            }
            lineaCarga.Acento = t.Tinte;
            lineaCarga.Progreso = t.Progreso;
            lineaCarga.Activo = true;
            lineaCarga.BringToFront();
            cargaGlobal.Acento = t.Tinte;
            cargaGlobal.Desde = t.Desde;
            cargaGlobal.Progreso = t.Progreso;
            cargaGlobal.Modo = t.Progreso.HasValue ? Cargador.Estilo.Progreso : Cargador.Estilo.Ondas;
            cargaGlobal.Texto = t.Que + (t.Detalle.Length > 0 ? " · " + t.Detalle : "") + (n > 1 ? $"  (+{n - 1})" : "");
            cargaGlobal.Visible = true;
            cargaGlobal.Activo = true;
            cargaGlobal.BringToFront();
        }

        void RefrescarPantalla()
        {
            using (Migas.Poner("refrescar pestaña «" + pestanas.Nombres[pestanas.Activa] + "»"))
                RefrescarPantallaYa();
        }

        void RefrescarPantallaYa()
        {
            if (pestanas.Activa == 1) pMensajes.Refrescar();
            else if (pestanas.Activa == 2) pCron.Refrescar();
            else if (pestanas.Activa == 3)
            {
                var ch = observador.Ultimos.Count > 0 ? observador.Ultimos : auto.UltimosChats;
                if (ch.Count > 0) pPers.PonerPresencias(ch); else pPers.Refrescar();
            }
            else if (pestanas.Activa == 4) pPerfil.Refrescar();
            else if (pestanas.Activa == 5) { pIA.Refrescar(); ActualizarInsignias(); }
            else if (pestanas.Activa == 6) pLlamadas.Refrescar();
            else if (pestanas.Activa == 7) { pPatrones.Refrescar(); ActualizarInsignias(); }
            else if (pestanas.Activa == 8) pEquipo.Refrescar();
            else if (pestanas.Activa == 9) pDia.Refrescar();
            else if (pestanas.Activa == 10) pSalud.Refrescar();
            else if (pestanas.Activa == 11) pHistoria.Refrescar();
        }

        const int IdAtajo = 0x5A11;
        bool atajoPuesto;

        const int WM_MOUSEWHEEL = 0x020A;

        /// <summary>El control más profundo que está bajo ese punto de pantalla.</summary>
        static Control BajoElMouse(Control raiz, Point pantalla)
        {
            Control actual = raiz;
            for (int i = 0; i < 12; i++)
            {
                Control hijo = null;
                try { hijo = actual.GetChildAtPoint(actual.PointToClient(pantalla)); } catch { }
                if (hijo == null) return actual;
                actual = hijo;
            }
            return actual;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Program.MsgMostrar) { Mostrar(); return; }
            if (m.Msg == Program.MsgFoto) { TomarFoto(); return; }
            if (m.Msg == Program.MsgCerrar) { log.Info("Me piden cerrar (--cerrar): cierro ordenado"); CerrarDeVerdad(); return; }
            if (m.Msg == Program.MsgReintentar) { PedidoReintentar(); return; }
            if (m.Msg == WM_MOUSEWHEEL)
            {
                // la rueda va al control que está BAJO el mouse, sin que nadie robe el foco del teclado
                long lp = m.LParam.ToInt64();
                var pantalla = new Point((short)(lp & 0xffff), (short)((lp >> 16) & 0xffff));
                var c = BajoElMouse(this, pantalla);
                while (c != null && !(c is IRueda)) c = c.Parent;
                if (c is IRueda r) { r.Rueda((short)((m.WParam.ToInt64() >> 16) & 0xffff)); return; }
            }
            if (m.Msg == Win32.WM_HOTKEY && m.WParam.ToInt32() == IdAtajo)
            {
                bool on = !auto.Encendido;
                auto.PonerModoAutomatico(on, "atajo Ctrl+Alt+C");
                bandeja.Automatico = on;
                bandeja.Aviso(on ? "Modo automático encendido" : "Modo automático apagado", on ? "Contesto solo los chats que lleguen" : "Vuelvo a no contestar nada");
                if (cfg.Sonido) Sonidos.Tic();
                ActualizarInsignias();
                if (pestanas.Activa == 1) pMensajes.Refrescar();
                return;
            }
            if (m.Msg == Win32.WM_NCLBUTTONDBLCLK) return;
            if (m.Msg == Win32.WM_NCHITTEST)
            {
                long lp = m.LParam.ToInt64();
                var p = PointToClient(new Point((short)(lp & 0xffff), (short)((lp >> 16) & 0xffff)));
                int b = S(7);
                bool izq = p.X < b, der = p.X >= Width - b, arr = p.Y < b, aba = p.Y >= Height - b;
                int ht = Win32.HTCLIENT;
                if (arr && izq) ht = Win32.HTTOPLEFT; else if (arr && der) ht = Win32.HTTOPRIGHT;
                else if (aba && izq) ht = Win32.HTBOTTOMLEFT; else if (aba && der) ht = Win32.HTBOTTOMRIGHT;
                else if (izq) ht = Win32.HTLEFT; else if (der) ht = Win32.HTRIGHT; else if (arr) ht = Win32.HTTOP; else if (aba) ht = Win32.HTBOTTOM;
                else if (rTitulo.Contains(p) && !rMin.Contains(p) && !rCerrar.Contains(p)) ht = Win32.HTCAPTION;
                m.Result = (IntPtr)ht;
                return;
            }
            if (m.Msg == 0x02E0) // WM_DPICHANGED
            {
                esc = (int)(m.WParam.ToInt64() & 0xffff) / 96f;
                try
                {
                    var r = Marshal.PtrToStructure<Win32.RECT>(m.LParam);
                    Bounds = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                }
                catch { }
                MinimumSize = new Size(Dpi.Bruto(esc, 900), Dpi.Bruto(esc, 580));
                Acomodar();
                Invalidate();
                return;
            }
            base.WndProc(ref m);
        }

        // ------------------------------------------------------------ chips

        Chip Nuevo(string texto, Chip.Modo modo, Color acento, EventHandler accion, string sub = "")
        {
            var ch = new Chip { Text = texto, Tipo = modo, Acento = acento, Sub = sub };
            ch.Accion += accion;
            Controls.Add(ch);
            chips.Add(ch);
            return ch;
        }

        void CrearChips()
        {
            chGracia = Nuevo("gracia", Chip.Modo.Valor, Tema.Durazno, (o, e) => Ciclar(chGracia, PresetsGracia, +1), Seg(cfg.GraciaSegundos));
            chGracia.AccionDerecha += (o, e) => Ciclar(chGracia, PresetsGracia, -1);
            chIntervalo = Nuevo("lectura cada", Chip.Modo.Valor, Tema.Cielo, (o, e) => Ciclar(chIntervalo, PresetsIntervalo, +1), Seg(cfg.IntervaloSegundos));
            chIntervalo.AccionDerecha += (o, e) => Ciclar(chIntervalo, PresetsIntervalo, -1);
            chPosponer = Nuevo("quedarme", Chip.Modo.Valor, Tema.Malva, (o, e) => Ciclar(chPosponer, PresetsPosponer, +1), Min(cfg.PosponerMinutos));
            chPosponer.AccionDerecha += (o, e) => Ciclar(chPosponer, PresetsPosponer, -1);
            chNadie = Nuevo("si nadie llega", Chip.Modo.Valor, Tema.Crema, (o, e) => Ciclar(chNadie, PresetsNadie, +1), NadieTxt(cfg.SalirSiNadieLlegaMinutos));
            chNadie.AccionDerecha += (o, e) => Ciclar(chNadie, PresetsNadie, -1);

            chSimulacion = Nuevo("simulación", Chip.Modo.Toggle, Tema.Crema, (o, e) => AlternarToggle(chSimulacion)); chSimulacion.Activo = cfg.Simulacion;
            chPresencia = Nuevo("siempre Disponible", Chip.Modo.Toggle, Tema.Cyan, (o, e) => AlternarToggle(chPresencia)); chPresencia.Activo = cfg.PresenciaSiempreOnline;
            chSonido = Nuevo("sonido", Chip.Modo.Toggle, Tema.Cyan, (o, e) => AlternarToggle(chSonido)); chSonido.Activo = cfg.Sonido;
            chAvisos = Nuevo("avisos", Chip.Modo.Toggle, Tema.Cyan, (o, e) => AlternarToggle(chAvisos)); chAvisos.Activo = cfg.Notificaciones;
            chCuenta = Nuevo("cuenta regresiva", Chip.Modo.Toggle, Tema.Durazno, (o, e) => AlternarToggle(chCuenta)); chCuenta.Activo = cfg.MostrarCuentaRegresiva;
            chRoster = Nuevo("verificar con Gente", Chip.Modo.Toggle, Tema.Salvia, (o, e) => AlternarToggle(chRoster)); chRoster.Activo = cfg.VerificarConRoster;
            chInicio = Nuevo("inicio con Windows", Chip.Modo.Toggle, Tema.Malva, (o, e) => AlternarToggle(chInicio)); chInicio.Activo = cfg.IniciarConWindows;
            chMinimizado = Nuevo("arrancar en bandeja", Chip.Modo.Toggle, Tema.Malva, (o, e) => AlternarToggle(chMinimizado)); chMinimizado.Activo = cfg.IniciarMinimizado;
            chHorario = Nuevo("horario laboral", Chip.Modo.Toggle, Tema.Cielo, (o, e) => AlternarToggle(chHorario), cfg.HorarioTexto); chHorario.Activo = cfg.HorarioActivo;

            chPausa = Nuevo("pausar", Chip.Modo.Boton, Tema.Malva, (o, e) => AlternarPausa());
            chLeer = Nuevo("leer ahora", Chip.Modo.Boton, Tema.Cielo, (o, e) => vigia.LeerYa());
            chSalir = Nuevo("salir de la reunión", Chip.Modo.Boton, Tema.Rosa, (o, e) => SalirManual());
            chVolcar = Nuevo("volcar árbol UIA", Chip.Modo.Boton, Tema.TextoSuave, (o, e) => Volcar());
            chCarpeta = Nuevo("abrir carpeta de datos", Chip.Modo.Boton, Tema.TextoSuave, (o, e) => AbrirCarpeta());
        }

        static string Seg(int s) => s + " s";
        static string Min(int m) => m + " min";
        static string NadieTxt(int m) => m == 0 ? "apagado" : m + " min";

        void Ciclar(Chip ch, int[] presets, int dir)
        {
            int actual = ch == chGracia ? cfg.GraciaSegundos : ch == chIntervalo ? cfg.IntervaloSegundos : ch == chPosponer ? cfg.PosponerMinutos : cfg.SalirSiNadieLlegaMinutos;
            int idx = Array.IndexOf(presets, actual);
            if (idx < 0) { idx = 0; for (int i = 0; i < presets.Length; i++) if (presets[i] <= actual) idx = i; }
            idx = (idx + dir + presets.Length) % presets.Length;
            int nuevo = presets[idx];
            if (ch == chGracia) { cfg.GraciaSegundos = nuevo; ch.Poner("gracia", Seg(nuevo)); log.Info($"Gracia: {nuevo} s antes de salir"); }
            else if (ch == chIntervalo) { cfg.IntervaloSegundos = nuevo; ch.Poner("lectura cada", Seg(nuevo)); log.Info($"Lectura cada {nuevo} s"); vigia.LeerYa(); }
            else if (ch == chPosponer) { cfg.PosponerMinutos = nuevo; ch.Poner("quedarme", Min(nuevo)); log.Info($"'Quedarme' pospone {nuevo} min"); }
            else { cfg.SalirSiNadieLlegaMinutos = nuevo; ch.Poner("si nadie llega", NadieTxt(nuevo)); log.Info(nuevo == 0 ? "Si nadie llega: no salgo nunca por eso" : $"Si nadie llega en {nuevo} min, salgo igual"); }
            cfg.Guardar();
            Acomodar();
        }

        void AlternarToggle(Chip ch)
        {
            ch.Activo = !ch.Activo;
            if (ch == chSimulacion) { cfg.Simulacion = ch.Activo; log.Aviso(ch.Activo ? "SIMULACIÓN activada: aviso pero no salgo de verdad" : "Simulación apagada: salida REAL"); }
            else if (ch == chPresencia) { cfg.PresenciaSiempreOnline = ch.Activo; log.Info(ch.Activo ? "Presencia: me mantengo Disponible aunque no toque nada" : "Presencia: Teams decide solo (Ausente a los 5 min sin actividad)"); presencia.Despertar(); }
            else if (ch == chSonido) cfg.Sonido = ch.Activo;
            else if (ch == chAvisos) cfg.Notificaciones = ch.Activo;
            else if (ch == chCuenta) cfg.MostrarCuentaRegresiva = ch.Activo;
            else if (ch == chRoster) { cfg.VerificarConRoster = ch.Activo; log.Info(ch.Activo ? "Antes de salir verifico con el panel Gente" : "Sin verificación con el panel Gente"); }
            else if (ch == chInicio) { cfg.IniciarConWindows = ch.Activo; Autoarranque.Poner(ch.Activo, Application.ExecutablePath); log.Info(ch.Activo ? "Arranca con Windows (HKCU Run)" : "Ya no arranca con Windows"); }
            else if (ch == chMinimizado) cfg.IniciarMinimizado = ch.Activo;
            else if (ch == chHorario) { cfg.HorarioActivo = ch.Activo; chHorario.Poner("horario laboral", cfg.HorarioTexto); presencia.Despertar(); auto.Despertar(); log.Info(ch.Activo ? $"Horario laboral activo: {cfg.HorarioTexto} · fuera de ahí no sostengo presencia ni contesto" : "Horario laboral apagado: activo siempre"); Acomodar(); }
            cfg.Guardar();
            ch.Invalidate();
            bandeja.Actualizar(vista, vigia.Pausado, cfg.Simulacion, cfg.PresenciaSiempreOnline);
            Invalidate();
        }

        void AlternarPausa()
        {
            vigia.Pausar(!vigia.Pausado);
            chPausa.Poner(vigia.Pausado ? "reanudar" : "pausar");
            Acomodar();
        }

        void SalirManual()
        {
            if (!chSalir.Armado) { chSalir.Armado = true; armadoHasta = DateTime.Now.AddSeconds(4); chSalir.Invalidate(); return; }
            chSalir.Armado = false; chSalir.Invalidate();
            log.Info("Panel: salir de la reunión ahora");
            vigia.SalirAhora();
        }

        void Volcar()
        {
            log.Info("Volcando el árbol UIA de las ventanas de Teams…");
            var carpeta = Path.Combine(Program.CarpetaDatos, "volcados");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var archivos = scanner.Volcar(carpeta);
                log.Ok($"Volcado listo: {archivos.Count} archivo/s en {carpeta}");
                try { if (archivos.Count > 0) Process.Start("explorer.exe", carpeta); } catch { }
            });
        }

        void AbrirCarpeta()
        {
            try { Process.Start("explorer.exe", Program.CarpetaDatos); } catch (Exception ex) { log.Aviso("No pude abrir la carpeta: " + ex.Message); }
        }

        // ------------------------------------------------------------ eventos del vigia

        void AplicarVista(Vista v)
        {
            vista = v;
            var L = v.Lectura;
            if (L != null && L.Hora != ultimaHoraLectura)
            {
                ultimaHoraLectura = L.Hora;
                historia.Add((L.Hora, L.HayLlamada ? L.Otros : 0, L.HayLlamada));
                while (historia.Count > 400) historia.RemoveAt(0);
                // el grabador mira cada lectura: arranca al entrar a la reunión y corta al salir.
                // ⭐ segundo disparador: si tu presencia nativa dice que estás en una llamada pero el scanner no
                //    vio la ventana (minimizada, compacta, o el árbol sin montar), igual grabamos — loopcap
                //    captura el audio del sistema, no necesita ver la reunión. Así no se pierde ninguna call.
                bool fresca = presencia != null && presencia.MedicionFresca;
                string pres = fresca ? presencia.PresenciaTeams : "";
                bool enLlamadaPorPresencia = fresca && Estados.EsEnLlamada(pres);
                // ⭐ «saliste de verdad»: tu presencia nativa dice Disponible/Ausente/Sin conexión → el grabador corta
                //    con gracia corta (8 s) en vez de la larga (90 s) que protege del parpadeo de la lectura
                bool presenciaLibre = fresca && (Estados.EsDisponible(pres) || Estados.EsAusente(pres) || Estados.EsDesconectado(pres));
                try { if (grabador != null) grabador.Mirar(L, v.GenteVista, enLlamadaPorPresencia, presenciaLibre); } catch { }
                // el vigía del micrófono mira el botón de ESTA ventana; si el botón no dice nada, tu ficha es el respaldo
                hwndLlamada = L.HayLlamada ? L.Hwnd : IntPtr.Zero;
                if (grabador != null && vigiaMic != null && vigiaMic.Estado == null && L.HayLlamada && L.PropioVisto)
                    grabador.MicDeTeams(L.PropioSilenciado);
                // ⭐ con vos adentro, el roster DA los nombres: no hay nada que inferir. Se anota como
                //    verdad de campo y después le gana al corrillo que se haya deducido de la presencia.
                try
                {
                    if (L.HayLlamada && L.Nombres.Count > 0 && (DateTime.Now - ultimoRoster).TotalSeconds >= 60)
                    {
                        ultimoRoster = DateTime.Now;
                        string reunion = L.Reunion, propio = L.NombrePropio;
                        if (propio.Length > 0 && chat.MiNombre.Length == 0) chat.MiNombre = propio;   // lo aprendió de tu ficha
                        var nombres = L.Nombres.ToList();
                        ThreadPool.QueueUserWorkItem(_ => { try { Corrillos.Anotar(rutaCorrillos, reunion, nombres, propio); } catch { } });
                    }
                }
                catch { }
            }
            chPausa.Text = vigia.Pausado ? "reanudar" : "pausar";
            bandeja.Actualizar(v, vigia.Pausado, cfg.Simulacion, cfg.PresenciaSiempreOnline);
            if (v.Estado == Estado.SalaVacia || v.Estado == Estado.Saliendo) { if (cuenta.Visible) cuenta.Actualizar(v); }
            else cuenta.Ocultar();
            Invalidate();
        }

        void AlVaciarse(Vista v)
        {
            if (cfg.Sonido) Sonidos.Aviso();
            if (cfg.Notificaciones) bandeja.Aviso("La sala se vació", $"Salgo de «{v.Lectura?.Reunion}» en {cfg.GraciaSegundos} s" + (cfg.Simulacion ? " (simulación)" : ""));
            if (cfg.MostrarCuentaRegresiva) cuenta.Mostrar(v, cfg.GraciaSegundos, cfg.PosponerMinutos);
        }

        void AlSalir(Vista v, bool ok, string detalle)
        {
            cuenta.Ocultar();
            string reunion = v.Lectura?.Reunion ?? "";
            if (ok)
            {
                if (cfg.Sonido) Sonidos.Adios();
                if (cfg.Notificaciones) bandeja.Aviso(detalle == "simulación" ? "SIMULACIÓN: habría salido" : "Salí de la reunión", reunion);
            }
            else if (cfg.Notificaciones) bandeja.Aviso("No pude salir de la reunión", detalle, ToolTipIcon.Warning);
            Invalidate();
        }

        void Latido()
        {
            using (Migas.Poner("latido del panel"))
                LatidoYa();
            var vivo = grabador?.Foto.Vivo;                // el REC de la barra de título: prendido mientras se graba
            indicadorRec.Poner(vivo != null, vivo?.Reunion ?? "");
        }

        void LatidoYa()
        {
            panelVigiaVisible = Visible && pestanas.Activa == 0;
            if (chSalir.Armado && DateTime.Now > armadoHasta) { chSalir.Armado = false; chSalir.Invalidate(); }
            if (vista.Estado == Estado.SalaVacia && vista.VacioDesde != null)
            {
                vista.SegundosRestantes = Math.Max(0, cfg.GraciaSegundos - (int)(DateTime.Now - vista.VacioDesde.Value).TotalSeconds);
                if (cuenta.Visible) cuenta.Actualizar(vista);
                bandeja.Actualizar(vista, vigia.Pausado, cfg.Simulacion, cfg.PresenciaSiempreOnline);
                if (cfg.Sonido && vista.SegundosRestantes > 0 && vista.SegundosRestantes <= 5) Sonidos.Tic();
            }
            if (Visible && pestanas.Activa == 0) Invalidate(new Rectangle(0, rHero.Top, Width, rLogTitulo.Bottom - rHero.Top));
            bool hubo = presencia.Toques != toquesVistos;
            if (hubo) toquesVistos = presencia.Toques;
            int idle = presencia.IdleSegundos;
            if (demo) idle = IdleDemo(tics, out hubo);   // la demo no sostiene la presencia: dibuja el diente de sierra típico
            sIdle.Empujar(idle, hubo);
            tics++;   // 🚨 fuera del if: si no, en las pestañas nuevas nunca avanzaría y no se refrescarían
            if (Visible && pestanas.Activa == 0 && tics % 2 == 0) LlenarFichas();
            // las pestañas de datos se refrescan solas, más espaciado: son más caras de armar
            else if (Visible && pestanas.Activa >= 4 && tics % 10 == 0) RefrescarPantalla();
        }

        /// <summary>La inactividad de la demo: sube un segundo por segundo hasta el umbral y cae a cero con un «toque», como en la vida real.</summary>
        static int IdleDemo(int tic, out bool toque)
        {
            const int periodo = 61;
            int i = tic % periodo;
            toque = i == 0 && tic > 0;
            return i;
        }

        static string Dur(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{t.Seconds} s";

        /// <summary>
        /// Reparte las pastillas en filas PAREJAS. El flujo codicioso de antes llenaba cada fila hasta el borde y
        /// dejaba la última con una o dos pastillas sueltas: al achicar la ventana la barra quedaba desprolija,
        /// que es justo lo que se ve feo. Acá se calcula cuántas filas hacen falta como mínimo y se reparte el
        /// ancho en partes iguales entre esas filas, respetando el orden y sin pasarse nunca del ancho real.
        /// </summary>
        static List<List<Chip>> Repartir(IList<Chip> grupo, int anchoDisp, int gap)
        {
            if (grupo == null || grupo.Count == 0) return new List<List<Chip>> { new List<Chip>() };
            int total = grupo.Sum(c => c.Width + gap) - gap;
            int masAncha = grupo.Max(c => c.Width);
            int filas = Math.Max(1, (int)Math.Ceiling(total / (double)Math.Max(1, anchoDisp)));
            for (int intento = 0; intento < 6; intento++)
            {
                int objetivo = Math.Max(masAncha, (int)((total - gap * (filas - 1)) / (double)filas * (1 + intento * 0.06)));
                var r = Empaquetar(grupo, anchoDisp, gap, objetivo);
                if (r.Count <= filas) return r;
            }
            return Empaquetar(grupo, anchoDisp, gap, anchoDisp);
        }

        static List<List<Chip>> Empaquetar(IList<Chip> grupo, int anchoDisp, int gap, int objetivo)
        {
            var res = new List<List<Chip>> { new List<Chip>() };
            int x = 0;
            foreach (var ch in grupo)
            {
                int conEste = x == 0 ? ch.Width : x + gap + ch.Width;
                // salta de fila si no entra de verdad, o si ya pasó el ancho objetivo de esta fila
                if (x > 0 && (conEste > anchoDisp || conEste > objetivo)) { res.Add(new List<Chip>()); x = 0; conEste = ch.Width; }
                res[res.Count - 1].Add(ch);
                x = conEste;
            }
            return res;
        }

        /// <summary>
        /// Ubica las 6 tarjetas: una fila cuando hay lugar, dos filas de 3 cuando la ventana es angosta.
        /// En dos filas cada fila reparte su propio ancho entre sus 3 pesos, así ninguna queda desproporcionada.
        /// </summary>
        static Rectangle[] RectsTarjetas(Rectangle area, bool dosFilas, int gap)
        {
            var res = new Rectangle[6];
            if (!dosFilas)
            {
                float unidad = (area.Width - gap * 5) / PesosTarjetas.Sum();
                float x = area.Left;
                for (int i = 0; i < 6; i++)
                {
                    int w = (int)Math.Round(unidad * PesosTarjetas[i]);
                    res[i] = new Rectangle((int)x, area.Top, w, area.Height);
                    x += w + gap;
                }
                return res;
            }
            int alto = (area.Height - gap) / 2;
            for (int fila = 0; fila < 2; fila++)
            {
                float suma = PesosTarjetas[fila * 3] + PesosTarjetas[fila * 3 + 1] + PesosTarjetas[fila * 3 + 2];
                float unidad = (area.Width - gap * 2) / suma;
                float x = area.Left;
                int y = area.Top + fila * (alto + gap);
                for (int c = 0; c < 3; c++)
                {
                    int i = fila * 3 + c;
                    int w = c == 2 ? area.Right - (int)x : (int)Math.Round(unidad * PesosTarjetas[i]);
                    res[i] = new Rectangle((int)x, y, w, alto);
                    x += w + gap;
                }
            }
            return res;
        }

        static Color ColorPresencia(string p)
        {
            if (Estados.EsDisponible(p)) return Tema.Salvia;
            if (Estados.EsAusente(p)) return Tema.Durazno;
            if (Estados.EsOcupado(p)) return Tema.Rosa;
            if (Estados.EsDesconectado(p)) return Tema.MuyApagado;
            return Tema.TextoSuave;
        }

        /// <summary>Antigüedad compacta para una columna angosta: «45s», «12m», «4h50», «2d». Fmt() da «4:50:58» y no entra.</summary>
        static string Hace(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalSeconds < 0) return "recién";
            if (d.TotalSeconds < 60) return (int)d.TotalSeconds + "s";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + "m";
            if (d.TotalHours < 24) return (int)d.TotalHours + "h" + d.Minutes.ToString("00");
            return (int)d.TotalDays + "d";
        }

        /// <summary>Apellido solo: en una columna angosta «Ferreyra, Ramiro» no entra y «Ferreyra» alcanza para reconocerlo.</summary>
        static string Corto(string nombre)
        {
            if (string.IsNullOrEmpty(nombre)) return "—";
            int c = nombre.IndexOf(',');
            return (c > 0 ? nombre.Substring(0, c) : nombre).Trim();
        }

        /// <summary>
        /// Llena la cronología con los cambios de presencia REALES: los míos salen del log nativo de Teams y los de
        /// los demás del observador de la lista de chats. Es lo que llena la media columna que el log de eventos
        /// deja vacía en un día tranquilo, y es dato de verdad, no relleno.
        /// </summary>
        void LlenarCronologia()
        {
            var filas = new List<FilaTabla>();
            try
            {
                foreach (var e in logTeams.Eventos)
                    filas.Add(FilaTabla.F(e, ColorPresencia(e.Estado),
                        Celda.C(e.Cuando.ToString("HH:mm"), Tema.Apagado),
                        Celda.C("yo", Tema.Cyan, true),
                        Celda.C(e.Estado, ColorPresencia(e.Estado)),
                        Celda.C(Hace(e.Cuando), Tema.TextoSuave)));

                foreach (var m in observador.Registro.Movimientos())
                    filas.Add(FilaTabla.F(m, ColorPresencia(m.Hasta),
                        Celda.C(m.Hora.ToString("HH:mm"), Tema.Apagado),
                        Celda.C(Corto(m.Persona), Tema.Texto),
                        Celda.C(m.Hasta.Length > 0 ? m.Hasta : "—", ColorPresencia(m.Hasta)),
                        Celda.C(Hace(m.Hora), Tema.TextoSuave)));
            }
            catch { }

            filas = filas.OrderByDescending(f => f.Tag is EventoPresencia ? ((EventoPresencia)f.Tag).Cuando : ((Movimiento)f.Tag).Hora)
                         .Take(120).ToList();
            int mios = filas.Count(f => f.Tag is EventoPresencia);
            tCrono.Poner(filas, null, filas.Count == 0 ? "" : $"{mios} míos · {filas.Count - mios} del equipo");
        }

        void LlenarFichas()
        {
            var L = vista.Lectura ?? new Lectura();
            fSistema.Poner(
                Dato.D("corriendo hace", Dur(DateTime.Now - arranque), Tema.Texto),
                Dato.D("estado del vigía", vigia.Pausado ? "en pausa" : vista.EstadoTexto.ToLowerInvariant(), vigia.Pausado ? Tema.Malva : ColorEstado(vista.Estado)),
                Dato.D("lecturas de la reunión", historia.Count.ToString(), Tema.Cielo),
                Dato.D("última lectura", L.Ms + " ms", L.Ms > 800 ? Tema.Durazno : Tema.Salvia),
                Dato.D("intervalo · gracia", $"{cfg.IntervaloSegundos} s · {cfg.GraciaSegundos} s", Tema.TextoSuave),
                Dato.D("método de salida", cfg.MetodoSalida + (cfg.VerificarConRoster ? " + Gente" : ""), Tema.TextoSuave),
                Dato.D("si nadie llega", cfg.SalirSiNadieLlegaMinutos == 0 ? "no salgo" : cfg.SalirSiNadieLlegaMinutos + " min", Tema.TextoSuave),
                Dato.D("salidas en esta sesión", vista.Salidas.ToString(), vista.Salidas > 0 ? Tema.Salvia : Tema.Apagado),
                Dato.D("simulación", cfg.Simulacion ? "SÍ (no sale)" : "no", cfg.Simulacion ? Tema.Crema : Tema.Apagado),
                Dato.Titulo("esta reunión"),
                Dato.D("máximo de gente", vista.MaxOtros.ToString(), Tema.Cyan),
                Dato.D("ahora hay", L.HayLlamada ? L.Otros.ToString() : "—", L.Otros > 0 ? Tema.Cyan : Tema.Durazno),
                Dato.D("duración", L.Duracion.Length > 0 ? L.Duracion : "—", Tema.TextoSuave),
                Dato.D("página de la galería", L.Pagina.Length > 0 ? L.Pagina : "—", Tema.TextoSuave),
                Dato.D("alguien comparte", L.HayCompartido ? "sí" : "no", L.HayCompartido ? Tema.Crema : Tema.Apagado),
                Dato.D("mi ficha", L.PropioVisto ? (L.NombrePropio.Length > 0 ? L.NombrePropio : "vista") : "no", L.PropioVisto ? Tema.Salvia : Tema.Apagado));

            var prox = recordatorios.Proximos().FirstOrDefault();
            var reglaTop = reglas.Reglas.OrderByDescending(r => r.Usos).FirstOrDefault(r => r.Usos > 0);
            fMensajeria.Poner(
                Dato.D("modo automático", auto.Activo ? auto.Estado : "apagado", auto.Activo ? Tema.Durazno : Tema.Apagado),
                Dato.D("chats sin leer", auto.UltimaLectura != null ? auto.ChatsNoLeidos.ToString() : "—", auto.ChatsNoLeidos > 0 ? Tema.Durazno : Tema.Apagado),
                Dato.D("lecturas de chats", auto.Lecturas.ToString(), Tema.TextoSuave),
                Dato.D("última", auto.UltimaLectura != null ? Tema.Relativo(auto.UltimaLectura.Value) + $" · {auto.UltimaLecturaMs} ms" : "nunca", Tema.TextoSuave),
                Dato.D("respuestas hoy", auto.RespuestasHoy.ToString(), auto.RespuestasHoy > 0 ? Tema.Salvia : Tema.Apagado),
                Dato.D("regla top", reglaTop != null ? $"{reglaTop.Nombre} ({reglaTop.Usos})" : "ninguna todavía", Tema.Malva),
                Dato.D("reglas activas", $"{reglas.Reglas.Count(r => r.Activa)}/{reglas.Reglas.Count}", Tema.Malva),
                Dato.D("enfriamiento", reglas.EnfriamientoGeneralMinutos + " min", Tema.TextoSuave),
                Dato.D("errores", auto.Errores.ToString(), auto.Errores > 0 ? Tema.Rosa : Tema.Apagado),
                Dato.Titulo("agenda"),
                Dato.D("programados", recordatorios.Lista.Count(r => r.Activo && r.Proximo != null).ToString(), Tema.Cielo),
                Dato.D("próximo", prox != null ? (prox.ParaMi ? "para mí" : prox.Para.Split(',')[0]) : "nada", prox != null ? Tema.Cielo : Tema.Apagado),
                Dato.D("cuándo", prox != null ? prox.ResumenCuando() : "—", Tema.TextoSuave),
                Dato.D("enviados · fallos", $"{recordatorios.Lista.Sum(r => r.Enviados)} · {recordatorios.Lista.Sum(r => r.Fallos)}", recordatorios.Lista.Sum(r => r.Fallos) > 0 ? Tema.Rosa : Tema.Salvia),
                Dato.D("mensajes propios", personalizados.Lista.Count + " · " + personalizados.Lista.Sum(p => p.Usos) + " usos", Tema.TextoSuave),
                Dato.D("contactos", contactos.Lista.Count.ToString(), Tema.TextoSuave));

            int idle = presencia.IdleSegundos;
            bool fresca = presencia.MedicionFresca;
            string teamsDice = fresca ? presencia.PresenciaTeams : "sin medir";
            var minHoy = logTeams.MinutosPorEstadoHoy();
            double minDisp = minHoy.ContainsKey("Disponible") ? minHoy["Disponible"] : 0;
            double minAus = minHoy.ContainsKey("Ausente") ? minHoy["Ausente"] : 0;
            double minTot = minHoy.Values.Sum();
            fPresencia.Poner(
                Dato.D("Teams dice que estás", teamsDice + (presencia.RespetandoManual ? " · lo pusiste vos, lo respeto" : presencia.Rendido ? " · no lo puedo corregir" : ""), !fresca ? Tema.Apagado : presencia.TeamsDiceDisponible ? Tema.Salvia : presencia.RespetandoManual ? Tema.Crema : presencia.TeamsDiceAusente ? Tema.Rosa : Tema.Apagado),
                Dato.D("así desde", fresca && presencia.PresenciaDesde.HasValue ? $"{presencia.PresenciaDesde.Value:HH:mm} · hace {Presencia.Fmt((int)(DateTime.Now - presencia.PresenciaDesde.Value).TotalSeconds)}" : "—", Tema.TextoSuave),
                Dato.D("cómo lo sé", (fresca && presencia.FuentePresencia.Length > 0 ? presencia.FuentePresencia : "todavía no pude") + (fresca ? " · leído " + Tema.Relativo(presencia.UltimaMedicion.Value) : ""), fresca ? (presencia.FuentePresencia == "log nativo" ? Tema.Cyan : Tema.Malva) : Tema.Apagado),
                Dato.D("detalle", presencia.Rendido && presencia.RescateDetalle.Length > 0 ? presencia.RescateDetalle : presencia.MedicionDetalle.Length > 0 ? presencia.MedicionDetalle : "—", presencia.Rendido ? Tema.Durazno : Tema.Apagado),
                Dato.D("hoy disponible · ausente", minTot >= 1 ? $"{Presencia.Fmt((int)(minDisp * 60))} · {Presencia.Fmt((int)(minAus * 60))}" : "—", minAus > minDisp ? Tema.Durazno : Tema.Salvia),
                Dato.D("cambios de estado hoy", logTeams.CambiosHoy.ToString(), logTeams.CambiosHoy > 6 ? Tema.Durazno : Tema.TextoSuave),
                Dato.D("condiciones", (presencia.Bloqueada ? "sesión BLOQUEADA" : "sesión abierta")
                                    + (cfg.HorarioActivo ? (presencia.EnHorario ? " · en horario" : " · FUERA de horario") : " · sin horario")
                                    + (presencia.PantallaDespierta ? " · pantalla sostenida" : " · pantalla libre"),
                    presencia.Bloqueada || (cfg.HorarioActivo && !presencia.EnHorario) ? Tema.Durazno : Tema.Salvia),
                Dato.Titulo("cómo lo sostengo"),
                Dato.D("estado", demo ? "demo" : presencia.Activa ? "manteniendo Disponible" : "Teams decide", demo ? Tema.MuyApagado : presencia.Activa ? Tema.Cyan : Tema.Crema),
                Dato.D("método", (cfg.PresenciaMetodo == "f15" ? "tecla fantasma F15" : cfg.PresenciaMetodo)
                                 + (presencia.MetodoQueFunciona.Length > 0 && presencia.MetodoQueFunciona != (cfg.PresenciaMetodo == "f15" ? "tecla fantasma F15" : cfg.PresenciaMetodo) ? " → " + presencia.MetodoQueFunciona : ""), Tema.Texto),
                Dato.D("sin tocar nada", Presencia.Fmt(idle), idle > 240 ? Tema.Durazno : Tema.Texto),
                Dato.D("próximo toque en", presencia.Bloqueada ? "—" : Presencia.Fmt(presencia.ProximoToqueEn), Tema.Cyan),
                Dato.D("margen antes de Ausente", idle >= 300 ? "vencido" : Presencia.Fmt(Math.Max(0, 300 - idle)), idle >= 240 ? Tema.Rosa : Tema.Salvia),
                Dato.Titulo("histórico"),
                Dato.D("toques", presencia.Toques + (presencia.UltimoToque.HasValue ? " · último " + Tema.Relativo(presencia.UltimoToque.Value) : " · todavía ninguno"), presencia.Toques > 0 ? Tema.Salvia : Tema.Apagado),
                Dato.D("resultado · fallos", (presencia.UltimoResultado.Length > 0 ? presencia.UltimoResultado : "—") + " · " + presencia.Fallos, presencia.Fallos > 0 ? Tema.Rosa : presencia.UltimoResultado.StartsWith("ok") ? Tema.Salvia : Tema.TextoSuave),
                Dato.D("derivas · corregidas · forzados", $"{presencia.Derivas} · {presencia.Correcciones} · {presencia.Forzados}" + (presencia.Sondas > 0 ? $" · {presencia.Sondas} sondas" : ""), presencia.Rendido ? Tema.Rosa : presencia.Derivas > presencia.Correcciones ? Tema.Durazno : Tema.Salvia),
                Dato.D("log de Teams", logTeams.Eventos.Count > 0 ? $"{logTeams.Eventos.Count} cambios · {logTeams.Archivo}" : (logTeams.Problema.Length > 0 ? logTeams.Problema : "sin leer todavía"), logTeams.Eventos.Count > 0 ? Tema.Cyan : Tema.Apagado));

            LlenarCronologia();

            LlenarVentanas();
        }

        /// <summary>La ficha «ventanas de Teams», pintada desde la última foto del sondeo. Cero I/O acá.</summary>
        void LlenarVentanas()
        {
            var f = sondeoTeams?.Ultimo;
            var vs = new List<Dato>();
            if (f == null) { vs.Add(Dato.D("mirando Teams", "…", Tema.Apagado)); fVentanas.Poner(vs); return; }
            if (f.Procesos > 0)
            {
                vs.Add(Dato.D("procesos", f.Procesos.ToString(), Tema.TextoSuave));
                vs.Add(Dato.D("ventanas web", f.Ventanas.Count.ToString(), Tema.TextoSuave));
                vs.Add(Dato.D("chat listo", f.ChatListo ? "sí" : "no", f.ChatListo ? Tema.Salvia : Tema.Durazno));
                if (f.ChatListo) vs.Add(Dato.D("chat abierto", f.ChatAbierto.Length > 0 ? f.ChatAbierto : "—", Tema.Cyan));
                vs.Add(Dato.Titulo("cada ventana"));
                foreach (var w in f.Ventanas.Take(6))
                {
                    string t = w.Titulo.Replace(" | Microsoft Teams", "");
                    if (t.Length > 26) t = t.Substring(0, 26) + "…";
                    string est = !w.Visible ? "en la bandeja" : w.Minimizada ? "minimizada" : $"{w.Ancho}x{w.Alto}";
                    vs.Add(Dato.D(t, est, !w.Visible ? Tema.Apagado : w.Minimizada ? Tema.Cyan : Tema.Crema));
                }
            }
            fVentanas.Poner(vs);
        }

        /// <summary>
        /// Corre en el hilo del sondeo, cada 2 s. Lo que antes hacía la UI: enumerar procesos (10-40 ms), leer
        /// las ventanas y, si el chat se perdió, BUSCARLO por UIA — ahora por la aduana y solo con el panel a la vista.
        /// </summary>
        FotoTeams MedirTeams()
        {
            if (demo) return FotoTeamsDemo();      // la demo no mira Teams: una escena inventada, coherente con el lector simulado
            var f = new FotoTeams();
            var pids = Win32.PidsDe("ms-teams");
            f.Procesos = pids.Count;
            if (pids.Count == 0) return f;
            f.Ventanas = Win32.VentanasDe(pids, false).Where(w => w.Clase == "TeamsWebView" && w.Titulo.Length > 0).ToList();
            bool perdido = chat.Hwnd == IntPtr.Zero || !Win32.IsWindow(chat.Hwnd);
            if (perdido && panelVigiaVisible && (DateTime.Now - ultimaBusquedaChat).TotalSeconds >= 12)
            {
                ultimaBusquedaChat = DateTime.Now;
                using (chat.Gate.Entrar("panel: buscar el chat"))
                    try { chat.BuscarVentana(); } catch { }
            }
            f.ChatListo = chat.Hwnd != IntPtr.Zero && Win32.IsWindow(chat.Hwnd);
            f.ChatAbierto = f.ChatListo ? chat.ChatAbierto() : "";
            return f;
        }

        /// <summary>Lo que la ficha «ventanas de Teams» muestra en la demo: dos ventanas de siempre y, en llamada, la de la reunión simulada.</summary>
        FotoTeams FotoTeamsDemo()
        {
            var f = new FotoTeams { Procesos = 2, ChatListo = true, ChatAbierto = "Rivas, Valentina" };
            f.Ventanas.Add(new VentanaInfo { Titulo = "Microsoft Teams", Clase = "TeamsWebView", Visible = true, Minimizada = true });
            f.Ventanas.Add(new VentanaInfo { Titulo = "Chat | Rivas, Valentina | Microsoft Teams", Clase = "TeamsWebView", Visible = true, Ancho = 1440, Alto = 900 });
            var L = vista.Lectura;
            if (L != null && L.HayLlamada) f.Ventanas.Add(new VentanaInfo { Titulo = L.Titulo, Clase = "TeamsWebView", Visible = true, Minimizada = true });
            return f;
        }

        /// <summary>La salud de la UI para estado.json: percentiles de latencia y trabas de los últimos 5 min.</summary>
        static Dictionary<string, object> SaludUi()
        {
            var e = LatidoUi.Estadistica();
            return new Dictionary<string, object>
            {
                ["p50Ms"] = Math.Round(e.p50, 1), ["p99Ms"] = Math.Round(e.p99, 1), ["maxMs"] = Math.Round(e.max, 1),
                ["trabas5min"] = e.trabas5min, ["muestras"] = e.muestras, ["trabasTotales"] = LatidoUi.TrabasTotales,
                ["peorMs"] = Math.Round(LatidoUi.PeorMs, 1), ["peorCulpable"] = LatidoUi.PeorCulpable,
            };
        }

        Dictionary<string, object> SaludGrabador()
        {
            var f = grabador?.Foto;
            if (f == null) return new Dictionary<string, object>();
            var d = new Dictionary<string, object> { ["activo"] = f.Activo, ["enCola"] = f.EnCola, ["cerrando"] = f.Cerrando, ["problema"] = f.Problema };
            if (f.Vivo != null)
            {
                d["reunion"] = f.Vivo.Reunion; d["segundos"] = Math.Round(f.Vivo.Segundos); d["nivelDb"] = f.Vivo.NivelDb; d["mb"] = Math.Round(f.Vivo.MB, 1);
                d["tramo"] = f.Vivo.Tramo; d["sano"] = f.Vivo.Sano; d["problemaVivo"] = f.Vivo.Problema; d["cierraEn"] = f.Vivo.CierraEn;
            }
            var act = f.Todas.FirstOrDefault(x => EstadoGrab.EnCola(x.Estado));
            if (act != null) { d["procesando"] = act.Reunion; d["etapa"] = act.Etapa; d["progreso"] = Math.Round(act.Progreso, 3); d["estado"] = act.Estado; d["enPausa"] = act.EnPausa; }
            return d;
        }

        // ------------------------------------------------------------ layout

        void Acomodar()
        {
            int pad = S(20);
            rTitulo = new Rectangle(0, 0, Width, S(38));
            rMin = new Rectangle(Width - S(84), 0, S(42), S(38));
            rCerrar = new Rectangle(Width - S(42), 0, S(42), S(38));
            // el indicador global va en la barra de título, entre el subtítulo y los botones de ventana
            int anchoCarga = Math.Min(S(420), Math.Max(S(180), Width / 3));
            cargaGlobal.SetBounds(Width - S(96) - anchoCarga, S(11), anchoCarga, S(18));
            lineaCarga.SetBounds(0, rTitulo.Bottom - Math.Max(2, S(2)), Width, Math.Max(2, S(2)));
            lineaCarga.BringToFront();
            // el REC va a la izquierda del indicador global: se ve desde cualquier pestaña mientras se graba
            indicadorRec.SetBounds(Width - S(96) - anchoCarga - S(280), S(8), S(268), S(22));
            indicadorRec.BringToFront();
            pestanas.SetBounds(pad - S(12), S(42), Width - pad * 2 + S(12), S(28));
            int topContenido = S(76);
            bool tab0 = pestanas.Activa == 0;
            linea.Visible = tab0;
            fSistema.Visible = fPresencia.Visible = fVentanas.Visible = fMensajeria.Visible = sIdle.Visible = tCrono.Visible = tab0;
            foreach (var ch in chips) ch.Visible = tab0;
            for (int i = 0; i < pantallas.Length; i++)
            {
                bool vis = pestanas.Activa == i + 1;
                pantallas[i].SetBounds(pad, topContenido, Width - pad * 2, Height - topContenido - S(12));
                pantallas[i].Visible = vis;
            }
            rHero = new Rectangle(pad, topContenido, Width - pad * 2, S(92));
            // 🚨 con la ventana angosta las 6 tarjetas en una fila quedan tan finitas que el valor se corta
            //    («abi …» en vez de «abierto»). Cuando la más chica no llega al mínimo legible, van en 2 filas de 3.
            int gapT = S(12), altoT = S(98);
            float unidadT = (Width - pad * 2 - gapT * 5) / PesosTarjetas.Sum();
            tarjetasDosFilas = unidadT * PesosTarjetas.Min() < S(150);
            rTarjetas = new Rectangle(pad, rHero.Bottom + S(10), Width - pad * 2, tarjetasDosFilas ? altoT * 2 + gapT : altoT);
            rLogTitulo = new Rectangle(pad, rTarjetas.Bottom + S(14), Width - pad * 2, S(18));

            // barra de pastillas: dos grupos (ajustes y acciones), cada uno repartido en filas parejas
            foreach (var ch in chips) ch.Ajustar();
            int gap = S(8), alto = S(26), filaH = alto + S(8);
            int anchoDisp = Width - pad * 2;
            var filasCfg = Repartir(new[] { chGracia, chIntervalo, chPosponer, chNadie, chSimulacion, chPresencia, chHorario, chSonido, chAvisos, chCuenta, chRoster, chInicio, chMinimizado }, anchoDisp, gap);
            var filasAcc = Repartir(new[] { chPausa, chLeer, chSalir, chVolcar, chCarpeta }, anchoDisp, gap);
            int sepGrupos = S(7);      // aire entre ajustes y acciones para que se lean como dos bloques
            filasBarra = filasCfg.Count + filasAcc.Count;
            int barraH = filasBarra * filaH + sepGrupos + S(10);
            rBarra = new Rectangle(pad, Height - barraH - S(12), Width - pad * 2, barraH);
            int y = rBarra.Top + S(10);
            foreach (var fila in filasCfg)
            {
                int x2 = pad;
                foreach (var ch in fila) { ch.Location = new Point(x2, y); x2 += ch.Width + gap; }
                y += filaH;
            }
            y += sepGrupos;
            foreach (var fila in filasAcc)
            {
                int x2 = pad;
                foreach (var ch in fila) { ch.Location = new Point(x2, y); x2 += ch.Width + gap; }
                y += filaH;
            }
            int topLog = rLogTitulo.Bottom + S(6);
            int altoLog = Math.Max(S(60), rBarra.Top - S(10) - topLog);
            // tablero 2x2 de fichas a la derecha de la línea de tiempo
            int anchoFichas = (int)((Width - pad * 2) * 0.46);
            int anchoLog = Width - pad * 2 - anchoFichas - S(12);
            int altoSerie = Math.Max(S(96), (int)(altoLog * 0.30));
            // la línea de tiempo comparte el ancho con la cronología de presencias: en un día tranquilo el log
            // deja media columna vacía, y ese hueco es justo donde entra el detalle que el user pide
            int anchoCrono = (int)(anchoLog * 0.38);
            int anchoLinea = anchoLog - anchoCrono - S(12);
            linea.Bounds = new Rectangle(pad, topLog, anchoLinea, altoLog - altoSerie - S(12));
            tCrono.SetBounds(pad + anchoLinea + S(12), topLog, anchoCrono, altoLog - altoSerie - S(12));
            sIdle.SetBounds(pad, topLog + altoLog - altoSerie, anchoLog, altoSerie);
            int xf = pad + anchoLog + S(12), colW = (anchoFichas - S(12)) / 2, hf = (altoLog - S(12)) / 2;
            fSistema.SetBounds(xf, topLog, colW, hf);
            fPresencia.SetBounds(xf + colW + S(12), topLog, anchoFichas - colW - S(12), hf);
            fMensajeria.SetBounds(xf, topLog + hf + S(12), colW, altoLog - hf - S(12));
            fVentanas.SetBounds(xf + colW + S(12), topLog + hf + S(12), anchoFichas - colW - S(12), altoLog - hf - S(12));
        }

        // ------------------------------------------------------------ pintura

        static Color ColorEstado(Estado e)
        {
            switch (e)
            {
                case Estado.SinTeams: return Tema.MuyApagado;
                case Estado.SinLlamada: return Tema.Apagado;
                case Estado.EnLlamada: return Tema.Cyan;
                case Estado.EsperandoGente: return Tema.Cielo;
                case Estado.SalaVacia: return Tema.Durazno;
                case Estado.Pospuesto: return Tema.Crema;
                case Estado.Saliendo: return Tema.Rosa;
                case Estado.Salido: return Tema.Salvia;
                case Estado.Pausado: return Tema.Malva;
            }
            return Tema.Apagado;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Tema.Fondo);
            var L = vista.Lectura ?? new Lectura();
            var cEstado = ColorEstado(vista.Estado);

            // --- barra de titulo propia
            using (var p = new Pen(Tema.BordeSuave)) g.DrawLine(p, 0, rTitulo.Bottom - 1, Width, rTitulo.Bottom - 1);
            int dot = S(8);
            using (var b = new SolidBrush(cEstado)) g.FillEllipse(b, S(18), (rTitulo.Height - dot) / 2f, dot, dot);
            int tx = S(34);
            var fT = Tema.Media(10f);
            Tema.Texto_(g, "TeamsTools", fT, Tema.Texto, new Rectangle(tx, 0, S(200), rTitulo.Height));
            tx += Tema.Medir(g, "TeamsTools", fT).Width + S(10);
            Tema.Texto_(g, demo ? "· DEMO con lector simulado" : "· el último apaga la luz · y nunca se duerme", Tema.Fina(9.5f), demo ? Tema.Crema : Tema.Apagado, new Rectangle(tx, 0, S(360), rTitulo.Height));
            if (cfg.Simulacion)
            {
                var fs = Tema.Media(8.5f);
                string s = "SIMULACIÓN";
                var sz = Tema.Medir(g, s, fs);
                var rs = new RectangleF(rMin.Left - sz.Width - S(30), (rTitulo.Height - sz.Height - S(8)) / 2f, sz.Width + S(16), sz.Height + S(8));
                Tema.Tarjeta_(g, rs, rs.Height / 2, Tema.Mezcla(Tema.Tarjeta, Tema.Crema, 0.12f), Tema.Alpha(Tema.Crema, 70));
                Tema.Texto_(g, s, fs, Tema.Crema, Rectangle.Round(rs), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            // botones min / cerrar
            if (hoverMin) using (var b = new SolidBrush(Tema.TarjetaHover)) g.FillRectangle(b, rMin);
            if (hoverCerrar) using (var b = new SolidBrush(Tema.Alpha(Tema.Rosa, 40))) g.FillRectangle(b, rCerrar);
            using (var p = new Pen(hoverMin ? Tema.Texto : Tema.TextoSuave, S(1)))
                g.DrawLine(p, rMin.Left + rMin.Width / 2 - S(5), rMin.Top + rMin.Height / 2, rMin.Left + rMin.Width / 2 + S(5), rMin.Top + rMin.Height / 2);
            using (var p = new Pen(hoverCerrar ? Tema.Rosa : Tema.TextoSuave, S(1)))
            {
                int cx = rCerrar.Left + rCerrar.Width / 2, cy = rCerrar.Top + rCerrar.Height / 2, r = S(5);
                g.DrawLine(p, cx - r, cy - r, cx + r, cy + r);
                g.DrawLine(p, cx - r, cy + r, cx + r, cy - r);
            }

            if (pestanas.Activa != 0) return;

            // --- hero
            var fEstado = Tema.Fina(23f);
            string estado = vigia != null && vigia.Pausado ? "EN PAUSA" : vista.EstadoTexto;
            var szEstado = Tema.Medir(g, estado, fEstado);
            int heroTextoW = rHero.Width - S(300);
            Tema.Texto_(g, estado, fEstado, cEstado, new Rectangle(rHero.Left, rHero.Top, heroTextoW, S(38)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            string sub;
            if (!L.TeamsCorriendo) sub = "abrí Teams y me quedo mirando";
            else if (!L.HayLlamada) sub = "cuando entres a una reunión la vigilo sola";
            else
            {
                sub = $"«{L.Reunion}»" + (L.Duracion.Length > 0 ? $" · {L.Duracion}" : "") + (L.Otros > 0 ? $" · con {L.ResumenNombres(3)}" : L.HayCompartido ? " · alguien comparte contenido" : " · nadie más en la sala");
                if (L.Minimizada) sub += " · ventana minimizada (la leo igual)";
            }
            Tema.Texto_(g, sub, Tema.Fina(10.5f), Tema.TextoSuave, new Rectangle(rHero.Left, rHero.Top + S(42), heroTextoW, S(20)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            string sub2 = vista.Estado == Estado.SalaVacia ? $"salgo en {vista.SegundosRestantes} s · {vista.Motivo}"
                        : vista.Estado == Estado.Pospuesto && vista.PospuestoHasta != null ? $"pospuesto hasta las {vista.PospuestoHasta:HH:mm}"
                        : vista.Estado == Estado.EsperandoGente ? (cfg.SalirSiNadieLlegaMinutos > 0 ? $"si nadie llega en {cfg.SalirSiNadieLlegaMinutos} min me voy" : "no salgo hasta que entre alguien y después se vaya")
                        : vista.Estado == Estado.Salido ? $"ya salí · {vista.Salidas} salida/s en esta sesión"
                        : vista.Estado == Estado.EnLlamada ? $"si se van todos, espero {cfg.GraciaSegundos} s y salgo" + (cfg.Simulacion ? " (simulación)" : "")
                        : "";
            Tema.Texto_(g, sub2, Tema.Fina(9.5f), Tema.Apagado, new Rectangle(rHero.Left, rHero.Top + S(64), heroTextoW, S(18)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            // anillo de gracia o tira de ocupacion, a la derecha del hero
            var rDer = new Rectangle(rHero.Right - S(280), rHero.Top, S(280), rHero.Height);
            if (vista.Estado == Estado.SalaVacia || vista.Estado == Estado.Saliendo)
            {
                int d = S(86);
                var ra = new RectangleF(rDer.Right - d, rDer.Top + (rDer.Height - d) / 2f, d, d);
                float frac = cfg.GraciaSegundos > 0 ? vista.SegundosRestantes / (float)cfg.GraciaSegundos : 0;
                Tema.Anillo(g, ra, S(5), vista.Estado == Estado.Saliendo ? 1f : frac, Tema.BordeSuave, cEstado);
                Tema.Texto_(g, vista.Estado == Estado.Saliendo ? "…" : vista.SegundosRestantes.ToString(), Tema.Fina(24f), Tema.Texto, Rectangle.Round(ra), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                Tema.Texto_(g, "SEGUNDOS", Tema.Media(8f), Tema.Apagado, new Rectangle(rDer.Left, (int)ra.Bottom - S(16), rDer.Width - d - S(12), S(16)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                Tema.Texto_(g, "PARA SALIR", Tema.Media(8f), Tema.Apagado, new Rectangle(rDer.Left, (int)ra.Bottom, rDer.Width - d - S(12), S(16)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
            else PintarOcupacion(g, rDer);

            // --- tarjetas: una fila de 6, o dos de 3 cuando la ventana es angosta (si no, el valor se corta)
            int gap = S(12);
            var rects = RectsTarjetas(rTarjetas, tarjetasDosFilas, gap);
            Tarjeta(g, rects[0], "TEAMS", L.TeamsCorriendo ? "abierto" : "cerrado", L.TeamsCorriendo ? $"{L.VentanasTeams} ventanas · {L.Ms} ms" : "no veo ms-teams.exe", L.TeamsCorriendo ? Tema.Cyan : Tema.MuyApagado);
            Tarjeta(g, rects[1], "REUNIÓN", L.HayLlamada ? L.Reunion : "—", L.HayLlamada ? (L.Duracion.Length > 0 ? "lleva " + L.Duracion : "") + (L.Minimizada ? " · minimizada" : "") + (L.EsCompacta ? " · solo vista compacta" : "") : "ninguna llamada activa", L.HayLlamada ? Tema.Malva : Tema.MuyApagado, Tema.Fina(13f));
            string subOtros = L.HayLlamada ? (L.Otros > 0 ? L.ResumenNombres(1) : (vista.GenteVista ? $"se fueron (hubo {vista.MaxOtros})" : "no entró nadie")) + (L.Pagina.Length > 0 ? $" · pág {L.Pagina}" : "") + (L.HayCompartido ? " · comparten" : "") : "—";
            Tarjeta(g, rects[2], "OTROS EN LA SALA", L.HayLlamada ? L.Otros.ToString() : "—", subOtros, L.HayLlamada ? (L.Otros > 0 || L.HayCompartido ? Tema.Cyan : Tema.Durazno) : Tema.MuyApagado);
            string vacio = vista.VacioDesde != null ? Watcher.Fmt(DateTime.Now - vista.VacioDesde.Value) : "—";
            string subVacio = vista.VacioDesde != null ? $"desde las {vista.VacioDesde:HH:mm:ss}" : vista.PospuestoHasta != null ? $"pospuesto hasta {vista.PospuestoHasta:HH:mm}" : $"gracia {cfg.GraciaSegundos} s";
            Tarjeta(g, rects[3], "SALA VACÍA HACE", vacio, subVacio, vista.VacioDesde != null ? Tema.Durazno : Tema.MuyApagado);
            string accion, subAccion;
            switch (vista.Estado)
            {
                case Estado.SalaVacia: accion = $"salgo en {vista.SegundosRestantes} s"; subAccion = cfg.Simulacion ? "simulación: solo aviso" : "método " + cfg.MetodoSalida; break;
                case Estado.Saliendo: accion = "saliendo…"; subAccion = cfg.VerificarConRoster ? "verifico con el panel Gente" : "método " + cfg.MetodoSalida; break;
                case Estado.Salido: accion = "salí"; subAccion = vista.Intentos > 0 ? $"{vista.Intentos} intento/s" : "simulación"; break;
                case Estado.Pospuesto: accion = "pospuesto"; subAccion = vista.PospuestoHasta != null ? $"hasta las {vista.PospuestoHasta:HH:mm}" : ""; break;
                case Estado.EsperandoGente: accion = "esperando"; subAccion = cfg.SalirSiNadieLlegaMinutos > 0 ? $"si nadie llega en {cfg.SalirSiNadieLlegaMinutos} min" : "a que entre alguien"; break;
                case Estado.EnLlamada: accion = "vigilando"; subAccion = cfg.Simulacion ? "simulación activa" : "salida real · " + cfg.MetodoSalida; break;
                case Estado.Pausado: accion = "en pausa"; subAccion = "no hago nada"; break;
                default: accion = "en espera"; subAccion = $"{vista.Salidas} salida/s en esta sesión"; break;
            }
            Tarjeta(g, rects[4], "ACCIÓN", accion, subAccion, cEstado, Tema.Fina(17f));
            string presVal = demo ? "demo" : presencia.Bloqueada ? "sesión bloqueada" : presencia.MedicionFresca ? presencia.PresenciaTeams : presencia.Activa ? "Disponible" : "Teams decide";
            string presSub;
            if (demo) presSub = "presencia apagada en la demo";
            else if (!presencia.Activa) presSub = "Ausente a los 5 min sin actividad";
            else
            {
                presSub = $"{Presencia.Fmt(presencia.IdleSegundos)} sin actividad · {presencia.Toques} toque/s";
                if (presencia.Fallos > 0) presSub += $" · {presencia.Fallos} fallo/s";
                else if (presencia.Toques == 0) presSub += $" · toco a los {cfg.PresenciaUmbralSegundos} s";
            }
            Tarjeta(g, rects[5], "PRESENCIA", presVal, presSub, demo ? Tema.MuyApagado : presencia.Activa ? Tema.Cyan : Tema.Crema, Tema.Fina(17f));

            // --- titulo de la linea de tiempo
            Tema.Texto_(g, "LÍNEA DE TIEMPO", Tema.Media(8.5f), Tema.Apagado, rLogTitulo);
            string derecha = L.Hora == DateTime.MinValue ? "" : $"última lectura hace {(int)(DateTime.Now - L.Hora).TotalSeconds} s · {L.Ms} ms" + (L.Error.Length > 0 ? " · error: " + L.Error : "");
            Tema.Texto_(g, derecha, Tema.Fina(9f), Tema.Apagado, rLogTitulo, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            using (var p = new Pen(Tema.BordeSuave)) g.DrawLine(p, rBarra.Left, rBarra.Top, rBarra.Right, rBarra.Top);
        }

        void Tarjeta(Graphics g, Rectangle r, string etiqueta, string valor, string sub, Color acento, Font fValor = null)
        {
            // minimal: tarjeta sin borde, un punto pastel al lado de la etiqueta
            Tema.Tarjeta_(g, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(10), Tema.Tarjeta, Tema.Tarjeta);
            int px = r.Left + S(14);
            int pd = S(5);
            using (var b = new SolidBrush(acento)) g.FillEllipse(b, px, r.Top + S(11) + (S(14) - pd) / 2f, pd, pd);
            Tema.Texto_(g, etiqueta, Tema.Media(8f), Tema.Apagado, new Rectangle(px + pd + S(7), r.Top + S(11), r.Width - S(28), S(14)));
            Tema.Texto_(g, valor, fValor ?? Tema.Fina(19f), Tema.Texto, new Rectangle(px, r.Top + S(26), r.Width - S(28), S(40)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Tema.Texto_(g, sub, Tema.Fina(9f), Tema.TextoSuave, new Rectangle(px, r.Bottom - S(27), r.Width - S(28), S(18)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        void PintarOcupacion(Graphics g, Rectangle r)
        {
            int n = 120;
            var datos = historia.Skip(Math.Max(0, historia.Count - n)).ToList();
            var rg = new Rectangle(r.Left, r.Top + S(20), r.Width, r.Height - S(46));
            Tema.Texto_(g, "OCUPACIÓN DE LA SALA · últimas " + Math.Max(datos.Count, 1) + " lecturas", Tema.Media(8f), Tema.Apagado, new Rectangle(r.Left, r.Bottom - S(20), r.Width, S(16)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            using (var p = new Pen(Tema.BordeSuave)) g.DrawLine(p, rg.Left, rg.Bottom, rg.Right, rg.Bottom);
            if (datos.Count == 0) return;
            int max = Math.Max(1, datos.Max(d => d.otros));
            float bw = rg.Width / (float)n;
            float x = rg.Right - datos.Count * bw;
            foreach (var d in datos)
            {
                float h = d.llamada ? Math.Max(S(2), rg.Height * (d.otros / (float)max)) : S(1);
                Color c = !d.llamada ? Tema.MuyApagado : d.otros > 0 ? Tema.Alpha(Tema.Cyan, 190) : Tema.Durazno;
                using (var b = new SolidBrush(c)) g.FillRectangle(b, x, rg.Bottom - h, Math.Max(1f, bw - 1f), h);
                x += bw;
            }
            var ult = datos[datos.Count - 1];
            string leyenda = ult.llamada ? $"máx {max} · ahora {ult.otros}" : "sin llamada";
            Tema.Texto_(g, leyenda, Tema.Fina(8.5f), Tema.Apagado, new Rectangle(r.Left, r.Top + S(2), r.Width, S(14)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        // ------------------------------------------------------------ mouse en la barra de titulo

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool m = rMin.Contains(e.Location), c = rCerrar.Contains(e.Location);
            if (m != hoverMin || c != hoverCerrar) { hoverMin = m; hoverCerrar = c; Invalidate(rTitulo); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            if (hoverMin || hoverCerrar) { hoverMin = hoverCerrar = false; Invalidate(rTitulo); }
            base.OnMouseLeave(e);
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (rMin.Contains(e.Location)) { WindowState = FormWindowState.Minimized; return; }
                if (rCerrar.Contains(e.Location)) { Hide(); return; }
            }
            base.OnMouseClick(e);
        }
    }

    /// <summary>Lo que el sondeo de fondo sabe de Teams. Inmutable para quien la lee.</summary>
    internal sealed class FotoTeams
    {
        public int Procesos;
        public List<VentanaInfo> Ventanas = new List<VentanaInfo>();
        public bool ChatListo;
        public string ChatAbierto = "";
    }
}
