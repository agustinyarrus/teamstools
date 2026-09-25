using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TeamsTools
{
    // =====================================================================================================
    // FONDO: la UI lee fotos, el fondo trabaja.
    //
    // La regla de toda la app: el hilo de la interfaz NUNCA espera a nada de afuera — ni disco, ni procesos,
    // ni Teams por UI Automation, ni la red. Lo lento corre en hilos de fondo que publican fotos inmutables;
    // la UI pinta la última foto y un indicador cuando algo está en camino. Estas son las piezas:
    //
    //   Disco      escrituras atómicas diferidas (gana la última versión; 30 clics = 1 escritura)
    //   Fondo      correr algo de fondo con indicador de carga y entregar el resultado en la UI
    //   Sondeo<T>  medir algo cada N ms en un hilo propio y publicar la foto
    //   LatidoUi   sismógrafo: mide cuánto tarda la UI en atender y atribuye cada traba a su culpable
    //   Migas      qué estaba haciendo la UI (para esa atribución)
    // =====================================================================================================

    /// <summary>
    /// Escritura a disco diferida, atómica y en orden. El que llama arma el texto en el momento (foto
    /// consistente) y sigue; un único hilo de fondo lo baja con tmp + File.Replace. Si llegan varias versiones
    /// del mismo archivo antes de escribir, gana la última (coalescencia). Los agregados (logs, jsonl) respetan
    /// el orden de llegada. Un fallo (antivirus, archivo tomado) se reintenta con espera creciente.
    /// </summary>
    internal static class Disco
    {
        const int VentanaMs = 60;              // junta ráfagas: un chip cliqueado 10 veces seguidas = 1 escritura
        const int ReintentosMax = 8;

        static readonly object candado = new object();
        static readonly Dictionary<string, string> reemplazos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, StringBuilder> agregados = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, int> fallosPorRuta = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static readonly AutoResetEvent hay = new AutoResetEvent(false);
        static Thread hilo;
        static int enVuelo;

        public static long Escrituras, Fallos;
        public static string UltimoError = "";

        /// <summary>Reemplaza el archivo entero (atómico). O(1) para el que llama.</summary>
        public static void Escribir(string ruta, string contenido)
        {
            lock (candado) reemplazos[ruta] = contenido ?? "";
            Despertar();
        }

        /// <summary>Agrega al final del archivo, en el orden de llegada. O(1) amortizado para el que llama.</summary>
        public static void Agregar(string ruta, string texto)
        {
            lock (candado)
            {
                if (!agregados.TryGetValue(ruta, out var sb)) agregados[ruta] = sb = new StringBuilder();
                sb.Append(texto);
            }
            Despertar();
        }

        /// <summary>Espera a que no quede nada pendiente (salida de la app, modos de línea de comando).</summary>
        public static bool Vaciar(int msMax = 4000)
        {
            var reloj = Stopwatch.StartNew();
            while (reloj.ElapsedMilliseconds < msMax)
            {
                lock (candado) if (reemplazos.Count == 0 && agregados.Count == 0 && Volatile.Read(ref enVuelo) == 0) return true;
                hay.Set();
                Thread.Sleep(15);
            }
            return false;
        }

        static void Despertar()
        {
            if (hilo == null)
                lock (candado)
                    // prioridad NORMAL a propósito: casi no usa CPU (es I/O), pero toma un candado que la UI también toma al
                    // loguear. A BelowNormal, con la CPU saturada por otros procesos, Windows lo desalojaba con el candado
                    // tomado y la UI esperaba (inversión de prioridad): una traba de 1,2 s sin culpable el 23-sep.
                    if (hilo == null) { hilo = new Thread(Bucle) { IsBackground = true, Name = "disco" }; hilo.Start(); }
            hay.Set();
        }

        static void Bucle()
        {
            while (true)
            {
                hay.WaitOne(1000);
                Thread.Sleep(VentanaMs);
                KeyValuePair<string, string>[] rs; KeyValuePair<string, string>[] ags;
                lock (candado)
                {
                    rs = reemplazos.ToArray(); reemplazos.Clear();
                    ags = agregados.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.ToString())).ToArray(); agregados.Clear();
                    Interlocked.Add(ref enVuelo, rs.Length + ags.Length);
                }
                foreach (var kv in rs) { Hacer(kv.Key, () => Reemplazar(kv.Key, kv.Value), () => Escribir(kv.Key, kv.Value), reemplazo: true); Interlocked.Decrement(ref enVuelo); }
                foreach (var kv in ags) { Hacer(kv.Key, () => AgregarYa(kv.Key, kv.Value), () => Reencolar(kv.Key, kv.Value), reemplazo: false); Interlocked.Decrement(ref enVuelo); }
            }
        }

        static void Hacer(string ruta, Action escribir, Action reintentar, bool reemplazo)
        {
            try
            {
                escribir();
                Interlocked.Increment(ref Escrituras);
                lock (candado) fallosPorRuta.Remove(ruta);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref Fallos);
                UltimoError = Path.GetFileName(ruta) + ": " + ex.Message;
                int n;
                lock (candado) { fallosPorRuta.TryGetValue(ruta, out n); fallosPorRuta[ruta] = ++n; }
                if (n > ReintentosMax) { lock (candado) fallosPorRuta.Remove(ruta); return; }
                // si mientras tanto llegó una versión más nueva del mismo archivo, esa gana: no se pisa con la vieja
                bool hayNueva;
                lock (candado) hayNueva = reemplazo && reemplazos.ContainsKey(ruta);
                if (!hayNueva) { Thread.Sleep(Math.Min(2000, 50 << Math.Min(n, 5))); reintentar(); }
            }
        }

        static void Reencolar(string ruta, string texto)
        {
            // lo que no se pudo agregar va ADELANTE de lo que llegó después: el orden del archivo se respeta
            lock (candado)
            {
                if (agregados.TryGetValue(ruta, out var sb)) sb.Insert(0, texto); else agregados[ruta] = new StringBuilder(texto);
            }
            hay.Set();
        }

        static void Reemplazar(string ruta, string contenido)
        {
            string dir = Path.GetDirectoryName(ruta);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = ruta + ".tmp";
            File.WriteAllText(tmp, contenido, new UTF8Encoding(false));
            if (File.Exists(ruta)) File.Replace(tmp, ruta, null); else File.Move(tmp, ruta);
        }

        static void AgregarYa(string ruta, string texto)
        {
            string dir = Path.GetDirectoryName(ruta);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(ruta, texto, new UTF8Encoding(false));
        }
    }

    /// <summary>
    /// Qué estaba haciendo la UI. Un filtro de mensajes anota cada mensaje que se despacha (timer, pintura,
    /// BeginInvoke…) y los caminos calientes se anotan a mano con <see cref="Poner"/>. Cuando el
    /// <see cref="LatidoUi"/> detecta una traba, la miga vigente es el culpable.
    /// </summary>
    internal static class Migas
    {
        static string actual = "";
        public static string Actual => Volatile.Read(ref actual);

        public static Marca Poner(string que)
        {
            string antes = Volatile.Read(ref actual);
            Volatile.Write(ref actual, que ?? "");
            return new Marca(antes);
        }

        internal struct Marca : IDisposable
        {
            readonly string antes;
            public Marca(string a) { antes = a; }
            public void Dispose() => Volatile.Write(ref actual, antes ?? "");
        }

        /// <summary>Filtro de mensajes: anota el mensaje en curso sin tocarlo (siempre devuelve false).</summary>
        internal sealed class Filtro : IMessageFilter
        {
            public bool PreFilterMessage(ref Message m)
            {
                string que;
                switch (m.Msg)
                {
                    case 0x0113: que = "timer"; break;
                    case 0x000F: que = "pintura"; break;
                    case 0x0200: case 0x0201: case 0x0202: case 0x020A: que = "mouse"; break;
                    case 0x0100: case 0x0101: case 0x0102: que = "teclado"; break;
                    default: que = m.Msg >= 0xC000 ? "BeginInvoke/registrado" : "mensaje 0x" + m.Msg.ToString("x4"); break;
                }
                Control c = null;
                try { c = Control.FromHandle(m.HWnd); } catch { }
                Volatile.Write(ref actual, que + (c != null ? " · " + c.GetType().Name : ""));
                return false;
            }
        }
    }

    /// <summary>
    /// Sismógrafo de la UI. Un hilo de fondo le manda al hilo de la interfaz un BeginInvoke vacío cada
    /// 100 ms y mide cuánto tarda en correr. Con la UI sana tarda &lt; 5 ms; si algo la trabó, el retraso
    /// ES la traba, y la miga vigente dice quién fue. Así «que no se tilde nunca» se mide con números.
    /// </summary>
    internal static class LatidoUi
    {
        public const int UmbralTrabaMs = 250;      // se siente como «tildado»
        const int PeriodoMs = 100;
        const int VentanaSeg = 300;                // estadística de los últimos 5 minutos

        static Control ui;
        static Logger log;
        static Thread hilo;
        static readonly object candado = new object();
        static readonly Queue<(DateTime t, double ms)> muestras = new Queue<(DateTime, double)>();
        static readonly List<(DateTime t, double ms, string culpable)> trabas = new List<(DateTime, double, string)>();

        public static double UltimoMs { get; private set; }
        public static int TrabasTotales { get; private set; }
        public static double PeorMs { get; private set; }
        public static string PeorCulpable { get; private set; } = "";

        public static void Iniciar(Control control, Logger l)
        {
            if (hilo != null) return;
            ui = control; log = l;
            Application.AddMessageFilter(new Migas.Filtro());
            hilo = new Thread(Bucle) { IsBackground = true, Name = "latido-ui", Priority = ThreadPriority.AboveNormal };
            hilo.Start();
        }

        static void Bucle()
        {
            var atendido = new ManualResetEventSlim(false);
            while (true)
            {
                Thread.Sleep(PeriodoMs);
                if (ui == null || ui.IsDisposed || !ui.IsHandleCreated) continue;
                atendido.Reset();
                int gc0 = GC.CollectionCount(2);
                long t0 = Stopwatch.GetTimestamp();
                try { ui.BeginInvoke(new Action(atendido.Set)); } catch { continue; }
                // mientras espera, mira la miga: si la espera se alarga, la miga del momento es la culpable
                string culpable = "";
                while (!atendido.Wait(40))
                {
                    double ya = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    if (ya >= UmbralTrabaMs && culpable.Length == 0) culpable = Migas.Actual;
                    if (ya > 120000) break;   // la UI murió: no seguir esperando para siempre
                }
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (ms >= UmbralTrabaMs && culpable.Length == 0)
                    culpable = GC.CollectionCount(2) > gc0 ? "recolección de basura (GC gen2)"
                             : "ningún mensaje en curso: CPU saturada o paginación del sistema";
                Anotar(ms, culpable);
            }
        }

        static void Anotar(double ms, string culpable)
        {
            var ahora = DateTime.Now;
            lock (candado)
            {
                UltimoMs = ms;
                muestras.Enqueue((ahora, ms));
                while (muestras.Count > 0 && (ahora - muestras.Peek().t).TotalSeconds > VentanaSeg) muestras.Dequeue();
                if (ms >= UmbralTrabaMs)
                {
                    TrabasTotales++;
                    trabas.Add((ahora, ms, culpable));
                    if (trabas.Count > 50) trabas.RemoveAt(0);
                    if (ms > PeorMs) { PeorMs = ms; PeorCulpable = culpable; }
                }
            }
            if (ms >= UmbralTrabaMs * 2) try { log?.Aviso($"UI: trabada {ms:0} ms · culpable «{(culpable.Length > 0 ? culpable : "¿?")}»"); } catch { }
        }

        /// <summary>Foto de la salud de la UI en la ventana de 5 minutos.</summary>
        public static (double p50, double p99, double max, int trabas5min, int muestras) Estadistica()
        {
            lock (candado)
            {
                if (muestras.Count == 0) return (0, 0, 0, 0, 0);
                var xs = muestras.Select(m => m.ms).OrderBy(x => x).ToArray();
                double P(double q) => xs[Math.Min(xs.Length - 1, (int)Math.Floor(q * (xs.Length - 1)))];
                var ahora = DateTime.Now;
                return (P(0.5), P(0.99), xs[xs.Length - 1], trabas.Count(t => (ahora - t.t).TotalSeconds <= VentanaSeg), xs.Length);
            }
        }

        public static (DateTime t, double ms, string culpable)[] UltimasTrabas(int n)
        {
            lock (candado) return trabas.Skip(Math.Max(0, trabas.Count - n)).ToArray();
        }
    }

    /// <summary>Correr algo de fondo con indicador de carga, y entregar el resultado en la UI.</summary>
    internal static class Fondo
    {
        /// <summary>
        /// Corre <paramref name="trabajo"/> en el pool, lo anota en <see cref="Tareas"/> (el indicador global
        /// lo muestra) y entrega el resultado en el hilo de la UI con BeginInvoke. Jamás bloquea al que llama.
        /// </summary>
        public static void Correr<T>(Control ui, string que, Func<T> trabajo, Action<T> listo, Action<Exception> error = null,
            Color? tinte = null, bool mostrar = true, Logger log = null)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                T r = default(T); Exception fallo = null;
                using (mostrar ? Tareas.Empezar(que, "", tinte) : null)
                {
                    try { r = trabajo(); } catch (Exception ex) { fallo = ex; }
                }
                if (fallo != null) try { log?.Error(que + ": " + fallo.Message); } catch { }
                EnUi(ui, () =>
                {
                    if (fallo == null) listo?.Invoke(r);
                    else error?.Invoke(fallo);
                });
            });
        }

        public static void Correr(Control ui, string que, Action trabajo, Action listo = null, Action<Exception> error = null, Color? tinte = null, bool mostrar = true, Logger log = null)
            => Correr<bool>(ui, que, () => { trabajo(); return true; }, _ => listo?.Invoke(), error, tinte, mostrar, log);

        /// <summary>BeginInvoke a prueba de controles muertos o sin handle. Nunca espera.</summary>
        public static void EnUi(Control ui, Action a)
        {
            if (a == null) return;
            try { if (ui != null && !ui.IsDisposed && ui.IsHandleCreated) ui.BeginInvoke(a); } catch { }
        }
    }

    /// <summary>
    /// Mide algo cada <c>cadaMs</c> en un hilo propio y publica la última foto. La UI lee <see cref="Ultimo"/>
    /// (una referencia inmutable, sin candados) y se entera por <see cref="Cambio"/>.
    /// </summary>
    internal sealed class Sondeo<T> where T : class
    {
        readonly string nombre;
        readonly int cadaMs;
        readonly Func<T> medir;
        readonly Logger log;
        readonly AutoResetEvent ya = new AutoResetEvent(false);
        volatile bool parar;
        Thread hilo;
        T ultimo;
        int errores;

        public Sondeo(string nombre, int cadaMs, Func<T> medir, Logger log = null) { this.nombre = nombre; this.cadaMs = cadaMs; this.medir = medir; this.log = log; }

        public T Ultimo => Volatile.Read(ref ultimo);
        public DateTime? Medido { get; private set; }
        public long MsUltima { get; private set; }
        public event Action Cambio;

        public void Iniciar()
        {
            if (hilo != null) return;
            hilo = new Thread(Bucle) { IsBackground = true, Name = "sondeo-" + nombre };   // liviano: Normal (ver Disco)
            hilo.Start();
        }
        public void Detener() { parar = true; ya.Set(); }
        public void YaMismo() => ya.Set();

        void Bucle()
        {
            while (!parar)
            {
                var reloj = Stopwatch.StartNew();
                try
                {
                    var r = medir();
                    Volatile.Write(ref ultimo, r);
                    Medido = DateTime.Now; MsUltima = reloj.ElapsedMilliseconds;
                    errores = 0;
                    try { Cambio?.Invoke(); } catch { }
                }
                catch (Exception ex)
                {
                    if (++errores == 1 || errores % 30 == 0) try { log?.Debug($"sondeo {nombre}: {ex.Message}"); } catch { }
                }
                ya.WaitOne(Math.Max(100, cadaMs - (int)reloj.ElapsedMilliseconds));
            }
        }
    }
}
