using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TeamsTools
{
    /// <summary>El texto ya masticado una sola vez: los detectores comparten esto en vez de re-normalizar cada uno.</summary>
    internal sealed class Masticado
    {
        public string Crudo = "", Plano = "", Norm = "";
        public string[] Palabras = new string[0];
        public HashSet<string> Set = new HashSet<string>(StringComparer.Ordinal);
        public int Largo, Letras, Mayus, Signos, Interrogaciones, Exclamaciones, Emojis;

        static readonly Regex ReEtiquetas = new Regex("<[^>]+>", RegexOptions.Compiled);
        static readonly Regex ReEspacios = new Regex(@"\s+", RegexOptions.Compiled);
        static readonly Regex ReToken = new Regex(@"[\p{L}\p{N}][\p{L}\p{N}'’\-]*", RegexOptions.Compiled);

        public static Masticado De(string texto)
        {
            var m = new Masticado { Crudo = texto ?? "" };
            m.Plano = ReEspacios.Replace(ReEtiquetas.Replace(m.Crudo, " "), " ").Trim();
            m.Norm = Contactos.Normalizar(m.Plano);
            m.Palabras = ReToken.Matches(m.Norm).Cast<Match>().Select(x => x.Value).ToArray();
            foreach (var p in m.Palabras) m.Set.Add(p);
            m.Largo = m.Plano.Length;
            foreach (var c in m.Plano)
            {
                if (char.IsLetter(c)) { m.Letras++; if (char.IsUpper(c)) m.Mayus++; }
                else if (c == '?' || c == '¿') m.Interrogaciones++;
                else if (c == '!' || c == '¡') m.Exclamaciones++;
                else if (char.IsPunctuation(c)) m.Signos++;
                else if (c >= 0x1F000 || (c >= 0x2600 && c <= 0x27BF)) m.Emojis++;
            }
            return m;
        }

        public bool Tiene(params string[] palabras) => palabras.Any(p => Set.Contains(p));
        public int Cuenta(params string[] palabras) => palabras.Count(p => Set.Contains(p));
        public bool Frase(params string[] frases) => frases.Any(f => Norm.IndexOf(f, StringComparison.Ordinal) >= 0);
        /// <summary>Proporción de mayúsculas sobre las letras: gritar es una señal, pero una sigla no.</summary>
        public double Grito => Letras >= 12 ? (double)Mayus / Letras : 0;
    }

    /// <summary>Lo que un detector encontró.</summary>
    internal sealed class Senal
    {
        public string Modelo = "", Etiqueta = "", Evidencia = "";
        public double Puntaje;                 // 0..1
        public Color Tinte = Tema.TextoSuave;
        public bool Prendida => Puntaje >= 0.5;
    }

    /// <summary>
    /// Lo que los detectores vieron en un mensaje entrante, en banderas. Es lo que consumen las reglas.
    /// `Porque` es la parte importante: convierte «la regla se disparó» en «se disparó por esto».
    /// </summary>
    internal sealed class Senales
    {
        public bool Urgente, Pregunta, PideAccion, Saludo, Agradece, Cierre, TieneFecha, TieneTicket, EsBot;
        public string Idioma = "";                 // "es" | "en" | ""
        public string Ticket = "";                 // "NIM-409" si lo detectó
        public DateTime? Vence;
        public int Prioridad;                      // 0..100, para ordenar una bandeja
        public HashSet<string> Etiquetas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Porque = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool Tiene(string senal) => Etiquetas.Contains(senal ?? "");
        public string PorQue(string senal) { string v; return Porque.TryGetValue(senal ?? "", out v) ? v : ""; }
        /// <summary>Una línea explicable: «urgente (2 palabras de apuro) · pide acción (3 verbos)».</summary>
        public string Explicacion() => Etiquetas.Count == 0 ? "ninguna señal"
            : string.Join(" · ", Etiquetas.OrderBy(e => e).Select(e => Micromodelos.Bonito(e) + (PorQue(e).Length > 0 ? $" ({PorQue(e)})" : "")));
    }

    /// <summary>Un detector con nombre: qué busca y cómo puntúa.</summary>
    internal sealed class Micromodelo
    {
        public string Nombre = "", Que = "";
        public Color Tinte = Tema.Cielo;
        public Func<Masticado, Senal> Correr;
    }

    /// <summary>
    /// Micromodelos: detectores chicos y LOCALES que leen un mensaje y prenden señales.
    ///
    /// 🚨 A propósito NO son LLM. El modelo grande de la máquina da 0,85 tok/s (el cuello son 25 GB/s de ancho de
    /// banda), así que una respuesta corta tardaría ~45 s: sirve para pensar, no para clasificar en vivo. Estos
    /// corren en microsegundos, sin servidor, sin red y sin depender de que el LLM esté levantado. El LLM queda
    /// como opción aparte para lo que estos no resuelven.
    ///
    /// Cada detector devuelve un puntaje 0..1 y la evidencia de por qué: sin evidencia no se puede auditar,
    /// y un clasificador que no se puede auditar termina decidiendo cosas que nadie entiende.
    /// </summary>
    internal static class Micromodelos
    {
        // --------------------------------------------------------------- léxicos
        static readonly string[] Urgentes = { "urgente", "urgencia", "ya", "ahora", "inmediato", "critico", "grave", "caido", "caida", "roto", "rompio", "bloqueante", "prod", "produccion", "corriendo", "apura", "rapido", "asap" };
        static readonly string[] Interrogativas = { "que", "qué", "como", "cómo", "cuando", "cuándo", "donde", "dónde", "quien", "quién", "cual", "cuál", "por que", "porque", "porqué", "cuanto", "cuánto" };
        // un imperativo directo («mirá el ticket») ya es un pedido; un modal («necesito café») pesa menos
        static readonly string[] Imperativos = { "mira", "mirá", "revisa", "revisá", "fijate", "chequea", "chequeá", "pasame", "mandame", "avisame", "ayudame", "confirmame", "verifica", "proba", "probá", "decime", "contame" };
        static readonly string[] Modales = { "podes", "podés", "puedes", "podrias", "podrías", "necesito", "necesitamos", "manda", "avisa", "ayuda", "confirma", "prueba", "pasa" };
        static readonly string[] Saludos = { "hola", "holis", "buenas", "buen dia", "buenos dias", "buenas tardes", "buenas noches", "hey", "che", "hi", "hello" };
        static readonly string[] Despedidas = { "chau", "adios", "nos vemos", "hasta luego", "saludos", "abrazo", "bye", "hasta manana", "hasta mañana" };
        static readonly string[] Gracias = { "gracias", "graciass", "thanks", "thank", "genial", "barbaro", "bárbaro", "perfecto", "joya", "dale gracias", "te agradezco" };
        static readonly string[] Positivas = { "genial", "perfecto", "excelente", "joya", "barbaro", "bárbaro", "buenisimo", "buenísimo", "ok", "dale", "listo", "funciona", "anda", "resuelto", "solucionado", "gracias" };
        static readonly string[] Negativas = { "no anda", "no funciona", "error", "falla", "fallo", "roto", "rompio", "problema", "mal", "nada", "imposible", "bloqueado", "trabado", "raro", "grave", "caido" };
        static readonly string[] Espera = { "quedo atento", "quedo atenta", "espero", "esperando", "avisame", "avisen", "me confirmas", "me confirmás", "cuando puedas", "cuando tengas", "me decis", "me decís" };
        static readonly string[] Reunion = { "reunion", "reunión", "call", "meet", "llamada", "huddle", "daily", "sync", "zoom", "teams" };
        static readonly string[] StopEs = { "de", "la", "que", "el", "en", "y", "a", "los", "no", "un", "por", "con", "una", "para", "es", "se", "lo", "las", "del", "al" };
        static readonly string[] StopEn = { "the", "of", "and", "to", "in", "is", "it", "you", "that", "was", "for", "on", "are", "with", "as", "this", "have", "from", "they", "be" };

        static readonly Regex ReTicket = new Regex(@"\b[A-Z]{2,10}\d?-\d{1,6}\b", RegexOptions.Compiled);
        static readonly Regex ReUrl = new Regex(@"https?://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex ReHora = new Regex(@"\b([01]?\d|2[0-3])[:.][0-5]\d\b", RegexOptions.Compiled);
        static readonly Regex ReFecha = new Regex(@"\b\d{1,2}[/\-]\d{1,2}([/\-]\d{2,4})?\b", RegexOptions.Compiled);
        static readonly Regex ReRuta = new Regex(@"[A-Za-z]:\\[^\s]+|/[a-z0-9_\-./]{6,}", RegexOptions.Compiled);
        static readonly Regex ReCodigo = new Regex(@"`{1,3}[^`]+`{1,3}|\b(select|insert|update|delete|function|class|var |const |public |private )\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex ReNumero = new Regex(@"\b\d{4,}\b", RegexOptions.Compiled);

        // --------------------------------------------------------------- el catálogo
        public static readonly List<Micromodelo> Catalogo = new List<Micromodelo>
        {
            M("urgencia", "qué tan apurado viene", Tema.Rosa, m =>
            {
                int n = m.Cuenta(Urgentes);
                double p = Math.Min(1, n * 0.34 + Math.Min(0.3, m.Exclamaciones * 0.12) + (m.Grito > 0.55 ? 0.35 : 0));
                var ev = new List<string>();
                if (n > 0) ev.Add(n + " palabra/s de apuro");
                if (m.Exclamaciones > 0) ev.Add(m.Exclamaciones + " signo/s de exclamación");
                if (m.Grito > 0.55) ev.Add($"{m.Grito * 100:0} % en mayúsculas");
                return S("urgencia", p >= 0.75 ? "muy urgente" : p >= 0.5 ? "urgente" : "tranquilo", p, string.Join(" · ", ev), Tema.Rosa);
            }),
            M("pregunta", "te están preguntando algo", Tema.Cielo, m =>
            {
                bool signo = m.Interrogaciones > 0;
                int inter = Interrogativas.Count(q => m.Norm.StartsWith(q + " ", StringComparison.Ordinal) || m.Norm.IndexOf(" " + q + " ", StringComparison.Ordinal) >= 0);
                double p = Math.Min(1, (signo ? 0.65 : 0) + Math.Min(0.45, inter * 0.2));
                return S("pregunta", p >= 0.5 ? "es una pregunta" : "no pregunta", p,
                    (signo ? m.Interrogaciones + " signo/s" : "sin signo") + (inter > 0 ? " · " + inter + " interrogativa/s" : ""), Tema.Cielo);
            }),
            M("pedido", "te piden que hagas algo", Tema.Durazno, m =>
            {
                int imp = m.Cuenta(Imperativos), mod = m.Cuenta(Modales);
                bool frase = m.Frase("necesito que", "podrias", "podrías", "te pido", "hace falta que", "me pasas", "me pasás");
                double p = Math.Min(1, imp * 0.55 + mod * 0.3 + (frase ? 0.35 : 0));
                var ev = new List<string>();
                if (imp > 0) ev.Add(imp + " imperativo/s");
                if (mod > 0) ev.Add(mod + " modal/es");
                if (frase) ev.Add("frase directa");
                return S("pedido", p >= 0.5 ? "te piden algo" : "sin pedido", p, ev.Count > 0 ? string.Join(" · ", ev) : "nada", Tema.Durazno);
            }),
            M("espera", "se quedan esperando tu respuesta", Tema.Malva, m =>
            {
                bool e = m.Frase(Espera);
                return S("espera", e ? "esperan respuesta" : "no marcan espera", e ? 0.85 : 0.05, e ? "frase de espera explícita" : "", Tema.Malva);
            }),
            M("ticket", "menciona un ticket", Tema.Cyan, m =>
            {
                var t = ReTicket.Matches(m.Plano).Cast<Match>().Select(x => x.Value).Distinct().ToArray();
                return S("ticket", t.Length > 0 ? string.Join(" ", t.Take(3)) : "ninguno", t.Length > 0 ? 1 : 0, t.Length + " ticket/s", Tema.Cyan);
            }),
            M("produccion", "habla de PROD", Tema.Rosa, m =>
            {
                bool prod = m.Frase("prod", "produccion", "producción");
                return S("produccion", prod ? "menciona PROD" : "no", prod ? 0.9 : 0, prod ? "🚨 mirarlo antes que el resto" : "", Tema.Rosa);
            }),
            M("cuando", "trae fecha u hora", Tema.Crema, m =>
            {
                var h = ReHora.Matches(m.Plano).Cast<Match>().Select(x => x.Value).Distinct().ToArray();
                var f = ReFecha.Matches(m.Plano).Cast<Match>().Select(x => x.Value).Distinct().ToArray();
                bool rel = m.Frase("hoy", "manana", "mañana", "ahora", "mas tarde", "más tarde", "esta tarde", "a la tarde", "el lunes", "el martes", "el miercoles", "el jueves", "el viernes");
                double p = Math.Min(1, (h.Length + f.Length) * 0.4 + (rel ? 0.45 : 0));
                var todo = h.Concat(f).Take(3).ToArray();
                return S("cuando", todo.Length > 0 ? string.Join(" ", todo) : rel ? "referencia relativa" : "sin fecha", p,
                    (h.Length > 0 ? h.Length + " hora/s " : "") + (f.Length > 0 ? f.Length + " fecha/s " : "") + (rel ? "+ relativa" : ""), Tema.Crema);
            }),
            M("saludo", "abre la conversación", Tema.Salvia, m =>
            {
                bool s = Saludos.Any(x => m.Norm.StartsWith(x, StringComparison.Ordinal)) || m.Tiene("hola", "buenas", "hey", "che");
                return S("saludo", s ? "saluda" : "va directo", s ? 0.85 : 0.05, s ? "empieza saludando" : "", Tema.Salvia);
            }),
            M("cierre", "cierra la conversación", Tema.Apagado, m =>
            {
                bool d = m.Frase(Despedidas), g = m.Frase(Gracias);
                double p = d ? 0.9 : g ? 0.6 : 0.05;
                return S("cierre", d ? "se despide" : g ? "agradece" : "sigue abierta", p, d ? "despedida" : g ? "agradecimiento" : "", Tema.Apagado);
            }),
            M("animo", "cómo viene el tono", Tema.Salvia, m =>
            {
                int pos = Positivas.Count(x => m.Norm.IndexOf(x, StringComparison.Ordinal) >= 0);
                int neg = Negativas.Count(x => m.Norm.IndexOf(x, StringComparison.Ordinal) >= 0);
                double p = pos + neg == 0 ? 0.5 : (double)pos / (pos + neg);
                return S("animo", p > 0.66 ? "positivo" : p < 0.34 ? "negativo" : "neutro", p,
                    $"{pos} positiva/s · {neg} negativa/s", p > 0.66 ? Tema.Salvia : p < 0.34 ? Tema.Rosa : Tema.TextoSuave);
            }),
            M("idioma", "en qué idioma escribe", Tema.Malva, m =>
            {
                int es = m.Cuenta(StopEs), en = m.Cuenta(StopEn);
                string cual = es == en ? "—" : es > en ? "castellano" : "inglés";
                double p = es + en == 0 ? 0 : Math.Abs(es - en) / (double)(es + en);
                return S("idioma", cual, p, $"{es} marcas es · {en} marcas en", Tema.Malva);
            }),
            M("tecnico", "trae código, rutas o links", Tema.Cielo, m =>
            {
                bool cod = ReCodigo.IsMatch(m.Plano), ruta = ReRuta.IsMatch(m.Plano);
                var urls = ReUrl.Matches(m.Plano).Count;
                double p = Math.Min(1, (cod ? 0.5 : 0) + (ruta ? 0.3 : 0) + Math.Min(0.4, urls * 0.25));
                return S("tecnico", p >= 0.5 ? "técnico" : "charla", p,
                    (cod ? "código " : "") + (ruta ? "ruta " : "") + (urls > 0 ? urls + " link/s" : ""), Tema.Cielo);
            }),
            M("reunion", "habla de juntarse", Tema.Cyan, m =>
            {
                int n = m.Cuenta(Reunion);
                return S("reunion", n > 0 ? "menciona reunión" : "no", Math.Min(1, n * 0.5), n + " mención/es", Tema.Cyan);
            }),
            M("largo", "cuánto escribió", Tema.TextoSuave, m =>
            {
                double p = Math.Min(1, m.Palabras.Length / 60.0);
                return S("largo", m.Palabras.Length < 6 ? "telegrama" : m.Palabras.Length < 30 ? "normal" : "parrafada", p,
                    $"{m.Palabras.Length} palabras · {m.Largo} caracteres", Tema.TextoSuave);
            }),
            M("datos", "trae números largos", Tema.Crema, m =>
            {
                var n = ReNumero.Matches(m.Plano).Cast<Match>().Select(x => x.Value).Distinct().Take(3).ToArray();
                return S("datos", n.Length > 0 ? string.Join(" ", n) : "sin números", n.Length > 0 ? 0.8 : 0,
                    n.Length > 0 ? "puede ser un id, una cuenta o un device" : "", Tema.Crema);
            }),
            M("bot", "lo mandó una máquina", Tema.MuyApagado, m =>
            {
                bool b = m.Frase("no-reply", "noreply", "no responder", "mensaje automatico", "mensaje automático", "automated", "do not reply",
                                 "build ", "pipeline", "deployment succeeded", "deployment failed", "se ha completado", "notificacion automatica");
                return S("bot", b ? "parece automático" : "parece humano", b ? 0.9 : 0.05, b ? "marca de mensaje automático" : "", Tema.MuyApagado);
            }),
        };

        static Micromodelo M(string nombre, string que, Color t, Func<Masticado, Senal> f) => new Micromodelo { Nombre = nombre, Que = que, Tinte = t, Correr = f };
        static Senal S(string modelo, string etiqueta, double p, string ev, Color t) =>
            new Senal { Modelo = modelo, Etiqueta = etiqueta, Puntaje = Math.Max(0, Math.Min(1, p)), Evidencia = ev ?? "", Tinte = t };

        /// <summary>Corre TODOS los detectores sobre un texto. Masticar una sola vez y repartir.</summary>
        public static List<Senal> Detectar(string texto)
        {
            var m = Masticado.De(texto);
            var res = new List<Senal>(Catalogo.Count);
            foreach (var mm in Catalogo)
            {
                try { res.Add(mm.Correr(m)); }
                catch { res.Add(new Senal { Modelo = mm.Nombre, Etiqueta = "falló", Puntaje = 0, Tinte = Tema.Apagado }); }
            }
            return res;
        }

        // ------------------------------------------------------------------ contrato simple para las reglas
        // La UI de reglas no quiere puntajes ni evidencia cruda: quiere banderas y un «por qué» mostrable.

        static readonly Dictionary<string, string> Lindos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "urgencia", "urgente" }, { "pregunta", "es una pregunta" }, { "pedido", "pide acción" },
            { "espera", "espera respuesta" }, { "ticket", "menciona un ticket" }, { "produccion", "habla de PROD" },
            { "cuando", "trae fecha u hora" }, { "saludo", "saluda" }, { "cierre", "cierra o agradece" },
            { "animo", "tono" }, { "idioma", "idioma" }, { "tecnico", "es técnico" }, { "reunion", "menciona reunión" },
            { "largo", "largo del mensaje" }, { "datos", "trae números" }, { "bot", "lo mandó una máquina" },
        };

        /// <summary>Nombres de todos los detectores: la UI de condiciones se arma de acá, sin cablear nada a mano.</summary>
        public static string[] Nombres => Catalogo.Select(c => c.Nombre).ToArray();

        /// <summary>Etiqueta linda para mostrar en la UI.</summary>
        public static string Bonito(string senal)
        {
            string v;
            return Lindos.TryGetValue(senal ?? "", out v) ? v : (senal ?? "");
        }

        /// <summary>Qué busca cada detector, para el tooltip o la ayuda.</summary>
        public static string Que(string senal)
        {
            var m = Catalogo.FirstOrDefault(c => string.Equals(c.Nombre, senal, StringComparison.OrdinalIgnoreCase));
            return m != null ? m.Que : "";
        }

        /// <summary>
        /// Lo que ven las reglas: banderas, no puntajes. `Porque` trae por qué coincidió cada una — sin eso la
        /// pantalla es una caja negra y el user no puede auditar por qué se disparó una respuesta automática.
        /// </summary>
        public static Senales Analizar(string texto)
        {
            var s = Detectar(texto);
            var r = new Senales
            {
                Urgente = Puntaje(s, "urgencia") >= 0.5,
                Pregunta = Puntaje(s, "pregunta") >= 0.5,
                PideAccion = Puntaje(s, "pedido") >= 0.5,
                Saludo = Puntaje(s, "saludo") >= 0.5,
                Cierre = Puntaje(s, "cierre") >= 0.5,
                TieneFecha = Puntaje(s, "cuando") >= 0.5,
                TieneTicket = Puntaje(s, "ticket") >= 0.5,
                EsBot = Puntaje(s, "bot") >= 0.5,
                Prioridad = Prioridad(s),
            };
            var cierre = Una(s, "cierre");
            r.Agradece = cierre != null && cierre.Evidencia.IndexOf("agradecimiento", StringComparison.Ordinal) >= 0;
            var idioma = Una(s, "idioma");
            r.Idioma = idioma == null || idioma.Puntaje <= 0 ? "" : idioma.Etiqueta == "castellano" ? "es" : idioma.Etiqueta == "inglés" ? "en" : "";
            var tk = Una(s, "ticket");
            r.Ticket = tk != null && tk.Puntaje > 0 ? tk.Etiqueta.Split(' ')[0] : "";
            r.Vence = Vencimiento(texto);
            foreach (var x in s)
            {
                if (!x.Prendida) continue;
                r.Etiquetas.Add(x.Modelo);
                r.Porque[x.Modelo] = x.Evidencia.Length > 0 ? x.Evidencia : x.Etiqueta;
            }
            return r;
        }

        /// <summary>
        /// Fecha límite, solo cuando es explícita. Conservador a propósito: preferimos no saber a inventar un
        /// vencimiento que después dispare un recordatorio equivocado.
        /// </summary>
        static DateTime? Vencimiento(string texto)
        {
            var plano = Masticado.De(texto);
            var mh = ReHora.Match(plano.Plano);
            if (!mh.Success) return null;
            var partes = mh.Value.Replace('.', ':').Split(':');
            int hh, mm;
            if (!int.TryParse(partes[0], out hh) || !int.TryParse(partes[1], out mm)) return null;
            var baseDia = DateTime.Today;
            if (plano.Frase("manana", "mañana")) baseDia = DateTime.Today.AddDays(1);
            else if (!plano.Frase("hoy", "ahora", "a las", "antes de las", "para las")) return null;
            try { return baseDia.AddHours(hh).AddMinutes(mm); } catch { return null; }
        }

        public static Senal Una(List<Senal> señales, string modelo) => señales.FirstOrDefault(s => s.Modelo == modelo);
        public static double Puntaje(List<Senal> señales, string modelo) { var s = Una(señales, modelo); return s != null ? s.Puntaje : 0; }

        /// <summary>Una línea con lo que importa, para la tabla y el log.</summary>
        public static string Resumen(List<Senal> señales)
        {
            var prendidas = señales.Where(s => s.Prendida && s.Modelo != "idioma" && s.Modelo != "largo" && s.Modelo != "animo")
                                   .OrderByDescending(s => s.Puntaje).Select(s => s.Modelo).Take(4).ToList();
            return prendidas.Count == 0 ? "nada llamativo" : string.Join(" · ", prendidas);
        }

        /// <summary>
        /// Prioridad 0..100 combinando las señales: es lo que ordena una bandeja cuando volvés y tenés 40 sin leer.
        /// Pesos a ojo pero explicables: PROD y urgencia mandan, después que te pidan algo o te estén esperando.
        /// </summary>
        public static int Prioridad(List<Senal> señales)
        {
            double p = 0;
            p += Puntaje(señales, "produccion") * 34;
            p += Puntaje(señales, "urgencia") * 26;
            p += Puntaje(señales, "pedido") * 16;
            p += Puntaje(señales, "espera") * 12;
            p += Puntaje(señales, "pregunta") * 8;
            p += Puntaje(señales, "ticket") * 4;
            return (int)Math.Round(Math.Min(100, p));
        }
    }
}
