using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace TeamsTools
{
    /// <summary>
    /// `TeamsTools.exe --probar-grabador [--segundos N] [--carpeta DIR] [--sin-whisper] [--matar-loopcap]`
    ///
    /// El grabador de verdad, de punta a punta, sin interfaz y en una carpeta aparte (no toca las grabaciones
    /// reales): simula una llamada, graba con loopcap, simula que saliste (gracia corta), y deja correr el
    /// pipeline con todas sus verificaciones. Imprime una tarjeta final con cada chequeo. Con --matar-loopcap
    /// mata loopcap a mitad de la grabación para probar que el vigía retoma en un tramo nuevo.
    /// </summary>
    internal static class PruebaGrabador
    {
        // los pasteles de la consola viven en un solo lugar (Pastel): acá solo se nombran
        static string Rosa => Pastel.Rosa;
        static string Salvia => Pastel.Salvia;
        static string Cyan => Pastel.Cyan;
        static string Durazno => Pastel.Durazno;
        static string Apagado => Pastel.Apagado;
        static string Fin => Pastel.Fin;

        public static int Correr(Logger log, string carpeta, int segundos, bool sinWhisper, bool matarLoopcap)
        {
            Directory.CreateDirectory(carpeta);
            var reloj = Stopwatch.StartNew();
            var chequeos = new System.Collections.Generic.List<(string que, bool ok, string det)>();
            void Chequeo(string q, bool ok, string d) { chequeos.Add((q, ok, d)); Console.WriteLine($"  {(ok ? Salvia + "✓" : Rosa + "✗")}{Fin} {q,-52} {Apagado}{d}{Fin}"); }

            var g = new Grabador(new Config(), log, carpeta) { PararAntesDeWhisper = sinWhisper };
            var c = g.Cfg;
            c.Activo = true; c.MinimoSegundos = 5; c.SoloConGente = false; c.Resumir = false; c.BorrarAudio = true; c.GraciaCortaSegundos = 4;
            c.Guardar();
            g.Iniciar();
            Console.WriteLine($"\n{Cyan}grabador · prueba de punta a punta{Fin} {Apagado}({segundos} s en {carpeta}){Fin}\n");

            // --- 1) la llamada empieza
            var L = new Lectura { HayLlamada = true, Reunion = "prueba del grabador", Otros = 1, TeamsCorriendo = true };
            var t0 = DateTime.Now;
            bool mate = false;
            long lecturasPulso = 0; int pistasPulso = 0;
            g.MicDeTeams(false);                                   // Teams dice: micrófono abierto…
            while ((DateTime.Now - t0).TotalSeconds < segundos)
            {
                g.Mirar(L, true, false, false);
                Thread.Sleep(1000);
                double pasaron = (DateTime.Now - t0).TotalSeconds;
                if (pasaron >= segundos * 0.3 && pasaron < segundos * 0.6) g.MicDeTeams(true);   // …en silencio un rato…
                else if (pasaron >= segundos * 0.6) g.MicDeTeams(false);                          // …y abierto de nuevo
                var pu = g.Pulso;
                if (pu != null) { lecturasPulso = Math.Max(lecturasPulso, pu.Escritas); pistasPulso = Math.Max(pistasPulso, pu.Pistas.Length); }
                var v = g.Foto.Vivo;
                if (v != null)
                    Console.Write($"\r  {Rosa}●{Fin} {(DateTime.Now - t0).TotalSeconds,4:0} s · nivel {v.NivelDb,6:0.0} dB · {v.MB,6:0.0} MB · tramo {v.Tramo} · {(v.Sano ? Salvia + "sano" : Durazno + (v.Problema.Length > 0 ? v.Problema : "sin reporte aún"))}{Fin}      ");
                if (matarLoopcap && !mate && (DateTime.Now - t0).TotalSeconds >= segundos / 2.0)
                {
                    foreach (var p in Process.GetProcessesByName("loopcap"))
                        try { if (LineaDe(p.Id).IndexOf(carpeta, StringComparison.OrdinalIgnoreCase) >= 0) { p.Kill(); mate = true; } } catch { } finally { p.Dispose(); }
                    Console.WriteLine($"\n  {Durazno}✂ maté loopcap a mitad de la grabación{Fin}");
                }
            }
            Console.WriteLine();
            var vivo = g.Foto.Vivo;
            Chequeo("grabó con estado en vivo", vivo != null && vivo.Actualizado > t0, vivo != null ? $"{vivo.MB:0.0} MB · leído hace {(DateTime.Now - vivo.Actualizado).TotalSeconds:0} s" : "sin foto en vivo");
            Chequeo("pulso en vivo de loopcap (-levels)", lecturasPulso >= (segundos - 3) * 12, $"{lecturasPulso} lecturas · {pistasPulso} pista/s · ≈ {lecturasPulso / Math.Max(1.0, segundos - 1):0} por segundo");
            if (matarLoopcap) Chequeo("el vigía retomó en un tramo nuevo", vivo != null && vivo.Tramo >= 2, vivo != null ? $"tramo {vivo.Tramo}" : "");

            // --- 2) saliste: la presencia lo confirma → gracia corta
            var tCorte = DateTime.Now;
            var sinLlamada = new Lectura { HayLlamada = false, TeamsCorriendo = true };
            while (g.Foto.Vivo != null && (DateTime.Now - tCorte).TotalSeconds < 30) { g.Mirar(sinLlamada, false, false, true); Thread.Sleep(250); }
            double tardo = (DateTime.Now - tCorte).TotalSeconds;
            Chequeo("cortó con gracia corta al confirmar la salida", g.Foto.Vivo == null && tardo <= c.GraciaCortaSegundos + 3, $"{tardo:0.0} s (gracia {c.GraciaCortaSegundos} s)");

            // --- 3) el pipeline
            string final = "";
            var tp = DateTime.Now;
            Grabacion gr = null;
            while ((DateTime.Now - tp).TotalMinutes < 30)
            {
                gr = g.Foto.Todas.FirstOrDefault(x => x.Reunion == "prueba del grabador");
                if (gr != null)
                {
                    Console.Write($"\r  {Cyan}◌{Fin} {gr.Estado,-15} {(gr.Progreso >= 0 ? gr.Progreso.ToString("P0") : ""),5} {Apagado}{gr.Etapa}{Fin}                    ");
                    if (gr.Estado == EstadoGrab.Lista || gr.Estado == EstadoGrab.Fallo || gr.Estado == EstadoGrab.Descartada) { final = gr.Estado; break; }
                    if (sinWhisper && gr.Estado == EstadoGrab.Transcribiendo && gr.TieneArchivo) { final = gr.Estado; break; }
                }
                else if ((DateTime.Now - tp).TotalSeconds > 15) break;
                Thread.Sleep(300);
            }
            Console.WriteLine("\n");
            if (gr == null) { Chequeo("la grabación quedó en el índice", false, "no aparece"); return Tarjeta(chequeos, reloj); }
            string dir = gr.Carpeta;
            Chequeo("audio a salvo tras el corte", gr.SegundosAudio >= segundos - 3, $"{Grabador.Fmt(gr.SegundosAudio)} de {segundos} s de llamada");
            Chequeo("archivado en .opus", gr.TieneArchivo && File.Exists(gr.Archivo), gr.TieneArchivo ? $"{gr.BytesArchivo / 1024.0:0} KB · {Path.GetFileName(gr.Archivo)}" : "sin archivo");
            double dOpus = gr.TieneArchivo ? Procesos.Duracion(gr.Archivo) : -1;
            Chequeo(".opus dura lo que tiene que durar", Math.Abs(dOpus - gr.SegundosAudio) <= Math.Max(2, gr.SegundosAudio * 0.01), $"ffprobe {dOpus:0.0} s vs {gr.SegundosAudio:0.0} s");
            var marcasMic = File.Exists(Path.Combine(dir, "mic-teams.jsonl")) ? File.ReadAllLines(Path.Combine(dir, "mic-teams.jsonl")).Length : 0;
            Chequeo("el mute de Teams quedó registrado con su hora", marcasMic >= 3, $"{marcasMic} cambios en mic-teams.jsonl (abierto → silencio → abierto)");
            var wavs = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.wav") : new string[0];
            foreach (var w in wavs)
            {
                var i = Wav.Leer(w);
                Chequeo("encabezado honesto · " + Path.GetFileName(w), i.Valido && !i.Miente, $"declara {i.SegundosDeclarados:0.0} s · tiene {i.Segundos:0.0} s");
            }
            if (sinWhisper) Chequeo("se detuvo antes de whisper (pedido)", final == EstadoGrab.Transcribiendo, final);
            else
            {
                Chequeo("el transcriptor leyó TODO el audio", gr.SegundosTranscriptos >= gr.SegundosAudio - Math.Max(3, gr.SegundosAudio * 0.02), $"{gr.SegundosTranscriptos:0.0} de {gr.SegundosAudio:0.0} s · {gr.Palabras} palabras");
                Chequeo("terminó lista", final == EstadoGrab.Lista, final + (gr.Detalle.Length > 0 ? " · " + gr.Detalle : ""));
                Chequeo("borró los WAV SOLO con el .opus verificado", wavs.Length == 0 && gr.TieneArchivo && File.Exists(gr.Archivo), $"{wavs.Length} wav · opus {(File.Exists(gr.Archivo) ? "presente" : "AUSENTE")}");
            }
            Console.WriteLine($"\n  {Apagado}bitácora:{Fin}");
            foreach (var b in gr.Bitacora) Console.WriteLine($"    {Apagado}{b}{Fin}");
            g.Detener();
            Disco.Vaciar();
            return Tarjeta(chequeos, reloj);
        }

        /// <summary>
        /// `--probar-grabador --archivo X.wav [--idioma en] [--trozo 60] [--claves a,b,c]`: el pipeline entero (energía,
        /// .opus, whisper POR TROZOS, unión, verificación, limpieza) sobre un audio de verdad conocida. Mide qué fracción
        /// de las palabras clave aparece EN ORDEN en la transcripción unida.
        /// </summary>
        public static int CorrerConArchivo(Logger log, string carpeta, string archivo, string idioma, int trozo, string[] claves, string calidad,
            string transcriptor = "", string fallaEn = "")
        {
            if (transcriptor.Length > 0) Grabador.Transcriptor = transcriptor;
            if (fallaEn.Length > 0) Environment.SetEnvironmentVariable("FALSO_FALLA_EN", fallaEn);
            bool reintentado = false;
            var reloj = Stopwatch.StartNew();
            var chequeos = new System.Collections.Generic.List<(string que, bool ok, string det)>();
            void Chequeo(string q, bool ok, string d) { chequeos.Add((q, ok, d)); Console.WriteLine($"  {(ok ? Salvia + "✓" : Rosa + "✗")}{Fin} {q,-52} {Apagado}{d}{Fin}"); }
            string id = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string dir = Path.Combine(carpeta, "grabaciones", id);
            Directory.CreateDirectory(dir);
            File.Copy(archivo, Path.Combine(dir, "audio__Prueba_TTS.wav"), true);
            File.WriteAllText(Path.Combine(dir, "reunion.txt"), "prueba por trozos", new UTF8Encoding(false));
            double esperado = Wav.Leer(archivo).Segundos;
            Console.WriteLine($"\n{Cyan}grabador · pipeline por trozos{Fin} {Apagado}({Grabador.Fmt(esperado)} de audio · trozos de {trozo} s · {idioma} · {calidad}){Fin}\n");

            var g = new Grabador(new Config(), log, carpeta);   // la importa como huérfana y la pone en cola
            var c = g.Cfg;
            c.Activo = false; c.Resumir = false; c.BorrarAudio = true; c.Idioma = idioma; c.TrozoSegundos = trozo; c.Calidad = calidad; c.PausarEnLlamada = false;
            if (transcriptor.Length > 0) c.Motor = "whisper";   // el transcriptor falso habla el contrato de whisper
            c.Guardar();
            g.Iniciar();
            Grabacion gr = null;
            while (reloj.Elapsed.TotalMinutes < 40)
            {
                gr = g.Foto.Todas.FirstOrDefault(x => x.Id == id);
                if (gr != null)
                {
                    Console.Write($"\r  {Cyan}◌{Fin} {gr.Estado,-15} {(gr.Progreso >= 0 ? gr.Progreso.ToString("P0") : ""),5} {Apagado}{gr.Etapa}{Fin}                         ");
                    // con una falla simulada: el primer «falló» se reintenta (tiene que retomar, no rehacer)
                    if (gr.Estado == EstadoGrab.Fallo && fallaEn.Length > 0 && !reintentado)
                    {
                        reintentado = true;
                        Console.WriteLine($"\n  {Durazno}✂ falló a propósito en {fallaEn} → reintento{Fin}");
                        g.Reintentar(id);
                        Thread.Sleep(600);
                        continue;
                    }
                    if (gr.Estado == EstadoGrab.Lista || gr.Estado == EstadoGrab.Fallo || gr.Estado == EstadoGrab.Descartada) break;
                }
                Thread.Sleep(400);
            }
            Console.WriteLine("\n");
            if (gr == null) { Chequeo("la prueba entró al pipeline", false, "no aparece"); return Tarjeta(chequeos, reloj); }
            Chequeo("terminó lista", gr.Estado == EstadoGrab.Lista, gr.Estado + " · " + gr.Detalle);
            Chequeo("archivó en .opus", gr.TieneArchivo && File.Exists(gr.Archivo), $"{gr.BytesArchivo / 1024.0:0} KB");
            Chequeo("el transcriptor leyó TODO (unión de trozos)", gr.SegundosTranscriptos >= esperado - Math.Max(3, esperado * 0.02), $"{gr.SegundosTranscriptos:0.0} de {esperado:0.0} s");
            int trozos = gr.Bitacora.Count(b => b.Contains("verificado ("));
            Chequeo("se transcribió en varios trozos", trozos >= 2, $"{trozos} trozos verificados");
            var srt = Path.Combine(dir, "audio16.srt");
            bool srtOk = File.Exists(srt) && File.ReadAllText(srt).Contains(" --> ");
            Chequeo("srt unido con tiempos de la reunión", srtOk, srtOk ? File.ReadAllLines(srt).Count(l => l.Contains(" --> ")) + " subtítulos" : "falta");
            string texto = gr.Texto.ToLowerInvariant();
            if (claves.Length > 0)
            {
                int pos = 0, enOrden = 0, halladas = 0;
                foreach (var k in claves)
                {
                    int i = texto.IndexOf(k.ToLowerInvariant(), StringComparison.Ordinal);
                    if (i >= 0) halladas++;
                    int j = texto.IndexOf(k.ToLowerInvariant(), pos, StringComparison.Ordinal);
                    if (j >= 0) { enOrden++; pos = j; }
                }
                Chequeo("palabras clave reconocidas", halladas >= claves.Length * 0.85, $"{halladas}/{claves.Length} ({halladas * 100 / claves.Length} %)");
                Chequeo("y en el orden correcto", enOrden >= claves.Length * 0.85, $"{enOrden}/{claves.Length} en orden");
            }
            if (fallaEn.Length > 0)
            {
                bool retomo = gr.Bitacora.Any(b => b.Contains("ya estaban hechos (retomo)"));
                Chequeo("el reintento retomó sin rehacer lo verificado", retomo, gr.Bitacora.FirstOrDefault(b => b.Contains("retomo")) ?? "no retomó");
            }
            // cada segmento del falso dice «tNN-SSS»: su tiempo global tiene que ser inicio del trozo NN + SSS
            var json = Json.LeerObjeto(Path.Combine(dir, "audio16.json"));
            if (json != null && transcriptor.Length > 0)
            {
                var segs = Json.Lista(json, "segments");
                var inicioTrozo = new System.Collections.Generic.Dictionary<int, double>();
                double previo = -1; int malos = 0, desordenados = 0;
                foreach (var s in segs)
                {
                    double ini = Convert.ToDouble(s["start"], System.Globalization.CultureInfo.InvariantCulture);
                    var m = System.Text.RegularExpressions.Regex.Match(Json.S(s, "text"), @"^t(\d+)-(\d+)$");
                    if (!m.Success) { malos++; continue; }
                    int n = int.Parse(m.Groups[1].Value); double local = int.Parse(m.Groups[2].Value);
                    double offset = ini - local;
                    if (!inicioTrozo.ContainsKey(n)) inicioTrozo[n] = offset;
                    else if (Math.Abs(inicioTrozo[n] - offset) > 0.6) malos++;
                    if (ini < previo - 0.001) desordenados++;
                    previo = ini;
                }
                var offs = inicioTrozo.OrderBy(k => k.Key).Select(k => k.Value).ToList();
                bool crecientes = offs.Zip(offs.Skip(1), (a, b) => b > a).All(x => x);
                double ultimoFin = segs.Count > 0 ? Convert.ToDouble(segs[segs.Count - 1]["end"], System.Globalization.CultureInfo.InvariantCulture) : 0;
                Chequeo("cada segmento en su lugar del reloj", malos == 0 && desordenados == 0 && crecientes,
                    $"{segs.Count} segmentos · {inicioTrozo.Count} trozos · inicios {string.Join(" / ", offs.Select(o => o.ToString("0.0")))} s · {malos} fuera de lugar");
                Chequeo("la unión cubre el audio entero", Math.Abs(ultimoFin - esperado) <= 5.5, $"último segmento termina en {ultimoFin:0.0} de {esperado:0.0} s");
            }
            Chequeo("WAV borrados solo con .opus verificado", Directory.GetFiles(dir, "*.wav").Length == 0 && gr.TieneArchivo, $"{Directory.GetFiles(dir, "*.wav").Length} wav · opus presente");
            Chequeo("carpeta de trozos limpiada", !Directory.Exists(Path.Combine(dir, "trozos")), "");
            Console.WriteLine($"\n  {Apagado}bitácora:{Fin}");
            foreach (var b in gr.Bitacora) Console.WriteLine($"    {Apagado}{b}{Fin}");
            Console.WriteLine($"\n  {Apagado}texto (primeros 400 caracteres):{Fin} {gr.Texto.Substring(0, Math.Min(400, gr.Texto.Length))}");
            g.Detener();
            Disco.Vaciar();
            return Tarjeta(chequeos, reloj);
        }

        /// <summary>
        /// `--probar-grabador --mezcla-mic`: la MEZCLA con tu micrófono, sin hardware. Arma una grabación sintética —una
        /// salida con un tono de 440 Hz y tu micrófono con 880 Hz que se suma a los 2 s— y un registro de Teams que te
        /// tiene en silencio de 5 a 9 s. Corre el pipeline de verdad (hasta antes de whisper) y mide, tono por tono y
        /// de a 100 ms (Goertzel), que la salida esté siempre y tu micrófono NO esté mientras estabas en silencio.
        /// </summary>
        public static int CorrerMezclaMic(Logger log, string carpeta)
        {
            var reloj = Stopwatch.StartNew();
            var chequeos = new System.Collections.Generic.List<(string que, bool ok, string det)>();
            void Chequeo(string q, bool ok, string d) { chequeos.Add((q, ok, d)); Console.WriteLine($"  {(ok ? Salvia + "✓" : Rosa + "✗")}{Fin} {q,-58} {Apagado}{d}{Fin}"); }
            string id = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string dir = Path.Combine(carpeta, "grabaciones", id);
            Directory.CreateDirectory(dir);
            const int sr = 48000;
            const double dur = 12, micDesde = 2, silDesde = 5, silHasta = 9, corrimiento = 0.2;
            var t0 = DateTime.Now.AddSeconds(-40);                  // la «grabación» empezó hace 40 s
            EscribirTono(Path.Combine(dir, "audio__Salida_Prueba.wav"), sr, dur, 440, 0.1, 0);
            EscribirTono(Path.Combine(dir, "audio.mic__Mic_Prueba.wav"), sr, dur, 880, 0.1, micDesde);
            File.WriteAllText(Path.Combine(dir, "estado.json"), "{\"version\":\"3.0\",\"state\":\"done\",\"started\":\"" +
                t0.ToString("o", System.Globalization.CultureInfo.InvariantCulture) + "\",\"devices\":[]}", new UTF8Encoding(false));
            string Marca(double s, bool sil) => "{\"t\":\"" + t0.AddSeconds(s).ToString("o", System.Globalization.CultureInfo.InvariantCulture) +
                                                "\",\"silenciado\":" + (sil ? "true" : "false") + "}\n";
            File.WriteAllText(Path.Combine(dir, "mic-teams.jsonl"), Marca(1, false) + Marca(silDesde, true) + Marca(silHasta, false), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "reunion.txt"), "prueba de mezcla con micrófono", new UTF8Encoding(false));
            Console.WriteLine($"\n{Cyan}grabador · mezcla con tu micrófono{Fin} {Apagado}(salida 440 Hz · mic 880 Hz desde {micDesde} s · silencio de Teams {silDesde}–{silHasta} s){Fin}\n");

            var g = new Grabador(new Config(), log, carpeta) { PararAntesDeWhisper = true };
            var c = g.Cfg;
            c.Activo = false; c.Resumir = false; c.BorrarAudio = false; c.PausarEnLlamada = false;
            c.Guardar();
            g.Iniciar();
            Grabacion gr = null;
            while (reloj.Elapsed.TotalSeconds < 120)
            {
                gr = g.Foto.Todas.FirstOrDefault(x => x.Id == id);
                if (gr != null && ((gr.Estado == EstadoGrab.Transcribiendo && gr.TieneArchivo) || gr.Estado == EstadoGrab.Fallo)) break;
                Thread.Sleep(300);
            }
            if (gr == null) { Chequeo("la prueba entró al pipeline", false, "no aparece"); return Tarjeta(chequeos, reloj); }
            Chequeo("archivó la mezcla", gr.TieneArchivo, gr.Estado + " · " + gr.Detalle);
            Chequeo("las pistas: la salida Y tu micrófono", gr.Pistas.Contains("mic:") && gr.Pistas.Contains("Salida"), gr.Pistas);
            string wav16 = Path.Combine(dir, "audio16.wav");
            if (!File.Exists(wav16)) { Chequeo("audio para el transcriptor", false, "falta audio16.wav"); return Tarjeta(chequeos, reloj); }
            var x = LeerPcm16(wav16, out int sr16);
            int marco = sr16 / 10;                                   // 100 ms
            int marcos = x.Length / marco;
            var a440 = new double[marcos]; var a880 = new double[marcos];
            for (int k = 0; k < marcos; k++) { a440[k] = Goertzel(x, k * marco, marco, 440, sr16); a880[k] = Goertzel(x, k * marco, marco, 880, sr16); }
            bool Cerca(double t, params double[] bordes) => bordes.Any(b => Math.Abs(t - b) < 0.3);
            double silA = silDesde - corrimiento, silB = silHasta - corrimiento;
            var enSil = Enumerable.Range(0, marcos).Where(k => { double t = k * 0.1 + 0.05; return t > silA && t < silB && !Cerca(t, silA, silB); }).ToList();
            var conMic = Enumerable.Range(0, marcos).Where(k => { double t = k * 0.1 + 0.05; return t > micDesde && t < dur && (t < silA || t > silB) && !Cerca(t, micDesde, silA, silB, dur); }).ToList();
            var todos = Enumerable.Range(0, marcos).Where(k => { double t = k * 0.1 + 0.05; return t < dur - 0.3; }).ToList();
            double min440 = todos.Min(k => a440[k]), max880Sil = enSil.Max(k => a880[k]), min880Mic = conMic.Min(k => a880[k]);
            Chequeo("la salida (440 Hz) suena en toda la mezcla", min440 > 0.03, $"amplitud mínima {min440:0.000} (el tono es 0,100)");
            Chequeo("tu micrófono (880 Hz) entra a la mezcla", min880Mic > 0.03, $"amplitud mínima fuera del silencio {min880Mic:0.000}");
            Chequeo("en silencio de Teams, tu micrófono NO está", max880Sil < 0.003,
                $"amplitud máxima {max880Sil:0.0000} entre {silA:0.0} y {silB:0.0} s ({20 * Math.Log10(Math.Max(max880Sil, 1e-9) / 0.1):0} dB bajo el tono)");
            Chequeo("la bitácora lo cuenta", gr.Bitacora.Any(b => b.Contains("en silencio de Teams quedan fuera")),
                gr.Bitacora.FirstOrDefault(b => b.Contains("tu micrófono")) ?? "no dice nada");
            double pico = x.Max(v => Math.Abs((double)v)) / 32768.0;
            Chequeo("la suma no satura (limitador)", pico < 0.99, $"pico {pico:0.000}");

            // tu micrófono y la salida, aparte y en la MISMA línea de tiempo (para «Yo» al separar las voces)
            string mic16 = Path.Combine(dir, "mic16.wav"), sal16 = Path.Combine(dir, "salida16.wav");
            bool hay = File.Exists(mic16) && File.Exists(sal16);
            Chequeo("tu micrófono y la salida quedaron aparte", hay, hay ? "mic16.wav + salida16.wav" : "faltan");
            if (hay)
            {
                var xm = LeerPcm16(mic16, out int srm); var xs = LeerPcm16(sal16, out int srs);
                Chequeo("las tres pistas miden lo mismo", Math.Abs(xm.Length - x.Length) <= srm / 10 && Math.Abs(xs.Length - x.Length) <= srs / 10,
                    $"mezcla {x.Length / (double)sr16:0.00} s · mic {xm.Length / (double)srm:0.00} s · salida {xs.Length / (double)srs:0.00} s");
                int mm = xm.Length / marco;
                double micSal = Enumerable.Range(0, Math.Min(marcos, mm)).Max(k => Goertzel(xs, k * marco, marco, 880, srs));
                double micMic = conMic.Min(k => Goertzel(xm, k * marco, marco, 880, srm));
                double micSil = enSil.Max(k => Goertzel(xm, k * marco, marco, 880, srm));
                double salMic = Enumerable.Range(0, Math.Min(marcos, mm)).Where(k => k * 0.1 < dur - 0.3).Min(k => Goertzel(xm, k * marco, marco, 440, srm));
                Chequeo("en mic16 está tu voz (880 Hz) y en salida16 no", micMic > 0.03 && micSal < 0.003, $"mic {micMic:0.000} · salida {micSal:0.0000}");
                Chequeo("mic16 también respeta el silencio de Teams", micSil < 0.003, $"880 Hz en el silencio: {micSil:0.0000}");
                Chequeo("en mic16 no se filtra la salida (440 Hz)", salMic < 0.003, $"440 Hz en mic16: {salMic:0.0000}");
            }
            g.Detener();
            Disco.Vaciar();
            return Tarjeta(chequeos, reloj);
        }

        /// <summary>WAV PCM 16 bit mono con un tono de <paramref name="amp"/> desde <paramref name="desde"/> s (antes, silencio).</summary>
        static void EscribirTono(string ruta, int sr, double seg, double hz, double amp, double desde)
        {
            int n = (int)(sr * seg);
            using (var bw = new BinaryWriter(File.Create(ruta)))
            {
                bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + n * 2); bw.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                bw.Write(16); bw.Write((short)1); bw.Write((short)1); bw.Write(sr); bw.Write(sr * 2); bw.Write((short)2); bw.Write((short)16);
                bw.Write(Encoding.ASCII.GetBytes("data")); bw.Write(n * 2);
                for (int i = 0; i < n; i++)
                    bw.Write((short)(i < desde * sr ? 0 : Math.Round(amp * 32767 * Math.Sin(2 * Math.PI * hz * i / sr))));
            }
        }

        static short[] LeerPcm16(string ruta, out int sr)
        {
            var b = File.ReadAllBytes(ruta);
            sr = BitConverter.ToInt32(b, 24);
            int i = 12;
            while (i + 8 <= b.Length && Encoding.ASCII.GetString(b, i, 4) != "data") i += 8 + BitConverter.ToInt32(b, i + 4);
            int ini = i + 8, largo = Math.Min(BitConverter.ToInt32(b, i + 4), b.Length - ini);
            var x = new short[largo / 2];
            Buffer.BlockCopy(b, ini, x, 0, x.Length * 2);
            return x;
        }

        /// <summary>Amplitud de UNA frecuencia en un marco (algoritmo de Goertzel): O(n), sin FFT.</summary>
        static double Goertzel(short[] x, int ini, int n, double hz, int sr)
        {
            double w = 2 * Math.PI * hz / sr, coef = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            for (int i = 0; i < n; i++) { double s0 = x[ini + i] / 32768.0 + coef * s1 - s2; s2 = s1; s1 = s0; }
            double pot = s1 * s1 + s2 * s2 - coef * s1 * s2;
            return 2 * Math.Sqrt(Math.Max(0, pot)) / n;
        }

        static int Tarjeta(System.Collections.Generic.List<(string que, bool ok, string det)> ch, Stopwatch reloj)
        {
            int ok = ch.Count(x => x.ok), n = ch.Count;
            string linea = $"  {ok}/{n} chequeos en verde · {reloj.Elapsed.TotalSeconds:0} s";
            Console.WriteLine($"\n  {Cyan}╭─ grabador · resultado {new string('─', 36)}╮{Fin}");
            Console.WriteLine($"  {Cyan}│{Fin}{(ok == n ? Salvia : Rosa)}{linea.PadRight(59)}{Fin}{Cyan}│{Fin}");
            Console.WriteLine($"  {Cyan}╰{new string('─', 59)}╯{Fin}\n");
            return ok == n ? 0 : 1;
        }

        static string LineaDe(int pid)
        {
            try
            {
                using (var s = new System.Management.ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid))
                    foreach (System.Management.ManagementObject o in s.Get()) return o["CommandLine"]?.ToString() ?? "";
            }
            catch { }
            return "";
        }
    }
}
