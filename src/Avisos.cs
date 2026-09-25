using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace TeamsTools
{
    /// <summary>Algo que merece que lo mires.</summary>
    internal sealed class Aviso
    {
        public string Modelo = "", Conv = "", Autor = "", Texto = "", Porque = "";
        public DateTime Cuando;
        public int Prioridad;
        public Color Tinte = Tema.TextoSuave;
        public MensajeHist Fuente;
    }

    /// <summary>Algo que conviene anotar: lo que prometiste, lo que te prometieron, lo que se decidió.</summary>
    internal sealed class Nota
    {
        public string Id = "", Modelo = "", Conv = "", Quien = "", Texto = "";
        public DateTime Cuando;
        public DateTime? Vence;
        public bool Hecho;
        public bool Fijada;
    }

    /// <summary>Lo que quedó marcado a mano: se guarda para que sobreviva a cerrar la app.</summary>
    internal sealed class Marcas
    {
        public List<string> Hechas = new List<string>();
        public List<string> Ocultas = new List<string>();
        public List<string> Fijadas = new List<string>();
        [ScriptIgnore] public string Ruta = "";

        public static Marcas Cargar(string ruta)
        {
            try { if (File.Exists(ruta)) { var m = new JavaScriptSerializer().Deserialize<Marcas>(File.ReadAllText(ruta, Encoding.UTF8)); if (m != null) { m.Ruta = ruta; return m; } } }
            catch { }
            return new Marcas { Ruta = ruta };
        }
        public void Guardar()
        {
            if (string.IsNullOrEmpty(Ruta)) return;
            try { Disco.Escribir(Ruta, new JavaScriptSerializer().Serialize(this)); }
            catch { }
        }
    }

    /// <summary>Un micromodelo que lee CONVERSACIONES enteras, no mensajes sueltos.</summary>
    internal sealed class Lector
    {
        public string Nombre = "", Que = "";
        public Color Tinte = Tema.Cielo;
        public int Encontrados;
        public long Ms;
    }

    /// <summary>
    /// La familia de micromodelos que mira el historial y saca dos cosas: AVISOS (lo que merece que lo mires)
    /// y NOTAS (lo que conviene anotar). A diferencia de los de `Micromodelos`, que puntúan un mensaje suelto,
    /// estos necesitan el CONTEXTO de la conversación: quién habló último, si contestaste, qué prometiste.
    ///
    /// 🚨 Todo local y determinista. Nada de esto llama al LLM: son búsquedas y reglas sobre los 59 mil mensajes
    /// que ya están en memoria, así que corre en cientos de milisegundos y anda con el servidor apagado.
    /// El LLM queda aparte, para pedirle un resumen cuando vos lo pidas.
    /// </summary>
    internal sealed class Radar
    {
        public List<Aviso> Avisos = new List<Aviso>();
        public List<Nota> Notas = new List<Nota>();
        public List<Lector> Lectores = new List<Lector>();
        public long MsTotal;
        public int Revisados;
        public DateTime? Corrido;

        public int Dias = 14;
        public Marcas Marcas;

        // ---- léxicos de compromiso
        static readonly string[] MeComprometo = { "lo hago", "lo miro", "lo veo", "me fijo", "te paso", "te aviso", "te mando", "lo reviso", "lo pruebo", "me encargo", "lo dejo", "lo subo", "lo armo", "lo corrijo", "yo lo", "ahora lo", "despues lo", "después lo", "mañana lo", "manana lo" };
        static readonly string[] MePromete = { "te paso", "te aviso", "te mando", "lo miro", "lo reviso", "me fijo", "lo hago", "te confirmo", "lo subo", "te digo" };
        static readonly string[] Decisiones = { "quedamos en", "se decidio", "se decidió", "decidimos", "vamos con", "queda asi", "queda así", "acordamos", "confirmado que", "definimos" };
        static readonly Regex ReTicket = new Regex(@"\b[A-Z]{2,10}\d?-\d{1,6}\b", RegexOptions.Compiled);

        public Radar(Marcas m) { Marcas = m; }

        /// <summary>Corre todos los lectores sobre el historial. `miNombre` sirve para detectar menciones.</summary>
        public void Correr(Historia hist, string miNombre)
        {
            var relojTotal = System.Diagnostics.Stopwatch.StartNew();
            Avisos = new List<Aviso>();
            Notas = new List<Nota>();
            Lectores = new List<Lector>();
            if (hist == null || !hist.Hay) { MsTotal = 0; Corrido = DateTime.Now; return; }

            var corte = DateTime.Now.AddDays(-Dias);
            var recientes = hist.Mensajes.Where(m => !m.EsRuido && m.Texto.Length > 0 && !m.SinFecha && m.Fecha >= corte).ToList();
            Revisados = recientes.Count;
            var porConv = recientes.GroupBy(m => m.Conv).ToDictionary(g => g.Key, g => g.OrderBy(m => m.Fecha).ToList());

            Medir("te deben respuesta", "alguien te preguntó algo y nunca contestaste", Tema.Rosa, () => DeudaDeRespuesta(porConv));
            Medir("te esperan", "dijeron explícitamente que esperan algo tuyo", Tema.Durazno, () => TeEsperan(porConv));
            Medir("vencimientos", "fechas que todavía no pasaron", Tema.Crema, () => Vencimientos(recientes));
            Medir("producción", "todo lo que tocó PROD", Tema.Rosa, () => Produccion(recientes));
            Medir("te nombraron", "mensajes donde escribieron tu nombre", Tema.Cyan, () => Menciones(recientes, miNombre));
            Medir("tickets", "tickets mencionados y su última novedad", Tema.Malva, () => Tickets(recientes));
            Medir("tema caliente", "palabras que se dispararon contra su propio promedio", Tema.Cielo, () => TemasCalientes(hist, recientes));
            Medir("conversación fría", "hablabas seguido y se cortó", Tema.Apagado, () => Frias(hist, porConv));

            MedirNotas("me comprometí", "cosas que dijiste que ibas a hacer", Tema.Cyan, () => MisCompromisos(recientes));
            MedirNotas("me prometieron", "cosas que te dijeron que iban a hacer", Tema.Salvia, () => SusCompromisos(recientes));
            MedirNotas("se decidió", "acuerdos y definiciones", Tema.Malva, () => Acuerdos(recientes));

            // las marcadas como hechas u ocultas no vuelven a molestar
            Avisos = Avisos.Where(a => !Marcas.Ocultas.Contains(Clave(a))).OrderByDescending(a => a.Prioridad).ThenByDescending(a => a.Cuando).ToList();
            foreach (var n in Notas) { n.Hecho = Marcas.Hechas.Contains(n.Id); n.Fijada = Marcas.Fijadas.Contains(n.Id); }
            // las fijadas primero, después las pendientes por fecha, y las hechas al fondo
            Notas = Notas.OrderByDescending(n => n.Fijada && !n.Hecho).ThenBy(n => n.Hecho).ThenByDescending(n => n.Cuando).ToList();

            MsTotal = relojTotal.ElapsedMilliseconds;
            Corrido = DateTime.Now;
        }

        public static string Clave(Aviso a) => a.Modelo + "|" + (a.Fuente != null ? a.Fuente.Id : a.Texto.GetHashCode().ToString());

        void Medir(string nombre, string que, Color t, Func<List<Aviso>> f)
        {
            var r = System.Diagnostics.Stopwatch.StartNew();
            List<Aviso> res;
            try { res = f() ?? new List<Aviso>(); } catch { res = new List<Aviso>(); }
            foreach (var a in res) { a.Modelo = nombre; if (a.Tinte == Tema.TextoSuave) a.Tinte = t; }
            // 🚨 copy-on-write: la pantalla recorre estas listas en el hilo de la UI mientras el radar sigue corriendo
            //    de fondo; agregar sobre la MISMA lista daba «Collection was modified» a mitad de un refresco.
            Avisos = Avisos.Concat(res).ToList();
            Lectores = Lectores.Concat(new[] { new Lector { Nombre = nombre, Que = que, Tinte = t, Encontrados = res.Count, Ms = r.ElapsedMilliseconds } }).ToList();
        }

        void MedirNotas(string nombre, string que, Color t, Func<List<Nota>> f)
        {
            var r = System.Diagnostics.Stopwatch.StartNew();
            List<Nota> res;
            try { res = f() ?? new List<Nota>(); } catch { res = new List<Nota>(); }
            foreach (var n in res) n.Modelo = nombre;
            Notas = Notas.Concat(res).ToList();      // copy-on-write, ídem Avisos
            Lectores = Lectores.Concat(new[] { new Lector { Nombre = nombre, Que = que, Tinte = t, Encontrados = res.Count, Ms = r.ElapsedMilliseconds } }).ToList();
        }

        // ------------------------------------------------------------------ los lectores

        /// <summary>El último que habló fue el otro, preguntó o pidió algo, y vos no volviste a escribir.</summary>
        List<Aviso> DeudaDeRespuesta(Dictionary<string, List<MensajeHist>> porConv)
        {
            var res = new List<Aviso>();
            foreach (var kv in porConv)
            {
                var msgs = kv.Value;
                int ultimoMio = msgs.FindLastIndex(m => m.Mio);
                var pendientes = msgs.Skip(ultimoMio + 1).Where(m => !m.Mio).ToList();
                if (pendientes.Count == 0) continue;
                foreach (var m in pendientes)
                {
                    var s = Micromodelos.Analizar(m.Texto);
                    if (!s.Pregunta && !s.PideAccion) continue;
                    if (s.EsBot) continue;
                    int prio = Micromodelos.Prioridad(Micromodelos.Detectar(m.Texto));
                    prio += (int)Math.Min(20, (DateTime.Now - m.Fecha).TotalDays * 3);   // cuanto más viejo, peor
                    res.Add(new Aviso
                    {
                        Conv = kv.Key, Autor = m.Autor, Texto = m.Texto, Cuando = m.Fecha, Fuente = m,
                        Prioridad = Math.Min(100, prio),
                        Porque = (s.Pregunta ? "te preguntó" : "te pidió algo") + " y no volviste a escribir en ese chat",
                    });
                }
            }
            return res.OrderByDescending(a => a.Prioridad).Take(40).ToList();
        }

        List<Aviso> TeEsperan(Dictionary<string, List<MensajeHist>> porConv)
        {
            var res = new List<Aviso>();
            foreach (var kv in porConv)
            {
                int ultimoMio = kv.Value.FindLastIndex(m => m.Mio);
                foreach (var m in kv.Value.Skip(ultimoMio + 1).Where(m => !m.Mio))
                {
                    var s = Micromodelos.Analizar(m.Texto);
                    if (!s.Tiene("espera")) continue;
                    res.Add(new Aviso
                    {
                        Conv = kv.Key, Autor = m.Autor, Texto = m.Texto, Cuando = m.Fecha, Fuente = m,
                        Prioridad = 70 + (int)Math.Min(25, (DateTime.Now - m.Fecha).TotalDays * 4),
                        Porque = s.PorQue("espera"),
                    });
                }
            }
            return res.Take(25).ToList();
        }

        List<Aviso> Vencimientos(List<MensajeHist> msgs)
        {
            var res = new List<Aviso>();
            foreach (var m in msgs)
            {
                var s = Micromodelos.Analizar(m.Texto);
                if (!s.Vence.HasValue) continue;
                // solo lo que todavía no pasó, y mirando desde la fecha del mensaje
                var vence = s.Vence.Value;
                if (vence < m.Fecha) vence = vence.AddDays(1);
                if (vence < DateTime.Now || vence > DateTime.Now.AddDays(7)) continue;
                res.Add(new Aviso
                {
                    Conv = m.Conv, Autor = m.Mio ? "yo" : m.Autor, Texto = m.Texto, Cuando = m.Fecha, Fuente = m,
                    Prioridad = (int)Math.Max(40, 95 - (vence - DateTime.Now).TotalHours),
                    Porque = "vence " + vence.ToString("dd/MM HH:mm") + " · en " + Presencia.Fmt((int)(vence - DateTime.Now).TotalSeconds),
                });
            }
            return res.Take(25).ToList();
        }

        List<Aviso> Produccion(List<MensajeHist> msgs)
        {
            var res = new List<Aviso>();
            foreach (var m in msgs.Where(x => x.Fecha >= DateTime.Now.AddDays(-7)))
            {
                var s = Micromodelos.Analizar(m.Texto);
                if (!s.Tiene("produccion")) continue;
                res.Add(new Aviso
                {
                    Conv = m.Conv, Autor = m.Mio ? "yo" : m.Autor, Texto = m.Texto, Cuando = m.Fecha, Fuente = m,
                    Prioridad = 60 + (s.Urgente ? 25 : 0),
                    Porque = "menciona PROD" + (s.Urgente ? " y viene urgente" : ""),
                });
            }
            return res.Take(30).ToList();
        }

        List<Aviso> Menciones(List<MensajeHist> msgs, string miNombre)
        {
            var res = new List<Aviso>();
            var claves = Claves(miNombre);
            if (claves.Length == 0) return res;
            foreach (var m in msgs.Where(x => !x.Mio))
            {
                var n = Contactos.Normalizar(m.Texto);
                var cual = claves.FirstOrDefault(k => n.IndexOf(k, StringComparison.Ordinal) >= 0);
                if (cual == null) continue;
                res.Add(new Aviso
                {
                    Conv = m.Conv, Autor = m.Autor, Texto = m.Texto, Cuando = m.Fecha, Fuente = m,
                    Prioridad = 55 + Micromodelos.Prioridad(Micromodelos.Detectar(m.Texto)) / 3,
                    Porque = "te nombró («" + cual + "»)",
                });
            }
            return res.OrderByDescending(a => a.Cuando).Take(30).ToList();
        }

        /// <summary>Nombre, apellido y apodo, normalizados. «Ferreyra, Ramiro» → ramiro, ferreyra, rami (el apodo: el nombre recortado a 4 letras).</summary>
        static string[] Claves(string miNombre)
        {
            var res = new List<string>();
            foreach (var parte in (miNombre ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var n = Contactos.Normalizar(parte);
                if (n.Length >= 4) res.Add(n);
            }
            // el apodo típico es el nombre de pila recortado («Agustín» → «Agus», «Valentina» → «Vale»): se agrega si el nombre da para eso
            var pila = res.FirstOrDefault(x => x.Length >= 6);
            if (pila != null) res.Add(pila.Substring(0, 4));
            return res.Distinct().ToArray();
        }

        List<Aviso> Tickets(List<MensajeHist> msgs)
        {
            var res = new List<Aviso>();
            var vistos = new Dictionary<string, MensajeHist>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in msgs)
                foreach (Match t in ReTicket.Matches(m.Texto))
                {
                    MensajeHist prev;
                    if (!vistos.TryGetValue(t.Value, out prev) || m.Fecha > prev.Fecha) vistos[t.Value] = m;
                }
            foreach (var kv in vistos.OrderByDescending(k => k.Value.Fecha).Take(25))
                res.Add(new Aviso
                {
                    Conv = kv.Value.Conv, Autor = kv.Value.Mio ? "yo" : kv.Value.Autor, Texto = kv.Key + " · " + kv.Value.Texto,
                    Cuando = kv.Value.Fecha, Fuente = kv.Value,
                    Prioridad = 35 + (kv.Value.Mio ? 0 : 10),
                    Porque = "última novedad de " + kv.Key,
                });
            return res;
        }

        /// <summary>Palabras que aparecen mucho más de lo que suelen: es la forma barata de detectar un tema nuevo.</summary>
        List<Aviso> TemasCalientes(Historia hist, List<MensajeHist> recientes)
        {
            var res = new List<Aviso>();
            var ventana = DateTime.Now.AddDays(-3);
            var calientes = recientes.Where(m => m.Fecha >= ventana).ToList();
            if (calientes.Count < 20) return res;

            var baseCuenta = Contar(hist.Mensajes.Where(m => !m.EsRuido && !m.SinFecha && m.Fecha < ventana && m.Fecha >= DateTime.Now.AddDays(-90)));
            var ahora = Contar(calientes);
            double totalBase = Math.Max(1, baseCuenta.Values.Sum()), totalAhora = Math.Max(1, ahora.Values.Sum());

            foreach (var kv in ahora.Where(k => k.Value >= 4).OrderByDescending(k => k.Value))
            {
                double fAhora = kv.Value / totalAhora;
                double fBase = (baseCuenta.ContainsKey(kv.Key) ? baseCuenta[kv.Key] : 0.5) / totalBase;
                double salto = fAhora / Math.Max(1e-9, fBase);
                if (salto < 4) continue;
                var ej = calientes.LastOrDefault(m => Contactos.Normalizar(m.Texto).IndexOf(kv.Key, StringComparison.Ordinal) >= 0);
                res.Add(new Aviso
                {
                    Conv = ej != null ? ej.Conv : "", Autor = ej != null ? (ej.Mio ? "yo" : ej.Autor) : "", Texto = "«" + kv.Key + "» · " + (ej != null ? ej.Texto : ""),
                    Cuando = ej != null ? ej.Fecha : DateTime.Now, Fuente = ej,
                    Prioridad = (int)Math.Min(60, 25 + salto),
                    Porque = $"{kv.Value} menciones en 3 días · {salto:0}× lo normal",
                });
                if (res.Count >= 10) break;
            }
            return res;
        }

        static readonly HashSet<string> Vacias = new HashSet<string>(new[]
        {
            "de","la","que","el","en","y","a","los","no","un","por","con","una","para","es","se","lo","las","del","al","mas","más","pero","como","ya","si","me","te","le","su","esta","este","eso","esa","hay","ser","son","fue","va","voy","vas","dale","bien","tambien","también","ahora","todo","toda","todos","cuando","donde","porque","solo","sólo","hace","hacer","tiene","tengo","sobre","desde","hasta","entre","nos","yo","vos","ellos","ese","esos","estan","están","era","habia","había","muy","asi","así","igual","algo","nada","ver","dice","dijo","puede","pueden","mismo","otra","otro","aca","acá","alla","allá","gracias","hola","jaja","jajaja","ok","oka","dsp","q","xq","pq","x","d","re","tipo","osea","bueno","claro","pero","igual"
        }, StringComparer.Ordinal);

        static Dictionary<string, int> Contar(IEnumerable<MensajeHist> msgs)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            var re = new Regex(@"[\p{L}][\p{L}\p{N}\-]{4,}", RegexOptions.Compiled);
            foreach (var m in msgs)
                foreach (Match t in re.Matches(Contactos.Normalizar(m.Texto)))
                {
                    if (Vacias.Contains(t.Value)) continue;
                    int v; d.TryGetValue(t.Value, out v); d[t.Value] = v + 1;
                }
            return d;
        }

        /// <summary>Chats que venías teniendo seguido y se apagaron: no es urgente, pero se pierde de vista.</summary>
        List<Aviso> Frias(Historia hist, Dictionary<string, List<MensajeHist>> porConv)
        {
            var res = new List<Aviso>();
            foreach (var g in hist.Mensajes.Where(m => !m.EsRuido && !m.SinFecha && m.Texto.Length > 0).GroupBy(m => m.Conv))
            {
                var ult = g.Max(m => m.Fecha);
                int hace = (int)(DateTime.Now - ult).TotalDays;
                if (hace < 10 || hace > 120) continue;
                int antes = g.Count(m => m.Fecha >= ult.AddDays(-30) && m.Fecha <= ult);
                if (antes < 25) continue;       // solo las que se movían de verdad
                var ejemplo = g.OrderByDescending(m => m.Fecha).First();
                res.Add(new Aviso
                {
                    Conv = g.Key, Autor = ejemplo.Mio ? "yo" : ejemplo.Autor, Texto = ejemplo.Texto, Cuando = ult, Fuente = ejemplo,
                    Prioridad = 20,
                    Porque = $"{antes} mensajes en su último mes y nada desde hace {hace} días",
                });
            }
            return res.OrderByDescending(a => a.Cuando).Take(10).ToList();
        }

        // ------------------------------------------------------------------ notas

        List<Nota> MisCompromisos(List<MensajeHist> msgs)
        {
            var res = new List<Nota>();
            foreach (var m in msgs.Where(x => x.Mio))
            {
                var n = Contactos.Normalizar(m.Texto);
                var cual = MeComprometo.FirstOrDefault(k => n.IndexOf(k, StringComparison.Ordinal) >= 0);
                if (cual == null) continue;
                if (m.Texto.Length > 400) continue;
                var s = Micromodelos.Analizar(m.Texto);
                res.Add(new Nota { Id = "mio|" + m.Id, Conv = m.Conv, Quien = "yo", Texto = m.Texto, Cuando = m.Fecha, Vence = s.Vence });
            }
            return res.Take(60).ToList();
        }

        List<Nota> SusCompromisos(List<MensajeHist> msgs)
        {
            var res = new List<Nota>();
            foreach (var m in msgs.Where(x => !x.Mio))
            {
                var n = Contactos.Normalizar(m.Texto);
                if (!MePromete.Any(k => n.IndexOf(k, StringComparison.Ordinal) >= 0)) continue;
                if (m.Texto.Length > 400) continue;
                var s = Micromodelos.Analizar(m.Texto);
                if (s.EsBot) continue;
                res.Add(new Nota { Id = "suyo|" + m.Id, Conv = m.Conv, Quien = m.Autor, Texto = m.Texto, Cuando = m.Fecha, Vence = s.Vence });
            }
            return res.Take(60).ToList();
        }

        List<Nota> Acuerdos(List<MensajeHist> msgs)
        {
            var res = new List<Nota>();
            foreach (var m in msgs)
            {
                var n = Contactos.Normalizar(m.Texto);
                if (!Decisiones.Any(k => n.IndexOf(k, StringComparison.Ordinal) >= 0)) continue;
                if (m.Texto.Length > 500) continue;
                res.Add(new Nota { Id = "dec|" + m.Id, Conv = m.Conv, Quien = m.Mio ? "yo" : m.Autor, Texto = m.Texto, Cuando = m.Fecha });
            }
            return res.Take(40).ToList();
        }
    }
}
