using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TeamsTools
{
    /// <summary>Alguien que estuvo en una llamada, tal como lo lista Teams.</summary>
    internal sealed class ParteLlamada
    {
        public string Id = "", Nombre = "";
        public int Segundos;
        /// <summary>Las identidades 28:… y 4:… son bots, salas y telefonos, no companeros.</summary>
        public bool EsPersona => Nombre.Length > 1 && !Nombre.StartsWith("28:", StringComparison.Ordinal)
                                                   && !Nombre.StartsWith("4:", StringComparison.Ordinal);
    }

    /// <summary>Un mensaje del historial local de Teams.</summary>
    internal sealed class MensajeHist
    {
        /// <summary>
        /// Si este mensaje es un evento de llamada, quienes estuvieron. 🚨 Es la UNICA fuente que dice con
        /// certeza quien hablo con quien: Teams la escribe en el `content` del Event/Call como un
        /// &lt;partlist&gt; y el extractor la tiraba por considerarla ruido.
        /// </summary>
        public List<ParteLlamada> Llamada;
        public string CallId = "", EstadoLlamada = "";
        public bool EsLlamada => Llamada != null && Llamada.Count > 0;
        public string Conv = "", ConvId = "", ConvTipo = "", Autor = "", Tipo = "", Texto = "", Id = "";
        public DateTime Fecha;              // ya convertida a hora local
        public bool SinFecha, Mio, Borrado;
        /// <summary>Altas y bajas de miembros, avisos de llamada: el 5 % del volumen que no es conversación.</summary>
        public bool EsRuido => Tipo.StartsWith("ThreadActivity/", StringComparison.Ordinal)
                            || Tipo.StartsWith("Event/Call", StringComparison.Ordinal)
                            || Tipo.StartsWith("RichText/Media_Call", StringComparison.Ordinal);
    }

    /// <summary>
    /// El historial ENTERO de Teams, leído del disco. No abre Teams, no toca la nube y no necesita ninguna ventana:
    /// los chats están en el IndexedDB local (LevelDB + snappy + serialización de V8) y ya existe un extractor
    /// propio en Python puro en `tools\teams-chats` que los saca a `mensajes.jsonl`.
    ///
    /// Acá NO se reimplementa nada de eso: se llama al extractor cuando hace falta y se lee el jsonl. Reescribir
    /// LevelDB en C# sería semanas de trabajo para empatar algo que ya funciona y está probado sobre decenas de miles de mensajes.
    ///
    /// 🚨 Las fechas del jsonl vienen en UTC y pueden faltar: se convierten a local y se marca `SinFecha`.
    /// </summary>
    internal sealed class Historia
    {
        readonly Logger log;
        public List<MensajeHist> Mensajes = new List<MensajeHist>();
        public string Problema = "", Paso = "";
        public DateTime? Extraido;
        public volatile bool Trabajando;
        public event Action Cambio;

        /// <summary>Dónde vive el extractor (config `carpetaExtractor`; vacío = tools\teams-chats al lado del exe).</summary>
        public static string CarpetaExtractorConfigurada = "";
        public static string CarpetaExtractor => Herramientas.Resolver(CarpetaExtractorConfigurada, "teams-chats");
        /// <summary>Dónde deja el extractor los mensajes (config `historialJsonl`; vacío = %TEMP%\teams-extraido\mensajes.jsonl, que es donde escribe).</summary>
        public static string JsonlConfigurado = "";
        public string Jsonl => JsonlConfigurado.Length > 0 ? Environment.ExpandEnvironmentVariables(JsonlConfigurado) : Path.Combine(Path.GetTempPath(), "teams-extraido", "mensajes.jsonl");

        public Historia(Logger l) { log = l; }

        public bool Hay => Mensajes.Count > 0;
        public int ConTexto { get; private set; }
        public int Mios { get; private set; }
        public int Ruido { get; private set; }
        public DateTime? Desde { get; private set; }
        public DateTime? Hasta { get; private set; }
        public string[] Conversaciones { get; private set; } = new string[0];
        public string[] Autores { get; private set; } = new string[0];
        /// <summary>Los eventos de llamada con su lista de participantes, ordenados por fecha.</summary>
        public MensajeHist[] Llamadas { get; private set; } = new MensajeHist[0];

        // --------------------------------------------------------------- cargar
        public void CargarAsync()
        {
            if (Trabajando) return;
            Trabajando = true; Paso = "leyendo el historial…"; Avisar();
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                using (Tareas.Empezar("leyendo el historial de Teams", "19 MB del disco", Tema.Cielo))
                {
                    try { Cargar(); }
                    catch (Exception ex) { Problema = ex.Message; log?.Error("Historia: " + ex.Message); }
                    finally { Trabajando = false; Paso = ""; Avisar(); }
                }
            });
        }

        /// <summary>Lee el jsonl de forma sincrónica. Público para poder usarlo desde la línea de comando.</summary>
        public void Cargar()
        {
            Problema = "";
            if (!File.Exists(Jsonl))
            {
                Problema = "todavía no hay extracción: tocá «releer Teams»";
                Mensajes = new List<MensajeHist>();
                return;
            }
            var reloj = Stopwatch.StartNew();
            var lista = new List<MensajeHist>(70000);
            using (var fs = new FileStream(Jsonl, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
            using (var sr = new StreamReader(fs, new UTF8Encoding(false)))
            {
                string linea;
                while ((linea = sr.ReadLine()) != null)
                {
                    if (linea.Length < 20) continue;
                    var m = Parsear(linea);
                    if (m != null) lista.Add(m);
                }
            }
            lista.Sort((a, b) => a.Fecha.CompareTo(b.Fecha));
            Mensajes = lista;
            Extraido = File.GetLastWriteTime(Jsonl);
            Recalcular();
            log?.Info($"Historia: {Mensajes.Count} mensajes leídos en {reloj.ElapsedMilliseconds} ms ({ConTexto} con texto, {Conversaciones.Length} conversaciones)");
        }

        void Recalcular()
        {
            ConTexto = Mensajes.Count(m => m.Texto.Length > 0 && !m.EsRuido);
            Mios = Mensajes.Count(m => m.Mio);
            Ruido = Mensajes.Count(m => m.EsRuido);
            var conFecha = Mensajes.Where(m => !m.SinFecha).ToList();
            Desde = conFecha.Count > 0 ? (DateTime?)conFecha[0].Fecha : null;
            Hasta = conFecha.Count > 0 ? (DateTime?)conFecha[conFecha.Count - 1].Fecha : null;

            // 🚨 los chats de a dos no tienen "topic": el nombre viene siendo el id crudo (19:…@unq.gbl.spaces).
            //    Se le pone el nombre del otro, que es con quien uno cree estar hablando.
            var lindos = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var g in Mensajes.GroupBy(m => m.Conv))
            {
                if (!g.Key.StartsWith("19:", StringComparison.Ordinal) && !g.Key.StartsWith("48:", StringComparison.Ordinal)) continue;
                var otro = g.Where(m => !m.Mio && m.Autor.Length > 0 && !m.Autor.StartsWith("19:", StringComparison.Ordinal))
                            .GroupBy(m => m.Autor).OrderByDescending(x => x.Count()).FirstOrDefault();
                if (otro != null) lindos[g.Key] = otro.Key;
            }
            foreach (var m in Mensajes) { string v; if (lindos.TryGetValue(m.Conv, out v)) m.Conv = v; }

            Conversaciones = Mensajes.Where(m => !m.EsRuido).Select(m => m.Conv).Distinct().OrderBy(x => x).ToArray();
            Autores = Mensajes.Where(m => !m.EsRuido && m.Autor.Length > 0).Select(m => m.Autor).Distinct().OrderBy(x => x).ToArray();
            Llamadas = Mensajes.Where(m => m.EsLlamada && !m.SinFecha).OrderBy(m => m.Fecha).ToArray();
        }

        // --------------------------------------------------------------- re-extraer
        /// <summary>
        /// Vuelve a sacar los chats del disco. ~1 minuto, sin cerrar Teams, sin mostrar nada.
        /// El script copia la base a %TEMP% y extrae; el `LOCK` falla al copiar y no importa.
        /// </summary>
        public void ReleerAsync()
        {
            if (Trabajando) return;
            Trabajando = true; Paso = "copiando la base de Teams…"; Problema = ""; Avisar();
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                using (var tarea = Tareas.Empezar("releyendo Teams del disco", "copiando la base…", Tema.Malva))
                try
                {
                    string script = Path.Combine(CarpetaExtractor, "actualizar_extraccion.py");
                    if (!File.Exists(script)) { Problema = "no encontré " + script; return; }
                    string python = BuscarPython();
                    if (python.Length == 0) { Problema = "no encontré python en el PATH"; return; }

                    // guardar la extracción anterior: el script pisa la salida y a veces conviene comparar
                    try { if (File.Exists(Jsonl)) File.Copy(Jsonl, Jsonl.Replace(".jsonl", "_prev.jsonl"), true); } catch { }

                    Paso = "extrayendo (no hace falta cerrar Teams)…"; Avisar();
                    tarea.Paso("extrayendo · no hace falta cerrar Teams");
                    var psi = new ProcessStartInfo(python, "\"" + script + "\"")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                    };
                    var reloj = Stopwatch.StartNew();
                    string salida;
                    using (var p = Process.Start(psi))
                    {
                        salida = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(600000);
                    }
                    log?.Info($"Historia: re-extracción en {reloj.ElapsedMilliseconds / 1000} s · " + Ultima(salida));
                    Paso = "leyendo el historial…"; Avisar();
                    tarea.Paso("leyendo los mensajes extraídos");
                    Cargar();
                }
                catch (Exception ex) { Problema = ex.Message; log?.Error("Historia: " + ex.Message); }
                finally { Trabajando = false; Paso = ""; Avisar(); }
            });
        }

        static string Ultima(string salida)
        {
            var l = (salida ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            return l.Length == 0 ? "" : string.Join(" · ", l.Skip(Math.Max(0, l.Length - 4)));
        }

        static string BuscarPython()
        {
            foreach (var c in new[] { "python.exe", "py.exe", "python3.exe" })
                foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                {
                    try { if (dir.Length > 0 && File.Exists(Path.Combine(dir, c))) return Path.Combine(dir, c); }
                    catch { }
                }
            return "";
        }

        void Avisar() { try { Cambio?.Invoke(); } catch { } }

        // --------------------------------------------------------------- buscar
        /// <summary>Filtra el corpus. Todo opcional; devuelve los más nuevos primero.</summary>
        public List<MensajeHist> Buscar(string texto, string conv, string autor, DateTime? desde, DateTime? hasta, bool incluirRuido, int tope = 4000)
        {
            var q = Mensajes.AsEnumerable();
            if (!incluirRuido) q = q.Where(m => !m.EsRuido && m.Texto.Length > 0);
            if (!string.IsNullOrWhiteSpace(conv)) q = q.Where(m => m.Conv.IndexOf(conv, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrWhiteSpace(autor)) q = q.Where(m => m.Autor.IndexOf(autor, StringComparison.OrdinalIgnoreCase) >= 0);
            if (desde.HasValue) q = q.Where(m => !m.SinFecha && m.Fecha >= desde.Value);
            if (hasta.HasValue) q = q.Where(m => !m.SinFecha && m.Fecha <= hasta.Value);
            if (!string.IsNullOrWhiteSpace(texto))
            {
                var terminos = texto.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                    .Select(Contactos.Normalizar).Where(t => t.Length > 0).ToArray();
                if (terminos.Length > 0)
                    q = q.Where(m => { var n = Contactos.Normalizar(m.Texto); return terminos.All(t => n.IndexOf(t, StringComparison.Ordinal) >= 0); });
            }
            return q.OrderByDescending(m => m.Fecha).Take(tope).ToList();
        }

        // --------------------------------------------------------------- parseo
        static MensajeHist Parsear(string l)
        {
            var m = new MensajeHist();
            m.Conv = Cadena(l, "conv");
            m.ConvId = Cadena(l, "conv_id");   // 🚨 el Conv se le pisa con el nombre del otro; el id es lo único estable
            m.ConvTipo = Cadena(l, "conv_tipo");
            m.Autor = Cadena(l, "autor");
            m.Tipo = Cadena(l, "tipo");
            m.Texto = Cadena(l, "texto");
            m.Id = Cadena(l, "id");
            m.Mio = Booleano(l, "mio");
            m.CallId = Cadena(l, "call_id");
            m.Llamada = Participantes(l, out m.EstadoLlamada);
            m.Borrado = Booleano(l, "borrado");
            string f = Cadena(l, "fecha");
            DateTime d;
            if (f.Length > 0 && DateTime.TryParse(f, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                m.Fecha = DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime();
            else { m.SinFecha = true; m.Fecha = DateTime.MinValue; }
            if (m.Conv.Length == 0 && m.Texto.Length == 0) return null;
            return m;
        }

        /// <summary>
        /// Saca la lista de participantes de un evento de llamada. El resto del jsonl es plano, esto no:
        /// `"llamada":{"estado":"ended","gente":[{"id":"...","nombre":"...","dur":92}, ...]}`.
        /// Igual alcanza con recorrer los objetos de `gente` de a uno; no hace falta un parser general.
        /// </summary>
        static List<ParteLlamada> Participantes(string l, out string estado)
        {
            estado = "";
            // 🚨 el jsonl lo escribe json.dump de Python, que separa con «": "» CON espacio. Buscar
            //    «"gente":[» pegado no matchea nunca y la lista entera se perdía en silencio.
            int i = l.IndexOf("\"llamada\"", StringComparison.Ordinal);
            if (i < 0) return null;
            int g = l.IndexOf("\"gente\"", i, StringComparison.Ordinal);
            if (g < 0) return null;
            g = l.IndexOf('[', g);
            if (g < 0) return null;
            estado = Cadena(l.Substring(i, Math.Min(80, l.Length - i)), "estado");

            var lista = new List<ParteLlamada>();
            int k = g;
            while (true)
            {
                int ini = l.IndexOf('{', k);
                if (ini < 0) break;
                int fin = l.IndexOf('}', ini);
                if (fin < 0) break;
                string trozo = l.Substring(ini, fin - ini + 1);
                var pa = new ParteLlamada
                {
                    Id = Cadena(trozo, "id"),
                    Nombre = Cadena(trozo, "nombre"),
                    Segundos = (int)Numero(trozo, "dur"),
                };
                if (pa.Id.Length > 0 || pa.Nombre.Length > 0) lista.Add(pa);
                k = fin + 1;
                // el cierre del arreglo corta la lista
                int cierre = l.IndexOf(']', fin);
                int sigue = l.IndexOf('{', fin);
                if (cierre >= 0 && (sigue < 0 || cierre < sigue)) break;
            }
            return lista.Count > 0 ? lista : null;
        }

        /// <summary>Un numero suelto del jsonl (sin comillas).</summary>
        static double Numero(string l, string clave)
        {
            int i = Donde(l, clave);
            if (i < 0) return 0;
            while (i < l.Length && (l[i] == ':' || l[i] == ' ')) i++;
            int j = i;
            while (j < l.Length && (char.IsDigit(l[j]) || l[j] == '-' || l[j] == '.')) j++;
            double v;
            return double.TryParse(l.Substring(i, Math.Max(0, j - i)), System.Globalization.NumberStyles.Any,
                CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        /// <summary>Lector de un campo string del jsonl. Sin traer un parser entero: son objetos planos.</summary>
        static string Cadena(string l, string clave)
        {
            int i = Donde(l, clave);
            if (i < 0) return "";
            while (i < l.Length && l[i] != '"' && l[i] != ',' && l[i] != '}') i++;
            if (i >= l.Length || l[i] != '"') return "";
            var sb = new StringBuilder();
            for (int k = i + 1; k < l.Length; k++)
            {
                char c = l[k];
                if (c == '\\' && k + 1 < l.Length)
                {
                    char n = l[++k];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 't') sb.Append(' ');
                    else if (n == 'r') { }
                    else if (n == 'u' && k + 4 < l.Length)
                    {
                        int cod;
                        if (int.TryParse(l.Substring(k + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cod)) sb.Append((char)cod);
                        k += 4;
                    }
                    else sb.Append(n);
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        static bool Booleano(string l, string clave)
        {
            int i = Donde(l, clave);
            return i >= 0 && l.IndexOf("true", i, Math.Min(8, l.Length - i), StringComparison.Ordinal) >= 0;
        }

        static int Donde(string l, string clave)
        {
            int i = l.IndexOf("\"" + clave + "\"", StringComparison.Ordinal);
            return i < 0 ? -1 : i + clave.Length + 2;
        }
    }
}
