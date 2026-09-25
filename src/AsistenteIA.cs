using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace TeamsTools
{
    /// <summary>
    /// Puente opcional con el LLM local (`Box\llm-local`, llama.cpp con API compatible con OpenAI en 127.0.0.1:8080).
    ///
    /// 🚨 Honestidad sobre la latencia, que es lo que decide si esto sirve: el cuello de la notebook no es la RAM
    /// sino **25 GB/s de ancho de banda**, y cada token lee los pesos activos ⇒ `tok/s ≈ 25 / GB_por_token`.
    ///   · Qwen3.8-27B en Q8 (~29 GB) → ~0,85 tok/s ⇒ una respuesta de 40 tokens tarda **~47 s**. No sirve para
    ///     autocontestar; sirve para pensar algo largo mientras hacés otra cosa.
    ///   · Qwen3.5-0.8B en Q2 (~0,4 GB) → decenas de tok/s ⇒ **ese sí** sirve para una respuesta corta.
    /// Por eso `Disponible` devuelve el modelo cargado y los tok/s medidos: el toggle de la UI tiene que poder
    /// mostrar «~47 s por respuesta» ANTES de que el user lo prenda, no después.
    ///
    /// Nunca se levanta el servidor solo: cargar 31 GB sin querer deja la máquina inutilizable. Si está apagado,
    /// se dice cómo prenderlo y listo.
    /// </summary>
    internal static class AsistenteIA
    {
        public static string Url = "http://127.0.0.1:8080";
        public static int TimeoutMs = 120000;

        static readonly object candado = new object();
        static DateTime ultimaSonda = DateTime.MinValue;
        static bool ultimaRespuesta;
        static string ultimoDetalle = "consultando al modelo local…";
        static string modeloCargado = "";
        static double tokPorSeg;
        static int sondeando;

        /// <summary>Modelo cargado, si lo sabemos.</summary>
        public static string Modelo => modeloCargado;
        /// <summary>Velocidad medida en la última generación real, 0 si todavía no hubo ninguna.</summary>
        public static double TokensPorSegundo => tokPorSeg;
        /// <summary>Hay una sonda en vuelo: la UI muestra «consultando…» en vez de un dato viejo.</summary>
        public static bool Consultando => Volatile.Read(ref sondeando) != 0;
        /// <summary>Cambió lo que se sabe del servidor. Llega desde un hilo de fondo: marshalear a la UI.</summary>
        public static event Action Cambio;

        /// <summary>
        /// Para la UI: NUNCA bloquea. Devuelve lo último que se sabe y, si tiene más de 20 s, larga una sonda de
        /// fondo (una sola a la vez) que avisa por <see cref="Cambio"/>.
        /// 🚨 Antes sondeaba en el hilo de la interfaz con 2,5 s de timeout: con el servidor apagado, Windows
        ///    reintenta el SYN al puerto cerrado y la pestaña se congelaba ~2 s cada 20 s.
        /// </summary>
        public static bool Disponible(out string detalle)
        {
            bool viejo;
            lock (candado) { viejo = (DateTime.Now - ultimaSonda).TotalSeconds >= 20; detalle = ultimoDetalle; }
            if (viejo && Interlocked.CompareExchange(ref sondeando, 1, 0) == 0)
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    bool antes; lock (candado) antes = ultimaRespuesta;
                    string d;
                    bool ahora;
                    try { ahora = Sondear(out d); }
                    finally { Volatile.Write(ref sondeando, 0); }
                    try { Cambio?.Invoke(); } catch { }
                });
            lock (candado) return ultimaRespuesta;
        }

        /// <summary>
        /// Para hilos de fondo (autocontestador, pipeline, línea de comando): la respuesta de verdad. Usa el
        /// caché de 20 s y si está viejo sondea en el momento (hasta 2,5 s). Jamás llamarlo desde la UI.
        /// </summary>
        public static bool Verificar(out string detalle)
        {
            lock (candado)
                if ((DateTime.Now - ultimaSonda).TotalSeconds < 20) { detalle = ultimoDetalle; return ultimaRespuesta; }
            return Sondear(out detalle);
        }

        static bool Sondear(out string detalle)
        {
            bool ok; string det;
            try
            {
                string cuerpo = Pedir(Url + "/props", null, 2500);
                string modelo = Extraer(cuerpo, "model_path");
                if (modelo.Length == 0) modelo = Extraer(cuerpo, "model");
                modeloCargado = modelo;
                string corto = modelo.Length > 0 ? Path.GetFileNameWithoutExtension(modelo) : "modelo desconocido";
                ok = true;
                det = corto + (tokPorSeg > 0 ? $" · {tokPorSeg:0.#} tok/s medidos · ~{Math.Ceiling(40 / Math.Max(0.1, tokPorSeg))} s por respuesta de 40 tokens" : " · sin medir todavía");
            }
            catch (Exception ex)
            {
                ok = false;
                bool rechazado = ex is WebException && ((WebException)ex).Status == WebExceptionStatus.ConnectFailure;
                det = rechazado
                    ? "el servidor local no está levantado — arrancá llama-server con un .gguf en el puerto 8080 (es opcional)"
                    : "no pude hablar con el servidor: " + ex.Message;
            }
            lock (candado) { ultimaSonda = DateTime.Now; ultimaRespuesta = ok; ultimoDetalle = det; }
            detalle = det;
            return ok;
        }

        /// <summary>
        /// Redacta una respuesta corta para un mensaje entrante. Devuelve "" si no se pudo, con el motivo en
        /// `detalle`. No inventa datos: la instrucción le prohíbe explícitamente prometer cosas concretas.
        /// </summary>
        public static string Redactar(string entrante, string autor, string instruccion, int maxTokens, out string detalle)
        {
            detalle = "";
            string porque;
            if (!Verificar(out porque)) { detalle = porque; return ""; }

            string sistema =
                "Sos el asistente de " + (autor ?? "un colega") + " y escribís su respuesta en un chat de trabajo, en castellano rioplatense. " +
                "Reglas: respondé en 1 o 2 oraciones, sin saludos largos, sin firmar, sin emojis. " +
                "NUNCA prometas fechas, números, versiones ni resultados concretos: si hace falta un dato que no está en el mensaje, decí que lo confirmás después. " +
                "Si el mensaje no pide nada, contestá con un acuse breve.";
            if (!string.IsNullOrWhiteSpace(instruccion)) sistema += " Indicación extra: " + instruccion.Trim();

            var json = new StringBuilder();
            json.Append("{\"messages\":[");
            json.Append("{\"role\":\"system\",\"content\":\"").Append(Esc(sistema)).Append("\"},");
            json.Append("{\"role\":\"user\",\"content\":\"").Append(Esc((entrante ?? "").Trim())).Append("\"}],");
            json.Append("\"max_tokens\":").Append(Math.Max(16, Math.Min(400, maxTokens)));
            // 🚨🚨 Los Qwen3 y afines RAZONAN por defecto. Si el servidor arrancó con `--jinja`, llama.cpp
            //    manda ese razonamiento a `reasoning_content` y deja **`content` VACÍO**: la respuesta se
            //    pierde entera y parece que el modelo se colgó. Medido: sin esta línea, pedirle «decí hola»
            //    a Qwen3.5-2B devuelve content="" con finish_reason="length" tras quemar 200 tokens
            //    pensando; con ella contesta «¡Hola! ¿En qué puedo ayudarte?» y termina en "stop".
            //    Los modelos que no piensan ignoran la variable, así que ponerla siempre es seguro.
            json.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
            json.Append(",\"temperature\":0.7,\"top_p\":0.8,\"stream\":false}");

            var reloj = Stopwatch.StartNew();
            string resp;
            // el modelo puede tardar decenas de segundos: que se vea que está pensando, no que se colgó
            using (Tareas.Empezar("el modelo local está pensando",
                       (modeloCargado.Length > 0 ? Path.GetFileNameWithoutExtension(modeloCargado) : "modelo local") +
                       (tokPorSeg > 0 ? $" · ~{Math.Ceiling(maxTokens / Math.Max(0.1, tokPorSeg))} s estimados" : ""), Tema.Malva))
            {
                try { resp = Pedir(Url + "/v1/chat/completions", json.ToString(), TimeoutMs); }
                catch (Exception ex) { detalle = "el modelo no contestó: " + ex.Message; return ""; }
            }
            reloj.Stop();

            string texto = Contenido(resp);
            if (texto.Length == 0)
            {
                // si vino razonamiento pero no respuesta, se quedó pensando: decirlo, no dejarlo en «vacío»
                detalle = resp.IndexOf("\"reasoning_content\"", StringComparison.Ordinal) >= 0
                    ? "el modelo se quedó pensando y no contestó (subí max_tokens)"
                    : "el modelo contestó vacío";
                return "";
            }

            // 🚨 los modelos con «pensamiento» devuelven el razonamiento entre <think>…</think>: eso no se manda a nadie
            texto = ReThink.Replace(texto, "").Trim();
            texto = texto.Trim('"', '“', '”', ' ', '\n', '\r');

            double tokens = Numero(resp, "completion_tokens");
            if (tokens > 0 && reloj.Elapsed.TotalSeconds > 0.2) tokPorSeg = tokens / reloj.Elapsed.TotalSeconds;
            detalle = $"{(int)reloj.ElapsedMilliseconds} ms" + (tokens > 0 ? $" · {tokens:0} tokens · {tokPorSeg:0.#} tok/s" : "");
            return texto;
        }

        static readonly Regex ReThink = new Regex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ------------------------------------------------------------------ plomería
        static string Pedir(string url, string cuerpo, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.Proxy = null;                 // 🚨 el proxy corporativo se come el localhost si no se lo anula
            if (cuerpo != null)
            {
                req.Method = "POST";
                req.ContentType = "application/json";
                var datos = new UTF8Encoding(false).GetBytes(cuerpo);
                req.ContentLength = datos.Length;
                using (var s = req.GetRequestStream()) s.Write(datos, 0, datos.Length);
            }
            using (var r = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        static string Esc(string s) => (s ?? "")
            .Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n").Replace("\t", " ");

        /// <summary>Saca el `content` de la respuesta sin meter un parser JSON entero: alcanza y sobra.</summary>
        static string Contenido(string json)
        {
            const string marca = "\"content\":";
            int i = json.IndexOf(marca, StringComparison.Ordinal);
            if (i < 0) return "";
            i = json.IndexOf('"', i + marca.Length);
            if (i < 0) return "";
            var sb = new StringBuilder();
            for (int k = i + 1; k < json.Length; k++)
            {
                char c = json[k];
                if (c == '\\' && k + 1 < json.Length)
                {
                    char n = json[++k];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 't') sb.Append('\t');
                    else if (n == 'r') { }
                    else if (n == 'u' && k + 4 < json.Length)
                    {
                        int cod;
                        if (int.TryParse(json.Substring(k + 1, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out cod)) sb.Append((char)cod);
                        k += 4;
                    }
                    else sb.Append(n);
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        static string Extraer(string json, string clave)
        {
            var m = Regex.Match(json ?? "", "\"" + Regex.Escape(clave) + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        static double Numero(string json, string clave)
        {
            var m = Regex.Match(json ?? "", "\"" + Regex.Escape(clave) + "\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)");
            double v;
            return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : 0;
        }
    }
}
