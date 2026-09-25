using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TeamsTools
{
    // =====================================================================================================
    // REPRODUCTOR — escuchar la reunión desde cualquier turno, sabiendo SIEMPRE por dónde va.
    //
    //   ffmpeg ──(PCM 24 kHz mono por el caño)──▶ alimentador (hilo propio) ──▶ waveOut (6 × 100 ms en vuelo)
    //
    // · La posición sale de waveOutGetPosition (muestras que YA sonaron en el parlante), no de un reloj de pared:
    //   el karaoke de la transcripción va clavado al audio aunque la máquina esté cargada y el caño se atrase.
    // · La velocidad la hace ffmpeg con atempo (1,5× sin voz de ardilla) y la posición se corrige por el factor.
    // · Saltar = otra sesión desde ese segundo. Nada de archivos temporales: el .opus se lee directo.
    // · Sin callbacks de winmm: el alimentador sondea WHDR_DONE cada 10 ms. Los callbacks de waveOut corren en hilos
    //   del sistema donde casi nada está permitido; sondear es simple y no puede trabar a nadie.
    // · ffmpeg entra al Job Object de la app (muere con ella) y su stderr se DRENA siempre (un caño lleno lo congela).
    // =====================================================================================================
    internal sealed class Reproductor : IDisposable
    {
        public enum Fase { Quieto, Abriendo, Sonando, Pausa, Terminado, Fallo }

        public const int Hz = 24000;
        const int BytesPorMuestra = 2;
        const int Bufferes = 6;
        const int MsPorBuffer = 100;
        const int BytesPorBuffer = Hz * BytesPorMuestra * MsPorBuffer / 1000;
        const int SondeoMs = 10;
        public static readonly double[] Velocidades = { 1.0, 1.25, 1.5, 2.0 };

        readonly object candado = new object();
        Sesion sesion;
        int generacion;
        Fase fase = Fase.Quieto;
        double ultimaPosicion;
        bool pausaPedida;                  // pausaron mientras abría: se pausa apenas empiece a sonar

        public string Archivo { get; private set; } = "";
        public double Velocidad { get; private set; } = 1.0;
        public string Problema { get; private set; } = "";
        /// <summary>Por dónde sale el audio: null = el de Windows por defecto; si no, parte del nombre (las pruebas usan una salida virtual).</summary>
        public string Dispositivo;
        /// <summary>Avisa de cada cambio de fase. Llega desde el hilo del alimentador: la UI lo tiene que pasar a su hilo.</summary>
        public event Action Cambio;

        public Fase Estado { get { lock (candado) return fase; } }
        public bool Activo { get { var f = Estado; return f == Fase.Abriendo || f == Fase.Sonando || f == Fase.Pausa; } }

        /// <summary>Segundo del ARCHIVO que está sonando ahora, con la velocidad ya descontada. O(1).</summary>
        public double Posicion
        {
            get
            {
                lock (candado)
                {
                    if (sesion != null && (fase == Fase.Sonando || fase == Fase.Pausa)) ultimaPosicion = sesion.Segundo();
                    else if (sesion != null && fase == Fase.Abriendo) ultimaPosicion = sesion.Desde;
                    return ultimaPosicion;
                }
            }
        }

        /// <summary>Empieza a sonar <paramref name="archivo"/> desde <paramref name="desde"/> segundos. Nunca espera: abre de fondo.</summary>
        public void Tocar(string archivo, double desde, double velocidad)
        {
            if (string.IsNullOrEmpty(archivo) || !File.Exists(archivo)) { Parar(); Fallar("no encuentro el audio: " + archivo); return; }
            Sesion vieja, nueva;
            lock (candado)
            {
                vieja = sesion;
                Archivo = archivo;
                Velocidad = Math.Max(0.5, Math.Min(2.0, velocidad));
                Problema = "";
                pausaPedida = false;
                nueva = new Sesion(this, ++generacion, archivo, Math.Max(0, desde), Velocidad, Dispositivo);
                sesion = nueva;
                fase = Fase.Abriendo;
                ultimaPosicion = nueva.Desde;
            }
            vieja?.Cortar();
            Avisar();
            nueva.Arrancar();
        }

        public void Pausar()
        {
            lock (candado)
            {
                if (sesion == null) return;
                if (fase == Fase.Abriendo) pausaPedida = true;
                else if (fase == Fase.Sonando && sesion.Pausar()) { ultimaPosicion = sesion.Segundo(); fase = Fase.Pausa; }
            }
            Avisar();
        }

        public void Seguir()
        {
            lock (candado)
            {
                pausaPedida = false;
                if (sesion != null && fase == Fase.Pausa && sesion.Seguir()) fase = Fase.Sonando;
            }
            Avisar();
        }

        public void Parar()
        {
            Sesion vieja;
            lock (candado)
            {
                vieja = sesion;
                if (vieja != null && (fase == Fase.Sonando || fase == Fase.Pausa)) ultimaPosicion = vieja.Segundo();
                sesion = null;
                generacion++;
                pausaPedida = false;
                fase = Fase.Quieto;
            }
            vieja?.Cortar();
            Avisar();
        }

        /// <summary>Misma posición, otra velocidad: se reabre desde donde iba (y si estaba en pausa, queda en pausa).</summary>
        public void CambiarVelocidad(double v)
        {
            var f = Estado;
            if (f != Fase.Sonando && f != Fase.Pausa && f != Fase.Abriendo) { lock (candado) Velocidad = v; Avisar(); return; }
            double p = Posicion;
            Tocar(Archivo, p, v);
            if (f == Fase.Pausa) Pausar();
        }

        public void Dispose() => Parar();

        void Fallar(string porque)
        {
            lock (candado) { fase = Fase.Fallo; Problema = porque; }
            Avisar();
        }

        void Avisar()
        {
            try { Cambio?.Invoke(); }
            catch (Exception ex) { Debug.WriteLine("Reproductor.Cambio: " + ex.Message); }   // un suscriptor roto no puede tirar el alimentador
        }

        /// <summary>El alimentador cuenta cómo le fue. Si ya no es la sesión vigente (saltaste a otro punto), no pisa nada.</summary>
        void DeLaSesion(int gen, Fase nueva, string problema)
        {
            lock (candado)
            {
                if (gen != generacion || sesion == null) return;
                if (nueva == Fase.Sonando && pausaPedida && sesion.Pausar()) { pausaPedida = false; nueva = Fase.Pausa; }
                if (nueva == Fase.Terminado || nueva == Fase.Fallo) ultimaPosicion = sesion.Segundo();
                fase = nueva;
                if (problema != null) Problema = problema;
            }
            Avisar();
        }

        /// <summary>Las salidas que ve winmm (el nombre viene cortado a 31 caracteres).</summary>
        public static List<string> Salidas()
        {
            var res = new List<string>();
            int n = waveOutGetNumDevs();
            for (int i = 0; i < n; i++)
            {
                var caps = new WAVEOUTCAPS();
                if (waveOutGetDevCaps((UIntPtr)(uint)i, ref caps, Marshal.SizeOf(typeof(WAVEOUTCAPS))) == 0) res.Add(caps.szPname ?? "");
            }
            return res;
        }

        // ================================================================== una sesión: un ffmpeg + un waveOut

        sealed class Sesion
        {
            readonly Reproductor dueño;
            readonly int gen;
            readonly string archivo, dispositivo;
            public readonly double Desde, Velocidad;
            readonly object candadoOnda = new object();    // TODA llamada a waveOut pasa por acá
            IntPtr onda = IntPtr.Zero;
            readonly IntPtr[] cabeceras = new IntPtr[Bufferes];
            readonly IntPtr[] datos = new IntPtr[Bufferes];
            readonly bool[] enVuelo = new bool[Bufferes];
            long muestrasAlCerrar;                          // lo que llegó a sonar: la posición sigue valiendo después de cerrar
            volatile Process ffmpeg;
            volatile bool cortar;
            static readonly int TamCabecera = Marshal.SizeOf(typeof(WAVEHDR));
            static readonly int OffsetBanderas = Marshal.OffsetOf(typeof(WAVEHDR), "dwFlags").ToInt32();
            static readonly int OffsetLargo = Marshal.OffsetOf(typeof(WAVEHDR), "dwBufferLength").ToInt32();

            public Sesion(Reproductor r, int generacion, string archivo, double desde, double velocidad, string dispositivo)
            {
                dueño = r; gen = generacion; this.archivo = archivo; Desde = desde; Velocidad = velocidad; this.dispositivo = dispositivo;
            }

            public void Arrancar() => new Thread(Alimentar) { IsBackground = true, Name = "reproductor", Priority = ThreadPriority.AboveNormal }.Start();

            /// <summary>El segundo del archivo que salió por el parlante. O(1).</summary>
            public double Segundo() => Desde + MuestrasSonadas() / (double)Hz * Velocidad;

            long MuestrasSonadas()
            {
                lock (candadoOnda)
                {
                    if (onda == IntPtr.Zero) return muestrasAlCerrar;
                    var t = new MMTIME { wType = TIME_SAMPLES };
                    if (waveOutGetPosition(onda, ref t, Marshal.SizeOf(typeof(MMTIME))) != 0) return muestrasAlCerrar;
                    // el driver puede contestar en otra unidad si no sabe de muestras: se convierte
                    long m = t.wType == TIME_SAMPLES ? t.valor : t.wType == TIME_BYTES ? t.valor / BytesPorMuestra : t.wType == TIME_MS ? t.valor * Hz / 1000 : 0;
                    muestrasAlCerrar = m;
                    return m;
                }
            }

            public bool Pausar() { lock (candadoOnda) return onda != IntPtr.Zero && waveOutPause(onda) == 0; }
            public bool Seguir() { lock (candadoOnda) return onda != IntPtr.Zero && waveOutRestart(onda) == 0; }

            /// <summary>Pide cortar y vuelve enseguida: el alimentador cierra todo en SU hilo (nunca se libera memoria en vuelo).</summary>
            public void Cortar()
            {
                cortar = true;
                Matar(ffmpeg);
            }

            void Alimentar()
            {
                string problema = null;
                bool sono = false;
                try
                {
                    if (!AbrirFfmpeg(out problema) || cortar) return;
                    if (!AbrirOnda(out problema) || cortar) return;
                    var flujo = ffmpeg.StandardOutput.BaseStream;
                    var bloque = new byte[BytesPorBuffer];
                    bool fin = false;
                    while (!cortar)
                    {
                        bool algoEnVuelo = false;
                        for (int i = 0; i < Bufferes && !cortar; i++)
                        {
                            if (enVuelo[i] && !Hecho(i)) { algoEnVuelo = true; continue; }
                            if (enVuelo[i]) { Despreparar(i); enVuelo[i] = false; }
                            if (fin) continue;
                            int n = LeerLleno(flujo, bloque);
                            n -= n % BytesPorMuestra;
                            if (n <= 0) { fin = true; continue; }
                            Marshal.Copy(bloque, 0, datos[i], n);
                            if (!Escribir(i, n, out problema)) return;
                            enVuelo[i] = true;
                            algoEnVuelo = true;
                            if (!sono) { sono = true; dueño.DeLaSesion(gen, Fase.Sonando, null); }
                        }
                        if (fin && !algoEnVuelo)
                        {
                            if (!sono) problema = "ffmpeg no devolvió audio desde ese punto";
                            break;
                        }
                        Thread.Sleep(SondeoMs);
                    }
                }
                catch (IOException ex) { problema = "se cortó el audio: " + ex.Message; }
                catch (Win32Exception ex) { problema = "ffmpeg: " + ex.Message; }
                catch (InvalidOperationException ex) { problema = "ffmpeg: " + ex.Message; }
                finally
                {
                    Cerrar();
                    if (!cortar) dueño.DeLaSesion(gen, problema != null ? Fase.Fallo : Fase.Terminado, problema);
                }
            }

            bool AbrirFfmpeg(out string problema)
            {
                problema = null;
                var ci = CultureInfo.InvariantCulture;
                string tempo = Math.Abs(Velocidad - 1) > 0.001 ? $"-af atempo={Velocidad.ToString("0.###", ci)} " : "";
                var psi = new ProcessStartInfo("ffmpeg",
                    $"-hide_banner -nostdin -loglevel error -ss {Desde.ToString("0.###", ci)} -i \"{archivo}\" -vn -ac 1 -ar {Hz} {tempo}-f s16le pipe:1")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8,
                };
                try
                {
                    var p = Process.Start(psi);
                    if (p == null) { problema = "no pude arrancar ffmpeg"; return false; }
                    ffmpeg = p;
                    TrabajoHijos.Atar(p);
                    p.ErrorDataReceived += (o, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Debug.WriteLine("ffmpeg: " + e.Data); };
                    p.BeginErrorReadLine();                      // 🚨 el stderr se drena SIEMPRE: un caño lleno congela al hijo
                    return true;
                }
                catch (Win32Exception ex) { problema = "no encuentro ffmpeg (" + ex.Message + ")"; return false; }
            }

            bool AbrirOnda(out string problema)
            {
                problema = null;
                var formato = new WAVEFORMATEX
                {
                    wFormatTag = 1, nChannels = 1, nSamplesPerSec = Hz, wBitsPerSample = 16,
                    nBlockAlign = BytesPorMuestra, nAvgBytesPerSec = Hz * BytesPorMuestra, cbSize = 0,
                };
                int id = WAVE_MAPPER;
                if (!string.IsNullOrEmpty(dispositivo))
                {
                    var salidas = Salidas();
                    id = salidas.FindIndex(s => s.IndexOf(dispositivo, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (id < 0) { problema = $"no hay una salida de audio que se llame «{dispositivo}»"; return false; }
                }
                int r;
                lock (candadoOnda)
                {
                    r = waveOutOpen(out onda, id, ref formato, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
                    if (r != 0) { onda = IntPtr.Zero; problema = $"no pude abrir la salida de audio (winmm {r})"; return false; }
                    for (int i = 0; i < Bufferes; i++)
                    {
                        datos[i] = Marshal.AllocHGlobal(BytesPorBuffer);
                        cabeceras[i] = Marshal.AllocHGlobal(TamCabecera);
                        Marshal.StructureToPtr(new WAVEHDR { lpData = datos[i], dwBufferLength = BytesPorBuffer }, cabeceras[i], false);
                    }
                }
                return true;
            }

            bool Hecho(int i) => (Marshal.ReadInt32(cabeceras[i], OffsetBanderas) & WHDR_DONE) != 0;

            bool Escribir(int i, int bytes, out string problema)
            {
                problema = null;
                lock (candadoOnda)
                {
                    Marshal.WriteInt32(cabeceras[i], OffsetLargo, bytes);
                    Marshal.WriteInt32(cabeceras[i], OffsetBanderas, 0);
                    int r = waveOutPrepareHeader(onda, cabeceras[i], TamCabecera);
                    if (r == 0) r = waveOutWrite(onda, cabeceras[i], TamCabecera);
                    if (r != 0) { problema = $"la salida de audio rechazó el bloque (winmm {r})"; return false; }
                }
                return true;
            }

            void Despreparar(int i) { lock (candadoOnda) waveOutUnprepareHeader(onda, cabeceras[i], TamCabecera); }

            /// <summary>Lee hasta llenar el bloque o hasta el fin del caño (un bloque a medias recién al final).</summary>
            static int LeerLleno(Stream s, byte[] b)
            {
                int total = 0;
                while (total < b.Length)
                {
                    int n = s.Read(b, total, b.Length - total);
                    if (n <= 0) break;
                    total += n;
                }
                return total;
            }

            void Cerrar()
            {
                MuestrasSonadas();                                  // la última posición queda anotada antes de soltar el dispositivo
                lock (candadoOnda)
                {
                    if (onda != IntPtr.Zero)
                    {
                        waveOutReset(onda);                         // devuelve TODOS los bloques en vuelo, marcados como hechos
                        for (int i = 0; i < Bufferes; i++) if (cabeceras[i] != IntPtr.Zero) waveOutUnprepareHeader(onda, cabeceras[i], TamCabecera);
                        waveOutClose(onda);
                        onda = IntPtr.Zero;
                    }
                    for (int i = 0; i < Bufferes; i++)
                    {
                        if (cabeceras[i] != IntPtr.Zero) { Marshal.FreeHGlobal(cabeceras[i]); cabeceras[i] = IntPtr.Zero; }
                        if (datos[i] != IntPtr.Zero) { Marshal.FreeHGlobal(datos[i]); datos[i] = IntPtr.Zero; }
                    }
                }
                var p = ffmpeg;
                Matar(p);
                p?.Dispose();
                ffmpeg = null;
            }

            static void Matar(Process p)
            {
                if (p == null) return;
                try { if (!p.HasExited) p.Kill(); }
                catch (InvalidOperationException) { }                 // ya había salido: justo lo que se quería
                catch (Win32Exception ex) { Debug.WriteLine("reproductor: no pude cortar ffmpeg: " + ex.Message); }
            }
        }

        // ================================================================== winmm

        const int WAVE_MAPPER = -1;
        const int CALLBACK_NULL = 0;
        const int WHDR_DONE = 0x00000001;
        const int TIME_MS = 0x0001, TIME_SAMPLES = 0x0002, TIME_BYTES = 0x0004;

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        struct WAVEFORMATEX
        {
            public short wFormatTag, nChannels;
            public int nSamplesPerSec, nAvgBytesPerSec;
            public short nBlockAlign, wBitsPerSample, cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR
        {
            public IntPtr lpData;
            public int dwBufferLength, dwBytesRecorded;
            public IntPtr dwUser;
            public int dwFlags, dwLoops;
            public IntPtr lpNext, reserved;
        }

        /// <summary>🚨 MMTIME es un UINT seguido de una UNIÓN de 8 bytes alineada a 4: la unión arranca en el byte 4, no en el 8.</summary>
        [StructLayout(LayoutKind.Explicit, Size = 12)]
        struct MMTIME
        {
            [FieldOffset(0)] public int wType;
            [FieldOffset(4)] public uint valor;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WAVEOUTCAPS
        {
            public short wMid, wPid;
            public int vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public int dwFormats;
            public short wChannels, wReserved1;
            public int dwSupport;
        }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, int uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, int fdwOpen);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);
        [DllImport("winmm.dll")] static extern int waveOutPause(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutRestart(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutGetPosition(IntPtr hwo, ref MMTIME pmmt, int cbmmt);
        [DllImport("winmm.dll")] static extern int waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveOutGetDevCapsW")] static extern int waveOutGetDevCaps(UIntPtr uDeviceID, ref WAVEOUTCAPS pwoc, int cbwoc);
    }
}
