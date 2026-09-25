using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TeamsTools
{
    /// <summary>
    /// Un envío = una secuencia de PARTES que se mandan al mismo chat, una tras otra, como burbujas separadas.
    /// Cada parte es texto con formato (Rico), una imagen (se pega inline) o un archivo (se adjunta). Así una
    /// respuesta del autocontestador, un recordatorio o un mensaje personalizado puede ser "varios mensajes,
    /// imágenes y demás" en vez de una sola burbuja de texto.
    /// Es retrocompatible: los mensajes viejos (un solo TextoHtml) se leen como un envío de una única parte de texto.
    /// </summary>
    internal enum TipoParte { Texto, Imagen, Archivo }

    internal sealed class Parte
    {
        public TipoParte Tipo = TipoParte.Texto;
        public string Html = "";              // Tipo=Texto: el mensaje con formato
        public string Plano = "";             // Tipo=Texto: versión plana (fallback y vista previa)
        public string Rtf = "";               // Tipo=Texto: lo que edita el RichTextBox
        public string Ruta = "";              // Tipo=Imagen|Archivo: ruta al archivo (imagen inline o adjunto)
        public string Nota = "";              // Tipo=Imagen|Archivo: pie opcional que va como texto ANTES del adjunto
        public int EsperaMs = 0;              // pausa a esperar ANTES de mandar esta parte (para escalonar burbujas)

        public static Parte DeTexto(string html, string plano = null, string rtf = "") =>
            new Parte { Tipo = TipoParte.Texto, Html = html ?? "", Plano = plano ?? (html != null ? Rico.DesdeHtml(html).Plano() : ""), Rtf = rtf ?? "" };
        public static Parte DeImagen(string ruta, string nota = "") => new Parte { Tipo = TipoParte.Imagen, Ruta = ruta ?? "", Nota = nota ?? "" };
        public static Parte DeArchivo(string ruta, string nota = "") => new Parte { Tipo = TipoParte.Archivo, Ruta = ruta ?? "", Nota = nota ?? "" };

        public bool EsAdjunto => Tipo == TipoParte.Imagen || Tipo == TipoParte.Archivo;
        public bool Existe => !EsAdjunto || (Ruta.Length > 0 && File.Exists(Ruta));

        public Rico RicoTexto() => Tipo == TipoParte.Texto
            ? (Html.Length > 0 ? Rico.DesdeHtml(Html) : new Rico())
            : new Rico();

        /// <summary>Resumen corto para tablas y logs.</summary>
        public string Resumen(int max = 60)
        {
            string s;
            switch (Tipo)
            {
                case TipoParte.Imagen: s = "🖼 " + Corto(Ruta) + (Nota.Length > 0 ? " · " + Nota : ""); break;
                case TipoParte.Archivo: s = "📎 " + Corto(Ruta) + (Nota.Length > 0 ? " · " + Nota : ""); break;
                default: s = (Html.Length > 0 ? Rico.DesdeHtml(Html).Plano() : Plano).Replace("\n", " ⏎ "); break;
            }
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }
        static string Corto(string ruta) => ruta.Length == 0 ? "(sin ruta)" : Path.GetFileName(ruta);
    }

    /// <summary>La secuencia de partes de un mensaje. Persiste y se rellena marcadores ({nombre}, etc.) parte por parte.</summary>
    internal sealed class Envio
    {
        public List<Parte> Partes = new List<Parte>();

        public bool Vacio => Partes.Count == 0 || Partes.All(p => p.Tipo == TipoParte.Texto && (p.Html.Length == 0 && p.Plano.Length == 0));
        public bool TieneAdjuntos => Partes.Any(p => p.EsAdjunto);
        public bool EsSimpleTexto => Partes.Count == 1 && Partes[0].Tipo == TipoParte.Texto;
        public int Cuantas => Partes.Count;

        /// <summary>Un envío de una sola burbuja de texto (el caso viejo, retrocompatible).</summary>
        public static Envio DeTexto(string html, string plano = null, string rtf = "")
        {
            var e = new Envio();
            if (!string.IsNullOrEmpty(html) || !string.IsNullOrEmpty(plano)) e.Partes.Add(Parte.DeTexto(html, plano, rtf));
            return e;
        }

        /// <summary>Un envío a partir de un Rico (una burbuja).</summary>
        public static Envio DeRico(Rico r) => DeTexto(r?.Html() ?? "", r?.Plano() ?? "");

        /// <summary>
        /// Lee un envío de un JSON. Si el objeto trae la lista "partes" la usa; si no, arma una sola parte de texto
        /// con los campos viejos (textoHtml/textoPlano/textoRtf o respuestaHtml/…). Así los .json existentes siguen andando.
        /// </summary>
        public static Envio Leer(Dictionary<string, object> o, string htmlViejo, string planoViejo, string rtfViejo)
        {
            var e = new Envio();
            var partes = Json.Lista(o, "partes");
            if (partes.Count > 0)
            {
                foreach (var po in partes)
                {
                    string tipo = Json.S(po, "tipo", "texto").ToLowerInvariant();
                    if (tipo == "imagen") e.Partes.Add(new Parte { Tipo = TipoParte.Imagen, Ruta = Json.S(po, "ruta"), Nota = Json.S(po, "nota"), EsperaMs = Json.I(po, "esperaMs") });
                    else if (tipo == "archivo") e.Partes.Add(new Parte { Tipo = TipoParte.Archivo, Ruta = Json.S(po, "ruta"), Nota = Json.S(po, "nota"), EsperaMs = Json.I(po, "esperaMs") });
                    else e.Partes.Add(new Parte { Tipo = TipoParte.Texto, Html = Json.S(po, "html"), Plano = Json.S(po, "plano"), Rtf = Json.S(po, "rtf"), EsperaMs = Json.I(po, "esperaMs") });
                }
            }
            else if (!string.IsNullOrEmpty(htmlViejo) || !string.IsNullOrEmpty(planoViejo))
            {
                e.Partes.Add(new Parte { Tipo = TipoParte.Texto, Html = htmlViejo ?? "", Plano = planoViejo ?? "", Rtf = rtfViejo ?? "" });
            }
            return e;
        }

        /// <summary>Serializa la lista de partes para guardar en el JSON.</summary>
        public List<Dictionary<string, object>> Guardar()
        {
            return Partes.Select(p =>
            {
                var d = new Dictionary<string, object> { ["tipo"] = p.Tipo.ToString().ToLowerInvariant() };
                if (p.Tipo == TipoParte.Texto) { d["html"] = p.Html; d["plano"] = p.Plano; d["rtf"] = p.Rtf; }
                else { d["ruta"] = p.Ruta; d["nota"] = p.Nota; }
                if (p.EsperaMs > 0) d["esperaMs"] = p.EsperaMs;
                return d;
            }).ToList();
        }

        /// <summary>Aplica una transformación a todo el texto (marcadores) de cada parte y devuelve un envío nuevo.</summary>
        public Envio Rellenar(Func<string, string> f)
        {
            var e = new Envio();
            foreach (var p in Partes)
            {
                if (p.Tipo == TipoParte.Texto)
                {
                    var r = p.RicoTexto().Rellenar(f);
                    e.Partes.Add(new Parte { Tipo = TipoParte.Texto, Html = r.Html(), Plano = r.Plano(), Rtf = p.Rtf, EsperaMs = p.EsperaMs });
                }
                else
                {
                    e.Partes.Add(new Parte { Tipo = p.Tipo, Ruta = p.Ruta, Nota = f(p.Nota ?? ""), EsperaMs = p.EsperaMs });
                }
            }
            return e;
        }

        /// <summary>
        /// Agrega la firma del autocontestador al final: como línea en cursiva de la ÚLTIMA parte de texto, o como
        /// una parte nueva si la última es un adjunto (así la firma no queda pegada a una imagen).
        /// </summary>
        public Envio ConFirma(string firma)
        {
            if (string.IsNullOrWhiteSpace(firma)) return this;
            var e = new Envio();
            e.Partes.AddRange(Partes.Select(p => new Parte { Tipo = p.Tipo, Html = p.Html, Plano = p.Plano, Rtf = p.Rtf, Ruta = p.Ruta, Nota = p.Nota, EsperaMs = p.EsperaMs }));
            var ultima = e.Partes.LastOrDefault();
            if (ultima != null && ultima.Tipo == TipoParte.Texto)
            {
                var r = ultima.RicoTexto();
                r.Lineas.Add(new LineaRica { Corridas = { new Formato.Corrida { Texto = firma, I = true } } });
                ultima.Html = r.Html(); ultima.Plano = r.Plano();
            }
            else e.Partes.Add(Parte.DeTexto("<i>" + System.Net.WebUtility.HtmlEncode(firma) + "</i>", firma));
            return e;
        }

        /// <summary>Primer texto plano no vacío (para tablas, avisos y compatibilidad con lo viejo).</summary>
        public string PrimerPlano()
        {
            var t = Partes.FirstOrDefault(p => p.Tipo == TipoParte.Texto && (p.Plano.Length > 0 || p.Html.Length > 0));
            return t == null ? "" : (t.Plano.Length > 0 ? t.Plano : Rico.DesdeHtml(t.Html).Plano());
        }

        /// <summary>Primer HTML (compatibilidad: lo que se guarda en el campo viejo textoHtml/respuestaHtml).</summary>
        public string PrimerHtml()
        {
            var t = Partes.FirstOrDefault(p => p.Tipo == TipoParte.Texto && p.Html.Length > 0);
            return t?.Html ?? "";
        }
        public string PrimerRtf()
        {
            var t = Partes.FirstOrDefault(p => p.Tipo == TipoParte.Texto && p.Rtf.Length > 0);
            return t?.Rtf ?? "";
        }

        /// <summary>Resumen de una línea: "3 mensajes · 🖼 captura.png · 📎 log.txt".</summary>
        public string Resumen(int max = 90)
        {
            if (Vacio) return "(vacío)";
            int textos = Partes.Count(p => p.Tipo == TipoParte.Texto);
            int imgs = Partes.Count(p => p.Tipo == TipoParte.Imagen);
            int arch = Partes.Count(p => p.Tipo == TipoParte.Archivo);
            var trozos = new List<string>();
            if (textos > 0) trozos.Add(textos == 1 ? PrimerPlano().Replace("\n", " ⏎ ") : textos + " mensajes");
            if (imgs > 0) trozos.Add(imgs + (imgs == 1 ? " imagen" : " imágenes"));
            if (arch > 0) trozos.Add(arch + (arch == 1 ? " archivo" : " archivos"));
            string s = string.Join(" · ", trozos);
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        /// <summary>Etiquetas cortas por parte, para dibujar las "fichas" del compositor.</summary>
        public IEnumerable<string> Etiquetas() => Partes.Select((p, i) => $"{i + 1}. {p.Resumen(40)}");

        /// <summary>Rutas de adjuntos que faltan en disco (para avisar antes de mandar).</summary>
        public List<string> AdjuntosFaltantes() => Partes.Where(p => p.EsAdjunto && !p.Existe).Select(p => p.Ruta.Length > 0 ? p.Ruta : "(sin ruta)").ToList();
    }
}
