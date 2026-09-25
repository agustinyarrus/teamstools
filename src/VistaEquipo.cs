using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace TeamsTools
{
    // =====================================================================================================
    // EQUIPO: quién está conectado AHORA cruzado con todo lo que pasó entre ustedes
    //
    // El observador de la lista de chats solo sabe el presente. El historial del disco solo sabe el pasado.
    // Juntos contestan las preguntas que ninguno contesta solo: a quién le debo una respuesta, quién me hace
    // esperar, con quién hablaba seguido y hace dos meses que no. Cuatro LENTES sobre la misma gente.
    // =====================================================================================================
    internal sealed class VistaEquipo : Pantalla
    {
        enum Lente { Ahora = 0, Historia = 1, Esperas = 2, Ritmo = 3 }

        /// <summary>Una persona vista por los dos lados: el vivo del observador y el vínculo del historial.</summary>
        sealed class Cara
        {
            public string Nombre = "";
            public EstadoPersona Vivo;      // null si no está en la lista de chats ahora
            public Vinculo V;               // null si nunca te escribió
            public string Presencia => Vivo != null ? Vivo.Presencia : "";
            public bool Conectado => Vivo != null && Estados.EsConectado(Vivo.Presencia);
        }

        readonly Historia chats;
        readonly Relaciones rel = new Relaciones();

        readonly Tabla tGente = new Tabla { Etiqueta = "el equipo", Vacio = "todavía no leí la lista de chats", AltoFila = 20 };
        readonly Tabla tMovs = new Tabla
        {
            Etiqueta = "idas y venidas", Vacio = "sin movimientos todavía", AltoFila = 18,
            Columnas =
            {
                new Columna { Titulo = "hora", Peso = 0, MinAncho = 44, Mono = true },
                new Columna { Titulo = "persona", Peso = 1.3f, MinAncho = 70 },
                new Columna { Titulo = "de", Peso = 1f, MinAncho = 54 },
                new Columna { Titulo = "a", Peso = 1f, MinAncho = 54 },
            }
        };
        readonly Barras gRanking = new Barras { Etiqueta = "ranking", Vacio = "sin datos suficientes" };
        readonly Ficha fPersona = new Ficha { Etiqueta = "la persona elegida", Acento = Tema.Cielo, Vacio = "elegí a alguien de la lista" };
        readonly Ficha fEquipo = new Ficha { Etiqueta = "resumen del equipo", Acento = Tema.Salvia };
        readonly Segmentado segLente = new Segmentado
        {
            Etiqueta = "cómo mirarlos",
            Opciones = new[] { "ahora", "historia", "esperas", "ritmo" },
            Acento = Tema.Cielo,
            Tintes = new[] { Tema.Salvia, Tema.Malva, Tema.Durazno, Tema.Cyan },
        };
        readonly Cargador carga = new Cargador { Modo = Cargador.Estilo.Puntos, Acento = Tema.Malva, MostrarTiempo = true };

        Chip chLeer, chSoloConectados, chSoloHistoria, chReleer;
        bool soloConectados, soloHistoria;
        bool pedidoHistorial, calculando;
        Lente lente = Lente.Ahora;
        Rectangle rCards;
        string elegida = "";

        public VistaEquipo(Contexto c, Historia h) : base(c)
        {
            chats = h;
            Controls.Add(tGente); Controls.Add(tMovs); Controls.Add(gRanking);
            Controls.Add(fPersona); Controls.Add(fEquipo); Controls.Add(segLente); Controls.Add(carga);
            segLente.Superficie = Tema.Fondo;

            chLeer = Nuevo("leer la lista ahora", Chip.Modo.Boton, Tema.Cyan, (o, e) => LeerAhora());
            chSoloConectados = Nuevo("solo conectados", Chip.Modo.Toggle, Tema.Salvia, (o, e) =>
            { soloConectados = !soloConectados; chSoloConectados.Activo = soloConectados; chSoloConectados.Invalidate(); Refrescar(); });
            chSoloHistoria = Nuevo("solo con historial", Chip.Modo.Toggle, Tema.Malva, (o, e) =>
            { soloHistoria = !soloHistoria; chSoloHistoria.Activo = soloHistoria; chSoloHistoria.Invalidate(); Refrescar(); });
            chReleer = Nuevo("releer el historial", Chip.Modo.Boton, Tema.Durazno, (o, e) => CargarHistorial(true));

            segLente.Cambio += (o, e) => { lente = (Lente)segLente.Elegido; Columnas(); Refrescar(); };
            tGente.SeleccionCambio += (o, e) => { var f = tGente.Actual; elegida = f != null && f.Tag is Cara ? ((Cara)f.Tag).Nombre : ""; MostrarPersona(); };
            if (chats != null) chats.Cambio += () => { try { ctx.EnUi?.Invoke(Recalcular); } catch { } };
            Columnas();
        }

        /// <summary>«ahora» · «historia» · «esperas» · «ritmo» — para poder fotografiar cada lente sin el mouse.</summary>
        public override void Modo(string que)
        {
            que = (que ?? "").Trim().ToLowerInvariant();
            if (que.Length == 0) return;
            var nombres = new[] { "ahora", "historia", "esperas", "ritmo" };
            int i = Array.FindIndex(nombres, n => n.StartsWith(que, StringComparison.Ordinal));
            if (i < 0) return;
            lente = (Lente)i; segLente.Poner(i); Columnas(); Refrescar();
        }

        // ------------------------------------------------------------------ columnas según la lente
        static Columna Col(string t, float peso, int min, bool der = false, bool mono = false) =>
            new Columna { Titulo = t, Peso = peso, MinAncho = min, Derecha = der, Mono = mono };

        void Columnas()
        {
            var cs = new List<Columna> { Col("persona", 1.15f, 96), Col("ahora", 0.8f, 66) };
            switch (lente)
            {
                case Lente.Ahora:
                    tGente.Etiqueta = "el equipo, como lo veo desde la lista de chats";
                    cs.AddRange(new[]
                    {
                        Col("hace", 0, 48, true, true), Col("cambios", 0, 50, true, true),
                        Col("disponible", 0, 58, true, true), Col("ausente", 0, 54, true, true),
                        Col("ocupado", 0, 54, true, true), Col("sin conexión", 0, 58, true, true),
                        Col("% disponible", 0, 56, true, true), Col("sin leer", 0, 46, true, true),
                        Col("1ª vez", 0, 48, true, true), Col("visto", 0, 48, true, true),
                    });
                    break;
                case Lente.Historia:
                    tGente.Etiqueta = "lo que se escribieron EN PRIVADO, desde siempre";
                    cs.AddRange(new[]
                    {
                        Col("en privado", 0, 62, true, true), Col("suyos", 0, 52, true, true), Col("míos", 0, 52, true, true),
                        Col("quién habla", 0.55f, 96), Col("en grupos", 0, 58, true, true), Col("días juntos", 0, 56, true, true),
                        Col("desde", 0, 56, true, true), Col("último", 0, 56, true, true),
                        Col("silencio", 0, 54, true, true), Col("la pelota", 0, 56),
                    });
                    break;
                case Lente.Esperas:
                    tGente.Etiqueta = "quién hace esperar a quién · medianas de todo el historial";
                    cs.AddRange(new[]
                    {
                        Col("él espera", 0, 62, true, true), Col("yo espero", 0, 62, true, true),
                        Col("la balanza", 0.55f, 96), Col("mi reacción", 0, 62, true, true), Col("su reacción", 0, 62, true, true),
                        Col("turnos", 0, 52, true, true), Col("arranco yo", 0, 56, true, true), Col("arranca él", 0, 56, true, true),
                        Col("iniciativa", 0, 56, true, true),
                    });
                    break;
                default:
                    tGente.Etiqueta = "el ritmo de cada uno · TODO lo que escribe, también en canales";
                    cs.AddRange(new[]
                    {
                        Col("hora pico", 0, 56, true, true), Col("su franja", 0.9f, 90), Col("días activo", 0, 56, true, true),
                        Col("msg por día", 0, 60, true, true), Col("pal/msg", 0, 52, true, true),
                        Col("chats", 0, 44, true, true), Col("primero", 0, 56, true, true), Col("último", 0, 56, true, true),
                        Col("silencio", 0, 54, true, true),
                    });
                    break;
            }
            tGente.Columnas.Clear();
            tGente.Columnas.AddRange(cs);
            tGente.Invalidate();
        }

        // ------------------------------------------------------------------ datos de fondo
        void LeerAhora()
        {
            var obs = ctx.Observador;
            if (obs == null) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                using (Tareas.Empezar("leyendo la lista de chats", "presencia del equipo", Tema.Salvia))
                { try { obs.LeerAhora(); } catch { } }
                try { ctx.EnUi?.Invoke(Refrescar); } catch { }
            });
        }

        /// <summary>Trae el historial del disco. La primera vez sola; con `forzar` lo vuelve a sacar de Teams.</summary>
        void CargarHistorial(bool forzar)
        {
            if (chats == null || chats.Trabajando) return;
            if (forzar) { chats.ReleerAsync(); return; }
            if (pedidoHistorial || chats.Hay) return;
            pedidoHistorial = true;
            chats.CargarAsync();
        }

        void Recalcular()
        {
            if (chats == null || !chats.Hay || calculando) return;
            calculando = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                using (Tareas.Empezar("cruzando el historial con el equipo", chats.Mensajes.Count.ToString("N0") + " mensajes", Tema.Malva))
                { try { rel.Calcular(chats); } catch (Exception ex) { ctx.Log?.Error("Relaciones: " + ex.Message); } }
                calculando = false;
                try { ctx.EnUi?.Invoke(Refrescar); } catch { }
            });
        }

        // ------------------------------------------------------------------ layout
        public override void Acomodar()
        {
            int pad = S(4), y = S(6);
            rCards = new Rectangle(pad, y, Width - pad * 2, S(74));
            y = rCards.Bottom + S(10);
            int yChips = Flujo(chips, pad, y, Math.Max(S(300), Width - pad * 2 - S(360)));
            segLente.SetBounds(Width - pad - S(340), y - S(4), S(340), S(42));
            y = Math.Max(yChips, y + S(44)) + S(2);
            carga.SetBounds(pad, y, Math.Max(S(200), Width - pad * 2), S(16));
            y += S(20);

            int alto = Math.Max(S(140), Height - y - S(10));
            int altoArriba = (int)(alto * 0.60);
            tGente.SetBounds(pad, y, Width - pad * 2, altoArriba);

            int y2 = y + altoArriba + S(12), alto2 = alto - altoArriba - S(12), w = Width - pad * 2;
            int wMov = (int)(w * 0.30), wBar = (int)(w * 0.22), wPer = (int)(w * 0.24);
            tMovs.SetBounds(pad, y2, wMov, alto2);
            gRanking.SetBounds(pad + wMov + S(10), y2, wBar, alto2);
            fPersona.SetBounds(pad + wMov + wBar + S(20), y2, wPer, alto2);
            fEquipo.SetBounds(pad + wMov + wBar + wPer + S(30), y2, w - wMov - wBar - wPer - S(30), alto2);
        }

        // ------------------------------------------------------------------ pintar los datos
        public override void Refrescar()
        {
            CargarHistorial(false);
            if (chats != null && chats.Hay && !rel.Hay && !calculando) Recalcular();

            var obs = ctx.Observador;
            var gente = (obs != null ? obs.Registro.Gente() : new EstadoPersona[0]).Where(p => p.Tipo != "yo").ToArray();
            var movs = obs != null ? obs.Registro.Movimientos() : new Movimiento[0];

            // fusión: la lista de chats manda en «ahora»; el historial abre la puerta a los otros 200
            var caras = new Dictionary<string, Cara>(StringComparer.Ordinal);
            foreach (var p in gente)
            {
                string k = Contactos.Normalizar(p.Nombre);
                if (k.Length == 0) continue;
                caras[k] = new Cara { Nombre = p.Nombre, Vivo = p, V = rel.De(p.Nombre) };
            }
            if (lente != Lente.Ahora)
                foreach (var v in rel.Todos)
                {
                    if (v.Total == 0) continue;
                    Cara c;
                    if (caras.TryGetValue(v.Clave, out c)) c.V = v;
                    else caras[v.Clave] = new Cara { Nombre = v.Nombre, V = v };
                }

            var lista = caras.Values
                .Where(c => !soloConectados || c.Conectado)
                .Where(c => !soloHistoria || (c.V != null && c.V.Total > 0))
                .ToList();
            lista = Ordenar(lista);

            var filas = new List<FilaTabla>();
            foreach (var c in lista) filas.Add(Fila(c));
            int conectados = gente.Count(p => Estados.EsConectado(p.Presencia));
            tGente.Poner(filas, filas.FirstOrDefault(f => ((Cara)f.Tag).Nombre == elegida)?.Tag,
                lente == Lente.Ahora ? conectados + " conectado/s de " + gente.Length
                                     : lista.Count + " persona/s · " + rel.Mensajes.ToString("N0") + " mensajes");
            if (tGente.Seleccion < 0 && filas.Count > 0) tGente.Seleccion = 0;   // que la ficha nunca arranque vacía
            MostrarPersona();

            // idas y venidas (siempre del observador; el historial no sabe de presencia)
            var yo = new HashSet<string>((obs != null ? obs.Registro.Gente() : new EstadoPersona[0])
                .Where(p => p.Tipo == "yo").Select(p => Contactos.Normalizar(p.Nombre)));
            movs = movs.Where(m => !yo.Contains(Contactos.Normalizar(m.Persona))).ToArray();
            var fm = new List<FilaTabla>();
            foreach (var m in movs.Reverse().Take(150))
                fm.Add(FilaTabla.F(m, VistaPatrones.ColorDe(m.Hasta),
                    Celda.C(m.Hora.ToString("HH:mm:ss"), Tema.Apagado),
                    Celda.C(m.Persona, Tema.Texto),
                    Celda.C(m.Desde.Length > 0 ? m.Desde : "—", Tema.Apagado),
                    Celda.C(m.Hasta.Length > 0 ? m.Hasta : "—", VistaPatrones.ColorDe(m.Hasta))));
            tMovs.Poner(fm, null, movs.Length + " en memoria");

            Ranking(gente);
            Resumen(gente, movs, conectados);

            bool enCamino = chats != null && (chats.Trabajando || calculando);
            tGente.Cargando = enCamino; tMovs.Cargando = enCamino;      // esqueleto mientras llega el dato (no «vacío»)
            if (enCamino)
            {
                if (!carga.Activo) { carga.Desde = DateTime.Now; carga.Activo = true; }
                carga.Texto = calculando ? "cruzando el historial…" : (chats.Paso.Length > 0 ? chats.Paso : "leyendo el historial…");
            }
            else if (carga.Activo) { carga.Activo = false; carga.Texto = ""; }

            Invalidate(rCards);
        }

        List<Cara> Ordenar(List<Cara> l)
        {
            switch (lente)
            {
                case Lente.Historia:
                    return l.OrderByDescending(c => c.V != null ? c.V.Total : 0).ThenBy(c => c.Nombre).ToList();
                case Lente.Esperas:
                    // 🚨 Primero los que tienen muestra suficiente. Sin esto, alguien con DOS turnos y una
                    //    mediana de 20 h encabezaba la tabla por encima de gente con 1.700 intercambios.
                    return l.OrderByDescending(c => c.V != null && c.V.Confiable)
                            .ThenByDescending(c => c.V != null && c.V.SuEspera.HasValue ? c.V.SuEspera.Value.TotalSeconds : -1)
                            .ThenBy(c => c.Nombre).ToList();
                case Lente.Ritmo:
                    return l.OrderByDescending(c => c.V != null ? c.V.Dias : 0).ThenBy(c => c.Nombre).ToList();
                default:
                    return l.OrderByDescending(c => c.Vivo != null && Estados.EsDisponible(c.Presencia))
                            .ThenByDescending(c => c.Conectado).ThenBy(c => c.Nombre).ToList();
            }
        }

        FilaTabla Fila(Cara c)
        {
            var v = c.V;
            var color = c.Vivo != null ? VistaPatrones.ColorDe(c.Presencia) : Tema.MuyApagado;
            var celdas = new List<Celda>
            {
                Celda.C(c.Nombre, Tema.Texto, c.Vivo != null && Estados.EsDisponible(c.Presencia)),
                Celda.C(c.Vivo != null ? (c.Presencia.Length > 0 ? c.Presencia : "—") : "fuera de la lista",
                        c.Vivo != null ? color : Tema.MuyApagado),
            };
            switch (lente)
            {
                case Lente.Ahora:
                    var p = c.Vivo;
                    if (p == null) { for (int i = 0; i < 10; i++) celdas.Add(Celda.C("—", Tema.MuyApagado)); break; }
                    var vistos = p.Disponible + p.Ausente + p.Ocupado + p.Desconectado;
                    celdas.Add(Celda.C(Corto(DateTime.Now - p.Desde), Tema.TextoSuave));
                    celdas.Add(Celda.C(p.Cambios.ToString(), p.Cambios > 8 ? Tema.Durazno : Tema.TextoSuave));
                    celdas.Add(Celda.C(Corto(p.Disponible), Tema.Salvia));
                    celdas.Add(Celda.C(Corto(p.Ausente), Tema.Durazno));
                    celdas.Add(Celda.C(Corto(p.Ocupado), Tema.Rosa));
                    celdas.Add(Celda.C(Corto(p.Desconectado), Tema.MuyApagado));
                    celdas.Add(Celda.C(vistos.TotalSeconds > 30 ? $"{p.Disponible.TotalSeconds / vistos.TotalSeconds * 100:0}%" : "—", Tema.Cielo));
                    celdas.Add(Celda.C(p.NoLeido ? "sí" : "", p.NoLeido ? Tema.Rosa : Tema.Apagado));
                    celdas.Add(Celda.C(p.VistaPrimeraVez.ToString("HH:mm"), Tema.Apagado));
                    celdas.Add(Celda.C(p.VistaUltimaVez.ToString("HH:mm"), Tema.Apagado));
                    break;

                case Lente.Historia:
                    if (v == null) { for (int i = 0; i < 10; i++) celdas.Add(Celda.C("—", Tema.MuyApagado)); break; }
                    celdas.Add(Celda.C(v.Total > 0 ? v.Total.ToString("N0") : "—", v.Total > 500 ? Tema.Cyan : Tema.TextoSuave, v.Total > 1000));
                    celdas.Add(Celda.C(v.Suyos.ToString("N0"), Tema.Malva));
                    celdas.Add(Celda.C(v.Mios.ToString("N0"), Tema.Cielo));
                    celdas.Add(Balanza(v.Balance, "él", "vos"));
                    celdas.Add(Celda.C(v.EnGrupos > 0 ? v.EnGrupos.ToString("N0") : "", Tema.Apagado));
                    celdas.Add(Celda.C(v.Dias.ToString(), Tema.TextoSuave));
                    celdas.Add(Celda.C(v.Primero > DateTime.MinValue ? v.Primero.ToString("MM/yy") : "—", Tema.Apagado));
                    celdas.Add(Celda.C(v.Ultimo > DateTime.MinValue ? v.Ultimo.ToString("dd/MM/yy") : "—", Tema.Apagado));
                    celdas.Add(Celda.C(Corto(v.Silencio), v.Silencio.TotalDays > 30 ? Tema.Durazno : Tema.TextoSuave));
                    celdas.Add(Celda.C(v.Pendiente ? (v.HilosSinContestar > 1 ? "te toca ×" + v.HilosSinContestar : "te toca") : "",
                        v.Pendiente ? Tema.Rosa : Tema.Apagado, v.Pendiente));
                    break;

                case Lente.Esperas:
                    if (v == null || v.Turnos == 0) { for (int i = 0; i < 9; i++) celdas.Add(Celda.C("—", Tema.MuyApagado)); break; }
                    // con pocos turnos los números se muestran igual, pero apagados: son una anécdota
                    var cEsp = v.Confiable;
                    celdas.Add(Celda.C(Tiempo(v.MiEspera), cEsp ? Tema.Cielo : Tema.MuyApagado));
                    celdas.Add(Celda.C(Tiempo(v.SuEspera), cEsp ? Tema.Durazno : Tema.MuyApagado));
                    celdas.Add(cEsp ? Balanza(1.0 / (1.0 + v.Deuda), "esperás vos", "espera él")
                                    : Celda.C("pocos datos", Tema.MuyApagado));
                    celdas.Add(Celda.C(Tiempo(v.MiReaccion), Tema.Apagado));
                    celdas.Add(Celda.C(Tiempo(v.SuReaccion), Tema.Apagado));
                    celdas.Add(Celda.C(v.Turnos.ToString("N0"), cEsp ? Tema.TextoSuave : Tema.MuyApagado));
                    celdas.Add(Celda.C(v.YoArranco.ToString(), Tema.Cielo));
                    celdas.Add(Celda.C(v.ElArranca.ToString(), Tema.Malva));
                    celdas.Add(Celda.C((v.Iniciativa > 0 ? "+" : "") + v.Iniciativa, v.Iniciativa > 0 ? Tema.Cielo : v.Iniciativa < 0 ? Tema.Malva : Tema.Apagado));
                    break;

                default:
                    // 🚨 Esta lente mira a la persona ENTERA (privado + canales + reuniones), así que TODOS
                    //    los números son de esa población. Mezclar acá los del chat de a dos daba cosas como
                    //    «274 palabras por mensaje» — las de sus canales divididas por sus mensajes privados.
                    if (v == null) { for (int i = 0; i < 9; i++) celdas.Add(Celda.C("—", Tema.MuyApagado)); break; }
                    double porDia = v.DiasTodo > 0 ? (double)v.Todo / v.DiasTodo : 0;
                    celdas.Add(Celda.C(v.HoraPico >= 0 ? v.HoraPico.ToString("00") + "h" : "—", Tema.Cyan));
                    celdas.Add(Franja(v));
                    celdas.Add(Celda.C(v.DiasTodo.ToString(), Tema.TextoSuave));
                    celdas.Add(Celda.C(porDia >= 0.05 ? porDia.ToString("0.0") : "—", Tema.Salvia));
                    celdas.Add(Celda.C(v.LargoTodo >= 0.5 ? v.LargoTodo.ToString("0.0") : "—", Tema.Apagado));
                    celdas.Add(Celda.C(v.Chats > 0 ? v.Chats.ToString() : "—", Tema.Apagado));
                    celdas.Add(Celda.C(v.PrimeroTodo > DateTime.MinValue ? v.PrimeroTodo.ToString("MM/yy") : "—", Tema.Apagado));
                    celdas.Add(Celda.C(v.UltimoTodo > DateTime.MinValue ? v.UltimoTodo.ToString("dd/MM/yy") : "—", Tema.Apagado));
                    celdas.Add(Celda.C(Corto(v.SilencioTodo), v.SilencioTodo.TotalDays > 30 ? Tema.Durazno : Tema.TextoSuave));
                    break;
            }
            return new FilaTabla { Tag = c, Punto = color, Celdas = celdas };
        }

        /// <summary>Una barra de texto que muestra de qué lado cae algo: ▁▂▃ a la izquierda o a la derecha.</summary>
        static Celda Balanza(double v, string izq, string der)
        {
            v = Math.Max(0, Math.Min(1, v));
            const int n = 9;
            int marca = (int)Math.Round(v * (n - 1));
            var sb = new System.Text.StringBuilder(n);
            for (int i = 0; i < n; i++) sb.Append(i == marca ? '●' : i == n / 2 ? '┊' : '·');
            var c = v < 0.38 ? Tema.Malva : v > 0.62 ? Tema.Cielo : Tema.Salvia;
            return new Celda { Texto = sb.ToString(), Color = c, Insignia = v < 0.38 ? izq : v > 0.62 ? der : "parejo", ColorInsignia = c };
        }

        /// <summary>Un sparkline de 24 caracteres con la franja horaria en la que esa persona escribe.</summary>
        static Celda Franja(Vinculo v)
        {
            const string rampa = " ▁▂▃▄▅▆▇█";
            int max = 0;
            for (int i = 0; i < 24; i++) if (v.PorHora[i] > max) max = v.PorHora[i];
            if (max == 0) return Celda.C("—", Tema.MuyApagado);
            var sb = new System.Text.StringBuilder(24);
            for (int i = 6; i < 24; i++) sb.Append(rampa[Math.Min(rampa.Length - 1, (int)Math.Round(v.PorHora[i] * 8.0 / max))]);
            return new Celda { Texto = sb.ToString(), Color = Tema.Cielo, Fuerte = false };
        }

        static string Tiempo(TimeSpan? t) => t.HasValue ? Corto(t.Value) : "—";

        void MostrarPersona()
        {
            var f = tGente.Actual;
            var c = f != null ? f.Tag as Cara : null;
            if (c == null) { fPersona.Etiqueta = "la persona elegida"; fPersona.Poner(new List<Dato>()); return; }
            fPersona.Etiqueta = c.Nombre;
            var d = new List<Dato>();
            if (c.Vivo != null)
            {
                d.Add(Dato.D("ahora", c.Presencia.Length > 0 ? c.Presencia : "—", VistaPatrones.ColorDe(c.Presencia)));
                d.Add(Dato.D("así desde", Corto(DateTime.Now - c.Vivo.Desde) + " atrás", Tema.TextoSuave));
                d.Add(Dato.D("cambios hoy", c.Vivo.Cambios.ToString(), Tema.Cyan));
                if (c.Vivo.NoLeido) d.Add(Dato.D("sin leer", "te escribió", Tema.Rosa));
            }
            else d.Add(Dato.D("ahora", "no está en tu lista de chats", Tema.MuyApagado));

            var v = c.V;
            if (v == null || v.Todo == 0) { d.Add(Dato.D("historial", "sin mensajes", Tema.Apagado)); fPersona.Poner(d); return; }

            d.Add(Dato.Titulo("en privado, entre ustedes dos"));
            d.Add(Dato.D("mensajes", v.Total.ToString("N0"), Tema.Cyan));
            d.Add(Dato.D("suyos · míos", v.Suyos.ToString("N0") + " · " + v.Mios.ToString("N0"), Tema.TextoSuave));
            d.Add(Dato.D("quién habla más", v.Balance > 0.58 ? "vos" : v.Balance < 0.42 ? Apellido(c.Nombre) : "parejo",
                v.Balance > 0.58 ? Tema.Cielo : v.Balance < 0.42 ? Tema.Malva : Tema.Salvia));
            d.Add(Dato.D("días que se hablaron", v.Dias.ToString("N0"), Tema.Salvia));
            d.Add(Dato.D("desde", v.Primero > DateTime.MinValue ? v.Primero.ToString("dd/MM/yyyy") : "—", Tema.Apagado));
            d.Add(Dato.D("último", v.Ultimo > DateTime.MinValue ? v.Ultimo.ToString("dd/MM/yyyy HH:mm") : "—", Tema.Apagado));
            d.Add(Dato.D("silencio", Corto(v.Silencio), v.Silencio.TotalDays > 30 ? Tema.Durazno : Tema.TextoSuave));
            if (v.EnGrupos > 0)
            {
                d.Add(Dato.Titulo("además, en canales y grupos"));
                d.Add(Dato.D("mensajes suyos", v.EnGrupos.ToString("N0"), Tema.Malva));
                d.Add(Dato.D("días que lo viste escribir", v.DiasTodo.ToString("N0"), Tema.TextoSuave));
                d.Add(Dato.D("último en cualquier lado", v.UltimoTodo > DateTime.MinValue ? v.UltimoTodo.ToString("dd/MM/yyyy") : "—", Tema.Apagado));
            }
            if (v.HoraPico >= 0) d.Add(Dato.D("escribe sobre las", v.HoraPico.ToString("00") + ":00", Tema.Cyan));

            if (v.Turnos > 0)
            {
                d.Add(Dato.Titulo("quién espera a quién · " + v.Turnos.ToString("N0") + " turnos"
                    + (v.Confiable ? "" : " · pocos, tomalo con pinzas")));
                d.Add(Dato.D("lo hacés esperar", Tiempo(v.MiEspera), Tema.Cielo));
                d.Add(Dato.D("te hace esperar", Tiempo(v.SuEspera), Tema.Durazno));
                d.Add(Dato.D("reacción · vos y él", Tiempo(v.MiReaccion) + " · " + Tiempo(v.SuReaccion), Tema.Apagado));
                d.Add(Dato.D("quién arranca", v.Iniciativa > 2 ? "vos (" + v.YoArranco + " vs " + v.ElArranca + ")"
                                            : v.Iniciativa < -2 ? Apellido(c.Nombre) + " (" + v.ElArranca + " vs " + v.YoArranco + ")"
                                            : "los dos igual", v.Iniciativa > 2 ? Tema.Cielo : v.Iniciativa < -2 ? Tema.Malva : Tema.Salvia));
            }
            if (v.Pendiente)
                d.Add(Dato.D("la pelota", "la tenés vos hace " + Corto(DateTime.Now - v.DebeDesde)
                    + (v.HilosSinContestar > 1 ? " · en " + v.HilosSinContestar + " hilos" : ""), Tema.Rosa));
            if (v.UltimoTexto.Length > 0) d.Add(Dato.D("lo último que dijo", Relaciones.Recortar(v.UltimoTexto, 48), Tema.TextoSuave));
            fPersona.Poner(d);
        }

        void Ranking(EstadoPersona[] gente)
        {
            var datos = new List<Barra>();
            string total;
            switch (lente)
            {
                case Lente.Historia:
                    gRanking.Etiqueta = "con quién más hablás en privado";
                    datos = rel.Todos.Where(v => v.Total > 0).OrderByDescending(v => v.Total).Take(12)
                        .Select(v => new Barra { Etiqueta = Apellido(v.Nombre), Valor = v.Total, Color = Tema.Malva, Extra = v.Total.ToString("N0") }).ToList();
                    total = rel.Personas + " personas";
                    break;
                case Lente.Esperas:
                    gRanking.Etiqueta = "quién te hace esperar más";
                    datos = rel.Todos.Where(v => v.SuEspera.HasValue && v.Turnos >= 12).OrderByDescending(v => v.SuEspera.Value)
                        .Take(12).Select(v => new Barra { Etiqueta = Apellido(v.Nombre), Valor = (int)v.SuEspera.Value.TotalSeconds, Color = Tema.Durazno, Extra = Corto(v.SuEspera.Value) }).ToList();
                    total = rel.Todos.Count(v => v.Turnos >= 12) + " con datos";
                    break;
                case Lente.Ritmo:
                    // la lente mira TODO lo que escribe, así que el ranking tiene que mirar lo mismo:
                    // con los días del privado, la columna decía 304 y la barra 131 para la misma persona
                    gRanking.Etiqueta = "en cuántos días lo viste escribir";
                    datos = rel.Todos.Where(v => v.DiasTodo > 0).OrderByDescending(v => v.DiasTodo).Take(12)
                        .Select(v => new Barra { Etiqueta = Apellido(v.Nombre), Valor = v.DiasTodo, Color = Tema.Cyan, Extra = v.DiasTodo + " d" }).ToList();
                    total = rel.Personas + " personas";
                    break;
                default:
                    gRanking.Etiqueta = "quién estuvo más Disponible";
                    datos = gente.Where(p => p.Disponible.TotalMinutes >= 1).OrderByDescending(p => p.Disponible).Take(12)
                        .Select(p => new Barra { Etiqueta = Apellido(p.Nombre), Valor = (int)p.Disponible.TotalMinutes, Color = Tema.Salvia, Extra = Corto(p.Disponible) }).ToList();
                    total = gente.Length + " personas";
                    break;
            }
            gRanking.Poner(datos, total);
        }

        void Resumen(EstadoPersona[] gente, Movimiento[] movs, int conectados)
        {
            var hoy = movs.Where(m => m.Hora.Date == DateTime.Today).ToList();
            var primero = hoy.Where(m => m.SeConecto).OrderBy(m => m.Hora).FirstOrDefault();
            var ultimo = hoy.OrderByDescending(m => m.Hora).FirstOrDefault();
            var inquieto = hoy.GroupBy(m => m.Persona).OrderByDescending(gg => gg.Count()).FirstOrDefault();
            var pend = rel.Pendientes();
            var frios = rel.Enfriados();

            var d = new List<Dato>
            {
                Dato.D("conectados ahora", conectados + " de " + gente.Length, conectados > 0 ? Tema.Salvia : Tema.Apagado),
                Dato.D("disponibles", gente.Count(p => Estados.EsDisponible(p.Presencia)).ToString(), Tema.Salvia),
                Dato.D("ausentes · ocupados", gente.Count(p => Estados.EsAusente(p.Presencia)) + " · " + gente.Count(p => Estados.EsOcupado(p.Presencia)), Tema.Durazno),
                Dato.Titulo("hoy"),
                Dato.D("movimientos", hoy.Count.ToString(), Tema.Cyan),
                Dato.D("primero en llegar", primero != null ? Apellido(primero.Persona) + " " + primero.Hora.ToString("HH:mm") : "—", Tema.Cielo),
                Dato.D("último movimiento", ultimo != null ? Apellido(ultimo.Persona) + " " + ultimo.Hora.ToString("HH:mm") : "—", Tema.TextoSuave),
                Dato.D("el más inquieto", inquieto != null ? Apellido(inquieto.Key) + " (" + inquieto.Count() + ")" : "—", Tema.Durazno),
            };

            if (rel.Hay)
            {
                var conTurnos = rel.Todos.Where(v => v.Turnos >= 12).ToList();
                d.Add(Dato.Titulo("el historial"));
                d.Add(Dato.D("mensajes leídos", rel.Mensajes.ToString("N0"), Tema.Malva));
                d.Add(Dato.D("gente que te escribió", rel.Personas.ToString(), Tema.Cyan));
                d.Add(Dato.D("chats de a dos", rel.DeADos + " de " + rel.Chats, Tema.TextoSuave));
                d.Add(Dato.D("les debés respuesta a", pend.Length.ToString(), pend.Length > 0 ? Tema.Rosa : Tema.Salvia));
                if (pend.Length > 0) d.Add(Dato.D("hace más que a nadie", Apellido(pend[0].Nombre) + " · " + Corto(DateTime.Now - pend[0].DebeDesde), Tema.Rosa));
                if (frios.Length > 0) d.Add(Dato.D("se te enfrió", Apellido(frios[0].Nombre) + " · " + Corto(frios[0].Silencio), Tema.Durazno));
                if (conTurnos.Count > 0)
                    d.Add(Dato.D("esperas · vos y ellos",
                        Corto(TimeSpan.FromSeconds(conTurnos.Average(v => v.MiEspera.Value.TotalSeconds))) + " · " +
                        Corto(TimeSpan.FromSeconds(conTurnos.Average(v => v.SuEspera.Value.TotalSeconds))), Tema.Cielo));
            }

            d.Add(Dato.Titulo("el observador"));
            d.Add(Dato.D("estado", ctx.Observador != null ? ctx.Observador.Estado : "apagado", Tema.TextoSuave));
            d.Add(Dato.D("lecturas · fallos", ctx.Observador != null ? ctx.Observador.Lecturas + " · " + ctx.Observador.Fallos : "—",
                ctx.Observador != null && ctx.Observador.Fallos > 0 ? Tema.Rosa : Tema.Salvia));
            fEquipo.Poner(d);
        }

        // ------------------------------------------------------------------ utilidades
        /// <summary>«Rivas, Valentina» → «Rivas». Los rankings no entran con nombre y apellido.</summary>
        internal static string Apellido(string n)
        {
            if (string.IsNullOrEmpty(n)) return "";
            int i = n.IndexOf(',');
            return i > 0 ? n.Substring(0, i) : n;
        }

        internal static string Corto(TimeSpan t)
        {
            if (t == TimeSpan.MaxValue) return "—";
            if (t.TotalSeconds < 0) return "—";
            if (t.TotalSeconds < 60) return (int)t.TotalSeconds + "s";
            if (t.TotalMinutes < 60) return (int)t.TotalMinutes + "m";
            if (t.TotalHours < 24) return (int)t.TotalHours + "h" + t.Minutes.ToString("00");
            if (t.TotalDays < 365) return (int)t.TotalDays + "d";
            return (t.TotalDays / 365).ToString("0.0") + "a";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            var obs = ctx.Observador;
            var gente = (obs != null ? obs.Registro.Gente() : new EstadoPersona[0]).Where(p => p.Tipo != "yo").ToArray();
            var movs = obs != null ? obs.Registro.Movimientos() : new Movimiento[0];
            int conectados = gente.Count(p => Estados.EsConectado(p.Presencia));
            int disp = gente.Count(p => Estados.EsDisponible(p.Presencia));
            var hoy = movs.Where(m => m.Hora.Date == DateTime.Today).ToList();
            var pend = rel.Pendientes();

            var r = Tarjetas(rCards, new[] { 1f, 1f, 1.1f, 1.1f, 1.3f });
            Ayuda.Tarjeta(g, esc, r[0], "CONECTADOS", conectados.ToString(), "de " + gente.Length + " que veo",
                conectados > 0 ? Tema.Salvia : Tema.MuyApagado);
            Ayuda.Tarjeta(g, esc, r[1], "DISPONIBLES", disp.ToString(), disp == 0 ? "nadie libre ahora" : "para hablarles",
                disp > 0 ? Tema.Cyan : Tema.Apagado);
            Ayuda.Tarjeta(g, esc, r[2], "LA PELOTA", rel.Hay ? pend.Length.ToString() : "—",
                !rel.Hay ? "leyendo el historial…" : pend.Length == 0 ? "no le debés nada a nadie"
                    : "les debés una respuesta", pend.Length > 0 ? Tema.Rosa : Tema.Salvia);
            Ayuda.Tarjeta(g, esc, r[3], "MENSAJES", rel.Hay ? Miles(rel.Mensajes) : "—",
                rel.Hay ? rel.Personas + " personas · " + rel.DeADos + " chats de a dos" : "todavía no lo leí", Tema.Malva);
            Ayuda.Tarjeta(g, esc, r[4], "MOVIMIENTOS HOY", hoy.Count.ToString(),
                obs != null ? movs.Length + " en memoria · " + obs.Lecturas + " lecturas cada " + obs.SegundosEntre + " s" : "observador apagado",
                Tema.Cielo);
        }

        static string Miles(int n) => n >= 1000 ? (n / 1000.0).ToString("0.#") + "k" : n.ToString();
    }
}
