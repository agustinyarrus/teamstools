// EspiaVentanas.cs — SOLO LECTURA. Registra qué le pasa a cada ventana top-level del escritorio (se muestra,
// se oculta, se minimiza, se restaura, toma el foreground, se destruye) con proceso, clase y título.
//
// Por qué existe: teams-autoleave manipula ventanas de Teams desde varios hilos y el user vio desaparecer
// Chrome y Teams «de la nada». Sin una grabación de eventos eso es palabra contra palabra; con esto, cada
// SW_HIDE / SW_MINIMIZE / robo de foco queda con hora al milisegundo y se cruza con el log de la app.
//
// No toca nada: WinEvent FUERA de contexto (WINEVENT_OUTOFCONTEXT) — no inyecta DLL en ningún proceso, solo
// recibe notificaciones en su propia cola de mensajes.
//
//   espia-ventanas.exe [--segundos N] [--salida ruta.log] [--todo]
//     --segundos  cuánto graba (default 180; 0 = hasta Ctrl+C)
//     --salida    archivo donde deja la grabación (default .\espia-AAAAMMDD-HHmmss.log)
//     --todo      no filtra ventanas sin título (IME, tooltips, popups): útil solo para depurar el espía
//
// Compilar: csc /nologo /optimize /out:espia-ventanas.exe EspiaVentanas.cs   (csc de .NET Framework 4.x)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class EspiaVentanas
{
    // ── eventos que importan (winuser.h) ───────────────────────────────────────────────────────────────
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    const uint EVENT_OBJECT_DESTROY = 0x8001;
    const uint EVENT_OBJECT_SHOW = 0x8002;
    const uint EVENT_OBJECT_HIDE = 0x8003;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    const int OBJID_WINDOW = 0, CHILDID_SELF = 0;
    const uint GA_ROOT = 2;
    const uint WM_QUIT = 0x0012;
    const int SEGUNDOS_POR_DEFECTO = 180;

    // clases que son ruido puro: nunca son "la ventana" de una app
    static readonly HashSet<string> ClasesRuido = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "IME", "MSCTFIME UI", "tooltips_class32", "#32768", "Xaml_WindowedPopupClass", "SysShadow",
        "Chrome_WidgetWin_2", "ForegroundStaging", "MultitaskingViewFrame", "TaskListThumbnailWnd",
        "Windows.UI.Core.CoreWindow", "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow",
    };

    delegate void WinEventDelegate(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventDelegate cb, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr h);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO li);
    [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(CtrlHandler h, bool add);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    delegate bool CtrlHandler(int tipo);

    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    /// <summary>Lo que se sabe de una ventana: se congela al verla por primera vez, porque al destruirse ya no hay a quién preguntarle.</summary>
    sealed class Ficha { public uint Pid; public string Proceso = "?", Clase = "", Titulo = ""; }

    static readonly Dictionary<IntPtr, Ficha> fichas = new Dictionary<IntPtr, Ficha>();
    static readonly Dictionary<uint, string> nombresProceso = new Dictionary<uint, string>();
    static readonly Dictionary<string, int> conteo = new Dictionary<string, int>();   // "proceso|evento" → veces
    static readonly List<string> lineas = new List<string>();
    static readonly Stopwatch reloj = Stopwatch.StartNew();
    static WinEventDelegate callback;   // campo estático: si el GC se lleva el delegado, el hook llama a basura
    static CtrlHandler ctrl;
    static uint hiloBucle;
    static bool todo;

    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        int segundos = SEGUNDOS_POR_DEFECTO;
        string salida = Path.Combine(Environment.CurrentDirectory, "espia-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].TrimStart('-', '/').ToLowerInvariant();
            if (a == "segundos" && i + 1 < args.Length) int.TryParse(args[++i], out segundos);
            else if (a == "salida" && i + 1 < args.Length) salida = Path.GetFullPath(args[++i]);
            else if (a == "todo") todo = true;
        }

        Encabezado(segundos, salida);
        Fotografiar();   // ficha de todo lo que ya existe: si algo se destruye, igual sabemos qué era

        callback = AlEvento;
        var hooks = new[]
        {
            SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS),
            SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS),
            SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE, IntPtr.Zero, callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS),
        };
        if (hooks.Any(h => h == IntPtr.Zero)) { Console.Error.WriteLine("no pude instalar los hooks de WinEvent"); return 2; }

        hiloBucle = GetCurrentThreadId();
        ctrl = _ => { PostThreadMessage(hiloBucle, WM_QUIT, IntPtr.Zero, IntPtr.Zero); return true; };
        SetConsoleCtrlHandler(ctrl, true);
        if (segundos > 0)
            new Thread(() => { Thread.Sleep(segundos * 1000); PostThreadMessage(hiloBucle, WM_QUIT, IntPtr.Zero, IntPtr.Zero); }) { IsBackground = true }.Start();

        // la cola de mensajes es obligatoria: los WinEvent fuera de contexto llegan por acá
        MSG m;
        while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessage(ref m); }

        foreach (var h in hooks) UnhookWinEvent(h);
        Resumen(salida);
        return 0;
    }

    // ── el corazón: un evento → una línea ──────────────────────────────────────────────────────────────

    static void AlEvento(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
        if (ev != EVENT_OBJECT_DESTROY && GetAncestor(hwnd, GA_ROOT) != hwnd) return;   // solo top-level
        var f = FichaDe(hwnd, ev != EVENT_OBJECT_DESTROY);
        if (f == null) return;
        if (!todo && (f.Titulo.Length == 0 || ClasesRuido.Contains(f.Clase))) return;

        string nombre = NombreEvento(ev);
        string estado = ev == EVENT_OBJECT_DESTROY ? "" : (IsWindowVisible(hwnd) ? "vis" : "OCULTA") + (IsIconic(hwnd) ? "·min" : "");
        string linea = string.Format("{0:HH:mm:ss.fff}  {1,-10} {2,-16} {3,-10} {4,-12} {5,-26} «{6}»  idle={7}s",
            DateTime.Now, nombre, Recortar(f.Proceso + "(" + f.Pid + ")", 16), "0x" + hwnd.ToInt64().ToString("X"),
            estado, Recortar(f.Clase, 26), Recortar(f.Titulo, 90), IdleSegundos());
        lineas.Add(linea);
        string k = f.Proceso + "|" + nombre;
        int n; conteo.TryGetValue(k, out n); conteo[k] = n + 1;
        Pintar(ev, linea);
    }

    static Ficha FichaDe(IntPtr h, bool refrescar)
    {
        Ficha f;
        if (fichas.TryGetValue(h, out f) && !refrescar) return f;
        if (f == null) f = new Ficha();
        uint pid; GetWindowThreadProcessId(h, out pid);
        if (pid == 0 && f.Pid == 0) return fichas.ContainsKey(h) ? f : null;
        if (pid != 0) { f.Pid = pid; f.Proceso = NombreProceso(pid); }
        var sb = new StringBuilder(512);
        if (GetClassName(h, sb, sb.Capacity) > 0) f.Clase = sb.ToString();
        sb.Clear();
        if (GetWindowText(h, sb, sb.Capacity) > 0) f.Titulo = sb.ToString();
        fichas[h] = f;
        return f;
    }

    static string NombreProceso(uint pid)
    {
        string n;
        if (nombresProceso.TryGetValue(pid, out n)) return n;   // memo: Process.GetProcessById es caro
        try { using (var p = Process.GetProcessById((int)pid)) n = p.ProcessName; } catch (ArgumentException) { n = "?"; } catch (InvalidOperationException) { n = "?"; }
        nombresProceso[pid] = n;
        return n;
    }

    static void Fotografiar()
    {
        int n = 0;
        EnumWindows((h, l) => { if (FichaDe(h, true) != null) n++; return true; }, IntPtr.Zero);
        Console.WriteLine("  \x1b[38;2;108;112;134mfoto inicial: {0} ventanas top-level fichadas\x1b[0m\n", n);
    }

    static int IdleSegundos()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
        return GetLastInputInfo(ref li) ? (int)(unchecked((uint)Environment.TickCount - li.dwTime) / 1000) : -1;
    }

    static string NombreEvento(uint ev)
    {
        switch (ev)
        {
            case EVENT_SYSTEM_FOREGROUND: return "FOCO";
            case EVENT_SYSTEM_MINIMIZESTART: return "MINIMIZA";
            case EVENT_SYSTEM_MINIMIZEEND: return "RESTAURA";
            case EVENT_OBJECT_DESTROY: return "DESTRUYE";
            case EVENT_OBJECT_SHOW: return "MUESTRA";
            case EVENT_OBJECT_HIDE: return "OCULTA";
        }
        return "0x" + ev.ToString("X");
    }

    // ── salida con forma: pastel sobre negro ───────────────────────────────────────────────────────────

    static void Pintar(uint ev, string linea)
    {
        string c;
        switch (ev)
        {
            case EVENT_OBJECT_HIDE: c = "243;139;168"; break;          // rosa: lo que desaparece
            case EVENT_SYSTEM_MINIMIZESTART: c = "250;179;135"; break; // durazno
            case EVENT_OBJECT_DESTROY: c = "235;160;172"; break;
            case EVENT_OBJECT_SHOW: c = "166;227;161"; break;          // salvia
            case EVENT_SYSTEM_MINIMIZEEND: c = "148;226;213"; break;   // teal
            default: c = "137;180;250"; break;                         // cielo: foco
        }
        Console.WriteLine("  \x1b[38;2;" + c + "m" + linea + "\x1b[0m");
    }

    static void Encabezado(int segundos, string salida)
    {
        Console.WriteLine();
        Console.WriteLine("  \x1b[38;2;203;166;247m◆ espía de ventanas\x1b[0m  \x1b[38;2;108;112;134m· solo lectura · WinEvent fuera de contexto · {0}\x1b[0m",
            segundos > 0 ? segundos + " s" : "hasta Ctrl+C");
        Console.WriteLine("  \x1b[38;2;108;112;134m→ {0}\x1b[0m", salida);
        Console.WriteLine();
    }

    static void Resumen(string salida)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# espía de ventanas · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " · " + (int)reloj.Elapsed.TotalSeconds + " s grabados");
        foreach (var l in lineas) sb.AppendLine(l);
        sb.AppendLine();
        sb.AppendLine("# resumen proceso × evento");
        foreach (var kv in conteo.OrderByDescending(k => k.Value)) sb.AppendLine(string.Format("#   {0,-28} {1,5}", kv.Key.Replace("|", " · "), kv.Value));
        File.WriteAllText(salida, sb.ToString(), new UTF8Encoding(false));

        Console.WriteLine();
        Console.WriteLine("  \x1b[38;2;203;166;247m╭─ resumen ─────────────────────────────────────────────╮\x1b[0m");
        Console.WriteLine("  \x1b[38;2;203;166;247m│\x1b[0m  {0,-38} {1,12}  \x1b[38;2;203;166;247m│\x1b[0m", "eventos registrados", lineas.Count);
        foreach (var kv in conteo.OrderByDescending(k => k.Value).Take(12))
            Console.WriteLine("  \x1b[38;2;203;166;247m│\x1b[0m  {0,-38} {1,12}  \x1b[38;2;203;166;247m│\x1b[0m", Recortar(kv.Key.Replace("|", " · "), 38), kv.Value);
        Console.WriteLine("  \x1b[38;2;203;166;247m╰───────────────────────────────────────────────────────╯\x1b[0m");
        Console.WriteLine("  \x1b[38;2;108;112;134m{0}\x1b[0m", salida);
    }

    // cuerpo de bloque a propósito: el csc de .NET Framework es C# 5 y no conoce los miembros con «=>»
    static string Recortar(string s, int n) { return string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }
}
