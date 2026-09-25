using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace TeamsTools
{
    // =====================================================================================================
    // TRANSCRIPCIÓN — la reunión como CONVERSACIÓN: quién dijo qué, cuándo y cuánto. Todo puro: sin UI ni hilos.
    //
    // 🚨 25-sep-2026 · «aparece persona 1 y persona x todo pegado»: el texto con hablantes («[00:01:12] Persona 2: …»)
    //    viaja con saltos \n, y el TextBox de Windows solo corta renglón con \r\n: la reunión entera se veía como UN
    //    bloque. La cura de fondo no es cambiar los saltos: la transcripción deja de ser un string y pasa a ser turnos.
    //
    //    Fuentes, de la más rica a la más pobre (gana la primera que sirva):
    //      1. audio16.json   segmentos con inicio, fin, texto y hablante → tiempos exactos y MARCAS por segmento
    //                        (el karaoke interpola DENTRO de cada segmento de ≤ 20 s, no a lo largo del turno entero)
    //      2. el texto del índice con «[hh:mm:ss] Quién: …» → el fin de un turno es el inicio del siguiente
    //      3. texto plano (whisper viejo, sin tiempos) → párrafos cortados en fin de oración
    // =====================================================================================================

    /// <summary>Un ancla texto↔audio: el carácter <see cref="Car"/> del turno suena en el segundo <see cref="Seg"/>.</summary>
    internal readonly struct Marca
    {
        public readonly int Car;
        public readonly double Seg;
        public Marca(int car, double seg) { Car = car; Seg = seg; }
    }

    /// <summary>Lo que dijo una voz de corrido. Se arma una vez y no se toca más.</summary>
    internal sealed class Intervencion
    {
        public int Indice;
        public double Ini, Fin;
        /// <summary>Id ESTABLE de la voz («Persona 3», «Yo»), o "" si la reunión no separó voces.</summary>
        public string Quien = "";
        /// <summary>El texto; los párrafos van separados por '\n'.</summary>
        public string Texto = "";
        /// <summary>El texto en minúsculas y sin tildes (la ñ se queda): MISMO largo que <see cref="Texto"/>, para buscar.</summary>
        public string Plegado = "";
        /// <summary>Anclas ordenadas (Car y Seg no decrecen). Vacío si no hay tiempos.</summary>
        public Marca[] Marcas = new Marca[0];
        public int Palabras;
        public double Dura => Math.Max(0, Fin - Ini);

        /// <summary>El carácter que sonaba en el segundo <paramref name="seg"/>, interpolado entre las dos anclas vecinas. O(log m).</summary>
        public int CaracterEn(double seg)
        {
            var m = Marcas;
            if (m.Length == 0) return 0;
            if (seg <= m[0].Seg) return m[0].Car;
            if (seg >= m[m.Length - 1].Seg) return m[m.Length - 1].Car;
            int lo = 0, hi = m.Length - 1;                // invariante: m[lo].Seg <= seg < m[hi].Seg
            while (hi - lo > 1) { int med = (lo + hi) >> 1; if (m[med].Seg <= seg) lo = med; else hi = med; }
            double tramo = m[hi].Seg - m[lo].Seg;
            double f = tramo > 1e-9 ? (seg - m[lo].Seg) / tramo : 0;
            return m[lo].Car + (int)Math.Round(f * (m[hi].Car - m[lo].Car));
        }

        /// <summary>El segundo en que sonó el carácter <paramref name="car"/> (la inversa de <see cref="CaracterEn"/>). O(log m).</summary>
        public double SegundoEn(int car)
        {
            var m = Marcas;
            if (m.Length == 0) return Ini;
            if (car <= m[0].Car) return m[0].Seg;
            if (car >= m[m.Length - 1].Car) return m[m.Length - 1].Seg;
            int lo = 0, hi = m.Length - 1;                // invariante: m[lo].Car <= car < m[hi].Car
            while (hi - lo > 1) { int med = (lo + hi) >> 1; if (m[med].Car <= car) lo = med; else hi = med; }
            int tramo = m[hi].Car - m[lo].Car;
            double f = tramo > 0 ? (car - m[lo].Car) / (double)tramo : 0;
            return m[lo].Seg + f * (m[hi].Seg - m[lo].Seg);
        }
    }

    /// <summary>Una voz de la reunión y todo lo que habló.</summary>
    internal sealed class Voz
    {
        public string Id = "";
        /// <summary>Lo que se muestra: el nombre que le pusiste, o el Id.</summary>
        public string Nombre = "";
        /// <summary>«Persona 12» → 12 · «Yo» → 0. Define el color: la misma voz tiene el mismo color en toda la app.</summary>
        public int Numero;
        public bool EsYo;
        public Color Color;
        public int Turnos, Palabras;
        public double Segundos, Fraccion, Primera = double.MaxValue, Ultima;
        public bool Renombrada => !string.Equals(Nombre, Id, StringComparison.Ordinal);

        /// <summary>Dos letras para el avatar: «Persona 12» → «P12», «Yo» → «YO», «Valentina Rivas» → «VR».</summary>
        public string Iniciales
        {
            get
            {
                if (EsYo) return "YO";
                if (!Renombrada && Numero > 0) return "P" + Numero;
                var partes = Nombre.Split(new[] { ' ', ',', '.' }, StringSplitOptions.RemoveEmptyEntries);
                if (partes.Length == 0) return "?";
                if (partes.Length == 1) return partes[0].Substring(0, Math.Min(2, partes[0].Length)).ToUpperInvariant();
                return (partes[0].Substring(0, 1) + partes[partes.Length - 1].Substring(0, 1)).ToUpperInvariant();
            }
        }
    }

    /// <summary>Un lugar donde apareció lo que buscaste.</summary>
    internal readonly struct Aparicion
    {
        public readonly int Turno, Car, Largo;
        public Aparicion(int turno, int car, int largo) { Turno = turno; Car = car; Largo = largo; }
    }

    internal sealed class Transcripcion
    {
        /// <summary>Un párrafo se corta en el primer fin de oración después de esto: un bloque de 1 500 caracteres no se lee.</summary>
        public const int CaracteresPorParrafo = 420;
        /// <summary>Un silencio así de la MISMA voz abre párrafo nuevo (retomó después de pensar, de mostrar algo…).</summary>
        public const double PausaDeParrafo = 3.0;
        /// <summary>Sin voces separadas, un silencio así abre un bloque nuevo (con su hora).</summary>
        public const double PausaDeBloque = 2.2;
        /// <summary>Cuando no hay duración ni tiempos: habla rioplatense a ~2,6 palabras por segundo.</summary>
        const double PalabrasPorSegundo = 2.6;

        static readonly Regex ReLinea = new Regex(@"^\[(\d{1,2}):(\d{2}):(\d{2})\]\s+([^:\[\]\r\n]{1,48}?):\s?(.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex ReNumero = new Regex(@"(\d+)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly char[] Blancos = { ' ', '\n', '\r', '\t' };

        public Intervencion[] Turnos = new Intervencion[0];
        /// <summary>De la que más habló a la que menos.</summary>
        public Voz[] Voces = new Voz[0];
        public double Duracion;
        public bool ConVoces, ConTiempos;
        public string Fuente = "";
        public int Palabras;
        readonly Dictionary<string, Voz> porId = new Dictionary<string, Voz>(StringComparer.Ordinal);

        public static readonly Transcripcion Vacia = new Transcripcion();
        public bool EsVacia => Turnos.Length == 0;

        public Voz VozDe(string id) => id != null && porId.TryGetValue(id, out var v) ? v : null;

        /// <summary>El último turno que empezó en o antes de <paramref name="seg"/>; −1 si todavía no habló nadie. O(log n).</summary>
        public int TurnoEn(double seg)
        {
            int lo = 0, hi = Turnos.Length - 1, res = -1;
            while (lo <= hi)
            {
                int med = (lo + hi) >> 1;
                if (Turnos[med].Ini <= seg) { res = med; lo = med + 1; } else hi = med - 1;
            }
            return res;
        }

        /// <summary>El turno que está SONANDO en <paramref name="seg"/> (dentro de su [ini, fin] con un respiro), o −1 en un silencio.</summary>
        public int TurnoSonando(double seg, double respiro = 0.6)
        {
            int k = TurnoEn(seg);
            return k >= 0 && seg <= Turnos[k].Fin + respiro ? k : -1;
        }

        /// <summary>Todas las apariciones de <paramref name="consulta"/> (sin tildes ni mayúsculas, la ñ aparte). O(caracteres totales).</summary>
        public List<Aparicion> Buscar(string consulta)
        {
            var res = new List<Aparicion>();
            string q = Pliegue.Texto((consulta ?? "").Trim());
            if (q.Length == 0) return res;
            foreach (var t in Turnos)
                for (int i = t.Plegado.IndexOf(q, StringComparison.Ordinal); i >= 0; i = t.Plegado.IndexOf(q, i + q.Length, StringComparison.Ordinal))
                    res.Add(new Aparicion(t.Indice, i, q.Length));
            return res;
        }

        /// <summary>Pone los nombres que elegiste («Persona 3» → «Valentina Rivas»). Un nombre vacío vuelve al Id.</summary>
        public void AplicarNombres(IDictionary<string, string> nombres)
        {
            foreach (var v in Voces)
                v.Nombre = nombres != null && nombres.TryGetValue(v.Id, out var n) && !string.IsNullOrWhiteSpace(n) && !v.EsYo ? n.Trim() : v.Id;
        }

        /// <summary>
        /// La transcripción para pegar en otro lado: un turno por párrafo con su hora y el nombre de la voz, y una línea
        /// en blanco entre turnos. Con \r\n: el que la pegue en un TextBox, Outlook o el Bloc de notas la ve bien. O(n).
        /// </summary>
        public string ComoTexto(int desde = 0)
        {
            var sb = new StringBuilder();
            for (int i = Math.Max(0, desde); i < Turnos.Length; i++)
            {
                var t = Turnos[i];
                if (sb.Length > 0) sb.Append("\r\n\r\n");
                if (ConTiempos) sb.Append('[').Append(Reloj(t.Ini, true)).Append("] ");
                var v = VozDe(t.Quien);
                if (v != null) sb.Append(v.Nombre).Append(": ");
                sb.Append(t.Texto.Replace("\n", "\r\n"));
            }
            return sb.ToString();
        }

        /// <summary>«1:02:03» / «02:03», o «01:02:03» / «00:02:03» con <paramref name="siempreHoras"/>.</summary>
        public static string Reloj(double seg, bool siempreHoras = false)
        {
            if (double.IsNaN(seg) || seg < 0) seg = 0;
            var t = TimeSpan.FromSeconds(Math.Floor(seg));
            if (siempreHoras) return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
        }

        // ================================================================== armado

        /// <summary>
        /// Desde los segmentos de audio16.json. Los segmentos seguidos de la MISMA voz son un turno (la misma regla que
        /// usa el grabador para audio16.hablantes.txt, así los dos cuentan igual); sin voces, un silencio largo o un
        /// bloque ya largo abren otro. Cada segmento deja dos anclas (su inicio y su fin). O(segmentos).
        /// </summary>
        public static Transcripcion DesdeSegmentos(IList<Dictionary<string, object>> segmentos, double duracion)
        {
            var turnos = new List<Intervencion>();
            if (segmentos == null) return Armar(turnos, duracion, true, "audio16.json");
            Intervencion actual = null;
            var sb = new StringBuilder();
            var marcas = new List<Marca>();
            double finAnterior = 0, ultimaMarca = 0;

            void Cerrar()
            {
                if (actual == null) return;
                actual.Texto = Parrafear(sb.ToString());      // mismo largo: las anclas siguen valiendo
                actual.Marcas = marcas.ToArray();
                actual.Fin = Math.Max(actual.Ini, finAnterior);
                turnos.Add(actual);
                actual = null;
            }

            foreach (var s in segmentos)
            {
                string texto = Normalizar(Json.S(s, "text"));
                if (texto.Length == 0) continue;
                double ini = Numero(s, "start"), fin = Numero(s, "end");
                if (double.IsNaN(ini) || ini < 0) ini = finAnterior;
                if (double.IsNaN(fin) || fin < ini) fin = ini;
                string quien = Json.S(s, "speaker").Trim();
                bool nuevo = actual == null || !string.Equals(quien, actual.Quien, StringComparison.Ordinal)
                             || (quien.Length == 0 && (ini - finAnterior >= PausaDeBloque || sb.Length >= CaracteresPorParrafo));
                if (nuevo)
                {
                    Cerrar();
                    actual = new Intervencion { Ini = ini, Quien = quien };
                    sb.Clear(); marcas.Clear();
                }
                else sb.Append(ini - finAnterior >= PausaDeParrafo ? '\n' : ' ');
                // las anclas nunca retroceden: un segmento que arranca antes de que termine el anterior se corre
                ultimaMarca = Math.Max(ultimaMarca, ini);
                marcas.Add(new Marca(sb.Length, ultimaMarca));
                sb.Append(texto);
                ultimaMarca = Math.Max(ultimaMarca, fin);
                marcas.Add(new Marca(sb.Length, ultimaMarca));
                finAnterior = Math.Max(finAnterior, fin);
            }
            Cerrar();
            return Armar(turnos, duracion, true, "audio16.json");
        }

        /// <summary>
        /// Desde el texto del índice. Si la mayoría de los renglones son «[hh:mm:ss] Quién: …», cada uno es un turno y
        /// termina donde empieza el siguiente; si no, es texto plano y se corta en párrafos sin tiempos. O(n).
        /// </summary>
        public static Transcripcion DesdeTexto(string texto, double duracion)
        {
            texto = (texto ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            var renglones = texto.Split('\n');
            int llenos = renglones.Count(r => r.Trim().Length > 0);
            int conHora = renglones.Count(r => ReLinea.IsMatch(r.Trim()));
            var turnos = new List<Intervencion>();
            if (llenos > 0 && conHora >= Math.Max(1, llenos / 2))
            {
                foreach (var crudo in renglones)
                {
                    string r = crudo.Trim();
                    if (r.Length == 0) continue;
                    var m = ReLinea.Match(r);
                    if (m.Success)
                    {
                        double ini = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                                   + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                                   + int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                        turnos.Add(new Intervencion { Ini = ini, Quien = m.Groups[4].Value.Trim(), Texto = Normalizar(m.Groups[5].Value) });
                    }
                    else if (turnos.Count > 0) turnos[turnos.Count - 1].Texto += "\n" + Normalizar(r);   // renglón suelto: sigue el turno anterior
                }
                turnos.RemoveAll(t => t.Texto.Length == 0);
                for (int i = 0; i < turnos.Count; i++)
                {
                    var t = turnos[i];
                    double estimado = t.Ini + ContarPalabras(t.Texto) / PalabrasPorSegundo;
                    t.Fin = i + 1 < turnos.Count ? Math.Max(t.Ini, turnos[i + 1].Ini) : duracion > t.Ini ? duracion : estimado;
                    t.Texto = Parrafear(t.Texto);
                    t.Marcas = new[] { new Marca(0, t.Ini), new Marca(t.Texto.Length, t.Fin) };
                }
                return Armar(turnos, duracion, true, "texto con hablantes");
            }
            // texto plano: bloques de párrafo, sin hora (whisper viejo guardaba todo en un renglón)
            string plano = Parrafear(Normalizar(texto.Replace('\n', ' ')));
            foreach (var p in plano.Split('\n'))
                if (p.Trim().Length > 0) turnos.Add(new Intervencion { Texto = p.Trim() });
            return Armar(turnos, duracion, false, "texto");
        }

        /// <summary>Numera, pliega, cuenta y arma las voces con sus números y colores. O(caracteres).</summary>
        static Transcripcion Armar(List<Intervencion> turnos, double duracion, bool conTiempos, string fuente)
        {
            var tr = new Transcripcion { Fuente = fuente, ConTiempos = conTiempos && turnos.Any(t => t.Fin > 0) };
            for (int i = 0; i < turnos.Count; i++)
            {
                var t = turnos[i];
                t.Indice = i;
                t.Plegado = Pliegue.Texto(t.Texto);
                t.Palabras = ContarPalabras(t.Texto);
                tr.Palabras += t.Palabras;
                if (t.Quien.Length == 0) continue;
                if (!tr.porId.TryGetValue(t.Quien, out var v))
                {
                    bool yo = string.Equals(t.Quien, "Yo", StringComparison.OrdinalIgnoreCase);
                    var mn = ReNumero.Match(t.Quien);
                    v = new Voz { Id = t.Quien, Nombre = t.Quien, EsYo = yo, Numero = yo ? 0 : mn.Success ? int.Parse(mn.Groups[1].Value, CultureInfo.InvariantCulture) : 100 + Math.Abs(t.Quien.GetHashCode() % 50) };
                    tr.porId[t.Quien] = v;
                }
                v.Turnos++;
                v.Palabras += t.Palabras;
                v.Segundos += t.Dura;
                v.Primera = Math.Min(v.Primera, t.Ini);
                v.Ultima = Math.Max(v.Ultima, t.Fin);
            }
            tr.Turnos = turnos.ToArray();
            tr.ConVoces = tr.porId.Count > 0;
            // la proporción va por TIEMPO hablado si lo hay; si no, por palabras (texto sin horas)
            double total = tr.ConTiempos ? tr.porId.Values.Sum(v => v.Segundos) : tr.porId.Values.Sum(v => v.Palabras);
            foreach (var v in tr.porId.Values)
            {
                v.Fraccion = total > 0 ? (tr.ConTiempos ? v.Segundos : v.Palabras) / total : 0;
                v.Color = PaletaVoces.De(v);
            }
            tr.Voces = tr.porId.Values.OrderByDescending(v => v.Fraccion).ThenBy(v => v.Numero).ToArray();
            double ultimo = tr.Turnos.Length > 0 ? tr.Turnos.Max(t => t.Fin) : 0;
            tr.Duracion = duracion > 0 && !double.IsNaN(duracion) ? Math.Max(duracion, ultimo) : ultimo;
            return tr;
        }

        /// <summary>
        /// Corta párrafos largos en el primer fin de oración después de <see cref="CaracteresPorParrafo"/>: cambia el
        /// espacio por '\n', así el largo NO cambia y las anclas siguen apuntando al mismo carácter. O(n).
        /// </summary>
        public static string Parrafear(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= CaracteresPorParrafo) return s ?? "";
            var a = s.ToCharArray();
            int desde = 0;
            for (int i = 1; i < a.Length - 1; i++)
            {
                if (a[i] == '\n') { desde = i + 1; continue; }
                if (a[i] == ' ' && i - desde >= CaracteresPorParrafo && EsFinDeOracion(a[i - 1]) && !char.IsLower(a[i + 1]))
                {
                    a[i] = '\n';
                    desde = i + 1;
                }
            }
            return new string(a);
        }

        static bool EsFinDeOracion(char c) => c == '.' || c == '?' || c == '!' || c == '…';

        /// <summary>Espacios colapsados, sin bordes, sin \r ni tabulaciones. Los '\n' (párrafos) se respetan.</summary>
        static string Normalizar(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            bool espacio = false;
            foreach (char c0 in s)
            {
                char c = c0 == '\t' || c0 == '\r' || c0 == ' ' ? ' ' : c0;
                if (c == ' ') { espacio = sb.Length > 0 && sb[sb.Length - 1] != '\n'; continue; }
                if (c == '\n') { while (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--; if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n'); espacio = false; continue; }
                if (espacio) { sb.Append(' '); espacio = false; }
                sb.Append(c);
            }
            while (sb.Length > 0 && (sb[sb.Length - 1] == '\n' || sb[sb.Length - 1] == ' ')) sb.Length--;
            return sb.ToString();
        }

        public static int ContarPalabras(string s) => string.IsNullOrEmpty(s) ? 0 : s.Split(Blancos, StringSplitOptions.RemoveEmptyEntries).Length;

        static double Numero(Dictionary<string, object> d, string k)
        {
            try { return d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : double.NaN; }
            catch (FormatException) { return double.NaN; }
            catch (InvalidCastException) { return double.NaN; }
        }
    }

    /// <summary>
    /// Plegado para buscar: minúsculas y sin tildes, pero la ñ se queda (año ≠ ano). Carácter a carácter con una
    /// tabla armada una vez (Latin-1 + Latin extendido): el plegado tiene el MISMO largo que el original, así que un
    /// hallazgo en el plegado apunta al mismo carácter del texto. O(n) sin asignaciones por carácter.
    /// </summary>
    internal static class Pliegue
    {
        static readonly char[] tabla = Armar();

        static char[] Armar()
        {
            var t = new char[0x250];
            for (int c = 0; c < t.Length; c++)
            {
                char ch = (char)c;
                if (ch == 'ñ' || ch == 'Ñ') { t[c] = 'ñ'; continue; }
                if (ch == '\n' || ch == '\r' || ch == '\t') { t[c] = ' '; continue; }   // un párrafo no corta una búsqueda
                string d = ch.ToString().Normalize(NormalizationForm.FormD);
                char b = d.Length > 0 && char.IsLetter(d[0]) ? d[0] : ch;
                t[c] = char.ToLowerInvariant(b);
            }
            return t;
        }

        public static char Char(char c) => c < tabla.Length ? tabla[c] : char.ToLowerInvariant(c);

        public static string Texto(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var a = s.ToCharArray();
            for (int i = 0; i < a.Length; i++) a[i] = Char(a[i]);
            return new string(a);
        }
    }

    /// <summary>
    /// El color de cada voz. «Yo» es malva (el mismo de «TU MIC» en la banda en vivo); las siete primeras usan los
    /// pasteles curados del tema en un orden que separa bien a las que más hablan; de ahí en más, tonos repartidos por
    /// el ÁNGULO DORADO (137,5°): por muchas voces que haya, dos números seguidos nunca caen en tonos parecidos.
    /// </summary>
    internal static class PaletaVoces
    {
        static readonly Color[] Curadas = { Tema.Cyan, Tema.Durazno, Tema.Cielo, Tema.Rosa, Tema.Salvia, Tema.Crema, Tema.Rojo };
        const double AnguloDorado = 137.50776405;

        public static Color De(Voz v)
        {
            if (v.EsYo) return Tema.Malva;
            if (v.Numero >= 1 && v.Numero <= Curadas.Length) return Curadas[v.Numero - 1];
            return Hsl((v.Numero * AnguloDorado + 30) % 360, 0.62, 0.80);
        }

        static Color Hsl(double h, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; } else if (h < 120) { r = x; g = c; b = 0; } else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; } else if (h < 300) { r = x; g = 0; b = c; } else { r = c; g = 0; b = x; }
            int K(double v) => Math.Max(0, Math.Min(255, (int)Math.Round((v + m) * 255)));
            return Color.FromArgb(K(r), K(g), K(b));
        }
    }

    /// <summary>
    /// Los nombres que le pusiste a las voces de UNA grabación: `nombres.json` en su carpeta, {"Persona 3": "Valentina Rivas"}.
    /// Se escribe con <see cref="Disco"/> (atómico y de fondo): renombrar nunca traba la pantalla.
    /// </summary>
    internal static class NombresDeVoces
    {
        public const string Archivo = "nombres.json";
        static readonly Regex RePersona = new Regex(@"\bPersona (\d{1,3})\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static Dictionary<string, string> Leer(string carpeta)
        {
            var res = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(carpeta)) return res;
            var d = Json.LeerObjeto(Path.Combine(carpeta, Archivo));
            if (d == null) return res;
            foreach (var kv in d)
                if (kv.Value is string s && s.Trim().Length > 0 && !kv.Key.StartsWith("_", StringComparison.Ordinal)) res[kv.Key] = s.Trim();
            return res;
        }

        public static void Guardar(string carpeta, IDictionary<string, string> nombres)
        {
            if (string.IsNullOrEmpty(carpeta) || !Directory.Exists(carpeta)) return;
            var d = new Dictionary<string, object> { ["_ayuda"] = "Quién es cada voz de esta grabación. Lo escribe TeamsTools al renombrar desde la transcripción." };
            foreach (var kv in nombres.OrderBy(k => k.Key, StringComparer.Ordinal)) d[kv.Key] = kv.Value;
            Json.Escribir(Path.Combine(carpeta, Archivo), d);
        }

        /// <summary>
        /// «Persona 3» → su nombre en cualquier texto (el resumen, una copia). Por TOKEN y con regex de palabra entera:
        /// «Persona 1» no pisa el comienzo de «Persona 12». «Yo» no se toca: en el texto libre es una palabra más. O(n).
        /// </summary>
        public static string Aplicar(string texto, IDictionary<string, string> nombres)
        {
            if (string.IsNullOrEmpty(texto) || nombres == null || nombres.Count == 0) return texto ?? "";
            return RePersona.Replace(texto, m => nombres.TryGetValue(m.Value, out var n) && n.Length > 0 ? n : m.Value);
        }
    }

    /// <summary>
    /// Quiénes estaban en la reunión, según el panel de Teams que el vigía anota una vez por minuto en
    /// corrillos.jsonl ({"hora", "reunion", "yo", "gente": "Apellido, Nombre | …"}). Es la lista de la que salen los
    /// nombres para ponerle a las voces. O(renglones del archivo).
    /// </summary>
    internal sealed class Participantes
    {
        public string Yo = "";
        /// <summary>«Apellido, Nombre», de los que más minutos estuvieron a los que menos.</summary>
        public List<string> Nombres = new List<string>();
        const double MargenMinutos = 3;

        public static Participantes Leer(string rutaCorrillos, DateTime desde, DateTime hasta)
        {
            var p = new Participantes();
            if (string.IsNullOrEmpty(rutaCorrillos) || !File.Exists(rutaCorrillos) || desde.Year < 2000) return p;
            if (hasta.Year < 2000 || hasta < desde) hasta = DateTime.Now;
            DateTime a = desde.AddMinutes(-MargenMinutos), b = hasta.AddMinutes(MargenMinutos);
            var minutos = new Dictionary<string, int>(StringComparer.Ordinal);
            var js = new JavaScriptSerializer();
            string[] renglones;
            using (var fs = new FileStream(rutaCorrillos, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
                renglones = sr.ReadToEnd().Split('\n');
            foreach (var r in renglones)
            {
                if (r.Trim().Length < 10) continue;
                Dictionary<string, object> o;
                try { o = js.Deserialize<Dictionary<string, object>>(r); }
                catch (ArgumentException) { continue; }        // un renglón a medio escribir no tira la lista entera
                catch (InvalidOperationException) { continue; }
                if (!DateTime.TryParseExact(Json.S(o, "hora"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var h) || h < a || h > b) continue;
                string yo = Json.S(o, "yo");
                if (yo.Length > 0) p.Yo = yo;
                foreach (var n in Json.S(o, "gente").Split('|'))
                {
                    string nombre = n.Trim();
                    if (nombre.Length == 0) continue;
                    minutos.TryGetValue(nombre, out int k);
                    minutos[nombre] = k + 1;
                }
            }
            p.Nombres = minutos.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key).ToList();
            return p;
        }

        /// <summary>«Rivas, Valentina» → «Valentina Rivas»: como se nombra a alguien en una conversación.</summary>
        public static string Mostrar(string apellidoNombre)
        {
            string s = (apellidoNombre ?? "").Trim();
            int c = s.IndexOf(',');
            return c > 0 ? (s.Substring(c + 1).Trim() + " " + s.Substring(0, c).Trim()).Trim() : s;
        }
    }
}
