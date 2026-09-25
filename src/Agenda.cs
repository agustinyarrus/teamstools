using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TeamsTools
{
    /// <summary>Un recordatorio programado: a quien, cuando, que.</summary>
    internal sealed class Recordatorio
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Para = "";           // nombre Teams ("Rivas, Valentina") o "yo"
        public string Correo = "";         // para el deep link, si se sabe
        public string Cuando = "";         // expresion original ("mañana 10:00", "cada lun,mie 09:30")
        public DateTime? Proximo;          // proxima ejecucion calculada
        public string Repetir = "";        // "" | diario | laborables | semanal:1,3,5 (0=domingo)
        public string Hora = "";           // "09:30" para las repeticiones
        public string TextoHtml = "";       // primera parte de texto (compatibilidad con exes viejos)
        public string TextoPlano = "";
        public string TextoRtf = "";
        /// <summary>El mensaje completo: puede ser varias burbujas (textos + imágenes).</summary>
        public Envio Mensaje = new Envio();
        public bool Activo = true;
        public bool ParaMi = false;        // aviso propio (globo + sonido) en vez de mensaje a otro
        public DateTime? UltimoEnvio;
        public string UltimoResultado = "";
        public int Enviados = 0;
        public int Fallos = 0;
        public bool EsRecurrente => Repetir.Length > 0;

        /// <summary>Cada disparo del recordatorio, con su resultado. Sirve para ver por qué falló algo hace tres días.</summary>
        public List<Disparo> Historial = new List<Disparo>();

        /// <summary>Los próximos n disparos calculados. Es el "qué pasa si" mientras se edita: se ve antes de guardar.</summary>
        public List<DateTime> Proximas(int n, DateTime? desde = null)
        {
            var res = new List<DateTime>();
            var d = desde ?? DateTime.Now;
            if (Proximo != null && Proximo.Value > d) { res.Add(Proximo.Value); d = Proximo.Value; }
            if (!EsRecurrente) return res;
            for (int i = 0; i < n * 3 && res.Count < n; i++)
            {
                var p = Agenda.Proxima(d, Repetir, Hora);
                if (p == null || p.Value <= d) break;
                res.Add(p.Value); d = p.Value;
            }
            return res.Take(n).ToList();
        }

        /// <summary>Cuántos de los últimos disparos salieron bien (para la columna de fiabilidad).</summary>
        public int PorcentajeOk()
        {
            int n = Enviados + Fallos;
            return n == 0 ? -1 : (int)Math.Round(Enviados * 100.0 / n);
        }

        public void Anotar(bool ok, string detalle, int ms)
        {
            Historial.Add(new Disparo { Cuando = DateTime.Now, Ok = ok, Detalle = detalle ?? "", Ms = ms });
            while (Historial.Count > 30) Historial.RemoveAt(0);
        }

        /// <summary>El envío a mandar: las partes si las hay, si no una sola burbuja con los campos de siempre.</summary>
        public Envio MensajeEfectivo()
        {
            if (Mensaje != null && Mensaje.Partes.Count > 0) return Mensaje;
            string html = TextoHtml.Length > 0 ? TextoHtml : System.Net.WebUtility.HtmlEncode(TextoPlano).Replace("\n", "<br>");
            return Envio.DeTexto(html, TextoPlano, TextoRtf);
        }

        /// <summary>Deja TextoHtml/Plano/Rtf con la primera parte de texto, para no perder nada con un exe viejo.</summary>
        public void SincronizarCamposViejos()
        {
            if (Mensaje == null) { Mensaje = new Envio(); return; }
            if (Mensaje.Partes.Count == 0) return;
            TextoHtml = Mensaje.PrimerHtml();
            TextoPlano = Mensaje.PrimerPlano();
            string rtf = Mensaje.PrimerRtf();
            if (rtf.Length > 0) TextoRtf = rtf;
        }

        public string ResumenCuando()
        {
            if (Proximo == null) return "sin fecha";
            var p = Proximo.Value;
            string dia = p.Date == DateTime.Today ? "hoy" : p.Date == DateTime.Today.AddDays(1) ? "mañana" : p.ToString("ddd dd/MM", Agenda.Es);
            string s = $"{dia} {p:HH:mm}";
            if (Repetir.Length > 0) s += " · " + Agenda.Leer(Repetir, "");
            return s;
        }
    }

    /// <summary>Un disparo del cron: cuándo, si salió y qué pasó.</summary>
    internal sealed class Disparo
    {
        public DateTime Cuando;
        public bool Ok;
        public string Detalle = "";
        public int Ms;
    }

    /// <summary>Parser de expresiones de tiempo en castellano y calculo de la proxima ocurrencia.</summary>
    internal static class Agenda
    {
        public static readonly CultureInfo Es = new CultureInfo("es-AR");
        static readonly string[] Dias = { "domingo", "lunes", "martes", "miercoles", "jueves", "viernes", "sabado" };
        static readonly string[] Cortos = { "dom", "lun", "mar", "mie", "jue", "vie", "sab" };
        public static string DiaCorto(int d) => Cortos[((d % 7) + 7) % 7];

        public sealed class Resultado { public DateTime? Proximo; public string Repetir = ""; public string Hora = ""; public string Error = ""; public string Lectura = ""; }

        /// <summary>
        /// Entiende: "en 45 min", "en 2 h", "hoy 15:30", "mañana 10", "pasado mañana 9:00", "17/09 10:00",
        /// "17/9/2026 10:00", "2026-09-17 10:00", "lunes 09:30", "el viernes 15:00", "cada día 09:00",
        /// "todos los días 9", "cada laborable 09:15", "lun a vie 9:15", "cada lunes 09:30", "cada lun,mie,vie 10:00".
        /// </summary>
        public static Resultado Parsear(string expr, DateTime ahora)
        {
            var r = new Resultado();
            string s = Contactos.Normalizar(expr ?? "").Replace("hs", "").Replace("hrs", "").Trim();
            if (s.Length == 0) { r.Error = "escribí cuándo"; return r; }
            try
            {
                Match m;
                // relativo
                m = Regex.Match(s, @"^en\s+(\d+)\s*(min|minutos?|m|h|horas?|hora|d|dias?|dia)$");
                if (m.Success)
                {
                    int n = int.Parse(m.Groups[1].Value); string u = m.Groups[2].Value;
                    r.Proximo = u.StartsWith("m") ? ahora.AddMinutes(n) : u.StartsWith("h") ? ahora.AddHours(n) : ahora.AddDays(n);
                    r.Lectura = "una sola vez"; return r;
                }
                // repeticiones
                m = Regex.Match(s, @"^(cada|todos los)\s+(dia|dias)\s+(a las\s+)?(.+)$");
                if (m.Success) { r.Repetir = "diario"; r.Hora = Hora(m.Groups[4].Value); r.Proximo = Proxima(ahora, r.Repetir, r.Hora); r.Lectura = "todos los días"; return r; }
                m = Regex.Match(s, @"^(cada\s+laborables?|cada\s+dia\s+habil|lun\s+a\s+vie|de\s+lunes\s+a\s+viernes|laborables)\s+(a las\s+)?(.+)$");
                if (m.Success) { r.Repetir = "laborables"; r.Hora = Hora(m.Groups[3].Value); r.Proximo = Proxima(ahora, r.Repetir, r.Hora); r.Lectura = "de lunes a viernes"; return r; }
                m = Regex.Match(s, @"^(cada|todos los)\s+([a-z,\s]+?)\s+(a las\s+)?(\d{1,2}(?:[:.]\d{2})?)$");
                if (m.Success)
                {
                    var dias = DiasDe(m.Groups[2].Value);
                    if (dias.Count == 0) { r.Error = "no entiendo qué días"; return r; }
                    r.Repetir = "semanal:" + string.Join(",", dias);
                    r.Hora = Hora(m.Groups[4].Value); r.Proximo = Proxima(ahora, r.Repetir, r.Hora); r.Lectura = "cada " + string.Join(", ", dias.Select(DiaCorto)); return r;
                }
                // cada N dias
                m = Regex.Match(s, @"^cada\s+(\d{1,3})\s*(dias?|d)\s+(a las\s+)?(\d{1,2}(?:[:.]\d{2})?)$");
                if (m.Success)
                {
                    r.Repetir = "cada:" + int.Parse(m.Groups[1].Value);
                    r.Hora = Hora(m.Groups[4].Value); r.Proximo = Proxima(ahora, r.Repetir, r.Hora);
                    r.Lectura = "cada " + m.Groups[1].Value + " días"; return r;
                }
                // el N de cada mes
                m = Regex.Match(s, @"^(el\s+)?(\d{1,2})\s+de\s+cada\s+mes\s+(a las\s+)?(\d{1,2}(?:[:.]\d{2})?)$");
                if (m.Success)
                {
                    int dm = int.Parse(m.Groups[2].Value);
                    if (dm < 1 || dm > 31) { r.Error = "día del mes inválido"; return r; }
                    r.Repetir = "mensual:" + dm;
                    r.Hora = Hora(m.Groups[4].Value); r.Proximo = Proxima(ahora, r.Repetir, r.Hora);
                    r.Lectura = "el " + dm + " de cada mes"; return r;
                }
                // el ultimo viernes del mes
                m = Regex.Match(s, @"^(el\s+)?ultimo\s+([a-z]+)\s+(del\s+mes\s+)?(a las\s+)?(\d{1,2}(?:[:.]\d{2})?)$");
                if (m.Success && DiaIndice(m.Groups[2].Value) >= 0)
                {
                    r.Repetir = "ultimo:" + DiaIndice(m.Groups[2].Value);
                    r.Hora = Hora(m.Groups[5].Value); r.Proximo = Proxima(ahora, r.Repetir, r.Hora);
                    r.Lectura = "el último " + m.Groups[2].Value + " del mes"; return r;
                }
                // un dia de la semana
                m = Regex.Match(s, @"^(el\s+|este\s+|proximo\s+)?([a-z]+)\s+(a las\s+)?(\d{1,2}(?:[:.]\d{2})?)$");
                if (m.Success && DiaIndice(m.Groups[2].Value) >= 0)
                {
                    int d = DiaIndice(m.Groups[2].Value); var h = HoraTs(m.Groups[4].Value);
                    var fecha = ahora.Date;
                    while ((int)fecha.DayOfWeek != d || fecha + h <= ahora) fecha = fecha.AddDays(1);
                    r.Proximo = fecha + h; r.Lectura = "una sola vez"; return r;
                }
                // hoy / mañana / pasado
                m = Regex.Match(s, @"^(hoy|manana|pasado manana)\s+(a las\s+)?(\d{1,2}(?:[:.]\d{2})?)$");
                if (m.Success)
                {
                    int off = m.Groups[1].Value == "hoy" ? 0 : m.Groups[1].Value == "manana" ? 1 : 2;
                    r.Proximo = ahora.Date.AddDays(off) + HoraTs(m.Groups[3].Value);
                    if (r.Proximo <= ahora) { r.Error = "esa hora ya pasó"; }
                    r.Lectura = "una sola vez"; return r;
                }
                // fecha explicita
                m = Regex.Match(s, @"^(\d{1,2})[/\-](\d{1,2})(?:[/\-](\d{2,4}))?\s+(a las\s+)?(\d{1,2}(?:[:.]\d{2})?)$");
                if (m.Success)
                {
                    int dd = int.Parse(m.Groups[1].Value), mm = int.Parse(m.Groups[2].Value);
                    int yy = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : ahora.Year; if (yy < 100) yy += 2000;
                    var f = new DateTime(yy, mm, dd) + HoraTs(m.Groups[5].Value);
                    if (!m.Groups[3].Success && f <= ahora) f = f.AddYears(1);
                    r.Proximo = f; r.Lectura = "una sola vez"; return r;
                }
                m = Regex.Match(s, @"^(\d{4})-(\d{2})-(\d{2})[\sT]+(\d{1,2}(?:[:.]\d{2})?)$");
                if (m.Success) { r.Proximo = new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) + HoraTs(m.Groups[4].Value); r.Lectura = "una sola vez"; return r; }
                // solo una hora: hoy si falta, si no mañana
                m = Regex.Match(s, @"^(a las\s+)?(\d{1,2}(?:[:.]\d{2})?)$");
                if (m.Success)
                {
                    var f = ahora.Date + HoraTs(m.Groups[2].Value);
                    if (f <= ahora) f = f.AddDays(1);
                    r.Proximo = f; r.Lectura = f.Date == ahora.Date ? "hoy" : "mañana"; return r;
                }
                r.Error = "no entiendo: mañana 10:00 · en 45 min · lunes 9:30 · cada laborable 9:15 · cada 3 días 10:00 · el 15 de cada mes 9:00 · el último viernes 17:00 · 17/09 15:00";
            }
            catch (Exception ex) { r.Error = ex.Message; }
            return r;
        }

        static string Hora(string h) { var ts = HoraTs(h); return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}"; }
        static TimeSpan HoraTs(string h)
        {
            h = h.Trim().Replace('.', ':');
            var p = h.Split(':');
            int hh = int.Parse(p[0]), mm = p.Length > 1 ? int.Parse(p[1]) : 0;
            if (hh > 23 || mm > 59) throw new Exception("hora inválida");
            return new TimeSpan(hh, mm, 0);
        }
        static int DiaIndice(string d)
        {
            d = d.Trim();
            for (int i = 0; i < 7; i++) if (Dias[i] == d || Cortos[i] == d || Dias[i].StartsWith(d) && d.Length >= 3) return i;
            return -1;
        }
        static List<int> DiasDe(string lista)
        {
            var res = new List<int>();
            foreach (var t in Regex.Split(lista, @"[,\s]+|\sy\s")) { int i = DiaIndice(t.Replace("y", "").Trim()); if (i >= 0 && !res.Contains(i)) res.Add(i); }
            return res.OrderBy(x => x).ToList();
        }

        /// <summary>
        /// Proxima ocurrencia de una repeticion despues de 'desde'. Formas soportadas:
        ///   diario · laborables · semanal:1,3,5 · cadaN:3 (cada 3 dias) · mensual:15 (dia 15) · ultimo:5 (ultimo viernes del mes)
        /// Busca hasta 400 dias para que "el ultimo viernes" y "el 31" no se queden sin encontrar el mes que toca.
        /// </summary>
        public static DateTime? Proxima(DateTime desde, string repetir, string hora)
        {
            if (string.IsNullOrEmpty(repetir) || string.IsNullOrEmpty(hora)) return null;
            var h = HoraTs(hora);
            var f = desde.Date + h;
            if (f <= desde) f = f.AddDays(1);
            int cadaN = repetir.StartsWith("cada:") && int.TryParse(repetir.Substring(5), out int cn) ? Math.Max(1, cn) : 0;
            for (int i = 0; i < 400; i++, f = f.AddDays(1))
            {
                int d = (int)f.DayOfWeek;
                if (repetir == "diario") return f;
                if (repetir == "laborables" && d >= 1 && d <= 5) return f;
                if (repetir.StartsWith("semanal:") && Dias_(repetir.Substring(8)).Contains(d)) return f;
                // cada N dias: se cuenta desde la fecha de referencia, no desde hoy, para que no se corra al reprogramar
                if (cadaN > 0 && ((f.Date - desde.Date).Days % cadaN == 0) && f > desde) return f;
                if (repetir.StartsWith("mensual:") && int.TryParse(repetir.Substring(8), out int dm))
                {
                    int ultimo = DateTime.DaysInMonth(f.Year, f.Month);
                    if (f.Day == Math.Min(dm, ultimo)) return f;          // el 31 en febrero cae el 28/29
                }
                if (repetir.StartsWith("ultimo:") && int.TryParse(repetir.Substring(7), out int ds) && d == ds)
                {
                    if (f.AddDays(7).Month != f.Month) return f;          // no hay otro igual este mes ⇒ es el ultimo
                }
            }
            return null;
        }

        static List<int> Dias_(string csv)
        {
            var r = new List<int>();
            foreach (var t in csv.Split(',', ';')) if (int.TryParse(t.Trim(), out int v)) r.Add(v);
            return r;
        }

        /// <summary>Cómo se lee una repetición, en castellano.</summary>
        public static string Leer(string repetir, string hora)
        {
            if (string.IsNullOrEmpty(repetir)) return "una sola vez";
            string h = hora.Length > 0 ? " a las " + hora : "";
            if (repetir == "diario") return "todos los días" + h;
            if (repetir == "laborables") return "de lunes a viernes" + h;
            if (repetir.StartsWith("semanal:")) return "cada " + string.Join(", ", Dias_(repetir.Substring(8)).Select(DiaCorto)) + h;
            if (repetir.StartsWith("cada:")) return "cada " + repetir.Substring(5) + " días" + h;
            if (repetir.StartsWith("mensual:")) return "el " + repetir.Substring(8) + " de cada mes" + h;
            if (repetir.StartsWith("ultimo:") && int.TryParse(repetir.Substring(7), out int d)) return "el último " + Dias[((d % 7) + 7) % 7] + " del mes" + h;
            return repetir;
        }
    }

    /// <summary>Lista de recordatorios persistida en recordatorios.json.</summary>
    internal sealed class Recordatorios
    {
        public List<Recordatorio> Lista = new List<Recordatorio>();
        string ruta = "";

        public static Recordatorios Cargar(string ruta)
        {
            var c = new Recordatorios { ruta = ruta };
            var d = Json.LeerObjeto(ruta);
            if (d != null)
                foreach (var o in Json.Lista(d, "recordatorios"))
                {
                    var r = new Recordatorio
                    {
                        Id = Json.S(o, "id", Guid.NewGuid().ToString("N").Substring(0, 8)), Para = Json.S(o, "para"), Correo = Json.S(o, "correo"), Cuando = Json.S(o, "cuando"),
                        Proximo = Json.F(o, "proximo"), Repetir = Json.S(o, "repetir"), Hora = Json.S(o, "hora"), TextoHtml = Json.S(o, "textoHtml"), TextoPlano = Json.S(o, "textoPlano"),
                        TextoRtf = Json.S(o, "textoRtf"), Activo = Json.B(o, "activo", true), ParaMi = Json.B(o, "paraMi", false), UltimoEnvio = Json.F(o, "ultimoEnvio"),
                        UltimoResultado = Json.S(o, "ultimoResultado"), Enviados = Json.I(o, "enviados"), Fallos = Json.I(o, "fallos")
                    };
                    foreach (var hd in Json.Lista(o, "historial"))
                        r.Historial.Add(new Disparo { Cuando = Json.F(hd, "cuando") ?? DateTime.MinValue, Ok = Json.B(hd, "ok"), Detalle = Json.S(hd, "detalle"), Ms = Json.I(hd, "ms") });
                    r.Mensaje = Envio.Leer(o, r.TextoHtml, r.TextoPlano, r.TextoRtf);
                    c.Lista.Add(r);
                }
            return c;
        }

        public void Guardar()
        {
            if (ruta.Length == 0) return;
            try
            {
                Json.Escribir(ruta, new Dictionary<string, object>
                {
                    ["_ayuda"] = "Recordatorios a compañeros por Teams (o a vos mismo). 'cuando' acepta: mañana 10:00 · en 45 min · lunes 9:30 · cada laborable 9:15 · cada lun,mie 10:00 · 17/09 15:00",
                    ["recordatorios"] = Lista.Select(r =>
                    {
                        r.SincronizarCamposViejos();
                        return new Dictionary<string, object>
                        {
                            ["id"] = r.Id, ["para"] = r.Para, ["correo"] = r.Correo, ["cuando"] = r.Cuando, ["proximo"] = r.Proximo, ["repetir"] = r.Repetir, ["hora"] = r.Hora,
                            ["textoPlano"] = r.TextoPlano, ["textoHtml"] = r.TextoHtml, ["textoRtf"] = r.TextoRtf, ["partes"] = r.Mensaje.Guardar(),
                            ["activo"] = r.Activo, ["paraMi"] = r.ParaMi,
                            ["ultimoEnvio"] = r.UltimoEnvio, ["ultimoResultado"] = r.UltimoResultado, ["enviados"] = r.Enviados, ["fallos"] = r.Fallos,
                            ["historial"] = r.Historial.Select(d => new Dictionary<string, object> { ["cuando"] = d.Cuando, ["ok"] = d.Ok, ["detalle"] = d.Detalle, ["ms"] = d.Ms }).ToList()
                        };
                    }).ToList()
                });
            }
            catch { }
        }

        public IEnumerable<Recordatorio> Pendientes(DateTime ahora) => Lista.Where(r => r.Activo && r.Proximo != null && r.Proximo.Value <= ahora).OrderBy(r => r.Proximo);
        public IEnumerable<Recordatorio> Proximos() => Lista.Where(r => r.Activo && r.Proximo != null).OrderBy(r => r.Proximo);
    }
}
