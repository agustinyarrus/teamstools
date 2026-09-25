using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TeamsTools
{
    /// <summary>
    /// El portero de lo que escribe el modelo local. Nada de lo que genere la IA se manda sin pasar por acá.
    ///
    /// La razón es simple: los micromodelos son deterministas y no inventan; un LLM sí. En un autocontestador el
    /// error caro no es contestar mal, es contestar algo que PARECE un dato — un número de ticket, una hora, un
    /// enlace — que nadie dijo nunca. Después alguien lo lee como si fuera cierto.
    /// Así que la regla es: la IA puede aportar REDACCIÓN, no datos. Si aparece un dato que no estaba en el mensaje
    /// entrante, la respuesta se descarta y se usa el texto fijo de la regla.
    ///
    /// Todo esto es texto puro: se puede probar entero sin tener ningún modelo levantado.
    /// </summary>
    internal static class RespuestaIA
    {
        internal sealed class Veredicto
        {
            public bool Sirve;
            public string Motivo = "";
            public string Texto = "";
            public override string ToString() => Sirve ? "sirve" : "descartada: " + Motivo;
        }

        static readonly Regex ReTicket = new Regex(@"\b([A-Z]{2,8}\d{0,3})[- ]?(\d{2,6})\b", RegexOptions.Compiled);
        static readonly Regex ReHora = new Regex(@"\b([01]?\d|2[0-3])[:.]([0-5]\d)\b", RegexOptions.Compiled);
        static readonly Regex ReFecha = new Regex(@"\b\d{1,2}\s*[/\-]\s*\d{1,2}(\s*[/\-]\s*\d{2,4})?\b", RegexOptions.Compiled);
        static readonly Regex ReEnlace = new Regex(@"(https?://|www\.)\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex RePensando = new Regex(@"</?think|^\s*(thinking|razonamiento)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ReEspacios = new Regex(@"\s+", RegexOptions.Compiled);

        /// <summary>Días de la semana y palabras de tiempo que también cuentan como "prometer una fecha".</summary>
        static readonly string[] Cuando =
        {
            "lunes", "martes", "miercoles", "miércoles", "jueves", "viernes", "sabado", "sábado", "domingo",
            "mañana", "manana", "pasado mañana", "hoy", "esta tarde", "esta noche", "la semana que viene",
            "el lunes", "en la semana", "fin de semana",
        };

        /// <summary>
        /// ¿Se puede mandar lo que escribió el modelo? Devuelve el texto ya limpio si sirve, o el motivo del descarte.
        /// <paramref name="entrante"/> es el mensaje al que se está contestando: todo dato de la respuesta tiene que
        /// poder rastrearse ahí.
        /// </summary>
        public static Veredicto Revisar(string generado, string entrante, int maxCaracteres = 400)
        {
            var v = new Veredicto();
            string t = Limpiar(generado);
            string e = entrante ?? "";

            if (RePensando.IsMatch(generado ?? "")) { v.Motivo = "devolvió su razonamiento en vez de una respuesta"; return v; }
            if (t.Length == 0) { v.Motivo = "el modelo devolvió vacío"; return v; }
            if (t.Length > maxCaracteres) { v.Motivo = $"demasiado larga ({t.Length} caracteres, el tope es {maxCaracteres})"; return v; }

            // --- tickets: no puede nombrar uno que no esté en el mensaje entrante
            var tickEnt = new HashSet<string>(ReTicket.Matches(e).Cast<Match>().Select(m => Norm(m.Value)));
            var inventados = ReTicket.Matches(t).Cast<Match>().Select(m => m.Value)
                .Where(x => !tickEnt.Contains(Norm(x))).Distinct().ToList();
            if (inventados.Count > 0) { v.Motivo = "nombra un ticket que nadie mencionó: " + string.Join(", ", inventados); return v; }

            // --- horas y fechas: si el entrante no habla de tiempo, la respuesta tampoco puede comprometerlo
            bool entranteTiempo = ReHora.IsMatch(e) || ReFecha.IsMatch(e) || TienePalabraDeTiempo(e);
            if (!entranteTiempo)
            {
                var hs = ReHora.Matches(t).Cast<Match>().Select(m => m.Value).Distinct().ToList();
                if (hs.Count > 0) { v.Motivo = "promete una hora que nadie pidió: " + string.Join(", ", hs); return v; }
                var fs = ReFecha.Matches(t).Cast<Match>().Select(m => m.Value).Distinct().ToList();
                if (fs.Count > 0) { v.Motivo = "promete una fecha que nadie pidió: " + string.Join(", ", fs); return v; }
                string dia = PalabraDeTiempo(t);
                if (dia != null) { v.Motivo = "se compromete a un día que nadie pidió: " + dia; return v; }
            }

            // --- enlaces: un modelo inventando URLs es de lo peor que puede pasar
            var links = ReEnlace.Matches(t).Cast<Match>().Select(m => m.Value)
                .Where(x => e.IndexOf(x, StringComparison.OrdinalIgnoreCase) < 0).Distinct().ToList();
            if (links.Count > 0) { v.Motivo = "inventa un enlace: " + string.Join(", ", links); return v; }

            v.Sirve = true;
            v.Texto = t;
            return v;
        }

        /// <summary>Saca el razonamiento, las comillas de más y los espacios raros. No cambia el contenido.</summary>
        public static string Limpiar(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            string t = Regex.Replace(s, @"(?is)<think>.*?</think>", " ");
            t = Regex.Replace(t, @"(?is)</?think[^>]*>", " ");
            t = ReEspacios.Replace(t, " ").Trim();
            // algunos modelos devuelven la respuesta entre comillas o con un "Respuesta:" adelante
            t = Regex.Replace(t, @"^(respuesta|answer)\s*:\s*", "", RegexOptions.IgnoreCase).Trim();
            if (t.Length >= 2 && (t[0] == '"' || t[0] == '«') && (t[t.Length - 1] == '"' || t[t.Length - 1] == '»'))
                t = t.Substring(1, t.Length - 2).Trim();
            return t;
        }

        static string Norm(string s) => (s ?? "").Replace(" ", "").Replace("-", "").ToUpperInvariant();

        static bool TienePalabraDeTiempo(string s) => PalabraDeTiempo(s) != null;

        static string PalabraDeTiempo(string s)
        {
            // 🚨 con la puntuación pegada, " lunes " no matcheaba "el lunes?" y la respuesta quedaba
            //    marcada como si se comprometiera a un día que el otro SÍ había nombrado.
            string n = " " + Regex.Replace(Contactos.Normalizar(s ?? ""), @"[^\w\s]", " ") + " ";
            n = Regex.Replace(n, @"\s+", " ");
            foreach (var p in Cuando)
            {
                string q = " " + Contactos.Normalizar(p) + " ";
                if (n.Contains(q)) return p;
            }
            return null;
        }
    }
}
