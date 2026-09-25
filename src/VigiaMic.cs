using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace TeamsTools
{
    // =====================================================================================================
    // VIGÍA DEL MICRÓFONO: ¿Teams te tiene en silencio?
    //
    // La grabación ahora incluye tu micrófono (loopcap -mic). Pero lo que decís con el micrófono en SILENCIO no
    // sale a la llamada y no tiene por qué quedar grabado: este vigía mira el botón de micrófono de la ventana
    // de la reunión unas 3 veces por segundo y avisa cada cambio al grabador, que lo anota con su hora.
    //
    // Barato a propósito: el botón se BUSCA una vez (recorrer el árbol de Teams cuesta cientos de ms) y después
    // solo se lee su nombre (una llamada). Si el botón desaparece (otra ventana, vista compacta), se vuelve a
    // buscar con espera de 5 s. El nombre dice lo que HARÍA el botón: «Silenciar» = hoy estás abierto;
    // «Reactivar» / «Unmute» = hoy estás en silencio. Cada nombre nuevo va al log: la primera llamada con esto
    // deja escrito exactamente qué dice el botón (y si hubiera que ajustar las palabras, se ve ahí).
    // =====================================================================================================
    internal sealed class VigiaMic : IDisposable
    {
        const int PeriodoMs = 300;
        const int EsperaBusquedaMs = 5000;

        static readonly Condition CondId = new PropertyCondition(AutomationElement.AutomationIdProperty, "microphone-button");
        static readonly Condition CondBotones = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton));
        static readonly Regex ReSilenciado = new Regex(@"reactivar|activar (el )?micr|unmute|dejar de silenciar|quitar (el )?silencio|desilenciar",
                                                       RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ReAbierto = new Regex(@"silenciar|\bmute\b|desactivar (el )?micr", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ReEsMic = new Regex(@"micr[oó]fono|\bmic\b|silenci|\bmute\b|unmute", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        readonly Func<IntPtr> ventana;
        readonly Action<bool?> alCambiar;
        readonly Logger log;
        readonly Thread hilo;
        volatile bool parar;
        AutomationElement boton;
        IntPtr hwndBoton;
        DateTime proximaBusqueda = DateTime.MinValue;
        string ultimoNombre = "";

        /// <summary>El último estado leído del botón: true = en silencio; null = no hay llamada o no se sabe.</summary>
        public bool? Estado { get; private set; }

        /// <param name="ventana">La ventana de la reunión (IntPtr.Zero si no hay llamada): la da el vigía de Teams.</param>
        public VigiaMic(Func<IntPtr> ventana, Action<bool?> alCambiar, Logger log)
        {
            this.ventana = ventana;
            this.alCambiar = alCambiar;
            this.log = log;
            // prioridad Normal: casi no usa CPU (una lectura cada 300 ms) y no toma candados de la UI
            hilo = new Thread(Bucle) { IsBackground = true, Name = "vigia-mic" };
            hilo.Start();
        }

        void Bucle()
        {
            while (!parar)
            {
                bool? e;
                try { e = Leer(ventana()); }
                catch (Exception ex) { boton = null; e = null; log.Debug("vigía del micrófono: " + ex.GetType().Name + ": " + ex.Message); }
                if (e != Estado)
                {
                    Estado = e;
                    try { alCambiar(e); } catch (Exception ex) { log.Debug("vigía del micrófono (aviso): " + ex.Message); }
                }
                Thread.Sleep(PeriodoMs);
            }
        }

        bool? Leer(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) { boton = null; hwndBoton = IntPtr.Zero; return null; }
            if (boton == null || hwndBoton != hwnd)
            {
                if (hwndBoton == hwnd && DateTime.Now < proximaBusqueda) return null;   // ya se buscó hace poco
                hwndBoton = hwnd;
                proximaBusqueda = DateTime.Now.AddMilliseconds(EsperaBusquedaMs);
                boton = Buscar(hwnd);
                if (boton == null) return null;
            }
            string n;
            try { n = boton.Current.Name ?? ""; }
            catch (ElementNotAvailableException) { boton = null; return null; }
            if (n != ultimoNombre)
            {
                ultimoNombre = n;
                log.Debug("vigía del micrófono: el botón dice «" + n + "»");
            }
            if (ReSilenciado.IsMatch(n)) return true;      // primero: «dejar de silenciar» también contiene «silenciar»
            if (ReAbierto.IsMatch(n)) return false;
            return null;
        }

        /// <summary>El botón de micrófono: por su AutomationId (Teams nuevo) o, si no, por nombre. O(árbol), una vez.</summary>
        AutomationElement Buscar(IntPtr hwnd)
        {
            var raiz = AutomationElement.FromHandle(hwnd);
            var b = raiz.FindFirst(TreeScope.Descendants, CondId);
            if (b == null)
                foreach (AutomationElement x in raiz.FindAll(TreeScope.Descendants, CondBotones))
                {
                    string n = x.Current.Name ?? "";
                    if (ReEsMic.IsMatch(n) && (ReSilenciado.IsMatch(n) || ReAbierto.IsMatch(n))) { b = x; break; }
                }
            log.Debug(b == null ? "vigía del micrófono: no encuentro el botón de micrófono (reintento en 5 s)"
                                : "vigía del micrófono: botón encontrado · id=«" + b.Current.AutomationId + "» · «" + b.Current.Name + "»");
            return b;
        }

        public void Dispose() => parar = true;
    }
}
