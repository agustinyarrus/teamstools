using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace TeamsTools
{
    /// <summary>Una transición de presencia: "Vale pasó de Ausente a Disponible".</summary>
    internal sealed class Movimiento
    {
        public DateTime Hora = DateTime.Now;
        public string Persona = "", Desde = "", Hasta = "";
        public bool SeConecto => Estados.EsConectado(Hasta) && !Estados.EsConectado(Desde);
        public bool SeDesconecto => !Estados.EsConectado(Hasta) && Estados.EsConectado(Desde);
    }

    internal static class Estados
    {
        public static bool EsDisponible(string p) { var n = Contactos.Normalizar(p); return n.StartsWith("disponible") || n.StartsWith("available"); }
        public static bool EsAusente(string p) { var n = Contactos.Normalizar(p); return n.StartsWith("ausente") || n.StartsWith("away") || n.Contains("vuelvo") || n.Contains("right back"); }
        public static bool EsOcupado(string p) { var n = Contactos.Normalizar(p); return n.StartsWith("ocupado") || n.StartsWith("busy") || n.Contains("no molestar") || n.Contains("do not disturb") || n.Contains("llamada") || n.Contains("presentando"); }
        public static bool EsDesconectado(string p) { var n = Contactos.Normalizar(p); return n.StartsWith("sin conexion") || n.StartsWith("offline") || n.Length == 0; }
        /// <summary>
        /// Está DENTRO de una llamada o reunión. Teams lo dice de varias formas según qué esté haciendo:
        /// «En una llamada», «En una reunión» y «Presentando» son todas la misma situación para nosotros.
        /// 🚨 Cae también bajo EsOcupado (una llamada ocupa), así que este chequeo va ANTES si importa.
        /// </summary>
        public static bool EsEnLlamada(string p)
        {
            var n = Contactos.Normalizar(p);
            return n.Contains("llamada") || n.Contains("reunion") || n.Contains("presentando")
                || n.Contains("in a call") || n.Contains("in a meeting") || n.Contains("presenting");
        }

        /// <summary>Conectado = cualquier estado que no sea "sin conexión".</summary>
        public static bool EsConectado(string p) => p.Length > 0 && !EsDesconectado(p);
    }

    /// <summary>Lo que sabemos de una persona a partir de su presencia en la lista de chats.</summary>
    internal sealed class EstadoPersona
    {
        public string Nombre = "", Presencia = "";
        public DateTime Desde = DateTime.Now;      // desde cuándo está en ese estado
        public DateTime VistaPrimeraVez = DateTime.Now, VistaUltimaVez = DateTime.Now;
        /// <summary>El día al que corresponden los acumuladores. Al cambiar, se reinician.</summary>
        public DateTime Dia = DateTime.Today;
        public int Cambios;                        // transiciones observadas
        public DateTime? PrimeroDisponible;        // la primera vez que lo vimos Disponible hoy
        public DateTime? UltimoDisponible;
        public TimeSpan Disponible, Ausente, Ocupado, Desconectado;   // acumulados de esta sesión
        public bool NoLeido;
        public string Tipo = "privado";

        public string Resumen
        {
            get
            {
                var t = DateTime.Now - Desde;
                return Presencia.Length == 0 ? "—" : $"{Presencia} · hace {(t.TotalHours >= 1 ? (int)t.TotalHours + " h" : Math.Max(1, (int)t.TotalMinutes) + " min")}";
            }
        }
    }

    /// <summary>Lleva la cuenta de quién se conecta, se desconecta y cómo va cambiando de estado el equipo.</summary>
    internal sealed class RegistroPresencia
    {
        readonly object candado = new object();
        readonly Dictionary<string, EstadoPersona> gente = new Dictionary<string, EstadoPersona>();
        readonly List<Movimiento> movimientos = new List<Movimiento>();
        public int Capacidad = 500;
        public string RutaHistorial;
        public event Action<Movimiento> Hubo;

        public EstadoPersona[] Gente() { lock (candado) return gente.Values.OrderBy(g => g.Nombre).ToArray(); }
        public Movimiento[] Movimientos() { lock (candado) return movimientos.ToArray(); }
        public EstadoPersona De(string nombre) { lock (candado) { EstadoPersona p; return gente.TryGetValue(Contactos.Normalizar(nombre), out p) ? p : null; } }

        /// <summary>
        /// Al arrancar, retoma los cambios de HOY desde presencia.jsonl: la tabla del equipo no arranca vacía tras un reinicio
        /// y «cambios hoy» cuenta de verdad desde la medianoche. Las líneas de otros días se saltean con una comparación de
        /// texto antes de parsear (O(líneas), parseo solo de las de hoy). Devuelve cuántos cambios entraron.
        /// </summary>
        public int Cargar()
        {
            if (string.IsNullOrEmpty(RutaHistorial) || !File.Exists(RutaHistorial)) return 0;
            string hoy = DateTime.Today.ToString("yyyy-MM-dd");
            var js = new System.Web.Script.Serialization.JavaScriptSerializer();
            int n = 0;
            lock (candado)
            {
                foreach (var l in File.ReadLines(RutaHistorial, Encoding.UTF8))
                {
                    if (l.IndexOf(hoy, StringComparison.Ordinal) < 0) continue;
                    Dictionary<string, object> d;
                    try { d = js.Deserialize<Dictionary<string, object>>(l); } catch { continue; }
                    var hora = Json.F(d, "hora");
                    string persona = Json.S(d, "persona"), desde = Json.S(d, "desde"), hasta = Json.S(d, "hasta");
                    if (hora == null || hora.Value.Date != DateTime.Today || persona.Length == 0) continue;
                    string k = Contactos.Normalizar(persona);
                    EstadoPersona p;
                    if (!gente.TryGetValue(k, out p))
                    {
                        p = new EstadoPersona { Nombre = persona, Presencia = desde, Desde = hora.Value, VistaPrimeraVez = hora.Value, VistaUltimaVez = hora.Value, Dia = DateTime.Today };
                        gente[k] = p;
                    }
                    // el tiempo entre dos cambios seguidos es lo que estuvo en el estado anterior
                    var trans = hora.Value - p.VistaUltimaVez;
                    if (trans > TimeSpan.Zero)
                    {
                        if (Estados.EsDisponible(p.Presencia)) p.Disponible += trans;
                        else if (Estados.EsAusente(p.Presencia)) p.Ausente += trans;
                        else if (Estados.EsOcupado(p.Presencia)) p.Ocupado += trans;
                        else if (Estados.EsDesconectado(p.Presencia)) p.Desconectado += trans;
                    }
                    p.VistaUltimaVez = hora.Value;
                    p.Presencia = hasta; p.Desde = hora.Value; p.Cambios++;
                    if (Estados.EsDisponible(hasta)) { if (p.PrimeroDisponible == null) p.PrimeroDisponible = hora.Value; p.UltimoDisponible = hora.Value; }
                    movimientos.Add(new Movimiento { Hora = hora.Value, Persona = persona, Desde = desde, Hasta = hasta });
                    while (movimientos.Count > Capacidad) movimientos.RemoveAt(0);
                    n++;
                }
            }
            return n;
        }

        /// <summary>Compara la lista de chats con lo que teníamos y anota cada cambio de estado.</summary>
        public List<Movimiento> Observar(List<ChatItem> chats)
        {
            var nuevos = new List<Movimiento>();
            var ahora = DateTime.Now;
            lock (candado)
            {
                foreach (var c in chats)
                {
                    if (c.Tipo != "privado" && c.Tipo != "yo") continue;
                    if (c.Nombre.Length == 0) continue;
                    string k = Contactos.Normalizar(c.Nombre);
                    EstadoPersona p;
                    if (!gente.TryGetValue(k, out p))
                    {
                        p = new EstadoPersona { Nombre = c.Nombre, Presencia = c.Presencia, Desde = ahora, Tipo = c.Tipo };
                        if (Estados.EsDisponible(c.Presencia)) { p.PrimeroDisponible = ahora; p.UltimoDisponible = ahora; }
                        gente[k] = p;
                        continue;
                    }
                    // 🚨 Los acumuladores son POR DÍA. Sin esto, dejar la app toda la noche llenaba
                    //    «ausente» con 12 h de gente durmiendo y el «% disponible» daba 0 % para todos
                    //    a la mañana siguiente: el día de trabajo no se veía debajo de la noche.
                    //    Las etiquetas ya decían «cambios hoy» y «1ª vez», así que ahora coinciden.
                    if (p.Dia != ahora.Date)
                    {
                        p.Dia = ahora.Date;
                        p.Disponible = p.Ausente = p.Ocupado = p.Desconectado = TimeSpan.Zero;
                        p.Cambios = 0;
                        p.VistaPrimeraVez = ahora;
                        p.PrimeroDisponible = null; p.UltimoDisponible = null;
                        p.Desde = ahora;
                    }

                    // acumular el tiempo que estuvo en el estado anterior
                    var trans = ahora - p.VistaUltimaVez;
                    if (trans > TimeSpan.Zero && trans < TimeSpan.FromMinutes(10))
                    {
                        if (Estados.EsDisponible(p.Presencia)) p.Disponible += trans;
                        else if (Estados.EsAusente(p.Presencia)) p.Ausente += trans;
                        else if (Estados.EsOcupado(p.Presencia)) p.Ocupado += trans;
                        else if (Estados.EsDesconectado(p.Presencia)) p.Desconectado += trans;
                    }
                    p.VistaUltimaVez = ahora;
                    p.NoLeido = c.NoLeido;
                    p.Tipo = c.Tipo;
                    if (!string.Equals(p.Presencia, c.Presencia, StringComparison.OrdinalIgnoreCase))
                    {
                        var m = new Movimiento { Hora = ahora, Persona = c.Nombre, Desde = p.Presencia, Hasta = c.Presencia };
                        p.Presencia = c.Presencia; p.Desde = ahora; p.Cambios++;
                        if (Estados.EsDisponible(c.Presencia)) { if (p.PrimeroDisponible == null) p.PrimeroDisponible = ahora; p.UltimoDisponible = ahora; }
                        movimientos.Add(m);
                        nuevos.Add(m);
                        while (movimientos.Count > Capacidad) movimientos.RemoveAt(0);
                    }
                }
            }
            foreach (var m in nuevos)
            {
                Guardar(m);
                try { Hubo?.Invoke(m); } catch { }
            }
            return nuevos;
        }

        void Guardar(Movimiento m)
        {
            if (string.IsNullOrEmpty(RutaHistorial)) return;
            try
            {
                var d = new Dictionary<string, object> { ["hora"] = m.Hora, ["persona"] = m.Persona, ["desde"] = m.Desde, ["hasta"] = m.Hasta };
                Disco.Agregar(RutaHistorial, Json.Texto(d).Replace("\n", " ") + Environment.NewLine);
            }
            catch { }
        }
    }

    /// <summary>
    /// Mira la lista de chats de Teams cada N segundos (solo lectura, sin mostrar nada) para alimentar el registro
    /// de presencia del equipo. Es independiente del modo automático: sirve aunque no estés contestando nada.
    /// </summary>
    internal sealed class Observador
    {
        readonly TeamsChat chat;
        readonly Logger log;
        Thread hilo;
        volatile bool parar;
        readonly AutoResetEvent despertar = new AutoResetEvent(false);
        public readonly RegistroPresencia Registro = new RegistroPresencia();
        public List<ChatItem> Ultimos { get; private set; } = new List<ChatItem>();
        public DateTime? UltimaLectura { get; private set; }
        public int Lecturas, Fallos;
        public long UltimaMs;
        public int SegundosEntre = 30;
        public bool Activo = true;
        public string Estado { get; private set; } = "arrancando";
        public event Action Cambio;

        public Observador(TeamsChat c, Logger l, string rutaHistorial) { chat = c; log = l; Registro.RutaHistorial = rutaHistorial; }

        public void Iniciar()
        {
            if (hilo != null) return;
            hilo = new Thread(Bucle) { IsBackground = true, Name = "observador-presencia" };
            hilo.SetApartmentState(ApartmentState.MTA);
            hilo.Start();
        }
        public void Detener() { parar = true; despertar.Set(); }
        public void LeerYa() => despertar.Set();

        /// <summary>Lectura a pedido: si Teams está desmontado lo despierta (puede destellar una vez) y lee.</summary>
        public List<ChatItem> LeerAhora()
        {
            List<ChatItem> l;
            // 🚨 leer la lista destapa y re-oculta la ventana: bajo la aduana para no hacerlo mientras el
            //    autocontestador está escribiendo en ella (le ocultábamos la ventana en medio del envío).
            using (chat.Gate.Entrar("observador (a pedido)"))
            {
                l = chat.LeerChatsInvisible();
                if (l.Count == 0 && chat.TeamsCorriendo)
                {
                    log.Info("Presencia del equipo: Teams está desmontado, lo despierto para poder leer");
                    if (chat.Despertar()) l = chat.LeerChatsInvisible();
                }
            }
            if (l.Count > 0)
            {
                Ultimos = l; UltimaLectura = DateTime.Now; Lecturas++;
                Estado = "leyendo cada " + SegundosEntre + " s";
                Registro.Observar(l);
                try { Cambio?.Invoke(); } catch { }
            }
            else Estado = chat.TeamsCorriendo ? "no pude montar Teams" : "Teams cerrado";
            return l;
        }

        void Bucle()
        {
            Thread.Sleep(4000);   // dejar que arranque todo lo demás
            while (!parar)
            {
                try
                {
                    if (Activo)
                    {
                        var sw = Stopwatch.StartNew();
                        List<ChatItem> l;
                        using (chat.Gate.Entrar("observador"))
                            l = chat.LeerChatsInvisible();
                        UltimaMs = sw.ElapsedMilliseconds;
                        if (l.Count == 0) Estado = chat.TeamsCorriendo ? "Teams desmontado · tocá «leer presencia ahora»" : "Teams cerrado";
                        else Estado = "leyendo cada " + SegundosEntre + " s";
                        if (l.Count > 0)
                        {
                            Ultimos = l;
                            UltimaLectura = DateTime.Now;
                            Lecturas++;
                            var movs = Registro.Observar(l);
                            foreach (var m in movs.Take(6))
                            {
                                string quien = m.Persona.Split(',')[0];
                                if (m.SeConecto) log.Info($"● {quien} se conectó ({m.Hasta})");
                                else if (m.SeDesconecto) log.Info($"○ {quien} se desconectó");
                                else log.Debug($"{quien}: {(m.Desde.Length > 0 ? m.Desde : "—")} → {m.Hasta}");
                            }
                            try { Cambio?.Invoke(); } catch { }
                        }
                    }
                }
                catch (Exception ex) { Fallos++; log.Debug("Observador: " + ex.Message); }
                despertar.WaitOne(Math.Max(10, SegundosEntre) * 1000);
            }
        }
    }
}
