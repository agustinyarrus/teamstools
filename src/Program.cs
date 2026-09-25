using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TeamsTools
{
    internal static class Program
    {
        public static string CarpetaDatos = "";
        public static readonly int MsgMostrar = Win32.RegisterWindowMessage("TeamsTools-mostrar-panel");
        public static readonly int MsgFoto = Win32.RegisterWindowMessage("TeamsTools-foto");
        /// <summary>Cierre ORDENADO desde afuera (para reemplazar el exe): corta limpio la grabación y vacía el disco.</summary>
        public static readonly int MsgCerrar = Win32.RegisterWindowMessage("TeamsTools-cerrar");
        /// <summary>Volver a procesar una grabación (con el motor y los ajustes de HOY): el id va en reintentar-pedido.txt.</summary>
        public static readonly int MsgReintentar = Win32.RegisterWindowMessage("TeamsTools-reintentar");

        [STAThread]
        static void Main(string[] args)
        {
            try { Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // los modos de consola imprimen tildes, ñ y dibujos de caja: UTF-8 o salen signos de pregunta
            // 🚨 con un exe de VENTANAS no hay consola: fijar Console.OutputEncoding no alcanza (o tira). Se escribe
            //    UTF-8 directo sobre los flujos estándar, que es lo que ve quien redirige la salida.
            try { Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true }); } catch { }
            try { Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true }); } catch { }
            CarpetaDatos = ElegirCarpetaDatos();
            var cfg = Config.Cargar(Path.Combine(CarpetaDatos, "config.json"));
            Historia.CarpetaExtractorConfigurada = cfg.CarpetaExtractor;
            Historia.JsonlConfigurado = cfg.HistorialJsonl;
            var log = new Logger(Path.Combine(CarpetaDatos, "logs")) { IncluirDebug = cfg.LogDebug };
            var scanner = new TeamsScanner(cfg, log);

            // la densidad entra en la métrica y además multiplica las fuentes: un solo número encoge todo parejo
            Tema.FactorUI = (float)cfg.EscalaUI;
            Tema.FactorFuente = (float)cfg.EscalaFuente * Tema.FactorUI;
            bool min = args.Any(a => Es(a, "min", false));
            // --mostrar: la abre una persona (el acceso del menú Inicio). El panel se ve aunque la config diga
            // «arrancar en bandeja»: sin esto, abrirla desde Inicio con la app cerrada no mostraba nada.
            bool mostrar = args.Any(a => Es(a, "mostrar"));
            bool demo = args.Any(a => Es(a, "demo", false));
            if (args.Any(a => Es(a, "simular", false))) cfg.Simulacion = true;
            if (demo)
            {
                // la demo no toca la config real: guarda en config-demo.json
                cfg.Ruta = Path.Combine(CarpetaDatos, "config-demo.json");
                cfg.Simulacion = false; cfg.GraciaSegundos = Math.Min(cfg.GraciaSegundos, 15); cfg.IntervaloSegundos = 2; cfg.IniciarMinimizado = false;
            }

            // modos de linea de comando (sin interfaz)
            if (args.Any(a => Es(a, "dump")))
            {
                var archivos = scanner.Volcar(Path.Combine(CarpetaDatos, "volcados"));
                File.WriteAllLines(Path.Combine(CarpetaDatos, "ultimo-volcado.txt"), archivos, new UTF8Encoding(false));
                log.Info($"Volcado UIA por línea de comando: {archivos.Count} archivo/s en {Path.Combine(CarpetaDatos, "volcados")}");
                return;
            }
            if (args.Any(a => Es(a, "micromodelos")))
            {
                // corre TODOS los detectores sobre un texto y muestra puntaje + por qué
                string txt = args.SkipWhile(a => !Es(a, "micromodelos")).Skip(1).FirstOrDefault() ?? "";
                if (txt.Length == 0) txt = "che rama, urgente: se rompió PROD, mirá el NIM-409 antes de las 15:30 porfa?? quedo atento";
                Console.WriteLine("texto: " + txt);
                Console.WriteLine(new string('-', 92));
                var señales = Micromodelos.Detectar(txt);
                foreach (var s in señales.OrderByDescending(x => x.Puntaje))
                    Console.WriteLine("{0,-12} {1,3}%  {2,-22} {3}", s.Modelo, (int)(s.Puntaje * 100), s.Etiqueta, s.Evidencia);
                Console.WriteLine(new string('-', 92));
                Console.WriteLine("prioridad : " + Micromodelos.Prioridad(señales) + "/100");
                Console.WriteLine("resumen   : " + Micromodelos.Resumen(señales));
                var sen = Micromodelos.Analizar(txt);
                Console.WriteLine("banderas  : " + sen.Explicacion());
                Console.WriteLine("ticket    : " + (sen.Ticket.Length > 0 ? sen.Ticket : "—") + "   vence: " + (sen.Vence.HasValue ? sen.Vence.Value.ToString("dd/MM HH:mm") : "—") + "   idioma: " + (sen.Idioma.Length > 0 ? sen.Idioma : "—"));
                return;
            }
            if (args.Any(a => Es(a, "historia")))
            {
                // busca en el historial LOCAL de Teams, sin abrir Teams ni tocar la nube
                string q = args.SkipWhile(a => !Es(a, "historia")).Skip(1).FirstOrDefault() ?? "";
                var h = new Historia(log);
                var reloj = System.Diagnostics.Stopwatch.StartNew();
                h.Cargar();
                Console.WriteLine($"corpus: {h.Mensajes.Count:N0} mensajes · {h.ConTexto:N0} con texto · {h.Conversaciones.Length} conversaciones · {h.Autores.Length} personas · leído en {reloj.ElapsedMilliseconds} ms");
                if (h.Problema.Length > 0) { Console.WriteLine("problema: " + h.Problema); return; }
                if (h.Desde.HasValue) Console.WriteLine($"desde {h.Desde:dd/MM/yyyy} hasta {h.Hasta:dd/MM/yyyy HH:mm}");
                if (q.Length == 0) { Console.WriteLine("(pasá un texto para buscar)"); return; }
                var res = h.Buscar(q, null, null, null, null, false, 40);
                Console.WriteLine($"\n«{q}» → {res.Count} resultado/s (los 40 más nuevos)\n" + new string('-', 110));
                foreach (var m in res)
                {
                    var señ = Micromodelos.Detectar(m.Texto);
                    string t = m.Texto.Replace('\n', ' ');
                    Console.WriteLine("{0}  {1,-22} {2,-20} [{3,3}] {4}",
                        m.SinFecha ? "sin fecha       " : m.Fecha.ToString("dd/MM/yyyy HH:mm"),
                        Corta(m.Conv, 22), Corta(m.Mio ? "yo" : m.Autor, 20), Micromodelos.Prioridad(señ), Corta(t, 60));
                }
                return;
            }
            if (args.Any(a => Es(a, "fijar-presencia")))
            {
                // fija el estado en el menú del avatar por UIA: sin teclas, sin foreground, sin mostrar nada
                string quiero = args.SkipWhile(a => !Es(a, "fijar-presencia")).Skip(1).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(quiero) || quiero.StartsWith("-")) quiero = "Disponible";
                var chatP = new TeamsChat(cfg, log);
                var lecP = new LectorPresenciaLog(log, cfg.CarpetaLogsTeamsONull);
                lecP.Refrescar();
                var antes = lecP.Ultimo;
                Console.WriteLine($"antes  : {(antes != null ? antes.Estado + " desde " + antes.Cuando.ToString("HH:mm:ss") : "sin dato")}");
                IntPtr fgAntes = Win32.GetForegroundWindow();
                string detP;
                var relojP = System.Diagnostics.Stopwatch.StartNew();
                bool okP = chatP.FijarPresencia(quiero, out detP);
                Console.WriteLine($"fijar «{quiero}»: {(okP ? "OK" : "FALLÓ")} en {relojP.ElapsedMilliseconds} ms · {detP}");
                for (int i = 0; i < 20 && okP; i++)
                {
                    Thread.Sleep(2000);
                    lecP.Refrescar();
                    var ah = lecP.Ultimo;
                    if (ah != null && (antes == null || ah.Cuando > antes.Cuando)) { Console.WriteLine($"después: {ah.Estado} a las {ah.Cuando:HH:mm:ss} ← la nube lo confirmó"); break; }
                    if (i == 19) Console.WriteLine($"después: sin evento nuevo en el log (¿ya estaba en {quiero}?)");
                }
                string detLect;
                Console.WriteLine($"avatar : {chatP.PresenciaReal(out detLect)} · {detLect}");
                Console.WriteLine($"foco   : {(Win32.GetForegroundWindow() == fgAntes ? "intacto" : "CAMBIÓ")}");
                log.Escribir(okP ? Nivel.Ok : Nivel.Error, $"--fijar-presencia «{quiero}» → {(okP ? "OK" : "FALLÓ")} · {detP}");
                return;
            }
            if (args.Any(a => Es(a, "cerrar")))
            {
                // --cerrar: le pide a la instancia abierta que se cierre DE VERDAD (no a la bandeja) y espera a que salga
                var antes = Process.GetProcessesByName("TeamsTools").Where(p => p.Id != Process.GetCurrentProcess().Id).ToList();
                Win32.PostMessage(Win32.HWND_BROADCAST, MsgCerrar, IntPtr.Zero, IntPtr.Zero);
                bool salio = antes.All(p => { try { return p.WaitForExit(90000); } catch { return true; } });
                Console.WriteLine(antes.Count == 0 ? "no había ninguna instancia abierta" : salio ? "cerrada" : "no se cerró en 90 s");
                Environment.ExitCode = salio ? 0 : 1;
                return;
            }
            if (args.Any(a => Es(a, "reintentar")))
            {
                // --reintentar <id>: la instancia abierta vuelve a procesar esa grabación (p. ej. con hablantes o motor nuevo)
                string id = Valor(args, "reintentar");
                if (id.Length == 0) { Console.Error.WriteLine("uso: --reintentar <id de la grabación, p. ej. 20260923-101718>"); Environment.ExitCode = 2; return; }
                File.WriteAllText(Path.Combine(CarpetaDatos, "reintentar-pedido.txt"), id.Trim(), new UTF8Encoding(false));
                Win32.PostMessage(Win32.HWND_BROADCAST, MsgReintentar, IntPtr.Zero, IntPtr.Zero);
                Console.WriteLine("pedido: reintentar " + id.Trim());
                return;
            }
            if (args.Any(a => Es(a, "probar-transcripcion") || Es(a, "foto-transcripcion") || Es(a, "probar-audio")))
            {
                // la transcripción como conversación (PruebaTranscripcion): SOLO LECTURA sobre los datos reales.
                //   --datos DIR permite correr un exe de prueba (compilado aparte) contra los datos de la app instalada
                if (args.Any(a => Es(a, "sin-color"))) Pastel.ConColor = false;
                string datos = Valor(args, "datos");
                datos = Path.GetFullPath(datos.Length > 0 ? datos : CarpetaDatos);
                if (args.Any(a => Es(a, "probar-transcripcion"))) Environment.ExitCode = PruebaTranscripcion.Correr(datos);
                else if (args.Any(a => Es(a, "foto-transcripcion")))
                {
                    string d = Valor(args, "foto-transcripcion");
                    Environment.ExitCode = PruebaTranscripcion.Fotos(Path.GetFullPath(d.Length > 0 && !d.StartsWith("-") ? d : "fotos-transcripcion"), datos);
                }
                else
                {
                    string archivo = Valor(args, "archivo");
                    if (archivo.Length == 0)
                        archivo = Grabador.LeerIndice(Path.Combine(datos, "grabaciones", "indice.json"))
                                          .Where(g => g.TieneArchivo && File.Exists(g.Archivo)).OrderByDescending(g => g.Desde).Select(g => g.Archivo).FirstOrDefault() ?? "";
                    double Num(string k, double def) => double.TryParse(Valor(args, k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : def;
                    Environment.ExitCode = PruebaTranscripcion.Audio(archivo, Num("desde", 30), Num("segundos", 3), Num("velocidad", 1), Valor(args, "dispositivo"));
                }
                return;
            }
            if (args.Any(a => Es(a, "foto-banda")))
            {
                // --foto-banda DIR: la banda en vivo dibujada fuera de pantalla con un pulso sintético (ver FotoBanda)
                string d = Valor(args, "foto-banda");
                Environment.ExitCode = FotoBanda.Sacar(Path.GetFullPath(d.Length > 0 ? d : "fotos-banda"));
                return;
            }
            if (args.Any(a => Es(a, "foto-overlay")))
            {
                // --foto-overlay DIR: el aviso de cuenta regresiva dibujado fuera de pantalla (ver FotoExtras)
                string d = Valor(args, "foto-overlay");
                Environment.ExitCode = FotoExtras.Overlay(Path.GetFullPath(d.Length > 0 && !d.StartsWith("-") ? d : "fotos-overlay"));
                return;
            }
            if (args.Any(a => Es(a, "foto-bandeja")))
            {
                // --foto-bandeja DIR: cada estado del ícono de la bandeja y los cuadros del anillo que se llena
                string d = Valor(args, "foto-bandeja");
                Environment.ExitCode = FotoExtras.Bandeja(Path.GetFullPath(d.Length > 0 && !d.StartsWith("-") ? d : "fotos-bandeja"));
                return;
            }
            if (args.Any(a => Es(a, "probar-grabador")))
            {
                // --probar-grabador [--segundos N] [--carpeta DIR] [--sin-whisper] [--matar-loopcap]
                //   el grabador de verdad, de punta a punta, en una carpeta aparte: ver PruebaGrabador
                int seg; if (!int.TryParse(Valor(args, "segundos"), out seg) || seg <= 0) seg = 20;
                string dir = Valor(args, "carpeta");
                if (dir.Length == 0) dir = Path.Combine(CarpetaDatos, "prueba-grabador");
                string archivo = Valor(args, "archivo");
                if (args.Any(a => Es(a, "mezcla-mic")))
                    Environment.ExitCode = PruebaGrabador.CorrerMezclaMic(log, Path.GetFullPath(dir));
                else if (archivo.Length > 0)
                {
                    int trozo; if (!int.TryParse(Valor(args, "trozo"), out trozo) || trozo <= 0) trozo = 60;
                    string idioma = Valor(args, "idioma"); if (idioma.Length == 0) idioma = "es";
                    string calidad = Valor(args, "calidad"); if (calidad.Length == 0) calidad = "rapido";
                    var claves = Valor(args, "claves").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray();
                    string transcriptor = Valor(args, "transcriptor");
                    Environment.ExitCode = PruebaGrabador.CorrerConArchivo(log, Path.GetFullPath(dir), Path.GetFullPath(archivo), idioma, trozo, claves, calidad,
                        transcriptor.Length > 0 ? Path.GetFullPath(transcriptor) : "", Valor(args, "falla-en"));
                }
                else
                    Environment.ExitCode = PruebaGrabador.Correr(log, Path.GetFullPath(dir), seg, args.Any(a => Es(a, "sin-whisper")), args.Any(a => Es(a, "matar-loopcap")));
                Disco.Vaciar();
                return;
            }
            if (args.Any(a => Es(a, "probar-presencia-log")))
            {
                Console.Write(PruebaPresenciaLog.Correr(log));
                return;
            }
            if (args.Any(a => Es(a, "presencia-log")))
            {
                // volcado de la presencia REAL leída del log nativo de Teams — no toca nada, solo lee
                var lec = new LectorPresenciaLog(log, cfg.CarpetaLogsTeamsONull);
                lec.Refrescar();
                var sb = new StringBuilder();
                sb.AppendLine($"archivo seguido : {(lec.Archivo.Length > 0 ? lec.Archivo : "(ninguno)")}");
                sb.AppendLine($"problema        : {(lec.Problema.Length > 0 ? lec.Problema : "—")}");
                sb.AppendLine($"transiciones    : {lec.Eventos.Count}   ·   hoy: {lec.CambiosHoy}");
                sb.AppendLine();
                foreach (var ev in lec.Eventos) sb.AppendLine($"  {ev.Cuando:dd/MM HH:mm:ss}  {ev.Token,-13} {ev.Estado,-18} {ev.Fuente}");
                sb.AppendLine();
                sb.AppendLine("minutos por estado hoy:");
                foreach (var kv in lec.MinutosPorEstadoHoy().OrderByDescending(k => k.Value)) sb.AppendLine($"  {kv.Key,-20} {kv.Value,8:0.00} min");
                var u = lec.Ultimo;
                sb.AppendLine();
                sb.AppendLine(u == null ? "ultimo: (ninguno)" : $"ultimo: {u.Estado} desde {u.Cuando:dd/MM HH:mm:ss} (hace {(int)(DateTime.Now - u.Cuando).TotalMinutes} min) por {u.Fuente}");
                string destino = Path.Combine(CarpetaDatos, "presencia-log.txt");
                File.WriteAllText(destino, sb.ToString(), new UTF8Encoding(false));
                Console.Write(sb.ToString());
                log.Info($"--presencia-log: {lec.Eventos.Count} transiciones · volcado en {destino}");
                return;
            }
            if (args.Any(a => Es(a, "probar-regla")))
            {
                // manda la respuesta de una regla a MI PROPIO chat por el camino real (lista -> seleccionar -> formato -> fallback), sin UI
                string nombreRegla = args.SkipWhile(a => !Es(a, "probar-regla")).Skip(1).FirstOrDefault() ?? "";
                var contactos = Contactos.Cargar(Path.Combine(CarpetaDatos, "contactos.json"));
                var reglas = ConfigRespuestas.Cargar(Path.Combine(CarpetaDatos, "respuestas.json"));
                var chat = new TeamsChat(cfg, log);
                var yo = contactos.Lista.FirstOrDefault(k => k.Apodo == "yo");
                if (yo != null) { chat.MiNombre = yo.Nombre; if (yo.Correo.Length > 0) chat.MiCorreo = yo.Correo; }
                var regla = reglas.Reglas.FirstOrDefault(r => nombreRegla.Length > 0 && r.Nombre.Equals(nombreRegla, StringComparison.OrdinalIgnoreCase)) ?? reglas.Reglas.FirstOrDefault(r => r.Activa);
                if (regla == null) { log.Aviso("--probar-regla: no hay reglas"); return; }
                var prog = new Programador(cfg, log, new Recordatorios(), contactos, chat, null);
                bool minAntes = chat.BuscarVentana() && Win32.IsIconic(chat.Hwnd);
                IntPtr fg0 = Win32.GetForegroundWindow();
                var res = prog.EnviarA(chat.MiNombre, chat.MiCorreo, regla.RespuestaHtml, regla.RespuestaTexto);
                bool minDespues = chat.Hwnd != IntPtr.Zero && Win32.IsIconic(chat.Hwnd);
                log.Escribir(res.Ok ? Nivel.Ok : Nivel.Error, $"--probar-regla «{regla.Nombre}» → {(res.Ok ? "OK" : "FALLÓ")} · {res.Detalle} · Teams minimizado antes={minAntes} después={minDespues} · foco devuelto={Win32.GetForegroundWindow() == fg0}");
                return;
            }
            if (args.Any(a => Es(a, "probar-envio")))
            {
                // manda un ENVÍO de varias partes (texto + imagen + texto) por el camino real, sin interfaz.
                // 🚨 por defecto va SOLO a mi propio chat; para mandarle a otra persona hay que pedirlo explícito con --para
                string para = Valor(args, "para");
                string img = Valor(args, "imagen");
                var contactos = Contactos.Cargar(Path.Combine(CarpetaDatos, "contactos.json"));
                var chat = new TeamsChat(cfg, log);
                var yo = contactos.Lista.FirstOrDefault(k => k.Apodo == "yo");
                if (yo != null) { chat.MiNombre = yo.Nombre; if (yo.Correo.Length > 0) chat.MiCorreo = yo.Correo; }
                string destino = para.Length > 0 ? para : chat.MiNombre;
                string correo = para.Length > 0 ? (contactos.Mejor(para)?.Correo ?? (para.Contains("@") ? para : "")) : chat.MiCorreo;
                if (para.Length == 0) log.Info("--probar-envio: sin --para, mando a mi propio chat");
                else log.Aviso($"--probar-envio: destinatario explícito «{destino}» — le va a llegar de verdad");

                var envio = new Envio();
                envio.Partes.Add(Parte.DeTexto("<b>prueba de envío</b> · parte 1 de 3 · texto con <i>formato</i>"));
                if (img.Length > 0 && File.Exists(img)) envio.Partes.Add(Parte.DeImagen(Path.GetFullPath(img), "parte 2 de 3 · imagen"));
                else if (img.Length > 0) log.Aviso("--probar-envio: no existe la imagen " + img);
                envio.Partes.Add(Parte.DeTexto("parte 3 de 3 · y una lista:<ul><li>uno</li><li>dos</li></ul>"));

                var prog = new Programador(cfg, log, new Recordatorios(), contactos, chat, null);
                IntPtr fg0 = Win32.GetForegroundWindow();
                var res = prog.EnviarA(destino, correo, envio);
                bool minDespues = chat.Hwnd != IntPtr.Zero && Win32.IsIconic(chat.Hwnd);
                log.Escribir(res.Ok ? Nivel.Ok : Nivel.Error,
                    $"--probar-envio → {(res.Ok ? "OK" : "FALLÓ")} · {res.Detalle} · Teams minimizado={minDespues} · foco devuelto={Win32.GetForegroundWindow() == fg0}");
                return;
            }
            if (args.Any(a => Es(a, "redactar")))
            {
                // --redactar "<mensaje que llegó>" [--de "Apellido, Nombre"] [--como "indicación extra"] [--tokens N]
                //   Prueba el puente con el modelo local SIN abrir la interfaz. 🚨 Sirve sobre todo para ver si el
                //   servidor contesta de verdad: un llama-server con --jinja y un modelo que razona pasa el /health
                //   perfecto y devuelve `content` VACÍO, así que «está disponible» no quiere decir «funciona».
                string entrante = args.SkipWhile(a => !Es(a, "redactar")).Skip(1).FirstOrDefault() ?? "";
                if (entrante.StartsWith("-")) entrante = "";
                if (entrante.Length == 0) entrante = "¿pudiste ver lo del deploy? avisame cuando puedas";
                // --url permite probar OTRO servidor sin tocar el que está en 8080. Sirve para medir un
                // modelo candidato en un puerto aparte sin bajarle al user el que ya tiene levantado.
                string urlOtra = Valor(args, "url");
                if (urlOtra.Length > 0) { AsistenteIA.Url = urlOtra.TrimEnd('/'); log.Info("--redactar: apunto a " + AsistenteIA.Url + " en vez del de siempre"); }
                string porque;
                if (!AsistenteIA.Verificar(out porque)) { log.Error("--redactar: el modelo local no está · " + porque); return; }
                log.Info("--redactar: modelo local OK · " + porque);
                int tok; if (!int.TryParse(Valor(args, "tokens"), out tok) || tok <= 0) tok = 120;
                string det;
                var reloj = System.Diagnostics.Stopwatch.StartNew();
                string salida = AsistenteIA.Redactar(entrante, Valor(args, "de"), Valor(args, "como"), tok, out det);
                reloj.Stop();
                log.Info("--redactar ← «" + Corta(entrante, 90) + "»");
                if (salida.Length == 0) log.Error("--redactar → NADA · " + det);
                else log.Ok("--redactar → «" + salida.Replace('\n', ' ').Replace('\r', ' ') + "» · " + det + " · total " + reloj.ElapsedMilliseconds + " ms");
                return;
            }
            if (args.Any(a => Es(a, "corrillos")))
            {
                // --corrillos [--top N]  →  quién estuvo en call con quién, por las tres fuentes.
                //   Sirve para ver de una si el partlist del historial se está leyendo: sin UI, sin esperar.
                var h = new Historia(log);
                var relojC = System.Diagnostics.Stopwatch.StartNew();
                h.Cargar();
                Console.WriteLine($"historial: {h.Mensajes.Count:N0} mensajes · {h.Llamadas.Length:N0} eventos de llamada · {relojC.ElapsedMilliseconds} ms");
                if (h.Problema.Length > 0) { Console.WriteLine("problema: " + h.Problema); return; }
                int conGente = h.Llamadas.Count(m => m.Llamada != null && m.Llamada.Any(x => x.EsPersona));
                Console.WriteLine($"de esos, con participantes con nombre: {conGente:N0}");

                var co = new Corrillos();
                co.Calcular(null, Path.Combine(CarpetaDatos, "presencia.jsonl"), Path.Combine(CarpetaDatos, "corrillos.jsonl"), h);
                Console.WriteLine($"\ncorrillos: {co.Todos.Length}  ·  de la llamada {co.DeLaLlamada} · roster {co.DelRoster} · deducidos {co.Deducidos}  ({co.Ms} ms)");
                Console.WriteLine($"sos: «{co.Yo}» · tuyas: {co.Mias.Length}");

                int topC; if (!int.TryParse(Valor(args, "top"), out topC) || topC <= 0) topC = 12;
                Console.WriteLine("\n--- las últimas ---");
                foreach (var c in co.Todos.Take(topC))
                    Console.WriteLine("  {0:dd/MM HH:mm}  {1,6}  {2,2}p  {3,-12} {4}{5}",
                        c.Ini, Presencia.Fmt((int)c.Dura.TotalSeconds), c.Personas, c.ComoLoSe,
                        c.ConVos ? "★ " : "  ", Corta(c.Nombres(6), 70));

                Console.WriteLine("\n--- con quién hablás vos ---");
                foreach (var x in co.Companeros(12))
                    Console.WriteLine("  {0,4} call/s  {1,8}  {2,3} a solas  {3,5:P0} de las suyas   {4}",
                        x.Juntos, Presencia.Fmt((int)x.Tiempo.TotalSeconds), x.SoloUstedes, x.Cercania, x.Nombre);

                Console.WriteLine("\n--- parejas que más coinciden ---");
                foreach (var t in co.Parejas(10))
                    Console.WriteLine("  {0,4}×  {1} + {2}", t.Item3, VistaEquipo.Apellido(t.Item1), VistaEquipo.Apellido(t.Item2));

                Console.WriteLine("\n--- grupos que se repiten ---");
                foreach (var t in co.Recurrentes(3, 8))
                    Console.WriteLine("  {0,3} veces · {1,7}  {2}", t.Item2, Presencia.Fmt((int)t.Item3.TotalSeconds), Corta(t.Item1, 70));

                Console.WriteLine("\n--- quién vive en llamadas ---");
                foreach (var t in co.Ranking(10))
                    Console.WriteLine("  {0,9}  {1,4} call/s   {2}", Presencia.Fmt((int)t.Item2.TotalSeconds), t.Item3, t.Item1);
                return;
            }
            if (args.Any(a => Es(a, "foto")))
            {
                // --foto <pestaña> [--salida ruta.png] [--ancho N --alto N]
                //   Le pide a la instancia que ya está corriendo que se retrate. Es la única forma honesta de
                //   revisar una pestaña: la foto sale del estado REAL, con los datos de verdad adentro.
                string cual = args.SkipWhile(a => !Es(a, "foto")).Skip(1).FirstOrDefault() ?? "0";
                if (cual.StartsWith("-")) cual = "0";
                string[] nombres = { "vigia", "mensajes", "cron", "personalizados", "perfil", "ia", "llamadas", "patrones", "equipo", "dia", "salud", "historia" };
                int tab;
                if (!int.TryParse(cual, out tab))
                {
                    tab = Array.FindIndex(nombres, n => n.StartsWith(cual.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase));
                    if (tab < 0) { Console.Error.WriteLine("no conozco la pestaña «" + cual + "»; son: " + string.Join(", ", nombres)); return; }
                }
                string salida = Valor(args, "salida");
                if (salida.Length == 0) salida = Path.Combine(CarpetaDatos, "capturas", "foto-" + nombres[tab] + ".png");
                salida = Path.GetFullPath(salida);
                string sw = Valor(args, "ancho"), sh = Valor(args, "alto");
                string modo = Valor(args, "modo"), esp = Valor(args, "espera");
                try { if (File.Exists(salida)) File.Delete(salida); } catch { }
                Directory.CreateDirectory(Path.Combine(CarpetaDatos, "capturas"));
                File.WriteAllText(Path.Combine(CarpetaDatos, "foto-pedido.txt"),
                    tab + "|" + salida + "|" + (sw.Length > 0 ? sw : "0") + "|" + (sh.Length > 0 ? sh : "0")
                        + "|" + modo + "|" + (esp.Length > 0 ? esp : "0"), new UTF8Encoding(false));
                Win32.PostMessage(Win32.HWND_BROADCAST, MsgFoto, IntPtr.Zero, IntPtr.Zero);
                for (int i = 0; i < 450 && !File.Exists(salida); i++) Thread.Sleep(100);
                if (File.Exists(salida)) Console.WriteLine(salida);
                else Console.Error.WriteLine("no salió la foto · ¿está corriendo TeamsTools?");
                return;
            }
            if (args.Any(a => Es(a, "salir")))
            {
                var L = scanner.Leer();
                if (!L.HayLlamada) { log.Aviso("--salir: no veo ninguna llamada de Teams"); return; }
                var r = scanner.Salir(L.Hwnd, cfg.MetodoSalida);
                if (r.Ok) log.Ok($"--salir: salí de «{L.Reunion}» ({r.Detalle})"); else log.Error($"--salir: no pude salir de «{L.Reunion}»: {r.Detalle}");
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) => { try { log.Error("Excepción no controlada: " + e.ExceptionObject); Disco.Vaciar(1500); } catch { } };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => { try { Disco.Vaciar(1500); } catch { } };
            Application.ThreadException += (s, e) => { try { log.Error("Excepción en la UI: " + e.Exception); } catch { } };

            using (var unico = new Mutex(true, demo ? @"Local\TeamsTools-demo" : @"Local\TeamsTools-instancia-unica", out bool nuevo))
            {
                if (!nuevo)
                {
                    // ya hay una corriendo: pedirle que muestre el panel y listo. Salvo con --min, que es el arranque
                    // con Windows: llega ~10 min después del login y no tiene por qué destapar una ventana que nadie pidió.
                    if (!min) Win32.PostMessage(Win32.HWND_BROADCAST, MsgMostrar, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                ILector lector = demo ? (ILector)new LectorDemo(log) : scanner;
                Application.Run(new MainForm(cfg, log, lector, min, demo, mostrar));
            }
        }

        /// <summary>Valor de una opción de línea de comando: --nombre valor (o --nombre=valor). "" si no está.</summary>
        static string Valor(string[] args, string nombre)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = (args[i] ?? "").TrimStart('-', '/');
                int eq = a.IndexOf('=');
                if (eq > 0 && a.Substring(0, eq).Equals(nombre, StringComparison.OrdinalIgnoreCase)) return a.Substring(eq + 1).Trim('"');
                if (a.Equals(nombre, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1];
            }
            return "";
        }

        static string Corta(string s, int n) { s = (s ?? "").Trim(); return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        static bool Es(string arg, string nombre) => Es(arg, nombre, true);

        /// <summary>
        /// ¿Este argumento es la opción `nombre`? Con `exigeGuion` (lo normal) tiene que venir como --nombre,
        /// -nombre o /nombre.
        ///
        /// 🚨 Sin exigir el guion, el VALOR de una opción se confunde con una bandera: `--foto equipo --modo
        ///    historia` disparaba el modo `--historia` y la app volcaba el corpus en vez de sacar la foto.
        ///    Pasó de verdad y costó encontrarlo porque solo fallaba el valor que se llamaba igual que un modo.
        ///    Las dos banderas de arranque siguen aceptándose peladas porque los accesos directos las pasan así.
        /// </summary>
        static bool Es(string arg, string nombre, bool exigeGuion)
        {
            if (string.IsNullOrEmpty(arg)) return false;
            if (exigeGuion && arg[0] != '-' && arg[0] != '/') return false;
            string a = arg.TrimStart('-', '/').ToLowerInvariant();
            int eq = a.IndexOf('=');
            if (eq > 0) a = a.Substring(0, eq);
            return a == nombre;
        }

        /// <summary>datos\ al lado del exe si se puede escribir (app portable); si no, %LOCALAPPDATA%\TeamsTools.</summary>
        static string ElegirCarpetaDatos()
        {
            try
            {
                string junto = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "datos");
                Directory.CreateDirectory(junto);
                string prueba = Path.Combine(junto, ".escritura");
                File.WriteAllText(prueba, "ok");
                File.Delete(prueba);
                return junto;
            }
            catch { }
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeamsTools");
            Directory.CreateDirectory(local);
            return local;
        }
    }
}
