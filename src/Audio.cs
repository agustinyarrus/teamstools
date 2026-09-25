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
    // AUDIO: WAV honesto, energía por muestreo y procesos hijos que no se cuelgan ni quedan huérfanos.
    // Todo acá es puro (sin estado de la app) y se puede llamar desde cualquier hilo de fondo.
    // =====================================================================================================

    /// <summary>Lo que dice un WAV de sí mismo y lo que DE VERDAD tiene.</summary>
    internal sealed class InfoWav
    {
        public bool Valido;
        public int Formato, Canales, Hz, Bits, BloqueBytes, BytesPorSeg;
        public long OffsetDatos, DatosDeclarados, DatosReales, Largo;

        public double Segundos => BytesPorSeg > 0 ? DatosReales / (double)BytesPorSeg : 0;
        public double SegundosDeclarados => BytesPorSeg > 0 ? DatosDeclarados / (double)BytesPorSeg : 0;
        /// <summary>El encabezado declara otra cosa que lo que hay (más de un bloque de diferencia).</summary>
        public bool Miente => Valido && Math.Abs(DatosDeclarados - DatosReales) > Math.Max(1, BloqueBytes);
    }

    /// <summary>Energía de una pista medida por muestreo.</summary>
    internal sealed class Energia
    {
        public double PicoDb = -120, RmsDb = -120;
        /// <summary>Fracción de ventanas en las que «algo suena» (RMS por encima de <see cref="Wav.UmbralSuenaDb"/>).</summary>
        public double FraccionSonando;
        public int Ventanas;
        public bool Muda => PicoDb < Wav.UmbralMudaDb || FraccionSonando < 0.002;
        public override string ToString() => $"pico {PicoDb:0.0} dB · rms {RmsDb:0.0} dB · suena {FraccionSonando:P1} de {Ventanas} ventanas";
    }

    internal static class Wav
    {
        public const double UmbralMudaDb = -60;    // por debajo, silencio digital o ruido de fondo
        public const double UmbralSuenaDb = -45;   // RMS de una ventana en la que hay voz o sonido
        const int ChunkMax = 64 << 20;             // un chunk RIFF de metadatos más grande que esto es basura

        /// <summary>
        /// Recorre los chunks RIFF sin asumir 44 bytes fijos (WAVE_FORMAT_EXTENSIBLE, LIST…). O(cantidad de chunks).
        /// </summary>
        public static InfoWav Leer(string ruta)
        {
            var info = new InfoWav();
            try
            {
                using (var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var br = new BinaryReader(fs))
                {
                    info.Largo = fs.Length;
                    if (fs.Length < 12 || Ascii(br.ReadBytes(4)) != "RIFF") return info;
                    br.ReadUInt32();
                    if (Ascii(br.ReadBytes(4)) != "WAVE") return info;
                    bool fmt = false;
                    while (fs.Position + 8 <= fs.Length)
                    {
                        string id = Ascii(br.ReadBytes(4));
                        uint tam = br.ReadUInt32();
                        long cuerpo = fs.Position;
                        if (id == "fmt ")
                        {
                            info.Formato = br.ReadUInt16();
                            info.Canales = br.ReadUInt16();
                            info.Hz = (int)br.ReadUInt32();
                            info.BytesPorSeg = (int)br.ReadUInt32();
                            info.BloqueBytes = br.ReadUInt16();
                            info.Bits = br.ReadUInt16();
                            if (info.Formato == 0xFFFE && tam >= 40) { fs.Position = cuerpo + 24; info.Formato = br.ReadUInt16(); }
                            fmt = true;
                        }
                        else if (id == "data")
                        {
                            info.OffsetDatos = cuerpo;
                            info.DatosDeclarados = tam;
                            long reales = fs.Length - cuerpo;
                            if (info.BloqueBytes > 0) reales -= reales % info.BloqueBytes;
                            info.DatosReales = Math.Max(0, reales);
                            info.Valido = fmt && info.BytesPorSeg > 0;
                            return info;
                        }
                        if (tam > ChunkMax) return info;
                        fs.Position = cuerpo + tam + (tam & 1);
                    }
                }
            }
            catch { }
            return info;
        }

        /// <summary>
        /// Deja el encabezado diciendo la verdad: RIFF = largo−8 y data = lo que hay de verdad después del chunk.
        /// 🚨 El 23-sep-2026 un WAV de 26 min declaraba 21 s (loopcap trabado escribiendo a un caño lleno):
        ///    ffmpeg obedeció al encabezado, whisper transcribió 21 s y el pipeline borró la reunión.
        ///    Se repara SIEMPRE antes de procesar, aunque parezca sano. Cuesta leer 44 bytes.
        /// </summary>
        public static bool Reparar(string ruta, out InfoWav info)
        {
            info = Leer(ruta);
            if (!info.Valido || !info.Miente) return false;
            try
            {
                using (var fs = new FileStream(ruta, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                using (var bw = new BinaryWriter(fs))
                {
                    long datos = Math.Min(info.DatosReales, uint.MaxValue - 64);
                    fs.Position = 4; bw.Write((uint)Math.Min(fs.Length - 8, uint.MaxValue));
                    fs.Position = info.OffsetDatos - 4; bw.Write((uint)datos);
                }
                info = Leer(ruta);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Energía por muestreo: una ventana de 100 ms cada <paramref name="pasoSeg"/> segundos. Para una hora
        /// de audio son 1 800 lecturas chicas en vez de 345 MB: O(duración / paso) y en milisegundos.
        /// </summary>
        public static Energia Medir(string ruta, double pasoSeg = 2.0)
        {
            var e = new Energia();
            var info = Leer(ruta);
            if (!info.Valido || info.DatosReales <= 0 || (info.Bits != 16 && info.Bits != 32)) return e;
            int bloque = info.BloqueBytes;
            int ventana = Math.Max(bloque, (int)(info.BytesPorSeg * 0.1) / bloque * bloque);
            long paso = Math.Max(ventana, (long)(info.BytesPorSeg * pasoSeg) / bloque * bloque);
            var buf = new byte[ventana];
            double sumaCuad = 0; long muestrasTot = 0; double pico = 0; int sonando = 0;
            try
            {
                using (var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    for (long off = 0; off + ventana <= info.DatosReales; off += paso)
                    {
                        fs.Position = info.OffsetDatos + off;
                        int n = fs.Read(buf, 0, ventana);
                        if (n <= 0) break;
                        double sc = 0; int m = 0;
                        if (info.Bits == 16)
                            for (int i = 0; i + 1 < n; i += 2) { double v = (short)(buf[i] | buf[i + 1] << 8) / 32768.0; sc += v * v; m++; if (Math.Abs(v) > pico) pico = Math.Abs(v); }
                        else if (info.Formato == 3)
                            for (int i = 0; i + 3 < n; i += 4) { double v = BitConverter.ToSingle(buf, i); sc += v * v; m++; if (Math.Abs(v) > pico) pico = Math.Abs(v); }
                        else
                            for (int i = 0; i + 3 < n; i += 4) { double v = BitConverter.ToInt32(buf, i) / 2147483648.0; sc += v * v; m++; if (Math.Abs(v) > pico) pico = Math.Abs(v); }
                        if (m == 0) continue;
                        e.Ventanas++;
                        sumaCuad += sc; muestrasTot += m;
                        if (Db(Math.Sqrt(sc / m)) > UmbralSuenaDb) sonando++;
                    }
                }
            }
            catch { }
            e.PicoDb = Db(pico);
            e.RmsDb = muestrasTot > 0 ? Db(Math.Sqrt(sumaCuad / muestrasTot)) : -120;
            e.FraccionSonando = e.Ventanas > 0 ? sonando / (double)e.Ventanas : 0;
            return e;
        }

        /// <summary>
        /// El instante más callado entre <paramref name="desde"/> y <paramref name="hasta"/> (ventanas de 250 ms, mínimo
        /// de energía): donde cortar el audio en trozos sin partir una palabra. Lee solo ese tramo: O(hasta − desde).
        /// </summary>
        public static double PuntoMasCallado(string ruta, double desde, double hasta)
        {
            double medio = (desde + hasta) / 2;
            var info = Leer(ruta);
            if (!info.Valido || info.Bits != 16 || hasta <= desde) return medio;
            int bloque = Math.Max(1, info.BloqueBytes);
            int ventana = Math.Max(bloque, (int)(info.BytesPorSeg * 0.25) / bloque * bloque);
            long ini = (long)(desde * info.BytesPorSeg) / bloque * bloque;
            long fin = Math.Min(info.DatosReales, (long)(hasta * info.BytesPorSeg) / bloque * bloque);
            var buf = new byte[ventana];
            double mejor = double.MaxValue, mejorSeg = medio;
            try
            {
                using (var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                {
                    fs.Position = info.OffsetDatos + ini;
                    for (long off = ini; off + ventana <= fin; off += ventana)
                    {
                        int n = fs.Read(buf, 0, ventana);
                        if (n < ventana) break;
                        double sc = 0;
                        for (int i = 0; i + 1 < n; i += 2) { double v = (short)(buf[i] | buf[i + 1] << 8); sc += v * v; }
                        if (sc < mejor) { mejor = sc; mejorSeg = (off + ventana / 2.0) / info.BytesPorSeg; }
                    }
                }
            }
            catch { return medio; }
            return mejorSeg;
        }

        public static double Db(double lineal) => lineal <= 1e-6 ? -120 : Math.Round(20 * Math.Log10(lineal), 1);

        static string Ascii(byte[] b) => b.Length == 4 ? Encoding.ASCII.GetString(b) : "";
    }

    // -----------------------------------------------------------------------------------------------------
    // Procesos hijos
    // -----------------------------------------------------------------------------------------------------

    internal sealed class ResultadoProceso
    {
        public int Codigo = -1;
        public bool Termino, Cancelado, NoArranco;
        public long Ms;
        public string Cola = "";      // los últimos renglones, para explicar un fallo
        public bool Ok => Termino && Codigo == 0;
        public override string ToString() => NoArranco ? "no arrancó · " + Cola : Cancelado ? "cancelado" : !Termino ? "se pasó del tiempo" : $"código {Codigo}" + (Codigo != 0 && Cola.Length > 0 ? " · " + Cola : "");
    }

    internal static class Procesos
    {
        /// <summary>
        /// Corre un proceso SIN ventana y drena los dos caños SIEMPRE: un caño redirigido que nadie lee congela
        /// al hijo en cuanto se llena (le pasó a loopcap el 23-sep). Cada renglón (\n o \r) va a <paramref name="linea"/>.
        /// El hijo entra al <see cref="TrabajoHijos"/>: si la app muere, muere con ella y no queda huérfano.
        /// </summary>
        public static ResultadoProceso Correr(string exe, string args, int timeoutMs, Action<string> linea = null,
            Action<Process> vigilar = null, CancellationToken cancel = default(CancellationToken), bool atarALaApp = true)
        {
            var r = new ResultadoProceso();
            var cola = new Queue<string>();
            var reloj = Stopwatch.StartNew();
            Action<string> anotar = s =>
            {
                if (string.IsNullOrWhiteSpace(s)) return;
                lock (cola) { cola.Enqueue(s.Trim()); while (cola.Count > 6) cola.Dequeue(); }
                try { linea?.Invoke(s); } catch { }
            };
            Process p = null;
            try
            {
                p = new Process
                {
                    StartInfo = new ProcessStartInfo(exe, args)
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                    },
                    EnableRaisingEvents = true,
                };
                p.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                p.StartInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
                p.OutputDataReceived += (o, e) => anotar(e.Data);
                p.ErrorDataReceived += (o, e) => anotar(e.Data);
                p.Start();
                if (atarALaApp) TrabajoHijos.Atar(p);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                var limite = timeoutMs > 0 ? reloj.ElapsedMilliseconds + timeoutMs : long.MaxValue;
                while (!p.WaitForExit(500))
                {
                    try { vigilar?.Invoke(p); } catch { }
                    if (cancel.IsCancellationRequested) { r.Cancelado = true; Matar(p); break; }
                    if (reloj.ElapsedMilliseconds > limite) { Matar(p); break; }
                }
                if (p.HasExited)
                {
                    p.WaitForExit();                  // sin argumento: espera también a los lectores asíncronos
                    r.Termino = !r.Cancelado && reloj.ElapsedMilliseconds <= limite;
                    r.Codigo = p.ExitCode;
                }
            }
            catch (Win32Exception ex) { r.NoArranco = true; lock (cola) cola.Enqueue(ex.Message); }
            catch (Exception ex) { lock (cola) cola.Enqueue(ex.Message); }
            finally { try { p?.Dispose(); } catch { } }
            r.Ms = reloj.ElapsedMilliseconds;
            lock (cola) r.Cola = string.Join(" | ", cola);
            return r;
        }

        public static void Matar(Process p)
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
            try { p.WaitForExit(3000); } catch { }
        }

        /// <summary>Duración real de un archivo de audio según ffprobe. −1 si no se pudo medir.</summary>
        public static double Duracion(string ruta)
        {
            double seg = -1;
            var r = Correr("ffprobe", $"-v error -show_entries format=duration -of default=nw=1:nk=1 \"{ruta}\"", 60000,
                l => { double d; if (double.TryParse(l.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) seg = d; });
            return r.Ok ? seg : -1;
        }
    }

    /// <summary>
    /// Job Object con KILL_ON_JOB_CLOSE: los hijos pesados (ffmpeg, whisper) mueren con la app aunque la app se
    /// cuelgue o la maten. Sin esto, cerrar la app a mitad de una transcripción dejaba un python comiéndose la
    /// CPU hasta terminar, y al reabrir arrancaba OTRO con la misma reunión.
    /// 🚨 loopcap NO va acá: corta solo y limpio con -parent (cerrar su WAV bien vale más que matarlo ya).
    /// </summary>
    internal static class TrabajoHijos
    {
        static IntPtr job = IntPtr.Zero;
        static readonly object candado = new object();

        public static void Atar(Process p)
        {
            try
            {
                lock (candado)
                {
                    if (job == IntPtr.Zero) job = Crear();
                    if (job != IntPtr.Zero) AssignProcessToJobObject(job, p.Handle);
                }
            }
            catch { }
        }

        static IntPtr Crear()
        {
            IntPtr h = CreateJobObject(IntPtr.Zero, null);
            if (h == IntPtr.Zero) return h;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            int largo = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr ptr = Marshal.AllocHGlobal(largo);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(h, 9 /* JobObjectExtendedLimitInformation */, ptr, (uint)largo)) { CloseHandle(h); return IntPtr.Zero; }
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return h;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong R, W, O, RB, WB, OB; }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string nombre);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int clase, IntPtr info, uint largo);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proceso);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    }
}
