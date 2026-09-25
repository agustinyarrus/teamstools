// sonda-adjuntos — prueba EN VIVO cómo recibe Teams imágenes, archivos y varios mensajes seguidos,
// escribiendo SIN mostrar Teams (ventana minimizada + foco por UIA + SendInput), siempre contra MI PROPIO chat.
//
//   sonda-adjuntos estado                      ventanas de Teams, chat abierto, editor, botón enviar, dónde está el foco
//   sonda-adjuntos abrir-yo                    abre mi chat "(Usted)" por UIA (Select, invisible)
//   sonda-adjuntos arbol [niveles]             vuelca el área de redacción (ancestros del editor) a out\
//   sonda-adjuntos ultimo [n]                  vuelca los últimos n mensajes del chat abierto a out\
//   sonda-adjuntos generar <ruta.png>          dibuja una imagen de prueba identificable (hora + texto)
//   sonda-adjuntos imagen <ruta> [--enviar] [--texto "..."] [--como-archivo]
//   sonda-adjuntos archivo <ruta> [--enviar] [--texto "..."]
//   sonda-adjuntos varios <n> [--espera ms]    n mensajes de texto seguidos
//   sonda-adjuntos limpiar                     vacía el editor (texto y adjuntos que hayan quedado)
//
// Cada corrida: guarda y restaura el portapapeles completo, espera a que no estés tipeando, mide cada 100 ms
// cuántas ventanas de Teams hay EN PANTALLA (tiene que dar 0) y devuelve el foco a la ventana que lo tenía.
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace SondaAdjuntos
{
    internal static class W
    {
        public delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        public const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        public static void Mover(IntPtr h, int x, int y) => SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
        [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inp, int size);
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO lii);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion U; }
        [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        public const int SW_HIDE = 0, SW_MINIMIZE = 6, SW_SHOWMINNOACTIVE = 7;
        const uint KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;
        public const ushort VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_RETURN = 0x0D, VK_DELETE = 0x2E, VK_BACK = 0x08, VK_ESCAPE = 0x1B, VK_MENU = 0x12, VK_DOWN = 0x28, VK_UP = 0x26, VK_TAB = 0x09;

        public static string Titulo(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, sb.Capacity); return sb.ToString(); }
        public static string Clase(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, sb.Capacity); return sb.ToString(); }
        public static bool Cloaked(IntPtr h) => DwmGetWindowAttribute(h, 14, out int v, 4) == 0 && v != 0;
        public static uint Pid(IntPtr h) { GetWindowThreadProcessId(h, out uint p); return p; }

        public static HashSet<uint> PidsTeams()
        {
            var s = new HashSet<uint>();
            foreach (var p in Process.GetProcessesByName("ms-teams")) { s.Add((uint)p.Id); p.Dispose(); }
            return s;
        }

        public static List<IntPtr> VentanasTeams(bool soloEnPantalla)
        {
            var pids = PidsTeams();
            var res = new List<IntPtr>();
            EnumWindows((h, l) =>
            {
                if (!pids.Contains(Pid(h))) return true;
                if (soloEnPantalla)
                {
                    if (!IsWindowVisible(h) || IsIconic(h) || Cloaked(h)) return true;
                    GetWindowRect(h, out var r);
                    if (r.Right - r.Left <= 1 || r.Bottom - r.Top <= 1 || r.Left <= -20000) return true;
                }
                res.Add(h);
                return true;
            }, IntPtr.Zero);
            return res;
        }

        public static int IdleSegundos()
        {
            var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref li)) return 0;
            return (int)(unchecked((uint)Environment.TickCount - li.dwTime) / 1000);
        }

        static void Tecla(ushort vk, bool up)
        {
            var inp = new INPUT[1];
            inp[0].type = 1;
            inp[0].U.ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 };
            SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
        }
        public static void Atajo(bool ctrl, bool shift, ushort vk)
        {
            if (ctrl) Tecla(VK_CONTROL, false);
            if (shift) Tecla(VK_SHIFT, false);
            Tecla(vk, false); Thread.Sleep(25); Tecla(vk, true);
            if (shift) Tecla(VK_SHIFT, true);
            if (ctrl) Tecla(VK_CONTROL, true);
        }
        public static void AtajoAlt(bool shift, ushort vk)
        {
            Tecla(VK_MENU, false);
            if (shift) Tecla(VK_SHIFT, false);
            Tecla(vk, false); Thread.Sleep(30); Tecla(vk, true);
            if (shift) Tecla(VK_SHIFT, true);
            Tecla(VK_MENU, true);
        }
        public static void Unicode(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            var inp = new INPUT[s.Length * 2];
            for (int i = 0; i < s.Length; i++)
            {
                inp[i * 2].type = 1; inp[i * 2].U.ki = new KEYBDINPUT { wScan = s[i], dwFlags = KEYEVENTF_UNICODE };
                inp[i * 2 + 1].type = 1; inp[i * 2 + 1].U.ki = new KEYBDINPUT { wScan = s[i], dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP };
            }
            SendInput((uint)inp.Length, inp, Marshal.SizeOf(typeof(INPUT)));
        }

        public static void Forzar(IntPtr h)
        {
            uint hiloActivo = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint yo = GetCurrentThreadId();
            bool att = hiloActivo != yo && AttachThreadInput(yo, hiloActivo, true);
            BringWindowToTop(h);
            SetForegroundWindow(h);
            if (att) AttachThreadInput(yo, hiloActivo, false);
        }
    }

    /// <summary>Mide cada 100 ms cuántas ventanas de Teams hay en pantalla y por dónde anduvo el foco.</summary>
    internal sealed class Vigia
    {
        volatile bool parar;
        Thread hilo;
        public int MaxEnPantalla;
        public readonly List<string> Eventos = new List<string>();
        public int Muestras;

        public void Iniciar()
        {
            hilo = new Thread(() =>
            {
                IntPtr fgPrev = IntPtr.Zero;
                var sw = Stopwatch.StartNew();
                while (!parar)
                {
                    var en = W.VentanasTeams(true);
                    Muestras++;
                    if (en.Count > MaxEnPantalla)
                    {
                        MaxEnPantalla = en.Count;
                        lock (Eventos) Eventos.Add($"{sw.ElapsedMilliseconds,6} ms  ⚠ {en.Count} ventana/s de Teams EN PANTALLA: " + string.Join(" | ", en.Select(h => $"{W.Clase(h)} «{W.Titulo(h)}»")));
                    }
                    var fg = W.GetForegroundWindow();
                    if (fg != fgPrev)
                    {
                        fgPrev = fg;
                        bool teams = W.PidsTeams().Contains(W.Pid(fg));
                        lock (Eventos) Eventos.Add($"{sw.ElapsedMilliseconds,6} ms  foco → {(teams ? "TEAMS " : "")}{W.Clase(fg)} «{Recortar(W.Titulo(fg), 50)}» {(W.IsIconic(fg) ? "(minimizada)" : "")}");
                    }
                    Thread.Sleep(100);
                }
            }) { IsBackground = true };
            hilo.Start();
        }
        public void Detener() { parar = true; hilo?.Join(1000); }
        static string Recortar(string s, int n) => s.Length > n ? s.Substring(0, n) + "…" : s;
    }

    internal static class Program
    {
        static string yo = Environment.GetEnvironmentVariable("TEAMS_YO") ?? "";   // tu nombre como lo muestra Teams («Apellido, Nombre»); variable de entorno TEAMS_YO
        static readonly StringBuilder log = new StringBuilder();
        static readonly Stopwatch reloj = Stopwatch.StartNew();
        static string carpetaOut;

        static void L(string s)
        {
            string linea = $"{reloj.ElapsedMilliseconds,6} ms  {s}";
            Console.WriteLine(linea);
            log.AppendLine(linea);
        }

        [STAThread]
        static int Main(string[] args)
        {
            try { W.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            Console.OutputEncoding = Encoding.UTF8;
            carpetaOut = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "out");
            Directory.CreateDirectory(carpetaOut);
            string modo = args.Length > 0 ? args[0].ToLowerInvariant() : "estado";
            string Opt(string nombre) { int i = Array.FindIndex(args, a => a.Equals(nombre, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            bool Flag(string nombre) => args.Any(a => a.Equals(nombre, StringComparison.OrdinalIgnoreCase));
            if (Opt("--yo") != null) yo = Opt("--yo");
            if (int.TryParse(Opt("--idle"), out int idleOpt)) idleSeg = idleOpt;
            todoElDoc = Flag("--todo");
            if (int.TryParse(Opt("--espera-max"), out int esperaOpt)) esperaMaxSeg = esperaOpt;
            int codigo = 0;
            try
            {
                switch (modo)
                {
                    case "estado": codigo = Estado(); break;
                    case "abrir-yo": codigo = AbrirYo() ? 0 : 1; break;
                    case "arbol": codigo = Arbol(int.TryParse(args.ElementAtOrDefault(1), out int nv) ? nv : 4); break;
                    case "ultimo": codigo = Ultimos(int.TryParse(args.ElementAtOrDefault(1), out int nu) ? nu : 2); break;
                    case "generar": Generar(args[1], Opt("--texto") ?? "sonda-adjuntos"); break;
                    case "imagen": codigo = Pegar(args[1], Flag("--como-archivo") ? "archivo" : "imagen", Flag("--enviar"), Opt("--texto")); break;
                    case "archivo": codigo = Pegar(args[1], "archivo", Flag("--enviar"), Opt("--texto")); break;
                    case "varios": codigo = Varios(int.Parse(args[1]), int.TryParse(Opt("--espera"), out int es) ? es : 800); break;
                    case "limpiar": codigo = Limpiar(); break;
                    case "buscar": codigo = Buscar(args[1]); break;
                    case "presencia-destapar": codigo = PresenciaDestapar(int.TryParse(args.ElementAtOrDefault(1), out int seg) ? seg : 15); break;
                    case "destapar-arbol": codigo = DestaparArbol(int.TryParse(args.ElementAtOrDefault(1), out int ms) ? ms : 1500); break;
                    case "cerrar-menu": codigo = CerrarMenu(); break;
                    case "adjuntar": codigo = Adjuntar(); break;
                    case "combo": codigo = Combo(args.ElementAtOrDefault(1)); break;
                    case "archivo-dialogo": codigo = ArchivoDialogo(args[1], Flag("--enviar")); break;
                    case "presencia": codigo = Presencia(int.TryParse(args.ElementAtOrDefault(1), out int np) ? np : 1, int.TryParse(Opt("--cada"), out int cada) ? cada : 3000); break;
                    default: L("modo desconocido: " + modo); codigo = 2; break;
                }
            }
            catch (Exception ex) { L("EXCEPCIÓN: " + ex); codigo = 3; }
            ReOcultar();
            try { File.WriteAllText(Path.Combine(carpetaOut, $"{DateTime.Now:HHmmss}-{modo}.txt"), log.ToString(), new UTF8Encoding(false)); } catch { }
            return codigo;
        }

        // ------------------------------------------------------------------ Teams por UIA

        static IntPtr hwnd;
        static bool todoElDoc;
        /// <summary>Segundos sin teclado ni mouse que exijo antes de robar el foco, y cuánto espero como máximo a que pase.</summary>
        static int idleSeg = 3, esperaMaxSeg = 90;

        static AutomationElement Doc()
        {
            var root = AutomationElement.FromHandle(hwnd);
            return root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "RootWebArea")) ?? root;
        }

        /// <summary>Ventana que destapé yo (SW_SHOWMINNOACTIVE) porque estaba guardada en la bandeja: la vuelvo a ocultar al salir.</summary>
        static IntPtr destapada = IntPtr.Zero;

        static AutomationElement ArbolChats(AutomationElement doc) =>
            doc.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tree), new PropertyCondition(AutomationElement.NameProperty, "Teams")));

        /// <summary>
        /// Encuentra la ventana de chat con el árbol montado. Si está guardada en la bandeja (oculta: 6 elementos, sin
        /// árbol) la destapa como minimizada sin activar y espera hasta 4 s a que Chromium monte el DOM; si quedó el
        /// «Menú de perfil» abierto lo cierra. Nunca aparece nada en pantalla.
        /// </summary>
        static bool BuscarVentana()
        {
            var candidatas = W.VentanasTeams(false).Where(h => W.Clase(h) == "TeamsWebView" && W.Titulo(h).Length > 0 && !W.Cloaked(h))
                .OrderByDescending(h => W.Titulo(h).StartsWith("Chat")).ThenByDescending(h => W.IsWindowVisible(h)).ToList();
            foreach (var h in candidatas)
            {
                try
                {
                    hwnd = h;
                    var doc = Doc();
                    var tree = ArbolChats(doc);
                    if (tree == null && !W.IsWindowVisible(h))
                    {
                        W.ShowWindow(h, W.SW_SHOWMINNOACTIVE);
                        destapada = h;
                        var sw = Stopwatch.StartNew();
                        while (sw.ElapsedMilliseconds < 4000 && tree == null && Popover(doc = Doc()) == null) { Thread.Sleep(200); tree = ArbolChats(doc); }
                        L($"«{W.Titulo(h)}» estaba en la bandeja: destapada minimizada sin activar, árbol {(tree != null ? "montado" : "NO montado")} en {sw.ElapsedMilliseconds} ms");
                    }
                    if (tree == null && Popover(doc) != null && CerrarPopover(h)) tree = ArbolChats(Doc());
                    if (tree == null) { if (destapada == h) { W.ShowWindow(h, W.SW_HIDE); destapada = IntPtr.Zero; } continue; }
                    return true;
                }
                catch { }
            }
            hwnd = IntPtr.Zero;
            return false;
        }

        static void ReOcultar()
        {
            if (destapada == IntPtr.Zero) return;
            try { if (W.IsWindowVisible(destapada)) { W.ShowWindow(destapada, W.SW_HIDE); L("ventana destapada por mí: vuelta a la bandeja"); } } catch { }
            destapada = IntPtr.Zero;
        }

        /// <summary>
        /// El «Menú de perfil» (popover de FluentUI, clase fui-PopoverSurface) deja el resto del DOM aria-hidden: sin él
        /// cerrado no hay árbol de chats, ni editor, ni botón Enviar, ni avatar. Queda abierto si el user cerró Teams a la
        /// bandeja con el menú desplegado.
        /// </summary>
        static AutomationElement Popover(AutomationElement doc)
        {
            try
            {
                var cr = new CacheRequest(); cr.Add(AutomationElement.NameProperty); cr.Add(AutomationElement.ClassNameProperty); cr.TreeScope = TreeScope.Element;
                using (cr.Activate())
                    foreach (AutomationElement e in doc.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)))
                        if ((e.Cached.ClassName ?? "").IndexOf("fui-PopoverSurface", StringComparison.OrdinalIgnoreCase) >= 0) return e;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Cierra el popover con Escape SIN mostrar Teams: la destapa como minimizada sin activar si hacía falta, enfoca un
        /// MenuItem de adentro (Teams pasa a foreground pero sigue minimizada), manda Escape y devuelve el foco.
        /// </summary>
        static bool CerrarPopover(IntPtr h)
        {
            IntPtr fg0 = W.GetForegroundWindow();
            bool oculta = !W.IsWindowVisible(h), minimizada = W.IsIconic(h) || oculta;
            if (oculta) { W.ShowWindow(h, W.SW_SHOWMINNOACTIVE); Thread.Sleep(250); }
            try
            {
                var pop = Popover(Doc());
                if (pop == null) { L("no había popover"); return true; }
                L($"popover abierto «{pop.Cached.Name}»: lo cierro con Escape sin mostrar Teams");
                var item = pop.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)) ?? pop;
                var sw = Stopwatch.StartNew();
                do { try { item.SetFocus(); } catch { } Thread.Sleep(100); }
                while (sw.ElapsedMilliseconds < 2000 && W.GetForegroundWindow() != h);
                if (W.GetForegroundWindow() != h) { L("no pude darle el teclado a Teams para cerrar el menú"); return false; }
                for (int i = 0; i < 3; i++)
                {
                    W.Atajo(false, false, W.VK_ESCAPE); Thread.Sleep(350);
                    if (Popover(Doc()) == null) { L($"popover cerrado con {i + 1} Escape (Teams minimizada={W.IsIconic(h)})"); return true; }
                }
                L("el popover sigue abierto tras 3 Escape");
                return false;
            }
            finally
            {
                if (minimizada && !W.IsIconic(h)) W.ShowWindow(h, W.SW_MINIMIZE);
                DevolverFoco(fg0, true);
                if (oculta) W.ShowWindow(h, W.SW_HIDE);
            }
        }

        static int CerrarMenu()
        {
            var ventana = W.VentanasTeams(false).Where(x => W.Clase(x) == "TeamsWebView" && W.Titulo(x).Length > 0)
                .OrderByDescending(x => W.Titulo(x).Contains("|")).FirstOrDefault();
            if (ventana == IntPtr.Zero) { L("sin ventana de Teams"); return 1; }
            hwnd = ventana;
            var espera = Stopwatch.StartNew();
            while (espera.ElapsedMilliseconds < 25000 && W.IdleSegundos() < 2) Thread.Sleep(250);
            L($"esperé {espera.ElapsedMilliseconds} ms a que no estés tipeando (idle {W.IdleSegundos()} s)");
            var vigia = new Vigia(); vigia.Iniciar();
            bool ok = CerrarPopover(ventana);
            Thread.Sleep(300); vigia.Detener();
            lock (vigia.Eventos) foreach (var ev in vigia.Eventos) L("   vigía " + ev);
            L($"VISIBILIDAD: máximo de ventanas de Teams en pantalla = {vigia.MaxEnPantalla} en {vigia.Muestras} muestras · foco final «{W.Titulo(W.GetForegroundWindow())}»");
            return ok ? 0 : 1;
        }

        static AutomationElement Editor(AutomationElement doc)
        {
            foreach (AutomationElement e in doc.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
                if ((e.Current.AutomationId ?? "").StartsWith("new-message-", StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        static AutomationElement BotonEnviar(AutomationElement doc)
        {
            foreach (AutomationElement b in doc.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
            {
                string n = b.Current.Name ?? "";
                if (n.StartsWith("Enviar", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Send", StringComparison.OrdinalIgnoreCase)) return b;
            }
            return null;
        }

        static string Valor(AutomationElement editor)
        {
            try { if (editor.TryGetCurrentPattern(ValuePattern.Pattern, out object vp)) return Regex.Replace(((ValuePattern)vp).Current.Value ?? "", "[​‌‍⁠﻿\r]", "").TrimEnd('\n'); } catch { }
            return "";
        }

        static string ChatAbierto()
        {
            var m = Regex.Match(W.Titulo(hwnd), @"^(?:Chat|Chat de grupo|Group chat)\s*\|\s*(.+?)\s*\|\s*Microsoft Teams", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        static bool EsMiChat() => ChatAbierto().IndexOf(yo, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Contenedor del redactor: el primer ancestro del editor que contiene el botón Enviar, y uno más arriba.</summary>
        static AutomationElement Redactor(AutomationElement editor, int extra = 1)
        {
            var w = TreeWalker.RawViewWalker;
            var actual = editor;
            for (int i = 0; i < 12 && actual != null; i++)
            {
                var padre = w.GetParent(actual);
                if (padre == null) break;
                actual = padre;
                bool tieneEnviar = false;
                foreach (AutomationElement b in actual.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                {
                    string n = b.Current.Name ?? "";
                    if (n.StartsWith("Enviar", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Send", StringComparison.OrdinalIgnoreCase)) { tieneEnviar = true; break; }
                }
                if (tieneEnviar)
                {
                    for (int k = 0; k < extra; k++) { var p = w.GetParent(actual); if (p != null) actual = p; }
                    return actual;
                }
            }
            return actual;
        }

        static string Desc(AutomationElement e)
        {
            try
            {
                var c = e.Current;
                string tipo = c.ControlType.ProgrammaticName.Replace("ControlType.", "");
                string nombre = (c.Name ?? "").Replace("\n", " ⏎ ");
                if (nombre.Length > 140) nombre = nombre.Substring(0, 140) + "…";
                var patrones = string.Join(",", e.GetSupportedPatterns().Select(p => p.ProgrammaticName.Replace("PatternIdentifiers.Pattern", "")));
                return $"{tipo} «{nombre}» id={c.AutomationId} clase={c.ClassName} habil={c.IsEnabled}" + (c.IsOffscreen ? " offscreen" : "") + (patrones.Length > 0 ? " [" + patrones + "]" : "");
            }
            catch (Exception ex) { return "(elemento muerto: " + ex.GetType().Name + ")"; }
        }

        static void Volcar(AutomationElement e, StringBuilder sb, int prof, int max)
        {
            if (prof > max || e == null) return;
            sb.Append(new string(' ', prof * 2)).AppendLine(Desc(e));
            var w = TreeWalker.RawViewWalker;
            AutomationElement h = null;
            try { h = w.GetFirstChild(e); } catch { }
            int n = 0;
            while (h != null && n++ < 400)
            {
                Volcar(h, sb, prof + 1, max);
                try { h = w.GetNextSibling(h); } catch { h = null; }
            }
        }

        /// <summary>Firma del redactor para ver qué aparece y desaparece mientras se sube un adjunto.</summary>
        static List<string> Firma(AutomationElement raiz)
        {
            var res = new List<string>();
            void Rec(AutomationElement e, int prof)
            {
                if (prof > (todoElDoc ? 45 : 14) || e == null) return;
                try
                {
                    var c = e.Current;
                    string nombre = (c.Name ?? "").Replace("\n", " ⏎ ");
                    if (nombre.Length > 90) nombre = nombre.Substring(0, 90) + "…";
                    res.Add($"{new string(' ', prof)}{c.ControlType.ProgrammaticName.Replace("ControlType.", "")}|{nombre}|{c.AutomationId}|{(c.IsEnabled ? "" : "deshab")}");
                }
                catch { return; }
                var w = TreeWalker.RawViewWalker;
                AutomationElement h = null;
                try { h = w.GetFirstChild(e); } catch { }
                int n = 0;
                while (h != null && n++ < 300) { Rec(h, prof + 1); try { h = w.GetNextSibling(h); } catch { h = null; } }
            }
            Rec(raiz, 0);
            return res;
        }

        // ------------------------------------------------------------------ modos de solo lectura

        static int Estado()
        {
            foreach (var h in W.VentanasTeams(false))
            {
                string cl = W.Clase(h);
                if (cl != "TeamsWebView") continue;
                W.GetWindowRect(h, out var r);
                L($"ventana {h} «{W.Titulo(h)}» visible={W.IsWindowVisible(h)} minimizada={W.IsIconic(h)} cloaked={W.Cloaked(h)} rect={r.Left},{r.Top},{r.Right - r.Left}x{r.Bottom - r.Top}");
            }
            if (!BuscarVentana()) { L("no encontré la ventana de chat de Teams con el árbol montado"); return 1; }
            L($"ventana elegida {hwnd} · chat abierto «{ChatAbierto()}» · es mi chat={EsMiChat()}");
            var doc = Doc();
            var ed = Editor(doc);
            L(ed == null ? "sin editor" : $"editor id={ed.Current.AutomationId} valor=«{Valor(ed)}»");
            var b = BotonEnviar(doc);
            L(b == null ? "sin botón Enviar" : $"botón «{b.Current.Name}» habilitado={b.Current.IsEnabled}");
            var fg = W.GetForegroundWindow();
            L($"foco en «{W.Titulo(fg)}» ({W.Clase(fg)}) · idle {W.IdleSegundos()} s · ventanas de Teams en pantalla: {W.VentanasTeams(true).Count}");
            return 0;
        }

        static bool AbrirYo()
        {
            if (!BuscarVentana()) { L("sin ventana de Teams"); return false; }
            if (EsMiChat()) { L("mi chat ya está abierto"); return true; }
            var doc = Doc();
            var tree = doc.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tree), new PropertyCondition(AutomationElement.NameProperty, "Teams")));
            foreach (AutomationElement it in tree.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem)))
            {
                string n = it.Current.Name ?? "";
                if (n.IndexOf(yo, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!(n.Contains("(Usted)") || n.Contains("(You)") || n.Contains("(Tú)"))) continue;
                L($"selecciono «{n}»");
                try { ((SelectionItemPattern)it.GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); }
                catch { ((InvokePattern)it.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); }
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 6000) { Thread.Sleep(250); if (EsMiChat()) { L($"abierto «{ChatAbierto()}» en {sw.ElapsedMilliseconds} ms"); return true; } }
                L($"no se abrió: el título quedó en «{W.Titulo(hwnd)}»");
                return false;
            }
            L("no encontré mi chat (Usted) en la lista");
            return false;
        }

        static int Arbol(int extra)
        {
            if (!BuscarVentana()) { L("sin ventana"); return 1; }
            var doc = Doc();
            var ed = Editor(doc);
            if (ed == null) { L("sin editor"); return 1; }
            var raiz = Redactor(ed, extra);
            var sb = new StringBuilder();
            Volcar(raiz, sb, 0, 30);
            string ruta = Path.Combine(carpetaOut, $"{DateTime.Now:HHmmss}-arbol-redactor.txt");
            File.WriteAllText(ruta, sb.ToString(), new UTF8Encoding(false));
            L($"redactor volcado ({sb.Length} chars) → {ruta}");
            return 0;
        }

        static int Ultimos(int n)
        {
            if (!BuscarVentana()) { L("sin ventana"); return 1; }
            var doc = Doc();
            var cuerpos = new List<AutomationElement>();
            var cr = new CacheRequest(); cr.Add(AutomationElement.AutomationIdProperty); cr.TreeScope = TreeScope.Element;
            using (cr.Activate())
                foreach (AutomationElement e in doc.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                    if ((e.Cached.AutomationId ?? "").StartsWith("message-body-", StringComparison.Ordinal)) cuerpos.Add(e);
            var sb = new StringBuilder();
            foreach (var c in cuerpos.Skip(Math.Max(0, cuerpos.Count - n)))
            {
                sb.AppendLine("==================== " + c.Current.AutomationId);
                Volcar(c, sb, 0, 20);
            }
            string ruta = Path.Combine(carpetaOut, $"{DateTime.Now:HHmmss}-ultimos.txt");
            File.WriteAllText(ruta, sb.ToString(), new UTF8Encoding(false));
            L($"{cuerpos.Count} mensajes en el chat «{ChatAbierto()}»; los últimos {Math.Min(n, cuerpos.Count)} → {ruta}");
            return 0;
        }

        static void Generar(string ruta, string texto)
        {
            using (var bmp = new Bitmap(720, 240))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var fondo = new LinearGradientBrush(new Rectangle(0, 0, 720, 240), ColorTranslator.FromHtml("#0f1015"), ColorTranslator.FromHtml("#1d1a2b"), 20f))
                    g.FillRectangle(fondo, 0, 0, 720, 240);
                string[] pastel = { "#c4b5fd", "#8fd6cc", "#f6c0a0", "#f3b9d2", "#a8cff2", "#b5dfa8" };
                for (int i = 0; i < pastel.Length; i++)
                    using (var b = new SolidBrush(ColorTranslator.FromHtml(pastel[i]))) g.FillEllipse(b, 40 + i * 34, 40, 22, 22);
                using (var f1 = new Font("Cascadia Code ExtraLight", 30f, GraphicsUnit.Pixel))
                using (var f2 = new Font("Cascadia Code ExtraLight", 20f, GraphicsUnit.Pixel))
                using (var b1 = new SolidBrush(ColorTranslator.FromHtml("#d3d6df")))
                using (var b2 = new SolidBrush(ColorTranslator.FromHtml("#8b91a3")))
                {
                    g.DrawString(texto, f1, b1, 40, 90);
                    g.DrawString($"teams-autoleave · {DateTime.Now:dd/MM HH:mm:ss}", f2, b2, 42, 150);
                }
                bmp.Save(ruta, ImageFormat.Png);
            }
            L("imagen de prueba → " + ruta);
        }

        // ------------------------------------------------------------------ portapapeles

        static IDataObject CopiarPortapapeles()
        {
            try
            {
                var d = Clipboard.GetDataObject();
                if (d == null) return null;
                var copia = new DataObject();
                int n = 0;
                foreach (var f in d.GetFormats(false))
                {
                    try { var v = d.GetData(f, false); if (v != null) { copia.SetData(f, false, v); n++; } } catch { }
                }
                L($"portapapeles guardado: {n} formato/s ({string.Join(", ", d.GetFormats(false).Take(8))})");
                return n > 0 ? copia : null;
            }
            catch (Exception ex) { L("no pude leer el portapapeles: " + ex.Message); return null; }
        }

        static void RestaurarPortapapeles(IDataObject copia)
        {
            try
            {
                if (copia == null) Clipboard.Clear();
                else Clipboard.SetDataObject(copia, true, 8, 100);
                L("portapapeles restaurado");
            }
            catch (Exception ex) { L("no pude restaurar el portapapeles: " + ex.Message); }
        }

        static void PonerImagen(string ruta)
        {
            var bytes = File.ReadAllBytes(ruta);
            using (var ms0 = new MemoryStream(bytes))
            using (var bmp = new Bitmap(ms0))
            {
                var data = new DataObject();
                var png = new MemoryStream();
                bmp.Save(png, ImageFormat.Png);
                data.SetData("PNG", false, png);
                data.SetData(DataFormats.Bitmap, true, new Bitmap(bmp));
                Clipboard.SetDataObject(data, true, 8, 100);
                L($"portapapeles ← imagen {bmp.Width}x{bmp.Height} ({png.Length / 1024} KB PNG + CF_BITMAP)");
            }
        }

        static void PonerArchivos(params string[] rutas)
        {
            var data = new DataObject();
            var sc = new StringCollection();
            sc.AddRange(rutas.Select(Path.GetFullPath).ToArray());
            data.SetFileDropList(sc);
            data.SetData("Preferred DropEffect", false, new MemoryStream(BitConverter.GetBytes(1)));
            Clipboard.SetDataObject(data, true, 8, 100);
            L("portapapeles ← archivo/s (CF_HDROP): " + string.Join(", ", rutas.Select(Path.GetFileName)));
        }

        // ------------------------------------------------------------------ escribir sin mostrar Teams

        sealed class Sesion : IDisposable
        {
            public IntPtr Fg0;
            public bool EstabaMinimizada, EstabaOculta;
            public AutomationElement Doc, Editor;
            public string Placeholder = "";
            public Vigia Vigia = new Vigia();

            public void Dispose()
            {
                try { if (EstabaMinimizada && !W.IsIconic(hwnd)) W.ShowWindow(hwnd, W.SW_MINIMIZE); } catch { }
                DevolverFoco(Fg0, EstabaMinimizada);
                if (EstabaOculta) { try { W.ShowWindow(hwnd, W.SW_HIDE); destapada = IntPtr.Zero; L("Teams vuelta a la bandeja"); } catch { } }
                Thread.Sleep(400);
                Vigia.Detener();
                lock (Vigia.Eventos) foreach (var ev in Vigia.Eventos) L("   vigía " + ev);
                L($"VISIBILIDAD: máximo de ventanas de Teams en pantalla = {Vigia.MaxEnPantalla} en {Vigia.Muestras} muestras · foco final en «{W.Titulo(W.GetForegroundWindow())}» · ¿devuelto? {W.GetForegroundWindow() == Fg0}");
            }
        }

        static void DevolverFoco(IntPtr fg0, bool minimizarTeams)
        {
            var pids = W.PidsTeams();
            try { var fg = W.GetForegroundWindow(); if (minimizarTeams && pids.Contains(W.Pid(fg))) { W.ShowWindow(fg, W.SW_MINIMIZE); Thread.Sleep(150); } } catch { }
            if (fg0 != IntPtr.Zero && W.IsWindow(fg0) && W.IsWindowVisible(fg0))
                for (int i = 0; i < 4 && W.GetForegroundWindow() != fg0; i++) { W.Forzar(fg0); Thread.Sleep(130); }
            // 🚨 pase lo que pase el foco NO puede quedar en Teams (lo que tipee el user caería en el cuadro de mensaje):
            // último recurso, la primera ventana visible con título que no sea de Teams
            for (int i = 0; i < 3 && pids.Contains(W.Pid(W.GetForegroundWindow())); i++)
            {
                IntPtr otra = IntPtr.Zero;
                W.EnumWindows((h, l) => { if (W.IsWindowVisible(h) && !W.IsIconic(h) && W.Titulo(h).Length > 0 && !pids.Contains(W.Pid(h)) && !W.Cloaked(h)) { otra = h; return false; } return true; }, IntPtr.Zero);
                if (otra == IntPtr.Zero) break;
                L($"el foco quedó en Teams: se lo doy a «{W.Titulo(otra)}»");
                W.Forzar(otra); Thread.Sleep(130);
            }
        }

        static Sesion Preparar()
        {
            if (!BuscarVentana()) throw new Exception("sin ventana de chat de Teams");
            if (!EsMiChat() && !AbrirYo()) throw new Exception("no pude abrir MI chat: no escribo en ningún otro");
            var s = new Sesion { Fg0 = W.GetForegroundWindow(), EstabaMinimizada = W.IsIconic(hwnd), EstabaOculta = !W.IsWindowVisible(hwnd) || destapada == hwnd };
            L($"Teams: minimizada={s.EstabaMinimizada} oculta={s.EstabaOculta} · foco inicial «{W.Titulo(s.Fg0)}»");
            if (!s.EstabaMinimizada && !s.EstabaOculta) L("⚠ Teams está EN PANTALLA: lo que haga se va a ver (no la toco, así la encontró el user)");
            s.Vigia.Iniciar();
            if (!W.IsWindowVisible(hwnd)) { W.ShowWindow(hwnd, W.SW_SHOWMINNOACTIVE); Thread.Sleep(260); L("destapada como minimizada sin activar"); }
            s.Doc = Doc();
            s.Editor = Editor(s.Doc);
            if (s.Editor == null) throw new Exception("sin editor");
            s.Placeholder = Valor(s.Editor);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < esperaMaxSeg * 1000L && W.IdleSegundos() < idleSeg) Thread.Sleep(250);
            L($"esperé {sw.ElapsedMilliseconds} ms a que no estés usando la PC (idle {W.IdleSegundos()} s, pedía {idleSeg} s)");
            if (W.IdleSegundos() < idleSeg) { s.Dispose(); throw new Exception($"estás usando la PC hace {esperaMaxSeg} s seguidos: no te robo el foco, pruebo más tarde"); }
            try
            {
                if (!EsMiChat()) throw new Exception("el chat cambió mientras esperaba: aborto");
                Enfocar(s);
            }
            catch { s.Dispose(); throw; }
            return s;
        }

        /// <summary>
        /// El SendInput va a la ventana en foreground, así que Teams tiene que pasar a foreground (minimizada: no se ve).
        /// Primero SetFocus del editor por UIA (lo hace el propio proceso de Teams); si Windows no le da el foreground
        /// (bloqueo por input reciente: p. ej. la F15 fantasma de la app cuenta como input), me cuelgo del hilo activo con
        /// AttachThreadInput y se lo doy yo, y vuelvo a enfocar el editor.
        /// </summary>
        static void Enfocar(Sesion s)
        {
            var sw = Stopwatch.StartNew();
            bool forzado = false;
            while (sw.ElapsedMilliseconds < 3000 && W.GetForegroundWindow() != hwnd)
            {
                try { s.Editor.SetFocus(); } catch { }
                Thread.Sleep(100);
                if (W.GetForegroundWindow() == hwnd) break;
                if (sw.ElapsedMilliseconds > 600 && !forzado) { W.Forzar(hwnd); forzado = true; Thread.Sleep(80); L("SetFocus no alcanzó: foreground forzado con AttachThreadInput"); }
            }
            if (W.GetForegroundWindow() != hwnd) throw new Exception("no pude enfocar el editor de Teams");
            try { s.Editor.SetFocus(); } catch { }
            L($"editor enfocado en {sw.ElapsedMilliseconds} ms (Teams en foreground, minimizada={W.IsIconic(hwnd)}{(forzado ? ", forzado" : "")})");
        }

        static void Tipear(string texto)
        {
            var lineas = texto.Replace("\r", "").Split('\n');
            for (int i = 0; i < lineas.Length; i++)
            {
                if (i > 0) { W.Atajo(false, true, W.VK_RETURN); Thread.Sleep(90); }
                W.Unicode(lineas[i]);
                Thread.Sleep(Math.Min(400, 40 + lineas[i].Length * 4));
            }
        }

        static bool Enviar(Sesion s, Func<bool> redactorVacio)
        {
            var b = BotonEnviar(s.Doc);
            if (b == null) { L("sin botón Enviar"); return false; }
            L($"Enviar «{b.Current.Name}» habilitado={b.Current.IsEnabled} → Invoke");
            ((InvokePattern)b.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 15000) { Thread.Sleep(250); if (redactorVacio()) { L($"redactor vacío {sw.ElapsedMilliseconds} ms después de Enviar"); return true; } }
            L("el redactor NO se vació tras Enviar");
            return false;
        }

        // ------------------------------------------------------------------ imagen / archivo

        static int Pegar(string ruta, string como, bool enviar, string texto)
        {
            ruta = Path.GetFullPath(ruta);
            if (!File.Exists(ruta)) { L("no existe " + ruta); return 1; }
            var clip = CopiarPortapapeles();
            try
            {
                using (var s = Preparar())
                {
                    // --todo: comparar el DOCUMENTO entero, por si el adjunto aparece fuera del redactor (tarjeta de archivo arriba del cuadro)
                    var redactor = todoElDoc ? s.Doc : Redactor(s.Editor, 1);
                    var antes = Firma(redactor);
                    L($"redactor antes: {antes.Count} nodos · editor «{Valor(s.Editor)}»");
                    if (!string.IsNullOrEmpty(texto)) { Tipear(texto); L($"texto tipeado · editor «{Valor(s.Editor)}»"); }
                    if (como == "imagen") PonerImagen(ruta); else PonerArchivos(ruta);
                    Enfocar(s);
                    W.Atajo(true, false, (ushort)'V');
                    L("Ctrl+V enviado");

                    // mirar qué aparece y cuándo se estabiliza
                    var previo = antes;
                    var sw = Stopwatch.StartNew();
                    long ultimoCambio = 0;
                    bool huboCambio = false;
                    while (sw.ElapsedMilliseconds < 60000)
                    {
                        Thread.Sleep(300);
                        List<string> ahora;
                        try { ahora = Firma(redactor); }
                        catch { redactor = todoElDoc ? Doc() : Redactor(Editor(Doc()) ?? s.Editor, 1); continue; }
                        var nuevos = ahora.Except(previo).ToList();
                        var idos = previo.Except(ahora).ToList();
                        if (nuevos.Count + idos.Count > 0)
                        {
                            huboCambio = true; ultimoCambio = sw.ElapsedMilliseconds;
                            foreach (var n in nuevos.Take(40)) L($"   + {n.Trim()}");
                            foreach (var n in idos.Take(40)) L($"   - {n.Trim()}");
                            previo = ahora;
                        }
                        bool cargando = ahora.Any(x => Regex.IsMatch(x, @"ProgressBar|Cargando|Subiendo|Uploading|Loading|\d+\s?%", RegexOptions.IgnoreCase));
                        if (huboCambio && !cargando && sw.ElapsedMilliseconds - ultimoCambio > 3500) { L($"estable {sw.ElapsedMilliseconds - ultimoCambio} ms sin cambios · cargando={cargando}"); break; }
                        if (!huboCambio && sw.ElapsedMilliseconds > 8000) { L("⚠ 8 s sin ningún cambio en el redactor: el pegado no entró"); break; }
                    }
                    var sb = new StringBuilder(); Volcar(redactor, sb, 0, 30);
                    string rutaArbol = Path.Combine(carpetaOut, $"{DateTime.Now:HHmmss}-redactor-con-{como}.txt");
                    File.WriteAllText(rutaArbol, sb.ToString(), new UTF8Encoding(false));
                    L($"árbol del redactor con el adjunto → {rutaArbol}");

                    if (enviar && huboCambio)
                    {
                        bool ok = Enviar(s, () =>
                        {
                            var f = Firma(Redactor(Editor(Doc()) ?? s.Editor, 1));
                            string v = Valor(Editor(Doc()) ?? s.Editor);
                            return (v.Length == 0 || v == s.Placeholder) && f.Count <= antes.Count + 2;
                        });
                        L(ok ? "ENVIADO" : "NO SE ENVIÓ");
                        return ok ? 0 : 1;
                    }
                    if (!enviar)
                    {
                        // dejar el redactor como estaba: Ctrl+A + Supr hasta que quede vacío
                        for (int i = 0; i < 5; i++)
                        {
                            W.Atajo(true, false, (ushort)'A'); Thread.Sleep(90);
                            W.Atajo(false, false, W.VK_DELETE); Thread.Sleep(450);
                            var f = Firma(Redactor(Editor(Doc()) ?? s.Editor, 1));
                            if (f.Count <= antes.Count && (Valor(s.Editor).Length == 0 || Valor(s.Editor) == s.Placeholder)) { L($"redactor limpio tras {i + 1} vuelta/s"); break; }
                        }
                    }
                    return huboCambio ? 0 : 1;
                }
            }
            finally { RestaurarPortapapeles(clip); }
        }

        static int Varios(int n, int esperaMs)
        {
            using (var s = Preparar())
            {
                for (int i = 1; i <= n; i++)
                {
                    if (!EsMiChat()) { L("el chat cambió: corto"); return 1; }
                    Enfocar(s);
                    string t = $"sonda · mensaje {i} de {n} · {DateTime.Now:HH:mm:ss}";
                    Tipear(t);
                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 3000 && Valor(s.Editor) != t) Thread.Sleep(120);
                    if (Valor(s.Editor) != t) { L($"el editor quedó con «{Valor(s.Editor)}»"); return 1; }
                    if (!Enviar(s, () => { string v = Valor(s.Editor); return v.Length == 0 || v == s.Placeholder; })) return 1;
                    if (i < n) Thread.Sleep(esperaMs);
                }
                return 0;
            }
        }

        /// <summary>
        /// SOLO LECTURA: qué estado de presencia expone Teams por UIA en cada ventana (visible, minimizada u oculta),
        /// tanto en el botón del avatar como en el TreeItem de mi chat "(Usted)". Repite n veces cada `cada` ms.
        /// </summary>
        static int Presencia(int veces, int cada)
        {
            var reAvatar = new Regex(@"(?:estado|status)\s+(.+?)\s*$", RegexOptions.IgnoreCase);
            for (int vuelta = 1; vuelta <= veces; vuelta++)
            {
                L($"---- vuelta {vuelta} · idle del SO {W.IdleSegundos()} s");
                foreach (var h in W.VentanasTeams(false).Where(x => W.Clase(x) == "TeamsWebView"))
                {
                    string estadoVentana = $"visible={W.IsWindowVisible(h)} minimizada={W.IsIconic(h)} cloaked={W.Cloaked(h)}";
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        var root = AutomationElement.FromHandle(h);
                        var cr = new CacheRequest(); cr.Add(AutomationElement.NameProperty); cr.Add(AutomationElement.AutomationIdProperty); cr.Add(AutomationElement.ControlTypeProperty); cr.TreeScope = TreeScope.Element;
                        var avatares = new List<string>();
                        string miItem = "";
                        using (cr.Activate())
                        {
                            var doc = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "RootWebArea")) ?? root;
                            foreach (AutomationElement e in doc.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                            {
                                string id = e.Cached.AutomationId ?? "", n = e.Cached.Name ?? "";
                                if (id.IndexOf("me-control-avatar", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    var m = reAvatar.Match(n);
                                    avatares.Add($"{e.Cached.ControlType.ProgrammaticName.Replace("ControlType.", "")} id={id} «{n}» → {(m.Success ? m.Groups[1].Value : "(no matchea)")}");
                                }
                                if (e.Cached.ControlType == ControlType.TreeItem && n.IndexOf(yo, StringComparison.OrdinalIgnoreCase) >= 0 && (n.Contains("(Usted)") || n.Contains("(You)")))
                                    miItem = n;
                            }
                        }
                        L($"ventana «{W.Titulo(h)}» {estadoVentana} · leída en {sw.ElapsedMilliseconds} ms");
                        if (avatares.Count == 0) L("   sin botón de avatar en esta ventana");
                        foreach (var a in avatares) L("   avatar: " + a);
                        if (miItem.Length > 0) L("   mi chat en la lista: «" + miItem + "»");
                    }
                    catch (Exception ex) { L($"ventana «{W.Titulo(h)}» {estadoVentana} · error UIA: {ex.GetType().Name} {ex.Message}"); }
                }
                if (vuelta < veces) Thread.Sleep(cada);
            }
            return 0;
        }

        /// <summary>
        /// Destapa la ventana principal de Teams (oculta en la bandeja) como MINIMIZADA SIN ACTIVAR — nunca aparece en
        /// pantalla —, lee cada 500 ms cuántos elementos expone el árbol y qué dice el avatar, y la vuelve a ocultar.
        /// Sirve para saber si el estado del avatar es confiable y cuánto tarda en montarse.
        /// </summary>
        static int PresenciaDestapar(int segundos)
        {
            var ventana = W.VentanasTeams(false).Where(x => W.Clase(x) == "TeamsWebView" && W.Titulo(x).Length > 0)
                .OrderByDescending(x => W.Titulo(x).Contains("|")).FirstOrDefault();
            if (ventana == IntPtr.Zero) { L("sin ventana de Teams"); return 1; }
            bool oculta = !W.IsWindowVisible(ventana), minimizada = W.IsIconic(ventana);
            L($"ventana «{W.Titulo(ventana)}» oculta={oculta} minimizada={minimizada}");
            var vigia = new Vigia(); vigia.Iniciar();
            var reAvatar = new Regex(@"(?:estado|status)\s+(.+?)\s*$", RegexOptions.IgnoreCase);
            try
            {
                if (oculta) { W.ShowWindow(ventana, W.SW_SHOWMINNOACTIVE); L("destapada como minimizada sin activar"); }
                var sw = Stopwatch.StartNew();
                string ultimo = null;
                while (sw.ElapsedMilliseconds < segundos * 1000)
                {
                    int total = 0; var avatares = new List<string>(); string miItem = "";
                    try
                    {
                        var cr = new CacheRequest(); cr.Add(AutomationElement.NameProperty); cr.Add(AutomationElement.AutomationIdProperty); cr.Add(AutomationElement.ControlTypeProperty); cr.TreeScope = TreeScope.Element;
                        using (cr.Activate())
                            foreach (AutomationElement e in AutomationElement.FromHandle(ventana).FindAll(TreeScope.Descendants, Condition.TrueCondition))
                            {
                                total++;
                                string id = e.Cached.AutomationId ?? "", n = e.Cached.Name ?? "";
                                if (id.IndexOf("me-control-avatar", StringComparison.OrdinalIgnoreCase) >= 0) { var m = reAvatar.Match(n); avatares.Add($"«{n}» → {(m.Success ? m.Groups[1].Value : "(no matchea)")}"); }
                                if (e.Cached.ControlType == ControlType.TreeItem && n.IndexOf(yo, StringComparison.OrdinalIgnoreCase) >= 0 && (n.Contains("(Usted)") || n.Contains("(You)"))) miItem = n;
                            }
                    }
                    catch (Exception ex) { avatares.Add("error " + ex.GetType().Name); }
                    string foto = $"{total} elementos · avatar {(avatares.Count == 0 ? "—" : string.Join(" / ", avatares))} · mi chat «{miItem}»";
                    if (foto != ultimo) { L($"t={sw.ElapsedMilliseconds,5} ms  {foto}"); ultimo = foto; }
                    Thread.Sleep(500);
                }
            }
            finally
            {
                if (oculta) { W.ShowWindow(ventana, W.SW_HIDE); L("vuelta a ocultar"); }
                Thread.Sleep(300);
                vigia.Detener();
                lock (vigia.Eventos) foreach (var ev in vigia.Eventos) L("   vigía " + ev);
                L($"VISIBILIDAD: máximo de ventanas de Teams en pantalla = {vigia.MaxEnPantalla} en {vigia.Muestras} muestras");
            }
            return 0;
        }

        /// <summary>
        /// Destapa la ventana guardada como minimizada sin activar, espera `esperaMs`, vuelca TODO el árbol a out\ y la
        /// vuelve a ocultar. Para ver en qué pantalla quedó Teams cuando el árbol se monta a medias.
        /// </summary>
        static int DestaparArbol(int esperaMs)
        {
            var ventana = W.VentanasTeams(false).Where(x => W.Clase(x) == "TeamsWebView" && W.Titulo(x).Length > 0)
                .OrderByDescending(x => W.Titulo(x).Contains("|")).FirstOrDefault();
            if (ventana == IntPtr.Zero) { L("sin ventana de Teams"); return 1; }
            bool oculta = !W.IsWindowVisible(ventana);
            L($"ventana «{W.Titulo(ventana)}» oculta={oculta} minimizada={W.IsIconic(ventana)}");
            try
            {
                if (oculta) { W.ShowWindow(ventana, W.SW_SHOWMINNOACTIVE); L("destapada como minimizada sin activar"); }
                Thread.Sleep(esperaMs);
                var sb = new StringBuilder();
                Volcar(AutomationElement.FromHandle(ventana), sb, 0, 40);
                string ruta = Path.Combine(carpetaOut, $"{DateTime.Now:HHmmss}-arbol-destapado.txt");
                File.WriteAllText(ruta, sb.ToString(), new UTF8Encoding(false));
                L($"árbol volcado: {sb.Length} chars → {ruta}");
            }
            finally { if (oculta) { W.ShowWindow(ventana, W.SW_HIDE); L("vuelta a ocultar"); } }
            return 0;
        }

        /// <summary>SOLO LECTURA: todos los elementos de todas las ventanas de Teams cuyo nombre o id matchea la regex.</summary>
        static int Buscar(string patron)
        {
            var re = new Regex(patron, RegexOptions.IgnoreCase);
            foreach (var h in W.VentanasTeams(false).Where(x => W.Clase(x) == "TeamsWebView"))
            {
                L($"ventana «{W.Titulo(h)}» visible={W.IsWindowVisible(h)} minimizada={W.IsIconic(h)}");
                try
                {
                    var root = AutomationElement.FromHandle(h);
                    var cr = new CacheRequest(); cr.Add(AutomationElement.NameProperty); cr.Add(AutomationElement.AutomationIdProperty); cr.Add(AutomationElement.ControlTypeProperty); cr.TreeScope = TreeScope.Element;
                    int total = 0, hits = 0;
                    using (cr.Activate())
                        foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                        {
                            total++;
                            string id = e.Cached.AutomationId ?? "", n = e.Cached.Name ?? "";
                            if (!re.IsMatch(n) && !re.IsMatch(id)) continue;
                            if (++hits > 60) continue;
                            L($"   {e.Cached.ControlType.ProgrammaticName.Replace("ControlType.", "")} id={id} «{(n.Length > 160 ? n.Substring(0, 160) + "…" : n)}»");
                        }
                    L($"   {hits} coincidencia/s en {total} elementos");
                }
                catch (Exception ex) { L("   error UIA: " + ex.Message); }
            }
            return 0;
        }

        /// <summary>
        /// Invoca el botón «Adjuntar archivos» y vuelca lo que aparece (menú en el DOM vs. diálogo nativo de Windows),
        /// para saber si hay un camino INVISIBLE para archivos que no sean imagen. Solo mi chat, no envía nada.
        /// </summary>
        static int Adjuntar()
        {
            using (var s = Preparar())
            {
                var doc = s.Doc;
                AutomationElement botonAdj = null;
                foreach (AutomationElement b in doc.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                {
                    string n = b.Current.Name ?? "";
                    if (n.IndexOf("Adjuntar", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Attach", StringComparison.OrdinalIgnoreCase) >= 0) { botonAdj = b; break; }
                }
                if (botonAdj == null) { L("no encontré el botón Adjuntar archivos"); return 1; }
                L($"botón «{botonAdj.Current.Name}» → Invoke");
                var antesVentanas = new System.Collections.Generic.HashSet<IntPtr>(W.VentanasTeams(false));
                var todas0 = TopLevels();
                try { ((InvokePattern)botonAdj.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); }
                catch (Exception ex) { L("Invoke falló: " + ex.Message + " · pruebo ExpandCollapse");
                    try { ((ExpandCollapsePattern)botonAdj.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand(); } catch (Exception e2) { L("ExpandCollapse falló: " + e2.Message); } }
                Thread.Sleep(1500);
                // ¿apareció una ventana nueva top-level (diálogo nativo)?
                foreach (var h in TopLevels())
                    if (!todas0.Contains(h) && W.Titulo(h).Length > 0)
                        L($"VENTANA NUEVA: «{W.Titulo(h)}» clase={W.Clase(h)} visible={W.IsWindowVisible(h)} — diálogo nativo, NO invisible");
                // ¿o un menú/lista dentro del DOM?
                var menu = doc.FindFirst(TreeScope.Descendants, new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List)));
                if (menu != null)
                {
                    L($"MENÚ en el DOM: «{menu.Current.Name}» (invisible-friendly). Ítems:");
                    foreach (AutomationElement it in menu.FindAll(TreeScope.Descendants, new OrCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))))
                        L($"   · «{it.Current.Name}» id={it.Current.AutomationId}");
                }
                else L("no apareció ni menú DOM ni ventana nueva detectable en 1,5 s");
                // ¿hay un input de archivos oculto en el DOM?
                int inputs = 0;
                foreach (AutomationElement e in doc.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                {
                    string id = e.Current.AutomationId ?? "", n = e.Current.Name ?? "";
                    if (id.IndexOf("file", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Cargar", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Upload", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("dispositivo", StringComparison.OrdinalIgnoreCase) >= 0)
                        { L($"   posible entrada de archivo: {e.Current.ControlType.ProgrammaticName.Replace("ControlType.","")} «{n}» id={id}"); inputs++; }
                }
                L($"{inputs} candidato/s de entrada de archivo");
                // cerrar cualquier menú con Escape para no dejar nada abierto
                Enfocar(s);
                W.Atajo(false, false, W.VK_ESCAPE); Thread.Sleep(300);
                W.Atajo(false, false, W.VK_ESCAPE); Thread.Sleep(300);
                return 0;
            }
        }

        /// <summary>
        /// Adjunta un archivo CUALQUIERA (no solo imagen) por el camino real de Teams pero SIN mostrar nada:
        /// abre el menu "Adjuntar archivos", invoca "Cargar desde este dispositivo", y cuando aparece el dialogo
        /// nativo de Windows (#32770) lo MANDA fuera de pantalla (es Win32, no Chromium: si renderiza afuera),
        /// escribe la ruta en el campo y pulsa Abrir. Despues observa el redactor y opcionalmente envia.
        /// </summary>
        static int ArchivoDialogo(string ruta, bool enviar)
        {
            ruta = Path.GetFullPath(ruta);
            if (!File.Exists(ruta)) { L("no existe " + ruta); return 1; }
            using (var s = Preparar())
            {
                var antes = Firma(s.Doc);
                var doc = s.Doc;
                AutomationElement botonAdj = null;
                foreach (AutomationElement b in doc.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                { string n = b.Current.Name ?? ""; if (n.IndexOf("Adjuntar", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Attach", StringComparison.OrdinalIgnoreCase) >= 0) { botonAdj = b; break; } }
                if (botonAdj == null) { L("sin boton Adjuntar"); return 1; }
                var top0 = new System.Collections.Generic.HashSet<IntPtr>(TopLevels());
                // el boton "Adjuntar archivos" tiene atajo Alt+Shift+O: es MUCHISIMO mas confiable que Invoke/Expand
                // (el boton es LeafNode en UIA y no soporta Invoke). Abre el flyout con "Cargar desde este dispositivo".
                AutomationElement cargar = null;
                for (int intento = 0; intento < 3 && cargar == null; intento++)
                {
                    Enfocar(s);
                    W.AtajoAlt(true, (ushort)'O');
                    L("Alt+Shift+O enviado (intento " + (intento + 1) + ")");
                    var swm = Stopwatch.StartNew();
                    while (swm.ElapsedMilliseconds < 2500 && cargar == null)
                    {
                        Thread.Sleep(180);
                        foreach (AutomationElement it in Doc().FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)))
                        { string n = it.Current.Name ?? ""; if (n.IndexOf("Cargar desde este dispositivo", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Upload from this", StringComparison.OrdinalIgnoreCase) >= 0) { cargar = it; L("Cargar... encontrado a los " + swm.ElapsedMilliseconds + " ms, habil=" + it.Current.IsEnabled); break; } }
                    }
                }
                if (cargar == null)
                {
                    L("no aparecio Cargar desde este dispositivo tras 3 intentos; vuelco el doc");
                    var sbm = new StringBuilder(); Volcar(Doc(), sbm, 0, 40);
                    File.WriteAllText(Path.Combine(carpetaOut, DateTime.Now.ToString("HHmmss") + "-doc-sin-cargar.txt"), sbm.ToString(), new UTF8Encoding(false));
                    W.Atajo(false, false, W.VK_ESCAPE); return 1;
                }
                L("activo Cargar desde este dispositivo y cazo el dialogo nativo para mandarlo fuera de pantalla");
                IntPtr dialogo = IntPtr.Zero;
                bool cazando = true;
                var cazador = new Thread(() =>
                {
                    var sw = Stopwatch.StartNew();
                    while (cazando && sw.ElapsedMilliseconds < 10000)
                    {
                        foreach (var h in TopLevels())
                            if (!top0.Contains(h) && (W.Clase(h) == "#32770" || W.Titulo(h) == "Abrir" || W.Titulo(h) == "Open"))
                            { W.Mover(h, -32000, -32000); dialogo = h; L("dialogo <" + W.Titulo(h) + "> clase=" + W.Clase(h) + " cazado y movido fuera de pantalla"); cazando = false; return; }
                        Thread.Sleep(12);
                    }
                }) { IsBackground = true };
                cazador.Start();
                // El dialogo de archivo del navegador solo se abre con un GESTO DE TECLADO/PUNTERO CONFIABLE. El flyout
                // ya esta abierto por Alt+Shift+O y tiene el foco de teclado: navego con flechas hasta "Cargar desde este
                // dispositivo" y pulso Enter. Es el gesto mas fuerte y no depende de UIA Invoke (que no siempre cuenta).
                bool Enfocado(AutomationElement it) { try { return (bool)it.GetCurrentPropertyValue(AutomationElement.HasKeyboardFocusProperty); } catch { return false; } }
                // asegurar foco dentro del menu
                for (int k = 0; k < 8 && !Enfocado(cargar) && dialogo == IntPtr.Zero; k++) { W.Atajo(false, false, W.VK_DOWN); Thread.Sleep(120); }
                if (Enfocado(cargar)) L("foco de teclado en Cargar...; Enter"); else L("no confirme foco en Cargar...; Enter igual");
                W.Atajo(false, false, W.VK_RETURN);
                var swd = Stopwatch.StartNew();
                while (dialogo == IntPtr.Zero && swd.ElapsedMilliseconds < 3000) Thread.Sleep(30);
                if (dialogo == IntPtr.Zero) { try { cargar.SetFocus(); Thread.Sleep(100); W.Atajo(false, false, W.VK_RETURN); } catch { } while (dialogo == IntPtr.Zero && swd.ElapsedMilliseconds < 6000) Thread.Sleep(30); }
                cazando = false;
                if (dialogo == IntPtr.Zero) { L("el dialogo nativo nunca aparecio"); W.Atajo(false, false, W.VK_ESCAPE); return 1; }
                var dlg = AutomationElement.FromHandle(dialogo);
                // el arbol del #32770 tarda en construirse; el campo "Nombre de archivo" es un Edit (a veces dentro de un ComboBox)
                AutomationElement edit = null;
                var swe = Stopwatch.StartNew();
                while (swe.ElapsedMilliseconds < 4000 && edit == null)
                {
                    dlg = AutomationElement.FromHandle(dialogo);
                    foreach (AutomationElement e in dlg.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
                    { if (e.Current.IsEnabled) { edit = e; break; } }
                    if (edit == null)
                        foreach (AutomationElement cb in dlg.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox)))
                        { var ed = cb.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)); if (ed != null && ed.Current.IsEnabled) { edit = ed; break; } }
                    if (edit == null) Thread.Sleep(150);
                }
                if (edit == null)
                {
                    L("no encontre el campo de nombre en el dialogo; vuelco el dialogo");
                    var sbd = new StringBuilder(); Volcar(dlg, sbd, 0, 20);
                    File.WriteAllText(Path.Combine(carpetaOut, DateTime.Now.ToString("HHmmss") + "-dialogo.txt"), sbd.ToString(), new UTF8Encoding(false));
                    return 1;
                }
                L("campo de nombre encontrado a los " + swe.ElapsedMilliseconds + " ms (id=" + edit.Current.AutomationId + ")");
                bool puesta = false;
                try { ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue(ruta); puesta = ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).Current.Value == ruta; L("ruta con ValuePattern, verificada=" + puesta); }
                catch (Exception ex) { L("ValuePattern fallo: " + ex.GetType().Name); }
                if (!puesta) { edit.SetFocus(); Thread.Sleep(150); W.Atajo(true, false, (ushort)'A'); Thread.Sleep(60); W.Unicode(ruta); L("ruta tipeada con SendInput"); }
                Thread.Sleep(250);
                AutomationElement abrir = null;
                foreach (AutomationElement b in dlg.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                { string n = (b.Current.Name ?? "").Replace("&", ""); if (n.Equals("Abrir", StringComparison.OrdinalIgnoreCase) || n.Equals("Open", StringComparison.OrdinalIgnoreCase)) { abrir = b; break; } }
                if (abrir != null) { try { ((InvokePattern)abrir.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); L("boton Abrir invocado"); } catch { edit.SetFocus(); W.Atajo(false, false, W.VK_RETURN); } }
                else { edit.SetFocus(); W.Atajo(false, false, W.VK_RETURN); L("sin boton Abrir: Enter"); }
                var previo = antes; var sw2 = Stopwatch.StartNew(); long ultimo = 0; bool cambio = false;
                while (sw2.ElapsedMilliseconds < 60000)
                {
                    Thread.Sleep(300);
                    System.Collections.Generic.List<string> ahora;
                    try { ahora = Firma(Doc()); } catch { continue; }
                    var nuevos = ahora.Except(previo).ToList();
                    if (nuevos.Count > 0) { cambio = true; ultimo = sw2.ElapsedMilliseconds; foreach (var n in nuevos.Take(30)) L("   + " + n.Trim()); previo = ahora; }
                    bool cargando = ahora.Any(x => Regex.IsMatch(x, @"ProgressBar|Cargando|Subiendo|Uploading|\d+\s?%", RegexOptions.IgnoreCase));
                    if (cambio && !cargando && sw2.ElapsedMilliseconds - ultimo > 4000) { L("estable, " + sw2.ElapsedMilliseconds + "ms"); break; }
                    if (!cambio && sw2.ElapsedMilliseconds > 12000) { L("12 s sin cambios: el archivo no entro"); break; }
                }
                var sb = new StringBuilder(); Volcar(Doc(), sb, 0, 40);
                File.WriteAllText(Path.Combine(carpetaOut, DateTime.Now.ToString("HHmmss") + "-doc-con-archivo.txt"), sb.ToString(), new UTF8Encoding(false));
                if (enviar && cambio)
                {
                    Enfocar(s);
                    bool ok = Enviar(s, () => { string v = Valor(Editor(Doc()) ?? s.Editor); return v.Length == 0 || v == s.Placeholder; });
                    L(ok ? "ENVIADO" : "NO SE ENVIO");
                    return ok ? 0 : 1;
                }
                if (!enviar && cambio) { for (int i = 0; i < 6; i++) { Enfocar(s); W.Atajo(true, false, (ushort)'A'); Thread.Sleep(90); W.Atajo(false, false, W.VK_DELETE); Thread.Sleep(450); if (Firma(Doc()).Count <= antes.Count + 2) break; } }
                return cambio ? 0 : 1;
            }
        }

        /// <summary>
        /// Simula un ENVIO real de varias partes a mi chat: burbuja de texto, burbuja con imagen inline, burbuja de
        /// texto. Cada parte se manda por separado (su propia burbuja). Verifica que lleguen 3 burbujas nuevas.
        /// </summary>
        static int Combo(string rutaImagen)
        {
            rutaImagen = Path.GetFullPath(string.IsNullOrEmpty(rutaImagen) ? "out/sonda.png" : rutaImagen);
            if (!File.Exists(rutaImagen)) { L("no existe la imagen " + rutaImagen); return 1; }
            string sello = DateTime.Now.ToString("HHmmss");
            var partes = new (string tipo, string texto)[] {
                ("texto", "combo " + sello + " · parte 1 de 3 (texto)"),
                ("imagen", "combo " + sello + " · parte 2 de 3 (imagen)"),
                ("texto", "combo " + sello + " · parte 3 de 3 (texto)"),
            };
            int enviadas = 0;
            using (var s = Preparar())
            {
                for (int i = 0; i < partes.Length; i++)
                {
                    if (!EsMiChat()) { L("el chat cambio: corto"); break; }
                    var (tipo, texto) = partes[i];
                    Enfocar(s);
                    if (tipo == "texto")
                    {
                        Tipear(texto);
                        var sw = Stopwatch.StartNew();
                        while (sw.ElapsedMilliseconds < 3000 && Valor(s.Editor) != texto) Thread.Sleep(120);
                        if (!Enviar(s, () => { string v = Valor(Editor(Doc()) ?? s.Editor); return v.Length == 0 || v == s.Placeholder; })) { L("no se envio la parte " + (i + 1)); break; }
                    }
                    else
                    {
                        var redactor = Redactor(s.Editor, 1);
                        var antes = Firma(redactor);
                        Tipear(texto);
                        PonerImagen(rutaImagen);
                        Enfocar(s);
                        W.Atajo(true, false, (ushort)'V');
                        var sw = Stopwatch.StartNew(); long ultimo = 0; bool cambio = false; var previo = antes;
                        while (sw.ElapsedMilliseconds < 30000)
                        {
                            Thread.Sleep(300);
                            System.Collections.Generic.List<string> ahora;
                            try { ahora = Firma(redactor); } catch { redactor = Redactor(Editor(Doc()) ?? s.Editor, 1); continue; }
                            if (ahora.Except(previo).Any() || previo.Except(ahora).Any()) { cambio = true; ultimo = sw.ElapsedMilliseconds; previo = ahora; }
                            bool cargando = ahora.Any(x => Regex.IsMatch(x, @"ProgressBar|Cargando|Subiendo|Uploading|\d+\s?%", RegexOptions.IgnoreCase));
                            if (cambio && !cargando && sw.ElapsedMilliseconds - ultimo > 3500) break;
                            if (!cambio && sw.ElapsedMilliseconds > 8000) break;
                        }
                        if (!cambio) { L("la imagen no entro en la parte " + (i + 1)); break; }
                        if (!Enviar(s, () => { string v = Valor(Editor(Doc()) ?? s.Editor); return v.Length == 0 || v == s.Placeholder; })) { L("no se envio la imagen"); break; }
                    }
                    enviadas++;
                    L("parte " + (i + 1) + " enviada");
                    if (i < partes.Length - 1) Thread.Sleep(700);
                }
            }
            L("ENVIADAS " + enviadas + " de " + partes.Length + " partes");
            return enviadas == partes.Length ? 0 : 1;
        }

        static System.Collections.Generic.List<IntPtr> TopLevels()
        {
            var res = new System.Collections.Generic.List<IntPtr>();
            W.EnumWindows((h, l) => { res.Add(h); return true; }, IntPtr.Zero);
            return res;
        }

        static int Limpiar()
        {
            using (var s = Preparar())
            {
                for (int i = 0; i < 5; i++)
                {
                    W.Atajo(true, false, (ushort)'A'); Thread.Sleep(90);
                    W.Atajo(false, false, W.VK_DELETE); Thread.Sleep(400);
                }
                L($"editor «{Valor(s.Editor)}»");
                return 0;
            }
        }
    }
}
