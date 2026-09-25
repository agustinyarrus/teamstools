using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace TeamsTools
{
    /// <summary>Una fila del mapa de calor: un día, 24 celdas (una por hora) y su etiqueta.</summary>
    internal sealed class FilaMapa
    {
        public string Etiqueta = "";
        public Color[] Horas = new Color[24];
        public string Extra = "";
    }

    /// <summary>
    /// Mapa de calor día × hora, dibujado a mano. Cada celda es el estado que más pesó en esa hora.
    /// Es la forma más compacta de ver una semana entera de presencia de un vistazo.
    /// </summary>
    internal sealed class Mapa : Control
    {
        public List<FilaMapa> Filas = new List<FilaMapa>();
        public string Etiqueta = "", Vacio = "todavía no hay días para comparar", Resumen = "";

        public Mapa()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Tema.Fondo;
            TabStop = false;
        }

        public void Poner(List<FilaMapa> f, string resumen = null) { Filas = f ?? new List<FilaMapa>(); if (resumen != null) Resumen = resumen; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float esc = Dpi.Escala(this);
            int S(int px) => Dpi.S(esc, px);
            Tema.Tarjeta_(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), S(10), Tema.Panel, Tema.Panel);

            int pd = S(5);
            using (var b = new SolidBrush(Tema.Cielo)) g.FillEllipse(b, S(14), S(10) + (S(14) - pd) / 2f, pd, pd);
            var fEnc = Tema.Media(7.5f);
            int xEti = S(14) + pd + S(7), derecha = Width - S(14);
            int anchoRes = Resumen.Length > 0 ? Tema.Medir(g, Resumen, fEnc).Width : 0;
            bool cabeRes = anchoRes > 0 && derecha - xEti - anchoRes >= S(70);
            if (cabeRes) Tema.Texto_(g, Resumen, fEnc, Tema.Apagado, new Rectangle(derecha - anchoRes, S(10), anchoRes, S(14)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            Tema.Texto_(g, Etiqueta.ToUpperInvariant(), fEnc, Tema.Apagado, new Rectangle(xEti, S(10), Math.Max(S(20), (cabeRes ? derecha - anchoRes - S(10) : derecha) - xEti), S(14)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (Filas.Count == 0) { Tema.Texto_(g, Vacio, Tema.Fina(9f), Tema.Apagado, new Rectangle(S(16), S(32), Width - S(32), S(18))); return; }

            int xLab = S(16), wLab = S(52);
            int x0 = xLab + wLab + S(6), ancho = Width - x0 - S(16) - S(46);
            int celda = Math.Max(S(4), ancho / 24), hueco = Math.Max(1, S(2));
            // con pocos días las bandas se hacen más gruesas en vez de dejar medio panel vacío
            int top = S(32), alto = Math.Max(S(9), Math.Min(S(30), (Height - top - S(20)) / Math.Max(1, Filas.Count)));

            // regla de horas: 0, 6, 12, 18
            var fh = Tema.Mono(6.5f);
            foreach (int h in new[] { 0, 6, 12, 18 })
                Tema.Texto_(g, h.ToString("00"), fh, Tema.MuyApagado, new Rectangle(x0 + celda * h, top - S(13), celda * 3, S(12)));

            int y = top;
            var fl = Tema.Mono(7f);
            foreach (var f in Filas)
            {
                if (y + alto > Height - S(6)) break;
                Tema.Texto_(g, f.Etiqueta, fl, Tema.Apagado, new Rectangle(xLab, y, wLab, alto), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                for (int h = 0; h < 24; h++)
                {
                    var c = f.Horas[h];
                    if (c == Color.Empty) c = Tema.Alpha(Tema.Tarjeta, 90);
                    using (var b = new SolidBrush(c))
                    using (var p = Tema.Redondeado(new RectangleF(x0 + celda * h, y + 1, celda - hueco, alto - 2), S(2)))
                        g.FillPath(b, p);
                }
                if (f.Extra.Length > 0)
                    Tema.Texto_(g, f.Extra, fl, Tema.MuyApagado, new Rectangle(x0 + celda * 24 + S(6), y, S(44), alto), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                y += alto;
            }
        }
    }

    // =====================================================================================================
    // PATRONES: qué cosas raras pasan con la presencia
    // =====================================================================================================
    internal sealed class VistaPatrones : Pantalla
    {
        readonly Presencia pres;
        readonly LectorPresenciaLog logTeams;
        readonly DiarioPresencia diario;
        /// <summary>Se REEMPLAZA entero cuando termina un análisis de fondo: la UI nunca lee uno a medio calcular.</summary>
        Patrones motor = new Patrones();
        bool analizando, primerAnalisis = true;
        readonly Tabla tHallazgos = new Tabla
        {
            Etiqueta = "lo que encontré", Vacio = "todavía no junté suficiente historia", AltoFila = 20,
            Columnas =
            {
                new Columna { Titulo = "qué", Peso = 0.9f, MinAncho = 96 },
                new Columna { Titulo = "quién", Peso = 0.5f, MinAncho = 108 },
                new Columna { Titulo = "detalle", Peso = 2.4f, MinAncho = 120 },
                new Columna { Titulo = "cuándo", Peso = 0, MinAncho = 54, Derecha = true, Mono = true },
            }
        };
        readonly Tabla tCorrillos = new Tabla
        {
            Etiqueta = "quién está en call con quién", Vacio = "todavía no vi a nadie junto en una llamada", AltoFila = 20,
            Columnas =
            {
                new Columna { Titulo = "cuándo", Peso = 0, MinAncho = 76, Mono = true },
                new Columna { Titulo = "duró", Peso = 0, MinAncho = 50, Derecha = true, Mono = true },
                new Columna { Titulo = "quiénes", Peso = 2.6f, MinAncho = 190 },
                new Columna { Titulo = "cuántos", Peso = 0, MinAncho = 52, Derecha = true, Mono = true },
                new Columna { Titulo = "llegaron tarde", Peso = 1f, MinAncho = 110 },
                new Columna { Titulo = "cómo lo sé", Peso = 0, MinAncho = 118 },
            }
        };
        readonly Ficha fResumen = new Ficha { Etiqueta = "mi presencia en números", Acento = Tema.Cyan };
        readonly Ficha fLlamadas = new Ficha { Etiqueta = "las llamadas del equipo", Acento = Tema.Rosa, Vacio = "sin llamadas observadas todavía" };
        readonly Barras gParejas = new Barras { Etiqueta = "quiénes se juntan más seguido", Vacio = "hace falta ver más llamadas" };
        readonly Barras gHoras = new Barras { Etiqueta = "a qué hora se te mueve el estado", Vacio = "hace falta más de un día de historia" };
        bool calculandoCorrillos;
        Chip chAnalizar, chVolcar, chColumnas, chSoltar;
        Rectangle rCards;
        string ultimo = "";
        /// <summary>Cuántos hallazgos son de los que hay que mirar: va como insignia en la pestaña.</summary>
        public int ParaMirar { get; private set; }

        public VistaPatrones(Contexto c, Presencia p, LectorPresenciaLog l, DiarioPresencia d) : base(c)
        {
            pres = p; logTeams = l; diario = d;
            Controls.Add(tHallazgos); Controls.Add(fResumen); Controls.Add(gHoras);
            Controls.Add(tCorrillos); Controls.Add(fLlamadas); Controls.Add(gParejas);
            chAnalizar = Nuevo("analizar ahora", Chip.Modo.Boton, Tema.Cyan, (o, e) => Refrescar());
            chVolcar = Nuevo("abrir el diario", Chip.Modo.Boton, Tema.Malva, (o, e) => AbrirDiario());
            // el borde de cada columna ya se puede arrastrar; esto lo hace por vos en las dos tablas
            chColumnas = Nuevo("acomodar columnas", Chip.Modo.Boton, Tema.Durazno, (o, e) =>
            { tHallazgos.AjustarTodas(); tCorrillos.AjustarTodas(); }, "o arrastrá el borde");
            chSoltar = Nuevo("volver al automático", Chip.Modo.Boton, Tema.Apagado, (o, e) =>
            { tHallazgos.SoltarAnchos(); tCorrillos.SoltarAnchos(); });

            // 🚨 el historial se carga de fondo y tarda ~1 s: si no se espera a que avise, el cruce corre
            //    con el corpus vacío y esta pantalla se queda solo con lo deducido, que es la peor fuente.
            if (ctx.Chats != null) ctx.Chats.Cambio += () => { try { ctx.EnUi?.Invoke(RecalcularCorrillos); } catch { } };
        }

        void AbrirDiario()
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + System.IO.Path.Combine(Program.CarpetaDatos, "mi-presencia.jsonl") + "\""); }
            catch (Exception ex) { ctx.Aviso?.Invoke("No pude abrir el diario", ex.Message); }
        }

        public override void Acomodar()
        {
            int pad = S(4), y = S(6);
            rCards = new Rectangle(pad, y, Width - pad * 2, S(74));
            y = rCards.Bottom + S(10);
            y = Flujo(chips, pad, y, Width - pad * 2) + S(4);
            int anchoDer = (int)((Width - pad * 2) * 0.34);
            int anchoIzq = Width - pad * 2 - anchoDer - S(12);
            int alto = Math.Max(S(120), Height - y - S(10));

            // izquierda: los hallazgos arriba, las llamadas del equipo abajo (antes ese hueco quedaba vacío)
            int altoHall = (int)(alto * 0.50);
            tHallazgos.SetBounds(pad, y, anchoIzq, altoHall);
            tCorrillos.SetBounds(pad, y + altoHall + S(12), anchoIzq, alto - altoHall - S(12));

            // derecha: cuatro fichas apiladas en vez de dos
            int xd = pad + anchoIzq + S(12), yd = y;
            int hRes = (int)(alto * 0.36), hLla = (int)(alto * 0.28), hPar = (int)(alto * 0.18);
            fResumen.SetBounds(xd, yd, anchoDer, hRes); yd += hRes + S(10);
            fLlamadas.SetBounds(xd, yd, anchoDer, hLla); yd += hLla + S(10);
            gParejas.SetBounds(xd, yd, anchoDer, hPar); yd += hPar + S(10);
            gHoras.SetBounds(xd, yd, anchoDer, Math.Max(S(70), y + alto - yd));
        }

        /// <summary>
        /// Relee el historial de presencia y deduce quién estuvo en call con quién. Va a un hilo aparte
        /// porque lee un archivo entero y cruza todas las sesiones contra todas.
        /// </summary>
        void RecalcularCorrillos()
        {
            if (calculandoCorrillos || ctx.Corrillos == null) return;
            if (ctx.Chats != null && ctx.Chats.Trabajando) return;      // esperar a que termine de leer
            // 🚨 la fuente buena de «quién con quién» es el partlist del historial: si nadie lo cargó
            //    todavía (pasa si no se abrió la pestaña equipo), pedirlo acá. Sin eso esta pantalla se
            //    queda solo con lo deducido, que es lo peor de las tres fuentes.
            if (ctx.Chats != null && !ctx.Chats.Hay && !ctx.Chats.Trabajando) ctx.Chats.CargarAsync();
            calculandoCorrillos = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                using (Tareas.Empezar("cruzando las llamadas del equipo", "quién estuvo con quién", Tema.Rosa))
                {
                    try { ctx.Corrillos.Calcular(ctx.Observador?.Registro, ctx.RutaPresencia, ctx.RutaCorrillos, ctx.Chats); }
                    catch (Exception ex) { ctx.Log?.Error("Corrillos: " + ex.Message); }
                }
                calculandoCorrillos = false;
                try { ctx.EnUi?.Invoke(LlenarCorrillos); } catch { }
            });
        }

        void LlenarCorrillos()
        {
            var co = ctx.Corrillos;
            if (co == null) return;
            tCorrillos.Cargando = calculandoCorrillos || (ctx.Chats != null && ctx.Chats.Trabajando);

            var filas = new List<FilaTabla>();
            foreach (var c in co.Todos.Take(120))
            {
                var tinte = c.Viva ? Tema.Rosa : c.Confirmado ? Tema.Salvia : c.Personas >= 4 ? Tema.Malva : Tema.Cielo;
                filas.Add(FilaTabla.F(c, tinte,
                    Celda.C(c.Ini.ToString(c.Ini.Date == DateTime.Today ? "'hoy' HH:mm" : "dd/MM HH:mm"), Tema.Apagado),
                    Celda.C(c.Viva ? "sigue" : VistaEquipo.Corto(c.Dura), c.Viva ? Tema.Rosa : Tema.TextoSuave, c.Viva),
                    Celda.C(c.Nombres(), Tema.Texto, c.Viva),
                    Celda.C(c.Personas.ToString(), c.Personas >= 4 ? Tema.Malva : Tema.TextoSuave),
                    Celda.C(c.Tarde(), Tema.Durazno),
                    new Celda
                    {
                        Texto = c.ComoLoSe,
                        Color = c.Fuente == FuenteCorrillo.Llamada ? Tema.Salvia
                              : c.Fuente == FuenteCorrillo.Roster ? Tema.Cyan
                              : c.Confianza >= 0.8 ? Tema.Cielo : Tema.Apagado,
                        Insignia = c.ConVos ? "con vos" : "", ColorInsignia = Tema.Cyan,
                    }));
            }
            tCorrillos.Poner(filas, null, co.Todos.Length + " llamada/s · " + co.Solitarias.Length + " en solitario");

            // la ficha: lo que una lista de llamadas no cuenta de un vistazo
            var hoy = co.DeHoy;
            var vivos = co.Vivos;
            var cerradas = co.Todos.Where(c => !c.Viva).ToList();
            var porPersona = co.Todos.SelectMany(c => c.Gente)
                .GroupBy(g => Contactos.Normalizar(g.Persona))
                .Select(g => new { Nombre = g.First().Persona, Tiempo = TimeSpan.FromSeconds(g.Sum(x => x.Dura.TotalSeconds)), Veces = g.Count() })
                .OrderByDescending(x => x.Tiempo).ToList();
            var masLarga = cerradas.OrderByDescending(c => c.Dura).FirstOrDefault();
            var masGente = co.Todos.OrderByDescending(c => c.Personas).FirstOrDefault();
            var horaPico = co.Todos.GroupBy(c => c.Ini.Hour).OrderByDescending(g => g.Count()).FirstOrDefault();

            var d = new List<Dato>();
            d.Add(Dato.D("en call ahora mismo", vivos.Length > 0 ? vivos.Sum(c => c.Personas) + " en " + vivos.Length + " llamada/s" : "nadie",
                vivos.Length > 0 ? Tema.Rosa : Tema.Apagado));
            if (vivos.Length > 0) d.Add(Dato.D("están juntos", vivos[0].Nombres(4), Tema.Texto));
            d.Add(Dato.D("llamadas hoy", hoy.Length.ToString(), hoy.Length > 0 ? Tema.Cyan : Tema.Apagado));
            d.Add(Dato.D("llamadas vistas", co.Todos.Length + " de a 2 o más", Tema.TextoSuave));
            d.Add(Dato.D("de la lista de Teams", co.DeLaLlamada.ToString(), Tema.Salvia));
            d.Add(Dato.D("del roster · deducidas", co.DelRoster + " · " + co.Deducidos, Tema.Apagado));
            d.Add(Dato.D("tuyas", co.Mias.Length.ToString(), Tema.Cyan));
            if (cerradas.Count > 0)
                d.Add(Dato.D("cuánto duran", VistaEquipo.Corto(TimeSpan.FromSeconds(Mediana(cerradas.Select(c => c.Dura.TotalSeconds).ToList()))) + " de mediana", Tema.TextoSuave));
            if (masLarga != null) d.Add(Dato.D("la más larga", VistaEquipo.Corto(masLarga.Dura) + " · " + masLarga.Nombres(2), Tema.Durazno));
            if (masGente != null) d.Add(Dato.D("la más concurrida", masGente.Personas + " personas · " + masGente.Ini.ToString("dd/MM HH:mm"), Tema.Malva));
            if (horaPico != null) d.Add(Dato.D("se juntan sobre las", horaPico.Key.ToString("00") + ":00", Tema.Cielo));
            var comp = co.Companeros(5);
            if (comp.Length > 0)
            {
                d.Add(Dato.Titulo("con quién hablás vos"));
                foreach (var x in comp)
                    d.Add(Dato.D(VistaEquipo.Apellido(x.Nombre),
                        x.Juntos + " call/s · " + VistaEquipo.Corto(x.Tiempo)
                        + (x.SoloUstedes > 0 ? " · " + x.SoloUstedes + " a solas" : ""), Tema.Cyan));
            }
            var recu = co.Recurrentes(3, 3);
            if (recu.Length > 0)
            {
                d.Add(Dato.Titulo("grupos que se repiten"));
                foreach (var r in recu)
                    d.Add(Dato.D(r.Item1, r.Item2 + " veces · " + VistaEquipo.Corto(r.Item3), Tema.Malva));
            }
            if (porPersona.Count > 0)
            {
                d.Add(Dato.Titulo("quién vive en reuniones"));
                foreach (var x in porPersona.Take(3))
                    d.Add(Dato.D(VistaEquipo.Apellido(x.Nombre), VistaEquipo.Corto(x.Tiempo) + " · " + x.Veces + " call/s", Tema.TextoSuave));
            }
            if (co.Problema.Length > 0) d.Add(Dato.D("problema", co.Problema, Tema.Rosa));
            fLlamadas.Poner(d);

            gParejas.Poner(co.Parejas(10)
                .Select(t => new Barra
                {
                    Etiqueta = VistaEquipo.Apellido(t.Item1) + " + " + VistaEquipo.Apellido(t.Item2),
                    Valor = t.Item3, Color = Tema.Rosa, Extra = t.Item3 + "×",
                }).ToList(), co.Sesiones + " sesiones");
        }

        static double Mediana(List<double> xs)
        {
            if (xs.Count == 0) return 0;
            xs.Sort();
            return xs.Count % 2 == 1 ? xs[xs.Count / 2] : (xs[xs.Count / 2 - 1] + xs[xs.Count / 2]) / 2.0;
        }

        /// <summary>
        /// El análisis corre DE FONDO (antes: releer el log de Teams + el diario + el motor, todo en el hilo de la UI
        /// cada 10 s). La presencia ya relee el log sola; acá se toma su foto, se absorbe al diario y se analiza en un
        /// motor NUEVO que reemplaza al viejo al terminar. Mientras tanto, la tabla muestra el esqueleto.
        /// </summary>
        public override void Refrescar()
        {
            RecalcularCorrillos();
            LlenarCorrillos();
            if (analizando) return;
            analizando = true;
            if (primerAnalisis) tHallazgos.Cargando = true;
            var eventos = logTeams?.Eventos;
            var movs = ctx.Observador?.Registro.Movimientos();
            var gente = ctx.Observador?.Registro.Gente();
            Fondo.Correr(this, "analizando tus patrones", () =>
            {
                if (eventos != null) diario.Absorber(eventos);
                var m = new Patrones();
                m.Analizar(diario.Cambios, movs, gente, pres);
                return m;
            }, m => { motor = m; analizando = false; primerAnalisis = false; Pintar(); },
               ex => { analizando = false; tHallazgos.Cargando = false; }, Tema.Cyan, mostrar: primerAnalisis, log: ctx.Log);
        }

        /// <summary>Vuelca el motor (ya calculado) a la pantalla. Solo lee: cero cálculo, cero I/O.</summary>
        void Pintar()
        {

            var filas = new List<FilaTabla>();
            foreach (var h in motor.Hallazgos)
                filas.Add(FilaTabla.F(h, h.Tinte,
                    Celda.C(h.Titulo, h.Tinte, h.Peso >= 2),
                    Celda.C(h.Quien, h.Quien == "yo" ? Tema.Cyan : Tema.TextoSuave),
                    Celda.C(h.Detalle, Tema.TextoSuave),
                    Celda.C(h.Cuando.ToString("HH:mm"), Tema.Apagado)));
            int graves = motor.Hallazgos.Count(h => h.Peso >= 2);
            ParaMirar = graves;
            tHallazgos.Poner(filas, null, filas.Count == 0 ? "" : $"{graves} para mirar · {filas.Count - graves} de color");

            double totalHoy = motor.MinutosHoy.Values.Sum();
            var fichas = new List<Dato>
            {
                Dato.D("cambios hoy", motor.CambiosHoy.ToString(), motor.MedianaCambiosDia > 0 && motor.CambiosHoy > motor.MedianaCambiosDia * 2 ? Tema.Durazno : Tema.Texto),
                Dato.D("ayer · mediana del día", $"{motor.CambiosAyer} · {motor.MedianaCambiosDia:0}", Tema.TextoSuave),
                Dato.D("primer cambio de hoy", motor.PrimerCambioHoy.HasValue ? motor.PrimerCambioHoy.Value.ToString("HH:mm:ss") : "—", Tema.Cielo),
                Dato.D("último cambio", motor.UltimoCambioHoy.HasValue ? motor.UltimoCambioHoy.Value.ToString("HH:mm:ss") : "—", Tema.Cielo),
                Dato.D("racha más larga de hoy", motor.RachaMasLarga.TotalMinutes >= 1 ? $"{Presencia.Fmt((int)motor.RachaMasLarga.TotalSeconds)} en {motor.RachaEstado}" : "—", Tema.Malva),
                Dato.D("tramos del día", motor.TramosHoy.Count.ToString(), Tema.TextoSuave),
                Dato.Titulo("reparto de hoy"),
            };
            foreach (var kv in motor.MinutosHoy.OrderByDescending(k => k.Value))
                fichas.Add(Dato.D(kv.Key.ToLowerInvariant(), $"{Presencia.Fmt((int)(kv.Value * 60))} · {(totalHoy > 0 ? kv.Value / totalHoy * 100 : 0):0} %", ColorDe(kv.Key)));
            fichas.Add(Dato.Titulo("historia"));
            fichas.Add(Dato.D("días con datos", diario.DiasConDatos.ToString(), diario.DiasConDatos >= 3 ? Tema.Salvia : Tema.Durazno));
            fichas.Add(Dato.D("desde", diario.Desde.HasValue ? diario.Desde.Value.ToString("dd/MM HH:mm") : "—", Tema.TextoSuave));
            fichas.Add(Dato.D("cambios guardados", diario.Cambios.Count.ToString(), Tema.TextoSuave));
            if (diario.Problema.Length > 0) fichas.Add(Dato.D("problema del diario", diario.Problema, Tema.Rosa));
            fResumen.Poner(fichas);

            var barras = new List<Barra>();
            for (int h = 0; h < 24; h++)
                if (motor.CambiosPorHora[h] > 0)
                    barras.Add(new Barra { Etiqueta = h.ToString("00") + " h", Valor = (int)motor.CambiosPorHora[h], Color = h < 7 || h > 21 ? Tema.Malva : Tema.Cielo });
            gHoras.Poner(barras.OrderByDescending(b => b.Valor).Take(12).ToList(), $"{motor.CambiosPorHora.Sum():0} cambios en total");

            ultimo = DateTime.Now.ToString("HH:mm:ss");
            Invalidate(rCards);
        }

        internal static Color ColorDe(string p)
        {
            if (Estados.EsDisponible(p)) return Tema.Salvia;
            if (Estados.EsAusente(p)) return Tema.Durazno;
            if (Estados.EsOcupado(p)) return Tema.Rosa;
            if (Estados.EsDesconectado(p)) return Tema.MuyApagado;
            return Tema.TextoSuave;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            int graves = motor.Hallazgos.Count(h => h.Peso >= 2);
            var co = ctx.Corrillos;
            var vivos = co != null ? co.Vivos : new Corrillo[0];
            int enCall = vivos.Sum(c => c.Personas);

            var r = Tarjetas(rCards, new[] { 1f, 1f, 1.5f, 1f, 1.1f, 1.2f });
            Ayuda.Tarjeta(g, esc, r[0], "PARA MIRAR", graves.ToString(), graves == 0 ? "nada raro por ahora" : "hallazgos con peso", graves > 0 ? Tema.Rosa : Tema.Salvia);
            Ayuda.Tarjeta(g, esc, r[1], "HALLAZGOS", motor.Hallazgos.Count.ToString(), "incluyendo curiosidades", Tema.Cyan);
            Ayuda.Tarjeta(g, esc, r[2], "EN CALL AHORA", enCall > 0 ? enCall.ToString() : "nadie",
                enCall > 0 ? (vivos.Length == 1 ? "juntos: " + vivos[0].Nombres(3) : "en " + vivos.Length + " llamadas distintas")
                           : (co != null && co.Hay ? co.Todos.Length + " llamadas vistas en total" : "todavía no vi ninguna"),
                enCall > 0 ? Tema.Rosa : Tema.Apagado, enCall > 0 ? Tema.Fina(15f) : (Font)null);
            Ayuda.Tarjeta(g, esc, r[3], "CAMBIOS HOY", motor.CambiosHoy.ToString(), motor.MedianaCambiosDia > 0 ? $"mediana {motor.MedianaCambiosDia:0}" : "sin comparación aún", Tema.Malva);
            Ayuda.Tarjeta(g, esc, r[4], "RACHA MÁS LARGA", motor.RachaMasLarga.TotalMinutes >= 1 ? Presencia.Fmt((int)motor.RachaMasLarga.TotalSeconds) : "—", motor.RachaEstado.Length > 0 ? "en " + motor.RachaEstado : "", Tema.Cielo);
            Ayuda.Tarjeta(g, esc, r[5], "DIARIO", diario.DiasConDatos + " día/s", $"{diario.Cambios.Count} cambios guardados · analizado {ultimo}", Tema.Crema);
        }
    }

    // =====================================================================================================
    // EQUIPO: quién está, quién se conecta y quién se va
    // =====================================================================================================
    // =====================================================================================================
    // DÍA: cómo se ve tu presencia hora por hora
    // =====================================================================================================
    internal sealed class VistaDia : Pantalla
    {
        readonly DiarioPresencia diario;
        readonly Mapa mapa = new Mapa { Etiqueta = "tu presencia, hora por hora" };
        readonly Tabla tTramos = new Tabla
        {
            Etiqueta = "los tramos de hoy", Vacio = "todavía no hay tramos", AltoFila = 19,
            Columnas =
            {
                new Columna { Titulo = "desde", Peso = 0, MinAncho = 48, Mono = true },
                new Columna { Titulo = "hasta", Peso = 0, MinAncho = 48, Mono = true },
                new Columna { Titulo = "estado", Peso = 1.4f, MinAncho = 70 },
                new Columna { Titulo = "duró", Peso = 0, MinAncho = 52, Derecha = true, Mono = true },
                new Columna { Titulo = "parte del día", Peso = 1f, MinAncho = 60, Derecha = true, Mono = true },
            }
        };
        readonly Ficha fDias = new Ficha { Etiqueta = "días comparados", Acento = Tema.Cielo };
        readonly Patrones motor = new Patrones();
        Chip chDias;
        int diasAMostrar = 14;
        Rectangle rCards;
        static readonly int[] PresetsDias = { 7, 14, 30 };

        public VistaDia(Contexto c, DiarioPresencia d) : base(c)
        {
            diario = d;
            Controls.Add(mapa); Controls.Add(tTramos); Controls.Add(fDias);
            chDias = Nuevo("últimos 14 días", Chip.Modo.Valor, Tema.Cielo, (o, e) =>
            {
                int i = Array.IndexOf(PresetsDias, diasAMostrar);
                diasAMostrar = PresetsDias[(i + 1) % PresetsDias.Length];
                chDias.Text = "últimos " + diasAMostrar + " días";
                chDias.Ajustar(); Acomodar(); Refrescar();
            });
        }

        public override void Acomodar()
        {
            int pad = S(4), y = S(6);
            rCards = new Rectangle(pad, y, Width - pad * 2, S(74));
            y = rCards.Bottom + S(10);
            y = Flujo(chips, pad, y, Width - pad * 2) + S(4);
            int alto = Math.Max(S(140), Height - y - S(10));
            // el mapa pide solo lo que necesita: una banda por día. Con 1 día no se come media pantalla.
            int altoMapa = Math.Min((int)(alto * 0.5), Math.Max(S(86), S(52) + Math.Max(1, mapa.Filas.Count) * S(22)));
            mapa.SetBounds(pad, y, Width - pad * 2, altoMapa);
            int y2 = y + altoMapa + S(12), alto2 = alto - altoMapa - S(12);
            int anchoFicha = (int)((Width - pad * 2) * 0.32);
            tTramos.SetBounds(pad, y2, Width - pad * 2 - anchoFicha - S(12), alto2);
            fDias.SetBounds(Width - pad - anchoFicha, y2, anchoFicha, alto2);
        }

        public override void Refrescar()
        {
            motor.Analizar(diario.Cambios, null, null, null);

            // mapa: una fila por día, cada hora pintada con el estado que más pesó
            var filas = new List<FilaMapa>();
            var porDia = diario.Cambios.GroupBy(c => c.Cuando.Date).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Cuando).ToList());
            for (int d = diasAMostrar - 1; d >= 0; d--)
            {
                var dia = DateTime.Today.AddDays(-d);
                var eventos = new List<MiCambio>();
                var previo = diario.Cambios.LastOrDefault(c => c.Cuando < dia);
                if (previo != null) eventos.Add(new MiCambio { Cuando = dia, Estado = previo.Estado, Token = previo.Token });
                if (porDia.ContainsKey(dia)) eventos.AddRange(porDia[dia]);
                if (eventos.Count == 0) continue;

                var fila = new FilaMapa { Etiqueta = dia.ToString("ddd dd").ToLowerInvariant() };
                var minutos = new Dictionary<string, double>[24];
                for (int h = 0; h < 24; h++) minutos[h] = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                var finDia = dia == DateTime.Today ? DateTime.Now : dia.AddDays(1);
                for (int i = 0; i < eventos.Count; i++)
                {
                    var ini = eventos[i].Cuando < dia ? dia : eventos[i].Cuando;
                    var fin = i + 1 < eventos.Count ? eventos[i + 1].Cuando : finDia;
                    if (fin <= ini) continue;
                    for (var t = ini; t < fin; t = t.Date.AddHours(t.Hour + 1))
                    {
                        var corte = t.Date.AddHours(t.Hour + 1);
                        if (corte > fin) corte = fin;
                        int h = t.Hour;
                        string k = eventos[i].Estado;
                        minutos[h][k] = (minutos[h].ContainsKey(k) ? minutos[h][k] : 0) + (corte - t).TotalMinutes;
                        if (corte >= fin) break;
                    }
                }
                double disp = 0, tot = 0;
                for (int h = 0; h < 24; h++)
                {
                    if (minutos[h].Count == 0) continue;
                    var top = minutos[h].OrderByDescending(k => k.Value).First();
                    fila.Horas[h] = Tema.Alpha(VistaPatrones.ColorDe(top.Key), (int)(120 + 135 * Math.Min(1, top.Value / 60.0)));
                    tot += minutos[h].Values.Sum();
                    disp += minutos[h].Where(k => Estados.EsDisponible(k.Key)).Sum(k => k.Value);
                }
                fila.Extra = tot > 0 ? $"{disp / tot * 100:0}%" : "";
                filas.Add(fila);
            }
            bool cambioAlto = filas.Count != mapa.Filas.Count;
            mapa.Poner(filas, filas.Count > 0 ? $"{filas.Count} día/s · el % es cuánto estuviste Disponible" : "");
            if (cambioAlto) Acomodar();   // el alto del mapa depende de cuántos días hay

            double totalHoy = motor.MinutosHoy.Values.Sum();
            var ft = new List<FilaTabla>();
            foreach (var t in Enumerable.Reverse(motor.TramosHoy))
                ft.Add(FilaTabla.F(t, VistaPatrones.ColorDe(t.Estado),
                    Celda.C(t.Desde.ToString("HH:mm:ss"), Tema.Apagado),
                    Celda.C(t.Hasta > DateTime.Now.AddSeconds(-2) ? "ahora" : t.Hasta.ToString("HH:mm:ss"), Tema.Apagado),
                    Celda.C(t.Estado, VistaPatrones.ColorDe(t.Estado), t.Dura.TotalMinutes > 60),
                    Celda.C(VistaEquipo.Corto(t.Dura), Tema.TextoSuave),
                    Celda.C(totalHoy > 0 ? $"{t.Dura.TotalMinutes / totalHoy * 100:0} %" : "—", Tema.Apagado)));
            tTramos.Poner(ft, null, $"{motor.TramosHoy.Count} tramos");

            var datos = new List<Dato>();
            foreach (var g in diario.Cambios.GroupBy(c => c.Cuando.Date).OrderByDescending(g => g.Key).Take(10))
                datos.Add(Dato.D(g.Key.ToString("ddd dd/MM").ToLowerInvariant(), g.Count() + " cambios", g.Key == DateTime.Today ? Tema.Cyan : Tema.TextoSuave));
            if (datos.Count == 0) datos.Add(Dato.D("historia", "todavía nada guardado", Tema.Apagado));
            datos.Insert(0, Dato.Titulo("cambios por día"));
            fDias.Poner(datos);
            Invalidate(rCards);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            double tot = motor.MinutosHoy.Values.Sum();
            double disp = motor.MinutosHoy.Where(k => Estados.EsDisponible(k.Key)).Sum(k => k.Value);
            double aus = motor.MinutosHoy.Where(k => Estados.EsAusente(k.Key)).Sum(k => k.Value);
            var r = Tarjetas(rCards, new[] { 1f, 1f, 1f, 1.2f });
            Ayuda.Tarjeta(g, esc, r[0], "HOY DISPONIBLE", tot > 0 ? $"{disp / tot * 100:0} %" : "—", Presencia.Fmt((int)(disp * 60)), Tema.Salvia);
            Ayuda.Tarjeta(g, esc, r[1], "HOY AUSENTE", tot > 0 ? $"{aus / tot * 100:0} %" : "—", Presencia.Fmt((int)(aus * 60)), aus > disp ? Tema.Rosa : Tema.Durazno);
            Ayuda.Tarjeta(g, esc, r[2], "TRAMOS", motor.TramosHoy.Count.ToString(), motor.RachaMasLarga.TotalMinutes >= 1 ? "el más largo " + VistaEquipo.Corto(motor.RachaMasLarga) : "", Tema.Malva);
            Ayuda.Tarjeta(g, esc, r[3], "HISTORIA", diario.DiasConDatos + " día/s", diario.Desde.HasValue ? "desde el " + diario.Desde.Value.ToString("dd/MM") : "", Tema.Cielo);
        }
    }

    // =====================================================================================================
    // SALUD: ¿está todo funcionando de verdad?
    // =====================================================================================================
    internal sealed class VistaSalud : Pantalla
    {
        readonly Presencia pres;
        readonly LectorPresenciaLog logTeams;
        readonly Tabla tChequeos = new Tabla
        {
            Etiqueta = "chequeos", Vacio = "tocá «revisar todo»", AltoFila = 20,
            Columnas =
            {
                new Columna { Titulo = "qué miro", Peso = 1.2f, MinAncho = 110 },
                new Columna { Titulo = "resultado", Peso = 0, MinAncho = 70 },
                new Columna { Titulo = "evidencia", Peso = 2.6f, MinAncho = 140 },
                new Columna { Titulo = "ms", Peso = 0, MinAncho = 40, Derecha = true, Mono = true },
            }
        };
        readonly Ficha fEntorno = new Ficha { Etiqueta = "entorno", Acento = Tema.Durazno };
        readonly Cargador carga = new Cargador { Modo = Cargador.Estilo.Anillo, Acento = Tema.Cyan, MostrarTiempo = true };
        Chip chRevisar, chLog;
        Rectangle rCards;
        int ok, mal, avisos;
        string cuando = "nunca";
        volatile bool corriendo;

        public VistaSalud(Contexto c, Presencia p, LectorPresenciaLog l) : base(c)
        {
            pres = p; logTeams = l;
            Controls.Add(tChequeos); Controls.Add(fEntorno); Controls.Add(carga);
            chRevisar = Nuevo("revisar todo", Chip.Modo.Boton, Tema.Cyan, (o, e) => Revisar());
            chLog = Nuevo("abrir la carpeta de datos", Chip.Modo.Boton, Tema.Malva, (o, e) =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", Program.CarpetaDatos); } catch { }
            });
        }

        public override void Acomodar()
        {
            int pad = S(4), y = S(6);
            rCards = new Rectangle(pad, y, Width - pad * 2, S(74));
            y = rCards.Bottom + S(10);
            y = Flujo(chips, pad, y, Width - pad * 2);
            carga.SetBounds(pad, y, Math.Max(S(200), Width - pad * 2), S(18));
            y += S(22);
            int alto = Math.Max(S(140), Height - y - S(10));
            int anchoFicha = (int)((Width - pad * 2) * 0.32);
            tChequeos.SetBounds(pad, y, Width - pad * 2 - anchoFicha - S(12), alto);
            fEntorno.SetBounds(Width - pad - anchoFicha, y, anchoFicha, alto);
        }

        public override void Refrescar()
        {
            var w = ctx.Chat;
            var datos = new List<Dato>
            {
                Dato.D("Teams corriendo", w != null && w.TeamsCorriendo ? "sí" : "no", w != null && w.TeamsCorriendo ? Tema.Salvia : Tema.Rosa),
                Dato.D("ventana de chat", w != null && w.Hwnd != IntPtr.Zero ? w.Hwnd.ToString() : "sin montar", w != null && w.Hwnd != IntPtr.Zero ? Tema.Salvia : Tema.Durazno),
                Dato.Titulo("presencia"),
                Dato.D("fuente", pres.FuentePresencia.Length > 0 ? pres.FuentePresencia : "—", pres.FuentePresencia == "log nativo" ? Tema.Cyan : Tema.Malva),
                Dato.D("medición fresca", pres.MedicionFresca ? "sí" : "no", pres.MedicionFresca ? Tema.Salvia : Tema.Durazno),
                Dato.D("estado leído", pres.PresenciaTeams.Length > 0 ? pres.PresenciaTeams : "sin medir", VistaPatrones.ColorDe(pres.PresenciaTeams)),
                Dato.D("respetando manual", pres.RespetandoManual ? "sí" : "no", pres.RespetandoManual ? Tema.Crema : Tema.Apagado),
                Dato.D("rendido", pres.Rendido ? "SÍ" : "no", pres.Rendido ? Tema.Rosa : Tema.Salvia),
                Dato.D("toques · fallos", $"{pres.Toques} · {pres.Fallos}", pres.Fallos > 0 ? Tema.Rosa : Tema.Salvia),
                Dato.D("derivas · corregidas", $"{pres.Derivas} · {pres.Correcciones}", pres.Derivas > pres.Correcciones ? Tema.Durazno : Tema.Salvia),
                Dato.D("forzados · sondas", $"{pres.Forzados} · {pres.Sondas}", Tema.TextoSuave),
                Dato.Titulo("log nativo de Teams"),
                Dato.D("archivo", logTeams.Archivo.Length > 0 ? logTeams.Archivo : "—", Tema.TextoSuave),
                Dato.D("transiciones", logTeams.Eventos.Count.ToString(), logTeams.Eventos.Count > 0 ? Tema.Cyan : Tema.Durazno),
                Dato.D("problema", logTeams.Problema.Length > 0 ? logTeams.Problema : "ninguno", logTeams.Problema.Length > 0 ? Tema.Rosa : Tema.Salvia),
            };
            fEntorno.Poner(datos);
            Invalidate(rCards);
        }

        /// <summary>«revisar» corre los chequeos (para fotografiar la pestaña con resultados sin el mouse).</summary>
        public override void Modo(string que) { if (string.Equals(que, "revisar", StringComparison.OrdinalIgnoreCase)) Revisar(); }

        void Revisar()
        {
            if (corriendo) return;
            corriendo = true;
            // 🚨 el texto nuevo es más ancho: hay que reacomodar la fila o se come al chip de al lado
            chRevisar.Text = "revisando…"; chRevisar.Ajustar(); Acomodar();
            carga.Texto = "corriendo los chequeos"; carga.Desde = DateTime.Now; carga.Activo = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                using (var tarea = Tareas.Empezar("revisando la salud de la app", "10 chequeos en vivo", Tema.Cyan))
                {
                var filas = new List<FilaTabla>();
                int o = 0, m = 0, a = 0;
                void Anota(string que, bool bien, bool aviso, string evidencia, long ms)
                {
                    var color = bien ? Tema.Salvia : aviso ? Tema.Durazno : Tema.Rosa;
                    if (bien) o++; else if (aviso) a++; else m++;
                    filas.Add(FilaTabla.F(null, color,
                        Celda.C(que, Tema.Texto, true),
                        Celda.C(bien ? "bien" : aviso ? "ojo" : "mal", color, true),
                        Celda.C(evidencia, Tema.TextoSuave),
                        Celda.C(ms.ToString(), Tema.Apagado)));
                }

                var reloj = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    // 1) Teams vivo
                    reloj.Restart();
                    bool corre = ctx.Chat != null && ctx.Chat.TeamsCorriendo;
                    Anota("Teams está corriendo", corre, false, corre ? "hay procesos ms-teams" : "no veo ms-teams.exe", reloj.ElapsedMilliseconds);

                    // 2) log nativo
                    reloj.Restart();
                    logTeams.Refrescar();
                    var ult = logTeams.Ultimo;
                    Anota("leo el log nativo de Teams", ult != null, ult == null,
                        ult != null ? $"último: {ult.Estado} a las {ult.Cuando:HH:mm:ss} por {ult.Fuente} en {logTeams.Archivo}" : (logTeams.Problema.Length > 0 ? logTeams.Problema : "sin transiciones todavía"),
                        reloj.ElapsedMilliseconds);

                    // 3) árbol de UIA
                    reloj.Restart();
                    string det = "";
                    string leido = ctx.Chat != null ? ctx.Chat.PresenciaReal(out det) : "";
                    det = det ?? "";
                    Anota("leo el avatar por UIA", leido.Length > 0, leido.Length == 0,
                        leido.Length > 0 ? $"«{leido}» · {det}" : det + " (normal si Teams está dormido en la bandeja)", reloj.ElapsedMilliseconds);

                    // 4) la tecla fantasma reinicia la inactividad
                    reloj.Restart();
                    int antes = Presencia.IdleAhora();
                    System.Threading.Thread.Sleep(1100);
                    int medio = Presencia.IdleAhora();
                    bool subio = medio >= antes;
                    Anota("el reloj de inactividad avanza", subio, false, $"idle {antes} s → {medio} s", reloj.ElapsedMilliseconds);

                    // 5) sesión y pantalla
                    reloj.Restart();
                    bool bloq = Win32.SesionBloqueada();
                    Anota("sesión desbloqueada", !bloq, bloq, bloq ? "con la sesión bloqueada ningún input sostiene la presencia" : "se puede inyectar input", reloj.ElapsedMilliseconds);

                    // 6) carpeta de datos escribible
                    reloj.Restart();
                    bool escribe = false; string evid;
                    try
                    {
                        string p = System.IO.Path.Combine(Program.CarpetaDatos, ".prueba-escritura");
                        System.IO.File.WriteAllText(p, "ok");
                        System.IO.File.Delete(p);
                        escribe = true; evid = Program.CarpetaDatos;
                    }
                    catch (Exception ex) { evid = ex.Message; }
                    Anota("puedo escribir en datos", escribe, false, evid, reloj.ElapsedMilliseconds);

                    // 7) coherencia entre las dos fuentes
                    reloj.Restart();
                    bool coincide = leido.Length == 0 || ult == null || string.Equals(leido, ult.Estado, StringComparison.OrdinalIgnoreCase);
                    Anota("el log y el avatar coinciden", coincide, !coincide,
                        ult == null || leido.Length == 0 ? "no pude comparar (falta una de las dos)" : $"log dice «{ult.Estado}», avatar dice «{leido}»", reloj.ElapsedMilliseconds);

                    // 8) permanencia online al día
                    reloj.Restart();
                    bool sano = !pres.Rendido && (pres.Derivas == 0 || pres.Correcciones > 0);
                    Anota("la permanencia online da abasto", sano, pres.Rendido,
                        pres.Rendido ? "me rendí: Teams vuelve a Ausente pase lo que pase" : $"{pres.Derivas} derivas · {pres.Correcciones} corregidas · {pres.Forzados} forzados", reloj.ElapsedMilliseconds);

                    // 9) los micromodelos contestan
                    reloj.Restart();
                    var prueba = Micromodelos.Detectar("urgente: se rompió PROD, mirá el NIM-409 antes de las 15:30");
                    int prio = Micromodelos.Prioridad(prueba);
                    Anota("los micromodelos clasifican", prio >= 50, prio > 0 && prio < 50,
                        $"mensaje de prueba → prioridad {prio}/100 · {prueba.Count(x => x.Prendida)} de {prueba.Count} señales", reloj.ElapsedMilliseconds);

                    // 10) el LLM local, que es opcional a propósito
                    reloj.Restart();
                    string porqueIA;
                    bool hayIA = AsistenteIA.Verificar(out porqueIA);
                    Anota("el modelo local está a mano", hayIA, !hayIA, porqueIA, reloj.ElapsedMilliseconds);
                }
                catch (Exception ex) { Anota("la revisión explotó", false, false, ex.GetType().Name + ": " + ex.Message, 0); }

                try
                {
                    ctx.EnUi?.Invoke(() =>
                    {
                        ok = o; mal = m; avisos = a; cuando = DateTime.Now.ToString("HH:mm:ss");
                        tChequeos.Poner(filas, null, $"{o} bien · {a} ojo · {m} mal");
                        corriendo = false;
                        carga.Activo = false; carga.Texto = "";
                        chRevisar.Text = "revisar todo"; chRevisar.Ajustar(); Acomodar();
                        Refrescar();
                    });
                }
                catch { corriendo = false; }
                }
            });
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            var r = Tarjetas(rCards, new[] { 1f, 1f, 1f, 1.4f });
            string veredicto = mal > 0 ? "hay algo roto" : avisos > 0 ? "anda con reservas" : ok > 0 ? "todo en orden" : "sin revisar";
            var cv = mal > 0 ? Tema.Rosa : avisos > 0 ? Tema.Durazno : ok > 0 ? Tema.Salvia : Tema.Apagado;
            Ayuda.Tarjeta(g, esc, r[0], "VEREDICTO", veredicto, "revisado " + cuando, cv, Tema.Fina(15f));
            Ayuda.Tarjeta(g, esc, r[1], "BIEN", ok.ToString(), "chequeos en verde", Tema.Salvia);
            Ayuda.Tarjeta(g, esc, r[2], "OJO · MAL", $"{avisos} · {mal}", "avisos y fallas", mal > 0 ? Tema.Rosa : avisos > 0 ? Tema.Durazno : Tema.Apagado);
            Ayuda.Tarjeta(g, esc, r[3], "PRESENCIA AHORA", pres.MedicionFresca ? pres.PresenciaTeams : "sin medir",
                pres.FuentePresencia.Length > 0 ? "por " + pres.FuentePresencia : "", VistaPatrones.ColorDe(pres.PresenciaTeams), Tema.Fina(15f));
        }
    }

    /// <summary>Pintado compartido por las pantallas nuevas: la tarjetita etiqueta / valor / sub.</summary>
    internal static class Ayuda
    {
        public static void Tarjeta(Graphics g, float esc, Rectangle r, string etiqueta, string valor, string sub, Color acento, Font fValor = null)
        {
            int S(int px) => Dpi.S(esc, px);
            Tema.Tarjeta_(g, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(10), Tema.Panel, Tema.Panel);
            int pd = S(5), px0 = r.Left + S(14);
            using (var b = new SolidBrush(acento)) g.FillEllipse(b, px0, r.Top + S(10) + (S(14) - pd) / 2f, pd, pd);
            Tema.Texto_(g, etiqueta.ToUpperInvariant(), Tema.Media(7.5f), Tema.Apagado, new Rectangle(px0 + pd + S(7), r.Top + S(10), r.Width - S(28), S(14)));
            Tema.Texto_(g, valor ?? "—", fValor ?? Tema.Fina(17f), Tema.Texto, new Rectangle(px0, r.Top + S(22), r.Width - S(26), S(28)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (!string.IsNullOrEmpty(sub))
                Tema.Texto_(g, sub, Tema.Fina(8.5f), Tema.TextoSuave, new Rectangle(px0, r.Bottom - S(22), r.Width - S(26), S(16)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}
