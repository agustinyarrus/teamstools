using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace TeamsTools
{
    // =====================================================================================================
    // GRABADOR v2 — «que no se pierda nunca una reunión»
    //
    // 🚨 Lo que pasó el 23-sep-2026 (y en silencio el 21-sep): loopcap se trabó a los 21 s escribiendo a un
    //    caño que nadie leía, el WAV quedó declarando 21 s, ffmpeg le creyó, whisper transcribió 21 s y el
    //    pipeline BORRÓ 26 minutos de reunión porque «el texto tenía más de 20 palabras». Encima el corte se
    //    hacía en el hilo de la UI y la congelaba 15 s. Este archivo es la respuesta a cada eslabón:
    //
    //    · CONTROL (un actor, hilo propio): arranca, vigila y corta loopcap. La UI solo encola; nunca espera.
    //      Vigila la salud de loopcap cada segundo (estado vivo, proceso vivo) y si muere o se traba, retoma
    //      en un TRAMO nuevo sin perder la reunión. Gracia corta si tu presencia confirma que saliste.
    //    · PIPELINE (otro hilo, una grabación por vez), con verificación en cada paso:
    //        grabada ─▶ comprimiendo ─▶ transcribiendo ─▶ transcripta ─▶ resumiendo ─▶ lista
    //        1. repara encabezados, mide la energía de cada pista y elige las que suenan (mezcla si hay más
    //           de una, concatena tramos),
    //        2. UNA pasada de ffmpeg saca el ARCHIVO (.opus, ~14 MB/h, se guarda siempre) y el 16 kHz para whisper,
    //        3. verifica con ffprobe que los dos duren lo que tiene que durar,
    //        4. whisper (pausado mientras estás en una llamada) y verificación de que leyó TODO el audio,
    //        5. recién ahí, y solo si lo pediste, se borran los WAV grandes. El .opus no se borra nunca.
    //    · Nada se borra de verdad a la primera: descartes y borrados van a una papelera de 7 días.
    //
    // v3 (23-sep-2026, tarde) — «cambié de auriculares en la llamada y no se grabó nada»:
    //    · loopcap 3 sigue los dispositivos en caliente (-all), graba tu MICRÓFONO (-mic) mientras la llamada
    //      lo usa y publica el pulso 20 veces por segundo (-levels) → PulsoAudio → la banda en vivo lo pinta;
    //    · cada dispositivo que se suma, vuelve o se va queda en la bitácora de la grabación;
    //    · lo que tu micrófono capta mientras Teams te tiene en SILENCIO no va a la mezcla: el vigía del
    //      micrófono anota cada cambio con su hora (mic-teams.jsonl) y al archivar esos tramos van en cero.
    // =====================================================================================================

    /// <summary>Estados de una grabación. Strings (no enum) porque viajan al índice JSON y los viejos siguen valiendo.</summary>
    internal static class EstadoGrab
    {
        public const string Grabando = "grabando";
        public const string Cerrando = "cerrando";
        public const string Grabada = "grabada";              // audio a salvo en WAV, esperando el pipeline
        public const string Comprimiendo = "comprimiendo";
        public const string Transcribiendo = "transcribiendo";
        public const string Transcripta = "transcripta";      // falta el resumen
        public const string Resumiendo = "resumiendo";
        public const string Lista = "lista";
        public const string Fallo = "falló";
        public const string Descartada = "descartada";

        public static bool EnCurso(string e) => e == Grabando || e == Cerrando;
        public static bool EnCola(string e) => e == Grabada || e == Comprimiendo || e == Transcribiendo || e == Transcripta || e == Resumiendo;
    }

    /// <summary>Una llamada grabada y todo lo que se hizo con ella. Se persiste en `grabaciones\indice.json`.</summary>
    internal sealed class Grabacion
    {
        public string Id = "", Reunion = "", Wav = "", Texto = "", Resumen = "", Estado = EstadoGrab.Grabando, Detalle = "";
        public DateTime Desde, Hasta;
        public long BytesWav;
        public double SegundosAudio;
        public long MsTranscripcion;
        public bool AudioBorrado;          // los WAV grandes ya no están (el .opus, si hay, sí)
        public int Palabras;
        // --- v2
        public int Version;
        public string Dir = "";
        public string Archivo = "";        // audio.opus: la copia comprimida que se guarda SIEMPRE
        public long BytesArchivo;
        public double SegundosTranscriptos;
        public double SegundosPerdidos;    // lo que loopcap no pudo escribir (disco lleno…), rellenado con silencio
        public int Tramos;
        public string Pistas = "";         // qué salida(s) llevaron el audio
        public int MaxOtros;               // cuántos otros llegó a mostrar Teams (tope para separar las voces)
        public string Etapa = "";          // lo que está pasando ahora, en palabras
        public double Progreso = -1;       // 0..1 de la etapa; −1 = no se sabe
        public bool EnPausa;               // whisper congelado porque estás en una llamada
        public List<string> Bitacora = new List<string>();

        [ScriptIgnore] public TimeSpan Dura => (Hasta.Year < 2000 ? DateTime.Now : Hasta) - Desde;
        [ScriptIgnore] public string Carpeta => Dir.Length > 0 ? Dir : (Path.GetDirectoryName(Wav ?? "") ?? "");
        [ScriptIgnore] public bool TieneArchivo => BytesArchivo > 0 && Archivo.Length > 0;

        public Grabacion Clonar()
        {
            var c = (Grabacion)MemberwiseClone();
            c.Bitacora = new List<string>(Bitacora ?? new List<string>());
            return c;
        }
    }

    /// <summary>Lo que se ve de la grabación en curso, un par de veces por segundo. No se persiste.</summary>
    internal sealed class EnVivo
    {
        public string Id = "", Reunion = "";
        public DateTime Desde;
        public double Segundos;
        public double NivelDb = -120;        // pico del último segundo en la pista más fuerte
        public double PicoTotalDb = -120;
        public double MB;
        public long DiscoLibreMB = -1;
        public string Pista = "";            // la que suena
        public string Problema = "";         // error de escritura, pista pausada, loopcap retomado…
        public int Tramo = 1;
        public double SegundosPerdidos;
        public double? CierraEn;             // segundos para cortar si la reunión no vuelve; null = no está por cortar
        public DateTime Actualizado = DateTime.MinValue;
        public bool Sano => Problema.Length == 0 && (DateTime.Now - Actualizado).TotalSeconds < 6;
    }

    /// <summary>La foto que lee la UI. Inmutable: se reemplaza entera, nunca se modifica.</summary>
    internal sealed class FotoGrabador
    {
        public Grabacion[] Todas = new Grabacion[0];   // clones, las más nuevas primero
        public EnVivo Vivo;                             // null si no está grabando
        public string Cerrando = "";                    // reunión que se está cerrando (STOP enviado)
        public int EnCola;
        public bool Activo;
        public string Problema = "";
        public DateTime Hora = DateTime.Now;
    }

    /// <summary>
    /// Los motores de transcripción. Todos locales. Medidos el 23-sep contra una daily rioplatense de verdad conocida
    /// (557 palabras, 4 voces, 3 condiciones de audio; tools\transcribe\banco_verdad.py):
    ///   Cohere Transcribe 2B · WER 3,0 % con audio de Teams, 6,8 % en condición dura · el que MENOS se come
    ///   Parakeet TDT 0.6B    · WER 4,1 % / 11,1 % · ~2,5 veces más rápido que Cohere
    ///   whisper large-v3     · el de antes: en «máximo», una hora de reunión eran ~8 h de CPU (y el tope la cortaba)
    /// </summary>
    internal sealed class MotorAsr
    {
        public string Id = "", Nombre = "", Detalle = "", Args = "";
        public double FactorTope;                  // tope de CPU = duración del trozo × esto (con un piso)

        public static readonly MotorAsr[] Todos =
        {
            new MotorAsr { Id = "parakeet", Nombre = "Parakeet", Detalle = "rápido · 4 % de error", Args = "--motor parakeet+b1.5", FactorTope = 3 },
            new MotorAsr { Id = "cohere", Nombre = "Cohere", Detalle = "preciso · 3 % de error", Args = "--motor cohere", FactorTope = 4 },
            new MotorAsr { Id = "whisper", Nombre = "whisper", Detalle = "el de antes · lento" },
        };

        public static MotorAsr De(string id) => Todos.FirstOrDefault(m => m.Id == id) ?? Todos[1];
        public bool EsWhisper => Id == "whisper";
    }

    /// <summary>Config del pipeline de llamadas, en `datos\grabador.json`.</summary>
    internal sealed class ConfigGrabador
    {
        public bool Activo;
        /// <summary>Borra los WAV grandes SOLO cuando el .opus y la transcripción están verificados. El .opus queda.</summary>
        public bool BorrarAudio = true;
        public bool Resumir = true;
        public bool SoloConGente = true;
        public int MinimoSegundos = 60;
        /// <summary>Gracia larga: la reunión dejó de verse pero tu presencia no confirma que saliste (la lectura parpadea).</summary>
        public int SegundosParaCortar = 90;
        /// <summary>Gracia corta: la ventana no está Y tu presencia dice Disponible/Ausente. Saliste de verdad.</summary>
        public int GraciaCortaSegundos = 8;
        /// <summary>Whisper se congela mientras estás en una llamada: en esta máquina de 15 W le robaría CPU a Teams.</summary>
        public bool PausarEnLlamada = true;
        public int KbpsArchivo = 32;
        /// <summary>Largo de cada trozo de whisper: el punto de guardado de una transcripción larga.</summary>
        public int TrozoSegundos = 600;
        public string Calidad = "rapido";             // solo para whisper (beam y precisión)
        /// <summary>Con qué se transcribe: cohere (por defecto, el más preciso), parakeet (rápido) o whisper (el de antes).</summary>
        public string Motor = "cohere";
        /// <summary>Separar quién habla («Persona 1: …», «Yo: …»). Solo con Cohere/Parakeet.</summary>
        public bool Hablantes = true;
        /// <summary>Migraciones de una sola vez (1 = las que falló whisper volvieron a la cola con el motor nuevo).</summary>
        public int MigracionMotor;
        public string Idioma = "es";
        public int MaxGrabacionesGuardadas = 60;
        public int DiasPapelera = 7;
        /// <summary>Rutas de las herramientas de afuera. Vacío = tools\ al lado del exe (tools\loopcap\loopcap.exe, tools\transcribe\…).</summary>
        public string Loopcap = "", Transcriptor = "", TranscriptorSherpa = "", ScriptHablantes = "";

        [ScriptIgnore] public string Ruta = "";

        public static ConfigGrabador Cargar(string ruta)
        {
            try
            {
                if (File.Exists(ruta))
                {
                    var c = new JavaScriptSerializer().Deserialize<ConfigGrabador>(File.ReadAllText(ruta, Encoding.UTF8));
                    if (c != null) { c.Ruta = ruta; Grabador.UsarRutasDe(c); return c; }
                }
            }
            catch { }
            var nueva = new ConfigGrabador { Ruta = ruta };
            Grabador.UsarRutasDe(nueva);
            nueva.Guardar();
            return nueva;
        }

        public void Guardar()
        {
            if (!string.IsNullOrEmpty(Ruta)) Disco.Escribir(Ruta, new JavaScriptSerializer().Serialize(this));
        }
    }

    internal sealed class Grabador
    {
        // --- las herramientas de afuera: lo que diga grabador.json o, si no, tools\ al lado del exe (ver Herramientas)
        static ConfigGrabador rutas;
        static string transcriptorForzado = "";
        /// <summary>La config cargada: de ahí salen las rutas de loopcap y de los scripts.</summary>
        internal static void UsarRutasDe(ConfigGrabador c) => rutas = c;
        public static string Loopcap => Herramientas.Resolver(rutas?.Loopcap, @"loopcap\loopcap.exe");
        /// <summary>whisper (transcribe.py). Las pruebas pueden forzar otro transcriptor (whisper-falso.py).</summary>
        public static string Transcriptor
        {
            get => transcriptorForzado.Length > 0 ? transcriptorForzado : Herramientas.Resolver(rutas?.Transcriptor, @"transcribe\transcribe.py");
            set => transcriptorForzado = value ?? "";
        }
        public static string TranscriptorSherpa => Herramientas.Resolver(rutas?.TranscriptorSherpa, @"transcribe\transcribe_sherpa.py");   // Cohere / Parakeet

        const string PorPresencia = "reunión (detectada por tu presencia)";
        const string Papelera = "_papelera";
        const string ArchivoMicTeams = "mic-teams.jsonl";
        /// <summary>El vigía del micrófono lee cada ~300 ms: el mute se corre esto hacia atrás (la mitad del período más la lectura).</summary>
        const double DemoraLecturaMicS = 0.2;
        const int SegundosSinEstadoParaTrabado = 25;   // loopcap escribe su estado cada segundo
        const int ReiniciosMaximos = 8;
        const long MbMinimosParaGrabar = 300;
        const long MbAvisoDisco = 2048;
        const int TopeHorasLoopcap = 6;                 // red de seguridad: ninguna reunión dura más
        static readonly Regex ReTramo = new Regex(@"^audio(?:\.t(?<n>\d+))?(?<mic>\.mic)?(?:__(?<dev>.+))?\.wav$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ReProgresoWhisper = new Regex(@"\[progreso\]\s+(?<p>[\d.]+)%", RegexOptions.Compiled);
        static readonly Regex ReDuracionWhisper = new Regex(@"duracion=(?<d>[\d.]+)s", RegexOptions.Compiled);
        static readonly Regex ReTiempoFfmpeg = new Regex(@"^out_time_us=(?<us>\d+)", RegexOptions.Compiled);

        readonly Logger log;
        readonly ConfigGrabador cfg;
        readonly string carpeta;
        readonly object candado = new object();                 // protege `lista` y los campos de cada Grabacion
        readonly List<Grabacion> lista = new List<Grabacion>();
        readonly BlockingCollection<Action> buzon = new BlockingCollection<Action>();
        readonly AutoResetEvent hayTrabajo = new AutoResetEvent(false);
        Thread hiloControl, hiloCola;
        volatile bool parar;
        FotoGrabador foto = new FotoGrabador();
        DateTime ultimaPublicacion = DateTime.MinValue;

        // --- estado del CONTROL: solo lo toca hiloControl
        Grabacion actual;
        Tramo tramo;
        EnVivo vivo;
        DateTime sinLlamadaDesde = DateTime.MinValue;
        int graciaActual;
        int reinicios;
        DateTime proximoTramo = DateTime.MaxValue;
        string cerrando = "";
        // --- compartido con el pipeline
        volatile bool enLlamada;
        volatile string stopVivo = "";            // para que Detener() corte loopcap sin pasar por el actor
        // --- pulso en vivo y micrófono de Teams (v3)
        PulsoAudio pulso;                         // null si no está grabando (se lee con Volatile)
        volatile string dirGrabando = "";         // carpeta de la grabación en curso: ahí va el registro de mute
        bool? micTeams;                           // lo último que dijo Teams de tu micrófono (lo toca el vigía del mic)

        sealed class Tramo
        {
            public int N;
            public string Wav = "", Stop = "", Estado = "";
            public Process Proceso;
            public DateTime Inicio;
        }

        public Grabador(Config c, Logger l, string carpetaDatos)
        {
            log = l;
            carpeta = Path.Combine(carpetaDatos, "grabaciones");
            cfg = ConfigGrabador.Cargar(Path.Combine(carpetaDatos, "grabador.json"));
            try { Directory.CreateDirectory(carpeta); } catch { }
            CargarIndice();
            ImportarHuerfanas();
            MigrarMotor();
            Publicar(forzar: true);
        }

        /// <summary>
        /// Una sola vez: las grabaciones que falló whisper (tope de CPU, trozo sin terminar) y que tienen su .opus
        /// verificado vuelven a la cola con el motor nuevo. Sus trozos viejos se borran (no se mezclan motores).
        /// </summary>
        void MigrarMotor()
        {
            if (cfg.MigracionMotor >= 1) return;
            var motor = MotorAsr.De(cfg.Motor);
            int n = 0;
            lock (candado)
                foreach (var g in lista.Where(x => x.Estado == EstadoGrab.Fallo && x.Texto.Length == 0 && x.TieneArchivo && File.Exists(x.Archivo)))
                {
                    try { Directory.Delete(Path.Combine(g.Carpeta, "trozos"), true); } catch { }
                    g.Estado = EstadoGrab.Transcribiendo; g.Etapa = "en cola"; g.Progreso = -1;
                    g.Detalle = "reintento con " + motor.Nombre;
                    g.Bitacora.Add(DateTime.Now.ToString("HH:mm:ss") + "  reintento automático con " + motor.Nombre + " (whisper no la pudo transcribir)");
                    n++;
                }
            cfg.MigracionMotor = 1;
            cfg.Guardar();
            if (n > 0) { log.Info($"Grabador: {n} grabación/es que falló whisper vuelven a la cola con {motor.Nombre}"); GuardarIndice(); }
        }

        // ================================================================== API para la UI (jamás bloquea)

        public ConfigGrabador Cfg => cfg;
        public FotoGrabador Foto => Volatile.Read(ref foto);
        /// <summary>Para las pruebas de consola: el pipeline se detiene justo antes de whisper (no gasta CPU).</summary>
        public bool PararAntesDeWhisper { get; set; }
        public bool Grabando => Foto.Vivo != null;

        /// <summary>El pulso de la grabación en curso (niveles 20 por segundo y dispositivos); null si no graba.</summary>
        public PulsoAudio Pulso => Volatile.Read(ref pulso);

        /// <summary>
        /// Lo que dice Teams de tu micrófono (lo llama el vigía del micrófono unas 3 veces por segundo; true = en
        /// silencio). Cada CAMBIO queda con su hora en mic-teams.jsonl: al archivar, lo que el micrófono captó
        /// mientras estabas en silencio no se mezcla (no salió a la llamada, no va a la grabación). O(1).
        /// </summary>
        public void MicDeTeams(bool? silenciado)
        {
            lock (candadoMic)                     // lo llaman el vigía del micrófono y, de respaldo, la lectura de tu ficha
            {
                if (silenciado == micTeams) return;
                micTeams = silenciado;
                var pu = Pulso;
                if (pu != null) { pu.MicSilenciado = silenciado; pu.MicSilenciadoDesde = DateTime.Now; }
                AnotarMicTeams(dirGrabando, silenciado);
            }
        }

        readonly object candadoMic = new object();

        static void AnotarMicTeams(string dir, bool? silenciado)
        {
            if (string.IsNullOrEmpty(dir) || !silenciado.HasValue) return;
            Disco.Agregar(Path.Combine(dir, ArchivoMicTeams), "{\"t\":\"" + DateTime.Now.ToString("o", CultureInfo.InvariantCulture) +
                                                                "\",\"silenciado\":" + (silenciado.Value ? "true" : "false") + "}\n");
        }

        public event Action Cambio;

        public void Iniciar()
        {
            if (hiloControl != null) return;
            hiloControl = new Thread(BucleControl) { IsBackground = true, Name = "grabador-control" };
            // Normal a propósito: el trabajo pesado es de ffmpeg/whisper (procesos hijos a BelowNormal); este hilo solo espera y
            // toma el candado del grabador un instante — bajarle la prioridad solo invita a una inversión de prioridad
            hiloCola = new Thread(BucleCola) { IsBackground = true, Name = "grabador-cola" };
            hiloControl.Start();
            hiloCola.Start();
            ThreadPool.QueueUserWorkItem(_ => { try { PurgarPapelera(); CortarLoopcapsHuerfanos(); } catch { } });
        }

        /// <summary>Cada lectura del vigía. Se encola y vuelve al instante.</summary>
        public void Mirar(Lectura L, bool huboGente, bool porPresencia, bool presenciaLibre)
            => Encolar(() => MirarEnControl(L, huboGente, porPresencia, presenciaLibre));

        public void CortarAhora(string porque) => Encolar(() => Cortar(porque));

        public void Reintentar(string id) => Encolar(() => ReintentarEnControl(id));

        public void Borrar(string id) => Encolar(() => BorrarEnControl(id));

        public void GuardarConfig() { cfg.Guardar(); Encolar(() => { if (!cfg.Activo && actual != null) Cortar("apagaste la grabación"); Publicar(true); }); }

        /// <summary>La app se cierra: STOP directo (1 ms, sin actor) y el índice marcado para retomar al volver.</summary>
        public void Detener()
        {
            parar = true;
            string s = stopVivo;
            if (s.Length > 0) { try { File.WriteAllText(s, ""); } catch { } }
            lock (candado)
                foreach (var g in lista.Where(x => EstadoGrab.EnCurso(x.Estado)))
                { g.Estado = EstadoGrab.Cerrando; g.Detalle = "la app se cerró · lo retomo al volver"; Anotar(g, "la app se cerró con la grabación en curso"); }
            GuardarIndice();
            hayTrabajo.Set();
        }

        void Encolar(Action a) { if (!parar) try { buzon.Add(a); } catch { } }

        // ================================================================== CONTROL

        void BucleControl()
        {
            var reloj = Stopwatch.StartNew();
            while (!parar)
            {
                try
                {
                    if (buzon.TryTake(out var a, 250)) Seguro(a);
                    if (reloj.ElapsedMilliseconds >= 1000) { reloj.Restart(); Seguro(Vigilar); }
                }
                catch (Exception ex) { log.Error("Grabador (control): " + ex.Message); }
            }
        }

        void Seguro(Action a)
        {
            try { a(); }
            catch (Exception ex) { log.Error("Grabador: " + ex.GetType().Name + ": " + ex.Message); }
        }

        void MirarEnControl(Lectura L, bool huboGente, bool porPresencia, bool presenciaLibre)
        {
            if (!cfg.Activo)
            {
                if (actual != null) Cortar("apagaste la grabación");
                return;
            }
            bool ventana = L != null && L.HayLlamada;
            bool llamada = ventana || porPresencia;
            enLlamada = llamada;

            if (llamada)
            {
                if (sinLlamadaDesde != DateTime.MinValue && actual != null)
                    Anotar(actual, $"la reunión volvió a verse tras {(int)(DateTime.Now - sinLlamadaDesde).TotalSeconds} s: sigo en la misma grabación");
                sinLlamadaDesde = DateTime.MinValue;
                if (vivo != null) vivo.CierraEn = null;
                if (actual != null && ventana)
                {
                    int otros = Math.Max(L.Otros, (L.RosterTotal ?? 0) - 1);
                    if (otros > actual.MaxOtros) lock (candado) actual.MaxOtros = otros;
                }
                if (actual == null)
                {
                    // con ventana respeto SoloConGente; por presencia sola arranco igual: una call es una call
                    bool permite = ventana ? (!cfg.SoloConGente || huboGente || L.Otros > 0) : true;
                    if (permite) Arrancar(ventana && !string.IsNullOrWhiteSpace(L.Reunion) ? L.Reunion : PorPresencia);
                }
                else if (ventana && !string.IsNullOrWhiteSpace(L.Reunion) && actual.Reunion == PorPresencia)
                {
                    lock (candado) actual.Reunion = L.Reunion;
                    if (vivo != null) vivo.Reunion = L.Reunion;
                    Anotar(actual, "ahora veo la ventana: se llama «" + L.Reunion + "»");
                    GuardarIndice();
                }
                return;
            }

            if (actual == null) return;
            // 🚨 NO cortar al primer «no veo la llamada»: la lectura por UIA parpadea (18-sep: una reunión de 58
            //    minutos quedó en pedacitos de 14 s). PERO si tu presencia nativa ya dice Disponible/Ausente, saliste
            //    de verdad: gracia corta. Antes esperaba 90 s + 15 s de cierre y parecía «colgado grabando».
            int gracia = presenciaLibre ? Math.Min(cfg.GraciaCortaSegundos, cfg.SegundosParaCortar) : cfg.SegundosParaCortar;
            if (sinLlamadaDesde == DateTime.MinValue)
            {
                sinLlamadaDesde = DateTime.Now;
                graciaActual = gracia;
                Anotar(actual, presenciaLibre ? $"saliste de la reunión (tu presencia lo confirma): corto en {gracia} s"
                                              : $"la reunión dejó de verse: si no vuelve en {gracia} s, corto");
            }
            else if (gracia < graciaActual)
            {
                graciaActual = gracia;
                Anotar(actual, $"tu presencia confirma que saliste: acorto la espera a {gracia} s");
            }
            ChequearGracia();
        }

        void ChequearGracia()
        {
            if (actual == null || sinLlamadaDesde == DateTime.MinValue) return;
            double pasaron = (DateTime.Now - sinLlamadaDesde).TotalSeconds;
            if (vivo != null) vivo.CierraEn = Math.Max(0, graciaActual - pasaron);
            if (pasaron >= graciaActual) Cortar("terminó la reunión");
        }

        void Arrancar(string reunion)
        {
            if (!File.Exists(Loopcap)) { Problema("no encuentro loopcap en " + Loopcap); return; }
            long libre = MbLibres(carpeta);
            if (libre >= 0 && libre < MbMinimosParaGrabar) { Problema($"no grabo: quedan {libre} MB libres en el disco"); log.Alerta("Grabador: " + ProblemaActual); return; }

            string id = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string dir = Path.Combine(carpeta, id);
            Directory.CreateDirectory(dir);
            var g = new Grabacion
            {
                Id = id, Dir = dir, Version = 2, Tramos = 0,
                Reunion = string.IsNullOrWhiteSpace(reunion) ? "reunión sin nombre" : reunion,
                Wav = Path.Combine(dir, "audio.wav"), Desde = DateTime.Now, Estado = EstadoGrab.Grabando,
                Etapa = "grabando",
            };
            Anotar(g, "empiezo a grabar «" + g.Reunion + "»" + (libre >= 0 ? $" · {libre / 1024.0:0.#} GB libres" : ""));
            if (libre >= 0 && libre < MbAvisoDisco) { Anotar(g, "⚠ disco casi lleno: loopcap pausa las salidas mudas para cuidar el espacio"); log.Aviso($"Grabador: arranco con solo {libre} MB libres"); }
            lock (candado) lista.Add(g);
            actual = g;
            reinicios = 0;
            sinLlamadaDesde = DateTime.MinValue;
            vivo = new EnVivo { Id = id, Reunion = g.Reunion, Desde = g.Desde, DiscoLibreMB = libre };
            var pu = new PulsoAudio { MicSilenciado = micTeams, MicSilenciadoDesde = DateTime.Now };
            pu.Aviso += (p, aviso) => AvisoPista(g, p, aviso);
            Volatile.Write(ref pulso, pu);
            dirGrabando = dir;
            AnotarMicTeams(dir, micTeams);            // cómo estaba tu micrófono al arrancar
            ProblemaActual = "";
            IniciarTramo(g, 1);
            log.Ok("Grabador: grabando «" + g.Reunion + "» → " + dir);
            GuardarIndice();
            Publicar(true);
        }

        void IniciarTramo(Grabacion g, int n)
        {
            string suf = n == 1 ? "" : ".t" + n;
            var t = new Tramo
            {
                N = n, Inicio = DateTime.Now,
                Wav = Path.Combine(g.Carpeta, "audio" + suf + ".wav"),
                Stop = Path.Combine(g.Carpeta, "STOP" + suf),
                Estado = Path.Combine(g.Carpeta, "estado" + suf + ".json"),
            };
            // -keepsilent: loopcap NUNCA borra; decide el pipeline (con papelera) después de medir la energía de verdad
            // -all: todas las salidas, también las que aparezcan (auriculares que se conectan a mitad de la llamada)
            // -mic: el micrófono que esté usando la llamada, en su pista · -levels: el pulso por stdout (PulsoAudio)
            string args = $"-o \"{t.Wav}\" -all -mic -levels -mono -keepsilent -quiet -stop \"{t.Stop}\" -status \"{t.Estado}\" " +
                          $"-parent {Process.GetCurrentProcess().Id} -t {TopeHorasLoopcap * 3600} -minfree 400";
            try
            {
                var p = new Process
                {
                    StartInfo = new ProcessStartInfo(Loopcap, args)
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        // 🚨 los DOS caños se drenan siempre. Un caño redirigido que nadie lee congeló a loopcap v1.
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                    },
                };
                var pu = Pulso;
                pu?.NuevoTramo(n);
                p.OutputDataReceived += (o, e) => pu?.Linea(e.Data);   // el pulso: niveles y dispositivos
                p.ErrorDataReceived += (o, e) => LineaLoopcap(g, e.Data);
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                t.Proceso = p;
                tramo = t;
                stopVivo = t.Stop;
                proximoTramo = DateTime.MaxValue;
                lock (candado) g.Tramos = n;
                if (vivo != null) vivo.Tramo = n;
                if (n > 1) Anotar(g, $"retomé en el tramo {n}");
            }
            catch (Exception ex)
            {
                Anotar(g, "no pude arrancar loopcap: " + ex.Message);
                log.Error("Grabador: no pude arrancar loopcap: " + ex.Message);
                ProgramarReinicio(g, "no arrancó");
            }
        }

        /// <summary>Un dispositivo que se suma, vuelve, queda en espera o se va: a la bitácora (se ve en el recorrido).</summary>
        void AvisoPista(Grabacion g, PistaEnVivo p, string aviso)
        {
            string que = (p.EsMic ? "micrófono" : "salida") + " «" + p.Nombre + "» " + aviso;
            Anotar(g, que);
            log.Info("Grabador: " + que);
        }

        /// <summary>Los renglones de loopcap: los avisos van a la bitácora y al log; el resto, a debug.</summary>
        void LineaLoopcap(Grabacion g, string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            s = s.Trim();
            if (s.StartsWith("[!]") || s.StartsWith("ERROR"))
            {
                Anotar(g, "loopcap: " + s.TrimStart('[', '!', ']', ' '));
                log.Aviso("Grabador · loopcap: " + s);
            }
            else if (s.StartsWith("[ok]")) { Anotar(g, "loopcap: " + s.Substring(4).Trim()); log.Info("Grabador · loopcap: " + s); }
            else log.Debug("loopcap: " + s);
        }

        /// <summary>Cada segundo: salud de loopcap, telemetría en vivo, gracia de corte y reinicios programados.</summary>
        void Vigilar()
        {
            if (actual != null && tramo != null)
            {
                var st = LeerEstadoLoopcap(tramo.Estado);
                if (st != null) AplicarEstado(st);
                bool murio = false;
                try { murio = tramo.Proceso.HasExited; } catch { murio = true; }
                double edad = st != null ? (DateTime.Now - st.Actualizado).TotalSeconds : (DateTime.Now - tramo.Inicio).TotalSeconds;
                if (murio)
                {
                    int cod = -1; try { cod = tramo.Proceso.ExitCode; } catch { }
                    log.Aviso($"Grabador: loopcap se cerró solo (código {cod}) en el tramo {tramo.N}; retomo en uno nuevo");
                    ProgramarReinicio(actual, $"loopcap se cerró solo (código {cod})");
                }
                else if (edad > SegundosSinEstadoParaTrabado)
                {
                    log.Aviso($"Grabador: loopcap no reporta hace {edad:0} s; lo reinicio en un tramo nuevo");
                    try { File.WriteAllText(tramo.Stop, ""); } catch { }
                    var p = tramo.Proceso;
                    ThreadPool.QueueUserWorkItem(_ => { if (!p.WaitForExit(4000)) Procesos.Matar(p); });
                    ProgramarReinicio(actual, $"loopcap no reportaba hace {edad:0} s");
                }
            }
            if (actual != null && tramo == null && DateTime.Now >= proximoTramo) IniciarTramo(actual, (actual.Tramos) + 1);
            ChequearGracia();
            Publicar(false);
        }

        void ProgramarReinicio(Grabacion g, string porque)
        {
            tramo = null;
            stopVivo = "";
            reinicios++;
            if (vivo != null) vivo.Problema = porque;
            if (reinicios > ReiniciosMaximos)
            {
                Anotar(g, $"loopcap falló {reinicios} veces: dejo de reintentar y cierro lo que hay");
                log.Error("Grabador: loopcap falla una y otra vez; corto la grabación para no perder lo que ya está");
                Cortar("loopcap no se mantiene en pie");
                return;
            }
            proximoTramo = DateTime.Now.AddSeconds(Math.Min(10, 1 << Math.Min(reinicios - 1, 3)));   // 1, 2, 4, 8 s
            Anotar(g, porque + $" · retomo en {(proximoTramo - DateTime.Now).TotalSeconds:0} s");
        }

        void Cortar(string porque)
        {
            var g = actual;
            if (g == null) return;
            actual = null;
            var t = tramo;
            tramo = null;
            stopVivo = "";
            sinLlamadaDesde = DateTime.MinValue;
            proximoTramo = DateTime.MaxValue;
            vivo = null;
            Volatile.Write(ref pulso, null);
            dirGrabando = "";
            cerrando = g.Reunion;
            lock (candado) { g.Estado = EstadoGrab.Cerrando; g.Hasta = DateTime.Now; g.Etapa = "cerrando los archivos"; g.Detalle = porque; }
            Anotar(g, "corto: " + porque);
            if (t != null)
            {
                try { File.WriteAllText(t.Stop, ""); } catch { }
                // la espera va a un hilo del pool: el actor sigue atendiendo (puede empezar otra reunión ya mismo)
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    var reloj = Stopwatch.StartNew();
                    bool limpio = t.Proceso.WaitForExit(20000);
                    if (!limpio) Procesos.Matar(t.Proceso);
                    try { t.Proceso.WaitForExit(); } catch { }   // que terminen de llegar los renglones de stderr
                    Anotar(g, limpio ? $"loopcap cerró limpio en {reloj.ElapsedMilliseconds} ms" : "loopcap no cerró en 20 s: lo terminé (el encabezado se repara igual)");
                    Encolar(() => Finalizar(g));
                });
            }
            else Encolar(() => Finalizar(g));
            GuardarIndice();
            Publicar(true);
        }

        /// <summary>Después del cierre: encabezados honestos, duración real, y a la cola (o a la papelera si fue un amague).</summary>
        void Finalizar(Grabacion g)
        {
            cerrando = "";
            var pistas = PistasDe(g.Carpeta);
            foreach (var p in pistas) { InfoWav i; if (Wav.Reparar(p.Ruta, out i)) Anotar(g, $"encabezado de {Path.GetFileName(p.Ruta)} corregido: decía {i.SegundosDeclarados:0} s y tiene {i.Segundos:0} s"); }
            pistas = PistasDe(g.Carpeta);
            double segundos = pistas.GroupBy(p => p.Tramo).Sum(t => t.Max(p => p.Info.Segundos));
            double perdidos = 0;
            foreach (var est in Directory.GetFiles(g.Carpeta, "estado*.json"))
            {
                var st = LeerEstadoLoopcap(est);
                if (st != null) perdidos += st.Devices.Count > 0 ? st.Devices.Max(d => d.LostSeconds) : 0;
            }
            lock (candado)
            {
                g.SegundosAudio = segundos;
                g.BytesWav = pistas.Sum(p => p.Info.Largo);
                g.SegundosPerdidos = perdidos;
                g.Wav = pistas.Count > 0 ? pistas.OrderByDescending(p => p.Info.Largo).First().Ruta : g.Wav;
            }
            if (pistas.Count == 0 || segundos < cfg.MinimoSegundos)
            {
                string det = pistas.Count == 0 ? "no quedó audio" : $"{(int)segundos} s · menos del mínimo de {cfg.MinimoSegundos} s";
                log.Info($"Grabador: descarto «{g.Reunion}» ({det}) · queda {cfg.DiasPapelera} días en la papelera");
                AlaPapelera(g, "descartada: " + det);
            }
            else
            {
                lock (candado) { g.Estado = EstadoGrab.Grabada; g.Etapa = "en cola"; g.Progreso = -1; g.Detalle = $"{Fmt(segundos)} de audio a salvo · en cola"; }
                Anotar(g, $"{Fmt(segundos)} de audio a salvo en {pistas.Count} pista/s" + (perdidos > 0 ? $" · {perdidos:0.#} s no se pudieron escribir" : ""));
                log.Ok($"Grabador: corté «{g.Reunion}» · {Fmt(segundos)} de audio · {g.BytesWav >> 20} MB" + (perdidos > 0 ? $" · ⚠ {perdidos:0.#} s perdidos" : ""));
                hayTrabajo.Set();
            }
            GuardarIndice();
            Publicar(true);
        }

        void ReintentarEnControl(string id)
        {
            var g = Buscar(id);
            if (g == null || EstadoGrab.EnCurso(g.Estado)) return;
            bool hayWav = PistasDe(g.Carpeta).Count > 0;
            bool hayOpus = g.Archivo.Length > 0 && File.Exists(g.Archivo);
            if (!hayWav && !hayOpus && g.Texto.Length == 0) { Anotar(g, "no hay audio para reintentar"); Publicar(true); return; }
            lock (candado)
            {
                g.Estado = hayWav && !hayOpus ? EstadoGrab.Grabada : hayOpus ? EstadoGrab.Transcribiendo : EstadoGrab.Transcripta;
                g.Detalle = "reintentando"; g.Etapa = "en cola"; g.Progreso = -1;
            }
            Anotar(g, "reintento pedido a mano");
            GuardarIndice();
            Publicar(true);
            hayTrabajo.Set();
        }

        void BorrarEnControl(string id)
        {
            var g = Buscar(id);
            if (g == null || EstadoGrab.EnCurso(g.Estado)) return;
            AlaPapelera(g, "borrada a mano");
            GuardarIndice();
            Publicar(true);
        }

        // ================================================================== PIPELINE

        void BucleCola()
        {
            while (!parar)
            {
                Grabacion g;
                // lo más reciente primero: es lo que vas a querer leer (una recuperación vieja no tapa la reunión de recién)
                lock (candado) g = lista.Where(x => EstadoGrab.EnCola(x.Estado)).OrderByDescending(x => x.Desde).FirstOrDefault();
                if (g == null) { hayTrabajo.WaitOne(15000); continue; }
                try { Procesar(g); }
                catch (Exception ex)
                {
                    Fallar(g, ex.GetType().Name + ": " + ex.Message);
                    log.Error("Grabador (pipeline): " + ex);
                }
            }
        }

        void Procesar(Grabacion g)
        {
            string e;
            lock (candado) e = g.Estado;
            if (e == EstadoGrab.Grabada || e == EstadoGrab.Comprimiendo) { if (!Comprimir(g)) return; lock (candado) e = g.Estado; }
            if (e == EstadoGrab.Transcribiendo && PararAntesDeWhisper) { hayTrabajo.WaitOne(2000); return; }
            if (e == EstadoGrab.Transcribiendo) { if (!Transcribir(g)) return; lock (candado) e = g.Estado; }
            if (e == EstadoGrab.Transcripta || e == EstadoGrab.Resumiendo) Resumir(g);
        }

        sealed class Pista
        {
            public string Ruta = "", Dispositivo = "";
            public int Tramo = 1;
            public bool EsMic;                  // tu micrófono (loopcap -mic): se mezcla sin los tramos en silencio
            public InfoWav Info;
            public Energia Energia;
        }

        static List<Pista> PistasDe(string dir)
        {
            var res = new List<Pista>();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return res;
            foreach (var f in Directory.GetFiles(dir, "audio*.wav"))
            {
                var m = ReTramo.Match(Path.GetFileName(f));
                if (!m.Success || Path.GetFileName(f).Equals("audio16.wav", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(f).IndexOf(".tmp", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                var info = Wav.Leer(f);
                if (!info.Valido) continue;
                res.Add(new Pista
                {
                    Ruta = f, Info = info,
                    Tramo = m.Groups["n"].Success ? int.Parse(m.Groups["n"].Value) : 1,
                    EsMic = m.Groups["mic"].Success,
                    Dispositivo = (m.Groups["mic"].Success ? "mic: " : "") +
                                  (m.Groups["dev"].Success ? m.Groups["dev"].Value.Replace('_', ' ').Trim() : "salida predeterminada"),
                });
            }
            return res.OrderBy(p => p.Tramo).ThenBy(p => p.Ruta).ToList();
        }

        /// <summary>
        /// Repara, mide, elige lo que suena y saca en UNA pasada de ffmpeg el archivo .opus (lo que se guarda) y el
        /// 16 kHz para whisper. Verifica las dos duraciones. Borra las pistas mudas (verificadas por energía).
        /// </summary>
        bool Comprimir(Grabacion g)
        {
            Poner(g, EstadoGrab.Comprimiendo, "revisando las pistas", -1);
            string dir = g.Carpeta, opus = Path.Combine(dir, "audio.opus"), wav16 = Path.Combine(dir, "audio16.wav");
            var pistas = PistasDe(dir);
            if (pistas.Count == 0)
            {
                if (File.Exists(opus) && Procesos.Duracion(opus) > 0)
                {
                    lock (candado) { g.Archivo = opus; g.BytesArchivo = new FileInfo(opus).Length; }
                    Poner(g, EstadoGrab.Transcribiendo, "en cola para transcribir", -1);
                    return true;
                }
                Fallar(g, "no quedó ningún audio en la carpeta");
                return false;
            }
            using (var tarea = Tareas.Empezar("archivando «" + Corto(g.Reunion) + "»", "midiendo las pistas", Tema.Malva))
            {
                foreach (var p in pistas) { InfoWav i; Wav.Reparar(p.Ruta, out i); p.Info = i.Valido ? i : Wav.Leer(p.Ruta); p.Energia = Wav.Medir(p.Ruta); }
                // 🚨 la energía se MUESTREA (100 ms cada 2 s): un micrófono donde dijiste dos frases en media hora podía
                //    salir «mudo» y quedar afuera. El pico que anotó loopcap es EXACTO (todos los paquetes): manda él.
                var picos = PicosLoopcap(dir);
                bool Suena(Pista p) => !p.Energia.Muda || (picos.TryGetValue(Path.GetFileName(p.Ruta), out var pk) && pk > Wav.UmbralMudaDb);
                var suenan = pistas.Where(Suena).ToList();
                foreach (var p in pistas)
                    Anotar(g, $"pista {p.Dispositivo} (tramo {p.Tramo}): {Fmt(p.Info.Segundos)} · {p.Energia}" +
                              (picos.TryGetValue(Path.GetFileName(p.Ruta), out var pk2) ? $" · pico exacto {pk2:0.0} dB" : ""));
                if (suenan.Count == 0)
                {
                    log.Aviso($"Grabador: «{g.Reunion}» no tiene sonido en ninguna salida; la mando a la papelera");
                    AlaPapelera(g, "todas las salidas estaban en silencio");
                    GuardarIndice(); Publicar(true);
                    return false;
                }
                // por tramo: lo que suena; si en un tramo no sonó nada, igual va su pista más larga (conserva el reloj)
                var porTramo = pistas.GroupBy(p => p.Tramo).OrderBy(t => t.Key)
                    .Select(t => { var s = t.Where(Suena).ToList(); return s.Count > 0 ? s : new List<Pista> { t.OrderByDescending(p => p.Info.Segundos).First() }; })
                    .ToList();
                double esperado = porTramo.Sum(t => t.Max(p => p.Info.Segundos));
                var entradas = porTramo.SelectMany(t => t).ToList();
                var sb = new StringBuilder("-hide_banner -nostats -y -progress pipe:1 ");
                foreach (var p in entradas) sb.Append("-ignore_length 1 -i \"").Append(p.Ruta).Append("\" ");
                // tu micrófono entra a la mezcla SIN los tramos en que Teams te tenía en silencio (no salieron a la
                // llamada): volume=0 con enable por intervalos, alineados al cero de cada tramo de loopcap
                var silencios = SilenciosMic(dir);
                var filtro = new StringBuilder();
                int idx = 0, gatillados = 0;
                for (int t = 0; t < porTramo.Count; t++)
                {
                    var tramoP = porTramo[t];
                    DateTime? inicioTramo = InicioTramo(dir, tramoP[0].Tramo);
                    var etiquetas = new StringBuilder();
                    foreach (var pz in tramoP)
                    {
                        string cond = pz.EsMic ? CondicionSilencio(silencios, inicioTramo, pz.Info.Segundos) : "";
                        if (cond.Length > 0)
                        {
                            filtro.Append($"[{idx}:a]volume=volume=0:enable='{cond}'[e{idx}];");
                            etiquetas.Append($"[e{idx}]");
                            gatillados++;
                        }
                        else etiquetas.Append($"[{idx}:a]");
                        idx++;
                    }
                    filtro.Append(etiquetas);
                    // varias pistas sumadas pueden pasar de 0 dBFS: un limitador suave (sin normalizar el nivel)
                    if (tramoP.Count > 1) filtro.Append($"amix=inputs={tramoP.Count}:duration=longest:normalize=0,alimiter=limit=0.95:level=false,");
                    filtro.Append($"aformat=sample_fmts=fltp:channel_layouts=mono,aresample=48000[t{t}];");
                }
                if (silencios.Count > 0)
                    Anotar(g, gatillados > 0 ? $"tu micrófono: {silencios.Count} tramo/s en silencio de Teams quedan fuera de la mezcla"
                                             : "Teams te tuvo en silencio, pero no hay pista de micrófono que recortar");
                for (int t = 0; t < porTramo.Count; t++) filtro.Append($"[t{t}]");
                filtro.Append(porTramo.Count > 1 ? $"concat=n={porTramo.Count}:v=0:a=1," : "anull,").Append("asplit=2[arch][wsp]");
                sb.Append("-filter_complex \"").Append(filtro).Append("\" ");
                // 🚨 a TEMPORALES: si la app muere a mitad, lo que queda es un .tmp y no un audio.opus truncado que
                //    después pase por bueno. Se renombran recién cuando ffprobe dice que duran lo que tienen que durar.
                string opusTmp = Path.Combine(dir, "audio.tmp.opus"), wav16Tmp = Path.Combine(dir, "audio16.tmp.wav");
                sb.Append($"-map \"[arch]\" -c:a libopus -b:a {Math.Max(16, cfg.KbpsArchivo)}k -application voip -ac 1 \"{opusTmp}\" ");
                sb.Append($"-map \"[wsp]\" -ar 16000 -ac 1 -c:a pcm_s16le \"{wav16Tmp}\"");
                lock (candado) g.Pistas = string.Join(" + ", entradas.Select(p => p.Dispositivo).Distinct());
                Poner(g, EstadoGrab.Comprimiendo, "comprimiendo a .opus y preparando el audio para transcribir", 0);
                tarea.Paso("ffmpeg: .opus + 16 kHz", 0);
                var r = Procesos.Correr("ffmpeg", sb.ToString(), (int)Math.Max(600000, esperado * 1000 * 2), l =>
                {
                    var m = ReTiempoFfmpeg.Match(l);
                    if (!m.Success || esperado <= 0) return;
                    double f = Math.Min(1, long.Parse(m.Groups["us"].Value) / 1e6 / esperado);
                    Progresar(g, f); tarea.Paso($"ffmpeg: {f:P0}", f);
                });
                if (!r.Ok) { Fallar(g, "ffmpeg no pudo archivar el audio (" + r + ") · los WAV siguen intactos"); return false; }
                double dOpus = Procesos.Duracion(opusTmp), d16 = Wav.Leer(wav16Tmp).Segundos;
                double tol = Math.Max(2.0, esperado * 0.01);
                if (Math.Abs(dOpus - esperado) > tol || Math.Abs(d16 - esperado) > tol)
                {
                    try { File.Delete(opusTmp); File.Delete(wav16Tmp); } catch { }
                    Fallar(g, $"verificación: esperaba {Fmt(esperado)} y salieron .opus {Fmt(dOpus)} / 16 kHz {Fmt(d16)} · no toco los WAV");
                    return false;
                }
                Reemplazar(opusTmp, opus);
                Reemplazar(wav16Tmp, wav16);
                // para separar las voces: tu micrófono y lo que salió por los parlantes, cada uno aparte y alineados
                if (cfg.Hablantes && entradas.Any(p => p.EsMic) && entradas.Any(p => !p.EsMic)) SepararMicYSalida(g, dir, porTramo, silencios);
                // las mudas (verificadas por energía) no sirven para nada: afuera ya
                foreach (var p in pistas.Where(x => !Suena(x) && !entradas.Contains(x))) { try { File.Delete(p.Ruta); } catch { } }
                long bytesOpus = new FileInfo(opus).Length;
                lock (candado) { g.Archivo = opus; g.BytesArchivo = bytesOpus; g.SegundosAudio = esperado; g.Wav = wav16; }
                Anotar(g, $"archivado: audio.opus {bytesOpus / 1048576.0:0.0} MB · {Fmt(dOpus)} verificados con ffprobe");
                log.Ok($"Grabador: «{g.Reunion}» archivada en .opus ({bytesOpus / 1048576.0:0.0} MB, {Fmt(dOpus)})");
                Poner(g, EstadoGrab.Transcribiendo, "en cola para transcribir", -1);
                return true;
            }
        }

        /// <summary>
        /// Whisper por TROZOS de ~10 min, cortados en el punto más callado cerca de cada marca (nunca a mitad de una
        /// palabra). Cada trozo se transcribe, se VERIFICA (whisper leyó el trozo entero) y queda guardado en
        /// `trozos\`: si la app se reinicia, retoma en el trozo siguiente. En «máximo» una reunión de una hora son
        /// ~8 h de CPU; tirar eso por un reinicio no es opción. Pausado mientras estás en una llamada. Al final se
        /// unen los trozos con los tiempos corridos al reloj de la reunión y se verifica la duración TOTAL.
        /// </summary>
        bool Transcribir(Grabacion g)
        {
            var motor = MotorAsr.De(cfg.Motor);
            string script = motor.EsWhisper ? Transcriptor : TranscriptorSherpa;
            if (!File.Exists(script)) { Fallar(g, "no encuentro " + script); return false; }
            string dir = g.Carpeta, wav16 = Path.Combine(dir, "audio16.wav");
            // 🚨 la vara es la duración VERIFICADA de la grabación (la fija Comprimir con ffprobe). Comparar whisper
            //    contra el propio audio16.wav era circular: un audio16 truncado habría aprobado una transcripción parcial.
            double total = g.SegundosAudio;
            double tolTotal = Math.Max(2.0, total * 0.01);
            double d16 = File.Exists(wav16) ? Wav.Leer(wav16).Segundos : 0;
            if (total <= 0) total = d16;
            if (d16 < total - tolTotal)
            {
                // el audio para whisper está incompleto o falta: si hay WAV de origen se rehace el archivado entero;
                // si no, se lo saca del .opus (verificado) a un temporal
                if (PistasDe(dir).Count > 0) { Anotar(g, $"el audio para whisper estaba incompleto ({Fmt(d16)} de {Fmt(total)}): lo rehago"); Poner(g, EstadoGrab.Grabada, "rehaciendo el audio para whisper", -1); return false; }
                if (g.Archivo.Length == 0 || !File.Exists(g.Archivo)) { Fallar(g, "no hay audio para transcribir"); return false; }
                double dArch = Procesos.Duracion(g.Archivo);
                if (dArch < total - tolTotal) { Fallar(g, $"el .opus dura {Fmt(dArch)} de {Fmt(total)} · no transcribo algo incompleto"); return false; }
                Poner(g, EstadoGrab.Transcribiendo, "descomprimiendo el .opus para whisper", -1);
                string tmp = Path.Combine(dir, "audio16.tmp.wav");
                var rd = Procesos.Correr("ffmpeg", $"-hide_banner -nostats -y -i \"{g.Archivo}\" -ar 16000 -ac 1 -c:a pcm_s16le \"{tmp}\"", 600000);
                if (!rd.Ok || Wav.Leer(tmp).Segundos < total - tolTotal) { try { File.Delete(tmp); } catch { } Fallar(g, "no pude descomprimir el .opus (" + rd + ")"); return false; }
                Reemplazar(tmp, wav16);
            }
            string dirTrozos = Path.Combine(dir, "trozos");
            Directory.CreateDirectory(dirTrozos);
            List<Turno> turnos = null;
            if (cfg.Hablantes && !motor.EsWhisper)
                using (var tareaH = Tareas.Empezar("separando las voces de «" + Corto(g.Reunion) + "»", Fmt(total) + " de audio", Tema.Cyan))
                    turnos = Hablantes(g, wav16, total, tareaH);
            var plan = PlanDeTrozos(wav16, total, Math.Max(30, cfg.TrozoSegundos), turnos);
            int yaHechos = Enumerable.Range(0, plan.Count).Count(i => TrozoListo(Path.Combine(dirTrozos, $"t{i + 1:00}.json"), plan[i].Fin - plan[i].Ini, turnos != null));
            Anotar(g, $"{motor.Nombre} en {plan.Count} trozo/s de ~{Fmt(Math.Max(30, cfg.TrozoSegundos))}" + (yaHechos > 0 ? $" · {yaHechos} ya estaban hechos (retomo)" : ""));
            foreach (var viejo in new[] { ".txt", ".srt", ".json" }) { try { File.Delete(Path.ChangeExtension(wav16, viejo)); } catch { } }

            var reloj = Stopwatch.StartNew();
            using (var tarea = Tareas.Empezar("transcribiendo «" + Corto(g.Reunion) + "»", Fmt(total) + " de audio", Tema.Malva))
            {
                for (int i = 0; i < plan.Count; i++)
                {
                    var t = plan[i];
                    string baseT = Path.Combine(dirTrozos, $"t{i + 1:00}");
                    if (TrozoListo(baseT + ".json", t.Fin - t.Ini, turnos != null)) continue;
                    string wavT = baseT + ".wav";
                    // cortar un PCM con -c copy es instantáneo y exacto al bloque
                    var rc = Procesos.Correr("ffmpeg", string.Format(CultureInfo.InvariantCulture,
                        "-hide_banner -nostats -y -ss {0:0.000} -t {1:0.000} -i \"{2}\" -c copy \"{3}\"", t.Ini, t.Fin - t.Ini, wav16, wavT), 180000);
                    if (!rc.Ok) { Fallar(g, $"no pude cortar el trozo {i + 1} (" + rc + ") · el audio sigue intacto"); return false; }
                    double antes = plan.Take(i).Sum(p => p.Fin - p.Ini);
                    string cortes = turnos != null ? EscribirCortes(baseT + ".cortes.json", turnos, t.Ini, t.Fin) : "";
                    if (!CorrerWhisper(g, wavT, Wav.Leer(wavT).Segundos, antes, total, tarea, i + 1, plan.Count, cortes)) return false;
                    try { File.Delete(wavT); } catch { }
                    Anotar(g, $"trozo {i + 1}/{plan.Count} verificado ({Fmt(t.Ini)}–{Fmt(t.Fin)})");
                    GuardarIndice();
                }
            }

            // --- unir: los tiempos de cada trozo se corren a la posición del trozo en la reunión
            double leido; int palabras; string texto;
            if (!UnirTrozos(dirTrozos, plan, wav16, out leido, out palabras, out texto)) { Fallar(g, "no pude unir los trozos de la transcripción · quedan en trozos\\"); return false; }
            if (leido < total - Math.Max(3, total * 0.02))
            {
                Fallar(g, $"{motor.Nombre} leyó {Fmt(leido)} de {Fmt(total)} · no toco el audio");
                return false;
            }
            lock (candado)
            {
                g.Texto = texto; g.Palabras = palabras; g.SegundosTranscriptos = leido;
                g.MsTranscripcion += reloj.ElapsedMilliseconds;
                g.Detalle = $"{palabras:N0} palabras · {Fmt(leido)} transcriptos en {Fmt(g.MsTranscripcion / 1000.0)}";
            }
            Anotar(g, $"transcripción verificada: {motor.Nombre} leyó {Fmt(leido)} de {Fmt(total)} en {plan.Count} trozo/s · {palabras:N0} palabras");
            log.Ok($"Grabador: transcripta «{g.Reunion}» · {palabras:N0} palabras de {Fmt(leido)}");
            try { Directory.Delete(dirTrozos, true); } catch { }
            LimpiarWavs(g);
            Poner(g, cfg.Resumir ? EstadoGrab.Transcripta : EstadoGrab.Lista, cfg.Resumir ? "en cola para el resumen" : "lista", -1);
            return true;
        }

        const int VentanaCorteSegundos = 45;     // se busca el silencio hasta ±45 s alrededor de cada marca

        struct Trozo { public double Ini, Fin; }

        /// <summary>
        /// Cortes cada ~10 min, en el punto más callado de ±45 s alrededor de cada marca. O(duración/10 min)
        /// lecturas de ~90 s de audio a 16 kHz (≈ 3 MB cada una): milisegundos.
        /// </summary>
        static List<Trozo> PlanDeTrozos(string wav16, double total, int trozo, List<Turno> turnos = null)
        {
            var res = new List<Trozo>();
            if (total <= trozo * 1.35) { res.Add(new Trozo { Ini = 0, Fin = total }); return res; }
            double ini = 0, margen = Math.Min(VentanaCorteSegundos, trozo * 0.2);
            for (double marca = trozo; marca < total - trozo * 0.35; marca += trozo)
            {
                double desde = Math.Max(ini + trozo * 0.5, marca - margen), hasta = Math.Min(total - trozo * 0.25, marca + margen);
                // con hablantes, el corte va en un CAMBIO de turno (cae en el silencio entre dos personas): nadie queda partido
                double corte = turnos?.Select(u => u.Fin).Where(b => b > desde && b < hasta).OrderBy(b => Math.Abs(b - marca)).DefaultIfEmpty(-1).First() ?? -1;
                if (corte < 0) corte = Wav.PuntoMasCallado(wav16, desde, hasta);
                if (corte <= ini + trozo * 0.25) corte = marca;
                res.Add(new Trozo { Ini = ini, Fin = corte });
                ini = corte;
            }
            res.Add(new Trozo { Ini = ini, Fin = total });
            return res;
        }

        /// <summary>El trozo está hecho si su .json cubre la duración pedida (y, con hablantes, si se hizo turno por turno).</summary>
        static bool TrozoListo(string json, double esperado, bool conHablantes = false)
        {
            try
            {
                if (!File.Exists(json)) return false;
                var d = Json.LeerObjeto(json);
                double dur = Convert.ToDouble(d?["duration"], CultureInfo.InvariantCulture);
                return dur >= esperado - Math.Max(2, esperado * 0.02) && (!conHablantes || Json.B(d, "hablantes"));
            }
            catch { return false; }
        }

        /// <summary>
        /// Un paso largo de Python del pipeline (transcriptor o separación de voces) con todo lo que necesita un proceso
        /// de fondo: progreso real mapeado al total, pausa mientras estás en una llamada, prioridad baja, tope de tiempo
        /// ACTIVO y la guardia de duración (el paso dice cuánto audio ve ANTES de gastar CPU).
        /// </summary>
        ResultadoProceso CorrerPaso(Grabacion g, string args, string quien, double esperado, double antes, double total,
                                    Tareas.Testigo tarea, long topeMs, out bool abortado, out string motivo)
        {
            Poner(g, EstadoGrab.Transcribiendo, quien + " · cargando el modelo", total > 0 ? antes / total : 0);
            var activo = Stopwatch.StartNew();
            bool suspendido = false, abort = false;
            string porque = "";
            var cancelar = new CancellationTokenSource();
            var r = Procesos.Correr("python", args, 0, l =>
            {
                var mp = ReProgresoWhisper.Match(l);
                if (mp.Success)
                {
                    double f = double.Parse(mp.Groups["p"].Value, CultureInfo.InvariantCulture) / 100.0;
                    double global = total > 0 ? (antes + f * esperado) / total : f;
                    Progresar(g, global, $"{quien} · {f:P0}"); tarea.Paso($"{quien} · {global:P0} del total", global);
                    return;
                }
                var md = ReDuracionWhisper.Match(l);
                if (md.Success)
                {
                    double d = double.Parse(md.Groups["d"].Value, CultureInfo.InvariantCulture);
                    // ⭐ la guardia que faltaba el 23-sep: el paso dice cuánto audio ve ANTES de gastar CPU
                    if (d < esperado - Math.Max(3, esperado * 0.02)) { abort = true; porque = $"{quien}: ve {Fmt(d)} de {Fmt(esperado)}"; cancelar.Cancel(); }
                    else Poner(g, EstadoGrab.Transcribiendo, quien, total > 0 ? antes / total : 0);
                }
            }, vigilar: p =>
            {
                bool pausar = cfg.PausarEnLlamada && enLlamada;
                if (pausar != suspendido && (pausar ? Suspender(p) : Reanudar(p)))
                {
                    suspendido = pausar;
                    if (pausar) activo.Stop(); else activo.Start();
                    lock (candado) g.EnPausa = pausar;
                    Anotar(g, pausar ? $"{quien}: en pausa, estás en una llamada" : $"{quien}: retoma, terminó la llamada");
                    tarea.Paso(pausar ? "en pausa mientras estás en una llamada" : "retomando", g.Progreso >= 0 ? g.Progreso : (double?)null);
                    Publicar(true);
                }
                try { if (!suspendido && p.PriorityClass != ProcessPriorityClass.BelowNormal) p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                if (activo.ElapsedMilliseconds > topeMs) { abort = true; porque = $"{quien}: pasó el tope de {Fmt(topeMs / 1000.0)} de CPU"; cancelar.Cancel(); }
            }, cancel: cancelar.Token);
            lock (candado) g.EnPausa = false;
            abortado = abort; motivo = porque;
            return r;
        }

        /// <summary>
        /// Una corrida del transcriptor sobre un trozo (con sus turnos de hablante si los hay) y la doble verificación
        /// de duración: al arrancar (CorrerPaso) y en el .json al final.
        /// </summary>
        bool CorrerWhisper(Grabacion g, string wav, double esperado, double antes, double total, Tareas.Testigo tarea, int n, int de, string cortes = "")
        {
            var motor = MotorAsr.De(cfg.Motor);
            bool maximo = cfg.Calidad == "maximo";
            string args = motor.EsWhisper
                ? $"\"{Transcriptor}\" \"{wav}\" --lang {cfg.Idioma} " + (maximo ? "--compute float32 --beam 10" : "--compute int8_float32 --beam 5")
                : $"\"{TranscriptorSherpa}\" \"{wav}\" --lang {cfg.Idioma} {motor.Args}" + (cortes.Length > 0 ? $" --cortes \"{cortes}\"" : "");
            string quien = de > 1 ? $"{motor.Nombre} · trozo {n}/{de}" : motor.Nombre;
            long topeMs = motor.EsWhisper ? (long)Math.Max(45 * 60_000, esperado * 1000 * (maximo ? 14 : 5))
                                          : (long)Math.Max(20 * 60_000, esperado * 1000 * motor.FactorTope);
            var r = CorrerPaso(g, args, quien, esperado, antes, total, tarea, topeMs, out bool abortado, out string motivo);
            if (abortado) { Fallar(g, motivo + " · no toco el audio"); return false; }
            string json = Path.ChangeExtension(wav, ".json");
            if (!r.Ok || !File.Exists(json)) { Fallar(g, $"{motor.Nombre} no terminó el trozo {n} (" + r + ") · el audio sigue intacto"); return false; }
            if (!TrozoListo(json, esperado, cortes.Length > 0)) { Fallar(g, $"{motor.Nombre} no leyó el trozo {n} entero · no toco el audio"); return false; }
            return true;
        }

        // ================================================================== quién habla

        struct Turno { public double Ini, Fin; public string Quien; }

        public static string ScriptHablantes => Herramientas.Resolver(rutas?.ScriptHablantes, @"transcribe\hablantes.py");

        /// <summary>
        /// Separa las voces de la reunión ENTERA (hablantes.py), una sola vez: si fuera por trozo, «Persona 1» cambiaría
        /// de trozo a trozo. Con tu micrófono aparte (mic16/salida16), tus turnos son «Yo» y la diarización se hace sobre
        /// la salida. Lo caro queda en diar\ (reanudable). Devuelve los turnos, o null si no se pudo: entonces se
        /// transcribe sin hablantes (la transcripción no se pierde nunca por esto).
        /// </summary>
        List<Turno> Hablantes(Grabacion g, string wav16, double total, Tareas.Testigo tarea)
        {
            string dir = g.Carpeta, json = Path.Combine(dir, "audio16.hablantes.json");
            var listos = LeerTurnos(json, total);
            if (listos != null) return listos;
            if (!File.Exists(ScriptHablantes)) { Anotar(g, "no encuentro hablantes.py: transcribo sin separar voces"); return null; }
            string mic = Path.Combine(dir, "mic16.wav"), sal = Path.Combine(dir, "salida16.wav");
            bool conMic = File.Exists(mic) && File.Exists(sal);
            int tope = g.MaxOtros > 0 ? g.MaxOtros + (conMic ? 0 : 1) : 0;   // sin tu mic aparte, tu voz también está en la mezcla
            string args = $"\"{ScriptHablantes}\" \"{wav16}\" --cache \"{Path.Combine(dir, "diar")}\"" +
                          (conMic ? $" --mic \"{mic}\" --salida \"{sal}\"" : "") + (tope > 0 ? $" --max-hablantes {tope}" : "");
            var r = CorrerPaso(g, args, "separando las voces", total, 0, total, tarea, (long)Math.Max(20 * 60_000, total * 1000 * 2), out bool abortado, out string motivo);
            listos = LeerTurnos(json, total);
            if (abortado || !r.Ok || listos == null)
            {
                Anotar(g, "no pude separar las voces (" + (abortado ? motivo : r.ToString()) + "): transcribo sin hablantes");
                return null;
            }
            int n = listos.Select(u => u.Quien).Distinct().Count();
            Anotar(g, $"voces separadas: {n} hablante/s en {listos.Count} turnos" + (conMic ? " · «Yo» sale de tu micrófono" : "") +
                      (tope > 0 ? $" · tope {tope} (Teams llegó a mostrar {g.MaxOtros} más)" : ""));
            return listos;
        }

        /// <summary>Los turnos de audio16.hablantes.json, si son de ESTE audio (misma duración). O(turnos).</summary>
        static List<Turno> LeerTurnos(string json, double total)
        {
            try
            {
                var d = Json.LeerObjeto(json);
                if (d == null || !d.ContainsKey("turnos")) return null;
                double dur = Convert.ToDouble(d["duracion"], CultureInfo.InvariantCulture);
                if (Math.Abs(dur - total) > Math.Max(2, total * 0.01)) return null;
                var res = new List<Turno>();
                foreach (var x in (System.Collections.IEnumerable)d["turnos"])
                {
                    var t = ((System.Collections.IEnumerable)x).Cast<object>().ToList();
                    res.Add(new Turno { Ini = Convert.ToDouble(t[0], CultureInfo.InvariantCulture), Fin = Convert.ToDouble(t[1], CultureInfo.InvariantCulture), Quien = Convert.ToString(t[2]) });
                }
                return res.Count > 0 ? res : null;
            }
            catch { return null; }
        }

        /// <summary>Los turnos que caen en [ini, fin), recortados y corridos al cero del trozo, para el transcriptor.</summary>
        static string EscribirCortes(string ruta, List<Turno> turnos, double ini, double fin)
        {
            var sb = new StringBuilder("[");
            foreach (var u in turnos)
            {
                double a = Math.Max(u.Ini, ini), b = Math.Min(u.Fin, fin);
                if (b - a < 0.05) continue;
                if (sb.Length > 1) sb.Append(',');
                sb.Append(string.Format(CultureInfo.InvariantCulture, "[{0:0.###},{1:0.###},\"{2}\"]", a - ini, b - ini, u.Quien.Replace("\"", "")));
            }
            sb.Append(']');
            File.WriteAllText(ruta, sb.ToString(), new UTF8Encoding(false));
            return ruta;
        }

        /// <summary>
        /// Tu micrófono (sin los tramos en silencio de Teams) y la salida (los demás), cada uno en su 16 kHz y en la
        /// MISMA línea de tiempo que audio16: con eso tus turnos salen del micrófono («Yo») y la diarización va sobre la
        /// salida, donde solo están los demás. Si falla, no pasa nada: se diariza la mezcla. O(pistas).
        /// </summary>
        void SepararMicYSalida(Grabacion g, string dir, List<List<Pista>> porTramo, List<(DateTime ini, DateTime fin)> silencios)
        {
            var sb = new StringBuilder("-hide_banner -nostats -y ");
            foreach (var p in porTramo.SelectMany(t => t)) sb.Append("-ignore_length 1 -i \"").Append(p.Ruta).Append("\" ");
            var f = new StringBuilder();
            int idx = 0;
            for (int t = 0; t < porTramo.Count; t++)
            {
                var tramoP = porTramo[t];
                double dur = tramoP.Max(p => p.Info.Segundos);
                DateTime? inicio = InicioTramo(dir, tramoP[0].Tramo);
                var mics = new List<string>(); var sals = new List<string>();
                foreach (var p in tramoP)
                {
                    string lab = $"[{idx}:a]";
                    if (p.EsMic)
                    {
                        string cond = CondicionSilencio(silencios, inicio, p.Info.Segundos);
                        if (cond.Length > 0) { f.Append($"{lab}volume=volume=0:enable='{cond}'[g{idx}];"); lab = $"[g{idx}]"; }
                        mics.Add(lab);
                    }
                    else sals.Add(lab);
                    idx++;
                }
                MezclaDe(f, mics, dur, $"m{t}");
                MezclaDe(f, sals, dur, $"s{t}");
            }
            string Unir(string pre) => string.Concat(Enumerable.Range(0, porTramo.Count).Select(t => $"[{pre}{t}]")) +
                                       (porTramo.Count > 1 ? $"concat=n={porTramo.Count}:v=0:a=1," : "anull,") + "aresample=16000";
            f.Append(Unir("m")).Append("[mic];").Append(Unir("s")).Append("[sal]");
            string micTmp = Path.Combine(dir, "mic16.tmp.wav"), salTmp = Path.Combine(dir, "salida16.tmp.wav");
            sb.Append("-filter_complex \"").Append(f).Append("\" ");
            sb.Append($"-map \"[mic]\" -ac 1 -c:a pcm_s16le \"{micTmp}\" -map \"[sal]\" -ac 1 -c:a pcm_s16le \"{salTmp}\"");
            var r = Procesos.Correr("ffmpeg", sb.ToString(), 900000);
            double d16 = Wav.Leer(Path.Combine(dir, "audio16.wav")).Segundos, dm = Wav.Leer(micTmp).Segundos, ds = Wav.Leer(salTmp).Segundos;
            if (!r.Ok || Math.Abs(dm - d16) > 0.5 || Math.Abs(ds - d16) > 0.5)
            {
                try { File.Delete(micTmp); File.Delete(salTmp); } catch { }
                Anotar(g, $"no pude separar tu micrófono de la salida ({r} · mic {Fmt(dm)} / salida {Fmt(ds)} / mezcla {Fmt(d16)}): las voces se separan sobre la mezcla");
                return;
            }
            Reemplazar(micTmp, Path.Combine(dir, "mic16.wav"));
            Reemplazar(salTmp, Path.Combine(dir, "salida16.wav"));
            Anotar(g, "tu micrófono y la salida quedaron aparte: «Yo» sale del micrófono al separar las voces");
        }

        /// <summary>Las pistas de un lado en un tramo, mezcladas y llevadas a la duración EXACTA del tramo (silencio si no hay).</summary>
        static void MezclaDe(StringBuilder f, List<string> labs, double dur, string salida)
        {
            string d = dur.ToString("0.###", CultureInfo.InvariantCulture);
            if (labs.Count == 0) { f.Append($"anullsrc=r=48000:cl=mono:d={d},aformat=sample_fmts=fltp:channel_layouts=mono[{salida}];"); return; }
            f.Append(string.Concat(labs));
            if (labs.Count > 1) f.Append($"amix=inputs={labs.Count}:duration=longest:normalize=0,");
            f.Append($"aformat=sample_fmts=fltp:channel_layouts=mono,aresample=48000,apad=whole_dur={d},atrim=0:{d}[{salida}];");
        }

        /// <summary>Une los .json de los trozos en audio16.json/.srt/.txt con los tiempos de la reunión. O(segmentos).</summary>
        static bool UnirTrozos(string dirTrozos, List<Trozo> plan, string wav16, out double leido, out int palabras, out string texto)
        {
            leido = 0; palabras = 0; texto = "";
            var segs = new List<Dictionary<string, object>>();
            var srt = new StringBuilder();
            var txt = new StringBuilder();
            var conHablantes = new StringBuilder();
            string hablaAntes = "";
            string idioma = "";
            try
            {
                for (int i = 0; i < plan.Count; i++)
                {
                    var d = Json.LeerObjeto(Path.Combine(dirTrozos, $"t{i + 1:00}.json"));
                    if (d == null) return false;
                    if (idioma.Length == 0) idioma = Json.S(d, "language");
                    leido += Convert.ToDouble(d["duration"], CultureInfo.InvariantCulture);
                    foreach (var s in Json.Lista(d, "segments"))
                    {
                        double ini = Convert.ToDouble(s["start"], CultureInfo.InvariantCulture) + plan[i].Ini;
                        double fin = Convert.ToDouble(s["end"], CultureInfo.InvariantCulture) + plan[i].Ini;
                        string t = Json.S(s, "text").Trim();
                        if (t.Length == 0) continue;
                        int id = segs.Count + 1;
                        string quien = Json.S(s, "speaker");
                        var seg = new Dictionary<string, object> { ["id"] = id, ["start"] = Math.Round(ini, 3), ["end"] = Math.Round(fin, 3), ["text"] = t };
                        if (quien.Length > 0)
                        {
                            seg["speaker"] = quien;
                            // un renglón por turno: los segmentos seguidos de la misma persona se juntan
                            if (quien == hablaAntes) conHablantes.Append(' ').Append(t);
                            else { if (conHablantes.Length > 0) conHablantes.Append('\n'); conHablantes.Append('[').Append(Ts(ini).Substring(0, 8)).Append("] ").Append(quien).Append(": ").Append(t); }
                            hablaAntes = quien;
                        }
                        segs.Add(seg);
                        srt.Append(id).Append('\n').Append(Ts(ini)).Append(" --> ").Append(Ts(fin)).Append('\n').Append(t).Append("\n\n");
                        if (txt.Length > 0) txt.Append(' ');
                        txt.Append(t);
                    }
                }
                texto = txt.ToString().Trim();
                palabras = texto.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
                File.WriteAllText(Path.ChangeExtension(wav16, ".txt"), texto + "\n", new UTF8Encoding(false));
                if (conHablantes.Length > 0)
                {
                    // «[00:01:12] Persona 2: …» — es lo que se muestra y lo que se resume
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(wav16), "audio16.hablantes.txt"), conHablantes + "\n", new UTF8Encoding(false));
                    texto = conHablantes.ToString();
                }
                File.WriteAllText(Path.ChangeExtension(wav16, ".srt"), srt.ToString(), new UTF8Encoding(false));
                Json.Escribir(Path.ChangeExtension(wav16, ".json"), new Dictionary<string, object> { ["language"] = idioma, ["duration"] = Math.Round(leido, 2), ["segments"] = segs });
                return true;
            }
            catch { return false; }
        }

        static string Ts(double s)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, s));
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
        }

        /// <summary>Los WAV grandes se van SOLO con el .opus y el texto verificados. El .opus no se toca nunca.</summary>
        void LimpiarWavs(Grabacion g)
        {
            if (!cfg.BorrarAudio) { Anotar(g, "los WAV quedan (pediste no borrarlos)"); return; }
            if (!(g.Archivo.Length > 0 && File.Exists(g.Archivo) && new FileInfo(g.Archivo).Length > 0))
            { Anotar(g, "no borro los WAV: no hay .opus verificado"); return; }
            long liberados = 0; int n = 0;
            foreach (var w in Directory.GetFiles(g.Carpeta, "*.wav"))
            {
                try { long b = new FileInfo(w).Length; File.Delete(w); liberados += b; n++; } catch { }
            }
            lock (candado) { g.AudioBorrado = true; g.BytesWav = 0; g.Wav = g.Archivo; }
            Anotar(g, $"borré {n} WAV ({liberados / 1048576.0:0} MB); queda audio.opus ({g.BytesArchivo / 1048576.0:0.0} MB)");
        }

        void Resumir(Grabacion g)
        {
            if (!cfg.Resumir) { Poner(g, EstadoGrab.Lista, "lista", -1); return; }
            Poner(g, EstadoGrab.Resumiendo, "el modelo local está leyendo la reunión", -1);
            string detalle;
            if (!AsistenteIA.Verificar(out detalle))
            {
                lock (candado) g.Detalle += " · sin resumen: " + detalle;
                Poner(g, EstadoGrab.Lista, "lista (sin resumen)", -1);
                GuardarIndice();
                return;
            }
            using (Tareas.Empezar("resumiendo «" + Corto(g.Reunion) + "»", "el modelo local está leyendo la reunión", Tema.Cyan))
            {
                string texto = g.Texto.Length > 9000 ? g.Texto.Substring(0, 9000) + "\n[…]" : g.Texto;
                string det2;
                string res = AsistenteIA.Redactar(texto, "",
                    "Esto es la transcripción automática de una reunión de trabajo, con errores de audio. Devolvé: una línea de resumen, después «Temas:» con viñetas, después «Decisiones:» y después «Pendientes:» con quién y qué. No inventes nada que no esté en el texto; si algo no se entiende, no lo pongas.",
                    700, out det2);
                lock (candado)
                {
                    g.Resumen = res;
                    g.Detalle += res.Length > 0 ? " · resumido en " + det2 : " · el resumen no salió: " + det2;
                }
                log.Escribir(res.Length > 0 ? Nivel.Ok : Nivel.Aviso, "Grabador: resumen de «" + g.Reunion + "» · " + det2);
            }
            Poner(g, EstadoGrab.Lista, "lista", -1);
        }

        // ================================================================== estado de loopcap

        sealed class EstadoLoopcap
        {
            public DateTime Actualizado;
            public DateTime? Inicio;              // el cero de las pistas de ese tramo (RFC3339 con nanos)
            public string Estado = "";
            public long DiscoLibreMB = -1;
            public List<Dispositivo> Devices = new List<Dispositivo>();
        }

        sealed class Dispositivo
        {
            public string Nombre = "", Error = "", Archivo = "";
            public double Segundos, PicoDb = -120, PicoTotalDb = -120, MB, LostSeconds;
            public bool Pausada;
        }

        static EstadoLoopcap LeerEstadoLoopcap(string ruta)
        {
            try
            {
                if (!File.Exists(ruta)) return null;
                string txt;
                using (var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, Encoding.UTF8)) txt = sr.ReadToEnd();
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(txt);
                if (d == null) return null;
                var st = new EstadoLoopcap
                {
                    Estado = Json.S(d, "state"),
                    DiscoLibreMB = (long)Num(d, "disk_free_mb", -1),
                };
                DateTimeOffset dto;
                if (DateTimeOffset.TryParse(Json.S(d, "updated"), CultureInfo.InvariantCulture, DateTimeStyles.None, out dto)) st.Actualizado = dto.LocalDateTime;
                if (DateTimeOffset.TryParse(Json.S(d, "started"), CultureInfo.InvariantCulture, DateTimeStyles.None, out dto)) st.Inicio = dto.LocalDateTime;
                foreach (var x in Json.Lista(d, "devices"))
                    st.Devices.Add(new Dispositivo
                    {
                        Nombre = Json.S(x, "device"), Error = Json.S(x, "write_error"), Archivo = Json.S(x, "file"),
                        Segundos = Num(x, "seconds"), PicoDb = Num(x, "peak_db", -120), PicoTotalDb = Num(x, "peak_total_db", -120),
                        MB = Num(x, "mb"), LostSeconds = Num(x, "lost_seconds"), Pausada = Json.B(x, "paused"),
                    });
                return st;
            }
            catch { return null; }
        }

        /// <summary>
        /// El pico EXACTO de cada pista según loopcap (máximo sobre todos sus paquetes), de los estados finales de
        /// todos los tramos: nombre de archivo → dB. O(pistas).
        /// </summary>
        static Dictionary<string, double> PicosLoopcap(string dir)
        {
            var res = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var est in Directory.GetFiles(dir, "estado*.json"))
            {
                var st = LeerEstadoLoopcap(est);
                if (st == null) continue;
                foreach (var d in st.Devices)
                    if (d.Archivo.Length > 0) res[Path.GetFileName(d.Archivo)] = d.PicoTotalDb;
            }
            return res;
        }

        /// <summary>El cero de las pistas del tramo n (lo que loopcap anotó como «started»); null si no se sabe.</summary>
        static DateTime? InicioTramo(string dir, int n) =>
            LeerEstadoLoopcap(Path.Combine(dir, n == 1 ? "estado.json" : $"estado.t{n}.json"))?.Inicio;

        /// <summary>
        /// Los intervalos (hora local) en que Teams tenía tu micrófono en silencio, según mic-teams.jsonl. Un
        /// silencio sin cierre dura hasta el final. O(renglones).
        /// </summary>
        static List<(DateTime ini, DateTime fin)> SilenciosMic(string dir)
        {
            var res = new List<(DateTime, DateTime)>();
            string ruta = Path.Combine(dir, ArchivoMicTeams);
            if (!File.Exists(ruta)) return res;
            var marcas = new List<(DateTime t, bool s)>();
            foreach (var l in File.ReadAllLines(ruta, Encoding.UTF8))
            {
                var m = ReMarcaMic.Match(l);
                if (m.Success && DateTimeOffset.TryParse(m.Groups["t"].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
                    marcas.Add((dto.LocalDateTime, m.Groups["s"].Value == "true"));
            }
            DateTime? desde = null;
            foreach (var (t, sil) in marcas.OrderBy(x => x.t))
            {
                if (sil && desde == null) desde = t;
                else if (!sil && desde != null) { res.Add((desde.Value, t)); desde = null; }
            }
            if (desde != null) res.Add((desde.Value, DateTime.MaxValue));
            return res;
        }

        static readonly Regex ReMarcaMic = new Regex("\"t\":\"(?<t>[^\"]+)\",\"silenciado\":(?<s>true|false)", RegexOptions.Compiled);

        /// <summary>
        /// La condición de ffmpeg («between(t,a,b)+…») con los silencios que caen dentro de una pista que empieza en
        /// <paramref name="inicio"/> y dura <paramref name="dur"/> s. Se corre DemoraLecturaMicS hacia atrás: el
        /// vigía se entera un poco después del clic. "" si no hay nada que recortar o no se sabe alinear.
        /// </summary>
        static string CondicionSilencio(List<(DateTime ini, DateTime fin)> silencios, DateTime? inicio, double dur)
        {
            if (silencios.Count == 0 || inicio == null || dur <= 0) return "";
            var partes = new List<string>();
            foreach (var (a0, b0) in silencios)
            {
                double a = (a0 - inicio.Value).TotalSeconds - DemoraLecturaMicS;
                double b = b0 == DateTime.MaxValue ? dur + 1 : (b0 - inicio.Value).TotalSeconds - DemoraLecturaMicS;
                a = Math.Max(0, a); b = Math.Min(dur + 1, b);
                if (b <= a || b <= 0 || a >= dur) continue;
                partes.Add(string.Format(CultureInfo.InvariantCulture, "between(t,{0:0.###},{1:0.###})", a, b));
            }
            return string.Join("+", partes);
        }

        static double Num(Dictionary<string, object> d, string k, double def = 0)
        {
            try { return d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : def; }
            catch { return def; }
        }

        void AplicarEstado(EstadoLoopcap st)
        {
            if (vivo == null) return;
            var fuerte = st.Devices.OrderByDescending(d => d.PicoTotalDb).FirstOrDefault();
            vivo.Actualizado = st.Actualizado;
            vivo.Segundos = (actual != null ? (DateTime.Now - actual.Desde).TotalSeconds : 0);
            vivo.NivelDb = st.Devices.Count > 0 ? st.Devices.Max(d => d.PicoDb) : -120;
            vivo.PicoTotalDb = fuerte?.PicoTotalDb ?? -120;
            vivo.Pista = fuerte != null && fuerte.PicoTotalDb > Wav.UmbralMudaDb ? fuerte.Nombre : "";
            vivo.MB = st.Devices.Sum(d => d.MB);
            vivo.DiscoLibreMB = st.DiscoLibreMB;
            vivo.SegundosPerdidos = st.Devices.Count > 0 ? st.Devices.Max(d => d.LostSeconds) : 0;
            var err = st.Devices.FirstOrDefault(d => d.Error.Length > 0);
            vivo.Problema = err != null ? "no puedo escribir: " + err.Error
                          : st.Devices.Any(d => d.Pausada) ? "disco casi lleno: pausé las salidas mudas"
                          : "";
        }

        // ================================================================== índice, papelera, huérfanas

        string RutaIndice => Path.Combine(carpeta, "indice.json");

        /// <summary>
        /// JavaScriptSerializer solo reconoce las fechas como <c>"\/Date(ms)\/"</c> (barras escapadas). Un índice
        /// editado a mano o por otra herramienta las trae como <c>"/Date(ms)/"</c> y la lectura ENTERA fallaba (pasó el
        /// 23-sep). Se normalizan antes de leer: O(n) sobre el texto, una sola pasada de regex.
        /// </summary>
        static readonly Regex ReFechaSinEscapar = new Regex("\"/Date\\((-?\\d+)\\)/\"", RegexOptions.Compiled);

        /// <summary>Si el índice no se pudo leer: se preserva como copia y NO se importan huérfanas (nada se pisa).</summary>
        bool indiceIlegible;

        /// <summary>
        /// El índice tal cual está en disco, con las fechas en hora local y ningún campo en null. SOLO LECTURA (lo usan
        /// también las pruebas sobre los datos reales). Tira si el archivo está ilegible: el que llama decide qué hacer.
        /// </summary>
        internal static List<Grabacion> LeerIndice(string ruta)
        {
            if (!File.Exists(ruta)) return new List<Grabacion>();
            string texto = File.ReadAllText(ruta, Encoding.UTF8);
            texto = ReFechaSinEscapar.Replace(texto, m => "\"\\/Date(" + m.Groups[1].Value + ")\\/\"");
            var l = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<List<Grabacion>>(texto) ?? new List<Grabacion>();
            foreach (var g in l)
            {
                if (g.Desde.Kind == DateTimeKind.Utc) g.Desde = g.Desde.ToLocalTime();
                if (g.Hasta.Kind == DateTimeKind.Utc && g.Hasta.Year > 2000) g.Hasta = g.Hasta.ToLocalTime();
                if (g.Hasta.Year < 2000) g.Hasta = default(DateTime);
                g.Bitacora = g.Bitacora ?? new List<string>();
                g.Dir = g.Dir ?? ""; g.Archivo = g.Archivo ?? ""; g.Pistas = g.Pistas ?? ""; g.Etapa = g.Etapa ?? "";
                g.Texto = g.Texto ?? ""; g.Resumen = g.Resumen ?? ""; g.Reunion = g.Reunion ?? ""; g.Detalle = g.Detalle ?? ""; g.Wav = g.Wav ?? "";
                g.EnPausa = false;
            }
            return l;
        }

        void CargarIndice()
        {
            try
            {
                if (!File.Exists(RutaIndice)) return;
                var l = LeerIndice(RutaIndice);
                foreach (var g in l)
                {
                    // las rutas del índice son absolutas: si la app se mudó de carpeta (teams-autoleave → TeamsTools),
                    // cada grabación se reubica por su Id, que ES el nombre de su carpeta. El índice viejo sirve tal cual.
                    string aca = Path.Combine(carpeta, g.Id);
                    if (g.Id.Length > 0 && Directory.Exists(aca) && !string.Equals(g.Carpeta, aca, StringComparison.OrdinalIgnoreCase))
                    {
                        g.Dir = aca;
                        if (g.Wav.Length > 0) g.Wav = Path.Combine(aca, Path.GetFileName(g.Wav));
                        if (g.Archivo.Length > 0) g.Archivo = Path.Combine(aca, Path.GetFileName(g.Archivo));
                    }
                    // la app se cerró a mitad: se retoma desde donde haya audio
                    if (EstadoGrab.EnCurso(g.Estado) || g.Estado == EstadoGrab.Comprimiendo || g.Estado == EstadoGrab.Transcribiendo || g.Estado == EstadoGrab.Resumiendo)
                    {
                        bool hayWav = PistasDe(g.Carpeta).Count > 0;
                        bool hayOpus = g.Archivo.Length > 0 && File.Exists(g.Archivo);
                        if (g.Hasta == default(DateTime)) g.Hasta = g.Desde;
                        string antes = g.Estado;
                        // se retoma en el punto VERIFICADO más avanzado: un Archivo puesto ya pasó por ffprobe (y ahora
                        // se escribe a temporal y se renombra, así que nunca es un .opus a medias)
                        g.Estado = g.Estado == EstadoGrab.Resumiendo ? EstadoGrab.Transcripta
                                 : g.Estado == EstadoGrab.Transcribiendo && hayOpus ? EstadoGrab.Transcribiendo
                                 : hayWav ? EstadoGrab.Grabada : hayOpus ? EstadoGrab.Transcribiendo : EstadoGrab.Fallo;
                        g.Detalle = g.Estado == EstadoGrab.Fallo ? "se cortó al cerrarse la app y no quedó audio" : "quedó a medias al cerrarse la app · la retomo";
                        g.Etapa = g.Estado == EstadoGrab.Fallo ? "" : "en cola";
                        Anotar(g, $"al reabrir: estaba «{antes}» → {g.Estado}");
                    }
                }
                lock (candado) lista.AddRange(l);
            }
            catch (Exception ex)
            {
                // 🚨 nunca perder el historial por un índice ilegible: se guarda una copia intacta y no se toca nada más
                indiceIlegible = true;
                string copia = Path.Combine(carpeta, $"indice.ilegible-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                try { File.Copy(RutaIndice, copia, true); } catch { }
                ProblemaActual = $"no pude leer el índice ({ex.Message}); quedó intacto en {Path.GetFileName(copia)}";
                log.Alerta("Grabador: " + ProblemaActual);
            }
        }

        /// <summary>Carpetas con audio que el índice no conoce (una app que murió, un rescate): se suman como grabadas.</summary>
        void ImportarHuerfanas()
        {
            // con el índice ilegible, TODAS las carpetas parecerían huérfanas: no se importa nada (se arregla a mano)
            if (indiceIlegible) return;
            try
            {
                HashSet<string> conocidas;
                lock (candado) conocidas = new HashSet<string>(lista.Select(g => g.Id), StringComparer.OrdinalIgnoreCase);
                foreach (var d in Directory.GetDirectories(carpeta))
                {
                    string id = Path.GetFileName(d);
                    if (id.StartsWith("_") || conocidas.Contains(id)) continue;
                    var pistas = PistasDe(d);
                    string opus = Path.Combine(d, "audio.opus");
                    if (pistas.Count == 0 && !File.Exists(opus)) continue;
                    DateTime desde;
                    if (!DateTime.TryParseExact(id.Substring(0, Math.Min(15, id.Length)), "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out desde))
                        desde = Directory.GetCreationTime(d);
                    string nombre = "reunión recuperada";
                    try { var marca = Path.Combine(d, "reunion.txt"); if (File.Exists(marca)) nombre = File.ReadAllText(marca, Encoding.UTF8).Trim(); } catch { }
                    double seg = pistas.GroupBy(p => p.Tramo).Sum(t => t.Max(p => p.Info.Segundos));
                    var g = new Grabacion
                    {
                        Id = id, Dir = d, Version = 2, Reunion = nombre, Desde = desde, Hasta = desde.AddSeconds(seg),
                        SegundosAudio = seg, BytesWav = pistas.Sum(p => p.Info.Largo), Tramos = Math.Max(1, pistas.Select(p => p.Tramo).DefaultIfEmpty(1).Max()),
                        Archivo = File.Exists(opus) ? opus : "", BytesArchivo = File.Exists(opus) ? new FileInfo(opus).Length : 0,
                        Estado = pistas.Count > 0 ? EstadoGrab.Grabada : EstadoGrab.Transcribiendo,
                        Detalle = "recuperada de una carpeta sin índice · en cola", Etapa = "en cola",
                    };
                    Anotar(g, $"la encontré en {id} sin índice: {Fmt(seg)} de audio · la proceso");
                    lock (candado) lista.Add(g);
                    log.Info($"Grabador: recuperé la carpeta {id} ({Fmt(seg)}) y la pongo en cola");
                }
                GuardarIndice();
            }
            catch (Exception ex) { log.Aviso("Grabador: no pude revisar carpetas huérfanas: " + ex.Message); }
        }

        void GuardarIndice()
        {
            Grabacion[] copia;
            lock (candado)
            {
                // se podan las más viejas del ÍNDICE; sus carpetas (con el .opus) quedan en disco
                while (lista.Count > cfg.MaxGrabacionesGuardadas && lista.Any(x => !EstadoGrab.EnCurso(x.Estado) && !EstadoGrab.EnCola(x.Estado)))
                    lista.Remove(lista.Where(x => !EstadoGrab.EnCurso(x.Estado) && !EstadoGrab.EnCola(x.Estado)).OrderBy(x => x.Desde).First());
                copia = lista.Select(x => x.Clonar()).ToArray();
            }
            try { Disco.Escribir(RutaIndice, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(copia)); }
            catch (Exception ex) { ProblemaActual = "no pude guardar el índice: " + ex.Message; }
        }

        /// <summary>Nada se borra a la primera: la carpeta va a `_papelera` y se purga a los N días.</summary>
        void AlaPapelera(Grabacion g, string porque)
        {
            try
            {
                string pap = Path.Combine(carpeta, Papelera);
                Directory.CreateDirectory(pap);
                string dir = g.Carpeta;
                if (Directory.Exists(dir))
                {
                    string destino = Path.Combine(pap, Path.GetFileName(dir));
                    if (Directory.Exists(destino)) destino += "-" + DateTime.Now.ToString("HHmmss");
                    Directory.Move(dir, destino);
                    try { File.WriteAllText(Path.Combine(destino, "papelera.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} · {g.Reunion} · {porque}", Encoding.UTF8); } catch { }
                }
            }
            catch (Exception ex) { log.Aviso($"Grabador: no pude mover «{g.Reunion}» a la papelera: {ex.Message}"); }
            lock (candado) lista.Remove(g);
        }

        void PurgarPapelera()
        {
            string pap = Path.Combine(carpeta, Papelera);
            if (!Directory.Exists(pap)) return;
            foreach (var d in Directory.GetDirectories(pap))
                if ((DateTime.Now - Directory.GetLastWriteTime(d)).TotalDays > Math.Max(1, cfg.DiasPapelera))
                    try { Directory.Delete(d, true); log.Debug("Grabador: purgué de la papelera " + Path.GetFileName(d)); } catch { }
        }

        /// <summary>Un loopcap que quedó vivo de otra corrida de la app (v1 no sabía morirse con el padre): se le manda STOP.</summary>
        void CortarLoopcapsHuerfanos()
        {
            foreach (var p in Process.GetProcessesByName("loopcap"))
            {
                try
                {
                    string cmd = LineaDeComando(p.Id);
                    if (cmd.IndexOf(carpeta, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var m = Regex.Match(cmd, "-stop\\s+\"([^\"]+)\"");
                    if (!m.Success) continue;
                    if (stopVivo.Length > 0 && string.Equals(m.Groups[1].Value, stopVivo, StringComparison.OrdinalIgnoreCase)) continue;
                    File.WriteAllText(m.Groups[1].Value, "");
                    log.Aviso($"Grabador: había un loopcap huérfano (pid {p.Id}) de otra corrida; le mandé STOP");
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        static string LineaDeComando(int pid)
        {
            try
            {
                using (var s = new System.Management.ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid))
                    foreach (System.Management.ManagementObject o in s.Get()) return o["CommandLine"]?.ToString() ?? "";
            }
            catch { }
            return "";
        }

        // ================================================================== utilidades

        public string ProblemaActual { get; private set; } = "";
        void Problema(string p) { ProblemaActual = p; Publicar(true); }

        Grabacion Buscar(string id) { lock (candado) return lista.FirstOrDefault(x => x.Id == id); }

        void Poner(Grabacion g, string estado, string etapa, double progreso)
        {
            lock (candado) { g.Estado = estado; g.Etapa = etapa; g.Progreso = progreso; }
            GuardarIndice();
            Publicar(true);
        }

        void Progresar(Grabacion g, double f, string etapa = null)
        {
            lock (candado) { g.Progreso = f; if (etapa != null) g.Etapa = etapa; }
            Publicar(false);
        }

        void Fallar(Grabacion g, string motivo)
        {
            lock (candado) { g.Estado = EstadoGrab.Fallo; g.Detalle = motivo; g.Etapa = ""; g.Progreso = -1; g.EnPausa = false; }
            Anotar(g, "✗ " + motivo);
            log.Error($"Grabador: «{g.Reunion}» · {motivo}");
            GuardarIndice();
            Publicar(true);
        }

        void Anotar(Grabacion g, string texto)
        {
            if (g == null) return;
            lock (candado)
            {
                g.Bitacora = g.Bitacora ?? new List<string>();
                g.Bitacora.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + texto);
                if (g.Bitacora.Count > 200) g.Bitacora.RemoveRange(0, g.Bitacora.Count - 200);
            }
        }

        /// <summary>Arma la foto para la UI. Throttle a 4 por segundo salvo cambios de estado (forzar).</summary>
        void Publicar(bool forzar)
        {
            if (!forzar && (DateTime.Now - ultimaPublicacion).TotalMilliseconds < 250) return;
            ultimaPublicacion = DateTime.Now;
            FotoGrabador f;
            lock (candado)
            {
                f = new FotoGrabador
                {
                    Todas = lista.OrderByDescending(x => x.Desde).Select(x => x.Clonar()).ToArray(),
                    EnCola = lista.Count(x => EstadoGrab.EnCola(x.Estado)),
                    Activo = cfg.Activo,
                    Problema = ProblemaActual,
                    Cerrando = cerrando,
                };
                var v = vivo;
                if (v != null)
                    f.Vivo = new EnVivo
                    {
                        Id = v.Id, Reunion = v.Reunion, Desde = v.Desde, Segundos = (DateTime.Now - v.Desde).TotalSeconds, NivelDb = v.NivelDb,
                        PicoTotalDb = v.PicoTotalDb, MB = v.MB, DiscoLibreMB = v.DiscoLibreMB, Pista = v.Pista, Problema = v.Problema,
                        Tramo = v.Tramo, SegundosPerdidos = v.SegundosPerdidos, CierraEn = v.CierraEn, Actualizado = v.Actualizado,
                    };
            }
            Volatile.Write(ref foto, f);
            try { Cambio?.Invoke(); } catch { }
        }

        /// <summary>Reemplazo atómico: File.Replace si el destino existe (mismo volumen), Move si no.</summary>
        static void Reemplazar(string tmp, string destino)
        {
            if (File.Exists(destino)) File.Replace(tmp, destino, null);
            else File.Move(tmp, destino);
        }

        static long MbLibres(string ruta)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(ruta))).AvailableFreeSpace >> 20; } catch { return -1; }
        }

        // suspender / reanudar un proceso entero (whisper corre todo dentro de python)
        [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr h);
        [DllImport("ntdll.dll")] static extern int NtResumeProcess(IntPtr h);
        static bool Suspender(Process p) { try { return NtSuspendProcess(p.Handle) >= 0; } catch { return false; } }
        static bool Reanudar(Process p) { try { return NtResumeProcess(p.Handle) >= 0; } catch { return false; } }

        public static string Fmt(double segundos)
        {
            if (segundos < 0 || double.IsNaN(segundos)) return "?";
            var t = TimeSpan.FromSeconds(segundos);
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h{t.Minutes:00}";
            if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m{t.Seconds:00}";
            return $"{t.Seconds}s";
        }

        static string Corto(string s)
        {
            s = (s ?? "").Trim();
            return s.Length > 30 ? s.Substring(0, 29) + "…" : s;
        }
    }
}
