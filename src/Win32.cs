using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TeamsTools
{
    /// <summary>Ventana top-level de otro proceso, tal como la ve EnumWindows.</summary>
    internal sealed class VentanaInfo
    {
        public IntPtr Hwnd;
        public uint Pid;
        public string Titulo = "";
        public string Clase = "";
        public bool Visible;
        public bool Minimizada;
        public bool Cloaked;
        public int Ancho, Alto;
        public override string ToString() => $"{Hwnd} '{Titulo}' [{Clase}] {Ancho}x{Alto}{(Minimizada ? " min" : "")}{(Cloaked ? " cloaked" : "")}";
    }

    /// <summary>P/Invoke crudo. Nada de librerias: user32, dwmapi, uxtheme, kernel32.</summary>
    internal static class Win32
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        public const int EM_SETCUEBANNER = 0x1501;
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string s);
        [DllImport("user32.dll")] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr hWnd, string app, string idList);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public InputUnion U; }
        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }
        [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int WM_NCLBUTTONDBLCLK = 0xA3;
        public const int WM_NCHITTEST = 0x84;
        public const int WM_SYSCOMMAND = 0x112;
        public const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        public const int SW_HIDE = 0, SW_SHOWNORMAL = 1, SW_SHOWMINIMIZED = 2, SW_SHOWNOACTIVATE = 4, SW_SHOW = 5, SW_MINIMIZE = 6, SW_SHOWMINNOACTIVE = 7, SW_RESTORE = 9;
        public const int DWMWA_CLOAKED = 14;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_BORDER_COLOR = 34;
        public const int DWMWCP_DEFAULT = 0, DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2, DWMWCP_ROUNDSMALL = 3;
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
        public const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2;
        public const ushort VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12;
        public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        public static string Titulo(IntPtr h) { var sb = new StringBuilder(1024); GetWindowText(h, sb, sb.Capacity); return sb.ToString(); }
        public static string Clase(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, sb.Capacity); return sb.ToString(); }
        public static bool Cloaked(IntPtr h) { int v = 0; return DwmGetWindowAttribute(h, DWMWA_CLOAKED, out v, 4) == 0 && v != 0; }

        /// <summary>Todas las ventanas top-level de los procesos indicados (incluye minimizadas, excluye las invisibles).</summary>
        public static List<VentanaInfo> VentanasDe(HashSet<uint> pids, bool soloVisibles = true)
        {
            var res = new List<VentanaInfo>();
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (!pids.Contains(pid)) return true;
                bool vis = IsWindowVisible(h);
                if (soloVisibles && !vis) return true;
                GetWindowRect(h, out RECT r);
                res.Add(new VentanaInfo
                {
                    Hwnd = h, Pid = pid, Titulo = Titulo(h), Clase = Clase(h), Visible = vis,
                    Minimizada = IsIconic(h), Cloaked = Cloaked(h), Ancho = r.Right - r.Left, Alto = r.Bottom - r.Top
                });
                return true;
            }, IntPtr.Zero);
            return res;
        }

        public static HashSet<uint> PidsDe(string nombreProceso)
        {
            var set = new HashSet<uint>();
            foreach (var p in Process.GetProcessesByName(nombreProceso)) { set.Add((uint)p.Id); p.Dispose(); }
            return set;
        }

        /// <summary>Trae una ventana al frente de verdad: restaura si esta minimizada y se cuelga del hilo activo para tener derecho al foco.</summary>
        public static bool TraerAlFrente(IntPtr h)
        {
            if (!IsWindow(h)) return false;
            if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
            IntPtr fg = GetForegroundWindow();
            uint hiloFg = GetWindowThreadProcessId(fg, out _);
            uint hiloYo = GetCurrentThreadId();
            bool attached = false;
            try
            {
                if (fg != IntPtr.Zero && hiloFg != hiloYo) attached = AttachThreadInput(hiloYo, hiloFg, true);
                BringWindowToTop(h);
                SetForegroundWindow(h);
            }
            finally { if (attached) AttachThreadInput(hiloYo, hiloFg, false); }
            for (int i = 0; i < 20; i++) { if (GetForegroundWindow() == h) return true; Thread.Sleep(50); }
            // plan B: un toque de ALT desbloquea SetForegroundWindow en algunas sesiones
            Tecla(VK_MENU, false); Tecla(VK_MENU, true);
            SetForegroundWindow(h);
            Thread.Sleep(100);
            return GetForegroundWindow() == h;
        }

        static void Tecla(ushort vk, bool up)
        {
            var inp = new INPUT[1];
            inp[0].type = INPUT_KEYBOARD;
            inp[0].U.ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 };
            SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
        }

        public static void CtrlV()
        {
            Tecla(VK_CONTROL, false); Tecla((ushort)'V', false);
            Thread.Sleep(40);
            Tecla((ushort)'V', true); Tecla(VK_CONTROL, true);
        }

        public const uint KEYEVENTF_UNICODE = 0x4;
        public const ushort VK_RETURN = 0x0D, VK_DELETE = 0x2E, VK_ESCAPE = 0x1B;

        /// <summary>Tipea texto Unicode real (acentos, ñ, emojis) en la ventana con foco.</summary>
        public static void Unicode(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            var inp = new INPUT[s.Length * 2];
            for (int i = 0; i < s.Length; i++)
            {
                inp[i * 2].type = INPUT_KEYBOARD; inp[i * 2].U.ki = new KEYBDINPUT { wScan = s[i], dwFlags = KEYEVENTF_UNICODE };
                inp[i * 2 + 1].type = INPUT_KEYBOARD; inp[i * 2 + 1].U.ki = new KEYBDINPUT { wScan = s[i], dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP };
            }
            // en tandas: SendInput acepta hasta ~unos cientos de eventos por llamada sin perder ninguno
            for (int i = 0; i < inp.Length; i += 200)
            {
                int n = Math.Min(200, inp.Length - i);
                var tanda = new INPUT[n]; Array.Copy(inp, i, tanda, 0, n);
                SendInput((uint)n, tanda, Marshal.SizeOf(typeof(INPUT)));
            }
        }

        /// <summary>Atajo con modificadores reales: Ctrl/Shift/Alt + tecla virtual.</summary>
        public static void Atajo(bool ctrl, bool shift, bool alt, ushort vk)
        {
            if (ctrl) Tecla(VK_CONTROL, false);
            if (shift) Tecla(VK_SHIFT, false);
            if (alt) Tecla(VK_MENU, false);
            Tecla(vk, false); Tecla(vk, true);
            if (alt) Tecla(VK_MENU, true);
            if (shift) Tecla(VK_SHIFT, true);
            if (ctrl) Tecla(VK_CONTROL, true);
        }

        public static void TeclaSuelta(ushort vk) { Tecla(vk, false); Tecla(vk, true); }

        // --- atajo global: prender/apagar el modo automático desde cualquier aplicación ---
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
        public const int WM_HOTKEY = 0x0312;

        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT p);
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct WINDOWPLACEMENT { public int length, flags, showCmd; public POINT min, max; public RECT normal; }
        public const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

        /// <summary>Posicion "normal" (no minimizada) de una ventana, aunque este oculta.</summary>
        public static RECT Normal(IntPtr h)
        {
            var p = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
            GetWindowPlacement(h, ref p);
            return p.normal;
        }
        public static void Mover(IntPtr h, int x, int y) => SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        // --- permanencia online: evitar que la pantalla se apague (una pantalla dormida fuerza Ausente) ---
        [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
        public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001, ES_DISPLAY_REQUIRED = 0x00000002;

        /// <summary>Le pide a Windows que no apague la pantalla ni suspenda mientras estemos activos.</summary>
        public static bool MantenerDespierto(bool si)
        {
            try { return SetThreadExecutionState(si ? (ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED) : ES_CONTINUOUS) != 0; }
            catch { return false; }
        }

        // --- deteccion de sesion bloqueada: con la sesion bloqueada NINGUN input inyectado mantiene Disponible ---
        [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr h);
        const uint DESKTOP_SWITCHDESKTOP = 0x0100;

        public static bool SesionBloqueada()
        {
            try
            {
                var d = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
                if (d == IntPtr.Zero) return true;         // el escritorio de entrada es el de bloqueo
                CloseDesktop(d);
                return false;
            }
            catch { return false; }
        }

        /// <summary>Ctrl+Shift+letra (el atajo de Teams para colgar es Ctrl+Shift+H).</summary>
        public static void CtrlShift(char letra)
        {
            ushort vk = (ushort)char.ToUpperInvariant(letra);
            Tecla(VK_CONTROL, false); Tecla(VK_SHIFT, false); Tecla(vk, false);
            Thread.Sleep(40);
            Tecla(vk, true); Tecla(VK_SHIFT, true); Tecla(VK_CONTROL, true);
        }

        public static void EsquinasRedondas(IntPtr h, bool chicas = false)
        {
            try { int v = chicas ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND; DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, 4); } catch { }
        }
        public static void BordeColor(IntPtr h, int bgr)
        {
            try { DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref bgr, 4); } catch { }
        }
        public static void ModoOscuro(IntPtr h)
        {
            try { int v = 1; DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, 4); } catch { }
        }
        public static void BarrasOscuras(IntPtr h)
        {
            try { SetWindowTheme(h, "DarkMode_Explorer", null); } catch { }
        }
        public static void ArrastrarVentana(IntPtr h)
        {
            ReleaseCapture();
            SendMessage(h, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }
    }
}
