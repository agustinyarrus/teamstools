using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace TeamsTools
{
    /// <summary>Un mensaje entrante visto por el autocontestador y lo que se hizo con el.</summary>
    internal sealed class Entrante
    {
        public DateTime Hora = DateTime.Now;
        public string Chat = "";
        public string Tipo = "privado";
        public string Autor = "";
        public string Texto = "";
        public string Regla = "";
        public string Respuesta = "";
        public bool Respondido;
        public string Resultado = "";
        public int Ms;
    }

    /// <summary>
    /// Modo automático: cada N segundos mira los chats sin leer, abre cada uno, lee lo ultimo que escribio la otra
    /// persona, elige una regla y contesta. Todo con la ventana de Teams minimizada.
    /// </summary>
    internal sealed class Autocontestador
    {
        readonly Config cfg;
        readonly Logger log;
        readonly ConfigRespuestas reglas;
        readonly TeamsChat chat;
        readonly Contactos contactos;
        readonly string rutaBandeja;
        Thread hilo;
        volatile bool parar;
        readonly AutoResetEvent despertar = new AutoResetEvent(false);
        readonly object candado = new object();
        readonly Dictionary<string, DateTime> ultimaRespuesta = new Dictionary<string, DateTime>();
        readonly Dictionary<string, long> ultimoTsVisto = new Dictionary<string, long>();
        public readonly List<Entrante> Bandeja = new List<Entrante>();
        public event Action Cambio;
        public event Action<Entrante> Respondido;

        // estado visible
        public bool Activo { get; private set; }
        public bool ActivadoPorInactividad { get; private set; }
        public string Estado { get; private set; } = "apagado";
        public DateTime? UltimaLectura { get; private set; }
        public int ChatsNoLeidos { get; private set; }
        public int Lecturas { get; private set; }
        public int RespuestasHoy { get; private set; }
        public int Errores { get; private set; }
        public long UltimaLecturaMs { get; private set; }
        public DateTime? ActivoDesde { get; private set; }
        public List<ChatItem> UltimosChats { get; private set; } = new List<ChatItem>();
        public bool Ocupado { get; private set; }   // enviando algo (para que otros no toquen Teams a la vez)
        /// <summary>El modo automático está prendido: a mano, o solo porque no tocaste nada un rato.</summary>
        public bool Encendido => reglas.ModoAutomatico || ActivadoPorInactividad;

        public Autocontestador(Config c, Logger l, ConfigRespuestas r, TeamsChat t, Contactos cs, string rutaBandejaJsonl)
        {
            cfg = c; log = l; reglas = r; chat = t; contactos = cs; rutaBandeja = rutaBandejaJsonl;
            CargarBandeja();
        }

        public void Iniciar()
        {
            if (hilo != null) return;
            hilo = new Thread(Bucle) { IsBackground = true, Name = "autocontestador" };
            hilo.SetApartmentState(ApartmentState.MTA);
            hilo.Start();
        }
        public void Detener() { parar = true; despertar.Set(); }
        public void Despertar() => despertar.Set();

        public void PonerModoAutomatico(bool on, string motivo = "manual")
        {
            reglas.ModoAutomatico = on;
            reglas.Guardar();
            ActivadoPorInactividad = false;
            log.Info(on ? $"Modo automático ENCENDIDO ({motivo}): contesto solo los chats sin leer" : "Modo automático apagado");
            despertar.Set();
        }

        void Bucle()
        {
            log.Info($"Autocontestador listo · {reglas.Reglas.Count(x => x.Activa)} reglas activas · modo automático {(reglas.ModoAutomatico ? "ENCENDIDO" : "apagado")}");
            int fallosSeguidos = 0;
            while (!parar)
            {
                int espera = Math.Max(3, reglas.SegundosEntreLecturas) * 1000;
                try
                {
                    int idle = Presencia.IdleAhora();
                    bool porInactividad = reglas.ActivarSiInactivoMinutos > 0 && idle >= reglas.ActivarSiInactivoMinutos * 60;
                    if (!reglas.ModoAutomatico && porInactividad && !ActivadoPorInactividad)
                    {
                        ActivadoPorInactividad = true;
                        log.Info($"Modo automático encendido solo: {reglas.ActivarSiInactivoMinutos} min sin actividad");
                    }
                    if (ActivadoPorInactividad && reglas.ApagarAlVolver && idle < 45 && !reglas.ModoAutomatico)
                    {
                        ActivadoPorInactividad = false;
                        log.Info("Volviste al teclado: modo automático apagado");
                    }
                    bool activo = Encendido;
                    if (activo && !cfg.EnHorario()) { Estado = "fuera de horario"; Activo = false; Publicar(); despertar.WaitOne(10000); continue; }
                    if (activo && !Activo) ActivoDesde = DateTime.Now;
                    Activo = activo;
                    if (!activo) { Estado = "apagado"; Publicar(); despertar.WaitOne(2000); continue; }

                    Estado = "leyendo";
                    // la lista se lee bajo la aduana (destapa/oculta la ventana) para no chocar con el observador
                    // ni con la presencia; después cada Atender toma su propio turno, así no retengo la aduana
                    // durante toda la tanda de respuestas.
                    List<ChatItem> chats;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using (chat.Gate.Entrar("autocontestador: leer lista"))
                    {
                        if (!chat.BuscarVentana() && !chat.Despertar())
                        {
                            Estado = "sin Teams";
                            if (++fallosSeguidos <= 2) log.Aviso("Modo automático: no encuentro la ventana de chat de Teams");
                            Publicar();
                            despertar.WaitOne(30000);
                            continue;
                        }
                        fallosSeguidos = 0;
                        chats = chat.ListarChats();
                    }
                    UltimaLecturaMs = sw.ElapsedMilliseconds;
                    UltimaLectura = DateTime.Now;
                    Lecturas++;
                    lock (candado) UltimosChats = chats;
                    var noLeidos = chats.Where(c => c.NoLeido && (c.Tipo == "privado" || (reglas.ResponderGrupos && (c.Tipo == "grupo" || c.Tipo == "reunion")))).ToList();
                    ChatsNoLeidos = chats.Count(c => c.NoLeido);
                    Estado = noLeidos.Count > 0 ? $"atendiendo {noLeidos.Count}" : "vigilando chats";
                    Publicar();
                    foreach (var c in noLeidos)
                    {
                        if (parar || !Encendido) break;
                        Atender(c);
                    }
                    Estado = "vigilando chats";
                }
                catch (Exception ex) { Errores++; log.Error("Autocontestador: " + ex.Message); }
                Publicar();
                despertar.WaitOne(espera);
            }
        }

        void Atender(ChatItem c)
        {
            string clave = Contactos.Normalizar(c.Nombre);
            lock (candado)
            {
                if (ultimaRespuesta.TryGetValue(clave, out var ult) && (DateTime.Now - ult).TotalMinutes < reglas.EnfriamientoGeneralMinutos)
                {
                    log.Debug($"«{c.Nombre}» sin leer, pero le contesté hace {(int)(DateTime.Now - ult).TotalMinutes} min: espero");
                    return;
                }
            }
            Ocupado = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // 🚨 La aduana: abrir el chat, leer lo último, elegir la regla y enviar es UN SOLO acto atómico.
            //    Antes el observador o la presencia se colaban entre «abrí el chat» y «envié», le ocultaban la
            //    ventana a Teams y el envío moría con COMException dejando el texto escrito sin mandar.
            using (chat.Gate.Entrar("autocontestador: " + c.Nombre))
            try
            {
                if (!chat.AbrirChat(c)) { log.Aviso($"No pude abrir «{c.Nombre}»"); return; }
                Thread.Sleep(500);
                var msgs = chat.LeerMensajes(12);
                long visto; lock (candado) ultimoTsVisto.TryGetValue(clave, out visto);
                // lo nuevo de la otra persona: los ultimos mensajes que no son mios y son posteriores a lo ya visto
                var ajenos = msgs.Where(m => !m.Mio && m.Ts > visto && m.Texto.Length > 0).ToList();
                if (ajenos.Count == 0)
                {
                    // sin registro previo: tomar el ultimo mensaje si es ajeno
                    var ultimo = msgs.LastOrDefault();
                    if (ultimo != null && !ultimo.Mio && ultimo.Texto.Length > 0 && visto == 0) ajenos.Add(ultimo);
                }
                if (msgs.Count > 0) lock (candado) ultimoTsVisto[clave] = msgs.Max(m => m.Ts);
                if (ajenos.Count == 0) { log.Debug($"«{c.Nombre}» sin leer pero lo último es mío o no es texto"); return; }
                string texto = string.Join("\n", ajenos.Select(m => m.Texto));
                var e = new Entrante { Chat = c.Nombre, Tipo = c.Tipo, Autor = ajenos.Last().Autor, Texto = texto };
                string motivo;
                var regla = reglas.Elegir(texto, c.Nombre, c.Tipo != "privado", Senales(texto), DateTime.Now, out motivo);
                if (regla == null)
                {
                    e.Resultado = motivo.Length > 0 ? "no contesté · " + motivo : "sin regla que coincida";
                    log.Info($"Entrante de «{c.Nombre}»: «{Corto(texto)}» · no contesto" + (motivo.Length > 0 ? " · " + motivo : ""));
                    Registrar(e);
                    return;
                }
                e.Regla = regla.Nombre;

                // --- contestar después de X tiempo: no parecer un robot que responde en el mismo instante.
                //     Mientras espera vuelve a leer: si la persona siguió escribiendo, contesta a lo ÚLTIMO que dijo,
                //     y si eso cambia qué regla corresponde, se usa la nueva.
                if (regla.RetrasoSegundos > 0)
                {
                    int espera = Math.Min(regla.RetrasoSegundos, 300);
                    log.Debug($"Regla «{regla.Nombre}»: espero {espera} s antes de contestarle a «{c.Nombre}»");
                    var relojEspera = System.Diagnostics.Stopwatch.StartNew();
                    while (relojEspera.Elapsed.TotalSeconds < espera && !parar && Encendido) Thread.Sleep(250);
                    if (parar || !Encendido) { e.Resultado = "cancelado durante la espera"; Registrar(e); return; }
                    try
                    {
                        var frescos = chat.LeerMensajes(12).Where(m => !m.Mio && m.Ts > visto && m.Texto.Length > 0).ToList();
                        if (frescos.Count > ajenos.Count)
                        {
                            texto = string.Join("\n", frescos.Select(m => m.Texto));
                            e.Texto = texto; e.Autor = frescos.Last().Autor;
                            lock (candado) ultimoTsVisto[clave] = frescos.Max(m => m.Ts);
                            string m2;
                            var otra = reglas.Elegir(texto, c.Nombre, c.Tipo != "privado", Senales(texto), DateTime.Now, out m2);
                            if (otra == null) { e.Resultado = "siguió escribiendo y ya ninguna regla aplica · " + m2; log.Info($"«{c.Nombre}» siguió escribiendo: ya no contesto · {m2}"); Registrar(e); return; }
                            if (otra != regla) log.Info($"«{c.Nombre}» siguió escribiendo: cambio de «{regla.Nombre}» a «{otra.Nombre}»");
                            regla = otra; e.Regla = regla.Nombre;
                        }
                    }
                    catch (Exception ex) { log.Debug("No pude releer tras la espera: " + ex.Message); }
                }
                // la respuesta puede ser VARIAS burbujas (textos + imágenes); si la regla es vieja, es una sola
                var envio = regla.RespuestaEfectiva().ConFirma(reglas.Firma)
                                 .Rellenar(t => ConfigRespuestas.Rellenar(t, c.Nombre, chat.MiNombre));
                if (regla.UsarIA)
                {
                    string detIA;
                    var redactada = RedactarConIA(texto, e.Autor, regla, out detIA);
                    if (redactada != null)
                    {
                        envio = Envio.DeTexto(System.Net.WebUtility.HtmlEncode(redactada).Replace("\n", "<br>"), redactada).ConFirma(reglas.Firma);
                        log.Ok($"Regla «{regla.Nombre}»: la redactó el modelo local · {detIA}");
                    }
                    else log.Aviso($"Regla «{regla.Nombre}»: contesto con el texto fijo · {detIA}");
                }
                var faltan = envio.AdjuntosFaltantes();
                if (faltan.Count > 0) log.Aviso($"Regla «{regla.Nombre}»: {faltan.Count} adjunto/s ya no están en disco ({string.Join(", ", faltan.Take(3))})");
                e.Respuesta = envio.Partes.Count > 1 ? envio.Resumen(140) : envio.PrimerPlano();
                // preferirPlano: la respuesta automática entra por WM_CHAR (invisible, sin robar el foco).
                // El formato no vale una ventana que salta; lo elegante es que el user no note nada.
                var r = chat.EnviarEnvio(envio, c.Nombre, preferirPlano: true);
                e.Respondido = r.Ok; e.Resultado = r.Detalle; e.Ms = (int)sw.ElapsedMilliseconds;
                regla.Anotar(r.Ok, e.Ms, r.Detalle);      // estadística propia de la regla: usos, fallos, ms, cupo del día
                if (r.Ok)
                {
                    lock (candado) ultimaRespuesta[clave] = DateTime.Now;
                    reglas.Guardar();
                    RespuestasHoy++;
                    log.Ok($"Contesté a «{c.Nombre}» con la regla «{regla.Nombre}» ({e.Ms} ms): «{Corto(e.Respuesta)}»");
                    try { var ms2 = chat.LeerMensajes(3); if (ms2.Count > 0) lock (candado) ultimoTsVisto[clave] = ms2.Max(m => m.Ts); } catch { }
                }
                else { Errores++; log.Error($"No pude contestarle a «{c.Nombre}»: {r.Detalle}"); }
                Registrar(e);
                try { Respondido?.Invoke(e); } catch { }
            }
            finally { Ocupado = false; }
        }

        void Registrar(Entrante e)
        {
            lock (candado) { Bandeja.Add(e); while (Bandeja.Count > 300) Bandeja.RemoveAt(0); }
            try
            {
                var d = new Dictionary<string, object> { ["hora"] = e.Hora, ["chat"] = e.Chat, ["tipo"] = e.Tipo, ["autor"] = e.Autor, ["texto"] = e.Texto, ["regla"] = e.Regla, ["respuesta"] = e.Respuesta, ["respondido"] = e.Respondido, ["resultado"] = e.Resultado, ["ms"] = e.Ms };
                Disco.Agregar(rutaBandeja, Json.Texto(d).Replace("\n", " ") + Environment.NewLine);
            }
            catch { }
        }

        void CargarBandeja()
        {
            try
            {
                if (!File.Exists(rutaBandeja)) return;
                var lineas = File.ReadAllLines(rutaBandeja, Encoding.UTF8);
                var js = new System.Web.Script.Serialization.JavaScriptSerializer();
                foreach (var l in lineas.Skip(Math.Max(0, lineas.Length - 300)))
                {
                    try
                    {
                        var d = js.Deserialize<Dictionary<string, object>>(l);
                        var e = new Entrante { Hora = Json.F(d, "hora") ?? DateTime.MinValue, Chat = Json.S(d, "chat"), Tipo = Json.S(d, "tipo"), Autor = Json.S(d, "autor"), Texto = Json.S(d, "texto"), Regla = Json.S(d, "regla"), Respuesta = Json.S(d, "respuesta"), Respondido = Json.B(d, "respondido"), Resultado = Json.S(d, "resultado"), Ms = Json.I(d, "ms") };
                        Bandeja.Add(e);
                        if (e.Respondido && e.Hora.Date == DateTime.Today) RespuestasHoy++;
                    }
                    catch { }
                }
            }
            catch { }
        }

        public Entrante[] Ultimos() { lock (candado) return Bandeja.ToArray(); }

        /// <summary>Prueba de reglas sin Teams: que regla contestaria y con que texto.</summary>
        public (Regla regla, string respuesta) Probar(string texto, string persona) { string _; return Probar(texto, persona, out _); }

        /// <summary>Como Probar, pero además dice POR QUÉ no contestó ninguna regla.</summary>
        public (Regla regla, string respuesta) Probar(string texto, string persona, out string motivo)
        {
            var r = reglas.Elegir(texto, persona, false, Senales(texto), DateTime.Now, out motivo);
            if (r == null) return (null, "");
            var envio = r.RespuestaEfectiva().Rellenar(t => ConfigRespuestas.Rellenar(t, persona, chat.MiNombre));
            return (r, envio.Partes.Count > 1 ? envio.Resumen(140) : envio.PrimerPlano());
        }

        /// <summary>
        /// Le pide al modelo local que redacte la respuesta. Devuelve null si NO se puede usar lo que escribió, y en
        /// ese caso el autocontestador cae al texto fijo de la regla. Tres candados, en este orden:
        ///   1. el modelo tiene que estar levantado (si no, ni se intenta y se dice cómo prenderlo);
        ///   2. RELOJ DE CORTE: si tarda más de lo permitido se abandona. Una respuesta automática que llega tarde
        ///      no sirve — el otro ya cerró el chat — y no puede dejar colgado el lazo que atiende los demás chats;
        ///   3. el portero de RespuestaIA: la IA aporta redacción, no datos. Si nombra un ticket, una hora o un
        ///      enlace que no estaban en el mensaje entrante, se descarta.
        /// El hilo que quedó corriendo tras el corte se abandona, no se mata: matar un hilo en medio de una llamada
        /// HTTP deja el socket en cualquier estado. Se lo deja terminar solo y su resultado se ignora.
        /// </summary>
        /// <summary>Cortes seguidos por lentitud, y hasta cuándo dejamos de intentar. Ver el corta-corriente abajo.</summary>
        int iaLentas;
        DateTime iaEnPausaHasta = DateTime.MinValue;

        string RedactarConIA(string entrante, string autor, Regla regla, out string detalle)
        {
            detalle = "";
            try
            {
                // 🚨 CORTA-CORRIENTE. El reloj de corte protege UNA respuesta, pero si el modelo está siempre lento
                // (otro servidor grande peleando por los núcleos, por ejemplo) se pagan los segundos del corte en
                // CADA mensaje y el autocontestador se arrastra. Tras 3 cortes seguidos se deja de intentar 10 min:
                // el que falla sistemáticamente no merece que le sigamos pagando el peaje.
                if (DateTime.Now < iaEnPausaHasta)
                {
                    detalle = $"la IA quedó en pausa hasta las {iaEnPausaHasta:HH:mm} porque venía tardando de más";
                    return null;
                }

                string porQue;
                if (!AsistenteIA.Verificar(out porQue)) { detalle = "el modelo no está disponible · " + porQue; return null; }

                int tope = Math.Max(1, reglas.IaMaxSegundos) * 1000;
                string salida = null, detIA = "";
                var reloj = System.Diagnostics.Stopwatch.StartNew();
                using (Tareas.Empezar("el modelo está redactando", "para " + (autor ?? ""), Tema.Malva))
                {
                    var tarea = System.Threading.Tasks.Task.Run(() =>
                    {
                        try { string d; var r = AsistenteIA.Redactar(entrante, autor ?? "", regla.InstruccionIA ?? "", reglas.IaMaxTokens, out d); detIA = d; return r; }
                        catch (Exception ex) { detIA = ex.Message; return null; }
                    });
                    if (!tarea.Wait(tope))
                    {
                        detalle = $"tardó más de {reglas.IaMaxSegundos} s y lo abandoné";
                        if (++iaLentas >= 3)
                        {
                            iaEnPausaHasta = DateTime.Now.AddMinutes(10);
                            iaLentas = 0;
                            log.Aviso($"La IA tardó de más 3 veces seguidas: dejo de intentar hasta las {iaEnPausaHasta:HH:mm} y contesto con el texto fijo. " +
                                      "Casi siempre es que hay un modelo grande levantado ocupando la máquina — alcanza con UNO: " +
                                      "medido, un llama-server con un modelo grande consume ~5,5 núcleos de 4 físicos SIN que nadie le pida nada. " +
                                      "Revisá que haya un solo llama-server vivo (y que no lo esté reviviendo una tarea programada).");
                        }
                        return null;
                    }
                    salida = tarea.Result;
                }

                var v = RespuestaIA.Revisar(salida, entrante);
                if (!v.Sirve) { detalle = v.Motivo + (detIA.Length > 0 ? " · " + detIA : ""); return null; }
                iaLentas = 0;   // contestó a tiempo: se perdona lo anterior
                detalle = $"{reloj.ElapsedMilliseconds} ms · {v.Texto.Length} caracteres" + (detIA.Length > 0 ? " · " + detIA : "");
                return v.Texto;
            }
            catch (Exception ex) { detalle = "error llamando al modelo: " + ex.Message; return null; }
        }

        /// <summary>
        /// Señales que los micromodelos ven en el texto. Si algo falla devuelve null, y las reglas leen ese null como
        /// "no hay con qué evaluar": una regla que EXIGE señales no dispara, en vez de disparar a ciegas.
        /// </summary>
        static ISet<string> Senales(string texto)
        {
            try { return Micromodelos.Analizar(texto)?.Etiquetas; } catch { return null; }
        }

        void Publicar() { try { Cambio?.Invoke(); } catch { } }
        static string Corto(string s) => string.IsNullOrEmpty(s) ? "" : (s = s.Replace("\n", " ⏎ ")).Length > 70 ? s.Substring(0, 70) + "…" : s;
    }
}
