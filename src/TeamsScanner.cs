using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace TeamsTools
{
    /// <summary>Foto de una pasada de lectura sobre Teams.</summary>
    internal sealed class Lectura
    {
        public DateTime Hora = DateTime.Now;
        public bool TeamsCorriendo;
        public int VentanasTeams;
        public bool HayLlamada;
        public IntPtr Hwnd;
        public string Titulo = "";        // titulo completo de la ventana
        public string Reunion = "";       // titulo sin "| Microsoft Teams" ni "Vista compacta"
        public bool EsCompacta;           // ventana "Vista compacta de la reunion" (sin boton Gente)
        public bool Minimizada;
        public string Duracion = "";      // "32:06"
        public int Otros;                 // fichas de OTRAS personas en la galeria
        public int FichasTotal;           // MenuItems del documento (incluye compartido/propio)
        public bool HayCompartido;        // hay ficha "Contenido compartido por ..."
        public string Pagina = "";        // "1/2" si la galeria pagina
        public int PaginasTotal;
        public bool PropioVisto;          // se vio la ficha propia
        public bool? PropioSilenciado;    // tu ficha dice «Silenciado» / «Muted» (respaldo del vigía del micrófono)
        public string NombrePropio = "";
        public int? RosterTotal;          // "En esta reunion (N)" si el panel Gente esta abierto
        public List<string> Nombres = new List<string>();
        public long Ms;
        public string Error = "";

        public string ResumenNombres(int max = 6)
        {
            if (Nombres.Count == 0) return "";
            var l = Nombres.Take(max).ToList();
            return string.Join(", ", l) + (Nombres.Count > max ? $" +{Nombres.Count - max}" : "");
        }
    }

    internal sealed class ResultadoSalida
    {
        public bool Ok;
        public string Detalle = "";
    }

    /// <summary>Lo que el vigia necesita de un lector de Teams (real o simulado).</summary>
    internal interface ILector
    {
        Lectura Leer();
        ResultadoSalida Salir(IntPtr hwnd, string metodo);
        int? VerificarRoster(IntPtr hwnd, out string detalle);
        List<string> Volcar(string carpeta);
    }

    /// <summary>
    /// Lee Teams por UI Automation. Solo lectura salvo Salir() y VerificarRoster().
    /// Puntos fijos del Teams nuevo (independientes del idioma): AutomationId
    /// hangup-button, roster-button, call-duration-custom, RootWebArea.
    /// Cada persona en la galeria es un MenuItem con su nombre; la ficha propia es una Image.
    /// </summary>
    internal sealed class TeamsScanner : ILector
    {
        readonly Config cfg;
        readonly Logger log;
        static readonly Regex RePagina = new Regex(@"(\d+)\s*/\s*(\d+)", RegexOptions.Compiled);
        // en tu ficha: "Video de mi mismo, Ferreyra, Ramiro, …, Silenciado, …" → estás en silencio
        static readonly Regex ReTokenSilenciado = new Regex(@"(^|,\s*)(silenciad[oa]|muted|micr[oó]fono (desactivado|apagado)|audio desactivado)\s*(,|$)",
                                                            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // panel Gente: Group id="roster-title-section-N" name="En esta reunión, 12 en total" / "Otros invitados, 2 en total"
        static readonly Regex ReNumero = new Regex(@"\d+", RegexOptions.Compiled);
        static readonly Regex ReEnEstaReunion = new Regex(@"en esta reuni|en esta llamada|in this meeting|in this call|nesta reuni|nesta chamada|dans cette r|in dieser|in questa|in deze|w tym spotkaniu", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ReSufijoTeams = new Regex(@"\s*\|\s*Microsoft Teams\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex RePrefijoCompacta = new Regex(@"^(Vista compacta de la reuni[oó]n|Compact (meeting )?view|Vista compacta)\s*\|\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        Regex rePropio, reCompartido;
        public string NombrePropioAprendido = "";

        static readonly Condition CondDoc = new PropertyCondition(AutomationElement.AutomationIdProperty, "RootWebArea");
        static readonly Condition CondHangup = new PropertyCondition(AutomationElement.AutomationIdProperty, "hangup-button");
        static readonly Condition CondRoster = new PropertyCondition(AutomationElement.AutomationIdProperty, "roster-button");
        static readonly Condition CondDuracion = new PropertyCondition(AutomationElement.AutomationIdProperty, "call-duration-custom");
        static readonly Condition CondMenuItem = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem);
        static readonly Condition CondImagen = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image);
        static readonly Condition CondBoton = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
        static readonly Condition CondTexto = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);

        public TeamsScanner(Config c, Logger l) { cfg = c; log = l; Recompilar(); }

        public void Recompilar()
        {
            rePropio = Construir(cfg.PatronesPropio, cfg.MiNombre);
            reCompartido = Construir(cfg.PatronesCompartido, null);
        }

        static Regex Construir(string[] pats, string extra)
        {
            var parts = (pats ?? new string[0]).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Regex.Escape(p.Trim())).ToList();
            if (!string.IsNullOrWhiteSpace(extra)) parts.Add(Regex.Escape(extra.Trim()));
            if (parts.Count == 0) parts.Add("(?!)");
            return new Regex(string.Join("|", parts), RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }

        static CacheRequest NuevoCache()
        {
            var cr = new CacheRequest();
            cr.Add(AutomationElement.NameProperty);
            cr.Add(AutomationElement.AutomationIdProperty);
            cr.Add(AutomationElement.ControlTypeProperty);
            cr.Add(AutomationElement.BoundingRectangleProperty);
            cr.Add(AutomationElement.IsOffscreenProperty);
            cr.TreeScope = TreeScope.Element;
            return cr;
        }

        /// <summary>Ventanas candidatas: top-level visibles de ms-teams con titulo (las minimizadas cuentan).</summary>
        public List<VentanaInfo> VentanasCandidatas()
        {
            var pids = Win32.PidsDe("ms-teams");
            if (pids.Count == 0) return new List<VentanaInfo>();
            var v = Win32.VentanasDe(pids).Where(w => w.Titulo.Length > 0 && !w.Cloaked && w.Ancho > 0).ToList();
            // primero las que NO son vista compacta: si hay ventana de reunion completa, es la que manda
            return v.OrderBy(w => RePrefijoCompacta.IsMatch(w.Titulo) ? 1 : 0).ThenBy(w => w.Minimizada ? 1 : 0).ToList();
        }

        public Lectura Leer()
        {
            var sw = Stopwatch.StartNew();
            var L = new Lectura();
            try
            {
                var pids = Win32.PidsDe("ms-teams");
                L.TeamsCorriendo = pids.Count > 0;
                if (!L.TeamsCorriendo) return L;
                var vents = VentanasCandidatas();
                L.VentanasTeams = vents.Count;
                Lectura mejor = null;
                foreach (var v in vents)
                {
                    Lectura l = null;
                    try { l = LeerVentana(v); }
                    catch (ElementNotAvailableException) { }
                    catch (Exception ex) { log.Debug($"lectura de {v.Hwnd} fallo: {ex.GetType().Name}: {ex.Message}"); }
                    if (l == null) continue;
                    if (mejor == null || (mejor.EsCompacta && !l.EsCompacta)) mejor = l;
                    if (!mejor.EsCompacta) break;
                }
                if (mejor != null)
                {
                    mejor.TeamsCorriendo = true; mejor.VentanasTeams = vents.Count; mejor.HayLlamada = true;
                    L = mejor;
                }
            }
            catch (Exception ex) { L.Error = ex.Message; }
            finally { L.Ms = sw.ElapsedMilliseconds; }
            return L;
        }

        Lectura LeerVentana(VentanaInfo v)
        {
            var root = AutomationElement.FromHandle(v.Hwnd);
            using (NuevoCache().Activate())
            {
                var doc = root.FindFirst(TreeScope.Descendants, CondDoc) ?? root;
                var hang = doc.FindFirst(TreeScope.Descendants, CondHangup);
                if (hang == null) return null;                       // no es una llamada
                var roster = doc.FindFirst(TreeScope.Descendants, CondRoster);
                var dur = doc.FindFirst(TreeScope.Descendants, CondDuracion);

                var L = new Lectura
                {
                    Hwnd = v.Hwnd, Titulo = v.Titulo, Minimizada = v.Minimizada,
                    EsCompacta = roster == null || RePrefijoCompacta.IsMatch(v.Titulo),
                    Reunion = ReSufijoTeams.Replace(RePrefijoCompacta.Replace(v.Titulo, ""), "").Trim()
                };
                if (dur != null)
                {
                    // el nodo dice "Tiempo transcurrido 30:03" (congelado) y su hijo Text trae el reloj vivo
                    string vivo = "";
                    try
                    {
                        var hijo = TreeWalker.ControlViewWalker.GetFirstChild(dur);
                        while (hijo != null) { var n = hijo.Cached.Name ?? ""; if (Regex.IsMatch(n, @"^\d{1,2}:\d{2}(:\d{2})?$")) { vivo = n; break; } hijo = TreeWalker.ControlViewWalker.GetNextSibling(hijo); }
                    }
                    catch { }
                    if (vivo.Length == 0) { var m = Regex.Match(dur.Cached.Name ?? "", @"\d{1,2}:\d{2}(:\d{2})?"); vivo = m.Success ? m.Value : ""; }
                    L.Duracion = vivo;
                }

                // fichas propias (Image "Video de mi mismo, Apellido, Nombre, ...") -> aprender el nombre
                foreach (AutomationElement im in doc.FindAll(TreeScope.Descendants, CondImagen))
                {
                    string n = im.Cached.Name ?? "";
                    if (n.Length == 0 || !rePropio.IsMatch(n)) continue;
                    L.PropioVisto = true;
                    L.PropioSilenciado = ReTokenSilenciado.IsMatch(n);
                    Aprender(n);
                    L.NombrePropio = NombrePropioAprendido;
                }

                // participantes = MenuItems del documento que no son ni propio ni contenido compartido
                foreach (AutomationElement it in doc.FindAll(TreeScope.Descendants, CondMenuItem))
                {
                    string n = it.Cached.Name ?? "";
                    if (n.Length == 0 || n == "System") continue;
                    L.FichasTotal++;
                    if (reCompartido.IsMatch(n))
                    {
                        // "Contenido compartido por X": si X no soy yo, hay alguien mas en la sala aunque la galeria este oculta
                        bool propio = NombrePropioAprendido.Length > 0 && n.IndexOf(NombrePropioAprendido, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!propio) L.HayCompartido = true;
                        continue;
                    }
                    if (rePropio.IsMatch(n) || (NombrePropioAprendido.Length > 0 && n.StartsWith(NombrePropioAprendido, StringComparison.OrdinalIgnoreCase)))
                    { L.PropioVisto = true; Aprender(n); L.NombrePropio = NombrePropioAprendido; continue; }
                    L.Otros++;
                    L.Nombres.Add(PrimerNombre(n));
                }

                // paginador de la galeria ("... Actualmente 1/2")
                foreach (AutomationElement b in doc.FindAll(TreeScope.Descendants, CondBoton))
                {
                    string n = b.Cached.Name ?? "";
                    var m = RePagina.Match(n);
                    if (!m.Success) continue;
                    L.Pagina = m.Groups[1].Value + "/" + m.Groups[2].Value;
                    int.TryParse(m.Groups[2].Value, out L.PaginasTotal);
                    break;
                }

                L.RosterTotal = LeerRosterTotal(doc);
                return L;
            }
        }

        void Aprender(string nombreFicha)
        {
            if (NombrePropioAprendido.Length > 0 || string.IsNullOrEmpty(nombreFicha)) return;
            // "Video de mi mismo, Ferreyra, Ramiro, Tiene el control..., Silenciado, ..." -> "Ferreyra, Ramiro"
            var t = nombreFicha.Split(new[] { ", " }, StringSplitOptions.None).Select(s => s.Trim()).ToList();
            if (t.Count < 2) return;
            string cand;
            if (t[1].Contains(' ') || t.Count < 3) cand = t[1];                 // "Ramiro Ferreyra"
            else cand = t[1] + ", " + t[2];                                     // "Ferreyra, Ramiro"
            if (cand.Length < 3 || Regex.IsMatch(cand, @"v[ií]deo|audio|control|silenc|mute|men[uú]|contextual|activ", RegexOptions.IgnoreCase)) return;
            NombrePropioAprendido = cand;
            log.Info($"Ficha propia reconocida: {cand}");
        }

        static string PrimerNombre(string fichaNombre)
        {
            // "Sosa, Florencia, El video esta activado, ..." -> "Sosa, Florencia"; "Ramiro Ferreyra, Muted" -> "Ramiro Ferreyra"
            var t = fichaNombre.Split(new[] { ", " }, StringSplitOptions.None).Select(s => s.Trim()).ToList();
            if (t.Count == 0) return fichaNombre;
            if (t.Count >= 2 && !t[0].Contains(' ') && t[1].Length > 0 && !Regex.IsMatch(t[1], @"v[ií]deo|audio|control|silenc|mute|men[uú]|contextual|activ|desactiv", RegexOptions.IgnoreCase))
                return t[0] + ", " + t[1];
            return t[0];
        }

        static readonly Condition CondGrupo = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group);

        /// <summary>Secciones del panel Gente (id roster-title-section-N) con su nombre; vacio si el panel esta cerrado.</summary>
        List<string> SeccionesRoster(AutomationElement doc)
        {
            var l = new List<string>();
            try
            {
                foreach (AutomationElement e in doc.FindAll(TreeScope.Descendants, CondGrupo))
                {
                    string id = e.Cached.AutomationId ?? "";
                    if (id.StartsWith("roster-title-section", StringComparison.OrdinalIgnoreCase)) l.Add(e.Cached.Name ?? "");
                }
            }
            catch { }
            return l;
        }

        bool RosterAbierto(AutomationElement doc) => SeccionesRoster(doc).Count > 0;

        int? LeerRosterTotal(AutomationElement doc)
        {
            // "En esta reunión, 12 en total" -> 12 (me incluye). Las otras secciones (invitados ausentes, lobby) no cuentan.
            foreach (var n in SeccionesRoster(doc))
            {
                if (!ReEnEstaReunion.IsMatch(n)) continue;
                var m = ReNumero.Match(n);
                if (m.Success && int.TryParse(m.Value, out int k)) return k;
            }
            return null;
        }

        AutomationElement Hangup(IntPtr hwnd)
        {
            if (!Win32.IsWindow(hwnd)) return null;
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                using (NuevoCache().Activate())
                {
                    var doc = root.FindFirst(TreeScope.Descendants, CondDoc) ?? root;
                    return doc.FindFirst(TreeScope.Descendants, CondHangup);
                }
            }
            catch { return null; }
        }

        public bool LlamadaSigue(IntPtr hwnd) => Hangup(hwnd) != null;

        bool EsperarFinLlamada(IntPtr hwnd, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Thread.Sleep(500);
                if (!Win32.IsWindow(hwnd)) return true;
                if (Hangup(hwnd) == null) return true;
            }
            return false;
        }

        /// <summary>Sale de la llamada: Invoke sobre hangup-button y, si hace falta y esta permitido, foco + Ctrl+Shift+H.</summary>
        public ResultadoSalida Salir(IntPtr hwnd, string metodo)
        {
            var r = new ResultadoSalida();
            metodo = (metodo ?? "ambos").ToLowerInvariant();
            bool usarUia = metodo == "uia" || metodo == "ambos";
            bool usarTeclado = metodo == "teclado" || metodo == "ambos";
            var detalle = new StringBuilder();

            if (usarUia)
            {
                var hang = Hangup(hwnd);
                if (hang == null) { detalle.Append("sin boton de salir; "); }
                else
                {
                    try
                    {
                        var inv = hang.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
                        if (inv == null) detalle.Append("hangup-button sin InvokePattern; ");
                        else
                        {
                            inv.Invoke();
                            log.Info("Invoke enviado al boton Salir (hangup-button)");
                            if (EsperarFinLlamada(hwnd, 8000)) { r.Ok = true; r.Detalle = "UIA Invoke"; return r; }
                            detalle.Append("tras el Invoke la llamada seguia; ");
                        }
                    }
                    catch (Exception ex) { detalle.Append("Invoke fallo: " + ex.Message + "; "); }
                }
            }
            if (usarTeclado)
            {
                if (!Win32.IsWindow(hwnd)) { r.Ok = true; r.Detalle = "la ventana ya no existe"; return r; }
                if (Win32.TraerAlFrente(hwnd) && Win32.GetForegroundWindow() == hwnd)
                {
                    Thread.Sleep(150);
                    Win32.CtrlShift('H');
                    log.Info("Ctrl+Shift+H enviado a la ventana de la reunion");
                    if (EsperarFinLlamada(hwnd, 8000)) { r.Ok = true; r.Detalle = "teclado Ctrl+Shift+H"; return r; }
                    detalle.Append("tras Ctrl+Shift+H la llamada seguia; ");
                }
                else detalle.Append("no pude darle foco a la ventana (no mando teclas a ciegas); ");
            }
            r.Ok = false;
            r.Detalle = detalle.ToString().TrimEnd(' ', ';');
            if (r.Detalle.Length == 0) r.Detalle = "metodo de salida '" + metodo + "' no hizo nada";
            return r;
        }

        /// <summary>
        /// Abre el panel Gente (si no estaba), lee "En esta reunion (N)" y lo vuelve a cerrar.
        /// Devuelve null si no pudo leerlo. Es la doble verificacion antes de salir.
        /// </summary>
        public int? VerificarRoster(IntPtr hwnd, out string detalle)
        {
            detalle = "";
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                using (NuevoCache().Activate())
                {
                    var doc = root.FindFirst(TreeScope.Descendants, CondDoc) ?? root;
                    if (RosterAbierto(doc))
                    {
                        int? ya = LeerRosterTotal(doc);
                        detalle = ya != null ? "panel ya abierto" : "panel abierto pero sin la sección 'en esta reunión': " + string.Join(" | ", SeccionesRoster(doc));
                        return ya;
                    }
                    var roster = doc.FindFirst(TreeScope.Descendants, CondRoster);
                    if (roster == null) { detalle = "sin boton Gente"; return null; }
                    var inv = roster.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
                    if (inv == null) { detalle = "Gente sin InvokePattern"; return null; }
                    inv.Invoke();
                    int? n = null;
                    var sw = Stopwatch.StartNew();
                    bool abrio = false;
                    while (sw.ElapsedMilliseconds < 4000 && n == null)
                    {
                        Thread.Sleep(350);
                        abrio = abrio || RosterAbierto(doc);
                        n = LeerRosterTotal(doc);
                    }
                    var secciones = SeccionesRoster(doc);
                    // cerrar el panel (el mismo boton alterna), solo si de verdad lo abrimos nosotros
                    string cierre = "";
                    if (abrio || secciones.Count > 0)
                    {
                        try
                        {
                            roster = doc.FindFirst(TreeScope.Descendants, CondRoster);
                            (roster?.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern)?.Invoke();
                            sw.Restart();
                            while (sw.ElapsedMilliseconds < 3000 && RosterAbierto(doc)) Thread.Sleep(300);
                            cierre = RosterAbierto(doc) ? "; el panel Gente quedó abierto" : "; panel cerrado";
                        }
                        catch (Exception ex) { cierre = "; al cerrar: " + ex.Message; }
                    }
                    detalle = (n == null ? "no encontré la sección 'en esta reunión' (" + string.Join(" | ", secciones) + ")" : "leído del panel Gente") + cierre;
                    return n;
                }
            }
            catch (Exception ex) { detalle = ex.Message; return null; }
        }

        // ------------------------------------------------------------------ diagnostico

        /// <summary>Vuelca el arbol UIA (vista de control y cruda) de cada ventana de Teams. Devuelve los archivos escritos.</summary>
        public List<string> Volcar(string carpeta)
        {
            var archivos = new List<string>();
            Directory.CreateDirectory(carpeta);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (var v in VentanasCandidatas())
            {
                foreach (bool raw in new[] { false, true })
                {
                    try
                    {
                        var cr = new CacheRequest();
                        foreach (var p in new AutomationProperty[] { AutomationElement.NameProperty, AutomationElement.AutomationIdProperty, AutomationElement.ControlTypeProperty,
                            AutomationElement.ClassNameProperty, AutomationElement.BoundingRectangleProperty, AutomationElement.IsOffscreenProperty, AutomationElement.IsEnabledProperty,
                            AutomationElement.IsInvokePatternAvailableProperty, AutomationElement.IsTogglePatternAvailableProperty, AutomationElement.IsExpandCollapsePatternAvailableProperty,
                            AutomationElement.IsSelectionItemPatternAvailableProperty, AutomationElement.IsValuePatternAvailableProperty, ValuePattern.ValueProperty }) cr.Add(p);
                        cr.TreeScope = TreeScope.Element | TreeScope.Descendants;
                        cr.TreeFilter = raw ? Automation.RawViewCondition : Automation.ControlViewCondition;
                        cr.AutomationElementMode = AutomationElementMode.None;
                        var root = AutomationElement.FromHandle(v.Hwnd);
                        root.FindFirst(TreeScope.Descendants, Condition.TrueCondition); // despierta la accesibilidad de Chromium
                        Thread.Sleep(600);
                        var el = root.GetUpdatedCache(cr);
                        var sb = new StringBuilder();
                        int nodos = 0;
                        Recorrer(el, 0, sb, ref nodos);
                        string slug = Regex.Replace(v.Titulo, @"[^A-Za-z0-9]+", "-").Trim('-');
                        if (slug.Length > 50) slug = slug.Substring(0, 50);
                        string f = Path.Combine(carpeta, $"{stamp}_{v.Hwnd}_{(raw ? "raw" : "control")}_{slug}.txt");
                        File.WriteAllText(f, $"# {v.Titulo}\n# pid={v.Pid} hwnd={v.Hwnd} class={v.Clase} {v.Ancho}x{v.Alto} min={v.Minimizada}\n# nodos={nodos} vista={(raw ? "raw" : "control")}\n\n" + sb, new UTF8Encoding(false));
                        archivos.Add(f);
                    }
                    catch (Exception ex) { log.Aviso($"volcado de {v.Hwnd} ({(raw ? "raw" : "control")}) fallo: {ex.Message}"); }
                }
            }
            return archivos;
        }

        static void Recorrer(AutomationElement el, int depth, StringBuilder sb, ref int nodos)
        {
            if (depth > 80) return;
            nodos++;
            string ct = el.Cached.ControlType.ProgrammaticName.Replace("ControlType.", "");
            var r = el.Cached.BoundingRectangle;
            string rs = (r.IsEmpty || double.IsInfinity(r.X) || double.IsInfinity(r.Width)) ? "sin-rect" : $"{(int)r.X},{(int)r.Y} {(int)r.Width}x{(int)r.Height}";
            var flags = new StringBuilder();
            if (el.Cached.IsOffscreen) flags.Append(" off");
            if (!el.Cached.IsEnabled) flags.Append(" disabled");
            if (Prop(el, AutomationElement.IsInvokePatternAvailableProperty)) flags.Append(" [Invoke]");
            if (Prop(el, AutomationElement.IsTogglePatternAvailableProperty)) flags.Append(" [Toggle]");
            if (Prop(el, AutomationElement.IsExpandCollapsePatternAvailableProperty)) flags.Append(" [Expand]");
            if (Prop(el, AutomationElement.IsSelectionItemPatternAvailableProperty)) flags.Append(" [SelItem]");
            string val = "";
            if (Prop(el, AutomationElement.IsValuePatternAvailableProperty)) { try { val = " value=" + (el.GetCachedPropertyValue(ValuePattern.ValueProperty) ?? ""); } catch { } }
            string cls = el.Cached.ClassName ?? "";
            if (cls.Length > 24) cls = cls.Substring(0, 24) + "…";
            sb.Append(' ', depth * 2).Append(ct).Append(" name=\"").Append(el.Cached.Name).Append("\" id=\"").Append(el.Cached.AutomationId)
              .Append("\" class=\"").Append(cls).Append("\" rect=").Append(rs).Append(flags).Append(val).AppendLine();
            foreach (AutomationElement ch in el.CachedChildren) Recorrer(ch, depth + 1, sb, ref nodos);
        }

        static bool Prop(AutomationElement el, AutomationProperty p)
        {
            try { return el.GetCachedPropertyValue(p) is bool b && b; } catch { return false; }
        }
    }
}
