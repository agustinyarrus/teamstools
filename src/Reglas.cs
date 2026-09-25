using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TeamsTools
{
    /// <summary>Una regla del autocontestador: patron de entrada -> respuesta con formato.</summary>
    internal sealed class Regla
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Nombre = "";
        public bool Activa = true;
        public string Patron = "";             // regex (o texto suelto si EsRegex = false)
        public bool EsRegex = true;
        public bool IgnorarMayusculas = true;
        public string RespuestaHtml = "";     // lo que se pega en Teams (con formato) — primera parte de texto
        public string RespuestaTexto = "";    // version plana (portapapeles de texto y vista previa)
        public string RespuestaRtf = "";      // lo que edita el RichTextBox
        /// <summary>
        /// La respuesta completa: puede ser VARIAS burbujas (textos + imágenes). Los tres campos de arriba siguen
        /// guardando la primera parte de texto para que un exe viejo lea la regla sin perder nada.
        /// </summary>
        public Envio Respuesta = new Envio();
        public bool SoloPrivados = true;      // no responder en grupos
        public int EnfriamientoMinutos = 30;  // no volver a responder a la misma persona antes de esto
        public string Personas = "";          // filtro opcional: "Rivas; Ortega" (vacio = todos)
        public int Orden = 0;

        // ------------------------------------------------------------------ cuándo y cómo contesta
        /// <summary>
        /// Segundos a esperar ANTES de contestar, para que no parezca un robot que responde en el mismo instante.
        /// 0 = al toque. Se cuenta desde que se detectó el mensaje; si mientras tanto la persona sigue escribiendo,
        /// el autocontestador vuelve a leer y contesta a lo último que dijo.
        /// </summary>
        public int RetrasoSegundos = 0;
        /// <summary>Tope de respuestas de ESTA regla por día (0 = sin tope). Evita que una regla suelta se desboque.</summary>
        public int MaxPorDia = 0;
        /// <summary>Solo contesta dentro de esta franja horaria ("09:00"-"18:00"). Vacío = a cualquier hora.</summary>
        public string DesdeHora = "", HastaHora = "";
        /// <summary>Días de la semana en los que aplica ("1,2,3,4,5"; 0 = domingo). Vacío = todos.</summary>
        public string Dias = "";
        /// <summary>
        /// Señales que el mensaje entrante TIENE que tener para que la regla dispare (nombres de los micromodelos,
        /// separados por ;). Vacío = no exige ninguna. Se suma al patrón: primero el regex, después las señales.
        /// </summary>
        public string SenalesRequeridas = "";
        /// <summary>Señales que, si aparecen, BLOQUEAN la regla aunque el patrón coincida.</summary>
        public string SenalesExcluidas = "";
        /// <summary>Si está encendida, la respuesta la redacta el modelo local en vez de usar el texto fijo.</summary>
        public bool UsarIA = false;
        /// <summary>Instrucción que se le da al modelo cuando UsarIA está encendida.</summary>
        public string InstruccionIA = "";

        // ------------------------------------------------------------------ estadística propia
        public int Usos = 0;
        public DateTime? UltimoUso;
        public int Fallos = 0;
        public string UltimoResultado = "";
        /// <summary>Milisegundos promedio que tardó en contestar (media móvil simple sobre los envíos ok).</summary>
        public int MsPromedio = 0;
        /// <summary>Respuestas de hoy, para el tope diario. Se reinicia solo al cambiar el día.</summary>
        public int UsosHoy = 0;
        public DateTime? DiaDeUsosHoy;

        /// <summary>Pone al día el contador diario si cambió la fecha, y dice si todavía tiene cupo.</summary>
        public bool TieneCupoHoy()
        {
            if (DiaDeUsosHoy?.Date != DateTime.Today) { UsosHoy = 0; DiaDeUsosHoy = DateTime.Today; }
            return MaxPorDia <= 0 || UsosHoy < MaxPorDia;
        }

        /// <summary>Anota un envío en la estadística de la regla.</summary>
        public void Anotar(bool ok, int ms, string detalle)
        {
            if (DiaDeUsosHoy?.Date != DateTime.Today) { UsosHoy = 0; DiaDeUsosHoy = DateTime.Today; }
            UltimoResultado = (ok ? "ok · " : "falló · ") + (detalle ?? "");
            if (ok)
            {
                Usos++; UsosHoy++; UltimoUso = DateTime.Now;
                MsPromedio = MsPromedio <= 0 ? ms : (int)Math.Round(MsPromedio * 0.7 + ms * 0.3);
            }
            else Fallos++;
        }

        /// <summary>¿Estamos dentro de la franja horaria y de los días que la regla permite?</summary>
        public bool EnHorario(DateTime ahora)
        {
            if (Dias.Length > 0)
            {
                var dias = Dias.Split(',', ';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                if (dias.Count > 0 && !dias.Contains(((int)ahora.DayOfWeek).ToString())) return false;
            }
            if (DesdeHora.Length == 0 || HastaHora.Length == 0) return true;
            if (!TimeSpan.TryParse(DesdeHora, out var d) || !TimeSpan.TryParse(HastaHora, out var h)) return true;
            var t = ahora.TimeOfDay;
            return d <= h ? (t >= d && t <= h) : (t >= d || t <= h);   // franja que cruza la medianoche
        }

        /// <summary>Las señales exigidas y las excluidas, ya separadas.</summary>
        public string[] Exigidas => Trozos(SenalesRequeridas);
        public string[] Excluidas => Trozos(SenalesExcluidas);
        static string[] Trozos(string s) => (s ?? "").Split(';', ',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();

        /// <summary>Resumen de una línea de TODAS las condiciones extra, para la tabla.</summary>
        public string ResumenCondiciones()
        {
            var t = new List<string>();
            if (Exigidas.Length > 0) t.Add("+" + string.Join(" +", Exigidas));
            if (Excluidas.Length > 0) t.Add("−" + string.Join(" −", Excluidas));
            if (DesdeHora.Length > 0 && HastaHora.Length > 0) t.Add(DesdeHora + "-" + HastaHora);
            if (Dias.Length > 0)
            {
                // "1,3,5" se lee horrible; mostrarlo como "lun mié vie"
                var nombres = Dias.Split(',', ';').Select(x => x.Trim()).Where(x => x.Length > 0)
                    .Select(x => int.TryParse(x, out int d) ? Agenda.DiaCorto(d) : x);
                t.Add(string.Join(" ", nombres));
            }
            if (MaxPorDia > 0) t.Add("máx " + MaxPorDia + "/día");
            return t.Count == 0 ? "" : string.Join(" · ", t);
        }

        [System.Web.Script.Serialization.ScriptIgnore]
        Regex compilada; string compiladaDe;

        public string Error
        {
            get { try { Compilar(); return ""; } catch (Exception ex) { return ex.Message; } }
        }

        Regex Compilar()
        {
            string clave = Patron + "|" + EsRegex + "|" + IgnorarMayusculas;
            if (compilada != null && compiladaDe == clave) return compilada;
            var op = RegexOptions.CultureInvariant | RegexOptions.Multiline;
            if (IgnorarMayusculas) op |= RegexOptions.IgnoreCase;
            string p = EsRegex ? Patron : Regex.Escape(Patron);
            if (p.Length == 0) p = "(?!)";
            compilada = new Regex(p, op, TimeSpan.FromMilliseconds(300));
            compiladaDe = clave;
            return compilada;
        }

        public bool Coincide(string texto)
        {
            if (string.IsNullOrEmpty(texto)) return false;
            try { return Compilar().IsMatch(texto); } catch { return false; }
        }

        public Match Buscar(string texto)
        {
            try { return Compilar().Match(texto ?? ""); } catch { return Match.Empty; }
        }

        /// <summary>
        /// El envío a mandar. Si la regla ya tiene partes, esas; si no (reglas de la semilla, la regla por defecto, o
        /// cualquier Regla construida en código), arma una sola burbuja con los campos de siempre.
        /// </summary>
        public Envio RespuestaEfectiva()
        {
            if (Respuesta != null && Respuesta.Partes.Count > 0) return Respuesta;
            string html = RespuestaHtml.Length > 0 ? RespuestaHtml : System.Net.WebUtility.HtmlEncode(RespuestaTexto).Replace("\n", "<br>");
            return Envio.DeTexto(html, RespuestaTexto, RespuestaRtf);
        }

        /// <summary>
        /// Deja RespuestaHtml/Texto/Rtf con la PRIMERA parte de texto del envío, para que un exe viejo (o cualquier
        /// código que todavía lea esos campos) siga viendo la respuesta aunque ahora haya varias burbujas.
        /// </summary>
        public void SincronizarCamposViejos()
        {
            if (Respuesta == null) { Respuesta = new Envio(); return; }
            if (Respuesta.Partes.Count == 0) return;
            RespuestaHtml = Respuesta.PrimerHtml();
            RespuestaTexto = Respuesta.PrimerPlano();
            string rtf = Respuesta.PrimerRtf();
            if (rtf.Length > 0) RespuestaRtf = rtf;
        }

        public bool AplicaA(string nombrePersona)
        {
            if (string.IsNullOrWhiteSpace(Personas)) return true;
            string n = Contactos.Normalizar(nombrePersona);
            return Personas.Split(';', ',').Select(p => Contactos.Normalizar(p)).Where(p => p.Length > 0).Any(p => n.Contains(p));
        }
    }

    /// <summary>Ajustes del autocontestador + reglas. Vive en respuestas.json.</summary>
    internal sealed class ConfigRespuestas
    {
        /// <summary>
        /// El interruptor principal: el MODO AUTOMÁTICO contesta solo los chats que llegan. Hasta el 25-sep se llamaba
        /// «modo celular» y al user le confundía (no tiene nada que ver con el teléfono): en el json se lee la clave
        /// nueva y, si no está, la vieja; y se escriben las dos para que un exe viejo siga entendiendo el archivo.
        /// </summary>
        public bool ModoAutomatico = false;
        public int ActivarSiInactivoMinutos = 0;       // 0 = solo manual; N = prender solo tras N min sin actividad
        public bool ApagarAlVolver = true;             // apagar el modo automático cuando vuelvo a tocar el teclado (si se prendio solo)
        public bool ResponderGrupos = false;
        public bool ResponderSinRegla = false;         // si ningun patron coincide, usar la respuesta por defecto
        public int EnfriamientoGeneralMinutos = 30;
        public int SegundosEntreLecturas = 6;
        public string Firma = "";                      // se agrega al final de cada respuesta (opcional)
        /// <summary>
        /// Segundos que se le dan al modelo local para redactar. Si tarda más, se descarta y contesta el texto fijo:
        /// una respuesta automática que llega tarde no sirve, el otro ya cerró el chat.
        /// 🚨 Tiene que estar POR ENCIMA del peor caso medido del modelo, no pegado: un corte a la altura del peor
        /// caso salta con la variación normal y tira a la basura generaciones que estaban bien. Es una válvula para
        /// lo patológico, no para el ruido. Con gemma-4-E2B, medido: mediana 3,0 s y peor 5,3 s ⇒ 8 deja margen real.
        /// </summary>
        public int IaMaxSegundos = 8;
        /// <summary>
        /// Techo de tokens que se le pide al modelo. Manda el tiempo: pedir de más alarga el peor caso sin mejorar
        /// nada, porque una respuesta de chat son dos oraciones. Medido: gemma nunca pasó de 22 tokens en 9 corridas.
        /// </summary>
        public int IaMaxTokens = 60;
        public Regla PorDefecto = new Regla { Nombre = "por defecto", Patron = ".*", RespuestaTexto = "Hola {nombre}! Estoy con el celular probando cosas, ya te escribo.", RespuestaHtml = "Hola {nombre}! Estoy con el celular probando cosas, ya te escribo." };
        public List<Regla> Reglas = new List<Regla>();
        string ruta;

        public static ConfigRespuestas Cargar(string ruta)
        {
            var c = new ConfigRespuestas { ruta = ruta };
            var d = Json.LeerObjeto(ruta);
            if (d != null)
            {
                c.ModoAutomatico = d.ContainsKey("modoAutomatico") ? Json.B(d, "modoAutomatico", false) : Json.B(d, "modoCelular", false);
                c.ActivarSiInactivoMinutos = Json.I(d, "activarSiInactivoMinutos", 0);
                c.ApagarAlVolver = Json.B(d, "apagarAlVolver", true);
                c.ResponderGrupos = Json.B(d, "responderGrupos", false);
                c.ResponderSinRegla = Json.B(d, "responderSinRegla", false);
                c.EnfriamientoGeneralMinutos = Math.Max(0, Json.I(d, "enfriamientoGeneralMinutos", 30));
                c.SegundosEntreLecturas = Math.Max(3, Json.I(d, "segundosEntreLecturas", 6));
                c.IaMaxSegundos = Math.Max(1, Json.I(d, "iaMaxSegundos", 8));
                c.IaMaxTokens = Math.Max(16, Json.I(d, "iaMaxTokens", 60));
                c.Firma = Json.S(d, "firma");
                if (d.TryGetValue("porDefecto", out var pd) && pd is Dictionary<string, object> pdd) c.PorDefecto = LeerRegla(pdd);
                c.Reglas = Json.Lista(d, "reglas").Select(LeerRegla).ToList();
            }
            else { c.Reglas = Semilla(); c.Guardar(); }
            return c;
        }

        static Regla LeerRegla(Dictionary<string, object> o)
        {
            var r = new Regla
            {
                Id = Json.S(o, "id", Guid.NewGuid().ToString("N").Substring(0, 8)),
                Nombre = Json.S(o, "nombre"), Activa = Json.B(o, "activa", true), Patron = Json.S(o, "patron"),
                EsRegex = Json.B(o, "esRegex", true), IgnorarMayusculas = Json.B(o, "ignorarMayusculas", true),
                RespuestaHtml = Json.S(o, "respuestaHtml"), RespuestaTexto = Json.S(o, "respuestaTexto"), RespuestaRtf = Json.S(o, "respuestaRtf"),
                SoloPrivados = Json.B(o, "soloPrivados", true), EnfriamientoMinutos = Json.I(o, "enfriamientoMinutos", 30),
                Personas = Json.S(o, "personas"), Orden = Json.I(o, "orden", 0), Usos = Json.I(o, "usos", 0), UltimoUso = Json.F(o, "ultimoUso"),
                // condiciones y estadística nuevas: todas con default = comportamiento de antes
                RetrasoSegundos = Json.I(o, "retrasoSegundos", 0), MaxPorDia = Json.I(o, "maxPorDia", 0),
                DesdeHora = Json.S(o, "desdeHora"), HastaHora = Json.S(o, "hastaHora"), Dias = Json.S(o, "dias"),
                SenalesRequeridas = Json.S(o, "senalesRequeridas"), SenalesExcluidas = Json.S(o, "senalesExcluidas"),
                UsarIA = Json.B(o, "usarIA", false), InstruccionIA = Json.S(o, "instruccionIA"),
                Fallos = Json.I(o, "fallos", 0), UltimoResultado = Json.S(o, "ultimoResultado"),
                MsPromedio = Json.I(o, "msPromedio", 0), UsosHoy = Json.I(o, "usosHoy", 0), DiaDeUsosHoy = Json.F(o, "diaDeUsosHoy")
            };
            // si el json trae "partes" se usa; si no, se arma una sola burbuja con los campos de siempre
            r.Respuesta = Envio.Leer(o, r.RespuestaHtml, r.RespuestaTexto, r.RespuestaRtf);
            return r;
        }

        static Dictionary<string, object> EscribirRegla(Regla r)
        {
            r.SincronizarCamposViejos();
            return new Dictionary<string, object>
            {
                ["id"] = r.Id, ["nombre"] = r.Nombre, ["activa"] = r.Activa, ["patron"] = r.Patron, ["esRegex"] = r.EsRegex, ["ignorarMayusculas"] = r.IgnorarMayusculas,
                ["respuestaTexto"] = r.RespuestaTexto, ["respuestaHtml"] = r.RespuestaHtml, ["respuestaRtf"] = r.RespuestaRtf,
                ["partes"] = r.Respuesta.Guardar(),
                ["soloPrivados"] = r.SoloPrivados, ["enfriamientoMinutos"] = r.EnfriamientoMinutos, ["personas"] = r.Personas, ["orden"] = r.Orden,
                ["retrasoSegundos"] = r.RetrasoSegundos, ["maxPorDia"] = r.MaxPorDia,
                ["desdeHora"] = r.DesdeHora, ["hastaHora"] = r.HastaHora, ["dias"] = r.Dias,
                ["senalesRequeridas"] = r.SenalesRequeridas, ["senalesExcluidas"] = r.SenalesExcluidas,
                ["usarIA"] = r.UsarIA, ["instruccionIA"] = r.InstruccionIA,
                ["usos"] = r.Usos, ["ultimoUso"] = r.UltimoUso.HasValue ? r.UltimoUso.Value.ToString("yyyy-MM-dd HH:mm:ss") : null,
                ["fallos"] = r.Fallos, ["ultimoResultado"] = r.UltimoResultado, ["msPromedio"] = r.MsPromedio,
                ["usosHoy"] = r.UsosHoy, ["diaDeUsosHoy"] = r.DiaDeUsosHoy.HasValue ? r.DiaDeUsosHoy.Value.ToString("yyyy-MM-dd HH:mm:ss") : null
            };
        }

        public void Guardar()
        {
            try
            {
                Json.Escribir(ruta, new Dictionary<string, object>
                {
                    ["_ayuda"] = "Autocontestador. Cada regla: patron (regex) sobre el mensaje entrante -> respuesta. Marcadores en la respuesta: {nombre} {apellido} {hora} {fecha} {yo}.",
                    ["modoAutomatico"] = ModoAutomatico, ["modoCelular"] = ModoAutomatico,   // la clave vieja, para el exe de _anterior
                    ["activarSiInactivoMinutos"] = ActivarSiInactivoMinutos, ["apagarAlVolver"] = ApagarAlVolver,
                    ["responderGrupos"] = ResponderGrupos, ["responderSinRegla"] = ResponderSinRegla, ["enfriamientoGeneralMinutos"] = EnfriamientoGeneralMinutos,
                    ["segundosEntreLecturas"] = SegundosEntreLecturas, ["firma"] = Firma, ["iaMaxSegundos"] = IaMaxSegundos, ["iaMaxTokens"] = IaMaxTokens,
                    ["porDefecto"] = EscribirRegla(PorDefecto),
                    ["reglas"] = Reglas.OrderBy(r => r.Orden).Select(EscribirRegla).ToList()
                });
            }
            catch { }
        }

        /// <summary>La primera regla activa que coincide con el texto (por orden). null = ninguna.</summary>
        public Regla Elegir(string texto, string persona, bool esGrupo) { string _; return Elegir(texto, persona, esGrupo, null, DateTime.Now, out _); }

        /// <summary>
        /// Igual que el anterior pero con las señales que los micromodelos vieron en el mensaje, y devolviendo POR QUÉ
        /// se descartó cada regla. El orden de los filtros va de lo barato a lo caro: primero lo que no mira el texto
        /// (activa, tipo de chat, persona, horario, cupo), después las señales, y el regex al final.
        /// </summary>
        public Regla Elegir(string texto, string persona, bool esGrupo, ISet<string> senales, DateTime ahora, out string motivo)
        {
            var descartes = new List<string>();
            foreach (var r in Reglas.OrderBy(x => x.Orden))
            {
                string no = PorQueNo(r, texto, persona, esGrupo, senales, ahora);
                if (no == null) { motivo = ""; return r; }
                descartes.Add($"«{(r.Nombre.Length > 0 ? r.Nombre : "sin nombre")}»: {no}");
            }
            if (ResponderSinRegla && (!esGrupo || !PorDefecto.SoloPrivados)) { motivo = ""; return PorDefecto; }
            motivo = descartes.Count == 0 ? "no hay reglas" : string.Join(" · ", descartes.Take(4));
            return null;
        }

        /// <summary>null si la regla aplica; si no, la razón por la que quedó afuera (para mostrarla en el probador).</summary>
        public static string PorQueNo(Regla r, string texto, string persona, bool esGrupo, ISet<string> senales, DateTime ahora)
        {
            if (!r.Activa) return "está apagada";
            if (esGrupo && r.SoloPrivados) return "es un grupo y la regla es solo para privados";
            if (!r.AplicaA(persona)) return "no aplica a esa persona";
            if (!r.EnHorario(ahora)) return "fuera de su horario";
            if (!r.TieneCupoHoy()) return $"llegó al tope de {r.MaxPorDia} por día";
            if (r.Exigidas.Length > 0)
            {
                if (senales == null) return "pide señales y todavía no hay detectores";
                var faltan = r.Exigidas.Where(s => !senales.Contains(s)).ToList();
                if (faltan.Count > 0) return "le falta la señal " + string.Join(" y ", faltan);
            }
            if (r.Excluidas.Length > 0 && senales != null)
            {
                var choca = r.Excluidas.Where(senales.Contains).ToList();
                if (choca.Count > 0) return "el mensaje trae " + string.Join(" y ", choca) + ", que la bloquea";
            }
            if (!r.Coincide(texto)) return "el patrón no coincide";
            return null;
        }

        public static string Rellenar(string plantilla, string personaTeams, string yo)
        {
            if (string.IsNullOrEmpty(plantilla)) return "";
            string nombre = personaTeams ?? "", apellido = "";
            int c = nombre.IndexOf(',');
            if (c >= 0) { apellido = nombre.Substring(0, c).Trim(); nombre = nombre.Substring(c + 1).Trim(); }
            int sp = nombre.IndexOf(' '); if (sp > 0) nombre = nombre.Substring(0, sp);
            var ahora = DateTime.Now;
            return plantilla.Replace("{nombre}", nombre).Replace("{apellido}", apellido).Replace("{hora}", ahora.ToString("HH:mm"))
                            .Replace("{fecha}", ahora.ToString("dd/MM")).Replace("{yo}", yo ?? "").Replace("{dia}", ahora.ToString("dddd", new CultureInfo("es-AR")));
        }

        static List<Regla> Semilla() => new List<Regla>
        {
            new Regla { Nombre = "saludo", Patron = @"^\s*(hola|holis|buenas|buen d[ií]a|buenos d[ií]as|hey|ey|che)\b", Orden = 1,
                RespuestaTexto = "Hola {nombre}! Estoy con un celular probando cosas de la empresa, veo el monitor pero no puedo escribir bien. Ya te contesto.",
                RespuestaHtml = "Hola {nombre}! Estoy con un <b>celular</b> probando cosas de la empresa, veo el monitor pero no puedo escribir bien. Ya te contesto." },
            new Regla { Nombre = "pregunta", Patron = @"\?\s*$|pod[eé]s|puedes|sab[eé]s|ten[eé]s idea|me ayud", Orden = 2,
                RespuestaTexto = "{nombre}, lo vi. Estoy probando en el celular y en un rato te respondo bien. Si es urgente llamame.",
                RespuestaHtml = "{nombre}, lo vi. Estoy probando en el celular y <b>en un rato te respondo bien</b>. Si es urgente llamame." },
            new Regla { Nombre = "urgente", Patron = @"urgente|urgent|ya mismo|prod\b|producci[oó]n|ca[ií]d[oa]|no anda|se rompi", Orden = 0, EnfriamientoMinutos = 10,
                RespuestaTexto = "Vi que es urgente. Estoy con el celular, dejo todo y en 5 minutos te escribo.",
                RespuestaHtml = "<b>Vi que es urgente.</b> Estoy con el celular, dejo todo y en 5 minutos te escribo." },
        };
    }
}
