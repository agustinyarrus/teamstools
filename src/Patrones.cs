using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TeamsTools
{
    /// <summary>Un cambio de MI presencia, guardado en disco para que los patrones abarquen varios días.</summary>
    internal sealed class MiCambio
    {
        public DateTime Cuando;
        public string Estado = "", Token = "";
        public override string ToString() => $"{Cuando:dd/MM HH:mm} {Estado}";
    }

    /// <summary>
    /// Diario de MI presencia en `datos\mi-presencia.jsonl`. El log nativo de Teams rota cada 2 MB y se lleva la
    /// historia puesta, así que si queremos hablar de patrones («todos los martes a las 14 te vas a Ausente»)
    /// hay que ir guardando aparte lo que vemos. Una línea por cambio, append, sin dependencias.
    /// </summary>
    internal sealed class DiarioPresencia
    {
        readonly string ruta;
        readonly List<MiCambio> cambios = new List<MiCambio>();
        // índice para el anti-duplicado: token + segundo. Antes era `cambios.Any(...)` DENTRO del bucle de nuevos
        // (O(n·m) en cada refresco); ahora son tres búsquedas O(1) por evento (el segundo y sus dos vecinos).
        readonly HashSet<string> vistos = new HashSet<string>(StringComparer.Ordinal);
        readonly object candado = new object();
        static string Clave(string token, long segundo) => token + "|" + segundo;
        static long Segundo(DateTime t) => t.Ticks / TimeSpan.TicksPerSecond;
        /// <summary>Copia: la lee la UI mientras el fondo puede estar absorbiendo.</summary>
        public IList<MiCambio> Cambios { get { lock (candado) return cambios.ToArray(); } }
        public string Problema { get; private set; } = "";

        public DiarioPresencia(string rutaArchivo) { ruta = rutaArchivo; Cargar(); }

        void Cargar()
        {
            try
            {
                if (!File.Exists(ruta)) return;
                foreach (var l in File.ReadAllLines(ruta, Encoding.UTF8))
                {
                    var c = Parsear(l);
                    if (c != null) cambios.Add(c);
                }
                cambios.Sort((a, b) => a.Cuando.CompareTo(b.Cuando));
                foreach (var c in cambios) vistos.Add(Clave(c.Token, Segundo(c.Cuando)));
            }
            catch (Exception ex) { Problema = ex.Message; }
        }

        static MiCambio Parsear(string linea)
        {
            if (string.IsNullOrWhiteSpace(linea)) return null;
            string t = Campo(linea, "t"), e = Campo(linea, "e"), k = Campo(linea, "k");
            DateTime d;
            if (t.Length == 0 || !DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return null;
            return new MiCambio { Cuando = d, Estado = e, Token = k };
        }

        static string Campo(string s, string clave)
        {
            string marca = "\"" + clave + "\":\"";
            int i = s.IndexOf(marca, StringComparison.Ordinal);
            if (i < 0) return "";
            i += marca.Length;
            int j = s.IndexOf('"', i);
            return j < 0 ? "" : s.Substring(i, j - i);
        }

        /// <summary>Suma los eventos del log nativo que todavía no teníamos. Devuelve cuántos entraron.</summary>
        public int Absorber(IEnumerable<EventoPresencia> nuevos)
        {
            if (nuevos == null) return 0;
            int entraron = 0;
            var nuevaLineas = new StringBuilder();
            lock (candado)
            {
                foreach (var e in nuevos.OrderBy(x => x.Cuando))
                {
                    if (e == null || e.Estado.Length == 0) continue;
                    // el log nativo puede repetir el mismo instante entre corridas: se compara al segundo (±1)
                    long s = Segundo(e.Cuando);
                    if (vistos.Contains(Clave(e.Token, s)) || vistos.Contains(Clave(e.Token, s - 1)) || vistos.Contains(Clave(e.Token, s + 1))) continue;
                    var c2 = new MiCambio { Cuando = e.Cuando, Estado = e.Estado, Token = e.Token };
                    cambios.Add(c2);
                    vistos.Add(Clave(c2.Token, s));
                    nuevaLineas.Append($"{{\"t\":\"{c2.Cuando:yyyy-MM-ddTHH:mm:ss}\",\"e\":\"{Escapar(c2.Estado)}\",\"k\":\"{Escapar(c2.Token)}\"}}").Append(Environment.NewLine);
                    entraron++;
                }
                if (entraron > 0) cambios.Sort((a, b) => a.Cuando.CompareTo(b.Cuando));
            }
            if (entraron > 0) Disco.Agregar(ruta, nuevaLineas.ToString());   // de fondo y en orden
            return entraron;
        }

        static string Escapar(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        public int DiasConDatos { get { lock (candado) return cambios.Select(c => c.Cuando.Date).Distinct().Count(); } }
        public DateTime? Desde { get { lock (candado) return cambios.Count > 0 ? (DateTime?)cambios[0].Cuando : null; } }
    }

    /// <summary>Algo que el análisis encontró y que vale la pena contar.</summary>
    internal sealed class Hallazgo
    {
        public string Titulo = "", Detalle = "", Quien = "";
        public DateTime Cuando = DateTime.Now;
        public int Peso;              // 2 = mirar esto, 1 = dato, 0 = curiosidad
        public Color Tinte = Tema.TextoSuave;
        public static Hallazgo H(int peso, Color tinte, string quien, string titulo, string detalle, DateTime? cuando = null)
            => new Hallazgo { Peso = peso, Tinte = tinte, Quien = quien, Titulo = titulo, Detalle = detalle, Cuando = cuando ?? DateTime.Now };
    }

    /// <summary>Un tramo continuo en un mismo estado.</summary>
    internal sealed class Tramo
    {
        public DateTime Desde, Hasta;
        public string Estado = "", Token = "";
        public TimeSpan Dura => Hasta - Desde;
    }

    /// <summary>
    /// Busca patrones raros en la presencia: parpadeos, jornadas larguísimas, actividad de madrugada, gente que
    /// se desconecta siempre a la misma hora, días fuera de lo normal. Todo sale de datos REALES ya guardados
    /// (el diario propio y `presencia.jsonl` del observador); no inventa nada ni estima lo que no vio.
    /// </summary>
    internal sealed class Patrones
    {
        public List<Hallazgo> Hallazgos = new List<Hallazgo>();
        public List<Tramo> TramosHoy = new List<Tramo>();
        public double[] CambiosPorHora = new double[24];     // los míos, del histórico entero
        public Dictionary<string, double> MinutosHoy = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public int CambiosHoy, CambiosAyer;
        public double MedianaCambiosDia;
        public TimeSpan RachaMasLarga;
        public string RachaEstado = "";
        public DateTime? PrimerCambioHoy, UltimoCambioHoy;

        public void Analizar(IList<MiCambio> mios, Movimiento[] movs, EstadoPersona[] gente, Presencia pres)
        {
            Hallazgos = new List<Hallazgo>();
            TramosHoy = new List<Tramo>();
            CambiosPorHora = new double[24];
            MinutosHoy = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            mios = mios ?? new List<MiCambio>();
            movs = movs ?? new Movimiento[0];
            // 🚨 el chat propio aparece en la lista de chats: mis patrones salen del log nativo, no de ahí
            gente = (gente ?? new EstadoPersona[0]).Where(p => p.Tipo != "yo").ToArray();

            MisTramos(mios);
            MisPatrones(mios);
            PatronesDelEquipo(movs, gente);
            if (pres != null) PatronesDeLaApp(pres);

            Hallazgos = Hallazgos.OrderByDescending(h => h.Peso).ThenByDescending(h => h.Cuando).ToList();
        }

        // ---------------------------------------------------------------- lo mío

        void MisTramos(IList<MiCambio> mios)
        {
            var hoy = mios.Where(c => c.Cuando.Date == DateTime.Today).OrderBy(c => c.Cuando).ToList();
            // el día arranca con el estado en que lo dejó el último cambio de ayer
            var previo = mios.LastOrDefault(c => c.Cuando.Date < DateTime.Today);
            if (previo != null) hoy.Insert(0, new MiCambio { Cuando = DateTime.Today, Estado = previo.Estado, Token = previo.Token });
            for (int i = 0; i < hoy.Count; i++)
            {
                var fin = i + 1 < hoy.Count ? hoy[i + 1].Cuando : DateTime.Now;
                if (fin <= hoy[i].Cuando) continue;
                TramosHoy.Add(new Tramo { Desde = hoy[i].Cuando, Hasta = fin, Estado = hoy[i].Estado, Token = hoy[i].Token });
                string k = hoy[i].Estado;
                MinutosHoy[k] = (MinutosHoy.ContainsKey(k) ? MinutosHoy[k] : 0) + (fin - hoy[i].Cuando).TotalMinutes;
            }

            foreach (var c in mios) CambiosPorHora[c.Cuando.Hour]++;
            CambiosHoy = mios.Count(c => c.Cuando.Date == DateTime.Today);
            CambiosAyer = mios.Count(c => c.Cuando.Date == DateTime.Today.AddDays(-1));
            PrimerCambioHoy = mios.Where(c => c.Cuando.Date == DateTime.Today).Select(c => (DateTime?)c.Cuando).FirstOrDefault();
            UltimoCambioHoy = mios.Where(c => c.Cuando.Date == DateTime.Today).Select(c => (DateTime?)c.Cuando).LastOrDefault();

            var porDia = mios.GroupBy(c => c.Cuando.Date).Select(g => (double)g.Count()).OrderBy(v => v).ToList();
            MedianaCambiosDia = porDia.Count == 0 ? 0 : porDia[porDia.Count / 2];

            var larga = TramosHoy.OrderByDescending(t => t.Dura).FirstOrDefault();
            if (larga != null) { RachaMasLarga = larga.Dura; RachaEstado = larga.Estado; }
        }

        void MisPatrones(IList<MiCambio> mios)
        {
            // 1) parpadeo: muchos cambios en muy poco tiempo (típico de pelearse con el estado)
            var orden = mios.OrderBy(c => c.Cuando).ToList();
            for (int i = 0; i + 3 < orden.Count; i++)
            {
                var ventana = orden[i + 3].Cuando - orden[i].Cuando;
                if (ventana.TotalMinutes > 10) continue;
                Hallazgos.Add(Hallazgo.H(2, Tema.Rosa, "yo", "parpadeo de estado",
                    $"4 cambios en {(int)ventana.TotalMinutes} min desde las {orden[i].Cuando:HH:mm} — algo te estaba moviendo el estado", orden[i].Cuando));
                i += 3;
            }

            // 2) tramo larguísimo sin moverse
            foreach (var t in TramosHoy.Where(t => t.Dura.TotalHours >= 6))
                Hallazgos.Add(Hallazgo.H(1, Estados.EsDisponible(t.Estado) ? Tema.Salvia : Tema.Durazno, "yo",
                    $"{t.Dura.TotalHours:0.#} h seguidas en {t.Estado}",
                    $"de {t.Desde:HH:mm} a {t.Hasta:HH:mm} sin un solo cambio", t.Desde));

            // 3) actividad de madrugada
            foreach (var g in mios.Where(c => c.Cuando.Hour >= 0 && c.Cuando.Hour < 6).GroupBy(c => c.Cuando.Date).OrderByDescending(g2 => g2.Key).Take(3))
                Hallazgos.Add(Hallazgo.H(1, Tema.Malva, "yo", "estuviste de madrugada",
                    $"{g.Count()} cambio/s el {g.Key:dd/MM} entre las {g.Min(c => c.Cuando):HH:mm} y las {g.Max(c => c.Cuando):HH:mm}", g.Max(c => c.Cuando)));

            // 4) hoy contra tu propia mediana
            if (MedianaCambiosDia >= 3 && CambiosHoy > MedianaCambiosDia * 2)
                Hallazgos.Add(Hallazgo.H(2, Tema.Durazno, "yo", "día movido",
                    $"{CambiosHoy} cambios hoy contra una mediana de {MedianaCambiosDia:0} — más del doble de lo normal"));

            // 5) proporción de Ausente en lo que va del día
            double total = MinutosHoy.Values.Sum();
            double aus = MinutosHoy.Where(k => Estados.EsAusente(k.Key)).Sum(k => k.Value);
            if (total > 60 && aus / total > 0.4)
                Hallazgos.Add(Hallazgo.H(2, Tema.Rosa, "yo", "mucho Ausente hoy",
                    $"{aus / total * 100:0} % del día te vieron Ausente ({(int)aus} min de {(int)total})"));

            // 6) hora en la que más se te mueve el estado
            if (CambiosPorHora.Sum() >= 8)
            {
                int pico = Array.IndexOf(CambiosPorHora, CambiosPorHora.Max());
                Hallazgos.Add(Hallazgo.H(0, Tema.Cielo, "yo", $"tu hora más inestable es a las {pico:00}",
                    $"{CambiosPorHora[pico]:0} de tus {CambiosPorHora.Sum():0} cambios pasaron entre las {pico:00} y las {(pico + 1) % 24:00}"));
            }
        }

        // ---------------------------------------------------------------- el equipo

        void PatronesDelEquipo(Movimiento[] movs, EstadoPersona[] gente)
        {
            var hoy = movs.Where(m => m.Hora.Date == DateTime.Today).ToList();

            var primero = hoy.Where(m => m.SeConecto).OrderBy(m => m.Hora).FirstOrDefault();
            if (primero != null)
                Hallazgos.Add(Hallazgo.H(0, Tema.Salvia, primero.Persona, "el primero en aparecer hoy",
                    $"{primero.Persona} se conectó a las {primero.Hora:HH:mm}", primero.Hora));

            var ultimo = hoy.Where(m => m.SeDesconecto).OrderByDescending(m => m.Hora).FirstOrDefault();
            if (ultimo != null)
                Hallazgos.Add(Hallazgo.H(0, Tema.Apagado, ultimo.Persona, "el último en irse",
                    $"{ultimo.Persona} se desconectó a las {ultimo.Hora:HH:mm}", ultimo.Hora));

            var inquieto = hoy.GroupBy(m => m.Persona).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (inquieto != null && inquieto.Count() >= 6)
                Hallazgos.Add(Hallazgo.H(1, Tema.Durazno, inquieto.Key, "el más inquieto del día",
                    $"{inquieto.Key} cambió de estado {inquieto.Count()} veces hoy"));

            foreach (var p in gente.Where(p => p.Cambios == 0 && Estados.EsDesconectado(p.Presencia)).Take(3))
                Hallazgos.Add(Hallazgo.H(0, Tema.MuyApagado, p.Nombre, "no dio señales",
                    $"{p.Nombre} viene Desconectado desde que lo miro"));

            foreach (var p in gente)
            {
                var suma = p.Disponible + p.Ausente + p.Ocupado + p.Desconectado;
                if (suma.TotalMinutes < 30) continue;
                if (p.Ocupado.TotalMinutes / suma.TotalMinutes > 0.6)
                    Hallazgos.Add(Hallazgo.H(1, Tema.Rosa, p.Nombre, "prácticamente siempre Ocupado",
                        $"{p.Nombre}: {p.Ocupado.TotalMinutes / suma.TotalMinutes * 100:0} % del tiempo que lo vi"));
                else if (p.Disponible.TotalMinutes / suma.TotalMinutes > 0.95 && suma.TotalHours > 3)
                    Hallazgos.Add(Hallazgo.H(0, Tema.Cielo, p.Nombre, "Disponible sin un solo hueco",
                        $"{p.Nombre}: {suma.TotalHours:0.#} h y nunca lo vi en otro estado — ¿o tiene algo como esto?"));
            }

            // gente que se desconecta siempre a la misma hora
            foreach (var g in movs.Where(m => m.SeDesconecto).GroupBy(m => m.Persona))
            {
                var horas = g.Select(m => m.Hora.TimeOfDay.TotalMinutes).ToList();
                if (horas.Count < 3) continue;
                double prom = horas.Average();
                double desvio = Math.Sqrt(horas.Sum(h => (h - prom) * (h - prom)) / horas.Count);
                if (desvio <= 20)
                    Hallazgos.Add(Hallazgo.H(1, Tema.Malva, g.Key, "relojito",
                        $"{g.Key} se desconecta siempre cerca de las {TimeSpan.FromMinutes(prom):hh\\:mm} (±{desvio:0} min, {horas.Count} veces)"));
            }
        }

        void PatronesDeLaApp(Presencia pres)
        {
            if (pres.Rendido)
                Hallazgos.Add(Hallazgo.H(2, Tema.Rosa, "la app", "no puedo sostener la presencia",
                    $"Teams vuelve a «{pres.PresenciaTeams}» pase lo que pase · {pres.RescateDetalle}"));
            if (pres.RespetandoManual)
                Hallazgos.Add(Hallazgo.H(1, Tema.Crema, "vos", "estado puesto a mano",
                    $"detecté que «{pres.PresenciaTeams}» lo pusiste vos y lo estoy respetando"));
            if (pres.Derivas > 0 && pres.Correcciones == 0)
                Hallazgos.Add(Hallazgo.H(2, Tema.Durazno, "la app", "derivas sin corregir",
                    $"{pres.Derivas} veces se fue a Ausente y no logré traerlo ninguna"));
            if (pres.Forzados > 0)
                Hallazgos.Add(Hallazgo.H(1, Tema.Cyan, "la app", "hubo que forzarlo",
                    $"{pres.Forzados} vez/veces la F15 no alcanzó y tuve que fijar Disponible desde el menú del avatar"));
            if (pres.Sondas > 0)
                Hallazgos.Add(Hallazgo.H(0, Tema.Malva, "la app", "Teams estaba dormido",
                    $"{pres.Sondas} vez/veces tuve que remontar la ventana para poder leer el estado"));
        }
    }
}
