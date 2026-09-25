using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace TeamsTools
{
    internal enum Estado { SinTeams, SinLlamada, EnLlamada, EsperandoGente, SalaVacia, Pospuesto, Saliendo, Salido, Pausado }

    /// <summary>Foto inmutable del vigia para la interfaz.</summary>
    internal sealed class Vista
    {
        public Estado Estado;
        public Lectura Lectura;
        public DateTime? VacioDesde;
        public int SegundosRestantes;
        public DateTime? PospuestoHasta;
        public DateTime? InicioSesion;
        public int MaxOtros;
        public bool GenteVista;
        public int Salidas;
        public int Intentos;
        public bool Simulacion;
        public string Motivo = "";
        public DateTime UltimaLectura;

        public string EstadoTexto
        {
            get
            {
                switch (Estado)
                {
                    case Estado.SinTeams: return "TEAMS NO ESTÁ ABIERTO";
                    case Estado.SinLlamada: return "SIN LLAMADA · VIGILANDO";
                    case Estado.EnLlamada: return "EN LA REUNIÓN · VIGILANDO";
                    case Estado.EsperandoGente: return "SOLO EN LA SALA · ESPERANDO GENTE";
                    case Estado.SalaVacia: return "LA SALA SE VACIÓ";
                    case Estado.Pospuesto: return "SALA VACÍA · POSPUESTO";
                    case Estado.Saliendo: return "SALIENDO…";
                    case Estado.Salido: return "SALÍ DE LA REUNIÓN";
                    case Estado.Pausado: return "EN PAUSA";
                }
                return Estado.ToString();
            }
        }
    }

    /// <summary>
    /// El vigia: lee Teams cada N segundos y decide. Corre en su propio hilo MTA
    /// (UI Automation prefiere MTA). Todo lo que sale hacia la UI va por eventos.
    /// </summary>
    internal sealed class Watcher
    {
        readonly Config cfg;
        readonly Logger log;
        readonly ILector scanner;
        readonly string rutaEstado;
        public Presencia Presencia;   // opcional: para volcar su estado en estado.json
        public Func<Dictionary<string, object>> Extras;   // mas datos para estado.json (mensajes, cron)
        Thread hilo;
        volatile bool parar;
        volatile bool pausado;
        volatile bool pedidoSalirAhora;
        volatile bool pedidoLeer;
        readonly AutoResetEvent despertar = new AutoResetEvent(false);
        readonly object candado = new object();
        Sesion sesion;
        Estado estado = Estado.SinTeams;
        Lectura ultima = new Lectura();
        int salidas;

        public event Action<Vista> Cambio;
        public event Action<Vista> SalaVaciada;
        public event Action<Vista> CuentaCancelada;
        public event Action<Vista, bool, string> SalidaHecha;

        sealed class Sesion
        {
            public IntPtr Hwnd;
            public string Reunion = "";
            public DateTime Inicio = DateTime.Now;
            public int MaxOtros;
            public bool GenteVista;
            public DateTime? VacioDesde;
            public DateTime? PospuestoHasta;
            public bool CanceladoEstaVez;
            public int Intentos;
            public DateTime? UltimoIntento;
            public bool SalidaOk;
            public bool AvisadoNadieLlega;
            public bool RosterFrenó;
            public string Motivo = "";
        }

        public Watcher(Config c, Logger l, ILector s, string rutaEstadoJson)
        {
            cfg = c; log = l; scanner = s; rutaEstado = rutaEstadoJson;
        }

        public bool Pausado => pausado;
        public Vista Foto() { lock (candado) return Snapshot(); }

        public void Iniciar()
        {
            if (hilo != null) return;
            hilo = new Thread(Bucle) { IsBackground = true, Name = "vigia-teams" };
            hilo.SetApartmentState(ApartmentState.MTA);
            hilo.Start();
        }

        public void Detener() { parar = true; despertar.Set(); }
        public void LeerYa() { pedidoLeer = true; despertar.Set(); }

        public void Pausar(bool p)
        {
            pausado = p;
            log.Info(p ? "Vigilancia en pausa" : "Vigilancia reanudada");
            despertar.Set();
        }

        /// <summary>Salir de la reunion ya mismo (accion manual del user).</summary>
        public void SalirAhora() { pedidoSalirAhora = true; despertar.Set(); }

        public void Posponer()
        {
            lock (candado)
            {
                if (sesion == null) return;
                sesion.PospuestoHasta = DateTime.Now.AddMinutes(cfg.PosponerMinutos);
                sesion.VacioDesde = null;
                log.Info($"Pospuesto {cfg.PosponerMinutos} min: no salgo hasta las {sesion.PospuestoHasta:HH:mm}");
            }
            despertar.Set();
        }

        public void CancelarEstaVez()
        {
            lock (candado)
            {
                if (sesion == null) return;
                sesion.CanceladoEstaVez = true;
                sesion.VacioDesde = null;
                log.Info("Cancelado: me quedo en esta reunión aunque esté vacía (hasta que vuelva a entrar gente)");
            }
            despertar.Set();
        }

        void Bucle()
        {
            log.Info($"Vigía iniciado · cada {cfg.IntervaloSegundos} s · gracia {cfg.GraciaSegundos} s · {(cfg.Simulacion ? "SIMULACIÓN" : "salida real")} · método {cfg.MetodoSalida}");
            while (!parar)
            {
                try
                {
                    if (pausado && !pedidoSalirAhora)
                    {
                        lock (candado) { estado = Estado.Pausado; }
                        Publicar();
                        despertar.WaitOne(1000);
                        continue;
                    }
                    var L = scanner.Leer();
                    lock (candado) { ultima = L; Procesar(L); }
                    if (pedidoSalirAhora) { pedidoSalirAhora = false; SalidaManual(L); }
                    Publicar();
                }
                catch (Exception ex)
                {
                    log.Error("Error en el vigía: " + ex.Message);
                }
                int espera = Math.Max(1, cfg.IntervaloSegundos) * 1000;
                lock (candado) { if (estado == Estado.SalaVacia || estado == Estado.Saliendo) espera = Math.Min(espera, 1000); }
                if (pedidoLeer) { pedidoLeer = false; continue; }
                despertar.WaitOne(espera);
            }
            log.Info("Vigía detenido");
        }

        void Procesar(Lectura L)
        {
            var ahora = DateTime.Now;
            if (!L.TeamsCorriendo)
            {
                if (sesion != null) { log.Info("Teams se cerró"); sesion = null; }
                estado = Estado.SinTeams;
                return;
            }
            if (!L.HayLlamada)
            {
                if (sesion != null)
                {
                    var dur = ahora - sesion.Inicio;
                    if (sesion.SalidaOk) log.Ok($"Confirmado: ya no estoy en «{sesion.Reunion}»");
                    else if (sesion.Intentos > 0) log.Ok($"La llamada «{sesion.Reunion}» terminó (después de {sesion.Intentos} intento/s de salida)");
                    else log.Info($"La llamada «{sesion.Reunion}» terminó · duró {Fmt(dur)} · máximo {sesion.MaxOtros} otros");
                    sesion = null;
                }
                estado = Estado.SinLlamada;
                return;
            }

            if (sesion == null || !string.Equals(sesion.Reunion, L.Reunion, StringComparison.Ordinal))
            {
                sesion = new Sesion { Hwnd = L.Hwnd, Reunion = L.Reunion };
                log.Info($"Reunión detectada: «{L.Reunion}» · {L.Otros} otros{(L.Pagina.Length > 0 ? " · pág " + L.Pagina : "")}{(L.Minimizada ? " · ventana minimizada" : "")}{(L.EsCompacta ? " · solo vista compacta" : "")}");
            }
            sesion.Hwnd = L.Hwnd;

            if (L.Otros > 0 || L.HayCompartido)
            {
                bool volvio = sesion.VacioDesde != null;
                if (!sesion.GenteVista) log.Info(L.Otros > 0 ? $"Hay gente en la sala: {L.Otros} ({L.ResumenNombres()})" : "Alguien comparte contenido: hay gente aunque no vea fichas");
                sesion.GenteVista = true;
                sesion.MaxOtros = Math.Max(sesion.MaxOtros, Math.Max(L.Otros, 1));
                sesion.VacioDesde = null;
                sesion.CanceladoEstaVez = false;
                sesion.RosterFrenó = false;
                sesion.Motivo = "";
                if (volvio) { log.Ok($"Volvió gente ({L.Otros}: {L.ResumenNombres()}) · cuenta regresiva cancelada"); estado = Estado.EnLlamada; Disparar(CuentaCancelada); return; }
                estado = Estado.EnLlamada;
                return;
            }

            // --- nadie mas en la sala ---
            if (!sesion.GenteVista && cfg.RequiereHaberVistoGente)
            {
                bool nadieLlega = cfg.SalirSiNadieLlegaMinutos > 0 && (ahora - sesion.Inicio).TotalMinutes >= cfg.SalirSiNadieLlegaMinutos;
                if (!nadieLlega) { estado = Estado.EsperandoGente; return; }
                if (!sesion.AvisadoNadieLlega) { sesion.AvisadoNadieLlega = true; sesion.Motivo = $"nadie llegó en {cfg.SalirSiNadieLlegaMinutos} min"; log.Aviso($"Nadie llegó en {cfg.SalirSiNadieLlegaMinutos} min: la trato como sala vacía"); }
            }
            if (sesion.PospuestoHasta != null && ahora < sesion.PospuestoHasta.Value) { estado = Estado.Pospuesto; return; }
            if (sesion.PospuestoHasta != null && ahora >= sesion.PospuestoHasta.Value) { sesion.PospuestoHasta = null; log.Info("Se cumplió la posposición y la sala sigue vacía"); }
            if (sesion.CanceladoEstaVez) { estado = Estado.EnLlamada; return; }
            if (sesion.RosterFrenó) { estado = Estado.EnLlamada; return; }

            if (sesion.VacioDesde == null)
            {
                sesion.VacioDesde = ahora;
                if (sesion.Motivo.Length == 0) sesion.Motivo = $"había {sesion.MaxOtros} y se fueron todos";
                log.Alerta($"La sala se vació ({sesion.Motivo}) · salgo en {cfg.GraciaSegundos} s");
                estado = Estado.SalaVacia;
                Disparar(SalaVaciada);
                return;
            }
            int restantes = cfg.GraciaSegundos - (int)(ahora - sesion.VacioDesde.Value).TotalSeconds;
            if (restantes > 0) { estado = Estado.SalaVacia; return; }

            if (sesion.SalidaOk) { estado = Estado.Salido; return; }
            if (sesion.Intentos >= 3)
            {
                if (sesion.Intentos == 3) { log.Error("Tres intentos de salida fallaron; dejo de insistir en esta reunión"); sesion.Intentos++; }
                estado = Estado.SalaVacia;
                return;
            }
            if (sesion.UltimoIntento != null && (ahora - sesion.UltimoIntento.Value).TotalSeconds < 20) { estado = Estado.Saliendo; return; }
            EjecutarSalida(L, false);
        }

        void EjecutarSalida(Lectura L, bool manual)
        {
            estado = Estado.Saliendo;
            Publicar();
            if (!manual && cfg.VerificarConRoster && !L.EsCompacta)
            {
                int? n = scanner.VerificarRoster(L.Hwnd, out string det);
                if (n != null)
                {
                    log.Info($"Panel Gente: {n} en la reunión ({det})");
                    if (n.Value > 1)
                    {
                        log.Aviso($"El panel Gente muestra {n.Value} personas: NO salgo (las fichas decían 0)");
                        sesion.RosterFrenó = true; sesion.GenteVista = true; sesion.VacioDesde = null;
                        estado = Estado.EnLlamada;
                        Disparar(CuentaCancelada);
                        return;
                    }
                }
                else log.Debug("No pude leer el panel Gente (" + det + "); sigo con lo que dicen las fichas");
            }
            if (cfg.Simulacion && !manual)
            {
                sesion.SalidaOk = true;
                log.Alerta($"SIMULACIÓN: acá habría salido de «{sesion.Reunion}» (no toco nada)");
                estado = Estado.Salido;
                try { SalidaHecha?.Invoke(Snapshot(), true, "simulación"); } catch { }
                return;
            }
            sesion.Intentos++;
            sesion.UltimoIntento = DateTime.Now;
            var r = scanner.Salir(L.Hwnd, cfg.MetodoSalida);
            if (r.Ok)
            {
                sesion.SalidaOk = true; salidas++;
                log.Ok($"Salí de «{sesion.Reunion}» · {r.Detalle} · {(manual ? "a pedido" : sesion.Motivo)}");
                estado = Estado.Salido;
            }
            else
            {
                log.Error($"No pude salir de «{sesion.Reunion}»: {r.Detalle} (intento {sesion.Intentos}/3)");
                estado = Estado.SalaVacia;
            }
            try { SalidaHecha?.Invoke(Snapshot(), r.Ok, r.Detalle); } catch { }
        }

        void SalidaManual(Lectura L)
        {
            lock (candado)
            {
                if (!L.HayLlamada) { log.Aviso("Pediste salir pero no veo ninguna llamada"); return; }
                if (sesion == null) sesion = new Sesion { Hwnd = L.Hwnd, Reunion = L.Reunion };
                sesion.Motivo = "a pedido";
                log.Info($"Salida manual pedida de «{L.Reunion}»");
                EjecutarSalida(L, true);
            }
        }

        void Disparar(Action<Vista> ev)
        {
            if (ev == null) return;
            var v = Snapshot();
            ThreadPool.QueueUserWorkItem(_ => { try { ev(v); } catch { } });
        }

        Vista Snapshot()
        {
            var v = new Vista
            {
                Estado = estado, Lectura = ultima, Salidas = salidas, Simulacion = cfg.Simulacion, UltimaLectura = ultima.Hora
            };
            if (sesion != null)
            {
                v.VacioDesde = sesion.VacioDesde; v.PospuestoHasta = sesion.PospuestoHasta; v.InicioSesion = sesion.Inicio;
                v.MaxOtros = sesion.MaxOtros; v.GenteVista = sesion.GenteVista; v.Intentos = sesion.Intentos; v.Motivo = sesion.Motivo;
                if (sesion.VacioDesde != null) v.SegundosRestantes = Math.Max(0, cfg.GraciaSegundos - (int)(DateTime.Now - sesion.VacioDesde.Value).TotalSeconds);
            }
            return v;
        }

        void Publicar()
        {
            Vista v; lock (candado) v = Snapshot();
            EscribirEstado(v);
            try { Cambio?.Invoke(v); } catch { }
        }

        void EscribirEstado(Vista v)
        {
            if (string.IsNullOrEmpty(rutaEstado)) return;
            try
            {
                var L = v.Lectura ?? new Lectura();
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine($"  \"hora\": \"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\",");
                sb.AppendLine($"  \"estado\": \"{v.Estado}\",");
                sb.AppendLine($"  \"estadoTexto\": {Config.Json(v.EstadoTexto)},");
                sb.AppendLine($"  \"teams\": {(L.TeamsCorriendo ? "true" : "false")},");
                sb.AppendLine($"  \"ventanasTeams\": {L.VentanasTeams},");
                sb.AppendLine($"  \"hayLlamada\": {(L.HayLlamada ? "true" : "false")},");
                sb.AppendLine($"  \"reunion\": {Config.Json(L.Reunion)},");
                sb.AppendLine($"  \"hwnd\": {L.Hwnd.ToInt64()},");
                sb.AppendLine($"  \"ventanaMinimizada\": {(L.Minimizada ? "true" : "false")},");
                sb.AppendLine($"  \"vistaCompacta\": {(L.EsCompacta ? "true" : "false")},");
                sb.AppendLine($"  \"duracion\": {Config.Json(L.Duracion)},");
                sb.AppendLine($"  \"otros\": {L.Otros},");
                sb.AppendLine($"  \"fichasTotal\": {L.FichasTotal},");
                sb.AppendLine($"  \"hayCompartido\": {(L.HayCompartido ? "true" : "false")},");
                sb.AppendLine($"  \"pagina\": {Config.Json(L.Pagina)},");
                sb.AppendLine($"  \"propioVisto\": {(L.PropioVisto ? "true" : "false")},");
                sb.AppendLine($"  \"nombrePropio\": {Config.Json(L.NombrePropio)},");
                sb.AppendLine($"  \"rosterTotal\": {(L.RosterTotal.HasValue ? L.RosterTotal.Value.ToString() : "null")},");
                sb.AppendLine($"  \"nombres\": [{string.Join(", ", L.Nombres.Select(Config.Json))}],");
                sb.AppendLine($"  \"maxOtros\": {v.MaxOtros},");
                sb.AppendLine($"  \"genteVista\": {(v.GenteVista ? "true" : "false")},");
                sb.AppendLine($"  \"vacioDesde\": {(v.VacioDesde.HasValue ? Config.Json(v.VacioDesde.Value.ToString("HH:mm:ss")) : "null")},");
                sb.AppendLine($"  \"segundosRestantes\": {v.SegundosRestantes},");
                sb.AppendLine($"  \"pospuestoHasta\": {(v.PospuestoHasta.HasValue ? Config.Json(v.PospuestoHasta.Value.ToString("HH:mm:ss")) : "null")},");
                sb.AppendLine($"  \"intentos\": {v.Intentos},");
                sb.AppendLine($"  \"salidas\": {v.Salidas},");
                sb.AppendLine($"  \"simulacion\": {(v.Simulacion ? "true" : "false")},");
                sb.AppendLine($"  \"motivo\": {Config.Json(v.Motivo)},");
                sb.AppendLine($"  \"lecturaMs\": {L.Ms},");
                var pr = Presencia;
                sb.AppendLine($"  \"presenciaActiva\": {(pr != null && pr.Activa ? "true" : "false")},");
                sb.AppendLine($"  \"presenciaIdleSegundos\": {(pr != null ? pr.IdleSegundos : 0)},");
                sb.AppendLine($"  \"presenciaToques\": {(pr != null ? pr.Toques : 0)},");
                sb.AppendLine($"  \"presenciaUltimoToque\": {(pr != null && pr.UltimoToque.HasValue ? Config.Json(pr.UltimoToque.Value.ToString("HH:mm:ss")) : "null")},");
                sb.AppendLine($"  \"presenciaMetodo\": {Config.Json(pr != null ? pr.MetodoQueFunciona : "")},");
                sb.AppendLine($"  \"presenciaFallos\": {(pr != null ? pr.Fallos : 0)},");
                try
                {
                    var ex = Extras?.Invoke();
                    if (ex != null) foreach (var kv in ex) sb.AppendLine($"  \"{kv.Key}\": {Json.Texto(kv.Value)},");
                }
                catch { }
                sb.AppendLine($"  \"error\": {Config.Json(L.Error)}");
                sb.AppendLine("}");
                Disco.Escribir(rutaEstado, sb.ToString());
            }
            catch { }
        }

        public static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
