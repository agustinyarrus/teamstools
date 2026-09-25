using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace TeamsTools
{
    /// <summary>
    /// Las pruebas de la transcripción como conversación (25-sep-2026). Sobre los datos REALES todo es SOLO LECTURA.
    ///   --probar-transcripcion [--datos DIR] [--sin-color]   modelo, lectores, búsqueda, detective, húngaro, ajuste de
    ///                                                        línea y pintura contra las reuniones de verdad + casos borde
    ///   --foto-transcripcion DIR [--datos DIR]               el visor y la bandeja dibujados FUERA de pantalla → PNG
    ///   --probar-audio [--archivo A] [--desde S] [--segundos N] [--velocidad V] [--dispositivo parte-del-nombre]
    /// </summary>
    internal static class PruebaTranscripcion
    {
        static string Rosa => Pastel.Rosa;
        static string Cyan => Pastel.Cyan;
        static string Apagado => Pastel.Apagado;
        static string Fin => Pastel.Fin;

        // ================================================================== la batería

        public static int Correr(string datos)
        {
            var b = new Pastel.Bateria();
            var numeros = new List<(string, string)>();
            Console.WriteLine($"\n{Cyan}transcripción · la reunión como conversación{Fin} {Apagado}(datos: {datos} · solo lectura){Fin}");

            var indice = LeerIndiceSeguro(datos, b);
            var conJson = indice.Where(g => File.Exists(Path.Combine(g.Carpeta, "audio16.json"))).OrderByDescending(g => g.Desde).ToList();
            string corrillos = Path.Combine(datos, "corrillos.jsonl");

            foreach (var g in conJson)
            {
                string dir = g.Carpeta;
                string hablantes = Path.Combine(dir, "audio16.hablantes.txt");
                bool conVoces = File.Exists(hablantes) && g.Texto.StartsWith("[", StringComparison.Ordinal);
                b.Grupo($"{g.Desde:dd/MM HH:mm} · {Corto(g.Reunion, 40)}", $"{Grabador.Fmt(g.SegundosAudio)} · {g.Palabras:N0} palabras · {(conVoces ? "con voces" : "sin voces")}");
                var reloj = Stopwatch.StartNew();
                var segs = Json.Lista(Json.LeerObjeto(Path.Combine(dir, "audio16.json")), "segments");
                var t = Transcripcion.DesdeSegmentos(segs, g.SegundosAudio);
                long msJson = reloj.ElapsedMilliseconds;
                b.Chequeo("audio16.json se lee y arma turnos", t.Turnos.Length > 0, $"{t.Turnos.Length} turnos · {t.Voces.Length} voces · {msJson} ms");

                if (conVoces)
                {
                    // la misma regla que el grabador: los dos tienen que contar EXACTO igual
                    var renglones = File.ReadAllText(hablantes, Encoding.UTF8).Replace("\r\n", "\n").Split('\n').Where(r => r.Trim().Length > 0).ToList();
                    b.Chequeo("mismos turnos que audio16.hablantes.txt", renglones.Count == t.Turnos.Length, $"{t.Turnos.Length} vs {renglones.Count}");
                    int distintos = 0; string primero = "";
                    for (int i = 0; i < Math.Min(renglones.Count, t.Turnos.Length); i++)
                    {
                        var tu = t.Turnos[i];
                        string esperado = $"[{Transcripcion.Reloj(tu.Ini, true)}] {tu.Quien}: {tu.Texto.Replace('\n', ' ')}";
                        if (esperado != renglones[i].Trim()) { distintos++; if (primero.Length == 0) primero = $"#{i}: «{Corto(esperado, 50)}» vs «{Corto(renglones[i], 50)}»"; }
                    }
                    b.Chequeo("cada turno: misma hora, misma voz, mismo texto", distintos == 0, distintos == 0 ? "renglón por renglón" : $"{distintos} distintos · {primero}");
                    var tt = Transcripcion.DesdeTexto(g.Texto, g.SegundosAudio);
                    bool igual = tt.Turnos.Length == t.Turnos.Length && tt.Turnos.Zip(t.Turnos, (x, y) => x.Quien == y.Quien && Math.Abs(x.Ini - Math.Floor(y.Ini)) < 0.001).All(z => z);
                    b.Chequeo("el texto del índice arma lo mismo (respaldo sin json)", igual, $"{tt.Turnos.Length} turnos · {tt.Voces.Length} voces · fuente «{tt.Fuente}»");
                    b.Chequeo("las voces suman el 100 % del tiempo hablado", Math.Abs(t.Voces.Sum(v => v.Fraccion) - 1) < 1e-6, string.Join(" ", t.Voces.Take(4).Select(v => $"{v.Id} {v.Fraccion:P0}")) + (t.Voces.Length > 4 ? " …" : ""));
                }
                else
                {
                    b.Chequeo("sin voces: bloques con hora (whisper viejo)", !t.ConVoces && t.ConTiempos && t.Turnos.All(x => x.Quien.Length == 0), $"{t.Turnos.Length} bloques");
                    int largo = t.Turnos.Max(x => x.Texto.Length);
                    b.Chequeo("ningún bloque es un paredón", largo <= Transcripcion.CaracteresPorParrafo * 3, $"el más largo: {largo} caracteres");
                }

                // anclas y karaoke
                bool monotonas = t.Turnos.All(x => x.Marcas.Zip(x.Marcas.Skip(1), (p, q) => q.Car >= p.Car && q.Seg >= p.Seg - 1e-9).All(z => z));
                b.Chequeo("las anclas nunca retroceden (carácter y segundo)", monotonas, $"{t.Turnos.Sum(x => x.Marcas.Length):N0} anclas");
                var azar = new Random(11);
                int idaVuelta = 0, pruebas = 0;
                foreach (var x in t.Turnos.Where(z => z.Texto.Length > 20).Take(80))
                    for (int k = 0; k < 10; k++)
                    {
                        double s = x.Ini + azar.NextDouble() * Math.Max(0.01, x.Dura);
                        int c = x.CaracterEn(s);
                        double s2 = x.SegundoEn(c);
                        pruebas++;
                        if (Math.Abs(x.CaracterEn(s2) - c) <= 1) idaVuelta++;
                    }
                b.Chequeo("karaoke: segundo → carácter → segundo vuelve al mismo", idaVuelta == pruebas, $"{idaVuelta}/{pruebas}");
                int malas = 0;
                for (int k = 0; k < 4000; k++)
                {
                    double s = azar.NextDouble() * (t.Duracion + 20) - 10;
                    int lineal = -1;
                    for (int i = 0; i < t.Turnos.Length; i++) if (t.Turnos[i].Ini <= s) lineal = i;
                    if (lineal != t.TurnoEn(s)) malas++;
                }
                b.Chequeo("qué turno suena: búsqueda binaria = recorrida lineal", malas == 0, $"4 000 instantes al azar · {malas} distintos");

                // búsqueda
                string palabra = PalabraFrecuente(t);
                var h = t.Buscar(palabra.ToUpperInvariant());
                int fuerza = t.Turnos.Sum(x => Regex.Matches(Pliegue.Texto(x.Texto), Regex.Escape(Pliegue.Texto(palabra))).Count);
                b.Chequeo($"buscar «{palabra.ToUpperInvariant()}» sin tildes ni mayúsculas", h.Count == fuerza && h.Count > 0, $"{h.Count} apariciones (a mano: {fuerza})");

                // el paquete entero como lo arma el visor (json + nombres + panel de Teams + detective)
                reloj.Restart();
                var p = PaqueteTranscripcion.Leer(g, corrillos);
                b.Chequeo("el paquete del visor lee todo de fondo", p.T.Turnos.Length == t.Turnos.Length && p.T.Fuente == "audio16.json", $"{p.Ms} ms · audio: {(p.Audio.Length > 0 ? Path.GetFileName(p.Audio) : "—")} · {p.Gente.Nombres.Count} en el panel de Teams");
                if (conVoces)
                {
                    b.Dato("detective: sugerencias", p.Sugerencias.Count == 0 ? "ninguna con confianza (no adivina)" : string.Join(" · ", p.Sugerencias.Select(s => $"{s.Voz} → {s.Persona} ({s.Confianza:P0})")));
                    foreach (var s in p.Sugerencias.Take(3)) b.Dato("   porque", Corto(s.Explicacion, 110));
                }

                // ajuste de línea a tres anchos (lo que pasa al redimensionar la ventana)
                reloj.Restart();
                int renglonesTot = 0;
                foreach (int cols in new[] { 48, 90, 140 })
                    foreach (var x in t.Turnos) renglonesTot += Prosa.Envolver(x.Texto, cols, 12).Count;
                long msEnvolver = reloj.ElapsedMilliseconds;
                b.Chequeo("ajuste de línea de toda la reunión, tres anchos", msEnvolver < 200, $"{renglonesTot:N0} renglones en {msEnvolver} ms");
                numeros.Add(($"{g.Desde:dd/MM HH:mm} leer json · envolver", $"{msJson} ms · {msEnvolver} ms (3 anchos)"));
            }

            PruebaRenombrar(b, conJson.FirstOrDefault(g => g.Texto.StartsWith("[", StringComparison.Ordinal)), corrillos);
            BordesDelModelo(b);
            BordesDeProsa(b);
            PruebaHungaro(b);
            PruebaDetective(b);
            PruebaPintura(b, conJson.FirstOrDefault(g => g.Texto.StartsWith("[", StringComparison.Ordinal)), corrillos, numeros);
            PruebaBandeja(b);

            b.Tarjeta("transcripción · resultado", numeros);
            return b.Fallas;
        }

        // ---------------------------------------------------------------- renombrar (en una COPIA)

        /// <summary>
        /// Ponerle nombre a una voz de punta a punta, sobre una COPIA de la grabación en %TEMP% (los datos reales no se
        /// tocan): el visor la muestra al toque, nombres.json queda escrito, el detective deja de sugerirla, la copia para
        /// pegar y el resumen la usan, el menú del clic derecho se arma, y «volver a Persona N» la deshace.
        /// </summary>
        static void PruebaRenombrar(Pastel.Bateria b, Grabacion real, string corrillos)
        {
            b.Grupo("renombrar voces", "sobre una copia en %TEMP%: los datos reales no se tocan");
            if (real == null) { b.Chequeo("hay una reunión con voces para copiar", false); return; }
            string copia = Path.Combine(Path.GetTempPath(), "teamstools-prueba-nombres-" + Process.GetCurrentProcess().Id);
            try
            {
                Directory.CreateDirectory(copia);
                foreach (var f in new[] { "audio16.json", "audio16.hablantes.txt", "audio16.hablantes.json" })
                    if (File.Exists(Path.Combine(real.Carpeta, f))) File.Copy(Path.Combine(real.Carpeta, f), Path.Combine(copia, f), true);
                var g = real.Clonar();
                g.Dir = copia; g.Archivo = ""; g.Wav = "";
                var p = PaqueteTranscripcion.Leer(g, corrillos);
                var voz = p.T.Voces.FirstOrDefault(v => !v.EsYo && p.Sugerencias.Any(s => s.Voz == v.Id)) ?? p.T.Voces.First(v => !v.EsYo);
                string quien = p.Sugerencias.FirstOrDefault(s => s.Voz == voz.Id)?.Persona ?? "Ñandú Pérez";
                using (var v = new VisorTranscripcion { Width = 900, Height = 700 })
                {
                    v.CreateControl();
                    v.PonerPaquete(g, p);
                    int cambios = 0;
                    v.NombresCambiaron += () => cambios++;
                    v.Renombrar(voz.Id, quien);
                    Disco.Vaciar(3000);
                    b.Chequeo($"«{voz.Id}» pasa a llamarse «{quien}» en el visor", p.T.VozDe(voz.Id).Nombre == quien && cambios == 1);
                    var leidos = NombresDeVoces.Leer(copia);
                    b.Chequeo("nombres.json quedó en la carpeta de la grabación", leidos.TryGetValue(voz.Id, out var n) && n == quien, string.Join(", ", leidos.Select(kv => kv.Key + "=" + kv.Value)));
                    b.Chequeo("el detective ya no sugiere esa voz ni ese nombre", p.Sugerencias.All(s => s.Voz != voz.Id && s.Persona != quien));
                    string texto = v.TextoParaCopiar();
                    b.Chequeo("la copia para pegar usa el nombre (y saltos de Windows)", texto.Contains("] " + quien + ": ") && !texto.Contains("] " + voz.Id + ": ") && texto.Contains("\r\n\r\n"));
                    string resumen = NombresDeVoces.Aplicar($"Pendientes: {voz.Id} revisa el tablero; {voz.Id}0 no.", v.Nombres);
                    b.Chequeo("el resumen usa el nombre (y no pisa «" + voz.Id + "0»)", resumen == $"Pendientes: {quien} revisa el tablero; {voz.Id}0 no.", resumen);
                    using (var m = v.MenuDeVozParaPrueba(voz.Id))
                        b.Chequeo("el menú del clic derecho se arma (panel de Teams + escribir + volver)", m.Items.Count >= 5 && m.Items.OfType<ToolStripTextBox>().Any(), $"{m.Items.Count} ítems");
                    v.Renombrar(voz.Id, "");
                    Disco.Vaciar(3000);
                    b.Chequeo($"«volver a {voz.Id}» lo deshace", p.T.VozDe(voz.Id).Nombre == voz.Id && !NombresDeVoces.Leer(copia).ContainsKey(voz.Id));
                }
            }
            finally
            {
                try { Directory.Delete(copia, true); }
                catch (IOException ex) { b.Dato("no pude borrar la copia", ex.Message); }
            }
        }

        // ---------------------------------------------------------------- casos borde del modelo

        static void BordesDelModelo(Pastel.Bateria b)
        {
            b.Grupo("casos borde del modelo", "vacío, uno, duplicados, nulos, tildes y Ñ, tiempos rotos, N grande");
            b.Chequeo("texto vacío → transcripción vacía, sin romperse", Transcripcion.DesdeTexto("", 0).EsVacia && Transcripcion.DesdeTexto("   \n \r\n ", 0).EsVacia && Transcripcion.DesdeTexto(null, 0).EsVacia);
            b.Chequeo("segmentos null o vacíos", Transcripcion.DesdeSegmentos(null, 10).EsVacia && Transcripcion.DesdeSegmentos(new List<Dictionary<string, object>>(), 10).EsVacia);
            var uno = Transcripcion.DesdeTexto("[00:00:05] Persona 1: hola", 12);
            b.Chequeo("un solo turno: termina al final del audio", uno.Turnos.Length == 1 && uno.Turnos[0].Ini == 5 && uno.Turnos[0].Fin == 12 && uno.Voces.Length == 1, $"{uno.Turnos[0].Ini}–{uno.Turnos[0].Fin} s");
            var plano = Transcripcion.DesdeTexto(string.Join(" ", Enumerable.Repeat("Una oración de prueba con acentos: ñandú, camión y pingüino.", 40)), 0);
            b.Chequeo("texto plano de whisper → párrafos, sin horas, sin perder palabras", !plano.ConTiempos && !plano.ConVoces && plano.Turnos.Length > 3 && plano.Palabras == 40 * 10, $"{plano.Turnos.Length} párrafos · {plano.Palabras} palabras");
            // segmentos seguidos de la misma voz se juntan; tiempos rotos (NaN, al revés, negativos) no rompen nada
            var segs = new List<Dictionary<string, object>>
            {
                S(0, 2, "Hola.", "Persona 1"), S(2, 4, "¿Cómo va?", "Persona 1"), S(9, 12, "Retomo después de pensar.", "Persona 1"),
                S(12, 11, "Tiempos al revés.", "Persona 2"), S(double.NaN, 13, "Sin inicio.", "Persona 2"), S(-5, -1, "Negativo.", "Persona 3"),
                S(20, 21, "   ", "Persona 3"), S(21, 22, "Yo hablo.", "Yo"),
            };
            var t = Transcripcion.DesdeSegmentos(segs, 30);
            b.Chequeo("misma voz seguida = un turno; texto vacío se saltea", t.Turnos.Length == 4 && t.Turnos[0].Quien == "Persona 1" && t.Turnos[3].Quien == "Yo", string.Join(" | ", t.Turnos.Select(x => x.Quien)));
            b.Chequeo("un silencio de 5 s de la misma voz abre párrafo", t.Turnos[0].Texto.Contains("\nRetomo"), t.Turnos[0].Texto.Replace("\n", " ⏎ "));
            b.Chequeo("tiempos rotos no hacen retroceder las anclas", t.Turnos.All(x => x.Marcas.Zip(x.Marcas.Skip(1), (p, q) => q.Seg >= p.Seg).All(z => z)));
            b.Chequeo("«Yo» es malva y se llama Yo", t.VozDe("Yo")?.EsYo == true && t.VozDe("Yo").Color == Tema.Malva && t.VozDe("Yo").Iniciales == "YO");
            // tildes y Ñ: el plegado mide lo mismo y la ñ no se pierde
            string raro = "ÑANDÚ Árbol ÜÑA ç É\n—«»";
            b.Chequeo("plegado: sin tildes, la ñ se queda, mismo largo", Pliegue.Texto(raro) == "ñandu arbol uña c e —«»" && Pliegue.Texto(raro).Length == raro.Length, Pliegue.Texto(raro));
            bool todos = true;
            for (int c = 0; c < 0x250; c++) todos &= Pliegue.Texto(((char)c).ToString()).Length == 1;
            b.Chequeo("plegado: los 592 caracteres latinos miden 1", todos);
            var ene = Transcripcion.DesdeTexto("[00:00:00] Persona 1: el AÑO que viene\n[00:00:04] Persona 2: el año pasado y el ano", 10);
            b.Chequeo("buscar «año» no encuentra «ano» (la ñ importa)", ene.Buscar("AÑO").Count == 2 && ene.Buscar("ano").Count == 1);
            // nombres: «Persona 1» no pisa «Persona 12»
            var nombres = new Dictionary<string, string> { ["Persona 1"] = "Vale", ["Persona 12"] = "Bruno" };
            string r = NombresDeVoces.Aplicar("Persona 1 y Persona 12 hablaron; Persona 10 no. Yo tampoco.", nombres);
            b.Chequeo("renombrar por palabra entera (Persona 1 ≠ Persona 12)", r == "Vale y Bruno hablaron; Persona 10 no. Yo tampoco.", r);
            // reloj
            b.Chequeo("reloj: 0, 59,9, 3 600, NaN y negativos", Transcripcion.Reloj(0) == "00:00" && Transcripcion.Reloj(59.9) == "00:59" && Transcripcion.Reloj(3600) == "1:00:00" && Transcripcion.Reloj(double.NaN) == "00:00" && Transcripcion.Reloj(-3) == "00:00" && Transcripcion.Reloj(75, true) == "00:01:15");
            // saltos para Windows
            b.Chequeo("saltos \\n, \\r y \\r\\n → \\r\\n (y es idempotente)", CajaTexto.Saltos("a\nb\r\nc\rd") == "a\r\nb\r\nc\r\nd" && CajaTexto.Saltos(CajaTexto.Saltos("a\nb")) == "a\r\nb" && CajaTexto.Saltos(null) == "");
            // N grande: 20 000 turnos
            var reloj = Stopwatch.StartNew();
            var grande = new StringBuilder();
            for (int i = 0; i < 20000; i++) grande.Append($"[{Transcripcion.Reloj(i * 3, true)}] Persona {1 + i % 9}: turno número {i} con algo de texto para envolver.\n");
            var tg = Transcripcion.DesdeTexto(grande.ToString(), 60000);
            long ms = reloj.ElapsedMilliseconds;
            b.Chequeo("20 000 turnos se arman rápido", tg.Turnos.Length == 20000 && tg.Voces.Length == 9 && ms < 2000, $"{ms} ms");
        }

        static Dictionary<string, object> S(double ini, double fin, string texto, string quien) =>
            new Dictionary<string, object> { ["start"] = double.IsNaN(ini) ? null : (object)ini, ["end"] = fin, ["text"] = texto, ["speaker"] = quien };

        // ---------------------------------------------------------------- ajuste de línea

        static void BordesDeProsa(Pastel.Bateria b)
        {
            b.Grupo("ajuste de línea (Prosa)", "invariantes sobre 3 000 textos y anchos al azar");
            var azar = new Random(5);
            string[] palabras = { "a", "de", "reunión", "transcripción", "ñandú", "sí,", "Persona", "12", "¿qué?", "supercalifragilisticoespialidoso-largo", "—", "ok." };
            int fallas = 0; string ejemplo = "";
            for (int k = 0; k < 3000; k++)
            {
                var sb = new StringBuilder();
                int n = azar.Next(0, 60);
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) sb.Append(azar.Next(12) == 0 ? "\n" : azar.Next(9) == 0 ? "  " : " ");
                    sb.Append(palabras[azar.Next(palabras.Length)]);
                }
                string s = sb.ToString();
                int cols = azar.Next(4, 60), sangria = azar.Next(0, 8);
                var rs = Prosa.Envolver(s, cols, sangria);
                bool ok = true;
                for (int i = 0; i < rs.Count; i++)
                {
                    var r = rs[i];
                    int max = i == 0 ? Math.Max(4, cols - sangria) : Math.Max(4, cols);
                    if (r.Largo > max || r.Ini < 0 || r.Fin > s.Length) ok = false;
                    else if (r.Largo > 0 && (s[r.Ini] == ' ' || s[r.Fin - 1] == ' ' || s.Substring(r.Ini, r.Largo).Contains('\n'))) ok = false;
                }
                // lo que se ve, junto, es el texto sin los espacios repetidos
                // (adentro de un renglón los espacios dobles quedan como están: el texto real viene normalizado)
                string junto = Regex.Replace(string.Join(" ", rs.Where(r => r.Largo > 0).Select(r => s.Substring(r.Ini, r.Largo))), " +", " ");
                string esperado = Regex.Replace(s.Replace('\n', ' '), " +", " ").Trim();
                bool partidas = rs.Any(r => r.Largo > 0 && r.Fin < s.Length && s[r.Fin] != ' ' && s[r.Fin] != '\n');
                if (!partidas && junto != esperado) ok = false;
                if (!ok) { fallas++; if (ejemplo.Length == 0) ejemplo = $"cols {cols} · «{Corto(s, 40)}»"; }
            }
            b.Chequeo("ningún renglón se pasa, ni empieza o termina en espacio", fallas == 0, fallas == 0 ? "3 000 textos" : $"{fallas} mal · {ejemplo}");
            var largo = Prosa.Envolver(new string('x', 25), 10);
            b.Chequeo("una palabra más larga que el renglón se parte", largo.Count == 3 && largo.All(r => r.Largo <= 10), string.Join(",", largo.Select(r => r.Largo)));
            var parr = Prosa.Envolver("uno dos\n\ntres", 20);
            b.Chequeo("párrafo vacío ocupa su lugar; fin de párrafo marcado", parr.Count == 3 && parr[0].FinDeParrafo && parr[1].Largo == 0);
            var comp = Prosa.Componer(new Prosa.Renglon(10, 30, false), Color.White, 25, Color.Red, Color.Gray,
                new[] { new Prosa.Marcado(12, 16, false), new Prosa.Marcado(20, 30, true), new Prosa.Marcado(38, 50, false) }, Color.Yellow, Color.Black, Color.Black, Color.Yellow);
            bool cubre = comp.Count > 0 && comp[0].Ini == 10 && comp[comp.Count - 1].Fin == 40 && comp.Zip(comp.Skip(1), (x, y) => x.Fin == y.Ini).All(z => z);
            b.Chequeo("tintas: ordenadas, sin huecos ni solapes, manda la búsqueda", cubre && comp.First(x => x.Ini == 20).Fondo == Color.Yellow, $"{comp.Count} tramos");
        }

        // ---------------------------------------------------------------- húngaro

        static void PruebaHungaro(Pastel.Bateria b)
        {
            b.Grupo("asignación óptima (húngaro)", "contra fuerza bruta: 300 matrices al azar hasta 7×7, cuadradas y rectangulares");
            var azar = new Random(3);
            int malas = 0;
            for (int k = 0; k < 300; k++)
            {
                int n = azar.Next(1, 7), m = n + azar.Next(0, 3);
                var c = new double[n, m];
                for (int i = 0; i < n; i++) for (int j = 0; j < m; j++) c[i, j] = Math.Round(azar.NextDouble() * 10 - 5, 2);
                var a = Hungaro.Asignar(c);
                double costo = Enumerable.Range(0, n).Sum(i => c[i, a[i]]);
                bool distintas = a.Distinct().Count() == n && a.All(j => j >= 0 && j < m);
                if (!distintas || Math.Abs(costo - FuerzaBruta(c, n, m)) > 1e-9) malas++;
            }
            b.Chequeo("el húngaro da el mismo óptimo que probar todo", malas == 0, $"{300 - malas}/300");
            bool tiro = false;
            try { Hungaro.Asignar(new double[3, 2]); } catch (ArgumentException) { tiro = true; }
            b.Chequeo("más filas que columnas → avisa en vez de mentir", tiro);
        }

        /// <summary>Todas las asignaciones posibles (m!/(m−n)!, a lo sumo 20 160). Sin podar: con costos negativos, una suma parcial alta todavía puede bajar.</summary>
        static double FuerzaBruta(double[,] c, int n, int m)
        {
            double mejor = double.PositiveInfinity;
            var usado = new bool[m];
            void Rec(int i, double acum)
            {
                if (i == n) { if (acum < mejor) mejor = acum; return; }
                for (int j = 0; j < m; j++) if (!usado[j]) { usado[j] = true; Rec(i + 1, acum + c[i, j]); usado[j] = false; }
            }
            Rec(0, 0);
            return mejor;
        }

        // ---------------------------------------------------------------- detective

        static void PruebaDetective(Pastel.Bateria b)
        {
            b.Grupo("detective de nombres", "una charla armada a mano con la verdad conocida");
            string charla = string.Join("\n",
                "[00:00:00] Persona 1: Bueno, arrancamos. Hoy vemos el tablero de Analytics. Sí, Vale.",
                "[00:00:08] Persona 2: Perdón, tengo una duda con los usuarios concurrentes.",
                "[00:00:20] Persona 1: Dale. Bruno, ¿vos lo viste?",
                "[00:00:25] Persona 3: Sí, lo vi ayer. Soy Bruno, el de backend.",
                "[00:00:33] Persona 1: Gracias Bruno.",
                "[00:00:36] Persona 2: Lo que decía Bruno está bien. Gracias, Flor.",
                "[00:00:41] Persona 1: De nada.");
            var t = Transcripcion.DesdeTexto(charla, 50);
            var gente = new Participantes { Yo = "Ferreyra, Ramiro", Nombres = new List<string> { "Rivas, Valentina", "Ortega, Bruno", "Sosa, Florencia", "Paredes, Florián", "Ferreyra, Ramiro" } };
            var sug = Detective.Sugerir(t, gente, "Revisión semanal");
            string Voz(string v) => sug.FirstOrDefault(s => s.Voz == v)?.Persona ?? "—";
            b.Chequeo("«…Sí, Vale.» y habla Persona 2 → Valentina Rivas", Voz("Persona 2") == "Valentina Rivas", Voz("Persona 2"));
            b.Chequeo("«soy Bruno» + «Bruno, ¿vos…?» → Persona 3 es Bruno", Voz("Persona 3") == "Bruno Ortega", Voz("Persona 3"));
            b.Chequeo("«Flor» es ambiguo (Florencia / Florián) → no adivina", Voz("Persona 1") == "—", Voz("Persona 1"));
            b.Chequeo("cada sugerencia trae su porqué", sug.All(s => s.Pistas.Count > 0 && s.Explicacion.Length > 0), sug.Count > 0 ? Corto(sug[0].Explicacion, 70) : "");
            var ya = Detective.Sugerir(t, gente, "", new Dictionary<string, string> { ["Persona 3"] = "Bruno Ortega" });
            b.Chequeo("lo que ya renombraste no se vuelve a sugerir", ya.All(s => s.Voz != "Persona 3" && s.Persona != "Bruno Ortega"), string.Join(" · ", ya.Select(s => s.Voz + "→" + s.Persona)));
            b.Chequeo("sin panel de Teams → ninguna sugerencia (no inventa)", Detective.Sugerir(t, new Participantes(), "").Count == 0);
            var conTitulo = Detective.Sugerir(Transcripcion.DesdeTexto("[00:00:00] Persona 1: Les muestro el tablero.\n[00:00:30] Persona 2: Buenísimo.", 40),
                                              new Participantes { Nombres = new List<string> { "Ortega, Bruno", "Rivas, Valentina" } }, "Analytics por Bruno");
            b.Chequeo("«… por Bruno» + la voz que más habla → pista del título", conTitulo.Any(s => s.Voz == "Persona 1" && s.Persona == "Bruno Ortega") || conTitulo.Count == 0,
                      conTitulo.Count == 0 ? "sola no alcanza el umbral (bien: es una sola pista)" : conTitulo[0].Explicacion);
        }

        // ---------------------------------------------------------------- pintura

        static void PruebaPintura(Pastel.Bateria b, Grabacion g, string corrillos, List<(string, string)> numeros)
        {
            b.Grupo("pintura del visor", "fuera de pantalla, con la reunión real");
            if (g == null) { b.Chequeo("hay una reunión con voces para pintar", false, "no encontré ninguna"); return; }
            var p = PaqueteTranscripcion.Leer(g, corrillos);
            foreach (var (ancho, alto, amplio) in new[] { (560, 560, false), (1700, 1000, true) })
            {
                using (var v = new VisorTranscripcion { Width = ancho, Height = alto })
                {
                    v.CreateControl();
                    if (amplio) v.CambiarAmplio(true);
                    v.PonerPaquete(g, p);
                    var tiempos = new List<double>();
                    // 🚨 se mide pintando como WinForms en pantalla: un BufferedGraphics sobre el DC de la ventana. Un Graphics
                    //    sacado de un Bitmap engaña: cada texto de GDI copia el bitmap entero ida y vuelta (300 ms por cuadro)
                    using (var bmp = new Bitmap(ancho, alto, PixelFormat.Format32bppArgb))
                    using (var gv = v.CreateGraphics())
                    using (var buffer = BufferedGraphicsManager.Current.Allocate(gv, new Rectangle(0, 0, ancho, alto)))
                    {
                        var reloj = new Stopwatch();
                        for (int i = 0; i < 40; i++)
                        {
                            v.Probar("sonar:" + (120 + i * 0.5).ToString(CultureInfo.InvariantCulture));   // karaoke corriendo: lo más caro que pinta
                            reloj.Restart();
                            v.PintarEn(buffer.Graphics, new Rectangle(0, 0, ancho, alto));
                            tiempos.Add(reloj.Elapsed.TotalMilliseconds);
                        }
                        // hay texto: en el cuarto central del cuerpo aparecen cientos de píxeles claros (letras), no un fondo liso
                        v.DrawToBitmap(bmp, new Rectangle(0, 0, ancho, alto));
                        int claros = 0;
                        for (int y = alto / 4; y < alto * 3 / 4; y += 2)
                            for (int x = ancho / 8; x < ancho / 2; x += 2)
                                if (bmp.GetPixel(x, y).GetBrightness() > 0.5f) claros++;
                        bool pinto = claros > 200;
                        tiempos.Sort();
                        double p50 = tiempos[tiempos.Count / 2], p95 = tiempos[(int)(tiempos.Count * 0.95)];
                        // 🚨 los topes van contra la MEDIANA: con una llamada de Teams en curso en esta máquina de 15 W, el p95 es ruido de CPU
                        b.Chequeo($"{(amplio ? "en grande" : "compacto")} {ancho}×{alto}: pinta texto (todo de nuevo)", pinto && p50 < 100, $"repintado completo p50 {p50:0.0} ms · p95 {p95:0.0} ms (tope p50 100: pasa al cambiar de tamaño, no por cuadro)");
                        numeros.Add(($"{(amplio ? "en grande" : "compacto")} · todo de nuevo p50/p95", $"{p50:0.0} / {p95:0.0} ms"));

                        // un cuadro de karaoke de verdad: solo el turno que suena, la cinta con el cabezal, el encabezado y la barra
                        var parciales = new List<double>();
                        for (int i = 0; i < 60; i++)
                        {
                            v.Probar("sonar:" + (140 + i * 0.25).ToString(CultureInfo.InvariantCulture));
                            reloj.Restart();
                            foreach (var parte in v.RegionKaraoke()) v.PintarEn(buffer.Graphics, parte);
                            parciales.Add(reloj.Elapsed.TotalMilliseconds);
                        }
                        parciales.Sort();
                        double k50 = parciales[parciales.Count / 2], k95 = parciales[(int)(parciales.Count * 0.95)];
                        double cpu = k50 * 1000.0 / 33;          // a 30 cuadros por segundo
                        b.Chequeo($"{(amplio ? "en grande" : "compacto")}: un cuadro de karaoke repinta poco", k50 < 12, $"p50 {k50:0.0} ms · p95 {k95:0.0} ms → ~{cpu / 10:0} % de un núcleo sonando (tope p50 12)");
                        numeros.Add(($"{(amplio ? "en grande" : "compacto")} · cuadro de karaoke p50/p95", $"{k50:0.0} / {k95:0.0} ms (~{cpu / 10:0} % de un núcleo)"));
                    }
                    if (amplio) foreach (var (que, ms) in MedirCostos(v)) b.Dato("costo: " + que, $"{ms:0.0} ms");
                }
            }
        }

        /// <summary>
        /// ¿Dónde se va el tiempo de pintar? Medido, no supuesto: 200 textos por Graphics (cada uno pide y suelta el HDC),
        /// 200 por un HDC tomado UNA vez, y el fondo redondeado entero contra un rectángulo liso.
        /// </summary>
        internal static List<(string, double)> MedirCostos(Control v)
        {
            var res = new List<(string, double)>();
            var f = Tema.Fina(12f);
            using (var gv = v.CreateGraphics())
            using (var buffer = BufferedGraphicsManager.Current.Allocate(gv, new Rectangle(0, 0, v.Width, v.Height)))
            {
                var g = buffer.Graphics;
                var reloj = Stopwatch.StartNew();
                for (int i = 0; i < 200; i++)
                    TextRenderer.DrawText(g, "Sí, exactamente. O sea, por cada interacción", f, new Point(20, 20 + i % 40 * 20), Color.White, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping);
                res.Add(("200 textos por Graphics (con recorte)", reloj.Elapsed.TotalMilliseconds));
                reloj.Restart();
                for (int i = 0; i < 200; i++)
                    TextRenderer.DrawText(g, "Sí, exactamente. O sea, por cada interacción", f, new Point(20, 20 + i % 40 * 20), Color.White, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                res.Add(("200 textos por Graphics (sin recorte)", reloj.Elapsed.TotalMilliseconds));
                reloj.Restart();
                var lote = new Prosa.Lote();
                for (int i = 0; i < 200; i++) lote.Agregar("Sí, exactamente. O sea, por cada interacción", f, 20, 20 + i % 40 * 20, Color.White);
                lote.Dibujar(g, new Rectangle(0, 0, v.Width, v.Height));
                res.Add(("200 textos por un HDC (lote)", reloj.Elapsed.TotalMilliseconds));
                reloj.Restart();
                for (int i = 0; i < 20; i++) Tema.Tarjeta_(g, new RectangleF(0.5f, 0.5f, v.Width - 1, v.Height - 1), 10, Tema.Panel, Tema.Borde);
                res.Add(("20 fondos redondeados enteros", reloj.Elapsed.TotalMilliseconds));
                reloj.Restart();
                using (var br = new SolidBrush(Tema.Panel)) for (int i = 0; i < 20; i++) g.FillRectangle(br, 0, 0, v.Width, v.Height);
                res.Add(("20 rectángulos lisos enteros", reloj.Elapsed.TotalMilliseconds));
            }
            return res;
        }

        static void PruebaBandeja(Pastel.Bateria b)
        {
            b.Grupo("bandeja", "el dibujo de siempre intacto y el nuevo «procesando»");
            using (var a = Bandeja.DibujoEstado(Tema.Cyan, "", false))
            using (var z = Bandeja.DibujoProceso(24, false))
            using (var zp = Bandeja.DibujoProceso(24, true))
            {
                var centro = a.GetPixel(16, 16);
                b.Chequeo("en llamada: el punto cyan de siempre", Math.Abs(centro.R - Tema.Cyan.R) < 3 && Math.Abs(centro.G - Tema.Cyan.G) < 3 && Math.Abs(centro.B - Tema.Cyan.B) < 3, $"#{centro.R:x2}{centro.G:x2}{centro.B:x2}");
                var abajo = z.GetPixel(16, 20); var arriba = z.GetPixel(16, 12);
                b.Chequeo("procesando a la mitad: el punto lleno abajo, vacío arriba", abajo.A > 200 && ColorCerca(abajo, Tema.Malva) && !ColorCerca(arriba, Tema.Malva), $"abajo #{abajo.R:x2}{abajo.G:x2}{abajo.B:x2} · arriba #{arriba.R:x2}{arriba.G:x2}{arriba.B:x2}");
                var anilloDer = z.GetPixel(28, 16); var anilloIzq = z.GetPixel(4, 16);
                b.Chequeo("el anillo se llena en sentido horario (la mitad: derecha sí, izquierda no)", ColorCerca(anilloDer, Tema.Malva) && !ColorCerca(anilloIzq, Tema.Malva), "");
                b.Chequeo("en pausa (esperando que cortes la llamada): crema", ColorCerca(zp.GetPixel(16, 20), Tema.Crema));
            }
            b.Chequeo("el avance va en 48 escalones (redibuja solo si se ve distinto)", Bandeja.PasoDe(-1) == -1 && Bandeja.PasoDe(0) == 0 && Bandeja.PasoDe(1) == 48 && Bandeja.PasoDe(2) == 48 && Bandeja.PasoDe(0.5) == 24 && Bandeja.PasoDe(double.NaN) == -1);
        }

        static bool ColorCerca(Color a, Color b) => Math.Abs(a.R - b.R) < 24 && Math.Abs(a.G - b.G) < 24 && Math.Abs(a.B - b.B) < 24;

        // ================================================================== las fotos

        public static int Fotos(string dir, string datos)
        {
            Directory.CreateDirectory(dir);
            var indice = Grabador.LeerIndice(Path.Combine(datos, "grabaciones", "indice.json"));
            string corrillos = Path.Combine(datos, "corrillos.jsonl");
            var conVoces = indice.Where(g => g.Texto.StartsWith("[", StringComparison.Ordinal)).OrderByDescending(g => g.Desde).ToList();
            var sinVoces = indice.Where(g => g.Texto.Length > 400 && !g.Texto.StartsWith("[", StringComparison.Ordinal) && File.Exists(Path.Combine(g.Carpeta, "audio16.json"))).OrderByDescending(g => g.Desde).FirstOrDefault();
            int n = 0;
            void Foto(Grabacion g, PaqueteTranscripcion p, int ancho, int alto, bool amplio, string nombre, params string[] pasos)
            {
                using (var v = new VisorTranscripcion { Width = ancho, Height = alto })
                {
                    v.CreateControl();
                    if (amplio) v.CambiarAmplio(true);
                    if (p != null) v.PonerPaquete(g, p); else v.Poner(g, corrillos);
                    foreach (var s in pasos) v.Probar(s);
                    using (var bmp = new Bitmap(ancho, alto, PixelFormat.Format32bppArgb))
                    {
                        for (int i = 0; i < 6; i++) { v.DrawToBitmap(bmp, new Rectangle(0, 0, ancho, alto)); Thread.Sleep(30); }   // el desplazamiento suave llega a destino
                        bmp.Save(Path.Combine(dir, nombre + ".png"), ImageFormat.Png);
                    }
                }
                n++;
                Console.WriteLine($"  {Pastel.Salvia}✓{Fin} {nombre}.png");
            }
            Console.WriteLine($"\n{Cyan}fotos del visor de transcripción{Fin} {Apagado}→ {dir}{Fin}");
            foreach (var g in conVoces.Take(2))
            {
                var p = PaqueteTranscripcion.Leer(g, corrillos);
                string id = g.Id.Substring(Math.Max(0, g.Id.Length - 6));
                Foto(g, p, 560, 600, false, $"compacto-{id}");
                Foto(g, p, 560, 600, false, $"compacto-{id}-karaoke", "sonar:150");
                Foto(g, p, 560, 600, false, $"compacto-{id}-buscar", "buscar:usuario");
                Foto(g, p, 1760, 1040, true, $"amplio-{id}");
                Foto(g, p, 1760, 1040, true, $"amplio-{id}-karaoke", "sonar:150", "hover:3");
                Foto(g, p, 1760, 1040, true, $"amplio-{id}-voz", "voz:" + (p.T.Voces.Length > 1 ? p.T.Voces[1].Id : ""));
                Foto(g, p, 1760, 1040, true, $"amplio-{id}-buscar", "buscar:analytics");
            }
            if (sinVoces != null) Foto(sinVoces, PaqueteTranscripcion.Leer(sinVoces, corrillos), 560, 600, false, "compacto-sin-voces");
            var enCurso = new Grabacion { Id = "demo-en-curso", Reunion = "Daily Equipo Dev", Estado = EstadoGrab.Transcribiendo, Progreso = 0.45, Etapa = "Cohere · trozo 2/5", Desde = DateTime.Now };
            Foto(enCurso, null, 560, 420, false, "compacto-transcribiendo");
            Foto(null, null, 560, 300, false, "compacto-sin-grabacion");

            // la bandeja, en grande (×8, sin suavizar) y a tamaño real, sobre fondo oscuro
            var estados = new (string, Bitmap)[]
            {
                ("sin-llamada", Bandeja.DibujoEstado(Tema.Apagado, "", false)), ("en-llamada", Bandeja.DibujoEstado(Tema.Cyan, "", false)),
                ("procesando-sin-avance", Bandeja.DibujoProceso(-1, false)), ("procesando-10", Bandeja.DibujoProceso(5, false)),
                ("procesando-45", Bandeja.DibujoProceso(22, false)), ("procesando-80", Bandeja.DibujoProceso(38, false)),
                ("procesando-100", Bandeja.DibujoProceso(48, false)), ("en-pausa-60", Bandeja.DibujoProceso(29, true)),
            };
            const int zoom = 8, sep = 26;
            using (var hoja = new Bitmap(estados.Length * (32 * zoom + sep) + sep, 32 * zoom + 32 + 56 + sep * 2))
            using (var gh = Graphics.FromImage(hoja))
            {
                gh.Clear(Color.FromArgb(8, 9, 12));
                gh.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                gh.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                for (int i = 0; i < estados.Length; i++)
                {
                    int x = sep + i * (32 * zoom + sep);
                    gh.DrawImage(estados[i].Item2, new Rectangle(x, sep, 32 * zoom, 32 * zoom));
                    gh.DrawImage(estados[i].Item2, new Rectangle(x + 32 * zoom / 2 - 12, sep + 32 * zoom + 14, 24, 24));   // como en la bandeja (24 px)
                    TextRenderer.DrawText(gh, estados[i].Item1, Tema.Fina(9f), new Point(x, sep + 32 * zoom + 48), Tema.TextoSuave);
                    estados[i].Item2.Dispose();
                }
                hoja.Save(Path.Combine(dir, "bandeja-estados.png"), ImageFormat.Png);
            }
            Console.WriteLine($"  {Pastel.Salvia}✓{Fin} bandeja-estados.png");
            Console.WriteLine($"\n  {n + 1} fotos en {dir}\n");
            return 0;
        }

        // ================================================================== el audio

        /// <summary>
        /// El reproductor de verdad: abre, suena, mide que la posición avance al ritmo del reloj (× velocidad), pausa,
        /// sigue, salta y para. Con --dispositivo se puede mandar a una salida virtual (nadie escucha nada).
        /// </summary>
        public static int Audio(string archivo, double desde, double segundos, double velocidad, string dispositivo)
        {
            var b = new Pastel.Bateria();
            Console.WriteLine($"\n{Cyan}reproductor · ffmpeg → waveOut{Fin} {Apagado}({Path.GetFileName(archivo)} desde {Transcripcion.Reloj(desde)} · {velocidad}× · {(string.IsNullOrEmpty(dispositivo) ? "salida por defecto" : dispositivo)}){Fin}");
            b.Grupo("salidas de audio");
            var salidas = Reproductor.Salidas();
            b.Chequeo("winmm ve salidas", salidas.Count > 0, string.Join(" · ", salidas));
            if (!string.IsNullOrEmpty(dispositivo)) b.Chequeo($"está «{dispositivo}»", salidas.Any(s => s.IndexOf(dispositivo, StringComparison.OrdinalIgnoreCase) >= 0));
            b.Grupo("sonar, medir, pausar, saltar");
            using (var r = new Reproductor { Dispositivo = string.IsNullOrEmpty(dispositivo) ? null : dispositivo })
            {
                var fases = new List<string>();
                r.Cambio += () => { lock (fases) fases.Add(r.Estado.ToString()); };
                var reloj = Stopwatch.StartNew();
                r.Tocar(archivo, desde, velocidad);
                while (r.Estado == Reproductor.Fase.Abriendo && reloj.ElapsedMilliseconds < 8000) Thread.Sleep(10);
                long abrio = reloj.ElapsedMilliseconds;
                b.Chequeo("abre y empieza a sonar", r.Estado == Reproductor.Fase.Sonando, $"{r.Estado} en {abrio} ms {(r.Problema.Length > 0 ? "· " + r.Problema : "")}");
                if (r.Estado != Reproductor.Fase.Sonando) { b.Tarjeta("reproductor · resultado"); return b.Fallas; }
                double p0 = r.Posicion; var t0 = Stopwatch.StartNew();
                Thread.Sleep((int)(segundos * 1000));
                double avance = r.Posicion - p0, esperado = t0.Elapsed.TotalSeconds * velocidad;
                b.Chequeo("la posición avanza al ritmo del reloj × velocidad", Math.Abs(avance - esperado) < 0.25 + esperado * 0.03, $"avanzó {avance:0.00} s en {t0.Elapsed.TotalSeconds:0.00} s (esperado {esperado:0.00})");
                r.Pausar();
                Thread.Sleep(150);
                double pp = r.Posicion; Thread.Sleep(800);
                b.Chequeo("en pausa la posición se queda quieta", r.Estado == Reproductor.Fase.Pausa && Math.Abs(r.Posicion - pp) < 0.05, $"{r.Estado} · se movió {Math.Abs(r.Posicion - pp):0.000} s");
                r.Seguir();
                Thread.Sleep(600);
                b.Chequeo("sigue desde donde quedó", r.Estado == Reproductor.Fase.Sonando && r.Posicion > pp + 0.2 * velocidad, $"{r.Posicion - pp:0.00} s después de seguir");
                double destino = desde + 60;
                r.Tocar(archivo, destino, velocidad);
                reloj.Restart();
                while (r.Estado != Reproductor.Fase.Sonando && reloj.ElapsedMilliseconds < 8000) Thread.Sleep(10);
                Thread.Sleep(300);
                b.Chequeo("saltar a otro minuto", r.Estado == Reproductor.Fase.Sonando && r.Posicion >= destino && r.Posicion < destino + 2, $"en {Transcripcion.Reloj(r.Posicion)} · tardó {reloj.ElapsedMilliseconds} ms");
                r.CambiarVelocidad(1.5);
                reloj.Restart();
                while (r.Estado != Reproductor.Fase.Sonando && reloj.ElapsedMilliseconds < 8000) Thread.Sleep(10);
                double pv = r.Posicion; var tv = Stopwatch.StartNew();
                Thread.Sleep(1500);
                double av = (r.Posicion - pv) / tv.Elapsed.TotalSeconds;
                b.Chequeo("a 1,5× la posición corre 1,5 veces más rápido", Math.Abs(av - 1.5) < 0.12, $"{av:0.00}× medido");
                r.Parar();
                b.Chequeo("parar deja todo quieto", r.Estado == Reproductor.Fase.Quieto && !r.Activo);
                Thread.Sleep(300);
                bool huerfano = Process.GetProcessesByName("ffmpeg").Any(pr => { try { return pr.StartTime > DateTime.Now.AddSeconds(-30); } catch (InvalidOperationException) { return false; } catch (System.ComponentModel.Win32Exception) { return false; } });
                b.Chequeo("no queda ningún ffmpeg colgado", !huerfano);
                lock (fases) b.Dato("fases vistas", string.Join(" → ", fases.Distinct()));
            }
            b.Tarjeta("reproductor · resultado");
            return b.Fallas;
        }

        // ================================================================== ayudas

        static List<Grabacion> LeerIndiceSeguro(string datos, Pastel.Bateria b)
        {
            b.Grupo("índice de grabaciones");
            string ruta = Path.Combine(datos, "grabaciones", "indice.json");
            try
            {
                var l = Grabador.LeerIndice(ruta);
                b.Chequeo("se lee el índice real (solo lectura)", l.Count > 0, $"{l.Count} grabaciones · {l.Count(g => g.Texto.StartsWith("[", StringComparison.Ordinal))} con voces");
                return l;
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is InvalidOperationException)
            {
                b.Chequeo("se lee el índice real", false, ex.Message);
                return new List<Grabacion>();
            }
        }

        /// <summary>Una palabra de 6+ letras que aparezca varias veces: sirve para probar la búsqueda con datos de verdad.</summary>
        static string PalabraFrecuente(Transcripcion t)
        {
            var cuenta = new Dictionary<string, int>();
            foreach (var x in t.Turnos)
                foreach (Match m in Regex.Matches(x.Texto, @"\p{L}{6,}"))
                {
                    string w = m.Value.ToLowerInvariant();
                    cuenta.TryGetValue(w, out int k);
                    cuenta[w] = k + 1;
                }
            return cuenta.Count == 0 ? "reunion" : cuenta.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Skip(Math.Min(3, cuenta.Count - 1)).First().Key;
        }

        static string Corto(string s, int n) { s = (s ?? "").Replace('\n', ' ').Trim(); return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }
    }
}
