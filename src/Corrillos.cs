using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TeamsTools
{
    // =====================================================================================================
    // CORRILLOS: quién estuvo en una llamada CON QUIÉN
    //
    // Hay tres formas de saberlo y no valen lo mismo. Se usan las tres, en este orden:
    //
    //   1. ⭐⭐ LA LLAMADA — cada `Event/Call` del historial de Teams trae un `<partlist>` con la lista EXACTA
    //      de participantes y la duración. Son 1.930 llamadas desde 2022, 8.278 participantes, 93 % con
    //      nombre resuelto. Es verdad, no inferencia. Estaba tirándose a la basura: el extractor marcaba los
    //      Event/Call como ruido y se quedaba solo con el `texto`, que en esos mensajes está vacío.
    //   2. EL ROSTER — cuando el user está EN la reunión, la ventana de Teams lista a los presentes. También
    //      es verdad, pero solo mientras la ventana sea legible y solo de las reuniones suyas.
    //   3. DEDUCIDO — quiénes entraron y salieron de «En una llamada» a la vez, según el observador de
    //      presencia. Es lo único que cubre las llamadas de HOY, antes de que Teams las escriba al historial.
    //
    // Lo confirmado le gana a lo deducido cuando se superponen: es el mismo hecho, mejor contado.
    // =====================================================================================================

    /// <summary>De dónde salió el dato. Determina cuánto se le puede creer.</summary>
    internal enum FuenteCorrillo { Deducido = 0, Roster = 1, Llamada = 2 }

    /// <summary>Un tramo continuo de alguien dentro de una llamada.</summary>
    internal sealed class SesionLlamada
    {
        public string Persona = "";
        public DateTime Ini, Fin;
        public bool Abierta;                       // sigue en la llamada ahora mismo
        public int Segundos;                       // lo que dijo Teams, cuando lo dice
        public TimeSpan Dura => Segundos > 0 ? TimeSpan.FromSeconds(Segundos) : Fin - Ini;
    }

    /// <summary>Un grupo de gente que estuvo en la misma llamada.</summary>
    internal sealed class Corrillo
    {
        public readonly List<SesionLlamada> Gente = new List<SesionLlamada>();
        public DateTime Ini, Fin;
        public bool Viva;                          // todavía hay alguien adentro
        public FuenteCorrillo Fuente = FuenteCorrillo.Deducido;
        public string Titulo = "";                 // nombre de la reunión, si se conoce
        public bool ConVos;
        public double Confianza = 1.0;             // mediana de la contención entre miembros (solo deducidos)
        public bool SoloEnCurso;                   // todos siguen adentro: se parecen porque nadie cortó
        public string CallId = "";

        public bool Confirmado => Fuente != FuenteCorrillo.Deducido;
        public int Personas => Gente.Count;
        public TimeSpan Dura => Fin - Ini;

        /// <summary>Cómo se supo. Lo deducido lleva además cuánto se parecen los tramos.</summary>
        public string ComoLoSe
        {
            get
            {
                if (Fuente == FuenteCorrillo.Llamada) return "la llamada";
                if (Fuente == FuenteCorrillo.Roster) return "el roster";
                if (SoloEnCurso) return "a la vez";
                return Confianza >= 0.85 ? "muy parecido" : Confianza >= 0.6 ? "se solapan" : "puede ser";
            }
        }

        public string Nombres(int max = 8)
        {
            var l = Gente.OrderByDescending(g => g.Dura).ThenBy(g => g.Ini)
                         .Select(g => VistaEquipo.Apellido(g.Persona)).Distinct().ToList();
            return string.Join(", ", l.Take(max)) + (l.Count > max ? " +" + (l.Count - max) : "");
        }

        /// <summary>
        /// Quiénes llegaron tarde y cuánto. 🚨 Solo tiene sentido en lo deducido y el roster: en el partlist
        /// Teams da UNA sola duración para todos, así que ahí nadie llega tarde aunque haya llegado.
        /// </summary>
        public string Tarde(int minMinutos = 3)
        {
            if (Fuente == FuenteCorrillo.Llamada) return "";
            var l = Gente.Where(g => (g.Ini - Ini).TotalMinutes >= minMinutos)
                         .OrderByDescending(g => g.Ini - Ini)
                         .Select(g => VistaEquipo.Apellido(g.Persona) + " " + (int)(g.Ini - Ini).TotalMinutes + "′")
                         .ToList();
            return string.Join(" · ", l.Take(4));
        }
    }

    /// <summary>Con quién te juntás, cuánto y desde cuándo.</summary>
    internal sealed class Companero
    {
        public string Nombre = "";
        public int Llamadas;                       // llamadas suyas, con vos o sin vos
        public int Juntos;                         // llamadas compartidas con vos
        public TimeSpan Tiempo;                    // tiempo compartido
        public DateTime Primera = DateTime.MaxValue, Ultima = DateTime.MinValue;
        public int SoloUstedes;                    // de esas, cuántas fueron de a dos
        /// <summary>De todas las llamadas de esta persona, qué parte compartió con vos.</summary>
        public double Cercania => Llamadas > 0 ? (double)Juntos / Llamadas : 0;
    }

    /// <summary>
    /// Reconstruye las llamadas del equipo. No abre Teams ni consulta nada: relee lo que ya está en disco.
    /// </summary>
    internal sealed class Corrillos
    {
        /// <summary>Cuánto del tramo más corto tiene que caer adentro del otro para ser la misma llamada.</summary>
        public const double MinContencion = 0.70;
        /// <summary>Dos personas de la misma llamada entran cerca. Más de esto y son llamadas distintas.</summary>
        public const double MinutosArranque = 15;
        /// <summary>Un corte más breve que esto es una reconexión, no dos llamadas.</summary>
        public const double MinutosReconexion = 2;
        /// <summary>Más gente que esto y es un townhall: no dice nada de quién trabaja con quién.</summary>
        public const int MaxParaParejas = 25;

        public Corrillo[] Todos = new Corrillo[0];
        public SesionLlamada[] Solitarias = new SesionLlamada[0];
        public string Problema = "";
        public int Eventos, Sesiones, DeLaLlamada, DelRoster, Deducidos;
        public DateTime Calculado;
        public long Ms;
        public string Yo = "";

        public Corrillo[] Vivos => Todos.Where(c => c.Viva).ToArray();
        public Corrillo[] DeHoy => Todos.Where(c => c.Ini.Date == DateTime.Today).ToArray();
        public Corrillo[] Mias => Todos.Where(c => c.ConVos).ToArray();
        public bool Hay => Todos.Length > 0;

        public Corrillo DondeEsta(string persona)
        {
            string k = Contactos.Normalizar(persona ?? "");
            return Todos.FirstOrDefault(c => c.Viva && c.Gente.Any(g => Contactos.Normalizar(g.Persona) == k));
        }

        // ------------------------------------------------------------------ lo que se lee de todo esto

        /// <summary>Con quiénes te juntás, ordenados por cuántas llamadas compartieron.</summary>
        public Companero[] Companeros(int top = 20)
        {
            string yo = Contactos.Normalizar(Yo);
            var ix = new Dictionary<string, Companero>(StringComparer.Ordinal);
            foreach (var c in Todos)
            {
                if (!c.ConVos) continue;
                var otros = c.Gente.Where(g => Contactos.Normalizar(g.Persona) != yo).ToList();
                foreach (var g in otros)
                {
                    string k = Contactos.Normalizar(g.Persona);
                    Companero x;
                    if (!ix.TryGetValue(k, out x)) { x = new Companero { Nombre = g.Persona }; ix[k] = x; }
                    x.Juntos++;
                    x.Tiempo += c.Dura;
                    if (c.Ini < x.Primera) x.Primera = c.Ini;
                    if (c.Ini > x.Ultima) x.Ultima = c.Ini;
                    if (otros.Count == 1) x.SoloUstedes++;
                }
            }
            // el total de llamadas de cada uno (con vos o sin vos) da la cercanía
            foreach (var c in Todos)
                foreach (var g in c.Gente)
                {
                    Companero x;
                    if (ix.TryGetValue(Contactos.Normalizar(g.Persona), out x)) x.Llamadas++;
                }
            return ix.Values.OrderByDescending(x => x.Juntos).ThenByDescending(x => x.Tiempo).Take(top).ToArray();
        }

        /// <summary>Las parejas que más veces coincidieron, estés vos o no.</summary>
        public Tuple<string, string, int>[] Parejas(int top = 12)
        {
            // 🚨 clave tipada, no una cadena con separador: los nombres vienen de Teams y no hay carácter
            //    que se pueda garantizar ausente. Un Tuple compara por valor y no hay nada que escapar.
            var cuenta = new Dictionary<Tuple<string, string>, int>();
            var nombre = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var c in Todos)
            {
                var gente = c.Gente.Select(g => g.Persona)
                                   .GroupBy(Contactos.Normalizar).Select(g => g.First())
                                   .OrderBy(Contactos.Normalizar, StringComparer.Ordinal).ToList();
                if (gente.Count > MaxParaParejas) continue;
                foreach (var g in gente) nombre[Contactos.Normalizar(g)] = g;
                for (int i = 0; i < gente.Count; i++)
                    for (int j = i + 1; j < gente.Count; j++)
                    {
                        var k = Tuple.Create(Contactos.Normalizar(gente[i]), Contactos.Normalizar(gente[j]));
                        int n; cuenta.TryGetValue(k, out n);
                        cuenta[k] = n + 1;
                    }
            }
            return cuenta.OrderByDescending(p => p.Value).Take(top)
                .Select(p => Tuple.Create(
                    nombre.ContainsKey(p.Key.Item1) ? nombre[p.Key.Item1] : p.Key.Item1,
                    nombre.ContainsKey(p.Key.Item2) ? nombre[p.Key.Item2] : p.Key.Item2,
                    p.Value)).ToArray();
        }

        /// <summary>
        /// Grupos que se repiten: el mismo conjunto de gente juntándose muchas veces. Es lo que distingue una
        /// reunión recurrente de una junta casual, y sale sin mirar el calendario.
        /// </summary>
        public Tuple<string, int, TimeSpan>[] Recurrentes(int minVeces = 3, int top = 10)
        {
            var ix = new Dictionary<string, List<Corrillo>>(StringComparer.Ordinal);
            foreach (var c in Todos)
            {
                if (c.Personas < 3 || c.Personas > MaxParaParejas) continue;
                string k = string.Join("|", c.Gente.Select(g => Contactos.Normalizar(g.Persona))
                                                   .Distinct().OrderBy(x => x, StringComparer.Ordinal));
                List<Corrillo> l;
                if (!ix.TryGetValue(k, out l)) { l = new List<Corrillo>(); ix[k] = l; }
                l.Add(c);
            }
            return ix.Values.Where(l => l.Count >= minVeces)
                .OrderByDescending(l => l.Count).Take(top)
                .Select(l => Tuple.Create(l[0].Nombres(5), l.Count,
                    TimeSpan.FromSeconds(l.Average(c => c.Dura.TotalSeconds))))
                .ToArray();
        }

        /// <summary>Quién pasa más tiempo en llamadas, mire quien mire.</summary>
        public Tuple<string, TimeSpan, int>[] Ranking(int top = 12)
        {
            return Todos.SelectMany(c => c.Gente.Select(g => new { g.Persona, c.Dura }))
                .GroupBy(x => Contactos.Normalizar(x.Persona))
                .Select(g => Tuple.Create(g.First().Persona,
                                          TimeSpan.FromSeconds(g.Sum(x => x.Dura.TotalSeconds)), g.Count()))
                .OrderByDescending(t => t.Item2).Take(top).ToArray();
        }

        /// <summary>A qué hora se junta el equipo: llamadas por hora del día.</summary>
        public int[] PorHora()
        {
            var h = new int[24];
            foreach (var c in Todos) h[c.Ini.Hour]++;
            return h;
        }

        // ------------------------------------------------------------------ el cálculo
        public void Calcular(RegistroPresencia reg, string rutaHistorial, string rutaConfirmados, Historia hist)
        {
            var reloj = System.Diagnostics.Stopwatch.StartNew();
            Problema = "";
            var juntados = new List<Corrillo>();

            // ---------- 1) la fuente buena: el partlist de cada Event/Call ----------
            DeLaLlamada = 0;
            if (hist != null && hist.Llamadas.Length > 0)
            {
                Yo = hist.Mensajes.Where(m => m.Mio && m.Autor.Length > 1
                                         && !m.Autor.StartsWith("19:", StringComparison.Ordinal))
                                  .GroupBy(m => m.Autor, StringComparer.Ordinal)
                                  .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
                string yoK = Contactos.Normalizar(Yo);

                // 🚨 Teams manda VARIOS Event/Call por llamada (uno al empezar, otro al terminar). Se juntan
                //    por callId y gana el que trae más gente; si no, la misma reunión aparecía dos o tres
                //    veces en la lista, y encima las de «started» vienen con el partlist vacío.
                foreach (var grupo in hist.Llamadas.GroupBy(m => m.CallId.Length > 0 ? m.CallId : m.Id))
                {
                    var mejor = grupo.OrderByDescending(m => m.Llamada.Count(p => p.EsPersona)).First();
                    var gente = mejor.Llamada.Where(p => p.EsPersona)
                                             .GroupBy(p => Contactos.Normalizar(p.Nombre))
                                             .Select(g => g.First()).ToList();
                    if (gente.Count < 2) continue;
                    var ini = grupo.Min(m => m.Fecha);
                    int seg = gente.Max(p => p.Segundos);
                    var c = new Corrillo
                    {
                        Ini = ini,
                        Fin = seg > 0 ? ini.AddSeconds(seg) : grupo.Max(m => m.Fecha),
                        Fuente = FuenteCorrillo.Llamada,
                        Confianza = 1.0,
                        CallId = mejor.CallId,
                        Titulo = mejor.Conv ?? "",
                        ConVos = gente.Any(p => Contactos.Normalizar(p.Nombre) == yoK),
                    };
                    foreach (var p in gente)
                        c.Gente.Add(new SesionLlamada { Persona = p.Nombre, Ini = c.Ini, Fin = c.Fin, Segundos = p.Segundos });
                    juntados.Add(c);
                    DeLaLlamada++;
                }
            }

            // ---------- 2) el roster de las reuniones donde estuviste ----------
            DelRoster = 0;
            foreach (var c in LeerConfirmados(rutaConfirmados))
            {
                if (juntados.Any(x => x.Fuente == FuenteCorrillo.Llamada && Pisa(x, c))) continue;
                juntados.Add(c); DelRoster++;
            }

            // ---------- 3) lo deducido de la presencia, para lo que las otras no cubren ----------
            Deducidos = 0;
            foreach (var c in Deducir(reg, rutaHistorial))
            {
                if (juntados.Any(x => x.Confirmado && Pisa(x, c))) continue;
                juntados.Add(c); Deducidos++;
            }

            Todos = juntados.OrderByDescending(c => c.Ini).ToArray();
            Calculado = DateTime.Now;
            Ms = reloj.ElapsedMilliseconds;
        }

        /// <summary>¿Estos dos corrillos son el mismo hecho contado dos veces?</summary>
        static bool Pisa(Corrillo a, Corrillo b)
        {
            if (b.Ini >= a.Fin || a.Ini >= b.Fin) return false;
            var ini = a.Ini > b.Ini ? a.Ini : b.Ini;
            var fin = a.Fin < b.Fin ? a.Fin : b.Fin;
            double inter = (fin - ini).TotalSeconds;
            double corta = Math.Min(Math.Max(1, a.Dura.TotalSeconds), Math.Max(1, b.Dura.TotalSeconds));
            return inter / corta >= 0.5;
        }

        // ------------------------------------------------------------------ deducción por presencia
        List<Corrillo> Deducir(RegistroPresencia reg, string rutaHistorial)
        {
            var eventos = new List<Tuple<DateTime, string, string>>();   // hora, persona, estado nuevo

            if (!string.IsNullOrEmpty(rutaHistorial) && File.Exists(rutaHistorial))
            {
                try
                {
                    foreach (var linea in File.ReadAllLines(rutaHistorial))
                    {
                        if (linea.Length < 20) continue;
                        string sh = Campo(linea, "hora"), pe = Campo(linea, "persona"), ha = Campo(linea, "hasta");
                        DateTime h;
                        if (pe.Length == 0 || !DateTime.TryParse(sh, CultureInfo.InvariantCulture, DateTimeStyles.None, out h)) continue;
                        eventos.Add(Tuple.Create(h, pe, ha));
                    }
                }
                catch (Exception ex) { Problema = "no pude leer el historial de presencia: " + ex.Message; }
            }
            if (reg != null)
                foreach (var m in reg.Movimientos())
                    eventos.Add(Tuple.Create(m.Hora, m.Persona, m.Hasta));

            // 🚨 los dos orígenes se pisan: el mismo movimiento está en memoria y en el archivo
            eventos = eventos.GroupBy(e => Contactos.Normalizar(e.Item2) + "|" + e.Item1.ToString("s"))
                             .Select(g => g.First())
                             .OrderBy(e => e.Item1).ToList();
            Eventos = eventos.Count;

            var abiertas = new Dictionary<string, SesionLlamada>(StringComparer.Ordinal);
            var sesiones = new List<SesionLlamada>();
            foreach (var e in eventos)
            {
                string k = Contactos.Normalizar(e.Item2);
                SesionLlamada s;
                if (Estados.EsEnLlamada(e.Item3))
                {
                    if (abiertas.TryGetValue(k, out s)) continue;
                    abiertas[k] = new SesionLlamada { Persona = e.Item2, Ini = e.Item1, Fin = e.Item1 };
                }
                else if (abiertas.TryGetValue(k, out s))
                {
                    s.Fin = e.Item1; abiertas.Remove(k); sesiones.Add(s);
                }
            }
            var ahora = DateTime.Now;
            foreach (var s in abiertas.Values) { s.Fin = ahora; s.Abierta = true; sesiones.Add(s); }

            // 🚨 Teams parpadea: alguien sale y vuelve en un minuto. Eso es UNA llamada, no dos — si no, la
            //    misma persona aparecía dos veces en el mismo corrillo.
            sesiones = sesiones.OrderBy(s => Contactos.Normalizar(s.Persona)).ThenBy(s => s.Ini).ToList();
            var limpias = new List<SesionLlamada>();
            foreach (var s in sesiones)
            {
                var ult = limpias.Count > 0 ? limpias[limpias.Count - 1] : null;
                if (ult != null && Contactos.Normalizar(ult.Persona) == Contactos.Normalizar(s.Persona)
                    && (s.Ini - ult.Fin).TotalMinutes <= MinutosReconexion)
                { ult.Fin = s.Fin; ult.Abierta = s.Abierta; continue; }
                limpias.Add(s);
            }
            sesiones = limpias.OrderBy(s => s.Ini).ToList();
            Sesiones = sesiones.Count;

            int n = sesiones.Count;
            var padre = new int[n];
            for (int i = 0; i < n; i++) padre[i] = i;
            Func<int, int> raiz = null;
            raiz = x => { while (padre[x] != x) { padre[x] = padre[padre[x]]; x = padre[x]; } return x; };

            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (Contactos.Normalizar(sesiones[i].Persona) == Contactos.Normalizar(sesiones[j].Persona)) continue;
                    if (Math.Abs((sesiones[i].Ini - sesiones[j].Ini).TotalMinutes) > MinutosArranque) continue;
                    double cont, jac;
                    Solape(sesiones[i], sesiones[j], out cont, out jac);
                    if (cont < MinContencion) continue;
                    int a = raiz(i), b = raiz(j);
                    if (a != b) padre[a] = b;
                }

            var grupos = new Dictionary<int, Corrillo>();
            for (int i = 0; i < n; i++)
            {
                int r = raiz(i);
                Corrillo c;
                if (!grupos.TryGetValue(r, out c)) { c = new Corrillo { Ini = DateTime.MaxValue, Fin = DateTime.MinValue }; grupos[r] = c; }
                c.Gente.Add(sesiones[i]);
                if (sesiones[i].Ini < c.Ini) c.Ini = sesiones[i].Ini;
                if (sesiones[i].Fin > c.Fin) c.Fin = sesiones[i].Fin;
                if (sesiones[i].Abierta) c.Viva = true;
            }

            // la confianza se calcula sobre el grupo YA armado: mediana de la contención de a pares.
            // 🚨 con el MÍNIMO, uno que llega 15 min tarde hundía toda la reunión a «puede ser».
            foreach (var c in grupos.Values)
            {
                c.SoloEnCurso = c.Gente.Count > 1 && c.Gente.All(x => x.Abierta);
                var pares = new List<double>();
                for (int i = 0; i < c.Gente.Count; i++)
                    for (int j = i + 1; j < c.Gente.Count; j++)
                    {
                        double cont, jac; Solape(c.Gente[i], c.Gente[j], out cont, out jac);
                        pares.Add(cont);
                    }
                c.Confianza = pares.Count == 0 ? 1.0 : Mediana(pares);
            }

            Solitarias = grupos.Values.Where(c => c.Personas == 1).Select(c => c.Gente[0])
                                      .OrderByDescending(s => s.Ini).ToArray();
            return grupos.Values.Where(c => c.Personas > 1).ToList();
        }

        static double Mediana(List<double> xs)
        {
            xs.Sort();
            return xs.Count % 2 == 1 ? xs[xs.Count / 2] : (xs[xs.Count / 2 - 1] + xs[xs.Count / 2]) / 2.0;
        }

        static void Solape(SesionLlamada a, SesionLlamada b, out double contencion, out double jaccard)
        {
            contencion = jaccard = 0;
            var ini = a.Ini > b.Ini ? a.Ini : b.Ini;
            var fin = a.Fin < b.Fin ? a.Fin : b.Fin;
            if (fin <= ini) return;
            double inter = (fin - ini).TotalSeconds;
            double corta = Math.Min(a.Dura.TotalSeconds, b.Dura.TotalSeconds);
            double union = ((a.Fin > b.Fin ? a.Fin : b.Fin) - (a.Ini < b.Ini ? a.Ini : b.Ini)).TotalSeconds;
            if (corta > 0) contencion = inter / corta;
            if (union > 0) jaccard = inter / union;
        }

        // ------------------------------------------------------------------ verdad de campo del roster
        /// <summary>
        /// Cuando el user está EN la reunión, el roster de Teams da los nombres y no hay nada que inferir.
        /// </summary>
        public static void Anotar(string ruta, string reunion, List<string> nombres, string yo)
        {
            if (string.IsNullOrEmpty(ruta) || nombres == null || nombres.Count == 0) return;
            try
            {
                var d = new Dictionary<string, object>
                {
                    ["hora"] = DateTime.Now,
                    ["reunion"] = reunion ?? "",
                    ["yo"] = yo ?? "",
                    ["gente"] = string.Join(" | ", nombres.Where(x => x.Length > 0).Distinct()),
                };
                Disco.Agregar(ruta, Json.Texto(d).Replace("\n", " ") + Environment.NewLine);
            }
            catch { }
        }

        List<Corrillo> LeerConfirmados(string ruta)
        {
            var res = new List<Corrillo>();
            if (string.IsNullOrEmpty(ruta) || !File.Exists(ruta)) return res;
            try
            {
                // una reunión deja muchas líneas (una por lectura): se junta por título y hora
                var porReunion = new Dictionary<string, Corrillo>(StringComparer.OrdinalIgnoreCase);
                foreach (var linea in File.ReadAllLines(ruta))
                {
                    if (linea.Length < 20) continue;
                    string sh = Campo(linea, "hora"), reu = Campo(linea, "reunion"), gente = Campo(linea, "gente");
                    DateTime h;
                    if (gente.Length == 0 || !DateTime.TryParse(sh, CultureInfo.InvariantCulture, DateTimeStyles.None, out h)) continue;
                    string clave = reu + "|" + h.ToString("yyyy-MM-dd HH");
                    Corrillo c;
                    if (!porReunion.TryGetValue(clave, out c))
                    {
                        c = new Corrillo { Ini = h, Fin = h, Fuente = FuenteCorrillo.Roster, ConVos = true, Titulo = reu, Confianza = 1.0 };
                        porReunion[clave] = c;
                    }
                    if (h < c.Ini) c.Ini = h;
                    if (h > c.Fin) c.Fin = h;
                    foreach (var nombre in gente.Split('|'))
                    {
                        string nn = nombre.Trim();
                        if (nn.Length == 0) continue;
                        if (c.Gente.Any(g => Contactos.Normalizar(g.Persona) == Contactos.Normalizar(nn))) continue;
                        c.Gente.Add(new SesionLlamada { Persona = nn, Ini = h, Fin = h });
                    }
                }
                foreach (var c in porReunion.Values.Where(x => x.Personas > 1))
                {
                    foreach (var g in c.Gente) { g.Ini = c.Ini; g.Fin = c.Fin; }
                    res.Add(c);
                }
            }
            catch { }
            return res;
        }

        /// <summary>Lector de un campo del jsonl sin traer un parser entero: son objetos planos.</summary>
        static string Campo(string l, string clave)
        {
            int i = l.IndexOf("\"" + clave + "\"", StringComparison.Ordinal);
            if (i < 0) return "";
            i += clave.Length + 2;
            while (i < l.Length && l[i] != '"' && l[i] != ',' && l[i] != '}') i++;
            if (i >= l.Length || l[i] != '"') return "";
            var sb = new StringBuilder();
            for (int k = i + 1; k < l.Length; k++)
            {
                char c = l[k];
                if (c == '\\' && k + 1 < l.Length) { sb.Append(l[++k]); continue; }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
