using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace TeamsTools
{
    /// <summary>
    /// Envío de MÚLTIPLES partes (varias burbujas de texto + imágenes) sin mostrar Teams. Cada parte se manda como
    /// su propia burbuja: los textos reusan EnviarConFallback (teclado con formato), las imágenes se pegan inline por
    /// portapapeles. Verificado end-to-end el 17-sep: 3 burbujas (texto, imagen, texto) a mi chat, 0 ventanas en
    /// pantalla, foco devuelto. Los archivos que no son imagen NO tienen camino invisible confiable (el selector de
    /// Windows exige un gesto real): se avisan como no soportados en el modo silencioso.
    /// Requiere que TeamsChat sea `partial` (una palabra en Mensajeria.cs).
    /// </summary>
    internal sealed partial class TeamsChat
    {
        /// <summary>
        /// Manda un envío completo. Retrocompatible: si es una sola burbuja de texto cae en EnviarConFallback tal cual.
        /// Devuelve Ok=true solo si TODAS las partes con contenido se enviaron; el detalle resume qué pasó con cada una.
        /// </summary>
        public ResultadoSalida EnviarEnvio(Envio envio, string chatEsperado = null, bool preferirPlano = false)
        {
            var r = new ResultadoSalida();
            if (envio == null || envio.Partes.Count == 0) { r.Detalle = "envío vacío"; return r; }

            // caso viejo: una sola burbuja de texto → el camino de siempre, sin tocar nada
            if (envio.Partes.Count == 1 && envio.Partes[0].Tipo == TipoParte.Texto)
                return EnviarConFallback(envio.Partes[0].RicoTexto(), chatEsperado, preferirPlano);

            int total = 0, ok = 0;
            var detalles = new System.Collections.Generic.List<string>();
            for (int i = 0; i < envio.Partes.Count; i++)
            {
                var p = envio.Partes[i];
                if (p.Tipo == TipoParte.Texto && p.Html.Length == 0 && p.Plano.Length == 0) continue;  // parte de texto vacía: la salto
                total++;
                if (p.EsperaMs > 0) Thread.Sleep(Math.Min(p.EsperaMs, 20000));

                ResultadoSalida res;
                if (p.Tipo == TipoParte.Texto)
                {
                    res = EnviarConFallback(p.RicoTexto(), chatEsperado);
                }
                else if (p.Tipo == TipoParte.Imagen || (p.Tipo == TipoParte.Archivo && Portapapeles.EsImagen(p.Ruta)))
                {
                    res = EnviarImagen(p.Ruta, p.Nota, chatEsperado);
                }
                else
                {
                    // archivo que no es imagen: sin camino invisible. Mando el aviso como texto para no perder la parte.
                    res = new ResultadoSalida();
                    string aviso = (p.Nota.Length > 0 ? p.Nota + " " : "") + "(adjunto no enviado: los archivos que no son imagen no se pueden mandar en modo invisible)";
                    log.Aviso($"Envío: la parte {i + 1} es un archivo no-imagen «{System.IO.Path.GetFileName(p.Ruta)}»: no hay camino invisible, mando un aviso de texto");
                    res = EnviarTexto(aviso, chatEsperado);
                    if (res.Ok) res.Detalle = "archivo no-imagen: avisado por texto";
                }

                if (res.Ok) { ok++; detalles.Add($"{i + 1}:{Etiqueta(p)}✓"); }
                else { detalles.Add($"{i + 1}:{Etiqueta(p)}✗({res.Detalle})"); log.Error($"Envío: falló la parte {i + 1}: {res.Detalle}"); }

                if (i < envio.Partes.Count - 1) Thread.Sleep(650);   // que las burbujas no se apelmacen
            }

            r.Ok = ok == total && total > 0;
            r.Detalle = total == 0 ? "envío sin partes con contenido" : $"{ok}/{total} partes · " + string.Join(" ", detalles);
            return r;
        }

        static string Etiqueta(Parte p) => p.Tipo == TipoParte.Imagen ? "img" : p.Tipo == TipoParte.Archivo ? "arch" : "txt";

        /// <summary>
        /// Manda UNA burbuja con una imagen inline (y una nota de texto opcional adelante) sin mostrar Teams: destapa,
        /// enfoca el editor, tipea la nota, pega la imagen por portapapeles, espera a que suba, invoca Enviar, y siempre
        /// restaura el portapapeles y devuelve el foco. Es el gemelo de EnviarTexto para imágenes.
        /// </summary>
        ResultadoSalida EnviarImagen(string ruta, string nota, string chatEsperado)
        {
            var r = new ResultadoSalida();
            if (string.IsNullOrEmpty(ruta) || !System.IO.File.Exists(ruta)) { r.Detalle = "no existe la imagen"; return r; }
            if (!Portapapeles.EsImagen(ruta)) { r.Detalle = "no es una imagen"; return r; }
            if (Hwnd == IntPtr.Zero && !BuscarVentana()) { r.Detalle = "sin ventana de chat"; return r; }
            if (chatEsperado != null && !Contactos.Normalizar(ChatAbierto()).Contains(Contactos.Normalizar(chatEsperado))) { r.Detalle = $"el chat abierto es «{ChatAbierto()}», no «{chatEsperado}»"; return r; }

            IntPtr fg0 = Win32.GetForegroundWindow();
            bool minimizada = Win32.IsIconic(Hwnd);
            bool oculta = Destapar();
            System.Windows.Forms.IDataObject clip = null;
            try
            {
                using (Cache().Activate())
                {
                    var doc = Doc(); var editor = Editor(doc);
                    if (editor == null) { r.Detalle = "sin editor"; return r; }
                    string placeholder = Valor(editor);
                    EsperarPausaDelUser();
                    if (!TomarElTeclado(editor)) { r.Detalle = "no pude enfocar el editor de Teams"; return r; }

                    clip = Portapapeles.Copiar();
                    // nota de texto opcional, antes de la imagen, en la misma burbuja
                    if (!string.IsNullOrWhiteSpace(nota))
                    {
                        Win32.Unicode(nota.Replace("\r", "").Replace("\n", " "));
                        Thread.Sleep(Math.Min(500, 60 + nota.Length * 5));
                    }
                    try { Portapapeles.PonerImagen(ruta); } catch (Exception ex) { r.Detalle = "no pude poner la imagen en el portapapeles: " + ex.Message; return r; }
                    Enfocar(editor);
                    Win32.CtrlV();

                    if (!EsperarAdjuntoListo(editor, 45000)) { r.Detalle = "la imagen no terminó de subir"; LimpiarTeclado(editor, placeholder); return r; }

                    var btn = BotonEnviar(Doc());
                    if (btn == null) { r.Detalle = "sin botón Enviar"; return r; }
                    ((InvokePattern)btn.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    var sw = Stopwatch.StartNew();
                    bool salio = false;
                    while (sw.ElapsedMilliseconds < 8000) { Thread.Sleep(300); var ed2 = Editor(Doc()) ?? editor; string v = Valor(ed2); if (v.Length == 0 || v == placeholder) { salio = true; break; } }
                    if (!salio) { r.Detalle = "el editor no se vació tras Enviar la imagen"; return r; }
                    r.Ok = true; r.Detalle = "imagen enviada (inline, invisible)" + (oculta ? " · Teams seguía en la bandeja" : "");
                    return r;
                }
            }
            catch (Exception ex) { r.Detalle = Detalle(ex); return r; }
            finally
            {
                Portapapeles.Restaurar(clip);
                if (minimizada && !Win32.IsIconic(Hwnd)) Win32.ShowWindow(Hwnd, Win32.SW_MINIMIZE);
                DevolverFoco(fg0);
                Tapar(oculta);
            }
        }

        static readonly Regex ReCargando = new Regex(@"ProgressBar|Cargando|Subiendo|Uploading|Loading|\d+\s?%", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Espera a que un adjunto pegado termine de subir: aparece un nodo de adjunto en el área de redacción, no queda
        /// ninguna barra de progreso, y el árbol se estabiliza 3,5 s sin cambios. true si llegó a estar listo.
        /// </summary>
        bool EsperarAdjuntoListo(AutomationElement editor, int msMax)
        {
            var sw = Stopwatch.StartNew();
            int estable = 0, previo = -1;
            bool huboAdjunto = false;
            while (sw.ElapsedMilliseconds < msMax)
            {
                Thread.Sleep(300);
                AutomationElement ed;
                try { ed = Editor(Doc()) ?? editor; } catch { continue; }
                int nodos = 0; bool cargando = false, adjunto = false;
                try
                {
                    foreach (AutomationElement e in ed.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                    {
                        nodos++;
                        string n = "";
                        try { n = e.Cached.Name ?? ""; } catch { try { n = e.Current.Name ?? ""; } catch { } }
                        if (ReCargando.IsMatch(n)) cargando = true;
                        try { if (e.Cached.ControlType == ControlType.Image || (e.Cached.ControlType == ControlType.Button && n.Length > 0)) adjunto = true; } catch { }
                    }
                }
                catch { continue; }
                if (adjunto) huboAdjunto = true;
                bool cambio = nodos != previo;
                previo = nodos;
                if (huboAdjunto && !cargando && !cambio) { if (++estable >= 12) return true; }   // ~3.6 s quieto
                else estable = 0;
            }
            return huboAdjunto;   // se subió algo aunque no terminemos de confirmar la calma
        }
    }
}
