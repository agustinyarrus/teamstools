using System;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace TeamsTools
{
    /// <summary>
    /// Mantiene la presencia de Teams en "Disponible": Teams pasa a Ausente cuando el sistema lleva
    /// 5 minutos sin entrada de usuario. Cuando la inactividad supera el umbral, inyecta una entrada
    /// inocua (movimiento de mouse de 0 px; si no alcanza, 1 px ida y vuelta; si no, la tecla F15)
    /// y comprueba con GetLastInputInfo que el reloj de inactividad se reinicio de verdad.
    /// Nunca actua mientras el user esta usando la maquina: solo cuando ya paso el umbral sin tocar nada.
    /// El "Ausente" queda manual: apagar el toggle o ponerlo desde el avatar de Teams.
    /// </summary>
    internal sealed class Presencia
    {
        [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
        const uint MOUSEEVENTF_MOVE = 0x0001;
        const ushort VK_F15 = 0x7E;

        readonly Config cfg;
        readonly Logger log;
        Thread hilo;
        volatile bool parar;
        readonly AutoResetEvent despertar = new AutoResetEvent(false);
        public event Action Cambio;

        public bool Activa => cfg.PresenciaSiempreOnline;
        public int IdleSegundos { get; private set; }
        public int Toques { get; private set; }
        public DateTime? UltimoToque { get; private set; }
        public string MetodoQueFunciona { get; private set; } = "";
        public string UltimoResultado { get; private set; } = "";
        public int Fallos { get; private set; }

        // --- permanencia online de verdad: medir, prevenir y corregir ---
        public TeamsChat Chat;                                   // opcional: para leer el estado REAL del avatar
        public TeamsGate Gate;                                   // la aduana: sólo para medir/rescatar, NUNCA para la F15
        public string PresenciaTeams { get; private set; } = ""; // lo que Teams muestra ahora mismo
        public DateTime? UltimaMedicion { get; private set; }
        public int Derivas { get; private set; }                 // veces que Teams se nos fue a Ausente igual
        public int Correcciones { get; private set; }            // veces que lo trajimos de vuelta
        public bool Bloqueada { get; private set; }              // sesión bloqueada: ningún input sirve
        public bool EnHorario { get; private set; } = true;      // dentro del horario laboral configurado
        public bool PantallaDespierta { get; private set; }
        public int ProximoToqueEn { get; private set; }          // segundos que faltan para el próximo toque
        public string MedicionDetalle { get; private set; } = "";
        public string FuentePresencia { get; private set; } = "";  // "log nativo" | "avatar" | "sonda profunda"
        public DateTime? PresenciaDesde { get; private set; }      // desde cuándo estás en ese estado (lo sabe el log)
        /// <summary>
        /// true si lo que mostramos vale. El log nativo es por eventos: mientras Teams corra, el último evento
        /// SIGUE siendo el estado actual por viejo que sea. La lectura por UIA, en cambio, caduca a los 90 s.
        /// </summary>
        public bool MedicionFresca => PresenciaTeams.Length > 0 &&
            (FuentePresencia == "log nativo"
                ? (Chat == null || Chat.TeamsCorriendo)
                : UltimaMedicion.HasValue && (DateTime.Now - UltimaMedicion.Value).TotalSeconds <= 90);
        public int Sondas { get; private set; }                   // veces que hubo que remontar Teams para poder leer
        public int Forzados { get; private set; }                 // veces que hubo que fijar el estado en el menú del avatar
        public string UltimoRescate { get; private set; } = "";   // "toque" | "fijar Disponible"
        public string RescateDetalle { get; private set; } = "";
        public bool Rendido { get; private set; }                 // Teams vuelve a Ausente pase lo que pase: estamos en pausa
        public DateTime? UltimoInputReal { get; private set; }    // última vez que el user tocó algo DE VERDAD (no nuestra F15)
        public bool RespetandoManual { get; private set; }        // el Ausente lo puso él: no se toca
        public DateTime EsperarHasta => esperarHasta;
        public LectorPresenciaLog LogTeams;                       // fuente de verdad sin tocar nada (opcional)
        /// <summary>
        /// 🚨 Jerarquía explícita con el guión de presencia. Si el guión está manejando y quiere un estado que NO
        /// es Disponible, acá no se toca ni se rescata: manda él. Dos lazos de corrección opinando sobre el mismo
        /// estado sin jerarquía no se pelean una vez, se pelean para siempre, y el user ve la presencia parpadear.
        /// </summary>
        public Func<string> EstadoDeseado;
        public string QuiereElGuion { get { try { return EstadoDeseado != null ? (EstadoDeseado() ?? "") : ""; } catch { return ""; } } }
        /// <summary>true si el guión está pidiendo un estado distinto de Disponible.</summary>
        public bool GuionManda { get { var q = QuiereElGuion; return q.Length > 0 && !GuionPresencia.EsOnline(q); } }
        bool ausentePendiente;
        int intentosRescate, idleAnterior;
        DateTime ultimaMedicionInterna = DateTime.MinValue, ultimaSonda = DateTime.MinValue, esperarHasta = DateTime.MinValue, respetarHasta = DateTime.MinValue;
        readonly Random azar = new Random();

        static readonly Regex ReAusente = new Regex(@"^(ausente|away|vuelvo|be right back)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ReDisponible = new Regex(@"^(disponible|available)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public bool TeamsDiceAusente => ReAusente.IsMatch(PresenciaTeams);
        public bool TeamsDiceDisponible => ReDisponible.IsMatch(PresenciaTeams);

        public Presencia(Config c, Logger l) { cfg = c; log = l; }

        public static int IdleAhora()
        {
            var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref li)) return 0;
            uint ahora = unchecked((uint)Environment.TickCount);
            return (int)(unchecked(ahora - li.dwTime) / 1000);
        }

        public void Iniciar()
        {
            if (hilo != null) return;
            hilo = new Thread(Bucle) { IsBackground = true, Name = "presencia-teams" };
            hilo.Start();
        }
        public void Detener() { parar = true; try { if (PantallaDespierta) Win32.MantenerDespierto(false); } catch { } despertar.Set(); }
        public void Despertar() => despertar.Set();

        void Bucle()
        {
            log.Info(cfg.PresenciaSiempreOnline ? $"Presencia: me mantengo Disponible con {(cfg.PresenciaMetodo == "f15" ? "la tecla fantasma F15 (sin mouse)" : "método " + cfg.PresenciaMetodo)}, toque tras {cfg.PresenciaUmbralSegundos} s sin actividad" : "Presencia: apagada (Teams decide solo)");
            bool avisadoPrimero = false;
            while (!parar)
            {
                try
                {
                    IdleSegundos = IdleAhora();
                    // ⭐ distinguir el input REAL del user de nuestra propia tecla fantasma: sabemos exactamente
                    //    cuándo tocamos nosotros, así que un reinicio del reloj que no sea nuestro es él de verdad.
                    //    Es lo único que permite saber si el Ausente lo puso él a mano o se lo puso Teams solo.
                    if (IdleSegundos < idleAnterior && (!UltimoToque.HasValue || (DateTime.Now - UltimoToque.Value).TotalSeconds > 6))
                        UltimoInputReal = DateTime.Now;
                    idleAnterior = IdleSegundos;
                    Bloqueada = Win32.SesionBloqueada();
                    // capa 1: que la pantalla no se duerma — una pantalla apagada manda a Ausente aunque el idle esté en cero
                    if (cfg.PresenciaSiempreOnline && cfg.PresenciaEvitarSuspension && !Bloqueada) PantallaDespierta = Win32.MantenerDespierto(true);
                    else if (PantallaDespierta) { Win32.MantenerDespierto(false); PantallaDespierta = false; }
                    // capa 2: medir lo que Teams REALMENTE dice de vos — nunca asumirlo
                    if (cfg.PresenciaSiempreOnline && cfg.PresenciaVerificarEnTeams && (LogTeams != null || Chat != null)
                        && (DateTime.Now - ultimaMedicionInterna).TotalSeconds >= 20)
                    {
                        ultimaMedicionInterna = DateTime.Now;
                        // 🚨 medir (y su eventual rescate) abre el menú del avatar y camina el DOM: bajo la aduana
                        //    para no hacerlo encima de un envío del autocontestador. La F15 de más abajo NO se
                        //    gatea: mantenerte online no puede depender de que Teams esté libre.
                        if (Gate != null) using (Gate.Entrar("presencia: medir")) Medir();
                        else Medir();
                    }
                    if (Bloqueada)
                    {
                        // con la sesión bloqueada Windows no acepta input inyectado para el escritorio activo: avisar y no gastar toques
                        UltimoResultado = "sesión bloqueada";
                        ProximoToqueEn = 0;
                        try { Cambio?.Invoke(); } catch { }
                        despertar.WaitOne(5000);
                        continue;
                    }
                    EnHorario = cfg.EnHorario();
                    if (!EnHorario)
                    {
                        // fuera del horario laboral no sostengo nada: que Teams haga lo suyo
                        UltimoResultado = "fuera de horario";
                        ProximoToqueEn = 0;
                        if (PantallaDespierta) { Win32.MantenerDespierto(false); PantallaDespierta = false; }
                        try { Cambio?.Invoke(); } catch { }
                        despertar.WaitOne(10000);
                        continue;
                    }
                    // el guión pidiendo Ausente y nosotros mandando F15 para sostener Disponible es la misma pelea
                    if (GuionManda)
                    {
                        UltimoResultado = "el guión maneja («" + QuiereElGuion + "»)";
                        ProximoToqueEn = 0;
                        try { Cambio?.Invoke(); } catch { }
                        despertar.WaitOne(5000);
                        continue;
                    }
                    int umbral = Math.Max(15, cfg.PresenciaUmbralSegundos);
                    ProximoToqueEn = Math.Max(0, umbral - IdleSegundos);
                    if (cfg.PresenciaSiempreOnline && IdleSegundos >= umbral)
                    {
                        int antes = IdleSegundos;
                        string metodo = Tocar();
                        int despues = IdleAhora();
                        bool ok = despues < antes && despues <= 2;
                        UltimoToque = DateTime.Now;
                        if (ok)
                        {
                            Toques++;
                            UltimoResultado = $"ok · {metodo}";
                            if (MetodoQueFunciona != metodo) { MetodoQueFunciona = metodo; log.Info($"Presencia: el método que reinicia la inactividad es «{metodo}»"); }
                            if (!avisadoPrimero) { avisadoPrimero = true; log.Ok($"Presencia: primer toque tras {Fmt(antes)} sin actividad · Teams no me va a marcar Ausente"); }
                            else log.Debug($"Presencia: toque tras {Fmt(antes)} sin actividad ({metodo})");
                        }
                        else
                        {
                            Fallos++;
                            UltimoResultado = "falló";
                            if (Fallos <= 3 || Fallos % 20 == 0) log.Aviso($"Presencia: el toque no reinició la inactividad (idle {antes}→{despues} s). ¿Hay una ventana elevada al frente?");
                        }
                        IdleSegundos = IdleAhora();
                    }
                    try { Cambio?.Invoke(); } catch { }
                }
                catch (Exception ex) { log.Error("Presencia: " + ex.Message); }
                despertar.WaitOne(5000);
            }
        }

        /// <summary>
        /// Averigua qué dice Teams de vos, en tres capas y de la más delicada a la más invasiva:
        ///
        ///   1. el LOG NATIVO de Teams — no toca nada, ni ventanas ni UIA: Teams escribe cada cambio de presencia;
        ///   2. el AVATAR por UIA en cualquier ventana montada — barato, pero el árbol se cae con Teams en la bandeja;
        ///   3. la SONDA PROFUNDA — remonta las ventanas guardadas en modo invisible; cara, una cada 5 min como techo.
        ///
        /// Y solo corrige tras DOS lecturas seguidas de "Ausente": al remontarse, Teams reporta Ausente un instante
        /// antes de sincronizar con la nube, y creerle a la primera mandaba toques al pepe.
        /// </summary>
        void Medir()
        {
            string p = "", det = "", fuente = "";
            DateTime? desde = null;

            // ── capa 1: el log nativo, la vía más de fondo que existe ──────────────────────────────
            if (LogTeams != null)
            {
                try
                {
                    LogTeams.Refrescar();
                    var ev = LogTeams.Ultimo;
                    if (ev != null && ev.Estado.Length > 0 && (Chat == null || Chat.TeamsCorriendo))
                    {
                        p = ev.Estado; fuente = "log nativo"; desde = ev.Cuando;
                        det = $"«{ev.Token}» por {ev.Fuente} en {LogTeams.Archivo} a las {ev.Cuando:HH:mm:ss}";
                    }
                    else if (ev == null && LogTeams.Problema.Length > 0) det = "log: " + LogTeams.Problema;
                }
                catch (Exception ex) { det = "log: " + ex.Message; }
            }

            // ── capa 2: el avatar por UIA ──────────────────────────────────────────────────────────
            if (p.Length == 0 && Chat != null)
            {
                string d2;
                p = Chat.PresenciaReal(out d2);
                if (p.Length > 0) { fuente = "avatar"; det = d2; }
                else if (det.Length == 0) det = d2;

                // ── capa 3: remontar Teams sin mostrarlo, como mucho una vez cada 5 min ────────────
                if (p.Length == 0 && (DateTime.Now - ultimaSonda).TotalSeconds >= 300)
                {
                    ultimaSonda = DateTime.Now;
                    Sondas++;
                    p = Chat.PresenciaRealProfunda(out d2);
                    if (p.Length > 0) { fuente = "sonda profunda"; det = d2; }
                    else det = d2;
                }
            }

            if (p.Length == 0)
            {
                MedicionDetalle = det;
                // 🚨 sin lectura nueva NO se sostiene el valor viejo: si no, un "Ausente" leído una vez
                //    (típicamente al despertar Teams) quedaba clavado para siempre y la UI mentía.
                if (UltimaMedicion.HasValue && (DateTime.Now - UltimaMedicion.Value).TotalSeconds > 90 && PresenciaTeams.Length > 0)
                {
                    log.Debug($"Presencia: la última lectura venció ({det}); dejo de afirmar «{PresenciaTeams}»");
                    PresenciaTeams = ""; FuentePresencia = ""; PresenciaDesde = null; ausentePendiente = false;
                }
                return;
            }

            bool cambio = !string.Equals(p, PresenciaTeams, StringComparison.OrdinalIgnoreCase);
            if (cambio) log.Debug($"Presencia: Teams dice «{p}» · {fuente} · {det}");
            PresenciaTeams = p;
            FuentePresencia = fuente;
            PresenciaDesde = desde ?? (cambio ? DateTime.Now : PresenciaDesde);
            UltimaMedicion = DateTime.Now;
            MedicionDetalle = det;

            // 🚨 si el guión pide un estado que no es Disponible, ese estado lo puso él a propósito: no es una
            //    deriva y no se corrige. Manda el guión, que es la jerarquía que evita el parpadeo.
            if (GuionManda) { Rendido = false; ausentePendiente = false; intentosRescate = 0; return; }
            // 🚨 solo se rescata el Ausente AUTOMÁTICO. Ocupado, No molestar, Vuelvo enseguida o Desconectado son
            //    decisiones del user y no se tocan nunca: él pidió que el Ausente sea manual, no que se lo pisemos.
            if (!Estados.EsAusente(p) || Bloqueada)
            {
                if (RespetandoManual) log.Debug("Presencia: saliste de Ausente, vuelvo a sostener Disponible");
                Rendido = false; RespetandoManual = false; ausentePendiente = false; intentosRescate = 0; respetarHasta = DateTime.MinValue;
                return;
            }
            // 🚨🚨 «el ausente quiero que sea manual por mí»: si el user tocó el teclado o el mouse DE VERDAD
            //    (no nuestra F15) justo antes de que Teams pasara a Ausente, lo puso él. No se le pisa.
            //    Se respeta 10 min y después se vuelve a evaluar, por si en realidad fue Teams el que lo movió
            //    mientras él estaba sentado (pasa: el 17-sep la nube devolvió Away 3 s después de abrir Teams).
            if (RespetandoManual && DateTime.Now < respetarHasta) return;
            if (!ausentePendiente) { ausentePendiente = true; log.Debug($"Presencia: leí «{p}», espero confirmarlo antes de corregir"); return; }

            bool recienEstuvo = UltimoInputReal.HasValue && (DateTime.Now - UltimoInputReal.Value).TotalMinutes <= 3;
            if (recienEstuvo)
            {
                respetarHasta = DateTime.Now.AddMinutes(10);
                if (!RespetandoManual) log.Info($"Presencia: Teams pasó a «{p}» con vos usando la máquina · lo tomo como decisión tuya y no lo toco");
                RespetandoManual = true;
                ausentePendiente = false;
                return;
            }
            RespetandoManual = false;
            if (DateTime.Now < esperarHasta) return;    // en pausa: ya probamos todo y Teams vuelve solo

            Derivas++;
            intentosRescate++;
            Rescatar(p);

            // volver a medir: la nube tarda ~3 s en devolver la presencia nueva (medido en el log)
            Thread.Sleep(LogTeams != null ? 3000 : 900);
            if (LogTeams != null) { try { LogTeams.Refrescar(); var e2 = LogTeams.Ultimo; if (e2 != null && e2.Estado.Length > 0) { PresenciaTeams = e2.Estado; PresenciaDesde = e2.Cuando; UltimaMedicion = DateTime.Now; } } catch { } }
            else if (Chat != null) { string d3; string p2 = Chat.PresenciaReal(out d3); if (p2.Length > 0) { PresenciaTeams = p2; MedicionDetalle = d3; UltimaMedicion = DateTime.Now; } }

            if (TeamsDiceDisponible)
            {
                Correcciones++; ausentePendiente = false; intentosRescate = 0; Rendido = false; esperarHasta = DateTime.MinValue;
                log.Ok($"Presencia: corregido, Teams volvió a Disponible ({UltimoRescate})");
            }
            else if (intentosRescate >= 4)
            {
                // 🚨 no martillar: la versión vieja mandó ~570 toques inútiles en 3,4 h sin corregir una sola vez.
                esperarHasta = DateTime.Now.AddMinutes(15);
                intentosRescate = 0;
                Rendido = true;
                log.Aviso($"Presencia: Teams se queda en «{PresenciaTeams}» pase lo que pase · dejo de insistir 15 min · {RescateDetalle}");
            }
        }

        /// <summary>
        /// La escalera: primero lo barato, después lo que de verdad funciona.
        ///
        /// 1. un toque de F15 — reinicia la inactividad del SO. **Sirve para EVITAR el Ausente, no para rescatarlo.**
        /// 2. fijar Disponible en el menú del avatar por UIA (`ExpandCollapse` + `Invoke` sobre el RadioButton).
        ///    Invisible: sin teclas, sin foreground, sin ventanas. Es lo único que lo trae de vuelta una vez que se fue.
        ///
        /// Medido el 17-sep: con Teams escondido en la bandeja, la F15 reinició la inactividad cada 60 s durante
        /// 3,4 horas y Teams siguió en Ausente igual; fijarlo en el menú lo devolvió a Disponible en 3 segundos.
        /// </summary>
        void Rescatar(string p)
        {
            bool puedeForzar = cfg.PresenciaForzarEnTeams && Chat != null;
            if (intentosRescate == 1 || !puedeForzar)
            {
                UltimoRescate = "toque";
                if (Derivas <= 3 || Derivas % 10 == 0 || intentosRescate == 1)
                    log.Aviso($"Presencia: Teams se fue a «{p}» · lo traigo de vuelta con un toque");
                Tocar();
                return;
            }
            UltimoRescate = "fijar Disponible";
            Forzados++;
            string det;
            bool ok = Chat.FijarPresencia("Disponible", out det);
            RescateDetalle = det;
            log.Escribir(ok ? Nivel.Ok : Nivel.Aviso,
                ok ? $"Presencia: la F15 no alcanzó, fijé Disponible desde el menú del avatar · {det}"
                   : $"Presencia: no pude fijar Disponible en Teams · {det}");
        }

        /// <summary>Intenta los metodos en cascada y devuelve el que dejo idle ~0.</summary>
        string Tocar()
        {
            // 🚨 marcar SIEMPRE cuándo tocamos: es lo que después permite distinguir nuestro pulso del input real del user
            UltimoToque = DateTime.Now;
            string pref = (cfg.PresenciaMetodo ?? "f15").ToLowerInvariant();
            // f15 = tecla fantasma: existe en el estándar HID, ningún teclado físico la tiene y ninguna app la mapea.
            // Reinicia el reloj de inactividad igual que el mouse, sin mover el cursor ni escribir nada. Es el default.
            if (pref == "f15") { Tecla(VK_F15); if (Reinicio()) return "tecla fantasma F15"; Mouse(0, 0); return "mouse 0 px (F15 no alcanzó)"; }
            if (pref == "mouse0") { Mouse(0, 0); return "mouse 0 px"; }
            if (pref == "mouse1") { Mouse(1, 0); Thread.Sleep(30); Mouse(-1, 0); return "mouse 1 px ida y vuelta"; }
            // auto: probar la tecla fantasma primero, después mouse
            Tecla(VK_F15); if (Reinicio()) return "tecla fantasma F15";
            Mouse(0, 0); if (Reinicio()) return "mouse 0 px";
            Mouse(1, 0); Thread.Sleep(30); Mouse(-1, 0);
            return "mouse 1 px ida y vuelta";
        }

        static bool Reinicio() { Thread.Sleep(120); return IdleAhora() <= 1; }

        static void Mouse(int dx, int dy)
        {
            var inp = new Win32.INPUT[1];
            inp[0].type = 0; // INPUT_MOUSE
            inp[0].U.mi = new Win32.MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE };
            Win32.SendInput(1, inp, Marshal.SizeOf(typeof(Win32.INPUT)));
        }

        static void Tecla(ushort vk)
        {
            var inp = new Win32.INPUT[2];
            inp[0].type = Win32.INPUT_KEYBOARD; inp[0].U.ki = new Win32.KEYBDINPUT { wVk = vk };
            inp[1].type = Win32.INPUT_KEYBOARD; inp[1].U.ki = new Win32.KEYBDINPUT { wVk = vk, dwFlags = Win32.KEYEVENTF_KEYUP };
            Win32.SendInput(2, inp, Marshal.SizeOf(typeof(Win32.INPUT)));
        }

        public static string Fmt(int seg) => seg >= 3600 ? $"{seg / 3600}:{(seg % 3600) / 60:00}:{seg % 60:00}" : $"{seg / 60}:{seg % 60:00}";
    }
}
