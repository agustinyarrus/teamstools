using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TeamsTools
{
    // =====================================================================================================
    // DETECTIVE DE NOMBRES — «Persona 7» es Vale, y lo dice la propia reunión.
    //
    // La diarización separa las voces pero no sabe de quién son. La conversación sí: la gente se nombra al darse la
    // palabra («…cada uno puede explorar. Sí, Vale.» → enseguida habla Persona 7), al presentarse («soy Valentina») y al
    // preguntar («María, ¿por qué te pone 12?» → contesta Persona 1). Cada pista SUMA a un par (voz, persona del panel
    // de Teams); hablarle a alguien o nombrarlo en tercera persona RESTA a quien habla (nadie se llama a sí mismo).
    // Al final, una ASIGNACIÓN ÓPTIMA (algoritmo húngaro) reparte los nombres: una persona por voz y el mayor puntaje
    // total, así dos voces nunca se quedan con el mismo nombre. Son SUGERENCIAS con su evidencia: nunca se aplican solas.
    // =====================================================================================================

    /// <summary>Una evidencia a favor (o en contra) de que la voz sea esa persona.</summary>
    internal sealed class Pista
    {
        public string Voz = "", Persona = "";
        public double Peso, Seg;
        public string Porque = "";
    }

    /// <summary>«Persona 7 es Valentina Rivas», con cuánta confianza y por qué.</summary>
    internal sealed class Sugerencia
    {
        public string Voz = "";
        /// <summary>Como se nombra a alguien: «Valentina Rivas».</summary>
        public string Persona = "";
        public double Puntaje, Confianza;
        public List<Pista> Pistas = new List<Pista>();
        public string Explicacion => string.Join(" · ", Pistas.Where(p => p.Peso > 0).OrderByDescending(p => p.Peso).Take(3).Select(p => p.Porque));
    }

    internal static class Detective
    {
        /// <summary>Una pista buena y limpia (darle la palabra con el nombre exacto) vale 1: menos que eso es ruido.</summary>
        const double Umbral = 0.9;
        /// <summary>Lo que tiene que sacarle a la segunda persona más probable para esa voz.</summary>
        const double Margen = 0.3;
        const double PesoDarPalabra = 1.0, PesoResponder = 0.45, PesoPresentarse = 2.0, PesoTercera = -1.0, PesoNoSoyYo = -0.6, PesoTitulo = 0.8;
        /// <summary>Si el que sigue tarda más que esto en hablar, quizás no le estaban hablando a él: la pista vale la mitad.</summary>
        const double SegundosDeRespuesta = 8;
        const int LargoMinimo = 3;
        const int LargoFragmento = 46;

        /// <summary>Lo que se dice antes de nombrar a alguien al principio de un turno («Sí, Vale…», «Dale, Bruno…»).</summary>
        static readonly HashSet<string> Muletillas = new HashSet<string>(StringComparer.Ordinal)
        {
            "si", "bueno", "dale", "gracias", "perdon", "hola", "che", "ok", "okey", "claro", "ah", "eh", "no", "genial",
            "joya", "listo", "mira", "escucha", "decime", "contame", "buenas", "buen", "dia", "buenos", "dias", "tardes",
            "y", "pero", "entonces", "ahi", "a", "ver", "vos", "exacto", "perfecto", "barbaro", "buenisimo", "ahora",
        };
        /// <summary>Después de estas, un nombre va sin coma («Hola Vale», «Gracias Bruno»).</summary>
        static readonly HashSet<string> Saludos = new HashSet<string>(StringComparer.Ordinal) { "hola", "buenas", "gracias", "chau", "dale", "perdon" };
        /// <summary>Apodos rioplatenses que NO son un prefijo del nombre (los que sí, como Vale o Flor, ya se reconocen solos).</summary>
        static readonly Dictionary<string, string> Apodos = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["coni"] = "constanza", ["cony"] = "constanza", ["maru"] = "maria", ["mary"] = "maria", ["pancho"] = "francisco",
            ["pepe"] = "jose", ["nacho"] = "ignacio", ["lucho"] = "luis", ["lu"] = "lucia", ["lucy"] = "lucia", ["vicky"] = "victoria",
            ["aldi"] = "aldana", ["mariafer"] = "mariafernanda", ["juanma"] = "juan", ["guille"] = "guillermo", ["cata"] = "catalina",
        };

        static readonly Regex RePalabra = new Regex(@"[\p{L}']+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex RePresentarse = new Regex(@"\b(?:[Ss]oy|[Hh]abla|[Ll]es habla|[Tt]e habla|[Mm]e llamo)\s+(?<n>\p{Lu}\p{Ll}+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex ReTercera = new Regex(@"\b(?:dijo|dec[ií]a|coment[oó]|mencion[oó]|seg[uú]n|explic[oó]|mostr[oó]|contaba|cont[oó]|pregunt[oó]|preguntaba|plante[oó]|planteaba)\s+(?<n>\p{Lu}\p{Ll}+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex RePorTitulo = new Regex(@"\bpor\s+(?<n>\p{Lu}\p{Ll}+)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        sealed class Persona
        {
            public string Crudo = "", Mostrar = "";
            public readonly List<KeyValuePair<string, double>> Alias = new List<KeyValuePair<string, double>>();
        }

        /// <summary>
        /// Las sugerencias para las voces que todavía no tienen nombre. Lo ya renombrado se respeta: esa voz y esa persona
        /// salen de la cuenta. O(turnos · personas) para juntar pistas + O(n³) del húngaro (n ≤ ~20).
        /// </summary>
        public static List<Sugerencia> Sugerir(Transcripcion t, Participantes gente, string reunion, IDictionary<string, string> nombres = null)
        {
            var res = new List<Sugerencia>();
            if (t == null || !t.ConVoces || gente == null || gente.Nombres.Count == 0) return res;
            nombres = nombres ?? new Dictionary<string, string>();
            bool hayYo = t.Voces.Any(v => v.EsYo);
            var usados = new HashSet<string>(nombres.Values.Select(Pliegue.Texto), StringComparer.Ordinal);
            // con «Yo» separado por tu micrófono, vos no sos ninguna de las otras voces; sin micrófono aparte, podés ser cualquiera
            var personas = gente.Nombres
                .Where(n => !(hayYo && Mismo(n, gente.Yo)))
                .Select(ArmarPersona)
                .Where(p => !usados.Contains(Pliegue.Texto(p.Mostrar)))
                .ToList();
            var voces = t.Voces.Where(v => !v.EsYo && !nombres.ContainsKey(v.Id)).ToList();
            if (personas.Count == 0 || voces.Count == 0) return res;

            var filaDe = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < voces.Count; i++) filaDe[voces[i].Id] = i;
            var puntaje = new double[voces.Count, personas.Count];
            var pistas = new List<Pista>();

            void Sumar(string voz, int persona, double peso, string porque, double seg)
            {
                if (voz == null || !filaDe.TryGetValue(voz, out int fila) || Math.Abs(peso) < 1e-6) return;
                puntaje[fila, persona] += peso;
                pistas.Add(new Pista { Voz = voz, Persona = personas[persona].Mostrar, Peso = peso, Porque = porque, Seg = seg });
            }

            var T = t.Turnos;
            for (int k = 0; k < T.Length; k++)
            {
                var tk = T[k];
                if (tk.Quien.Length == 0) continue;
                int sig = Vecino(T, k, +1), ant = Vecino(T, k, -1);
                string nombreVoz = t.VozDe(tk.Quien)?.Nombre ?? tk.Quien;

                // 1) darle la palabra: el nombre cierra el turno («…Sí, Vale.») → el que habla después es esa persona
                var fin = VocativoFinal(tk.Texto);
                if (fin != null)
                    foreach (var (p, w) in Quienes(fin.Item1, personas))
                    {
                        if (sig >= 0)
                        {
                            double demora = T[sig].Ini - tk.Fin;
                            string frag = Fragmento(tk.Texto, fin.Item2, true);
                            Sumar(T[sig].Quien, p, PesoDarPalabra * w * (demora <= SegundosDeRespuesta ? 1 : 0.5),
                                  $"{nombreVoz} dijo «{frag}» y enseguida habló esta voz ({Transcripcion.Reloj(T[sig].Ini)})", T[sig].Ini);
                        }
                        Sumar(tk.Quien, p, PesoNoSoyYo * w, "", tk.Ini);
                    }

                // 2) hablarle al empezar («María, ¿por qué…?») → contesta el siguiente, o le contestaba al anterior.
                //    Un turno cortito («Gracias, Vale.») empieza y termina con el MISMO nombre: cuenta una vez, como final.
                var ini = VocativoInicial(tk.Texto);
                if (ini != null && fin != null && ini.Item2 == fin.Item2) ini = null;
                if (ini != null)
                    foreach (var (p, w) in Quienes(ini.Item1, personas))
                    {
                        string frag = Fragmento(tk.Texto, ini.Item2, false);
                        if (sig >= 0) Sumar(T[sig].Quien, p, PesoResponder * w, $"{nombreVoz} arrancó con «{frag}» y contestó esta voz ({Transcripcion.Reloj(T[sig].Ini)})", T[sig].Ini);
                        if (ant >= 0) Sumar(T[ant].Quien, p, PesoResponder * 0.8 * w, $"{nombreVoz} le respondió con «{frag}» a esta voz ({Transcripcion.Reloj(tk.Ini)})", tk.Ini);
                        Sumar(tk.Quien, p, PesoNoSoyYo * w, "", tk.Ini);
                    }

                // 3) presentarse («soy Valentina», «les habla Bruno»)
                foreach (Match m in RePresentarse.Matches(tk.Texto))
                    foreach (var (p, w) in Quienes(Pliegue.Texto(m.Groups["n"].Value), personas))
                        Sumar(tk.Quien, p, PesoPresentarse * w, $"dijo «{Fragmento(tk.Texto, m.Index, false)}» ({Transcripcion.Reloj(tk.Ini)})", tk.Ini);

                // 4) nombrar a alguien en tercera persona («lo que decía Flor») → el que habla no es esa persona
                foreach (Match m in ReTercera.Matches(tk.Texto))
                    foreach (var (p, w) in Quienes(Pliegue.Texto(m.Groups["n"].Value), personas))
                        Sumar(tk.Quien, p, PesoTercera * w, "", tk.Ini);
            }

            // 5) «Analytics por Bruno»: la voz que más habló probablemente sea la de quien presenta
            var mt = RePorTitulo.Match(reunion ?? "");
            var principal = voces.OrderByDescending(v => v.Fraccion).FirstOrDefault();
            if (mt.Success && principal != null)
                foreach (var (p, w) in Quienes(Pliegue.Texto(mt.Groups["n"].Value), personas))
                    Sumar(principal.Id, p, PesoTitulo * w, $"la reunión se llama «{reunion}» y es la voz que más habló", 0);

            // --- el reparto: una persona por voz, el mayor puntaje total (húngaro sobre la matriz cuadrada con relleno)
            int n = Math.Max(voces.Count, personas.Count);
            var costo = new double[n, n];
            for (int i = 0; i < voces.Count; i++)
                for (int j = 0; j < personas.Count; j++)
                    costo[i, j] = -puntaje[i, j];
            int[] asignada = Hungaro.Asignar(costo);
            for (int i = 0; i < voces.Count; i++)
            {
                int j = asignada[i];
                if (j < 0 || j >= personas.Count) continue;
                double p = puntaje[i, j];
                double segunda = Enumerable.Range(0, personas.Count).Where(x => x != j).Select(x => puntaje[i, x]).DefaultIfEmpty(0).Max();
                if (p < Umbral || p - Math.Max(0, segunda) < Margen) continue;
                double positivo = Enumerable.Range(0, personas.Count).Sum(x => Math.Max(0, puntaje[i, x]));
                string voz = voces[i].Id, quien = personas[j].Mostrar;
                res.Add(new Sugerencia
                {
                    Voz = voz, Persona = quien, Puntaje = p,
                    Confianza = Math.Max(0, Math.Min(0.99, p / (positivo + 0.35))),
                    Pistas = pistas.Where(x => x.Voz == voz && x.Persona == quien && x.Porque.Length > 0).OrderByDescending(x => x.Peso).ToList(),
                });
            }
            return res.OrderByDescending(s => s.Confianza).ToList();
        }

        /// <summary>El turno más cercano en esa dirección que sea de OTRA voz (el que contesta, o al que se le contesta).</summary>
        static int Vecino(Intervencion[] T, int k, int paso)
        {
            for (int j = k + paso; j >= 0 && j < T.Length; j += paso)
                if (T[j].Quien.Length > 0 && !string.Equals(T[j].Quien, T[k].Quien, StringComparison.Ordinal)) return j;
            return -1;
        }

        /// <summary>«…explorar. Sí, Vale.» → («vale», posición). Un nombre (o dos, «María Sol») al final, precedido por coma.</summary>
        static Tuple<string, int> VocativoFinal(string s)
        {
            var palabras = RePalabra.Matches(s);
            if (palabras.Count == 0) return null;
            var ult = palabras[palabras.Count - 1];
            // después del nombre solo puede venir puntuación: «Vale.» sí, «Vale, ¿no?» no
            for (int i = ult.Index + ult.Length; i < s.Length; i++) if (char.IsLetterOrDigit(s[i])) return null;
            if (!char.IsUpper(ult.Value[0])) return null;
            var primera = ult;
            if (palabras.Count >= 2)
            {
                var pen = palabras[palabras.Count - 2];
                if (char.IsUpper(pen.Value[0]) && SoloEspacios(s, pen.Index + pen.Length, ult.Index) && AntesHayComa(s, pen.Index)) primera = pen;
            }
            if (!AntesHayComa(s, primera.Index)) return null;
            string nombre = primera == ult ? ult.Value : s.Substring(primera.Index, ult.Index + ult.Length - primera.Index);
            return Tuple.Create(Pliegue.Texto(nombre), primera.Index);
        }

        /// <summary>«Sí, María, ¿por qué…» → («maria», posición). Salteando muletillas, un nombre seguido de coma o de «?», «!», «:».</summary>
        static Tuple<string, int> VocativoInicial(string s)
        {
            var palabras = RePalabra.Matches(s);
            string anterior = "";
            for (int i = 0; i < palabras.Count && i < 5; i++)
            {
                var w = palabras[i];
                string pl = Pliegue.Texto(w.Value);
                if (Muletillas.Contains(pl)) { anterior = pl; continue; }      // «Sí, …», «Bueno, …»: todavía no nombró a nadie
                if (!char.IsUpper(w.Value[0])) return null;
                char despues = SiguienteNoEspacio(s, w.Index + w.Length);
                bool marcado = despues == ',' || despues == '?' || despues == '!' || despues == ':';
                return marcado || Saludos.Contains(anterior) ? Tuple.Create(pl, w.Index) : null;
            }
            return null;
        }

        static bool AntesHayComa(string s, int indice)
        {
            for (int i = indice - 1; i >= 0; i--)
            {
                if (s[i] == ' ') continue;
                return s[i] == ',';
            }
            return false;
        }

        static bool SoloEspacios(string s, int a, int b)
        {
            for (int i = a; i < b; i++) if (s[i] != ' ') return false;
            return true;
        }

        static char SiguienteNoEspacio(string s, int i)
        {
            for (; i < s.Length; i++) if (s[i] != ' ') return s[i];
            return '\0';
        }

        /// <summary>El pedacito de texto que sirve de evidencia: la oración donde aparece el nombre, recortada.</summary>
        static string Fragmento(string s, int indice, bool alFinal)
        {
            if (alFinal)
            {
                int ini = indice;
                for (int i = indice - 1; i >= 0 && indice - i < LargoFragmento; i--)
                {
                    ini = i;
                    if (i > 0 && (s[i - 1] == '.' || s[i - 1] == '?' || s[i - 1] == '!')) break;
                }
                string f = s.Substring(ini).Trim();
                return (ini > 0 && !(s[ini - 1] == '.' || s[ini - 1] == '?' || s[ini - 1] == '!' || s[ini - 1] == ' ') ? "…" : "") + f;
            }
            int fin = Math.Min(s.Length, indice + LargoFragmento);
            int corte = s.IndexOfAny(new[] { '.', '?', '!' }, indice);
            if (corte >= 0 && corte < fin) fin = corte + 1;
            int desde = Math.Max(0, indice - 12);
            return (desde > 0 ? "…" : "") + s.Substring(desde, fin - desde).Trim() + (fin < s.Length && corte != fin - 1 ? "…" : "");
        }

        static Persona ArmarPersona(string crudo)
        {
            var p = new Persona { Crudo = crudo, Mostrar = Participantes.Mostrar(crudo) };
            int c = crudo.IndexOf(',');
            string apellido = c > 0 ? crudo.Substring(0, c) : "", nombres = c > 0 ? crudo.Substring(c + 1) : crudo;
            var dados = nombres.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(Pliegue.Texto).Where(x => x.Length >= 2).ToList();
            foreach (var d in dados) p.Alias.Add(new KeyValuePair<string, double>(d, 1.0));
            if (dados.Count > 1) p.Alias.Add(new KeyValuePair<string, double>(string.Join(" ", dados), 1.0));
            foreach (var a in apellido.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(Pliegue.Texto).Where(x => x.Length >= 3))
                p.Alias.Add(new KeyValuePair<string, double>(a, 0.8));
            foreach (var kv in Apodos)
                if (dados.Contains(kv.Value)) p.Alias.Add(new KeyValuePair<string, double>(kv.Key, 0.9));
            return p;
        }

        /// <summary>
        /// A quién puede referirse una palabra, y con cuánto peso. Exacto vale su factor; «Vale» → «Valentina» (el apodo es el
        /// comienzo del nombre, recortado) vale 0,9; y nada más: ante la duda, no suma. Si hay varias personas posibles, la
        /// evidencia se REPARTE (peso² / suma): una palabra ambigua no puede valer lo mismo que una clara. O(personas · alias).
        /// </summary>
        static List<(int persona, double peso)> Quienes(string palabra, List<Persona> personas)
        {
            var res = new List<(int, double)>();
            if (string.IsNullOrEmpty(palabra) || palabra.Length < LargoMinimo && !Apodos.ContainsKey(palabra)) return res;
            double suma = 0;
            var mejores = new double[personas.Count];
            for (int i = 0; i < personas.Count; i++)
            {
                double mejor = 0;
                foreach (var kv in personas[i].Alias)
                {
                    string a = kv.Key;
                    // un apodo es un nombre RECORTADO (Vale, Flor, Rami, Sofi: dos letras o más de menos). Una letra de
                    // diferencia, o una palabra que CONTIENE al nombre, es OTRO nombre: Julián no es Julia ni Juliana
                    // (medido con una reunión real: esa regla le ponía el nombre equivocado a quien presentaba)
                    double f = a == palabra ? kv.Value
                             : palabra.Length >= LargoMinimo && a.Length - palabra.Length >= 2 && a.StartsWith(palabra, StringComparison.Ordinal) ? kv.Value * 0.9
                             : 0;
                    if (f > mejor) mejor = f;
                }
                mejores[i] = mejor;
                suma += mejor;
            }
            if (suma <= 0) return res;
            for (int i = 0; i < personas.Count; i++)
                if (mejores[i] > 0) res.Add((i, mejores[i] * mejores[i] / suma));
            return res;
        }

        static bool Mismo(string a, string b) => string.Equals(Pliegue.Texto((a ?? "").Trim()), Pliegue.Texto((b ?? "").Trim()), StringComparison.Ordinal);
    }

    /// <summary>
    /// Asignación óptima (algoritmo húngaro de Kuhn-Munkres con potenciales, O(n²·m)): a cada fila una columna
    /// DISTINTA con el costo total mínimo. Para maximizar un puntaje, pasarle el puntaje negado.
    /// </summary>
    internal static class Hungaro
    {
        /// <summary>Devuelve la columna asignada a cada fila. Exige filas ≤ columnas (rellenar con ceros si hace falta).</summary>
        public static int[] Asignar(double[,] costo)
        {
            int n = costo.GetLength(0), m = costo.GetLength(1);
            if (n > m) throw new ArgumentException($"el húngaro necesita filas ≤ columnas ({n} > {m}): rellená columnas con ceros");
            var u = new double[n + 1];
            var v = new double[m + 1];
            var p = new int[m + 1];          // p[j] = fila asignada a la columna j (1-based; 0 = libre)
            var camino = new int[m + 1];
            for (int i = 1; i <= n; i++)
            {
                p[0] = i;
                int j0 = 0;
                var minv = new double[m + 1];
                for (int j = 0; j <= m; j++) minv[j] = double.PositiveInfinity;
                var usado = new bool[m + 1];
                do
                {
                    usado[j0] = true;
                    int i0 = p[j0], j1 = 0;
                    double delta = double.PositiveInfinity;
                    for (int j = 1; j <= m; j++)
                    {
                        if (usado[j]) continue;
                        double cur = costo[i0 - 1, j - 1] - u[i0] - v[j];
                        if (cur < minv[j]) { minv[j] = cur; camino[j] = j0; }
                        if (minv[j] < delta) { delta = minv[j]; j1 = j; }
                    }
                    for (int j = 0; j <= m; j++)
                    {
                        if (usado[j]) { u[p[j]] += delta; v[j] -= delta; }
                        else minv[j] -= delta;
                    }
                    j0 = j1;
                } while (p[j0] != 0);
                do { int j1 = camino[j0]; p[j0] = p[j1]; j0 = j1; } while (j0 != 0);
            }
            var res = new int[n];
            for (int i = 0; i < n; i++) res[i] = -1;
            for (int j = 1; j <= m; j++) if (p[j] != 0) res[p[j] - 1] = j - 1;
            return res;
        }
    }
}
