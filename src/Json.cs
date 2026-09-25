using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace TeamsTools
{
    /// <summary>JSON legible a mano: lectura con JavaScriptSerializer, escritura propia con sangria.</summary>
    internal static class Json
    {
        public static Dictionary<string, object> LeerObjeto(string ruta)
        {
            try
            {
                if (!File.Exists(ruta)) return null;
                var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                return js.Deserialize<Dictionary<string, object>>(File.ReadAllText(ruta, Encoding.UTF8));
            }
            catch { return null; }
        }

        public static void Escribir(string ruta, object valor)
        {
            // se serializa YA (foto consistente del objeto) y la escritura atómica la hace Disco de fondo
            var sb = new StringBuilder();
            Serializar(sb, valor, 0);
            sb.AppendLine();
            Disco.Escribir(ruta, sb.ToString());
        }

        public static string Texto(object valor) { var sb = new StringBuilder(); Serializar(sb, valor, 0); return sb.ToString(); }

        static void Serializar(StringBuilder sb, object v, int nivel)
        {
            string sangria = new string(' ', nivel * 2), sangria2 = new string(' ', (nivel + 1) * 2);
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: sb.Append(Config.Json(s)); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case double d: sb.Append(d.ToString("0.###", CultureInfo.InvariantCulture)); return;
                case float f: sb.Append(f.ToString("0.###", CultureInfo.InvariantCulture)); return;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); return;
                case DateTime dt: sb.Append(Config.Json(dt.ToString("yyyy-MM-dd HH:mm:ss"))); return;
                case Enum e: sb.Append(Config.Json(e.ToString())); return;
                case IDictionary dic:
                    {
                        if (dic.Count == 0) { sb.Append("{}"); return; }
                        sb.Append("{\n");
                        int k = 0;
                        foreach (DictionaryEntry de in dic)
                        {
                            sb.Append(sangria2).Append(Config.Json(de.Key.ToString())).Append(": ");
                            Serializar(sb, de.Value, nivel + 1);
                            sb.Append(++k < dic.Count ? ",\n" : "\n");
                        }
                        sb.Append(sangria).Append('}');
                        return;
                    }
                case IEnumerable en:
                    {
                        var items = en.Cast<object>().ToList();
                        if (items.Count == 0) { sb.Append("[]"); return; }
                        bool simples = items.All(x => x == null || x is string || x is bool || x is int || x is long || x is double);
                        if (simples && items.Count <= 12)
                        {
                            sb.Append('[');
                            for (int i = 0; i < items.Count; i++) { if (i > 0) sb.Append(", "); Serializar(sb, items[i], nivel + 1); }
                            sb.Append(']');
                            return;
                        }
                        sb.Append("[\n");
                        for (int i = 0; i < items.Count; i++)
                        {
                            sb.Append(sangria2);
                            Serializar(sb, items[i], nivel + 1);
                            sb.Append(i < items.Count - 1 ? ",\n" : "\n");
                        }
                        sb.Append(sangria).Append(']');
                        return;
                    }
                default:
                    {
                        // objeto plano: propiedades y campos publicos, en orden de declaracion
                        var t = v.GetType();
                        var pares = new List<KeyValuePair<string, object>>();
                        foreach (var f in t.GetFields()) if (!f.IsStatic && !Attribute.IsDefined(f, typeof(ScriptIgnoreAttribute))) pares.Add(new KeyValuePair<string, object>(Camel(f.Name), f.GetValue(v)));
                        foreach (var p in t.GetProperties()) if (p.CanRead && p.GetIndexParameters().Length == 0 && !Attribute.IsDefined(p, typeof(ScriptIgnoreAttribute))) pares.Add(new KeyValuePair<string, object>(Camel(p.Name), p.GetValue(v, null)));
                        var d = new Dictionary<string, object>();
                        foreach (var kv in pares) d[kv.Key] = kv.Value;
                        Serializar(sb, d, nivel);
                        return;
                    }
            }
        }

        static string Camel(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);

        // --- lectura tolerante ---
        public static string S(Dictionary<string, object> d, string k, string def = "") => d != null && d.TryGetValue(k, out var v) && v != null ? v.ToString() : def;
        public static int I(Dictionary<string, object> d, string k, int def = 0) { try { return d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : def; } catch { return def; } }
        public static bool B(Dictionary<string, object> d, string k, bool def = false)
        {
            if (d == null || !d.TryGetValue(k, out var v) || v == null) return def;
            if (v is bool b) return b;
            var s = v.ToString().Trim().ToLowerInvariant();
            return s == "true" || s == "1" || s == "si" || s == "sí";
        }
        public static DateTime? F(Dictionary<string, object> d, string k)
        {
            var s = S(d, k, "");
            if (s.Length == 0) return null;
            if (DateTime.TryParseExact(s, new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return dt;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return dt;
            return null;
        }
        public static List<Dictionary<string, object>> Lista(Dictionary<string, object> d, string k)
        {
            var res = new List<Dictionary<string, object>>();
            if (d == null || !d.TryGetValue(k, out var v) || !(v is IEnumerable en) || v is string) return res;
            foreach (var o in en) if (o is Dictionary<string, object> od) res.Add(od);
            return res;
        }
        public static string[] Cadenas(Dictionary<string, object> d, string k)
        {
            var res = new List<string>();
            if (d == null || !d.TryGetValue(k, out var v) || !(v is IEnumerable en) || v is string) return res.ToArray();
            foreach (var o in en) if (o != null) res.Add(o.ToString());
            return res.ToArray();
        }
    }
}
