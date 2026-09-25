using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace TeamsTools
{
    internal sealed class ChatItem
    {
        public string Nombre = "";        // "Rivas, Valentina" / "Equipo Dev"
        public string Tipo = "privado";   // privado | grupo | reunion | canal | otro
        public string Presencia = "";
        public bool NoLeido;
        public bool Silenciado;
        public string Crudo = "";
        public AutomationElement Elemento;
        public override string ToString() => $"{(NoLeido ? "● " : "")}{Nombre} [{Tipo}]{(Presencia.Length > 0 ? " " + Presencia : "")}";
    }

    internal sealed class Mensaje
    {
        public long Ts;
        public DateTime Hora;
        public string Autor = "";
        public string Texto = "";
        public bool Mio;
        public override string ToString() => $"{Hora:HH:mm} {Autor}: {Texto}";
    }

    /// <summary>
    /// Maneja el chat de Teams por UI Automation sin mostrar nada:
    ///   · lista de chats = Tree "Teams" (TreeItem "Chat Apellido, Nombre Presencia", prefijo "Mensaje sin leer")
    ///   · mensajes = Group id=message-body-&lt;ts&gt; (+ content-&lt;ts&gt;, timestamp-&lt;ts&gt;, Text autor antes)
    ///   · editor = Edit id=new-message-* ; se escribe con WM_CHAR al Chrome_RenderWidgetHostHWND (anda minimizada)
    ///   · enviar = Button "Enviar (Ctrl+Enter)" por Invoke
    /// Si Teams esta "cerrado" a la bandeja la app esta desmontada: se despierta con un deep link msteams: y se minimiza.
    /// </summary>
    internal sealed partial class TeamsChat
    {
        readonly Config cfg;
        readonly Logger log;
        public IntPtr Hwnd = IntPtr.Zero;
        /// <summary>Tu nombre como lo muestra Teams («Apellido, Nombre»). Sale de config.json (miNombre), del contacto «yo» o de tu ficha en la reunión.</summary>
        public string MiNombre = "";
        /// <summary>Tu correo, para el deep link que despierta Teams. Sale de config.json (miCorreo) o del contacto «yo».</summary>
        public string MiCorreo = "";

        /// <summary>
        /// La aduana: TODO acceso de fondo a Teams (observador, autocontestador, presencia, cron, guión) pasa
        /// por acá para no pisarse. Los hilos la toman con <c>using (chat.Gate.Entrar("motivo"))</c> alrededor
        /// de la OPERACIÓN COMPLETA (abrir → leer → escribir → enviar), no método por método: si no, otro hilo
        /// se cuela entre pasos, oculta la ventana y el envío muere con COMException dejando el texto a medias.
        /// </summary>
        public readonly TeamsGate Gate;

        static readonly Regex ReNoLeido = new Regex(@"^(Mensajes? sin leer|Unread messages?|Mensajes? no le[ií]dos?|Nuevo mensaje)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ReTipo = new Regex(@"^(Chat de grupo|Group chat|Chat de reuni[oó]n|Meeting chat|Chat|Favoritos Canal|Equipos y canales|Teams and channels)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly string[] Presencias = { "Disponible", "Ausente", "Ocupado", "No molestar", "Sin conexión", "Sin conexion", "Silenciado", "Aparecer ausente", "Vuelvo enseguida", "En una llamada", "En una reunión", "En una reunion", "Presentando", "Desconocido",
                                                "Available", "Away", "Busy", "Do not disturb", "Offline", "Muted", "Be right back", "In a call", "In a meeting", "Presenting", "Unknown" };
        static readonly Condition CondDoc = new PropertyCondition(AutomationElement.AutomationIdProperty, "RootWebArea");
        static readonly Condition CondTree = new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tree), new PropertyCondition(AutomationElement.NameProperty, "Teams"));
        static readonly Condition CondTreeItem = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem);
        static readonly Condition CondEdit = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
        static readonly Condition CondButton = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
        static readonly Condition CondText = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
        static readonly Condition CondMenuItem = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem);
        const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_CHAR = 0x102;
        const ushort VK_BACK = 0x08;

        public TeamsChat(Config c, Logger l) { cfg = c; log = l; Gate = new TeamsGate(l); }

        static CacheRequest Cache()
        {
            var cr = new CacheRequest();
            cr.Add(AutomationElement.NameProperty); cr.Add(AutomationElement.AutomationIdProperty); cr.Add(AutomationElement.ControlTypeProperty);
            cr.Add(AutomationElement.BoundingRectangleProperty); cr.Add(AutomationElement.IsOffscreenProperty);
            cr.TreeScope = TreeScope.Element;
            return cr;
        }

        // ------------------------------------------------------------------ ventana

        /// <summary>Busca una ventana de Teams con la lista de chats y el editor montados (minimizada vale).</summary>
        public bool BuscarVentana()
        {
            var pids = Win32.PidsDe("ms-teams");
            if (pids.Count == 0) { Hwnd = IntPtr.Zero; return false; }
            // ⚠️ incluye las OCULTAS: con Teams "cerrado a la bandeja" la ventana sigue existiendo y conserva su árbol,
            // así que se puede usar sin mostrar nada (basta con ShowWindow "minimizada sin activar" al momento de escribir).
            var candidatas = Win32.VentanasDe(pids, false).Where(w => w.Titulo.Length > 0 && !w.Cloaked && w.Clase.IndexOf("WebView", StringComparison.OrdinalIgnoreCase) >= 0)
                                  .OrderByDescending(w => w.Titulo.StartsWith("Chat")).ThenByDescending(w => w.Visible).ToList();
            var tapadas = new List<IntPtr>();
            foreach (var v in candidatas)
            {
                try
                {
                    var root = AutomationElement.FromHandle(v.Hwnd);
                    using (Cache().Activate())
                    {
                        var doc = root.FindFirst(TreeScope.Descendants, CondDoc) ?? root;
                        if (doc.FindFirst(TreeScope.Descendants, CondTree) == null)
                        {
                            // 🚨 hay RootWebArea pero no está el árbol de chats: casi siempre es el «Menú de perfil»
                            //    abierto, que deja TODO lo demás aria-hidden (sin editar, sin enviar, sin avatar).
                            //    Se anota y se intenta cerrar recién cuando ninguna ventana sirvió, para no molestar.
                            if (root != doc) tapadas.Add(v.Hwnd);
                            continue;
                        }
                        if (Editor(doc) == null && !v.Titulo.StartsWith("Chat")) continue;
                        Hwnd = v.Hwnd;
                        return true;
                    }
                }
                catch { }
            }
            // ninguna servía: si alguna tenía el menú de perfil tapando el DOM, lo cerramos y volvemos a mirar
            foreach (var h in tapadas)
            {
                if (!Popover.CerrarMenuPerfil(h, log, SoltarFoco)) continue;
                try
                {
                    var root = AutomationElement.FromHandle(h);
                    using (Cache().Activate())
                    {
                        var doc = root.FindFirst(TreeScope.Descendants, CondDoc) ?? root;
                        if (doc.FindFirst(TreeScope.Descendants, CondTree) != null) { Hwnd = h; return true; }
                    }
                }
                catch { }
            }
            Hwnd = IntPtr.Zero;
            return false;
        }

        public bool TeamsCorriendo => Win32.PidsDe("ms-teams").Count > 0;

        static readonly Regex ReEstadoAvatar = new Regex(@"(?:estado|status)\s+(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ReEstadoMenu = new Regex(@"^(.+?),\s*(?:cambiar estado|change status)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Lee el estado REAL que Teams muestra en tu avatar. El botón `idna-me-control-avatar-trigger` se llama
        /// "Tu perfil, estado Disponible" → devuelve "Disponible". Vacío si no se pudo leer. Es solo lectura.
        /// </summary>
        public string PresenciaReal() { string d; return PresenciaReal(out d); }

        /// <summary>
        /// Busca el avatar en TODAS las ventanas de Teams, no solo en la que quedó en Hwnd: las de reunión y la
        /// vista compacta no lo tienen (o lo tienen viejo). `detalle` cuenta dónde lo encontró, para el log.
        /// </summary>
        public string PresenciaReal(out string detalle)
        {
            detalle = "";
            var pids = Win32.PidsDe("ms-teams");
            if (pids.Count == 0) { detalle = "Teams cerrado"; return ""; }
            var candidatas = new List<VentanaInfo>();
            try
            {
                candidatas = Win32.VentanasDe(pids, false)
                    .Where(w => w.Clase == "TeamsWebView" && w.Titulo.Length > 0 && !w.Cloaked
                                && !RePrefijoCompactaPub.IsMatch(w.Titulo))                 // la vista compacta no sirve
                    .OrderByDescending(w => w.Hwnd == Hwnd).ThenByDescending(w => !w.Minimizada).ToList();
            }
            catch { }
            int miradas = 0;
            foreach (var v in candidatas)
            {
                try
                {
                    var root = AutomationElement.FromHandle(v.Hwnd);
                    using (Cache().Activate())
                    {
                        var doc = root.FindFirst(TreeScope.Descendants, CondDoc);
                        if (doc == null) continue;
                        miradas++;
                        string donde = (v.Minimizada ? "ventana minimizada" : "ventana visible") + " " + v.Hwnd;
                        foreach (AutomationElement b in doc.FindAll(TreeScope.Descendants, CondButton))
                        {
                            if ((b.Cached.AutomationId ?? "").IndexOf("me-control-avatar", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            string crudo = b.Cached.Name ?? "";
                            var m = ReEstadoAvatar.Match(crudo);
                            if (!m.Success) continue;
                            detalle = $"«{crudo.Trim()}» en {donde}";
                            return m.Groups[1].Value.Trim();
                        }
                        // 🚨 si el user dejó abierto el menú del avatar y cerró Teams a la bandeja, el popover deja todo lo
                        //    demás aria-hidden y el botón del avatar NO existe. El estado sigue a la vista, pero en el
                        //    MenuItem «Disponible, cambiar estado» del menú de cuenta. (medido por la sonda, 17-sep)
                        foreach (AutomationElement mi in doc.FindAll(TreeScope.Descendants, CondMenuItem))
                        {
                            var m2 = ReEstadoMenu.Match((mi.Cached.Name ?? "").Trim());
                            if (!m2.Success) continue;
                            detalle = $"«{(mi.Cached.Name ?? "").Trim()}» (menú de perfil abierto) en {donde}";
                            return m2.Groups[1].Value.Trim();
                        }
                    }
                }
                catch { }
            }
            detalle = miradas == 0 ? "ninguna ventana de Teams con el árbol montado" : $"{miradas} ventana/s montadas pero sin el avatar";
            return "";
        }

        static readonly Regex RePrefijoCompactaPub = new Regex(@"^(Vista compacta|Compact )", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly Regex ReCambiarEstado = new Regex(@"^(.+?),\s*(?:cambiar estado|change status)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Condition CondRadio = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton);
        static readonly Condition CondAvatar = new PropertyCondition(AutomationElement.AutomationIdProperty, "idna-me-control-avatar-trigger");

        /// <summary>
        /// Fija el estado de presencia en Teams SIN teclas, SIN robar el foreground y SIN mostrar ninguna ventana.
        ///
        /// ⭐ El botón del avatar no expone InvokePattern, pero **sí expone ExpandCollapsePattern**: `Expand()` abre el
        /// menú de perfil desde UIA puro. Adentro, «Ausente, cambiar estado» también es ExpandCollapse, y las opciones
        /// son **RadioButton** («Disponible», «Ocupado», «No molestar», «Vuelvo enseguida», «Aparecer como ausente»,
        /// «Desconectado») con Invoke y SelectionItem. Cada nivel del menú vive en su propio popover portalizado al
        /// RootWebArea, así que hay que buscar por toda la ventana, no entre los hijos del ítem que lo abrió.
        ///
        /// Verificado 17-sep-2026 05:52: Away desde las 05:48 → Available a los 3 s, avatar «estado Disponible »,
        /// foreground intacto y 0 ventanas en pantalla. Es la única vía que RESCATA la presencia: la F15 evita que
        /// Teams se vaya a Ausente, pero una vez que se fue no lo trae de vuelta por más que reinicie la inactividad.
        /// </summary>
        public bool FijarPresencia(string estado, out string detalle)
        {
            detalle = "";
            using (Tareas.Empezar("fijando «" + estado + "» en Teams", "por el menú del avatar, sin mostrar nada", Tema.Malva))
            return FijarPresenciaInterno(estado, out detalle);
        }

        bool FijarPresenciaInterno(string estado, out string detalle)
        {
            detalle = "";
            EsperarSinEnvios();                 // no abrir el menú con un mensaje a medio escribir
            var rx = new Regex("^(" + Regex.Escape(estado) + ")$", RegexOptions.IgnoreCase);
            var pids = Win32.PidsDe("ms-teams");
            if (pids.Count == 0) { detalle = "Teams cerrado"; return false; }
            List<VentanaInfo> candidatas;
            try
            {
                candidatas = Win32.VentanasDe(pids, false)
                    .Where(w => w.Clase == "TeamsWebView" && w.Titulo.Length > 0 && !w.Cloaked && !RePrefijoCompactaPub.IsMatch(w.Titulo))
                    .OrderByDescending(w => w.Hwnd == Hwnd).ToList();
            }
            catch (Exception ex) { detalle = Detalle(ex); return false; }

            foreach (var v in candidatas)
            {
                bool oculta = !v.Visible;
                // 🚨 Expand()/Invoke() sobre elementos de Chromium le dan el foreground a la ventana de Teams aunque
                //    esté minimizada. Sin devolver el foco, lo que teclee el user después cae en el cuadro de mensaje.
                IntPtr fg0 = Win32.GetForegroundWindow();
                ExpandCollapsePattern menu = null;
                try
                {
                    // 🚨🚨 DESTAPAR ANTES DE BUSCAR. Con Teams dormido en la bandeja (medido: 9 y 18 MB de RAM,
                    //    0 ms de CPU) el DOM no está montado y el avatar todavía NO EXISTE. La versión anterior
                    //    lo buscaba primero y se iba en 506 ms sin intentar despertarlo — justo en el único
                    //    escenario donde el rescate hace falta, porque es cuando Teams se va a Ausente y se queda.
                    if (oculta) { try { Win32.ShowWindow(v.Hwnd, Win32.SW_SHOWMINNOACTIVE); } catch { } }
                    AutomationElement avatar = null;
                    var swMonta = Stopwatch.StartNew();
                    while (swMonta.ElapsedMilliseconds < (oculta ? 4000 : 800))
                    {
                        try { avatar = AutomationElement.FromHandle(v.Hwnd).FindFirst(TreeScope.Descendants, CondAvatar); } catch { }
                        if (avatar != null) break;
                        Thread.Sleep(250);
                    }
                    if (avatar == null) { detalle = $"la ventana {v.Hwnd} no montó el avatar en {swMonta.ElapsedMilliseconds} ms"; continue; }

                    menu = avatar.GetCurrentPattern(ExpandCollapsePattern.Pattern) as ExpandCollapsePattern;
                    if (menu == null) { detalle = "el avatar no expone ExpandCollapse"; continue; }
                    menuDePerfilAbierto = true;   // desde acá y hasta el finally, cualquier envío tiene que esperar
                    menu.Expand();
                    Thread.Sleep(900);

                    var cambiar = PorNombre(v.Hwnd, CondMenuItem, n => ReCambiarEstado.IsMatch(n));
                    if (cambiar == null) { detalle = "no apareció «cambiar estado» en el menú de perfil"; continue; }
                    var sub = cambiar.GetCurrentPattern(ExpandCollapsePattern.Pattern) as ExpandCollapsePattern;
                    if (sub == null) { detalle = "«cambiar estado» no expone ExpandCollapse"; continue; }
                    sub.Expand();
                    Thread.Sleep(1100);

                    var opcion = PorNombre(v.Hwnd, CondRadio, n => rx.IsMatch(n.Trim()));
                    if (opcion == null) { detalle = $"no encontré la opción «{estado}» en el submenú de estados"; continue; }

                    bool hecho = false;
                    try { ((InvokePattern)opcion.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); hecho = true; }
                    catch { }
                    if (!hecho) { try { ((SelectionItemPattern)opcion.GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); hecho = true; } catch (Exception ex) { detalle = Detalle(ex); } }
                    if (!hecho) continue;

                    detalle = $"«{estado}» fijado desde el menú del avatar en la ventana {v.Hwnd}";
                    return true;
                }
                catch (Exception ex) { detalle = Detalle(ex); }
                finally
                {
                    // 🚨 dejar el menú abierto deja TODO el resto del DOM aria-hidden y rompe el envío y la lectura
                    try { if (menu != null) menu.Collapse(); } catch { }
                    Thread.Sleep(300);
                    try { if (Popover.HayMenuAbierto(v.Hwnd)) Popover.CerrarMenuPerfil(v.Hwnd, log, SoltarFoco); } catch { }
                    menuDePerfilAbierto = false;
                    try { if (EsDeTeams(Win32.GetForegroundWindow())) DevolverFoco(fg0); } catch { }
                    if (oculta) { try { Win32.ShowWindow(v.Hwnd, Win32.SW_HIDE); } catch { } }
                }
            }
            if (detalle.Length == 0) detalle = "no encontré el avatar en ninguna ventana de Teams";
            return false;
        }

        /// <summary>Primer elemento de la ventana que cumple la condición y cuyo Name pasa el filtro. Los popovers de
        /// FluentUI cuelgan del RootWebArea, no del ítem que los abrió: hay que barrer toda la ventana.</summary>
        static AutomationElement PorNombre(IntPtr hwnd, Condition cond, Func<string, bool> filtro)
        {
            try
            {
                foreach (AutomationElement e in AutomationElement.FromHandle(hwnd).FindAll(TreeScope.Descendants, cond))
                {
                    string n;
                    try { n = e.Current.Name ?? ""; } catch { continue; }
                    if (n.Length > 0 && filtro(n)) return e;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Igual que PresenciaReal pero, si el árbol está caído (Teams guardado en la bandeja horas), destapa las
        /// ventanas como "minimizada sin activar" para que Chromium vuelva a montar el DOM, lee, y las re-oculta.
        /// Nunca aparece nada en pantalla y no toca el foco. Caro: llamarlo cada varios minutos, no cada 20 s.
        /// </summary>
        public string PresenciaRealProfunda(out string detalle)
        {
            string p = PresenciaReal(out detalle);
            if (p.Length > 0) return p;
            List<VentanaInfo> ocultas;
            try
            {
                ocultas = Win32.VentanasDe(Win32.PidsDe("ms-teams"), false)
                    .Where(w => w.Clase == "TeamsWebView" && w.Titulo.Length > 0 && !w.Visible
                                && !RePrefijoCompactaPub.IsMatch(w.Titulo)).ToList();
            }
            catch { return p; }
            if (ocultas.Count == 0) { detalle += " · no hay ventanas guardadas para remontar"; return ""; }
            foreach (var v in ocultas)
            {
                try
                {
                    Win32.ShowWindow(v.Hwnd, Win32.SW_SHOWMINNOACTIVE);
                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 2500)
                    {
                        Thread.Sleep(250);
                        p = PresenciaReal(out detalle);
                        if (p.Length > 0) break;
                    }
                }
                catch { }
                finally { try { Win32.ShowWindow(v.Hwnd, Win32.SW_HIDE); } catch { } }
                if (p.Length > 0) { detalle += " · remontada desde la bandeja"; return p; }
            }
            detalle += " · remonté las ventanas guardadas y aun así no leí el avatar";
            return "";
        }

        /// <summary>
        /// Manda las ventanas de Teams MUY lejos (fuera de cualquier monitor) para que un deep link no muestre nada.
        /// Devuelve la lista para volverlas a su lugar. Es lo que hace invisible el "despertar" con Teams en la bandeja.
        /// </summary>
        List<Tuple<IntPtr, int, int>> Esconder()
        {
            var res = new List<Tuple<IntPtr, int, int>>();
            try
            {
                foreach (var v in Win32.VentanasDe(Win32.PidsDe("ms-teams"), false).Where(w => w.Clase == "TeamsWebView"))
                {
                    var rc = Win32.Normal(v.Hwnd);
                    if (rc.Left < -20000) continue;            // ya estaba escondida
                    res.Add(Tuple.Create(v.Hwnd, rc.Left, rc.Top));
                    Win32.Mover(v.Hwnd, -32000, -32000);
                }
            }
            catch { }
            return res;
        }

        void Restaurar(List<Tuple<IntPtr, int, int>> l)
        {
            foreach (var v in l) { try { Win32.Mover(v.Item1, v.Item2, v.Item3); } catch { } }
        }

        /// <summary>
        /// Deja la ventana en condiciones de recibir teclado SIN que aparezca en pantalla: si estaba oculta (Teams en la
        /// bandeja) la pasa a "minimizada sin activar" — sale en la barra de tareas, nunca en pantalla. Devuelve true si
        /// hubo que destaparla, para volver a ocultarla al terminar.
        /// </summary>
        bool Destapar()
        {
            // 🚨 punto único de espera: todos los caminos que van a tocar el DOM pasan por acá, así que si la
            //    permanencia online tiene abierto el menú del avatar (que deja todo lo demás aria-hidden),
            //    el envío espera a que lo cierre en vez de encontrarse sin editor.
            EsperarTeamsLibre();
            Interlocked.Increment(ref usandoElDom);        // lo baja Tapar(), que va siempre en un finally
            if (Hwnd == IntPtr.Zero) return false;
            if (Win32.IsWindowVisible(Hwnd)) return destapadasPorMi.Contains(Hwnd);   // la destapó Despertar: sigue siendo nuestra
            Win32.ShowWindow(Hwnd, Win32.SW_SHOWMINNOACTIVE);
            Thread.Sleep(220);
            destapadasPorMi.Add(Hwnd);
            return true;
        }

        void Tapar(bool estabaOculta)
        {
            Interlocked.Decrement(ref usandoElDom);
            if (!estabaOculta || Hwnd == IntPtr.Zero) return;
            try { Win32.ShowWindow(Hwnd, Win32.SW_HIDE); } catch { }
            destapadasPorMi.Remove(Hwnd);
        }

        static int usandoElDom;

        /// <summary>
        /// La otra mitad del semáforo: el forzado de presencia espera a que no haya un envío en curso antes de
        /// abrir el menú del avatar. Abrirlo con el editor a medio escribir es peor que perder un envío: podría
        /// mandar el mensaje partido. FijarPresencia no pasa por Destapar, así que no se traba a sí mismo.
        /// </summary>
        static void EsperarSinEnvios(int maxMs = 30000)
        {
            var sw = Stopwatch.StartNew();
            while (Interlocked.CompareExchange(ref usandoElDom, 0, 0) > 0 && sw.ElapsedMilliseconds < maxMs) Thread.Sleep(150);
        }

        /// <summary>
        /// 🚨 Las ventanas que destapamos nosotros hay que volver a ocultarlas: si no, una que el user tenía guardada
        /// en la bandeja queda en la barra de tareas y le cambiamos cómo la dejó. Se llama al terminar una operación
        /// que usó Despertar() sin pasar por Destapar/Tapar.
        /// </summary>
        public void RestaurarOcultas()
        {
            foreach (var h in destapadasPorMi.ToList())
            {
                try { if (Win32.IsWindow(h)) Win32.ShowWindow(h, Win32.SW_HIDE); } catch { }
                destapadasPorMi.Remove(h);
            }
        }

        readonly HashSet<IntPtr> destapadasPorMi = new HashSet<IntPtr>();

        /// <summary>
        /// Lee la lista de chats aunque Teams esté guardado en la bandeja: destapa la ventana como minimizada
        /// (nunca aparece en pantalla), lee, y la vuelve a ocultar. Solo lectura, no toca el foco.
        /// </summary>
        public List<ChatItem> LeerChatsInvisible()
        {
            if (Hwnd == IntPtr.Zero && !BuscarVentana())
            {
                // ninguna ventana con el árbol vivo: probar destapando las que estén guardadas
                foreach (var v in Win32.VentanasDe(Win32.PidsDe("ms-teams"), false).Where(w => w.Clase == "TeamsWebView" && !w.Visible && w.Titulo.Length > 0))
                {
                    Win32.ShowWindow(v.Hwnd, Win32.SW_SHOWMINNOACTIVE);
                    Thread.Sleep(350);
                    if (BuscarVentana())
                    {
                        try { return ListarChats(); }
                        finally { Win32.ShowWindow(v.Hwnd, Win32.SW_HIDE); }
                    }
                    Win32.ShowWindow(v.Hwnd, Win32.SW_HIDE);
                }
                return new List<ChatItem>();
            }
            bool oculta = Destapar();
            try { return ListarChats(); }
            finally { Tapar(oculta); }
        }

        /// <summary>Si la app de Teams esta desmontada (cerrada a la bandeja), la despierta con un deep link al chat propio y la minimiza.</summary>
        public bool Despertar()
        {
            if (BuscarVentana()) return true;
            if (!TeamsCorriendo) { log.Aviso("Teams no está corriendo: no puedo leer chats"); return false; }
            IntPtr fg0 = Win32.GetForegroundWindow();
            // 1) si hay una ventana oculta, destaparla como "minimizada sin activar" suele alcanzar: el árbol sigue vivo y NO aparece nada
            foreach (var v in Win32.VentanasDe(Win32.PidsDe("ms-teams"), false).Where(w => w.Clase == "TeamsWebView" && !w.Visible && w.Titulo.Length > 0))
            {
                Win32.ShowWindow(v.Hwnd, Win32.SW_SHOWMINNOACTIVE);
                Thread.Sleep(400);
                if (BuscarVentana()) { destapadasPorMi.Add(v.Hwnd); log.Ok("Teams estaba en la bandeja: lo destapé minimizado, sin mostrar nada"); DevolverFoco(fg0); return true; }
                Win32.ShowWindow(v.Hwnd, Win32.SW_HIDE);
            }
            // 2) último recurso: deep link. La ventana TIENE que estar en pantalla para montar su contenido
            //    (fuera de pantalla Chromium no renderiza), así que la minimizo apenas aparece para que el destello sea mínimo.
            log.Info("Teams está desmontado: lo despierto con un deep link y lo minimizo apenas aparece");
            try { Process.Start(new ProcessStartInfo("msteams:/l/chat/0/0?users=" + Uri.EscapeDataString(MiCorreo)) { UseShellExecute = true }); }
            catch (Exception ex) { log.Error("No pude abrir el deep link de Teams: " + ex.Message); return false; }
            try
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 20000)
                {
                    Thread.Sleep(120);
                    if (BuscarVentana()) { if (!Win32.IsIconic(Hwnd)) Win32.ShowWindow(Hwnd, Win32.SW_MINIMIZE); break; }
                }
                if (Hwnd == IntPtr.Zero) { log.Aviso("Teams no montó la ventana de chat tras el deep link"); return false; }
                log.Ok("Ventana de chat de Teams lista (minimizada)");
                return true;
            }
            finally { DevolverFoco(fg0); }
        }

        /// <summary>
        /// Suelta el foreground de Teams y se lo devuelve a la ventana que lo tenía. Una ventana MINIMIZADA puede
        /// quedarse con el foreground: minimizarla de nuevo obliga a Windows a pasárselo a la de atrás.
        /// </summary>
        /// <summary>Lo mismo que DevolverFoco, expuesto para los helpers que trabajan sobre la ventana de Teams.</summary>
        public void SoltarFoco(IntPtr fg0) => DevolverFoco(fg0);

        void DevolverFoco(IntPtr fg0)
        {
            // 1) una ventana minimizada puede quedarse con el foreground: minimizarla otra vez lo suelta
            try { if (EsDeTeams(Win32.GetForegroundWindow())) { Win32.ShowWindow(Win32.GetForegroundWindow(), Win32.SW_MINIMIZE); Thread.Sleep(130); } } catch { }
            // 2) devolvérselo a la ventana que lo tenía
            if (fg0 != IntPtr.Zero && fg0 != Hwnd && Win32.IsWindow(fg0) && Win32.IsWindowVisible(fg0))
                for (int i = 0; i < 4 && Win32.GetForegroundWindow() != fg0; i++) { Forzar(fg0); Thread.Sleep(130); }
            // 3) 🚨 pase lo que pase, que el foco NO quede en Teams: si no, lo que escriba el user cae en el cuadro de mensaje
            for (int i = 0; i < 3 && EsDeTeams(Win32.GetForegroundWindow()); i++)
            {
                var otra = PrimeraVisibleAjena();
                if (otra == IntPtr.Zero) break;
                Forzar(otra);
                Thread.Sleep(130);
            }
        }

        void Forzar(IntPtr h)
        {
            try
            {
                uint hiloActivo = Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out _);
                uint yo = Win32.GetCurrentThreadId();
                bool att = hiloActivo != yo && Win32.AttachThreadInput(yo, hiloActivo, true);
                Win32.BringWindowToTop(h);
                Win32.SetForegroundWindow(h);
                if (att) Win32.AttachThreadInput(yo, hiloActivo, false);
            }
            catch { }
        }

        static bool EsDeTeams(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            try { Win32.GetWindowThreadProcessId(h, out uint pid); return Win32.PidsDe("ms-teams").Contains(pid); } catch { return false; }
        }

        /// <summary>Primera ventana visible, con título y no minimizada, que no sea de Teams: para soltarle el foco.</summary>
        static IntPtr PrimeraVisibleAjena()
        {
            IntPtr res = IntPtr.Zero;
            var pids = Win32.PidsDe("ms-teams");
            try
            {
                Win32.EnumWindows((h, l) =>
                {
                    if (!Win32.IsWindowVisible(h) || Win32.IsIconic(h)) return true;
                    Win32.GetWindowThreadProcessId(h, out uint pid);
                    if (pids.Contains(pid)) return true;
                    if (Win32.Titulo(h).Length == 0) return true;
                    if (Win32.Cloaked(h)) return true;
                    res = h;
                    return false;
                }, IntPtr.Zero);
            }
            catch { }
            return res;
        }

        AutomationElement Doc()
        {
            if (Hwnd == IntPtr.Zero || !Win32.IsWindow(Hwnd)) return null;
            var root = AutomationElement.FromHandle(Hwnd);
            return root.FindFirst(TreeScope.Descendants, CondDoc) ?? root;
        }

        static AutomationElement Editor(AutomationElement doc)
        {
            foreach (AutomationElement e in doc.FindAll(TreeScope.Descendants, CondEdit))
                if ((e.Cached.AutomationId ?? "").StartsWith("new-message-", StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        static AutomationElement BotonEnviar(AutomationElement doc)
        {
            foreach (AutomationElement b in doc.FindAll(TreeScope.Descendants, CondButton))
            {
                string n = b.Cached.Name ?? "";
                if (n.StartsWith("Enviar", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Send", StringComparison.OrdinalIgnoreCase)) return b;
            }
            return null;
        }

        /// <summary>"Chat | Rivas, Valentina | Microsoft Teams" -> "Rivas, Valentina".</summary>
        public string ChatAbierto()
        {
            if (Hwnd == IntPtr.Zero) return "";
            var t = Win32.Titulo(Hwnd);
            var m = Regex.Match(t, @"^(?:Chat|Chat de grupo|Group chat)\s*\|\s*(.+?)\s*\|\s*Microsoft Teams", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        // ------------------------------------------------------------------ lista de chats

        public List<ChatItem> ListarChats()
        {
            var res = new List<ChatItem>();
            using (Cache().Activate())
            {
                var doc = Doc(); if (doc == null) return res;
                var tree = doc.FindFirst(TreeScope.Descendants, CondTree); if (tree == null) return res;
                foreach (AutomationElement it in tree.FindAll(TreeScope.Descendants, CondTreeItem))
                {
                    string crudo = it.Cached.Name ?? "";
                    if (crudo.Length == 0) continue;
                    var ci = Parsear(crudo);
                    if (ci == null) continue;
                    ci.Elemento = it;
                    res.Add(ci);
                }
            }
            return res;
        }

        /// <summary>"Mensaje sin leer Chat Rivas, Valentina Ausente" -> Nombre, Tipo, Presencia, NoLeido.</summary>
        public static ChatItem Parsear(string crudo)
        {
            string s = crudo.Trim();
            var ci = new ChatItem { Crudo = crudo };
            var mnl = ReNoLeido.Match(s);
            if (mnl.Success) { ci.NoLeido = true; s = s.Substring(mnl.Length).Trim(); }
            var mt = ReTipo.Match(s);
            if (!mt.Success) return null;                         // "Copilot", "Favoritos", "Chats", "Ver más"...
            string tipo = mt.Groups[1].Value.ToLowerInvariant();
            ci.Tipo = tipo.Contains("grupo") || tipo.Contains("group") ? "grupo" : tipo.Contains("reuni") || tipo.Contains("meeting") ? "reunion" : tipo.Contains("canal") || tipo.Contains("channel") || tipo.Contains("equipos") ? "canal" : "privado";
            s = s.Substring(mt.Length).Trim();
            if (s.EndsWith("Silenciado", StringComparison.OrdinalIgnoreCase) || s.EndsWith("Muted", StringComparison.OrdinalIgnoreCase)) { ci.Silenciado = true; s = Regex.Replace(s, @"\s*(Silenciado|Muted)$", "", RegexOptions.IgnoreCase).Trim(); }
            if (s.EndsWith("Tiene menú contextual", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - "Tiene menú contextual".Length).Trim();
            foreach (var p in Presencias.OrderByDescending(p => p.Length))
                if (s.EndsWith(" " + p, StringComparison.OrdinalIgnoreCase)) { ci.Presencia = p; s = s.Substring(0, s.Length - p.Length).Trim(); break; }
            foreach (var yo in new[] { "(Usted)", "(You)", "(Tú)" })
                if (s.EndsWith(yo, StringComparison.OrdinalIgnoreCase)) { ci.Tipo = "yo"; s = s.Substring(0, s.Length - yo.Length).Trim(); break; }
            ci.Nombre = s;
            return ci;
        }

        public bool AbrirChat(ChatItem chat)
        {
            if (chat?.Elemento == null) return false;
            try
            {
                try { ((SelectionItemPattern)chat.Elemento.GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); }
                catch { ((InvokePattern)chat.Elemento.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); }
                var sw = Stopwatch.StartNew();
                string quiero = Contactos.Normalizar(chat.Nombre);
                while (sw.ElapsedMilliseconds < 5000)
                {
                    Thread.Sleep(300);
                    string abierto = Contactos.Normalizar(ChatAbierto());
                    if (abierto.Length > 0 && (abierto.Contains(quiero) || quiero.Contains(abierto))) return true;
                }
                log.Debug($"abrí «{chat.Nombre}» pero el título quedó en «{ChatAbierto()}»");
                return ChatAbierto().Length > 0;
            }
            catch (Exception ex) { log.Aviso($"No pude abrir el chat «{chat.Nombre}»: {ex.Message}"); return false; }
        }

        /// <summary>Abre el chat con alguien aunque no este en la lista visible: deep link por correo (activa Teams un instante) y vuelve a minimizar.</summary>
        public bool AbrirChatPorCorreo(string correo)
        {
            if (string.IsNullOrWhiteSpace(correo)) return false;
            IntPtr fg0 = Win32.GetForegroundWindow();
            var escondidas = Esconder();
            try
            {
                try { Process.Start(new ProcessStartInfo("msteams:/l/chat/0/0?users=" + Uri.EscapeDataString(correo.Trim())) { UseShellExecute = true }); }
                catch (Exception ex) { log.Error("Deep link falló: " + ex.Message); return false; }
                var sw = Stopwatch.StartNew();
                bool ok = false;
                while (sw.ElapsedMilliseconds < 12000) { Thread.Sleep(600); if (BuscarVentana() && ChatAbierto().Length > 0) { ok = true; break; } }
                Thread.Sleep(600);
                if (Hwnd != IntPtr.Zero) Win32.ShowWindow(Hwnd, Win32.SW_MINIMIZE);
                return ok;
            }
            finally { Restaurar(escondidas); DevolverFoco(fg0); }
        }

        // ------------------------------------------------------------------ mensajes

        public List<Mensaje> LeerMensajes(int ultimos = 15)
        {
            var res = new List<Mensaje>();
            using (Cache().Activate())
            {
                var doc = Doc(); if (doc == null) return res;
                var cuerpos = new List<AutomationElement>();
                foreach (AutomationElement e in doc.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                {
                    string id = e.Cached.AutomationId ?? "";
                    if (id.StartsWith("message-body-", StringComparison.Ordinal)) cuerpos.Add(e);
                }
                var cr = Cache();
                foreach (var c in cuerpos.Skip(Math.Max(0, cuerpos.Count - ultimos)))
                {
                    var m = new Mensaje();
                    string id = c.Cached.AutomationId ?? "";
                    long.TryParse(id.Substring("message-body-".Length), out m.Ts);
                    // 🚨 acotar el rango ANTES de FromUnixTimeMilliseconds: un id con un número fuera del rango de
                    //    fechas válidas (chats propios, ids atípicos) tiraba una excepción que se comía el envío
                    //    entero — el mensaje SÍ salía, pero la lectura de vuelta para el detalle crasheaba y todo
                    //    quedaba reportado como FALLÓ. Fuera de rango ⇒ sin fecha, no reventar.
                    m.Hora = (m.Ts > 0 && m.Ts <= 253402300799999L)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(m.Ts).ToLocalTime().DateTime
                        : DateTime.MinValue;
                    // texto: el hijo content-<ts>
                    foreach (AutomationElement h in c.FindAll(TreeScope.Children, Condition.TrueCondition))
                        if ((h.Cached.AutomationId ?? "").StartsWith("content-")) { m.Texto = (h.Cached.Name ?? "").Trim(); break; }
                    // autor: el Text hermano anterior sin id (antes viene el timestamp con id)
                    try
                    {
                        var prev = TreeWalker.ControlViewWalker.GetPreviousSibling(c, cr);
                        int saltos = 0;
                        while (prev != null && saltos++ < 4)
                        {
                            string pid = prev.Cached.AutomationId ?? ""; string pn = prev.Cached.Name ?? "";
                            if (pid.Length == 0 && pn.Length > 0 && pn.Length < 80 && !pn.EndsWith(".") && prev.Cached.ControlType == ControlType.Text) { m.Autor = pn.Trim(); break; }
                            prev = TreeWalker.ControlViewWalker.GetPreviousSibling(prev, cr);
                        }
                    }
                    catch { }
                    if (m.Autor.Length == 0)
                    {
                        // del nombre del cuerpo: "Autor [Enviado] texto fecha."
                        string n = c.Cached.Name ?? "";
                        int k = m.Texto.Length > 0 ? n.IndexOf(m.Texto, StringComparison.Ordinal) : -1;
                        m.Autor = (k > 0 ? n.Substring(0, k) : n).Replace(" Enviado", "").Replace(" Leído", "").Trim();
                    }
                    m.Mio = Contactos.Normalizar(m.Autor) == Contactos.Normalizar(MiNombre);
                    res.Add(m);
                }
            }
            return res;
        }

        // ------------------------------------------------------------------ escribir y enviar

        IntPtr RenderHwnd()
        {
            IntPtr res = IntPtr.Zero;
            Win32.EnumChildWindows(Hwnd, (h, l) => { if (Win32.Clase(h) == "Chrome_RenderWidgetHostHWND") { res = h; return false; } return true; }, IntPtr.Zero);
            return res;
        }

        static readonly Regex ReInvisibles = new Regex("[​‌‍⁠﻿]", RegexOptions.Compiled);

        /// <summary>Valor del editor sin los caracteres de ancho cero que Teams mete al alternar formato.</summary>
        static string Valor(AutomationElement editor)
        {
            try { object vp; if (editor.TryGetCurrentPattern(ValuePattern.Pattern, out vp)) return ReInvisibles.Replace((((ValuePattern)vp).Current.Value ?? "").Replace("\r", ""), "").TrimEnd('\n'); } catch { }
            return "";
        }

        /// <summary>Largo real del editor (con invisibles), para saber cuantos Backspace mandar.</summary>
        static int LargoCrudo(AutomationElement editor)
        {
            try { object vp; if (editor.TryGetCurrentPattern(ValuePattern.Pattern, out vp)) return (((ValuePattern)vp).Current.Value ?? "").Length; } catch { }
            return 0;
        }

        /// <summary>Vacia el editor a Backspazos, verificando; hasta 4 vueltas.</summary>
        void Vaciar(IntPtr render, AutomationElement editor, string placeholder)
        {
            for (int vuelta = 0; vuelta < 4; vuelta++)
            {
                string v = Valor(editor);
                if (v.Length == 0 || v == placeholder) return;
                Borrar(render, LargoCrudo(editor) + 12);
                Thread.Sleep(250);
            }
        }

        /// <summary>
        /// Vacía el editor a fondo y CONFIRMA que quedó vacío (o en el placeholder). Devuelve false si no lo logró:
        /// el llamador NO debe escribir encima, porque hacerlo sobre texto que no se fue es el bug del mensaje
        /// duplicado. Hasta 5 vueltas de Backspace (el WM_CHAR/Backspace posteado tarda en reflejarse).
        /// </summary>
        bool VaciarFirme(IntPtr render, AutomationElement editor, string placeholder)
        {
            for (int vuelta = 0; vuelta < 5; vuelta++)
            {
                string v = Valor(editor);
                if (v.Length == 0 || v == placeholder) return true;
                Borrar(render, LargoCrudo(editor) + 8);
                Thread.Sleep(200);
            }
            string f = Valor(editor);
            return f.Length == 0 || f == placeholder;
        }

        /// <summary>
        /// Espera a que el editor REFLEJE exactamente el texto esperado, sondeando cada 120 ms hasta el tope. Es lo
        /// que reemplaza al viejo Sleep fijo: el WM_CHAR posteado entra asincrónico y el ValuePattern lo refleja con
        /// retraso variable, así que hay que sondear en vez de suponer un tiempo. Devuelve true apenas coincide.
        /// </summary>
        static bool EsperarValor(AutomationElement editor, string esperado, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (Valor(editor) == esperado) return true;
                Thread.Sleep(120);
            }
            return Valor(editor) == esperado;
        }

        static void Enfocar(AutomationElement editor)
        {
            try { editor.SetFocus(); } catch { }
            Thread.Sleep(90);
        }

        /// <summary>
        /// Deja el teclado apuntando al editor de Teams: SetFocus y, si a los 600 ms Windows todavía no le dio el
        /// foreground, se lo fuerza con AttachThreadInput.
        ///
        /// 🚨 Por qué hace falta: Windows le niega el foreground a una app que no acaba de recibir input del user, y
        ///    nuestra propia tecla fantasma F15 cuenta como input reciente. O sea que la permanencia online le
        ///    saboteaba el foco al envío invisible y el SetFocus solo no alcanzaba. (medido por la sonda, 17-sep)
        /// </summary>
        bool TomarElTeclado(AutomationElement editor)
        {
            var sw = Stopwatch.StartNew();
            bool forzado = false;
            while (sw.ElapsedMilliseconds < 2400 && Win32.GetForegroundWindow() != Hwnd)
            {
                Enfocar(editor);
                if (Win32.GetForegroundWindow() == Hwnd) break;
                if (sw.ElapsedMilliseconds > 600 && !forzado) { Forzar(Hwnd); forzado = true; Thread.Sleep(90); }
            }
            bool ok = Win32.GetForegroundWindow() == Hwnd;
            if (ok && forzado) log.Debug("Teclado: Windows no le daba el foreground a Teams (input reciente), lo forcé con AttachThreadInput");
            return ok;
        }

        /// <summary>Espera a que el user no este tipeando (idle >= 1.5 s), hasta 25 s.</summary>
        static void EsperarPausaDelUser()
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 25000 && Presencia.IdleAhora() < 2) Thread.Sleep(300);
        }

        static volatile bool menuDePerfilAbierto;

        /// <summary>
        /// 🚨 Espera a que la permanencia online termine de manosear el menú del avatar. Mientras ese menú está
        /// abierto, Teams deja TODO el resto del DOM aria-hidden: no hay editor, no hay botón Enviar, no hay árbol
        /// de chats. Un envío que caiga justo ahí falla con "sin editor" o se come el placeholder.
        /// Verificado el 17-sep 05:58: un envío falló exactamente entre dos forzados de presencia.
        /// </summary>
        public static void EsperarTeamsLibre(int maxMs = 15000)
        {
            if (!menuDePerfilAbierto) return;
            var sw = Stopwatch.StartNew();
            while (menuDePerfilAbierto && sw.ElapsedMilliseconds < maxMs) Thread.Sleep(120);
        }

        void PostTexto(IntPtr render, string texto)
        {
            foreach (char ch in texto)
            {
                if (ch == '\r') continue;
                Win32.PostMessage(render, WM_CHAR, (IntPtr)(int)ch, IntPtr.Zero);
            }
        }

        void Borrar(IntPtr render, int n)
        {
            for (int i = 0; i < n; i++) { Win32.PostMessage(render, WM_KEYDOWN, (IntPtr)(int)VK_BACK, IntPtr.Zero); Win32.PostMessage(render, WM_KEYUP, (IntPtr)(int)VK_BACK, IntPtr.Zero); }
        }

        /// <summary>Escribe texto plano en el editor sin mostrar Teams y lo envia. Devuelve el resultado.</summary>
        public ResultadoSalida EnviarTexto(string texto, string chatEsperado = null)
        {
            var r = new ResultadoSalida();
            if (string.IsNullOrWhiteSpace(texto)) { r.Detalle = "texto vacío"; return r; }
            texto = texto.Replace("\r\n", "\n");
            if (Hwnd == IntPtr.Zero && !BuscarVentana()) { r.Detalle = "sin ventana de chat"; return r; }
            if (chatEsperado != null && !Contactos.Normalizar(ChatAbierto()).Contains(Contactos.Normalizar(chatEsperado))) { r.Detalle = $"el chat abierto es «{ChatAbierto()}», no «{chatEsperado}»"; return r; }
            IntPtr render = RenderHwnd();
            if (render == IntPtr.Zero) { r.Detalle = "sin Chrome_RenderWidgetHostHWND"; return r; }
            IntPtr fg0 = Win32.GetForegroundWindow();
            bool minimizada = Win32.IsIconic(Hwnd);
            bool oculta = Destapar();
            try
            {
                using (Cache().Activate())
                {
                    var doc = Doc(); var editor = Editor(doc);
                    if (editor == null) { r.Detalle = "sin editor"; return r; }
                    string placeholder = Valor(editor);
                    string unaLinea = texto.Replace("\n", " ");
                    string v = Valor(editor);
                    // 0) si quedó basura de un intento anterior, dejar el editor vacío ANTES de escribir (con foco,
                    //    que sin foco los Backspace no entran). Si no puedo confirmar que quedó vacío, no escribo
                    //    encima: sería el bug del texto duplicado.
                    if (v.Length > 0 && v != placeholder)
                    {
                        EsperarPausaDelUser(); Enfocar(editor);
                        if (!VaciarFirme(render, editor, placeholder)) { r.Detalle = "no pude limpiar el editor antes de escribir"; return r; }
                    }
                    // 1) INVISIBLE: postear por WM_CHAR SIN foco y ESPERAR a que el ValuePattern lo refleje. Si el
                    //    chat se acaba de abrir, el editor ya tiene el foco del DOM y el texto entra sin activar la
                    //    ventana. Se dan DOS intentos sin foco (a veces el primer WM_CHAR "despierta" el foco del
                    //    editor y el segundo entra), limpiando a fondo entre uno y otro para no duplicar.
                    //    🚨 Antes había un Sleep(350) fijo: si el editor tardaba en reflejar, daba falso negativo, se
                    //    iba al reintento y RE-POSTEABA → «dame un cachitodame un cachito», y quedaba sin enviar.
                    bool puesto = false;
                    for (int intento = 0; intento < 2 && !puesto; intento++)
                    {
                        if (intento > 0 && !VaciarFirme(render, editor, placeholder)) break;
                        PostTexto(render, unaLinea);
                        puesto = EsperarValor(editor, unaLinea, intento == 0 ? 1800 : 2200);
                    }
                    if (!puesto)
                    {
                        // 2) recién ahora, como última opción, darle foco al editor (Teams pasa a foreground pero
                        //    sigue minimizada = invisible), limpiar A FONDO y postear una sola vez más.
                        EsperarPausaDelUser();
                        Enfocar(editor);
                        if (!VaciarFirme(render, editor, placeholder)) { r.Detalle = "no pude limpiar el editor para reintentar"; return r; }
                        PostTexto(render, unaLinea);
                        EsperarValor(editor, unaLinea, 3000);
                    }
                    v = Valor(editor);
                    if (v != unaLinea)
                    {
                        r.Detalle = $"el editor quedó con «{Corto(v)}» en vez del texto";
                        VaciarFirme(render, editor, placeholder);
                        return r;
                    }
                    // 3) enviar
                    var btn = BotonEnviar(doc);
                    if (btn == null) { r.Detalle = "sin botón Enviar"; return r; }
                    ((InvokePattern)btn.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    var sw2 = Stopwatch.StartNew();
                    bool salio = false;
                    while (sw2.ElapsedMilliseconds < 5000) { Thread.Sleep(300); string v2 = Valor(editor); if (v2 == placeholder || v2.Length == 0) { salio = true; break; } }
                    if (!salio) { r.Detalle = "el editor no se vació tras Enviar"; return r; }
                    // el editor se vació ⇒ el mensaje YA salió. La lectura de vuelta es sólo para el detalle lindo:
                    // si falla, no puede degradar un envío exitoso a «FALLÓ».
                    r.Ok = true;
                    try { var ult = LeerMensajes(3).LastOrDefault(m => m.Mio); r.Detalle = ult != null && ult.Texto.Length > 0 ? "enviado · " + Corto(ult.Texto) : "enviado"; }
                    catch { r.Detalle = "enviado"; }
                    return r;
                }
            }
            catch (Exception ex) { r.Detalle = Detalle(ex); return r; }
            finally
            {
                if (minimizada && !Win32.IsIconic(Hwnd)) Win32.ShowWindow(Hwnd, Win32.SW_MINIMIZE);
                DevolverFoco(fg0);
                Tapar(oculta);
            }
        }

        // ------------------------------------------------------------------ envio con formato por atajos de teclado (SendInput, invisible)

        // atajos del Teams nuevo (verificados en el volcado de la barra de formato)
        const ushort VK_B = 0x42, VK_I = 0x49, VK_U = 0x55, VK_X = 0x58, VK_7 = 0x37, VK_8 = 0x38, VK_4 = 0x34, VK_A = 0x41;

        /// <summary>Ctrl+A + Supr con foco, verificando; para eso Teams tiene que estar en foreground (sigue minimizada = invisible).</summary>
        void LimpiarTeclado(AutomationElement editor, string placeholder)
        {
            for (int vuelta = 0; vuelta < 4; vuelta++)
            {
                string v = Valor(editor);
                if (v.Length == 0 || v == placeholder) return;
                Win32.Atajo(true, false, false, VK_A);
                Thread.Sleep(90);
                Win32.TeclaSuelta(Win32.VK_DELETE);
                Thread.Sleep(320);
            }
        }

        /// <summary>Alterna un estilo de bloque (viñeta/numerada/cita/código) por su atajo.</summary>
        static void ToggleBloque(string estilo)
        {
            switch (estilo)
            {
                case "vineta": Win32.Atajo(true, true, false, VK_8); break;
                case "numerada": Win32.Atajo(true, true, false, VK_7); break;
                case "cita": Win32.Atajo(true, false, true, VK_4); break;
                case "codigo": Win32.Atajo(true, true, false, VK_B); break;
            }
            Thread.Sleep(90);
        }

        /// <summary>
        /// Envía con formato SIN mostrar Teams: ventana minimizada, foco al editor por UIA, y TODO el tipeo por
        /// SendInput — texto Unicode (acentos/ñ/emoji sin depender del layout) y atajos Ctrl+B / Ctrl+I / Ctrl+U /
        /// Ctrl+Alt+X / Ctrl+Shift+8 / Ctrl+Shift+7 / Ctrl+Alt+4 / Ctrl+Shift+B, con Shift+Enter entre líneas
        /// (nunca Enter suelto: eso mandaría antes de tiempo). Es el camino probado y el más robusto.
        /// </summary>
        public ResultadoSalida EnviarTeclado(Rico rico, string chatEsperado = null)
        {
            var r = new ResultadoSalida();
            if (rico == null || rico.Vacio) { r.Detalle = "mensaje vacío"; return r; }
            if (Hwnd == IntPtr.Zero && !BuscarVentana()) { r.Detalle = "sin ventana de chat"; return r; }
            if (chatEsperado != null && !Contactos.Normalizar(ChatAbierto()).Contains(Contactos.Normalizar(chatEsperado))) { r.Detalle = $"el chat abierto es «{ChatAbierto()}», no «{chatEsperado}»"; return r; }
            IntPtr fg0 = Win32.GetForegroundWindow();
            bool minimizada = Win32.IsIconic(Hwnd);
            bool oculta = Destapar();
            try
            {
                using (Cache().Activate())
                {
                    var doc = Doc(); var editor = Editor(doc);
                    if (editor == null) { r.Detalle = "sin editor"; return r; }
                    string placeholder = Valor(editor);
                    EsperarPausaDelUser();
                    // el SendInput va a la ventana en foreground: enfocamos el editor (Teams pasa a foreground pero sigue minimizada, invisible)
                    if (!TomarElTeclado(editor)) { r.Detalle = "no pude enfocar el editor de Teams"; return r; }
                    LimpiarTeclado(editor, placeholder);
                    string bloque = "normal";
                    for (int n = 0; n < rico.Lineas.Count; n++)
                    {
                        var l = rico.Lineas[n];
                        if (n > 0) { Win32.Atajo(false, true, false, Win32.VK_RETURN); Thread.Sleep(120); }   // Shift+Enter
                        string destino = (l.Tipo == "vineta" || l.Tipo == "numerada" || l.Tipo == "cita" || l.Tipo == "codigo") ? l.Tipo : "normal";
                        if (destino != bloque) { if (bloque != "normal") ToggleBloque(bloque); if (destino != "normal") ToggleBloque(destino); bloque = destino; }
                        bool b = false, i = false, u = false, s = false;
                        foreach (var c in l.Corridas)
                        {
                            if (c.B != b) { Win32.Atajo(true, false, false, VK_B); Thread.Sleep(45); b = c.B; }
                            if (c.I != i) { Win32.Atajo(true, false, false, VK_I); Thread.Sleep(45); i = c.I; }
                            if (c.U != u) { Win32.Atajo(true, false, false, VK_U); Thread.Sleep(45); u = c.U; }
                            if (c.S != s) { Win32.Atajo(true, false, true, VK_X); Thread.Sleep(45); s = c.S; }
                            Win32.Unicode(c.Texto);
                            Thread.Sleep(Math.Min(500, 40 + c.Texto.Length * 5));
                        }
                        if (b) { Win32.Atajo(true, false, false, VK_B); Thread.Sleep(45); }
                        if (i) { Win32.Atajo(true, false, false, VK_I); Thread.Sleep(45); }
                        if (u) { Win32.Atajo(true, false, false, VK_U); Thread.Sleep(45); }
                        if (s) { Win32.Atajo(true, false, true, VK_X); Thread.Sleep(45); }
                    }
                    // esperar a que el editor se estabilice y verificar que el texto plano entró completo y en orden
                    string prev = "", v = ""; int estable = 0;
                    var sw = Stopwatch.StartNew();
                    string esperado = Contactos.Normalizar(Regex.Replace(rico.Plano(), @"\s+", " "));
                    while (sw.ElapsedMilliseconds < 6000 && estable < 3)
                    {
                        Thread.Sleep(180);
                        v = Contactos.Normalizar(Regex.Replace(Valor(editor), @"\s+", " "));
                        if (v == prev) estable++; else { estable = 0; prev = v; }
                    }
                    if (v.Length < esperado.Length * 0.8) { r.Detalle = $"el editor quedó con «{Corto(v)}» (esperaba «{Corto(esperado)}»)"; LimpiarTeclado(editor, placeholder); return r; }
                    var btn = BotonEnviar(doc);
                    if (btn == null) { r.Detalle = "sin botón Enviar"; return r; }
                    ((InvokePattern)btn.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    var sw2 = Stopwatch.StartNew();
                    bool salio = false;
                    while (sw2.ElapsedMilliseconds < 5000) { Thread.Sleep(300); string v2 = Valor(editor); if (v2 == placeholder || v2.Length == 0) { salio = true; break; } }
                    if (!salio) { r.Detalle = "el editor no se vació tras Enviar"; return r; }
                    r.Ok = true; r.Detalle = "enviado con formato (teclado, invisible)" + (oculta ? " · Teams seguía en la bandeja" : "");
                    return r;
                }
            }
            catch (Exception ex) { r.Detalle = Detalle(ex); return r; }
            finally
            {
                if (minimizada && !Win32.IsIconic(Hwnd)) Win32.ShowWindow(Hwnd, Win32.SW_MINIMIZE);
                DevolverFoco(fg0);
                Tapar(oculta);
            }
        }

        // ------------------------------------------------------------------ formato por la barra de Teams (sin mostrar nada)

        static readonly Dictionary<string, string[]> BotonesFormato = new Dictionary<string, string[]>
        {
            ["abrir"] = new[] { "Mostrar opciones de formato", "Show formatting options", "Show format options" },
            ["cerrar"] = new[] { "Ocultar opciones de formato", "Hide formatting options", "Hide format options" },
            ["B"] = new[] { "Negrita", "Bold" },
            ["I"] = new[] { "Cursiva", "Italic" },
            ["U"] = new[] { "Subrayado", "Underline" },
            ["S"] = new[] { "Tachado", "Strikethrough" },
            ["vineta"] = new[] { "Lista con viñetas", "Bulleted list" },
            ["numerada"] = new[] { "Lista numerada", "Numbered list" },
            ["cita"] = new[] { "Cita", "Quote" },
            ["codigo"] = new[] { "Bloque de código", "Code block" },
        };

        static AutomationElement BotonFormato(AutomationElement doc, string clave)
        {
            var prefijos = BotonesFormato[clave];
            foreach (AutomationElement b in doc.FindAll(TreeScope.Descendants, CondButton))
            {
                string n = b.Cached.Name ?? "";
                foreach (var p in prefijos) if (n.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return b;
            }
            return null;
        }

        /// <summary>Abre la barra de formato si esta cerrada. Devuelve true si la abrio (para cerrarla despues).</summary>
        bool AbrirBarra(AutomationElement doc)
        {
            if (BotonFormato(doc, "B") != null) return false;
            var abrir = BotonFormato(doc, "abrir");
            if (abrir == null) throw new Exception("no encuentro el botón de opciones de formato");
            ((InvokePattern)abrir.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000) { Thread.Sleep(200); if (BotonFormato(doc, "B") != null) return true; }
            throw new Exception("la barra de formato no apareció");
        }

        void CerrarBarra(AutomationElement doc)
        {
            try { var c = BotonFormato(doc, "cerrar"); if (c != null) ((InvokePattern)c.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); } catch { }
        }

        static bool EstadoToggle(AutomationElement b)
        {
            try { object tp; if (b.TryGetCurrentPattern(TogglePattern.Pattern, out tp)) return ((TogglePattern)tp).Current.ToggleState == ToggleState.On; } catch { }
            return false;
        }

        /// <summary>Alterna un boton de formato y DEVUELVE el foco al editor (el Invoke se lo lleva al boton y lo tipeado despues se pierde).</summary>
        static void Poner(AutomationElement doc, string clave, bool on, AutomationElement editor)
        {
            var b = BotonFormato(doc, clave);
            if (b == null) return;
            if (EstadoToggle(b) == on) return;
            try
            {
                object tp;
                if (b.TryGetCurrentPattern(TogglePattern.Pattern, out tp)) ((TogglePattern)tp).Toggle();
                else ((InvokePattern)b.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            }
            catch { }
            Thread.Sleep(60);
            Enfocar(editor);
        }

        /// <summary>
        /// Envia un mensaje con formato sin mostrar Teams: abre la barra de formato, alterna negrita/cursiva/etc. por corrida,
        /// tipea por WM_CHAR (en modo expandido Enter es salto de linea) y manda con Invoke en "Enviar".
        /// </summary>
        public ResultadoSalida EnviarRico(Rico rico, string chatEsperado = null)
        {
            var r = new ResultadoSalida();
            if (rico == null || rico.Vacio) { r.Detalle = "mensaje vacío"; return r; }
            if (!rico.TieneFormato && rico.Lineas.Count == 1) return EnviarTexto(rico.Plano(), chatEsperado);
            if (Hwnd == IntPtr.Zero && !BuscarVentana()) { r.Detalle = "sin ventana de chat"; return r; }
            if (chatEsperado != null && !Contactos.Normalizar(ChatAbierto()).Contains(Contactos.Normalizar(chatEsperado))) { r.Detalle = $"el chat abierto es «{ChatAbierto()}», no «{chatEsperado}»"; return r; }
            IntPtr render = RenderHwnd();
            if (render == IntPtr.Zero) { r.Detalle = "sin Chrome_RenderWidgetHostHWND"; return r; }
            IntPtr fg0 = Win32.GetForegroundWindow();
            bool minimizada = Win32.IsIconic(Hwnd);
            bool abriBarra = false;
            AutomationElement doc = null;
            try
            {
                using (Cache().Activate())
                {
                    doc = Doc(); var editor = Editor(doc);
                    if (editor == null) { r.Detalle = "sin editor"; return r; }
                    string placeholder = Valor(editor);
                    EsperarPausaDelUser();
                    Enfocar(editor);
                    Vaciar(render, editor, placeholder);
                    abriBarra = AbrirBarra(doc);
                    Thread.Sleep(150);
                    Enfocar(editor);
                    string tipoLista = "normal";
                    foreach (var clave in new[] { "B", "I", "U", "S" }) Poner(doc, clave, false, editor);
                    for (int n = 0; n < rico.Lineas.Count; n++)
                    {
                        var l = rico.Lineas[n];
                        bool esLista = l.Tipo == "vineta" || l.Tipo == "numerada";
                        if (esLista && tipoLista != l.Tipo) { if (tipoLista != "normal") Poner(doc, tipoLista, false, editor); Poner(doc, l.Tipo, true, editor); tipoLista = l.Tipo; }
                        else if (!esLista && tipoLista != "normal") { Poner(doc, tipoLista, false, editor); tipoLista = "normal"; }
                        if (l.Tipo == "cita") Poner(doc, "cita", true, editor);
                        if (l.Tipo == "codigo") Poner(doc, "codigo", true, editor);
                        bool b = false, i = false, u = false, s = false;
                        foreach (var c in l.Corridas)
                        {
                            if (c.B != b) { Poner(doc, "B", c.B, editor); b = c.B; }
                            if (c.I != i) { Poner(doc, "I", c.I, editor); i = c.I; }
                            if (c.U != u) { Poner(doc, "U", c.U, editor); u = c.U; }
                            if (c.S != s) { Poner(doc, "S", c.S, editor); s = c.S; }
                            PostTexto(render, c.Texto);
                            Thread.Sleep(Math.Min(400, 20 + c.Texto.Length * 2));
                        }
                        if (b) Poner(doc, "B", false, editor); if (i) Poner(doc, "I", false, editor); if (u) Poner(doc, "U", false, editor); if (s) Poner(doc, "S", false, editor);
                        if (l.Tipo == "cita") Poner(doc, "cita", false, editor);
                        if (l.Tipo == "codigo") Poner(doc, "codigo", false, editor);
                        if (n < rico.Lineas.Count - 1) { Win32.PostMessage(render, WM_CHAR, (IntPtr)'\r', IntPtr.Zero); Thread.Sleep(120); }
                    }
                    if (tipoLista != "normal") Poner(doc, tipoLista, false, editor);
                    // verificar que el texto plano entro (sin exigir igualdad exacta: las listas agregan marcas)
                    var sw = Stopwatch.StartNew();
                    string esperado = Contactos.Normalizar(Regex.Replace(rico.Plano(), @"\s+", " "));
                    string v = "";
                    while (sw.ElapsedMilliseconds < 3000)
                    {
                        Thread.Sleep(200);
                        v = Contactos.Normalizar(Regex.Replace(Valor(editor), @"\s+", " "));
                        if (v.Length >= esperado.Length * 0.8) break;
                    }
                    if (v.Length < esperado.Length * 0.6) { r.Detalle = $"el editor quedó con «{Corto(v)}»"; Vaciar(render, editor, placeholder); return r; }
                    var btn = BotonEnviar(doc);
                    if (btn == null) { r.Detalle = "sin botón Enviar"; return r; }
                    ((InvokePattern)btn.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    sw.Restart();
                    bool salio = false;
                    while (sw.ElapsedMilliseconds < 5000) { Thread.Sleep(300); string v2 = Valor(editor); if (v2 == placeholder || v2.Length == 0) { salio = true; break; } }
                    if (!salio) { r.Detalle = "el editor no se vació tras Enviar"; return r; }
                    r.Ok = true; r.Detalle = "enviado con formato (barra de Teams)";
                    return r;
                }
            }
            catch (Exception ex) { r.Detalle = Detalle(ex); return r; }
            finally
            {
                try { if (abriBarra && doc != null) using (Cache().Activate()) CerrarBarra(Doc()); } catch { }
                if (minimizada && !Win32.IsIconic(Hwnd)) Win32.ShowWindow(Hwnd, Win32.SW_MINIMIZE);
                DevolverFoco(fg0);
            }
        }

        static string Detalle(Exception ex) => string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;

        /// <summary>
        /// Manda con formato; si algo del formato falla (Teams re-renderizó, etc.) reintenta en texto plano así el
        /// mensaje sale igual.
        ///
        /// 🚨 <paramref name="preferirPlano"/>: el autocontestador lo pide en true. El camino con formato
        /// (<see cref="EnviarTeclado"/>) tiene que ENFOCAR el editor, y enfocar una ventana de Chromium le da el
        /// foreground a Teams un instante — se ve como que «se cierra» la ventana en la que estabas. Para una
        /// respuesta automática eso no vale la pena: mejor plano por WM_CHAR, que entra sin tocar el foco. Lo
        /// invisible le gana a la negrita. Los envíos que el user dispara a mano (personalizados, cron) siguen
        /// yendo con formato completo.
        /// </summary>
        public ResultadoSalida EnviarConFallback(Rico rico, string chatEsperado = null, bool preferirPlano = false)
        {
            if (rico == null || rico.Vacio) return new ResultadoSalida { Detalle = "mensaje vacío" };
            if (preferirPlano || (!rico.TieneFormato && rico.Lineas.Count == 1)) return EnviarTexto(rico.Plano(), chatEsperado);
            var r = EnviarTeclado(rico, chatEsperado);
            if (r.Ok) return r;
            log.Aviso($"El envío con formato falló ({r.Detalle}); lo mando en texto plano");
            var r2 = EnviarTexto(rico.Plano(), chatEsperado);
            if (r2.Ok) r2.Detalle = "texto plano (el formato falló: " + r.Detalle + ")";
            return r2;
        }

        /// <summary>Con formato: trae Teams al frente un instante, pega HTML por portapapeles, envia y devuelve el foco.</summary>
        public ResultadoSalida EnviarHtml(string html, string textoPlano, string chatEsperado = null)
        {
            var r = new ResultadoSalida();
            if (string.IsNullOrWhiteSpace(html)) return EnviarTexto(textoPlano, chatEsperado);
            if (Hwnd == IntPtr.Zero && !BuscarVentana()) { r.Detalle = "sin ventana de chat"; return r; }
            if (chatEsperado != null && !Contactos.Normalizar(ChatAbierto()).Contains(Contactos.Normalizar(chatEsperado))) { r.Detalle = $"el chat abierto es «{ChatAbierto()}», no «{chatEsperado}»"; return r; }
            IntPtr fg0 = Win32.GetForegroundWindow();
            bool minimizada = Win32.IsIconic(Hwnd);
            string clipAntes = Portapapeles.TextoActual();
            try
            {
                using (Cache().Activate())
                {
                    var doc = Doc(); var editor = Editor(doc);
                    if (editor == null) { r.Detalle = "sin editor"; return r; }
                    string placeholder = Valor(editor);
                    EsperarPausaDelUser();
                    if (!Win32.TraerAlFrente(Hwnd)) { r.Detalle = "no pude traer Teams al frente"; return r; }
                    Thread.Sleep(200);
                    try { editor.SetFocus(); } catch { }
                    Thread.Sleep(200);
                    Portapapeles.Poner(html, textoPlano);
                    Win32.CtrlV();
                    var sw = Stopwatch.StartNew();
                    string v = "";
                    while (sw.ElapsedMilliseconds < 3000) { Thread.Sleep(200); v = Valor(editor); if (v.Length > 0 && v != placeholder) break; }
                    if (v.Length == 0 || v == placeholder) { r.Detalle = "el pegado no entró al editor"; return r; }
                    var btn = BotonEnviar(doc);
                    if (btn == null) { r.Detalle = "sin botón Enviar"; return r; }
                    ((InvokePattern)btn.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    sw.Restart();
                    bool salio = false;
                    while (sw.ElapsedMilliseconds < 5000) { Thread.Sleep(300); string v2 = Valor(editor); if (v2 == placeholder || v2.Length == 0) { salio = true; break; } }
                    if (!salio) { r.Detalle = "el editor no se vació tras Enviar"; return r; }
                    r.Ok = true; r.Detalle = "enviado con formato";
                    return r;
                }
            }
            catch (Exception ex) { r.Detalle = ex.Message; return r; }
            finally
            {
                if (minimizada || true) Win32.ShowWindow(Hwnd, Win32.SW_MINIMIZE);
                DevolverFoco(fg0);
                if (clipAntes != null) Portapapeles.PonerTexto(clipAntes);
            }
        }

        static string Corto(string s) => string.IsNullOrEmpty(s) ? "" : s.Length > 60 ? s.Substring(0, 60) + "…" : s;
    }
}
