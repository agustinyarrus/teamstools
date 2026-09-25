using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TeamsTools
{
    /// <summary>Un cambio de presencia tal como lo contó el propio Teams en su log.</summary>
    internal sealed class EventoPresencia
    {
        public DateTime Cuando;         // ya en hora local
        public string Token = "";       // available / away / busy / berightback / offline… (idioma-independiente)
        public string Estado = "";      // como lo muestra Teams en tu idioma ("Disponible", "Ausente"…)
        public string Fuente = "";      // "nube" (UserPresenceAction) o "insignia" (SetBadge)
        public override string ToString() => $"{Cuando:HH:mm:ss} {Estado} ({Fuente})";
    }

    /// <summary>
    /// Lee la presencia REAL desde el log nativo de la app de Teams. Es la vía más de fondo que hay:
    /// no abre ventanas, no monta el árbol de UIA, no inyecta nada — solo lee un archivo que Teams ya escribe.
    ///
    /// Dos renglones sirven, y se complementan:
    ///   · `UserDataCrossCloudModule: Received Action: UserPresenceAction: {…, availability: Away}`
    ///       → lo que la NUBE dice de vos (la verdad de cara a los demás).
    ///   · `TaskbarBadgeServiceLegacy:Work: SetBadge Setting badge: GlyphBadge{"away"}, …, estado Ausente`
    ///       → lo que la app PINTA en la barra de tareas, y de regalo el nombre traducido del estado.
    ///
    /// 🚨 Las marcas de tiempo del log están en UTC aunque el texto diga "-03:00": el offset es mentira.
    ///    Verificado 17-sep-2026: la línea 03:50:33-03:00 ocurrió a las 00:50:33 locales (UTC-3).
    ///    Por eso se parsea el instante y se trata como UTC, ignorando el offset escrito.
    /// </summary>
    internal sealed class LectorPresenciaLog
    {
        // 2026-09-17T03:45:49.918182-03:00 … availability: Available}
        static readonly Regex ReNube = new Regex(
            @"^(?<t>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?).*?UserPresenceAction:\s*\{[^}]*availability:\s*(?<a>[A-Za-z]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // …SetBadge Setting badge: GlyphBadge{"away"}, overlay: No hay elementos, estado Ausente
        static readonly Regex ReInsignia = new Regex(
            @"^(?<t>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?).*?SetBadge Setting badge:\s*\w*Badge\{(?<g>[^}]*)\}.*?,\s*(?:estado|status)\s+(?<e>[^,\r\n]+?)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        static readonly Regex ReGlifo = new Regex("\"(?<g>[a-zA-Z]+)\"", RegexOptions.Compiled);

        readonly Logger log;
        readonly string carpeta;
        string archivo = "";            // el log que estamos siguiendo
        long offset;                    // hasta dónde lo leímos
        readonly List<EventoPresencia> eventos = new List<EventoPresencia>();
        const int MaxEventos = 400;
        // 🚨 lo leen TRES hilos (presencia, la UI, la revisión de salud) y antes no tenía ningún candado: la UI
        //    recorría la lista mientras la presencia le agregaba eventos («Collection was modified», tapado por un
        //    catch vacío). Ahora: `lectura` serializa las relecturas (el I/O va AFUERA del otro candado) y
        //    `candado` protege la lista; los lectores reciben COPIAS y nunca esperan a un disco.
        readonly object lectura = new object();
        readonly object candado = new object();

        /// <param name="carpetaPropia">Solo para pruebas: apunta el lector a una carpeta de logs falsa.</param>
        public LectorPresenciaLog(Logger l, string carpetaPropia = null)
        {
            log = l;
            carpeta = carpetaPropia ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\Logs");
        }

        public bool Disponible => Directory.Exists(carpeta);
        public string Archivo => archivo.Length > 0 ? Path.GetFileName(archivo) : "";
        public int Leidos { get; private set; }             // eventos vistos desde que arrancamos
        public DateTime? UltimaLectura { get; private set; }
        public string Problema { get; private set; } = "";

        /// <summary>El último evento de presencia conocido, o null si todavía no leímos ninguno.</summary>
        public EventoPresencia Ultimo { get { lock (candado) return eventos.Count > 0 ? eventos[eventos.Count - 1] : null; } }

        /// <summary>Los cambios de estado, del más viejo al más nuevo (solo transiciones, sin repeticiones).</summary>
        public IList<EventoPresencia> Eventos { get { lock (candado) return eventos.ToArray(); } }

        /// <summary>Cuánto hace que estás en el estado actual, según el log.</summary>
        public TimeSpan? Desde => Ultimo != null ? (TimeSpan?)(DateTime.Now - Ultimo.Cuando) : null;

        /// <summary>Cambios de estado ocurridos hoy.</summary>
        public int CambiosHoy { get { lock (candado) return eventos.Count(e => e.Cuando.Date == DateTime.Today); } }

        /// <summary>
        /// Cuánto tiempo estuviste hoy en cada estado, en minutos (el último tramo llega hasta ahora).
        /// El día arranca con el estado en que te dejó el último cambio de ayer, si lo tenemos: si no,
        /// se perdería el tramo que va de medianoche hasta la primera transición de la mañana.
        /// </summary>
        public Dictionary<string, double> MinutosPorEstadoHoy()
        {
            var res = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var todos = (EventoPresencia[])Eventos;
            var hoy = todos.Where(e => e.Cuando.Date == DateTime.Today).ToList();
            var previo = todos.LastOrDefault(e => e.Cuando.Date < DateTime.Today);
            if (previo != null)
                hoy.Insert(0, new EventoPresencia { Cuando = DateTime.Today, Token = previo.Token, Estado = previo.Estado, Fuente = "arrastre de ayer" });
            for (int i = 0; i < hoy.Count; i++)
            {
                DateTime fin = i + 1 < hoy.Count ? hoy[i + 1].Cuando : DateTime.Now;
                double min = Math.Max(0, (fin - hoy[i].Cuando).TotalMinutes);
                string k = hoy[i].Estado.Length > 0 ? hoy[i].Estado : hoy[i].Token;
                res[k] = (res.ContainsKey(k) ? res[k] : 0) + min;
            }
            return res;
        }

        /// <summary>
        /// Relee lo nuevo del log. Barato: sigue el archivo desde donde quedó. Si Teams rotó a uno nuevo
        /// (rota cada ~2 MB) lo detecta y empieza de cero en el nuevo. Devuelve true si apareció algo.
        /// </summary>
        public bool Refrescar()
        {
            lock (lectura) return RefrescarYa();
        }

        bool RefrescarYa()
        {
            if (!Disponible) { Problema = "no encontré la carpeta de logs de Teams"; return false; }
            try
            {
                var nuevos = new List<FileInfo>();
                var todos = new DirectoryInfo(carpeta).GetFiles("MSTeams_*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
                if (todos.Count == 0) { Problema = "la carpeta de logs está vacía"; return false; }

                var actual = todos[0];
                bool primeraVez = archivo.Length == 0;
                bool roto = !string.Equals(archivo, actual.FullName, StringComparison.OrdinalIgnoreCase);
                if (roto)
                {
                    // 🚨 al arrancar, el log nuevo puede tener pocos minutos y ningún cambio de presencia:
                    //    en ese caso también miramos el anterior, que es donde está la última transición.
                    if (primeraVez) nuevos.AddRange(todos.Skip(1).Take(2).Reverse());
                    archivo = actual.FullName;
                    offset = 0;
                }
                nuevos.Add(actual);

                int antes; lock (candado) antes = eventos.Count;
                foreach (var f in nuevos)
                {
                    bool esActual = string.Equals(f.FullName, archivo, StringComparison.OrdinalIgnoreCase);
                    long desde = esActual ? offset : 0;
                    long fin = Tragar(f.FullName, desde);
                    if (esActual) offset = fin;
                }
                UltimaLectura = DateTime.Now;
                Problema = "";
                int cuantos; lock (candado) cuantos = eventos.Count - antes;
                Leidos += Math.Max(0, cuantos);
                return cuantos > 0;
            }
            catch (Exception ex) { Problema = ex.Message; return false; }
        }

        const int TechoPasada = 8 << 20;   // 8 MB por pasada: los logs rotan a los 2 MB, esto es solo un tope de memoria

        /// <summary>
        /// Lee un archivo desde un offset y devuelve dónde quedó. Comparte el handle porque Teams lo tiene abierto
        /// (sin FileShare.ReadWrite tira "used by another process").
        ///
        /// 🚨 Devuelve el offset del último salto de línea COMPLETO, no `fs.Length`: Teams puede estar escribiendo
        ///    una línea a medias justo cuando leemos, y darla por leída se comería el evento. Por eso tampoco se
        ///    usa StreamReader: al cerrarlo cerraba el FileStream y consultar `fs.Length` después tiraba
        ///    ObjectDisposedException, el catch devolvía el offset viejo y el archivo se releía ENTERO cada 20 s,
        ///    duplicando los eventos (6 en frío contra 10 con la app corriendo). Cazado el 17-sep-2026.
        /// </summary>
        long Tragar(string ruta, long desde)
        {
            try
            {
                using (var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                {
                    long largo = fs.Length;
                    if (desde > largo) desde = 0;                        // lo truncaron o rotaron: volver a empezar
                    if (largo <= desde) return desde;                    // no hay nada nuevo
                    int cuanto = (int)Math.Min(largo - desde, TechoPasada);
                    long arranca = largo - cuanto;
                    fs.Seek(arranca, SeekOrigin.Begin);
                    var buf = new byte[cuanto];
                    int leidos = 0, n;
                    while (leidos < cuanto && (n = fs.Read(buf, leidos, cuanto - leidos)) > 0) leidos += n;

                    int corte = -1;
                    for (int i = leidos - 1; i >= 0; i--) if (buf[i] == (byte)'\n') { corte = i; break; }
                    if (corte < 0) return desde;                         // todavía no hay ni una línea entera

                    foreach (var linea in Encoding.UTF8.GetString(buf, 0, corte + 1).Split('\n'))
                    {
                        if (linea.Length < 40) continue;
                        // filtro barato antes de gastar el regex: el 99,9 % de las líneas no nos interesa
                        if (linea.IndexOf("UserPresenceAction", StringComparison.Ordinal) >= 0) Nube(linea);
                        else if (linea.IndexOf("SetBadge Setting badge", StringComparison.Ordinal) >= 0) Insignia(linea);
                    }
                    return arranca + corte + 1;
                }
            }
            catch { return desde; }
        }

        void Nube(string linea)
        {
            var m = ReNube.Match(linea);
            if (!m.Success) return;
            Agregar(new EventoPresencia
            {
                Cuando = Instante(m.Groups["t"].Value),
                Token = m.Groups["a"].Value.ToLowerInvariant(),
                Estado = Traducir(m.Groups["a"].Value),
                Fuente = "nube"
            });
        }

        void Insignia(string linea)
        {
            var m = ReInsignia.Match(linea);
            if (!m.Success) return;
            var g = ReGlifo.Match(m.Groups["g"].Value);
            string token = g.Success ? g.Groups["g"].Value.ToLowerInvariant() : "";
            string estado = m.Groups["e"].Value.Trim();
            if (token.Length == 0 && estado.Length == 0) return;
            // el glifo trae `NumericBadge{0}` cuando la insignia es un contador de mensajes: ahí el estado igual sirve
            if (token.Length == 0) token = Destraducir(estado);
            Agregar(new EventoPresencia
            {
                Cuando = Instante(m.Groups["t"].Value),
                Token = token,
                Estado = estado.Length > 0 ? estado : Traducir(token),
                Fuente = "insignia"
            });
        }

        /// <summary>Solo guardamos transiciones: si el token no cambió respecto del último, es ruido.</summary>
        void Agregar(EventoPresencia e)
        {
            if (e.Token.Length == 0) return;
            lock (candado)
            {
                var ult = eventos.Count > 0 ? eventos[eventos.Count - 1] : null;
                if (ult != null && ult.Token == e.Token)
                {
                    // misma presencia: nos quedamos con la fuente más confiable (la nube manda) sin duplicar la fila
                    if (ult.Fuente != "nube" && e.Fuente == "nube") { ult.Fuente = "nube"; if (ult.Estado.Length == 0) ult.Estado = e.Estado; }
                    return;
                }
                // 🚨 cinturón y tiradores contra la relectura: un evento ANTERIOR al último que ya tenemos es historia
                //    que volvimos a leer, no algo que pasó. Antes se le pisaba la hora y entraba igual, creando
                //    transiciones fantasma de duración cero que inflaban el conteo sin mover los minutos.
                if (ult != null && e.Cuando < ult.Cuando) return;
                eventos.Add(e);
                if (eventos.Count > MaxEventos) eventos.RemoveRange(0, eventos.Count - MaxEventos);
            }
        }

        /// <summary>🚨 El log escribe UTC con un offset local falso pegado atrás. Se parsea el instante y se asume UTC.</summary>
        static DateTime Instante(string t)
        {
            DateTime d;
            if (DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                return DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime();
            return DateTime.Now;
        }

        static readonly Dictionary<string, string> Nombres = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "available", "Disponible" }, { "availableidle", "Disponible" },
            { "away", "Ausente" }, { "idle", "Ausente" },
            { "berightback", "Vuelvo enseguida" },
            { "busy", "Ocupado" }, { "busyidle", "Ocupado" }, { "inacall", "En una llamada" },
            { "inameeting", "En una reunión" }, { "presenting", "Presentando" },
            { "donotdisturb", "No molestar" }, { "dnd", "No molestar" },
            { "offline", "Desconectado" }, { "unknown", "" }, { "none", "" },
        };

        public static string Traducir(string token)
        {
            if (token == null) return "";
            string v;
            return Nombres.TryGetValue(token.Trim(), out v) ? v : token.Trim();
        }

        static string Destraducir(string estado)
        {
            var n = Contactos.Normalizar(estado);
            if (n.StartsWith("disponible") || n.StartsWith("available")) return "available";
            if (n.StartsWith("ausente") || n.StartsWith("away")) return "away";
            if (n.StartsWith("ocupado") || n.StartsWith("busy")) return "busy";
            if (n.StartsWith("no molestar") || n.Contains("disturb")) return "donotdisturb";
            if (n.Contains("vuelvo") || n.Contains("right back")) return "berightback";
            if (n.StartsWith("desconectado") || n.StartsWith("offline")) return "offline";
            return "";
        }
    }
}
