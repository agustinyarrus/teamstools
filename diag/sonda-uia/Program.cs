using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

// Sonda 4: chats de Teams por UIA (ventana "Chat | ... | Microsoft Teams").
//   probe.exe chats                    lista el arbol de chats (nombre, estado, no leidos)
//   probe.exe mensajes                 estructura de los mensajes visibles del chat abierto
//   probe.exe enviar <texto>           SetValue en el editor + Invoke "Enviar" (silencioso)
//   probe.exe pegar <html> <texto>     foco + portapapeles CF_HTML + Ctrl+V + Invoke "Enviar"
//   probe.exe abrir <nombre>           selecciona el chat cuyo nombre contiene <nombre>
//   probe.exe roster                   (viejo) prueba del panel Gente en una reunion
// Todo va a probe-out.txt al lado del exe.
static class Program
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool f);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] p, int cb);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    const uint WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_CHAR = 0x102, WM_PASTE = 0x302;

    static readonly StringBuilder sb = new StringBuilder();
    static void L(string s) => sb.AppendLine(s);
    static readonly List<IntPtr> destapadas = new List<IntPtr>();   // las que destapamos: hay que volver a ocultarlas al final
    static readonly CacheRequest cr = new CacheRequest();

    [STAThread]
    static void Main(string[] args)
    {
        string modo = args.Length > 0 ? args[0] : "chats";
        try { Correr(modo, args); }
        catch (Exception ex) { L("EXCEPCION: " + ex); }
        finally { foreach (var h in destapadas) { try { ShowWindow(h, 0); } catch { } } if (destapadas.Count > 0) L($"(volvi a ocultar {destapadas.Count} ventana/s que estaban en la bandeja)"); }
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe-out.txt"), sb.ToString(), new UTF8Encoding(false));
    }

    static void Correr(string modo, string[] args)
    {
        cr.Add(AutomationElement.NameProperty); cr.Add(AutomationElement.AutomationIdProperty); cr.Add(AutomationElement.ControlTypeProperty);
        cr.Add(AutomationElement.BoundingRectangleProperty); cr.Add(AutomationElement.IsOffscreenProperty); cr.Add(AutomationElement.HelpTextProperty);
        cr.TreeScope = TreeScope.Element;

        var pids = Process.GetProcessesByName("ms-teams").Select(p => (uint)p.Id).ToHashSet();
        var wins = new List<(IntPtr h, string title)>();
        EnumWindows((h, l) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (!pids.Contains(pid)) return true;
            var t = new StringBuilder(512); GetWindowText(h, t, 512);
            if (t.Length == 0) return true;
            // 🚨 SOLO las ventanas reales de Teams. Sin este filtro se destapaban las auxiliares
            // (5 "Default IME", GDI+ Hook, Rtc PnP, DDE Server) y aparecían como basura en el escritorio.
            var cl = new StringBuilder(128); GetClassName(h, cl, 128);
            if (cl.ToString() != "TeamsWebView") return true;
            if (!IsWindowVisible(h)) { ShowWindow(h, 7); Thread.Sleep(250); destapadas.Add(h); }   // minimizada: no se ve
            wins.Add((h, t.ToString()));
            return true;
        }, IntPtr.Zero);

        // la ventana de chat: titulo "Chat | ... | Microsoft Teams" o la que tenga el editor
        foreach (var w in wins)
        {
            var sw = Stopwatch.StartNew();
            AutomationElement root;
            try { root = AutomationElement.FromHandle(w.h); } catch (Exception ex) { L($"== {w.title}: {ex.Message}"); continue; }
            using (cr.Activate())
            {
                var doc = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "RootWebArea")) ?? root;
                var editor = BuscarEditor(doc);
                var arbol = doc.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tree), new PropertyCondition(AutomationElement.NameProperty, "Teams")));
                L($"== {w.title} hwnd={w.h} editor={(editor != null)} arbol={(arbol != null)} ({sw.ElapsedMilliseconds}ms)");
                if (editor == null && arbol == null) continue;

                if (modo == "chats" && arbol != null) Chats(arbol);
                else if (modo == "mensajes") Mensajes(doc);
                else if (modo == "enviar" && editor != null) Enviar(doc, editor, string.Join(" ", args.Skip(1)));
                else if (modo == "pegar" && editor != null) Pegar(w.h, doc, editor, args.Length > 1 ? args[1] : "<b>prueba</b>", args.Length > 2 ? args[2] : "prueba");
                else if (modo == "abrir" && arbol != null) Abrir(w.h, doc, arbol, string.Join(" ", args.Skip(1)));
                else if (modo == "tipear" && editor != null) Tipear(w.h, doc, editor, string.Join(" ", args.Skip(1)));
                else if (modo == "formato" && editor != null) Formato(w.h, doc, editor);
                else if (modo == "rico" && editor != null) Rico(w.h, doc, editor);
                else if (modo == "directo" && editor != null) Directo(w.h, doc, editor, args.Length > 1 ? string.Join(" ", args.Skip(1)) : "prueba silenciosa");
                else if (modo == "limpiar" && editor != null) Limpiar(w.h, doc, editor);
                else if (modo == "presencia") { Presencia(w.h, doc); break; }
                else if (modo == "si" && editor != null) SendInputRico(w.h, doc, editor);
                L($"   ({sw.ElapsedMilliseconds}ms)");
                break;
            }
        }
    }

    static AutomationElement BuscarEditor(AutomationElement doc)
    {
        foreach (AutomationElement e in doc.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
        {
            string id = e.Cached.AutomationId ?? "";
            if (id.StartsWith("new-message-", StringComparison.OrdinalIgnoreCase)) return e;
        }
        return null;
    }

    static AutomationElement BotonEnviar(AutomationElement doc)
    {
        foreach (AutomationElement e in doc.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
        {
            string n = e.Cached.Name ?? "";
            if (n.StartsWith("Enviar", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Send", StringComparison.OrdinalIgnoreCase)) return e;
        }
        return null;
    }

    static void Chats(AutomationElement arbol)
    {
        foreach (AutomationElement it in arbol.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem)))
        {
            string n = it.Cached.Name ?? "";
            bool sel = false;
            try { sel = ((SelectionItemPattern)it.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected; } catch { }
            var hijos = new List<string>();
            foreach (AutomationElement h in it.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                string hn = h.Cached.Name ?? ""; string hid = h.Cached.AutomationId ?? "";
                if (hn.Length > 0 && hn != n) hijos.Add($"{h.Cached.ControlType.ProgrammaticName.Replace("ControlType.", "")}:{hn}" + (hid.Length > 0 ? $"#{hid}" : ""));
            }
            L($"   TreeItem sel={sel} id={it.Cached.AutomationId} :: {n}" + (hijos.Count > 0 ? "  <" + string.Join(" | ", hijos.Take(8)) + ">" : ""));
        }
    }

    static void Mensajes(AutomationElement doc)
    {
        // cada mensaje tiene un nodo message-body-N; subimos al contenedor y volcamos su subarbol
        var cuerpos = new List<AutomationElement>();
        foreach (AutomationElement e in doc.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            string id = e.Cached.AutomationId ?? "";
            if (id.StartsWith("message-body-")) cuerpos.Add(e);
        }
        L($"   mensajes visibles: {cuerpos.Count}");
        foreach (var c in cuerpos.Skip(Math.Max(0, cuerpos.Count - 4)))
        {
            var padre = TreeWalker.ControlViewWalker.GetParent(c, cr);
            var abuelo = padre != null ? TreeWalker.ControlViewWalker.GetParent(padre, cr) : null;
            var cont = abuelo ?? padre ?? c;
            L($"   --- contenedor {cont.Cached.ControlType.ProgrammaticName.Replace("ControlType.", "")} name=\"{cont.Cached.Name}\" id={cont.Cached.AutomationId}");
            Volcar(cont, 2, 0);
        }
    }

    static void Volcar(AutomationElement el, int depth, int nivel)
    {
        if (nivel > 6) return;
        var ch = TreeWalker.ControlViewWalker.GetFirstChild(el, cr);
        while (ch != null)
        {
            string n = ch.Cached.Name ?? ""; string id = ch.Cached.AutomationId ?? "";
            if (n.Length > 0 || id.Length > 0)
                L(new string(' ', depth + nivel * 2) + $"{ch.Cached.ControlType.ProgrammaticName.Replace("ControlType.", "")} name=\"{(n.Length > 120 ? n.Substring(0, 120) + "…" : n)}\" id=\"{id}\"");
            Volcar(ch, depth, nivel + 1);
            ch = TreeWalker.ControlViewWalker.GetNextSibling(ch, cr);
        }
    }

    static string UltimoMensaje(AutomationElement doc)
    {
        AutomationElement ult = null;
        foreach (AutomationElement e in doc.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            string id = e.Cached.AutomationId ?? "";
            if (id.StartsWith("message-body-")) ult = e;
        }
        if (ult == null) return "(sin mensajes)";
        var textos = new List<string>();
        foreach (AutomationElement t in ult.FindAll(TreeScope.Descendants, Condition.TrueCondition)) { var n = t.Cached.Name ?? ""; if (n.Length > 0) textos.Add(n); }
        return $"{ult.Cached.Name} | " + string.Join(" / ", textos.Take(6));
    }

    static void Enviar(AutomationElement doc, AutomationElement editor, string texto)
    {
        L($"   editor id={editor.Cached.AutomationId} name=\"{editor.Cached.Name}\"");
        object vp; bool tiene = editor.TryGetCurrentPattern(ValuePattern.Pattern, out vp);
        L($"   ValuePattern: {tiene}" + (tiene ? $" valor actual=\"{((ValuePattern)vp).Current.Value}\" readonly={((ValuePattern)vp).Current.IsReadOnly}" : ""));
        if (!tiene) return;
        try { ((ValuePattern)vp).SetValue(texto); L("   SetValue ok"); }
        catch (Exception ex) { L("   SetValue fallo: " + ex.Message); return; }
        Thread.Sleep(500);
        L($"   valor tras SetValue=\"{((ValuePattern)vp).Current.Value}\"");
        var btn = BotonEnviar(doc);
        L($"   boton enviar: {(btn != null ? btn.Cached.Name : "-")}");
        if (btn == null) return;
        try { ((InvokePattern)btn.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); L("   Invoke Enviar ok"); }
        catch (Exception ex) { L("   Invoke fallo: " + ex.Message); }
        Thread.Sleep(2500);
        L("   ultimo mensaje: " + UltimoMensaje(doc));
        L($"   editor ahora=\"{((ValuePattern)vp).Current.Value}\"");
    }

    static void Pegar(IntPtr hwnd, AutomationElement doc, AutomationElement editor, string html, string texto)
    {
        IntPtr fg0 = GetForegroundWindow();
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
        uint hiloFg = GetWindowThreadProcessId(fg0, out _), yo = GetCurrentThreadId();
        bool att = fg0 != IntPtr.Zero && hiloFg != yo && AttachThreadInput(yo, hiloFg, true);
        SetForegroundWindow(hwnd);
        if (att) AttachThreadInput(yo, hiloFg, false);
        Thread.Sleep(300);
        L($"   foreground es la ventana de Teams: {GetForegroundWindow() == hwnd}");
        try { editor.SetFocus(); L("   SetFocus ok"); } catch (Exception ex) { L("   SetFocus fallo: " + ex.Message); }
        Thread.Sleep(300);
        var data = new DataObject();
        data.SetData(DataFormats.Html, CfHtml(html));
        data.SetData(DataFormats.UnicodeText, texto);
        Clipboard.SetDataObject(data, true);
        L("   portapapeles cargado (CF_HTML + texto)");
        CtrlV();
        Thread.Sleep(800);
        object vp; if (editor.TryGetCurrentPattern(ValuePattern.Pattern, out vp)) L($"   editor tras pegar=\"{((ValuePattern)vp).Current.Value}\"");
        var btn = BotonEnviar(doc);
        if (btn != null) { try { ((InvokePattern)btn.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); L("   Invoke Enviar ok"); } catch (Exception ex) { L("   Invoke fallo: " + ex.Message); } }
        Thread.Sleep(2500);
        L("   ultimo mensaje: " + UltimoMensaje(doc));
        // devolver el foco (con AttachThreadInput contra el hilo de Teams, que ahora es el activo)
        if (fg0 != IntPtr.Zero && fg0 != hwnd)
        {
            uint hiloTeams = GetWindowThreadProcessId(hwnd, out _);
            bool a2 = AttachThreadInput(yo, hiloTeams, true);
            SetForegroundWindow(fg0);
            if (a2) AttachThreadInput(yo, hiloTeams, false);
            ShowWindow(hwnd, 6);
            L($"   foco devuelto: {GetForegroundWindow() == fg0} · Teams minimizada de nuevo: {IsIconic(hwnd)}");
        }
    }

    static void Abrir(IntPtr hwnd, AutomationElement doc, AutomationElement arbol, string nombre)
    {
        foreach (AutomationElement it in arbol.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem)))
        {
            string n = it.Cached.Name ?? "";
            if (n.IndexOf(nombre, StringComparison.OrdinalIgnoreCase) < 0) continue;
            L($"   encontrado: {n}");
            try { ((SelectionItemPattern)it.GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); L("   Select ok"); }
            catch (Exception ex)
            {
                L("   Select fallo: " + ex.Message);
                try { ((InvokePattern)it.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); L("   Invoke ok"); } catch (Exception ex2) { L("   Invoke fallo: " + ex2.Message); }
            }
            Thread.Sleep(2500);
            var t = new StringBuilder(512); GetWindowText(hwnd, t, 512);
            L($"   titulo ahora: {t}");
            Mensajes(doc);
            return;
        }
        L("   no encontre ningun chat con ese nombre");
    }

    static void CtrlV()
    {
        var inp = new INPUT[4];
        inp[0].type = 1; inp[0].U.ki = new KEYBDINPUT { wVk = 0x11 };
        inp[1].type = 1; inp[1].U.ki = new KEYBDINPUT { wVk = 0x56 };
        inp[2].type = 1; inp[2].U.ki = new KEYBDINPUT { wVk = 0x56, dwFlags = 2 };
        inp[3].type = 1; inp[3].U.ki = new KEYBDINPUT { wVk = 0x11, dwFlags = 2 };
        uint n = SendInput(4, inp, Marshal.SizeOf(typeof(INPUT)));
        L($"   SendInput Ctrl+V: {n}/4 (sizeof INPUT={Marshal.SizeOf(typeof(INPUT))})");
    }

    /// <summary>Escribir SIN foco: WM_CHAR por PostMessage al HWND de render del WebView2 (y al top-level), con la ventana minimizada.</summary>
    static void Tipear(IntPtr hwnd, AutomationElement doc, AutomationElement editor, string texto)
    {
        if (!IsIconic(hwnd)) { ShowWindow(hwnd, 6); Thread.Sleep(600); }
        L($"   ventana minimizada: {IsIconic(hwnd)} · foreground es Teams: {GetForegroundWindow() == hwnd}");
        try { editor.SetFocus(); L("   SetFocus (UIA) ok"); } catch (Exception ex) { L("   SetFocus fallo: " + ex.Message); }
        Thread.Sleep(300);
        L($"   tras SetFocus: minimizada={IsIconic(hwnd)} foreground es Teams={GetForegroundWindow() == hwnd}");
        var hijos = new List<(IntPtr h, string cls)>();
        EnumChildWindows(hwnd, (h, l) => { var s = new StringBuilder(256); GetClassName(h, s, 256); hijos.Add((h, s.ToString())); return true; }, IntPtr.Zero);
        L("   hijos: " + string.Join(", ", hijos.Select(x => x.cls + "=" + x.h)));
        var render = hijos.Where(x => x.cls == "Chrome_RenderWidgetHostHWND").Select(x => x.h).FirstOrDefault();
        var widget = hijos.Where(x => x.cls == "Chrome_WidgetWin_1").Select(x => x.h).FirstOrDefault();
        object vp; editor.TryGetCurrentPattern(ValuePattern.Pattern, out vp);
        foreach (var destino in new[] { ("render", render), ("widget", widget), ("top", hwnd) })
        {
            if (destino.Item2 == IntPtr.Zero) continue;
            foreach (char c in texto) PostMessage(destino.Item2, WM_CHAR, (IntPtr)c, IntPtr.Zero);
            Thread.Sleep(700);
            string v = vp != null ? ((ValuePattern)vp).Current.Value : "?";
            L($"   WM_CHAR a {destino.Item1} ({destino.Item2}): editor=\"{v.Replace("\n", "\\n")}\"");
            if (v.Contains(texto)) { L("   >>> FUNCIONA sin foco por " + destino.Item1); break; }
        }
        // WM_PASTE, por si Chromium lo atiende
        Portapapeles("<b>negrita por WM_PASTE</b>", "negrita por WM_PASTE");
        if (render != IntPtr.Zero) { PostMessage(render, WM_PASTE, IntPtr.Zero, IntPtr.Zero); Thread.Sleep(700); L($"   WM_PASTE a render: editor=\"{((ValuePattern)vp).Current.Value.Replace("\n", "\\n")}\""); }
        L("   (no envio nada: solo mido si el texto entro al editor)");
    }

    /// <summary>Abre la barra de formato del editor y lista sus botones (nombre, id, toggle), luego la cierra.</summary>
    static void Formato(IntPtr hwnd, AutomationElement doc, AutomationElement editor)
    {
        if (!IsIconic(hwnd)) { ShowWindow(hwnd, 6); Thread.Sleep(500); }
        AutomationElement btnFormato = null;
        foreach (AutomationElement b in doc.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
        {
            string n = b.Cached.Name ?? "";
            if (n.IndexOf("formato", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("format", StringComparison.OrdinalIgnoreCase) >= 0) { btnFormato = b; L($"   boton formato: \"{n}\""); break; }
        }
        if (btnFormato == null) { L("   no encontre el boton de formato"); return; }
        ((InvokePattern)btnFormato.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Thread.Sleep(1200);
        L("   --- botones/toggles visibles tras abrir formato ---");
        var antes = new HashSet<string>();
        foreach (AutomationElement b in doc.FindAll(TreeScope.Descendants, new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox))))
        {
            string n = b.Cached.Name ?? ""; string id = b.Cached.AutomationId ?? "";
            var r = b.Cached.BoundingRectangle;
            bool tog = false, tst = false; object tp;
            if (b.TryGetCurrentPattern(TogglePattern.Pattern, out tp)) { tog = true; tst = ((TogglePattern)tp).Current.ToggleState == ToggleState.On; }
            if (n.Length == 0) continue;
            L($"   {b.Cached.ControlType.ProgrammaticName.Replace("ControlType.", "")} \"{n}\" id=\"{id}\" {(int)r.Width}x{(int)r.Height}{(tog ? $" toggle={tst}" : "")}");
        }
        // el editor en modo expandido: nuevo id?
        var ed2 = BuscarEditor(doc);
        L($"   editor ahora: id={ed2?.Cached.AutomationId} name=\"{ed2?.Cached.Name}\"");
        // cerrar la barra de formato
        foreach (AutomationElement b in doc.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
        {
            string n = b.Cached.Name ?? "";
            if (n.IndexOf("formato", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("format", StringComparison.OrdinalIgnoreCase) >= 0) { ((InvokePattern)b.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); L($"   cerrado con \"{n}\""); break; }
        }
    }

    static void Portapapeles(string html, string texto)
    {
        var data = new DataObject();
        data.SetData(DataFormats.Html, CfHtml(html));
        data.SetData(DataFormats.UnicodeText, texto);
        Clipboard.SetDataObject(data, true);
    }

    /// <summary>
    /// Envio SILENCIOSO: ventana minimizada, WM_CHAR al render SIN SetFocus; solo si no entra, SetFocus (sigue minimizada).
    /// Mide en cada paso si el foreground dejo de ser la ventana del que llama. Manda de verdad al chat abierto.
    /// </summary>
    static void Directo(IntPtr hwnd, AutomationElement doc, AutomationElement editor, string texto)
    {
        IntPtr fg0 = GetForegroundWindow();
        var t0 = new StringBuilder(256); GetWindowText(fg0, t0, 256);
        L($"foreground inicial: {fg0} \"{t0}\"");
        if (!IsIconic(hwnd)) { ShowWindow(hwnd, 6); Thread.Sleep(500); }
        L($"Teams minimizada: {IsIconic(hwnd)} · foreground sigue siendo el inicial: {GetForegroundWindow() == fg0}");
        IntPtr render = IntPtr.Zero;
        EnumChildWindows(hwnd, (h, l) => { var s = new StringBuilder(64); GetClassName(h, s, 64); if (s.ToString() == "Chrome_RenderWidgetHostHWND") { render = h; return false; } return true; }, IntPtr.Zero);
        object vp; editor.TryGetCurrentPattern(ValuePattern.Pattern, out vp);
        Func<string> valor = () => vp != null ? ((ValuePattern)vp).Current.Value.Replace("\r", "").TrimEnd('\n') : "?";
        string placeholder = valor();
        // 1) sin foco
        foreach (char c in texto) PostMessage(render, WM_CHAR, (IntPtr)(int)c, IntPtr.Zero);
        Thread.Sleep(600);
        string v = valor();
        bool entro = v == texto;
        L($"paso 1 (sin SetFocus): editor=\"{v}\" entró={entro} · foreground intacto={GetForegroundWindow() == fg0}");
        if (!entro)
        {
            try { editor.SetFocus(); } catch (Exception ex) { L("SetFocus: " + ex.Message); }
            Thread.Sleep(300);
            L($"paso 2 (tras SetFocus): minimizada={IsIconic(hwnd)} · foreground es Teams={GetForegroundWindow() == hwnd} · intacto={GetForegroundWindow() == fg0}");
            string v1 = valor();
            if (v1.Length > 0 && v1 != placeholder) for (int i = 0; i < v1.Length + 2; i++) { PostMessage(render, WM_KEYDOWN, (IntPtr)8, IntPtr.Zero); PostMessage(render, WM_KEYUP, (IntPtr)8, IntPtr.Zero); }
            Thread.Sleep(200);
            foreach (char c in texto) PostMessage(render, WM_CHAR, (IntPtr)(int)c, IntPtr.Zero);
            Thread.Sleep(600);
            v = valor(); entro = v == texto;
            L($"paso 2 tipeo: editor=\"{v}\" entró={entro}");
        }
        if (!entro) { L("no entró: no envío"); return; }
        var enviar = doc.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>().FirstOrDefault(b => (b.Cached.Name ?? "").StartsWith("Enviar"));
        if (enviar == null) { L("sin boton Enviar"); return; }
        ((InvokePattern)enviar.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Thread.Sleep(2000);
        L($"tras Enviar: editor=\"{valor()}\" · minimizada={IsIconic(hwnd)} · foreground es Teams={GetForegroundWindow() == hwnd}");
        // devolver el foco si se movio
        if (GetForegroundWindow() != fg0 && fg0 != IntPtr.Zero)
        {
            uint hiloAct = GetWindowThreadProcessId(GetForegroundWindow(), out _), yo = GetCurrentThreadId();
            bool att = hiloAct != yo && AttachThreadInput(yo, hiloAct, true);
            SetForegroundWindow(fg0);
            if (att) AttachThreadInput(yo, hiloAct, false);
            Thread.Sleep(200);
            L($"foco devuelto al inicial: {GetForegroundWindow() == fg0}");
        }
        L("ultimo: " + UltimoMensaje(doc));
    }

    /// <summary>Explora el control de presencia de Teams: lee el estado del avatar, abre su menu y vuelca las opciones.</summary>
    static void Presencia(IntPtr hwnd, AutomationElement doc)
    {
        if (!IsIconic(hwnd)) { ShowWindow(hwnd, 6); Thread.Sleep(400); }
        AutomationElement avatar = null;
        foreach (AutomationElement b in doc.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
        {
            string id = b.Cached.AutomationId ?? "", n = b.Cached.Name ?? "";
            if (id.IndexOf("me-control-avatar", StringComparison.OrdinalIgnoreCase) >= 0 || n.StartsWith("Tu perfil", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Your profile", StringComparison.OrdinalIgnoreCase))
            { avatar = b; L($"avatar: id=\"{id}\" name=\"{n}\""); break; }
        }
        if (avatar == null) { L("no encontre el boton del avatar"); return; }
        L(">>> ESTADO ACTUAL segun el avatar: " + (avatar.Cached.Name ?? ""));
        try { ((InvokePattern)avatar.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); L("menu del avatar abierto"); }
        catch (Exception ex) { L("no pude abrir el menu: " + ex.Message); return; }
        Thread.Sleep(1600);
        L("--- items del menu (todo lo que aparecio) ---");
        foreach (AutomationElement e in doc.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            string n = e.Cached.Name ?? "", id = e.Cached.AutomationId ?? "";
            if (n.Length == 0 && id.Length == 0) continue;
            string ct = e.Cached.ControlType.ProgrammaticName.Replace("ControlType.", "");
            if (ct != "MenuItem" && ct != "Button" && ct != "ListItem" && ct != "Text" && ct != "RadioButton") continue;
            if (Regex.IsMatch(n, @"Disponible|Available|Ocupado|Busy|No molestar|Do not disturb|Vuelvo|right back|Ausente|Away|Aparecer|Appear|Restablecer|Reset|estado|status|Duraci|Duration", RegexOptions.IgnoreCase))
                L($"   {ct} id=\"{id}\" name=\"{n}\"");
        }
        Thread.Sleep(300);
        // cerrar con Escape
        var inp = new INPUT[2];
        inp[0].type = 1; inp[0].U.ki = new KEYBDINPUT { wVk = 0x1B };
        inp[1].type = 1; inp[1].U.ki = new KEYBDINPUT { wVk = 0x1B, dwFlags = 2 };
        SendInput(2, inp, Marshal.SizeOf(typeof(INPUT)));
        Thread.Sleep(500);
        L("menu cerrado con Escape · estado ahora: " + (doc.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "idna-me-control-avatar-trigger"))?.Cached.Name ?? "?"));
    }

    // --- SendInput: texto Unicode y atajos con modificadores reales, hacia la ventana en foco (Teams minimizada)
    static void Tecla(ushort vk, bool up) { var i = new INPUT[1]; i[0].type = 1; i[0].U.ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u }; SendInput(1, i, Marshal.SizeOf(typeof(INPUT))); }
    static void Unicode(string s)
    {
        foreach (char c in s)
        {
            var i = new INPUT[2];
            i[0].type = 1; i[0].U.ki = new KEYBDINPUT { wScan = c, dwFlags = 4 };          // KEYEVENTF_UNICODE
            i[1].type = 1; i[1].U.ki = new KEYBDINPUT { wScan = c, dwFlags = 4 | 2 };
            SendInput(2, i, Marshal.SizeOf(typeof(INPUT)));
        }
    }
    static void Atajo(bool ctrl, bool shift, bool alt, ushort vk)
    {
        if (ctrl) Tecla(0x11, false); if (shift) Tecla(0x10, false); if (alt) Tecla(0x12, false);
        Tecla(vk, false); Tecla(vk, true);
        if (alt) Tecla(0x12, true); if (shift) Tecla(0x10, true); if (ctrl) Tecla(0x11, true);
        Thread.Sleep(60);
    }

    /// <summary>Formato por atajos de Teams (Ctrl+B, Ctrl+I, Shift+Enter) con SendInput, ventana minimizada y en foco. Manda de verdad.</summary>
    static void SendInputRico(IntPtr hwnd, AutomationElement doc, AutomationElement editor)
    {
        IntPtr fg0 = GetForegroundWindow();
        if (!IsIconic(hwnd)) { ShowWindow(hwnd, 6); Thread.Sleep(400); }
        try { editor.SetFocus(); } catch (Exception ex) { L("SetFocus: " + ex.Message); }
        Thread.Sleep(300);
        L($"minimizada={IsIconic(hwnd)} · foreground es Teams={GetForegroundWindow() == hwnd}");
        object vp; editor.TryGetCurrentPattern(ValuePattern.Pattern, out vp);
        Func<string> valor = () => vp != null ? ((ValuePattern)vp).Current.Value.Replace("\n", "\\n") : "?";
        // limpiar por las dudas (Ctrl+A, Supr) y esperar
        Atajo(true, false, false, (ushort)'A'); Thread.Sleep(100); Tecla(0x2E, false); Tecla(0x2E, true); Thread.Sleep(350);
        L($"editor limpio: \"{valor()}\"");
        Action<string> texto = s => { Unicode(s); Thread.Sleep(40 + s.Length * 6); };
        Action<bool, bool, bool, ushort> atajo = (c, sh, a, vk) => { Thread.Sleep(90); Atajo(c, sh, a, vk); Thread.Sleep(120); };
        texto("prueba SendInput: ");
        atajo(true, false, false, (ushort)'B'); texto("negrita"); atajo(true, false, false, (ushort)'B');
        texto(" y ");
        atajo(true, false, false, (ushort)'I'); texto("cursiva"); atajo(true, false, false, (ushort)'I');
        texto(" · segunda línea con ñ y acentos:");
        atajo(false, true, false, 0x0D);          // Shift+Enter
        atajo(true, true, false, (ushort)'8');    // lista con viñetas
        texto("ítem uno");
        atajo(false, true, false, 0x0D);
        texto("ítem dos");
        // esperar a que el editor se estabilice
        string prev = ""; int estable = 0;
        for (int i = 0; i < 30 && estable < 3; i++) { Thread.Sleep(150); string v = valor(); if (v == prev) estable++; else { estable = 0; prev = v; } }
        L($"editor estable: \"{valor()}\"");
        bool ok = valor().Contains("prueba SendInput: negrita y cursiva") && valor().Contains("ítem dos");
        L($"texto completo y en orden: {ok}");
        var enviar = doc.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>().FirstOrDefault(b => (b.Cached.Name ?? "").StartsWith("Enviar"));
        if (ok && enviar != null) { ((InvokePattern)enviar.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); L("Invoke Enviar"); }
        else { L("NO envío (texto incompleto); limpio"); Atajo(true, false, false, (ushort)'A'); Thread.Sleep(100); Tecla(0x2E, false); Tecla(0x2E, true); }
        Thread.Sleep(2500);
        L($"tras enviar: editor=\"{valor()}\" · minimizada={IsIconic(hwnd)}");
        L("ultimo: " + UltimoMensaje(doc));
        if (fg0 != IntPtr.Zero && GetForegroundWindow() != fg0) { uint hiloAct = GetWindowThreadProcessId(GetForegroundWindow(), out _), yo = GetCurrentThreadId(); bool att = hiloAct != yo && AttachThreadInput(yo, hiloAct, true); SetForegroundWindow(fg0); if (att) AttachThreadInput(yo, hiloAct, false); L($"foco devuelto: {GetForegroundWindow() == fg0}"); }
    }

    /// <summary>Vacia el editor (borrador sucio) a Backspazos con foco, verificando.</summary>
    static void Limpiar(IntPtr hwnd, AutomationElement doc, AutomationElement editor)
    {
        if (!IsIconic(hwnd)) { ShowWindow(hwnd, 6); Thread.Sleep(400); }
        IntPtr render = IntPtr.Zero;
        EnumChildWindows(hwnd, (h, l) => { var s = new StringBuilder(64); GetClassName(h, s, 64); if (s.ToString() == "Chrome_RenderWidgetHostHWND") { render = h; return false; } return true; }, IntPtr.Zero);
        object vp; editor.TryGetCurrentPattern(ValuePattern.Pattern, out vp);
        Func<string> valor = () => vp != null ? ((ValuePattern)vp).Current.Value : "?";
        L($"antes: largo={valor().Length} \"{valor().Replace("\n", "\\n")}\"");
        try { editor.SetFocus(); } catch (Exception ex) { L("SetFocus: " + ex.Message); }
        Thread.Sleep(300);
        for (int vuelta = 0; vuelta < 3; vuelta++)
        {
            if (valor().TrimEnd('\n') == "Escriba un mensaje" || valor().Trim().Length == 0) break;
            if (GetForegroundWindow() == hwnd) { Atajo(true, false, false, (ushort)'A'); Thread.Sleep(120); Tecla(0x2E, false); Tecla(0x2E, true); Thread.Sleep(400); L($"vuelta {vuelta + 1} (Ctrl+A, Supr): largo={valor().Length} \"{valor().Replace("\n", "\\n")}\""); }
            else
            {
                int n = valor().Length + 20;
                for (int i = 0; i < n; i++) { PostMessage(render, WM_KEYDOWN, (IntPtr)8, IntPtr.Zero); PostMessage(render, WM_KEYUP, (IntPtr)8, IntPtr.Zero); }
                Thread.Sleep(400);
                L($"vuelta {vuelta + 1} (Backspace): largo={valor().Length} \"{valor().Replace("\n", "\\n")}\"");
            }
        }
        // cerrar la barra de formato si quedo abierta
        var cerrar = doc.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>().FirstOrDefault(b => (b.Cached.Name ?? "").StartsWith("Ocultar opciones de formato"));
        if (cerrar != null) { ((InvokePattern)cerrar.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); L("barra de formato cerrada"); }
    }

    /// <summary>Prueba de envio con formato de punta a punta: abre barra, alterna B, tipea, envia. Manda de verdad a este chat.</summary>
    static void Rico(IntPtr hwnd, AutomationElement doc, AutomationElement editor)
    {
        if (!IsIconic(hwnd)) { ShowWindow(hwnd, 6); Thread.Sleep(500); }
        try { editor.SetFocus(); } catch (Exception ex) { L("SetFocus: " + ex.Message); }
        Thread.Sleep(300);
        var abrir = doc.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>().FirstOrDefault(b => (b.Cached.Name ?? "").StartsWith("Mostrar opciones de formato"));
        if (abrir != null) { ((InvokePattern)abrir.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); Thread.Sleep(1000); }
        IntPtr render = IntPtr.Zero;
        EnumChildWindows(hwnd, (h, l) => { var s = new StringBuilder(64); GetClassName(h, s, 64); if (s.ToString() == "Chrome_RenderWidgetHostHWND") { render = h; return false; } return true; }, IntPtr.Zero);
        Func<string, AutomationElement> btn = pref => doc.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>().FirstOrDefault(b => (b.Cached.Name ?? "").StartsWith(pref));
        Action<string> toggle = pref => { var b = btn(pref); if (b != null) { try { object tp; if (b.TryGetCurrentPattern(TogglePattern.Pattern, out tp)) ((TogglePattern)tp).Toggle(); else ((InvokePattern)b.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); } catch (Exception ex) { L("toggle " + pref + ": " + ex.Message); } Thread.Sleep(80); try { editor.SetFocus(); } catch { } Thread.Sleep(90); } };
        Action<string> tipear = t => { foreach (char c in t) PostMessage(render, WM_CHAR, (IntPtr)(int)c, IntPtr.Zero); Thread.Sleep(300); };
        object vp; editor.TryGetCurrentPattern(ValuePattern.Pattern, out vp);
        tipear("prueba ");
        toggle("Negrita"); tipear("negrita"); toggle("Negrita");
        tipear(" y "); toggle("Cursiva"); tipear("cursiva"); toggle("Cursiva");
        tipear(" desde teams-autoleave");
        Thread.Sleep(400);
        L("editor: \"" + (vp != null ? ((ValuePattern)vp).Current.Value.Replace("\n", "\\n") : "?") + "\"");
        var enviar = btn("Enviar");
        if (enviar != null) { ((InvokePattern)enviar.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); L("Invoke Enviar"); }
        Thread.Sleep(2500);
        L("ultimo: " + UltimoMensaje(doc));
    }

    /// <summary>Arma el formato CF_HTML con offsets en bytes UTF-8, como lo espera Chromium.</summary>
    static string CfHtml(string fragmento)
    {
        string pre = "<html><body><!--StartFragment-->", post = "<!--EndFragment--></body></html>";
        string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        int hl = string.Format(header, 0, 0, 0, 0).Length;
        var enc = Encoding.UTF8;
        int startHtml = hl, startFrag = hl + enc.GetByteCount(pre), endFrag = startFrag + enc.GetByteCount(fragmento), endHtml = endFrag + enc.GetByteCount(post);
        return string.Format(header, startHtml, endHtml, startFrag, endFrag) + pre + fragmento + post;
    }
}
