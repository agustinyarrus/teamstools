using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace TeamsTools
{
    /// <summary>Mensaje propio guardado para mandar con un clic ("personalizados").</summary>
    internal sealed class Personalizado
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Nombre = "";
        public string Para = "";          // contacto sugerido (nombre Teams) o vacio
        public string TextoHtml = "";     // primera parte de texto (compatibilidad con exes viejos)
        public string TextoPlano = "";
        public string TextoRtf = "";
        /// <summary>El mensaje completo: puede ser varias burbujas (textos + imágenes).</summary>
        public Envio Mensaje = new Envio();
        public int Usos = 0;
        public DateTime? UltimoUso;
        public string Etiqueta = "";      // "trabajo", "cortesía", etc.

        /// <summary>El envío a mandar: las partes si las hay, si no una sola burbuja con los campos de siempre.</summary>
        public Envio MensajeEfectivo()
        {
            if (Mensaje != null && Mensaje.Partes.Count > 0) return Mensaje;
            string html = TextoHtml.Length > 0 ? TextoHtml : System.Net.WebUtility.HtmlEncode(TextoPlano).Replace("\n", "<br>");
            return Envio.DeTexto(html, TextoPlano, TextoRtf);
        }

        /// <summary>Deja TextoHtml/Plano/Rtf con la primera parte de texto, para no perder nada con un exe viejo.</summary>
        public void SincronizarCamposViejos()
        {
            if (Mensaje == null) { Mensaje = new Envio(); return; }
            if (Mensaje.Partes.Count == 0) return;
            TextoHtml = Mensaje.PrimerHtml();
            TextoPlano = Mensaje.PrimerPlano();
            string rtf = Mensaje.PrimerRtf();
            if (rtf.Length > 0) TextoRtf = rtf;
        }
    }

    internal sealed class Personalizados
    {
        public List<Personalizado> Lista = new List<Personalizado>();
        string ruta;
        public static Personalizados Cargar(string ruta)
        {
            var c = new Personalizados { ruta = ruta };
            var d = Json.LeerObjeto(ruta);
            if (d != null)
                foreach (var o in Json.Lista(d, "mensajes"))
                {
                    var p = new Personalizado { Id = Json.S(o, "id", Guid.NewGuid().ToString("N").Substring(0, 8)), Nombre = Json.S(o, "nombre"), Para = Json.S(o, "para"), TextoHtml = Json.S(o, "textoHtml"), TextoPlano = Json.S(o, "textoPlano"), TextoRtf = Json.S(o, "textoRtf"), Usos = Json.I(o, "usos"), UltimoUso = Json.F(o, "ultimoUso"), Etiqueta = Json.S(o, "etiqueta") };
                    p.Mensaje = Envio.Leer(o, p.TextoHtml, p.TextoPlano, p.TextoRtf);
                    c.Lista.Add(p);
                }
            if (c.Lista.Count == 0 && d == null) { c.Lista = Semilla(); c.Guardar(); }
            return c;
        }
        public void Guardar()
        {
            try
            {
                Json.Escribir(ruta, new Dictionary<string, object>
                {
                    ["_ayuda"] = "Mensajes propios listos para mandar con un clic. {nombre} se reemplaza por el nombre de pila del destinatario.",
                    ["mensajes"] = Lista.Select(p =>
                    {
                        p.SincronizarCamposViejos();
                        return new Dictionary<string, object> { ["id"] = p.Id, ["nombre"] = p.Nombre, ["para"] = p.Para, ["etiqueta"] = p.Etiqueta, ["textoPlano"] = p.TextoPlano, ["textoHtml"] = p.TextoHtml, ["textoRtf"] = p.TextoRtf, ["partes"] = p.Mensaje.Guardar(), ["usos"] = p.Usos, ["ultimoUso"] = p.UltimoUso };
                    }).ToList()
                });
            }
            catch { }
        }
        static Personalizado P(string nombre, string etiqueta, string html, string para = "")
        {
            var r = Rico.DesdeHtml(html);
            var p = new Personalizado { Nombre = nombre, Etiqueta = etiqueta, Para = para, TextoHtml = html, TextoPlano = r.Plano() };
            p.Mensaje = Envio.DeTexto(html, p.TextoPlano);
            return p;
        }

        static List<Personalizado> Semilla() => new List<Personalizado>
        {
            // --- cortesía / avisos del día
            P("buen día", "cortesía", "Buen día {nombre}! Ya estoy conectado, cualquier cosa avisame."),
            P("llego tarde", "cortesía", "Buen día! <b>Llego un rato más tarde</b>, cualquier cosa me escriben."),
            P("voy al médico", "cortesía", "Aviso que hoy tengo <b>turno médico</b> y me desconecto un rato. Vuelvo en cuanto pueda."),
            P("salgo a almorzar", "cortesía", "Salgo a almorzar, vuelvo en un rato. Si es urgente llamame."),
            P("me desconecto un rato", "cortesía", "Me desconecto un rato, <i>sigo viendo los mensajes</i> pero tardo en contestar."),
            P("ya vuelvo", "cortesía", "Ya vuelvo, me levanto cinco minutos."),
            P("me voy por hoy", "cortesía", "Me voy por hoy. Mañana sigo con lo que quedó pendiente."),
            P("buen finde", "cortesía", "Listo por esta semana, <b>buen finde</b> para todos!"),
            P("estoy con el celular", "cortesía", "Estoy probando en el celular, veo el monitor pero no puedo escribir bien. Ya te contesto."),
            // --- tickets y desarrollo
            P("pedir aprobación", "trabajo", "{nombre}, cuando puedas: necesito la <b>aprobación del pasaje</b>. Los cambios están probados en UAT."),
            P("ticket a coding done", "trabajo", "{nombre}, te pasé el ticket a <b>coding done</b>. Está probado en local, listo para UAT."),
            P("ticket a resolved", "trabajo", "Pasé el ticket a <b>resolved</b>: probado en UAT y funcionando."),
            P("pedir más tickets", "trabajo", "{nombre}, terminé lo que tenía. <b>¿Me pasás algo más?</b>"),
            P("subí la rama", "trabajo", "Subí la rama con el fix. Cuando puedas le pegás una mirada.<br>- se probó en local<br>- no toca base de datos"),
            P("necesito review", "trabajo", "¿Me hacés un <b>review</b> cuando tengas un rato? Es un cambio chico."),
            P("estoy bloqueado", "trabajo", "Estoy <b>bloqueado</b> con esto, ¿tenés cinco minutos para mirarlo conmigo?"),
            P("pedir permisos", "trabajo", "No tengo permisos para esto, ¿me los podés habilitar vos?"),
            P("pregunto por el backend", "trabajo", "{nombre}, consulta del backend: ¿esto es un bug o está definido así?"),
            P("pregunto por el pipeline", "trabajo", "{nombre}, el deploy no anda. ¿Le podés pegar una mirada al pipeline?"),
            // --- QA y pruebas
            P("listo para probar", "QA", "Ya está en UAT, <b>listo para probar</b>. Avisame si ves algo raro."),
            P("probado en local", "QA", "Probado en local de punta a punta, funcionando."),
            P("probado en UAT", "QA", "Probado en <b>UAT</b> contra datos reales, todo ok."),
            P("encontré un bug", "QA", "Encontré un bug, te paso el detalle:<br>- qué hice<br>- qué esperaba<br>- qué pasó"),
            P("no puedo reproducir", "QA", "No lo puedo reproducir de mi lado. ¿Me pasás los pasos exactos y la cuenta?"),
            P("pedir video del bug", "QA", "¿Me pasás un <b>video o captura</b> del error? Así lo veo tal cual te pasa."),
            P("no tocar UAT", "QA", "Ojo que <b>los QA están probando en UAT</b>, mejor no tocar nada ahí ahora."),
            // --- deploy y ambientes
            P("qué hay desplegado", "deploy", "¿Me confirmás <b>qué versión está desplegada</b> en cada ambiente?"),
            P("pasaje pedido", "deploy", "Pedí el pasaje a producción. Aviso cuando esté aprobado."),
            P("deploy terminado", "deploy", "Deploy terminado y verificado. <b>Todo funcionando.</b>"),
            // --- reuniones
            P("no puedo entrar", "reunión", "No puedo entrar a la call, se me colgó Teams. Ya reintento."),
            P("me quedé sin audio", "reunión", "Me quedé sin audio, salgo y vuelvo a entrar."),
            P("empiecen sin mí", "reunión", "Arranquen sin mí que me demoro cinco minutos."),
            P("cancelo la reunión", "reunión", "Voy a tener que <b>cancelar la reunión</b> de hoy, la reprogramo."),
            // --- soporte / casos de clientes
            P("pedir datos de la cuenta", "soporte", "{nombre}, ¿me pasás el <b>identificador</b> o el mail de la cuenta? Con eso la busco."),
            P("caso resuelto", "soporte", "Ya está resuelto el caso, la cuenta quedó funcionando. Cualquier cosa avisame."),
            P("lo estoy mirando", "soporte", "Lo estoy mirando, en un rato te confirmo."),
        };
    }

    /// <summary>El cron: cada 15 s ejecuta los recordatorios vencidos (mensaje a un compañero por Teams o aviso propio).</summary>
    internal sealed class Programador
    {
        readonly Config cfg;
        readonly Logger log;
        readonly Recordatorios lista;
        readonly Contactos contactos;
        readonly TeamsChat chat;
        readonly Autocontestador auto;
        Thread hilo;
        volatile bool parar;
        readonly AutoResetEvent despertar = new AutoResetEvent(false);
        public event Action Cambio;
        public event Action<Recordatorio, bool, string> Ejecutado;
        public event Action<Recordatorio> AvisoPropio;
        public int Enviados { get; private set; }
        public int Fallos { get; private set; }
        public DateTime? UltimaEjecucion { get; private set; }
        public bool Ocupado { get; private set; }

        public Programador(Config c, Logger l, Recordatorios r, Contactos cs, TeamsChat t, Autocontestador a) { cfg = c; log = l; lista = r; contactos = cs; chat = t; auto = a; }

        public void Iniciar()
        {
            if (hilo != null) return;
            hilo = new Thread(Bucle) { IsBackground = true, Name = "cron-recordatorios" };
            hilo.SetApartmentState(ApartmentState.MTA);
            hilo.Start();
        }
        public void Detener() { parar = true; despertar.Set(); }
        public void Despertar() => despertar.Set();

        void Bucle()
        {
            int pend = lista.Lista.Count(r => r.Activo && r.Proximo != null);
            log.Info($"Cron listo · {pend} recordatorio/s programado/s" + (pend > 0 ? $" · próximo {lista.Proximos().First().ResumenCuando()}" : ""));
            while (!parar)
            {
                try
                {
                    var ahora = DateTime.Now;
                    foreach (var r in lista.Pendientes(ahora).ToList())
                    {
                        if (parar) break;
                        Ejecutar(r);
                    }
                }
                catch (Exception ex) { log.Error("Cron: " + ex.Message); }
                try { Cambio?.Invoke(); } catch { }
                despertar.WaitOne(15000);
            }
        }

        /// <summary>Ejecuta un recordatorio ya (lo usa el cron y el boton "mandar ahora").</summary>
        public bool Ejecutar(Recordatorio r, bool manual = false)
        {
            if (auto != null && auto.Ocupado) { log.Debug("Cron: el autocontestador está enviando; espero"); return false; }
            Ocupado = true;
            bool ok = false; string detalle = "";
            try
            {
                UltimaEjecucion = DateTime.Now;
                if (r.ParaMi)
                {
                    ok = true; detalle = "aviso propio";
                    r.Anotar(true, detalle, 0);
                    log.Ok($"Recordatorio para vos: «{r.TextoPlano}»");
                    try { AvisoPropio?.Invoke(r); } catch { }
                }
                else
                {
                    // queda anotado en el registro central: la barra de titulo muestra que esto esta trabajando
                    var reloj = System.Diagnostics.Stopwatch.StartNew();
                    using (Tareas.Empezar("mandando un recordatorio", "a " + r.Para, Tema.Cielo))
                    {
                        var res = EnviarA(r.Para, r.Correo, r.MensajeEfectivo());
                        ok = res.Ok; detalle = res.Detalle;
                    }
                    r.Anotar(ok, detalle, (int)reloj.ElapsedMilliseconds);
                    if (ok) log.Ok($"Cron: le mandé a «{r.Para}»: «{Corto(r.TextoPlano)}» ({detalle})");
                    else log.Error($"Cron: no pude mandarle a «{r.Para}»: {detalle}");
                }
                r.UltimoResultado = (ok ? "ok · " : "falló · ") + detalle;
                if (ok)
                {
                    r.UltimoEnvio = DateTime.Now; r.Enviados++; Enviados++;
                    if (!manual || r.Proximo <= DateTime.Now)
                    {
                        if (r.EsRecurrente) r.Proximo = Agenda.Proxima(DateTime.Now, r.Repetir, r.Hora);
                        else { r.Activo = false; }
                    }
                }
                else
                {
                    r.Fallos++; Fallos++;
                    if (!manual)
                    {
                        if (r.Fallos % 3 == 0) { if (r.EsRecurrente) r.Proximo = Agenda.Proxima(DateTime.Now, r.Repetir, r.Hora); else r.Activo = false; log.Aviso($"Cron: «{r.Para}» falló 3 veces, {(r.EsRecurrente ? "salto a la próxima repetición" : "lo desactivo")}"); }
                        else r.Proximo = DateTime.Now.AddMinutes(5);
                    }
                }
                lista.Guardar();
                try { Ejecutado?.Invoke(r, ok, detalle); } catch { }
            }
            finally { Ocupado = false; }
            return ok;
        }

        /// <summary>Manda un mensaje a un compañero: abre su chat (lista, o deep link por correo) y envia con o sin formato.</summary>
        public ResultadoSalida EnviarA(string nombreTeams, string correo, string html, string plano)
        {
            string h = html.Length > 0 ? html : System.Net.WebUtility.HtmlEncode(plano).Replace("\n", "<br>");
            return EnviarA(nombreTeams, correo, Envio.DeTexto(h, plano));
        }

        /// <summary>
        /// Igual que el anterior pero con un ENVÍO de varias partes (textos + imágenes): abre el chat una sola vez y
        /// manda cada parte como su propia burbuja. Los marcadores ({nombre}, {hora}…) se rellenan parte por parte.
        /// </summary>
        public ResultadoSalida EnviarA(string nombreTeams, string correo, Envio envio)
        {
            var r = new ResultadoSalida();
            if (envio == null || envio.Partes.Count == 0) { r.Detalle = "mensaje vacío"; return r; }
            // 🚨 mismo acto atómico que el autocontestador: abrir el chat y mandar sin que otro hilo se cuele
            using (chat.Gate.Entrar("cron → " + nombreTeams))
            {
            if (!chat.BuscarVentana() && !chat.Despertar()) { r.Detalle = "sin ventana de chat de Teams"; return r; }
            var contacto = contactos.PorNombreTeams(nombreTeams) ?? contactos.Mejor(nombreTeams);
            string nombre = contacto?.Nombre ?? nombreTeams;
            string mail = correo.Length > 0 ? correo : contacto?.Correo ?? "";
            var faltan = envio.AdjuntosFaltantes();
            if (faltan.Count > 0) log.Aviso($"Envío a «{nombre}»: {faltan.Count} adjunto/s ya no están en disco ({string.Join(", ", faltan.Take(3))})");
            bool abierto = false;
            var item = chat.ListarChats().FirstOrDefault(c => Contactos.Normalizar(c.Nombre) == Contactos.Normalizar(nombre) && c.Tipo != "canal");
            if (item != null) abierto = chat.AbrirChat(item);
            if (!abierto && mail.Length > 0) { log.Info($"«{nombre}» no está en la lista de chats: abro por correo ({mail})"); abierto = chat.AbrirChatPorCorreo(mail); }
            if (!abierto) { r.Detalle = $"no pude abrir el chat con «{nombre}»" + (mail.Length == 0 ? " (sin correo para el deep link)" : ""); return r; }
            Thread.Sleep(400);
            var listo = envio.Rellenar(t => ConfigRespuestas.Rellenar(t, nombre, chat.MiNombre));
            return chat.EnviarEnvio(listo, nombre);
            }
        }

        static string Corto(string s) => string.IsNullOrEmpty(s) ? "" : (s = s.Replace("\n", " ⏎ ")).Length > 70 ? s.Substring(0, 70) + "…" : s;
    }
}
