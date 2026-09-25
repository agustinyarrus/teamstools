using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace TeamsTools
{
    /// <summary>
    /// El «Menú de perfil» de Teams (popover de FluentUI, clase fui-PopoverSurface) queda abierto si el user cerró
    /// Teams a la bandeja con el menú desplegado. Mientras está abierto el resto del DOM queda aria-hidden: sin árbol
    /// de chats, sin editor, sin botón Enviar y sin el botón del avatar (el árbol se queda en ~164 elementos).
    /// Acá se detecta, se lee el estado desde el propio menú y se cierra con Escape SIN mostrar nada en pantalla.
    /// Medido el 17-sep: se cierra con 1 Escape, foco devuelto en 0,7 s, 0 ventanas de Teams en pantalla.
    /// </summary>
    internal static class Popover
    {
        static readonly Regex ReEstadoMenu = new Regex(@"^(.+?),\s*(cambiar estado|change status)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        const ushort VK_ESCAPE = 0x1B;

        /// <summary>true si en esa ventana hay un popover de FluentUI abierto (el menú de perfil u otro).</summary>
        public static bool HayMenuAbierto(IntPtr hwnd) => Buscar(hwnd) != null;

        /// <summary>
        /// Estado de presencia leído del MenuItem «Disponible, cambiar estado» del menú abierto. "" si no hay menú
        /// o el ítem no está. Sirve de segunda opinión cuando el botón del avatar no aparece.
        /// </summary>
        public static string EstadoDesdeMenu(IntPtr hwnd)
        {
            var pop = Buscar(hwnd);
            if (pop == null) return "";
            try
            {
                var cr = new CacheRequest(); cr.Add(AutomationElement.NameProperty); cr.TreeScope = TreeScope.Element;
                using (cr.Activate())
                    foreach (AutomationElement it in pop.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)))
                    {
                        var m = ReEstadoMenu.Match(it.Cached.Name ?? "");
                        if (m.Success) return m.Groups[1].Value.Trim();
                    }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Cierra el menú de perfil con Escape sin mostrar Teams. Devuelve true SOLO si había menú y lo cerró.
        /// Pasos: si la ventana está oculta la destapa como minimizada sin activar; enfoca un MenuItem de adentro
        /// (Teams pasa a foreground pero sigue minimizada, no aparece nada); manda Escape hasta 3 veces; re-minimiza,
        /// devuelve el foco con <paramref name="devolverFoco"/> (pasar Chat.SoltarFoco) y re-oculta si estaba oculta.
        /// </summary>
        public static bool CerrarMenuPerfil(IntPtr hwnd, Logger log, Action<IntPtr> devolverFoco = null)
        {
            if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return false;
            IntPtr fg0 = Win32.GetForegroundWindow();
            bool oculta = !Win32.IsWindowVisible(hwnd), minimizada = Win32.IsIconic(hwnd) || oculta;
            AutomationElement pop = null;
            try
            {
                if (oculta) { Win32.ShowWindow(hwnd, Win32.SW_SHOWMINNOACTIVE); Thread.Sleep(250); }
                pop = Buscar(hwnd);
                if (pop == null) return false;
                string nombre = "";
                try { nombre = pop.Current.Name ?? ""; } catch { }
                log?.Info($"Teams quedó con el menú «{(nombre.Length > 0 ? nombre : "de perfil")}» abierto (tapa el editor y el avatar): lo cierro con Escape sin mostrar nada");
                // el teclado va a la ventana en foreground: enfocar algo DE ADENTRO del popover
                AutomationElement item = null;
                try { item = pop.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)); } catch { }
                item = item ?? pop;
                var sw = Stopwatch.StartNew();
                bool forzado = false;
                while (sw.ElapsedMilliseconds < 3000 && Win32.GetForegroundWindow() != hwnd)
                {
                    try { item.SetFocus(); } catch { }
                    Thread.Sleep(100);
                    if (Win32.GetForegroundWindow() == hwnd) break;
                    // Windows no le da el foreground a Teams si hubo input reciente (la F15 fantasma cuenta): forzarlo desde acá
                    if (sw.ElapsedMilliseconds > 600 && !forzado) { Forzar(hwnd); forzado = true; Thread.Sleep(80); }
                }
                if (Win32.GetForegroundWindow() != hwnd) { log?.Aviso("No pude darle el teclado a Teams para cerrar el menú de perfil"); return false; }
                for (int i = 0; i < 3; i++)
                {
                    Win32.TeclaSuelta(VK_ESCAPE);
                    Thread.Sleep(350);
                    if (Buscar(hwnd) == null) { log?.Ok($"Menú de perfil cerrado con {i + 1} Escape · Teams sigue {(Win32.IsIconic(hwnd) ? "minimizada" : "como estaba")}"); return true; }
                }
                log?.Aviso("El menú de perfil sigue abierto tras 3 Escape");
                return false;
            }
            catch (Exception ex) { log?.Aviso("Cerrando el menú de perfil: " + ex.Message); return false; }
            finally
            {
                try { if (minimizada && !Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_MINIMIZE); } catch { }
                try { if (devolverFoco != null) devolverFoco(fg0); else Forzar(fg0); } catch { }
                try { if (oculta) Win32.ShowWindow(hwnd, Win32.SW_HIDE); } catch { }
            }
        }

        /// <summary>El popover (Window con ClassName fui-PopoverSurface) bajo el RootWebArea de la ventana, o null.</summary>
        static AutomationElement Buscar(IntPtr hwnd)
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                var doc = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "RootWebArea")) ?? root;
                var cr = new CacheRequest(); cr.Add(AutomationElement.NameProperty); cr.Add(AutomationElement.ClassNameProperty); cr.TreeScope = TreeScope.Element;
                using (cr.Activate())
                    foreach (AutomationElement e in doc.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)))
                        if ((e.Cached.ClassName ?? "").IndexOf("fui-PopoverSurface", StringComparison.OrdinalIgnoreCase) >= 0) return e;
            }
            catch { }
            return null;
        }

        static void Forzar(IntPtr h)
        {
            if (h == IntPtr.Zero || !Win32.IsWindow(h)) return;
            uint hiloActivo = Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out _);
            uint yo = Win32.GetCurrentThreadId();
            bool att = hiloActivo != yo && Win32.AttachThreadInput(yo, hiloActivo, true);
            try { Win32.BringWindowToTop(h); Win32.SetForegroundWindow(h); }
            finally { if (att) Win32.AttachThreadInput(yo, hiloActivo, false); }
        }
    }
}
