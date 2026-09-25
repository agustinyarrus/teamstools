using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace TeamsTools
{
    /// <summary>Ajustes en config.json (claves en castellano, editable a mano).</summary>
    internal sealed class Config
    {
        // ritmo
        public int IntervaloSegundos = 3;          // cada cuanto se lee Teams
        public int GraciaSegundos = 5;             // cuanto esperar con la sala vacia antes de salir
        public int PosponerMinutos = 10;           // "Quedarme": cuanto posponer
        // reglas
        public bool RequiereHaberVistoGente = true;   // solo salir si la sala TUVO gente y se vacio
        public int SalirSiNadieLlegaMinutos = 0;      // 0 = apagado; si nadie llega en N min, salir igual
        public bool VerificarConRoster = true;        // antes de salir, abrir "Gente" y confirmar el numero
        public string MetodoSalida = "uia";           // uia | teclado | ambos
        // comportamiento
        public bool Simulacion = false;               // no sale de verdad, solo avisa
        public bool Sonido = true;
        public bool Notificaciones = true;
        public bool MostrarCuentaRegresiva = true;
        public bool IniciarMinimizado = false;
        public bool IniciarConWindows = false;
        public bool LogDebug = false;
        public double EscalaFuente = 0.8;             // factor sobre todas las fuentes del panel (1.0 = tamaño base)
        public double EscalaUI = 0.8;                 // densidad: encoge TODA la interfaz (1.0 = original, 0.8 = 20% más chico)
        // presencia (no dejar que Teams me ponga Ausente por inactividad)
        public bool PresenciaSiempreOnline = true;
        public int PresenciaUmbralSegundos = 60;      // tras cuantos segundos sin tocar nada mando el toque
        public string PresenciaMetodo = "f15";         // f15 (tecla fantasma, sin mouse) | auto | mouse0 | mouse1
        public bool PresenciaEvitarSuspension = true;  // pedirle a Windows que no apague la pantalla (dormida = Ausente)
        public bool PresenciaVerificarEnTeams = true;  // leer el estado REAL del avatar de Teams y corregir si se fue a Ausente
        public bool PresenciaForzarEnTeams = true;     // si se fue a Ausente igual, fijar Disponible en el menú del avatar (invisible)
        // horario laboral: fuera de él no sostengo la presencia ni contesto solo
        public bool HorarioActivo = false;
        public string HorarioDesde = "09:00", HorarioHasta = "18:30";
        public string HorarioDias = "1,2,3,4,5";       // 0=domingo … 6=sábado
        public bool AtajoGlobal = true;                // Ctrl+Alt+C prende/apaga el modo automático desde cualquier lado
        // ventana: se recuerda dónde y de qué tamaño la dejaste (0 = todavía sin guardar)
        public int VentanaX, VentanaY, VentanaAncho, VentanaAlto;
        // identificacion
        public string MiNombre = "";                  // marca extra para excluir la ficha propia (opcional); tambien mi nombre en los chats
        public string MiCorreo = "";                  // para el deep link que despierta Teams (si esta vacio se toma del contacto "yo")
        public string[] PatronesPropio = { "mí mismo", "mi mismo", "myself", "yourself", "(Tú)", "(You)" };
        public string[] PatronesCompartido = { "Contenido compartido", "Content shared", "shared by", "compartido por", "está compartiendo", "is sharing" };
        // carpetas y herramientas de afuera (vacío = el default de cada una)
        public string CarpetaLogsTeams = "";          // logs nativos de Teams; vacío = %LOCALAPPDATA%\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\Logs
        public string HistorialJsonl = "";            // mensajes.jsonl del extractor; vacío = %TEMP%\teams-extraido\mensajes.jsonl
        public string CarpetaExtractor = "";          // dónde está actualizar_extraccion.py; vacío = tools\teams-chats al lado del exe
        /// <summary>La carpeta de logs configurada (con variables expandidas) o null para que el lector use la de Teams.</summary>
        [ScriptIgnore] public string CarpetaLogsTeamsONull => string.IsNullOrWhiteSpace(CarpetaLogsTeams) ? null : Environment.ExpandEnvironmentVariables(CarpetaLogsTeams.Trim());

        [ScriptIgnore] public string Ruta;

        public static Config Cargar(string ruta)
        {
            var c = new Config { Ruta = ruta };
            try
            {
                if (File.Exists(ruta))
                {
                    var js = new JavaScriptSerializer();
                    var d = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(ruta, Encoding.UTF8)) ?? new Dictionary<string, object>();
                    c.IntervaloSegundos = Ent(d, "intervaloSegundos", c.IntervaloSegundos, 1, 60);
                    c.GraciaSegundos = Ent(d, "graciaSegundos", c.GraciaSegundos, 0, 3600);
                    c.PosponerMinutos = Ent(d, "posponerMinutos", c.PosponerMinutos, 1, 240);
                    c.RequiereHaberVistoGente = Bool(d, "requiereHaberVistoGente", c.RequiereHaberVistoGente);
                    c.SalirSiNadieLlegaMinutos = Ent(d, "salirSiNadieLlegaMinutos", c.SalirSiNadieLlegaMinutos, 0, 600);
                    c.VerificarConRoster = Bool(d, "verificarConRoster", c.VerificarConRoster);
                    c.MetodoSalida = Str(d, "metodoSalida", c.MetodoSalida).ToLowerInvariant();
                    if (c.MetodoSalida != "uia" && c.MetodoSalida != "teclado" && c.MetodoSalida != "ambos") c.MetodoSalida = "uia";
                    c.Simulacion = Bool(d, "simulacion", c.Simulacion);
                    c.Sonido = Bool(d, "sonido", c.Sonido);
                    c.Notificaciones = Bool(d, "notificaciones", c.Notificaciones);
                    c.MostrarCuentaRegresiva = Bool(d, "mostrarCuentaRegresiva", c.MostrarCuentaRegresiva);
                    c.IniciarMinimizado = Bool(d, "iniciarMinimizado", c.IniciarMinimizado);
                    c.IniciarConWindows = Bool(d, "iniciarConWindows", c.IniciarConWindows);
                    c.LogDebug = Bool(d, "logDebug", c.LogDebug);
                    c.EscalaFuente = Dec(d, "escalaFuente", c.EscalaFuente, 0.5, 1.6);
                    c.EscalaUI = Dec(d, "escalaUI", c.EscalaUI, 0.6, 1.4);
                    c.PresenciaSiempreOnline = Bool(d, "presenciaSiempreOnline", c.PresenciaSiempreOnline);
                    c.PresenciaUmbralSegundos = Ent(d, "presenciaUmbralSegundos", c.PresenciaUmbralSegundos, 15, 280);
                    c.PresenciaMetodo = Str(d, "presenciaMetodo", c.PresenciaMetodo).ToLowerInvariant();
                    if (c.PresenciaMetodo != "auto" && c.PresenciaMetodo != "mouse0" && c.PresenciaMetodo != "mouse1" && c.PresenciaMetodo != "f15") c.PresenciaMetodo = "f15";
                    c.PresenciaEvitarSuspension = Bool(d, "presenciaEvitarSuspension", c.PresenciaEvitarSuspension);
                    c.PresenciaVerificarEnTeams = Bool(d, "presenciaVerificarEnTeams", c.PresenciaVerificarEnTeams);
                    c.PresenciaForzarEnTeams = Bool(d, "presenciaForzarEnTeams", c.PresenciaForzarEnTeams);
                    c.HorarioActivo = Bool(d, "horarioActivo", c.HorarioActivo);
                    c.HorarioDesde = Str(d, "horarioDesde", c.HorarioDesde);
                    c.HorarioHasta = Str(d, "horarioHasta", c.HorarioHasta);
                    c.HorarioDias = Str(d, "horarioDias", c.HorarioDias);
                    c.AtajoGlobal = Bool(d, "atajoGlobal", c.AtajoGlobal);
                    c.VentanaX = Ent(d, "ventanaX", 0, -20000, 20000); c.VentanaY = Ent(d, "ventanaY", 0, -20000, 20000);
                    c.VentanaAncho = Ent(d, "ventanaAncho", 0, 0, 20000); c.VentanaAlto = Ent(d, "ventanaAlto", 0, 0, 20000);
                    c.MiNombre = Str(d, "miNombre", c.MiNombre);
                    c.MiCorreo = Str(d, "miCorreo", c.MiCorreo);
                    c.PatronesPropio = Lista(d, "patronesPropio", c.PatronesPropio);
                    c.PatronesCompartido = Lista(d, "patronesCompartido", c.PatronesCompartido);
                    c.CarpetaLogsTeams = Str(d, "carpetaLogsTeams", c.CarpetaLogsTeams);
                    c.HistorialJsonl = Str(d, "historialJsonl", c.HistorialJsonl);
                    c.CarpetaExtractor = Str(d, "carpetaExtractor", c.CarpetaExtractor);
                }
            }
            catch { /* un json roto no tiene que tumbar la app: quedan los defaults */ }
            if (!File.Exists(ruta)) c.Guardar();
            return c;
        }

        public void Guardar()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"_ayuda\": \"Ajustes de TeamsTools. Tiempos en segundos salvo que diga minutos. metodoSalida: uia | teclado | ambos. presenciaMetodo: auto | mouse0 | mouse1 | f15. carpetaLogsTeams / historialJsonl / carpetaExtractor vacíos = el default.\",");
                sb.AppendLine($"  \"intervaloSegundos\": {IntervaloSegundos},");
                sb.AppendLine($"  \"graciaSegundos\": {GraciaSegundos},");
                sb.AppendLine($"  \"posponerMinutos\": {PosponerMinutos},");
                sb.AppendLine($"  \"requiereHaberVistoGente\": {B(RequiereHaberVistoGente)},");
                sb.AppendLine($"  \"salirSiNadieLlegaMinutos\": {SalirSiNadieLlegaMinutos},");
                sb.AppendLine($"  \"verificarConRoster\": {B(VerificarConRoster)},");
                sb.AppendLine($"  \"metodoSalida\": \"{MetodoSalida}\",");
                sb.AppendLine($"  \"simulacion\": {B(Simulacion)},");
                sb.AppendLine($"  \"sonido\": {B(Sonido)},");
                sb.AppendLine($"  \"notificaciones\": {B(Notificaciones)},");
                sb.AppendLine($"  \"mostrarCuentaRegresiva\": {B(MostrarCuentaRegresiva)},");
                sb.AppendLine($"  \"iniciarMinimizado\": {B(IniciarMinimizado)},");
                sb.AppendLine($"  \"iniciarConWindows\": {B(IniciarConWindows)},");
                sb.AppendLine($"  \"logDebug\": {B(LogDebug)},");
                sb.AppendLine($"  \"escalaFuente\": {EscalaFuente.ToString("0.##", CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"escalaUI\": {EscalaUI.ToString("0.##", CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"presenciaSiempreOnline\": {B(PresenciaSiempreOnline)},");
                sb.AppendLine($"  \"presenciaUmbralSegundos\": {PresenciaUmbralSegundos},");
                sb.AppendLine($"  \"presenciaMetodo\": \"{PresenciaMetodo}\",");
                sb.AppendLine($"  \"presenciaEvitarSuspension\": {B(PresenciaEvitarSuspension)},");
                sb.AppendLine($"  \"presenciaVerificarEnTeams\": {B(PresenciaVerificarEnTeams)},");
                sb.AppendLine($"  \"presenciaForzarEnTeams\": {B(PresenciaForzarEnTeams)},");
                sb.AppendLine($"  \"horarioActivo\": {B(HorarioActivo)},");
                sb.AppendLine($"  \"horarioDesde\": {Json(HorarioDesde)},");
                sb.AppendLine($"  \"horarioHasta\": {Json(HorarioHasta)},");
                sb.AppendLine($"  \"horarioDias\": {Json(HorarioDias)},");
                sb.AppendLine($"  \"atajoGlobal\": {B(AtajoGlobal)},");
                sb.AppendLine($"  \"ventanaX\": {VentanaX}, \"ventanaY\": {VentanaY}, \"ventanaAncho\": {VentanaAncho}, \"ventanaAlto\": {VentanaAlto},");
                sb.AppendLine($"  \"miNombre\": {Json(MiNombre)},");
                sb.AppendLine($"  \"miCorreo\": {Json(MiCorreo)},");
                sb.AppendLine($"  \"patronesPropio\": [{string.Join(", ", PatronesPropio.Select(Json))}],");
                sb.AppendLine($"  \"patronesCompartido\": [{string.Join(", ", PatronesCompartido.Select(Json))}],");
                sb.AppendLine($"  \"carpetaLogsTeams\": {Json(CarpetaLogsTeams)},");
                sb.AppendLine($"  \"historialJsonl\": {Json(HistorialJsonl)},");
                sb.AppendLine($"  \"carpetaExtractor\": {Json(CarpetaExtractor)}");
                sb.AppendLine("}");
                Disco.Escribir(Ruta, sb.ToString());
            }
            catch { }
        }

        static readonly string[] NombresDias = { "dom", "lun", "mar", "mié", "jue", "vie", "sáb" };

        List<int> DiasDelHorario()
        {
            var res = new List<int>();
            foreach (var s in (HorarioDias ?? "").Split(',', ';', ' '))
            { int d; if (int.TryParse(s.Trim(), out d) && d >= 0 && d <= 6 && !res.Contains(d)) res.Add(d); }
            return res;
        }

        static TimeSpan Hora(string s, int hDef)
        {
            TimeSpan t;
            return TimeSpan.TryParse((s ?? "").Trim(), CultureInfo.InvariantCulture, out t) ? t : new TimeSpan(hDef, 0, 0);
        }

        /// <summary>¿Estamos dentro del horario laboral configurado? Sin horario activo, siempre true.</summary>
        public bool EnHorario(DateTime? cuando = null)
        {
            if (!HorarioActivo) return true;
            var t = cuando ?? DateTime.Now;
            var dias = DiasDelHorario();
            if (dias.Count > 0 && !dias.Contains((int)t.DayOfWeek)) return false;
            var d1 = Hora(HorarioDesde, 9); var d2 = Hora(HorarioHasta, 18);
            var ahora = t.TimeOfDay;
            return d2 > d1 ? (ahora >= d1 && ahora <= d2) : (ahora >= d1 || ahora <= d2);   // soporta franjas que cruzan medianoche
        }

        public string HorarioTexto
        {
            get
            {
                if (!HorarioActivo) return "siempre activo";
                var dias = DiasDelHorario();
                string ds = dias.Count == 0 ? "todos los días" : dias.Count == 7 ? "todos los días" :
                            (dias.Count == 5 && dias.All(d => d >= 1 && d <= 5)) ? "lun a vie" : string.Join(",", dias.Select(d => NombresDias[d]));
                return $"{HorarioDesde}–{HorarioHasta} · {ds}";
            }
        }

        static string B(bool b) => b ? "true" : "false";
        public static string Json(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4")); else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
        static int Ent(Dictionary<string, object> d, string k, int def, int min, int max)
        {
            if (!d.TryGetValue(k, out var v) || v == null) return def;
            try { int n = Convert.ToInt32(v, CultureInfo.InvariantCulture); return Math.Max(min, Math.Min(max, n)); } catch { return def; }
        }
        static double Dec(Dictionary<string, object> d, string k, double def, double min, double max)
        {
            if (!d.TryGetValue(k, out var v) || v == null) return def;
            try { double x = Convert.ToDouble(v, CultureInfo.InvariantCulture); return Math.Max(min, Math.Min(max, x)); } catch { return def; }
        }
        static bool Bool(Dictionary<string, object> d, string k, bool def)
        {
            if (!d.TryGetValue(k, out var v) || v == null) return def;
            if (v is bool b) return b;
            var s = v.ToString().Trim().ToLowerInvariant();
            return s == "true" || s == "1" || s == "si" || s == "sí";
        }
        static string Str(Dictionary<string, object> d, string k, string def) => d.TryGetValue(k, out var v) && v != null ? v.ToString() : def;
        static string[] Lista(Dictionary<string, object> d, string k, string[] def)
        {
            if (!d.TryGetValue(k, out var v) || !(v is System.Collections.IEnumerable en) || v is string) return def;
            var l = new List<string>();
            foreach (var o in en) if (o != null && o.ToString().Length > 0) l.Add(o.ToString());
            return l.Count > 0 ? l.ToArray() : def;
        }
    }

    /// <summary>HKCU\...\Run: arranque con Windows (reversible, sin admin).</summary>
    internal static class Autoarranque
    {
        const string Clave = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Nombre = "TeamsTools";
        const string NombreViejo = "teams-autoleave";   // la app se llamó así hasta el 23-sep-2026
        public static bool Activo()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(Clave)) return k?.GetValue(Nombre) != null; } catch { return false; }
        }
        public static void Poner(bool activo, string exe)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Clave))
                {
                    if (activo) k.SetValue(Nombre, $"\"{exe}\" --min");
                    else if (k.GetValue(Nombre) != null) k.DeleteValue(Nombre);
                    if (k.GetValue(NombreViejo) != null) k.DeleteValue(NombreViejo);
                }
            }
            catch { }
        }

        /// <summary>
        /// El cambio de nombre (teams-autoleave → TeamsTools) no puede apagar el arranque con Windows: si estaba
        /// la entrada vieja, se reemplaza por la nueva; si la nueva apunta a otro exe (la carpeta se movió), se
        /// corrige. Devuelve true si tocó algo.
        /// </summary>
        public static bool Migrar(string exe)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Clave))
                {
                    bool habiaVieja = k.GetValue(NombreViejo) != null;
                    string actual = k.GetValue(Nombre) as string ?? "";
                    bool apuntaMal = actual.Length > 0 && actual.IndexOf(exe, StringComparison.OrdinalIgnoreCase) < 0;
                    if (!habiaVieja && !apuntaMal) return false;
                    k.SetValue(Nombre, $"\"{exe}\" --min");
                    if (habiaVieja) k.DeleteValue(NombreViejo);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
