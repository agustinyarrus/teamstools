using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TeamsTools
{
    /// <summary>
    /// Prueba del lector del log nativo contra una carpeta de logs falsa. Cubre lo que de verdad se rompió:
    /// releer el archivo entero en cada pasada (duplicaba eventos), la línea escrita a medias, la rotación
    /// de archivo, y el huso mentiroso del log (los dígitos son UTC aunque diga -03:00).
    /// Se corre con: TeamsTools.exe --probar-presencia-log
    /// </summary>
    internal static class PruebaPresenciaLog
    {
        static int ok, mal;
        static StringBuilder sb;

        public static string Correr(Logger log)
        {
            sb = new StringBuilder();
            ok = mal = 0;
            string carpeta = Path.Combine(Path.GetTempPath(), "tal-prueba-log-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(carpeta);
            try
            {
                string f1 = Path.Combine(carpeta, "MSTeams_2026-01-01_10-00-00.01.log");

                // Todo en HORA LOCAL y dentro del día de hoy: los minutos por estado se cuentan desde medianoche,
                // y si la prueba corre a las 00:30 no puede irse al día de ayer. Se reparten seis hitos entre el
                // arranque y ahora. El log se escribe convirtiendo cada hito a UTC, como hace Teams de verdad.
                var arranque = DateTime.Now.AddMinutes(-40);
                if (arranque.Date != DateTime.Today) arranque = DateTime.Today.AddSeconds(1);
                double paso = Math.Max(1, (DateTime.Now - arranque).TotalSeconds / 7.0);
                Func<int, DateTime> hito = i => arranque.AddSeconds(paso * i);
                var t1 = hito(0);
                var t2 = hito(1);
                var t3 = hito(2);
                Escribir(f1, false,
                    Ruido(t1.AddMinutes(-5)),
                    Nube(t1, "Available"), Insignia(t1, "available", "Disponible"), Insignia(t1, "available", "Disponible"),
                    Ruido(t1.AddMinutes(1)),
                    Nube(t2, "Away"), Insignia(t2, "away", "Ausente"), Nube(t2, "Away"), Insignia(t2, "away", "Ausente"),
                    Nube(t3, "Available"), Insignia(t3, "available", "Disponible"));
                Tocar(f1, t3);

                var lec = new LectorPresenciaLog(log, carpeta);
                lec.Refrescar();
                Chequear("primera lectura: 3 transiciones y no 11", 3, lec.Eventos.Count);
                Chequear("el huso: devuelve la hora local, no los dígitos del log",
                         t1.ToString("dd/MM HH:mm:ss"), lec.Eventos[0].Cuando.ToString("dd/MM HH:mm:ss"));
                Chequear("y no se comió el -03:00 como si fuera local",
                         TimeZoneInfo.Local.BaseUtcOffset == TimeSpan.Zero,
                         lec.Eventos[0].Cuando.ToString("HH:mm") == t1.ToUniversalTime().ToString("HH:mm"));
                Chequear("el último es Disponible", "Disponible", lec.Ultimo?.Estado);

                // ── 🚨 el bug: refrescar sin novedades NO puede sumar nada ─────────────────────────────
                for (int i = 0; i < 5; i++) lec.Refrescar();
                Chequear("5 refrescos sin cambios: sigue en 3", 3, lec.Eventos.Count);

                // ── algo nuevo de verdad: una transición más ───────────────────────────────────────────
                var t4 = hito(3);
                Escribir(f1, true, Nube(t4, "Away"), Insignia(t4, "away", "Ausente"));
                Tocar(f1, t4);
                lec.Refrescar();
                Chequear("una transición nueva: 4", 4, lec.Eventos.Count);
                Chequear("y el último pasó a Ausente", "Ausente", lec.Ultimo?.Estado);
                lec.Refrescar(); lec.Refrescar();
                Chequear("releer no la duplica: sigue en 4", 4, lec.Eventos.Count);

                // ── línea a medias: Teams escribiendo justo cuando leemos ──────────────────────────────
                var t5 = hito(4);
                string mitad = Nube(t5, "Available");
                Agregar(f1, mitad.Substring(0, mitad.Length - 12));      // sin cerrar y sin salto de línea
                lec.Refrescar();
                Chequear("línea incompleta: no se cuenta todavía", 4, lec.Eventos.Count);
                Agregar(f1, mitad.Substring(mitad.Length - 12) + "\r\n");  // ahora sí, completa
                Tocar(f1, t5);
                lec.Refrescar();
                Chequear("al completarse sí se cuenta: 5", 5, lec.Eventos.Count);
                Chequear("y es Disponible", "Disponible", lec.Ultimo?.Estado);

                // ── rotación: Teams abre un archivo nuevo a los 2 MB ───────────────────────────────────
                var t6 = hito(5);
                string f2 = Path.Combine(carpeta, "MSTeams_2026-01-01_14-00-00.02.log");
                Escribir(f2, false, Ruido(t6.AddMinutes(-1)), Nube(t6, "Away"), Insignia(t6, "away", "Ausente"));
                Tocar(f2, t6);
                lec.Refrescar();
                Chequear("tras rotar sigue el archivo nuevo", "MSTeams_2026-01-01_14-00-00.02.log", lec.Archivo);
                Chequear("y suma su transición: 6", 6, lec.Eventos.Count);
                lec.Refrescar(); lec.Refrescar();
                Chequear("rotado y releído: sigue en 6", 6, lec.Eventos.Count);

                // ── los minutos por estado tienen que cerrar con el reloj ──────────────────────────────
                var min = lec.MinutosPorEstadoHoy();
                double total = min.Values.Sum();
                double esperado = (DateTime.Now - t1).TotalMinutes;
                Chequear("los minutos de hoy suman lo transcurrido", Math.Round(esperado), Math.Round(total));
                Chequear("hubo dos estados distintos hoy", 2, min.Count);

                // ── una carpeta vacía no puede explotar ni inventar ────────────────────────────────────
                string vacia = Path.Combine(carpeta, "vacia");
                Directory.CreateDirectory(vacia);
                var lec2 = new LectorPresenciaLog(log, vacia);
                lec2.Refrescar();
                Chequear("carpeta sin logs: 0 eventos y sin excepción", 0, lec2.Eventos.Count);
                Chequear("y lo dice en vez de callarse", true, lec2.Problema.Length > 0);
            }
            catch (Exception ex) { mal++; sb.AppendLine("  EXPLOTÓ  " + ex.GetType().Name + ": " + ex.Message); }
            finally { try { Directory.Delete(carpeta, true); } catch { } }

            sb.AppendLine();
            sb.AppendLine(mal == 0 ? $"TODO BIEN · {ok}/{ok} comprobaciones" : $"FALLARON {mal} de {ok + mal}");
            return sb.ToString();
        }

        static void Chequear(string que, object esperado, object obtenido)
        {
            bool bien = Equals(Convert.ToString(esperado, CultureInfo.InvariantCulture),
                               Convert.ToString(obtenido, CultureInfo.InvariantCulture));
            if (bien) { ok++; sb.AppendLine($"  ok    {que}"); }
            else { mal++; sb.AppendLine($"  FALLA {que}  ·  esperaba «{esperado}» y vino «{obtenido}»"); }
        }

        /// <summary>
        /// Marca de tiempo tal como la escribe Teams: recibe una hora LOCAL, escribe los dígitos en UTC y le
        /// pega un "-03:00" que es mentira. Si el lector se creyera el offset, daría una hora corrida.
        /// </summary>
        static string Marca(DateTime local) =>
            local.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "-03:00";

        static string Nube(DateTime t, string estado) =>
            $"{Marca(t)} 0x0001c8a8 <INFO> native_modules::UserDataCrossCloudModule: Received Action: UserPresenceAction: {{cloud_context: https://teams.microsoft.com, availability: {estado}}}";

        static string Insignia(DateTime t, string glifo, string traducido) =>
            $"{Marca(t)} 0x0001c8a8 <DBG>  TaskbarBadgeServiceLegacy:Work: SetBadge Setting badge: GlyphBadge{{\"{glifo}\"}}, overlay: No hay elementos, estado {traducido}";

        static string Ruido(DateTime t) =>
            $"{Marca(t)} 0x0001c8a8 <DBG>  native_modules::Inspector: targetReloadedAfterCrash sin nada que ver con la presencia";

        static void Escribir(string ruta, bool agregar, params string[] lineas)
        {
            using (var fs = new FileStream(ruta, agregar ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                foreach (var l in lineas) sw.Write(l + "\r\n");
        }

        static void Agregar(string ruta, string crudo)
        {
            using (var fs = new FileStream(ruta, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                sw.Write(crudo);
        }

        /// <summary>El lector elige archivo por fecha de modificación: en la prueba la fijamos a mano.</summary>
        static void Tocar(string ruta, DateTime t) { try { File.SetLastWriteTimeUtc(ruta, DateTime.UtcNow.AddSeconds(t.TimeOfDay.TotalSeconds / 100000.0)); } catch { } }
    }
}
