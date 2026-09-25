using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace TeamsTools
{
    /// <summary>Convierte lo que se escribe en el RichTextBox (negrita, cursiva, subrayado, tachado, código, viñetas, links) a HTML para Teams.</summary>
    internal static class Formato
    {
        public sealed class Corrida { public string Texto = ""; public bool B, I, U, S, Codigo; }

        /// <summary>Recorre el RichTextBox y agrupa los caracteres con el mismo formato.</summary>
        public static List<Corrida> Corridas(RichTextBox rtb)
        {
            var res = new List<Corrida>();
            string t = rtb.Text;
            if (t.Length == 0) return res;
            int sel0 = rtb.SelectionStart, len0 = rtb.SelectionLength;
            try
            {
                Corrida actual = null;
                for (int i = 0; i < t.Length; i++)
                {
                    rtb.Select(i, 1);
                    var f = rtb.SelectionFont;
                    bool b = f != null && f.Bold, it = f != null && f.Italic, u = f != null && f.Underline, s = f != null && f.Strikeout;
                    bool code = f != null && f.Name.IndexOf("Cascadia", StringComparison.OrdinalIgnoreCase) >= 0 && f.Name.IndexOf("Mono", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (actual == null || actual.B != b || actual.I != it || actual.U != u || actual.S != s || actual.Codigo != code)
                    {
                        actual = new Corrida { B = b, I = it, U = u, S = s, Codigo = code };
                        res.Add(actual);
                    }
                    actual.Texto += t[i];
                }
            }
            finally { rtb.Select(sel0, len0); }
            return res;
        }

        /// <summary>HTML con &lt;b&gt;, &lt;i&gt;, &lt;u&gt;, &lt;s&gt;, &lt;code&gt;, listas por lineas que empiezan con "- " o "• ", y links automaticos.</summary>
        public static string Html(RichTextBox rtb)
        {
            var corridas = Corridas(rtb);
            var sb = new StringBuilder();
            foreach (var c in corridas)
            {
                string h = Enlazar(WebUtility.HtmlEncode(c.Texto));
                if (c.Codigo) h = "<code>" + h + "</code>";
                if (c.B) h = "<b>" + h + "</b>";
                if (c.I) h = "<i>" + h + "</i>";
                if (c.U) h = "<u>" + h + "</u>";
                if (c.S) h = "<s>" + h + "</s>";
                sb.Append(h);
            }
            return Lineas(sb.ToString());
        }

        /// <summary>Divide en lineas: las que empiezan con "- " o "• " van como lista, el resto separadas con &lt;br&gt;.</summary>
        static string Lineas(string html)
        {
            var lineas = html.Replace("\r", "").Split('\n');
            var sb = new StringBuilder();
            bool enLista = false;
            foreach (var l in lineas)
            {
                var m = Regex.Match(l, @"^(?:<[^>]+>)*\s*(?:-|•|\*)\s+(.*)$");
                if (m.Success)
                {
                    if (!enLista) { sb.Append("<ul>"); enLista = true; }
                    sb.Append("<li>").Append(Regex.Replace(l, @"(?<=^(?:<[^>]+>)*)\s*(?:-|•|\*)\s+", "")).Append("</li>");
                }
                else
                {
                    if (enLista) { sb.Append("</ul>"); enLista = false; }
                    else if (sb.Length > 0) sb.Append("<br>");
                    sb.Append(l);
                }
            }
            if (enLista) sb.Append("</ul>");
            return sb.ToString();
        }

        static string Enlazar(string htmlEscapado) =>
            Regex.Replace(htmlEscapado, @"(https?://[^\s<>&\""]+)", m => $"<a href=\"{m.Value}\">{m.Value}</a>");

        public static string Plano(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            string t = Regex.Replace(html, @"<br\s*/?>|</li>|</p>|</div>", "\n", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"<li>", "- ", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"<[^>]+>", "");
            return WebUtility.HtmlDecode(t).Trim();
        }

        /// <summary>Carga HTML sencillo (el que generamos nosotros) en el RichTextBox para volver a editarlo.</summary>
        public static void CargarHtml(RichTextBox rtb, string html, Font baseFont, Font codeFont)
        {
            rtb.Clear();
            if (string.IsNullOrEmpty(html)) return;
            html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"</?ul>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<li>", "- ", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"</li>", "\n", RegexOptions.IgnoreCase);
            var tokens = Regex.Split(html, @"(<[^>]+>)");
            bool b = false, i = false, u = false, s = false, code = false;
            foreach (var tk in tokens)
            {
                if (tk.Length == 0) continue;
                if (tk.StartsWith("<"))
                {
                    string tag = tk.ToLowerInvariant();
                    bool cierre = tag.StartsWith("</");
                    string nombre = Regex.Match(tag, @"^</?\s*([a-z]+)").Groups[1].Value;
                    bool val = !cierre;
                    switch (nombre)
                    {
                        case "b": case "strong": b = val; break;
                        case "i": case "em": i = val; break;
                        case "u": u = val; break;
                        case "s": case "strike": case "del": s = val; break;
                        case "code": code = val; break;
                    }
                    continue;
                }
                string texto = WebUtility.HtmlDecode(tk);
                var estilo = FontStyle.Regular;
                if (b) estilo |= FontStyle.Bold; if (i) estilo |= FontStyle.Italic; if (u) estilo |= FontStyle.Underline; if (s) estilo |= FontStyle.Strikeout;
                var fuente = new Font(code ? codeFont.FontFamily : baseFont.FontFamily, baseFont.Size, estilo);
                int pos = rtb.TextLength;
                rtb.AppendText(texto);
                rtb.Select(pos, texto.Length);
                rtb.SelectionFont = fuente;
                rtb.SelectionColor = code ? Tema.Crema : Tema.Texto;
            }
            rtb.Select(rtb.TextLength, 0);
            rtb.SelectionFont = baseFont;
            rtb.SelectionColor = Tema.Texto;
        }

        /// <summary>CF_HTML con offsets en bytes UTF-8, como lo espera Chromium/Teams.</summary>
        public static string CfHtml(string fragmento)
        {
            string pre = "<html><body><!--StartFragment-->", post = "<!--EndFragment--></body></html>";
            string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
            int hl = string.Format(header, 0, 0, 0, 0).Length;
            var enc = Encoding.UTF8;
            int startHtml = hl, startFrag = hl + enc.GetByteCount(pre), endFrag = startFrag + enc.GetByteCount(fragmento), endHtml = endFrag + enc.GetByteCount(post);
            return string.Format(header, startHtml, endHtml, startFrag, endFrag) + pre + fragmento + post;
        }
    }

    /// <summary>Una linea de un mensaje con formato: tipo de linea + corridas con estilo.</summary>
    internal sealed class LineaRica
    {
        public string Tipo = "normal";   // normal | vineta | numerada | cita | codigo
        public List<Formato.Corrida> Corridas = new List<Formato.Corrida>();
        public string Plano => string.Concat(Corridas.Select(c => c.Texto));
    }

    /// <summary>Mensaje con formato, independiente del editor: se arma desde el RichTextBox o desde HTML, y sale como HTML, texto plano o tipeo con toggles.</summary>
    internal sealed class Rico
    {
        public List<LineaRica> Lineas = new List<LineaRica>();

        public bool TieneFormato => Lineas.Any(l => l.Tipo != "normal" || l.Corridas.Any(c => c.B || c.I || c.U || c.S || c.Codigo));
        public bool Vacio => Lineas.All(l => l.Plano.Trim().Length == 0);

        public static Rico DesdeRtb(RichTextBox rtb)
        {
            var r = new Rico();
            var linea = new LineaRica();
            foreach (var c in Formato.Corridas(rtb))
            {
                var partes = c.Texto.Replace("\r", "").Split('\n');
                for (int i = 0; i < partes.Length; i++)
                {
                    if (i > 0) { r.Lineas.Add(linea); linea = new LineaRica(); }
                    if (partes[i].Length > 0) linea.Corridas.Add(new Formato.Corrida { Texto = partes[i], B = c.B, I = c.I, U = c.U, S = c.S, Codigo = c.Codigo });
                }
            }
            r.Lineas.Add(linea);
            foreach (var l in r.Lineas) DetectarPrefijo(l);
            return r;
        }

        static void DetectarPrefijo(LineaRica l)
        {
            if (l.Corridas.Count == 0) return;
            var m = Regex.Match(l.Corridas[0].Texto, @"^\s*(?:(-|•|\*)|(\d+[.)])|(>))\s+");
            if (!m.Success) return;
            l.Tipo = m.Groups[1].Success ? "vineta" : m.Groups[2].Success ? "numerada" : "cita";
            l.Corridas[0].Texto = l.Corridas[0].Texto.Substring(m.Length);
            if (l.Corridas[0].Texto.Length == 0) l.Corridas.RemoveAt(0);
            if (l.Corridas.Count > 0 && l.Corridas.All(c => c.Codigo)) l.Tipo = "codigo";
        }

        public static Rico DesdeHtml(string html)
        {
            var r = new Rico();
            if (string.IsNullOrEmpty(html)) { r.Lineas.Add(new LineaRica()); return r; }
            var linea = new LineaRica();
            bool b = false, i = false, u = false, s = false, code = false;
            string tipoLista = "normal";
            void Cerrar() { r.Lineas.Add(linea); linea = new LineaRica(); }
            foreach (var tk in Regex.Split(html, @"(<[^>]+>)"))
            {
                if (tk.Length == 0) continue;
                if (tk.StartsWith("<"))
                {
                    string tag = tk.ToLowerInvariant();
                    bool cierre = tag.StartsWith("</");
                    string nombre = Regex.Match(tag, @"^</?\s*([a-z0-9]+)").Groups[1].Value;
                    switch (nombre)
                    {
                        case "b": case "strong": b = !cierre; break;
                        case "i": case "em": i = !cierre; break;
                        case "u": u = !cierre; break;
                        case "s": case "strike": case "del": s = !cierre; break;
                        case "code": code = !cierre; break;
                        case "br": Cerrar(); break;
                        case "p": case "div": if (cierre && linea.Corridas.Count > 0) Cerrar(); break;
                        case "ul": tipoLista = cierre ? "normal" : "vineta"; if (cierre && linea.Corridas.Count > 0) Cerrar(); break;
                        case "ol": tipoLista = cierre ? "normal" : "numerada"; if (cierre && linea.Corridas.Count > 0) Cerrar(); break;
                        case "li": if (!cierre) { if (linea.Corridas.Count > 0) Cerrar(); linea.Tipo = tipoLista; } else Cerrar(); break;
                        case "blockquote": if (!cierre) { if (linea.Corridas.Count > 0) Cerrar(); linea.Tipo = "cita"; } else Cerrar(); break;
                        case "pre": if (!cierre) { if (linea.Corridas.Count > 0) Cerrar(); linea.Tipo = "codigo"; code = true; } else { code = false; Cerrar(); } break;
                    }
                    continue;
                }
                string texto = WebUtility.HtmlDecode(tk).Replace("\r", "");
                var partes = texto.Split('\n');
                for (int k = 0; k < partes.Length; k++)
                {
                    if (k > 0) Cerrar();
                    if (partes[k].Length > 0) linea.Corridas.Add(new Formato.Corrida { Texto = partes[k], B = b, I = i, U = u, S = s, Codigo = code });
                }
            }
            if (linea.Corridas.Count > 0 || r.Lineas.Count == 0) r.Lineas.Add(linea);
            return r;
        }

        public string Html()
        {
            var sb = new StringBuilder();
            string listaAbierta = "";
            for (int n = 0; n < Lineas.Count; n++)
            {
                var l = Lineas[n];
                string tipoLista = l.Tipo == "vineta" ? "ul" : l.Tipo == "numerada" ? "ol" : "";
                if (listaAbierta.Length > 0 && tipoLista != listaAbierta) { sb.Append("</").Append(listaAbierta).Append('>'); listaAbierta = ""; }
                if (tipoLista.Length > 0 && listaAbierta.Length == 0) { sb.Append('<').Append(tipoLista).Append('>'); listaAbierta = tipoLista; }
                var cuerpo = new StringBuilder();
                foreach (var c in l.Corridas)
                {
                    string h = WebUtility.HtmlEncode(c.Texto);
                    if (l.Tipo != "codigo") h = Regex.Replace(h, @"(https?://[^\s<>&""]+)", m => $"<a href=\"{m.Value}\">{m.Value}</a>");
                    if (c.Codigo && l.Tipo != "codigo") h = "<code>" + h + "</code>";
                    if (c.B) h = "<b>" + h + "</b>";
                    if (c.I) h = "<i>" + h + "</i>";
                    if (c.U) h = "<u>" + h + "</u>";
                    if (c.S) h = "<s>" + h + "</s>";
                    cuerpo.Append(h);
                }
                if (tipoLista.Length > 0) sb.Append("<li>").Append(cuerpo).Append("</li>");
                else if (l.Tipo == "cita") sb.Append("<blockquote>").Append(cuerpo).Append("</blockquote>");
                else if (l.Tipo == "codigo") sb.Append("<pre>").Append(cuerpo).Append("</pre>");
                else { sb.Append(cuerpo); if (n < Lineas.Count - 1 && (n + 1 >= Lineas.Count || Lineas[n + 1].Tipo == "normal")) sb.Append("<br>"); }
            }
            if (listaAbierta.Length > 0) sb.Append("</").Append(listaAbierta).Append('>');
            return sb.ToString();
        }

        public string Plano()
        {
            var sb = new StringBuilder();
            int num = 0;
            foreach (var l in Lineas)
            {
                if (sb.Length > 0) sb.Append('\n');
                if (l.Tipo == "vineta") sb.Append("- ");
                else if (l.Tipo == "numerada") sb.Append(++num).Append(". ");
                else if (l.Tipo == "cita") sb.Append("> ");
                if (l.Tipo != "numerada") num = 0;
                sb.Append(l.Plano);
            }
            return sb.ToString();
        }

        public Rico Rellenar(Func<string, string> f)
        {
            var r = new Rico();
            foreach (var l in Lineas)
            {
                var nl = new LineaRica { Tipo = l.Tipo };
                foreach (var c in l.Corridas) nl.Corridas.Add(new Formato.Corrida { Texto = f(c.Texto), B = c.B, I = c.I, U = c.U, S = c.S, Codigo = c.Codigo });
                r.Lineas.Add(nl);
            }
            return r;
        }

        /// <summary>Resumen corto para listas: primera linea sin formato.</summary>
        public string Resumen(int max = 70)
        {
            string p = Plano().Replace("\n", " ⏎ ");
            return p.Length > max ? p.Substring(0, max) + "…" : p;
        }
    }

    /// <summary>Portapapeles desde cualquier hilo (STA propio), guardando y devolviendo el texto que habia.</summary>
    internal static class Portapapeles
    {
        static void Sta(Action a)
        {
            Exception err = null;
            var t = new Thread(() => { try { a(); } catch (Exception ex) { err = ex; } });
            t.SetApartmentState(ApartmentState.STA);
            t.Start(); t.Join(4000);
            if (err != null) throw err;
        }

        public static string TextoActual()
        {
            string s = null;
            try { Sta(() => { if (Clipboard.ContainsText()) s = Clipboard.GetText(); }); } catch { }
            return s;
        }

        public static void Poner(string html, string texto)
        {
            Sta(() =>
            {
                var data = new DataObject();
                if (!string.IsNullOrEmpty(html)) data.SetData(DataFormats.Html, Formato.CfHtml(html));
                data.SetData(DataFormats.UnicodeText, texto ?? "");
                data.SetData(DataFormats.Text, texto ?? "");
                Clipboard.SetDataObject(data, true, 5, 100);
            });
        }

        public static void PonerTexto(string texto)
        {
            try { Sta(() => { if (string.IsNullOrEmpty(texto)) Clipboard.Clear(); else Clipboard.SetText(texto); }); } catch { }
        }

        /// <summary>
        /// Deja una imagen en el portapapeles como la espera Teams/Chromium para pegar INLINE: el stream "PNG"
        /// (formato preferido, conserva transparencia) más CF_BITMAP de respaldo. Lanza si el archivo no es una imagen.
        /// </summary>
        public static void PonerImagen(string ruta)
        {
            var bytes = File.ReadAllBytes(ruta);
            Sta(() =>
            {
                using (var ms0 = new MemoryStream(bytes))
                using (var bmp = new Bitmap(ms0))
                {
                    var data = new DataObject();
                    var png = new MemoryStream();
                    bmp.Save(png, ImageFormat.Png);
                    data.SetData("PNG", false, png);
                    data.SetData(DataFormats.Bitmap, true, new Bitmap(bmp));
                    Clipboard.SetDataObject(data, true, 8, 120);
                }
            });
        }

        /// <summary>¿La ruta es una imagen que Teams pega inline? (por extensión).</summary>
        public static readonly string[] ExtImagen = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };
        public static bool EsImagen(string ruta) => !string.IsNullOrEmpty(ruta) && ExtImagen.Contains(Path.GetExtension(ruta).ToLowerInvariant());

        /// <summary>
        /// Copia TODO el contenido actual del portapapeles (todos los formatos) para restaurarlo después de pegar un
        /// adjunto. Devuelve null si estaba vacío. Se usa junto con <see cref="Restaurar"/>.
        /// </summary>
        public static IDataObject Copiar()
        {
            IDataObject copia = null;
            try
            {
                Sta(() =>
                {
                    var d = Clipboard.GetDataObject();
                    if (d == null) return;
                    var c = new DataObject();
                    int n = 0;
                    foreach (var f in d.GetFormats(false))
                    {
                        try { var v = d.GetData(f, false); if (v != null) { c.SetData(f, false, v); n++; } } catch { }
                    }
                    if (n > 0) copia = c;
                });
            }
            catch { }
            return copia;
        }

        /// <summary>Restaura el portapapeles a lo que devolvió <see cref="Copiar"/> (o lo limpia si era null).</summary>
        public static void Restaurar(IDataObject copia)
        {
            try { Sta(() => { if (copia == null) Clipboard.Clear(); else Clipboard.SetDataObject(copia, true, 8, 120); }); } catch { }
        }
    }
}
