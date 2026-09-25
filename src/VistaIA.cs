using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace TeamsTools
{
    // =====================================================================================================
    // IA: varios micromodelos leyendo tus conversaciones — qué mirar y qué anotar
    // =====================================================================================================
    internal sealed class VistaIA : Pantalla
    {
        readonly Historia hist;
        readonly Radar radar;
        readonly Tabla tAvisos = new Tabla
        {
            Etiqueta = "lo que deberías mirar", Vacio = "tocá «revisar todo» para que los micromodelos lean tus chats", AltoFila = 19,
            Columnas =
            {
                new Columna { Titulo = "prio", Peso = 0, MinAncho = 34, Derecha = true, Mono = true },
                new Columna { Titulo = "micromodelo", Peso = 0, MinAncho = 92 },
                new Columna { Titulo = "cuándo", Peso = 0, MinAncho = 70, Mono = true },
                new Columna { Titulo = "dónde", Peso = 0.8f, MinAncho = 80 },
                new Columna { Titulo = "quién", Peso = 0.6f, MinAncho = 70 },
                new Columna { Titulo = "qué dice", Peso = 2.4f, MinAncho = 170 },
                new Columna { Titulo = "por qué te lo muestro", Peso = 1.2f, MinAncho = 110 },
            }
        };
        readonly Tabla tNotas = new Tabla
        {
            Etiqueta = "la libreta · lo que se prometió y lo que se decidió", Vacio = "sin anotaciones todavía", AltoFila = 19,
            Columnas =
            {
                new Columna { Titulo = "", Peso = 0, MinAncho = 20 },
                new Columna { Titulo = "tipo", Peso = 0, MinAncho = 88 },
                new Columna { Titulo = "cuándo", Peso = 0, MinAncho = 66, Mono = true },
                new Columna { Titulo = "quién", Peso = 0.6f, MinAncho = 70 },
                new Columna { Titulo = "dónde", Peso = 0.7f, MinAncho = 76 },
                new Columna { Titulo = "qué", Peso = 2.6f, MinAncho = 170 },
                new Columna { Titulo = "vence", Peso = 0, MinAncho = 64, Derecha = true, Mono = true },
            }
        };
        readonly Tabla tModelos = new Tabla
        {
            Etiqueta = "los micromodelos y qué encontró cada uno", Vacio = "sin correr", AltoFila = 18,
            Columnas =
            {
                new Columna { Titulo = "micromodelo", Peso = 0.9f, MinAncho = 92 },
                new Columna { Titulo = "qué busca", Peso = 2f, MinAncho = 150 },
                new Columna { Titulo = "encontró", Peso = 0, MinAncho = 52, Derecha = true, Mono = true },
                new Columna { Titulo = "ms", Peso = 0, MinAncho = 36, Derecha = true, Mono = true },
            }
        };
        readonly Ficha fResumen = new Ficha { Etiqueta = "el resumen", Acento = Tema.Cyan };
        readonly Deslizador dDias = new Deslizador { Etiqueta = "mirar los últimos", Min = 1, Max = 90, Paso = 1, Acento = Tema.Malva, Marcas = new[] { 7, 14, 30 }, Formato = v => v == 1 ? "1 día" : v + " días" };
        readonly Cargador carga = new Cargador { Modo = Cargador.Estilo.Puntos, Acento = Tema.Cyan, MostrarTiempo = true };
        Chip chRevisar, chHecho, chFijar, chOcultar, chCopiar, chIA;
        Rectangle rCards;
        volatile bool corriendo;
        string estadoIA = "";

        public VistaIA(Contexto c, Historia h, Radar r) : base(c)
        {
            hist = h; radar = r;
            Controls.Add(tAvisos); Controls.Add(tNotas); Controls.Add(tModelos); Controls.Add(fResumen); Controls.Add(dDias); Controls.Add(carga);
            dDias.Superficie = Tema.Fondo;
            dDias.Poner(radar.Dias);
            dDias.Cambio += (o, e) => { radar.Dias = dDias.Valor; Revisar(); };

            chRevisar = Nuevo("revisar todo", Chip.Modo.Boton, Tema.Cyan, (o, e) => Revisar());
            chHecho = Nuevo("marcar como hecho", Chip.Modo.Boton, Tema.Salvia, (o, e) => MarcarHecho());
            chFijar = Nuevo("fijar arriba", Chip.Modo.Boton, Tema.Crema, (o, e) => Fijar());
            chOcultar = Nuevo("no mostrarme más este aviso", Chip.Modo.Boton, Tema.Apagado, (o, e) => Ocultar());
            chCopiar = Nuevo("copiar la libreta", Chip.Modo.Boton, Tema.Crema, (o, e) => Copiar());
            chIA = Nuevo("pedirle un resumen al modelo local", Chip.Modo.Boton, Tema.Malva, (o, e) => Resumir());
        }

        // ------------------------------------------------------------------ acciones
        /// <summary>«revisar» corre los micromodelos (para fotografiar la pestaña con datos sin el mouse).</summary>
        public override void Modo(string que) { if (string.Equals(que, "revisar", StringComparison.OrdinalIgnoreCase)) Revisar(); }

        void Revisar()
        {
            if (corriendo || hist == null) return;
            if (!hist.Hay) { hist.CargarAsync(); ctx.Aviso?.Invoke("Todavía no leí el historial", "lo estoy cargando, probá de nuevo en unos segundos"); return; }
            corriendo = true;
            chRevisar.Text = "leyendo…"; chRevisar.Ajustar(); Acomodar();
            carga.Texto = "leyendo tus conversaciones"; carga.Modo = Cargador.Estilo.Ondas; carga.Acento = Tema.Cyan; carga.Desde = DateTime.Now; carga.Activo = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                using (Tareas.Empezar("los micromodelos están leyendo", radar.Dias + " días de conversaciones", Tema.Cyan))
                {
                    try { radar.Correr(hist, ctx.Chat != null ? ctx.Chat.MiNombre : ""); }
                    catch (Exception ex) { ctx.Log?.Error("Radar: " + ex.Message); }
                }
                try
                {
                    ctx.EnUi?.Invoke(() =>
                    {
                        corriendo = false;
                        carga.Activo = false; carga.Texto = "";
                        chRevisar.Text = "revisar todo"; chRevisar.Ajustar(); Acomodar();
                        Llenar();
                    });
                }
                catch { corriendo = false; }
            });
        }

        void MarcarHecho()
        {
            var n = tNotas.Actual != null ? tNotas.Actual.Tag as Nota : null;
            if (n == null) return;
            if (radar.Marcas.Hechas.Contains(n.Id)) radar.Marcas.Hechas.Remove(n.Id);
            else radar.Marcas.Hechas.Add(n.Id);
            radar.Marcas.Guardar();
            n.Hecho = radar.Marcas.Hechas.Contains(n.Id);
            Llenar();
        }

        void Fijar()
        {
            var n = tNotas.Actual != null ? tNotas.Actual.Tag as Nota : null;
            if (n == null) return;
            if (radar.Marcas.Fijadas.Contains(n.Id)) radar.Marcas.Fijadas.Remove(n.Id);
            else radar.Marcas.Fijadas.Add(n.Id);
            radar.Marcas.Guardar();
            n.Fijada = radar.Marcas.Fijadas.Contains(n.Id);
            radar.Notas = radar.Notas.OrderByDescending(x => x.Fijada && !x.Hecho).ThenBy(x => x.Hecho).ThenByDescending(x => x.Cuando).ToList();
            Llenar();
        }

        void Ocultar()
        {
            var a = tAvisos.Actual != null ? tAvisos.Actual.Tag as Aviso : null;
            if (a == null) return;
            radar.Marcas.Ocultas.Add(Radar.Clave(a));
            radar.Marcas.Guardar();
            radar.Avisos.Remove(a);
            Llenar();
        }

        void Copiar()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("LIBRETA · " + DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
                foreach (var g in radar.Notas.GroupBy(n => n.Modelo))
                {
                    sb.AppendLine();
                    sb.AppendLine(g.Key.ToUpperInvariant());
                    foreach (var n in g.OrderByDescending(x => x.Cuando))
                        sb.AppendLine($"  [{(n.Hecho ? "x" : " ")}] {n.Cuando:dd/MM HH:mm}  {n.Quien} en {n.Conv}: {Una(n.Texto, 200)}"
                                      + (n.Vence.HasValue ? "   (vence " + n.Vence.Value.ToString("dd/MM HH:mm") + ")" : ""));
                }
                if (radar.Notas.Count == 0) { ctx.Aviso?.Invoke("La libreta está vacía", "corré «revisar todo» primero"); return; }
                Clipboard.SetText(sb.ToString());
                ctx.Aviso?.Invoke("Libreta copiada", radar.Notas.Count + " anotación/es");
            }
            catch (Exception ex) { ctx.Aviso?.Invoke("No pude copiar", ex.Message); }
        }

        /// <summary>Lo único que usa el LLM, y solo cuando vos lo pedís.</summary>
        void Resumir()
        {
            string porque;
            if (!AsistenteIA.Disponible(out porque))
            {
                estadoIA = porque;
                ctx.Aviso?.Invoke("El modelo local no está a mano", porque);
                Invalidate(rCards);
                return;
            }
            if (radar.Avisos.Count == 0) { ctx.Aviso?.Invoke("Nada que resumir", "corré «revisar todo» primero"); return; }
            estadoIA = "pensando…";
            chIA.Text = "pensando…"; chIA.Ajustar(); Acomodar();
            carga.Texto = "el modelo local está pensando"; carga.Modo = Cargador.Estilo.Anillo; carga.Acento = Tema.Malva; carga.Desde = DateTime.Now; carga.Activo = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                var sb = new System.Text.StringBuilder();
                foreach (var a in radar.Avisos.Take(15))
                    sb.AppendLine($"- [{a.Modelo}] {a.Cuando:dd/MM HH:mm} {a.Autor} en {a.Conv}: {Una(a.Texto, 160)}");
                string detalle;
                string r = AsistenteIA.Redactar(sb.ToString(), ctx.Chat != null ? ctx.Chat.MiNombre : "",
                    "Resumí en 3 o 4 viñetas qué es lo más importante de esta lista y qué conviene hacer primero. No inventes nada que no esté en la lista.", 220, out detalle);
                try
                {
                    ctx.EnUi?.Invoke(() =>
                    {
                        carga.Activo = false; carga.Texto = "";
                        chIA.Text = "pedirle un resumen al modelo local"; chIA.Ajustar(); Acomodar();
                        estadoIA = r.Length > 0 ? detalle : "no salió: " + detalle;
                        if (r.Length > 0) { try { Clipboard.SetText(r); } catch { } ctx.Aviso?.Invoke("Resumen listo (copiado)", r.Length > 220 ? r.Substring(0, 220) + "…" : r); }
                        else ctx.Aviso?.Invoke("El modelo no contestó", detalle);
                        Invalidate(rCards);
                    });
                }
                catch { }
            });
        }

        static string Una(string t, int n)
        {
            if (string.IsNullOrEmpty(t)) return "";
            var s = t.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return s.Length > n ? s.Substring(0, n) + "…" : s;
        }

        // ------------------------------------------------------------------ layout
        public override void Acomodar()
        {
            int pad = S(4), y = S(6);
            rCards = new Rectangle(pad, y, Width - pad * 2, S(74));
            y = rCards.Bottom + S(10);
            int anchoDias = (int)((Width - pad * 2) * 0.22);
            dDias.SetBounds(pad, y, anchoDias, S(44));
            int yChips = y + S(6);
            y = Flujo(chips, pad + anchoDias + S(12), yChips, Width - pad * 2 - anchoDias - S(12));
            carga.SetBounds(pad + anchoDias + S(12), y, Math.Max(S(200), Width - pad * 2 - anchoDias - S(12)), S(18));
            y = Math.Max(y + S(20), rCards.Bottom + S(10) + S(56)) + S(2);

            int alto = Math.Max(S(180), Height - y - S(10));
            int anchoDer = (int)((Width - pad * 2) * 0.26);
            int anchoIzq = Width - pad * 2 - anchoDer - S(12);
            int altoArriba = (int)(alto * 0.52);
            tAvisos.SetBounds(pad, y, anchoIzq, altoArriba);
            tNotas.SetBounds(pad, y + altoArriba + S(12), anchoIzq, alto - altoArriba - S(12));
            int altoModelos = (int)(alto * 0.5);
            tModelos.SetBounds(pad + anchoIzq + S(12), y, anchoDer, altoModelos);
            fResumen.SetBounds(pad + anchoIzq + S(12), y + altoModelos + S(12), anchoDer, alto - altoModelos - S(12));
        }

        // ------------------------------------------------------------------ datos
        public override void Refrescar()
        {
            if (!hist.Hay && !hist.Trabajando && hist.Problema.Length == 0) hist.CargarAsync();
            tAvisos.Cargando = corriendo || hist.Trabajando || (radar.Corrido == null && hist.Problema.Length == 0);
            if (radar.Corrido == null && hist.Hay && !corriendo) Revisar();
            else Llenar();
        }

        void Llenar()
        {
            var fa = new List<FilaTabla>();
            foreach (var a in radar.Avisos.Take(300))
                fa.Add(FilaTabla.F(a, a.Tinte,
                    Celda.C(a.Prioridad.ToString(), a.Prioridad >= 70 ? Tema.Rosa : a.Prioridad >= 45 ? Tema.Durazno : Tema.Apagado, a.Prioridad >= 70),
                    Celda.C(a.Modelo, a.Tinte),
                    Celda.C(a.Cuando == DateTime.MinValue ? "—" : a.Cuando.ToString("dd/MM HH:mm"), Tema.Apagado),
                    Celda.C(a.Conv, Tema.TextoSuave),
                    Celda.C(a.Autor == "yo" ? "yo" : a.Autor, a.Autor == "yo" ? Tema.Cyan : Tema.Texto),
                    Celda.C(Una(a.Texto, 300), Tema.Texto),
                    Celda.C(a.Porque, Tema.TextoSuave)));
            int urgentes = radar.Avisos.Count(a => a.Prioridad >= 70);
            tAvisos.Poner(fa, null, radar.Avisos.Count == 0 ? "" : $"{urgentes} para hoy · {radar.Avisos.Count} en total");

            var fn = new List<FilaTabla>();
            foreach (var n in radar.Notas.Take(300))
                fn.Add(new FilaTabla
                {
                    Tag = n,
                    Punto = n.Hecho ? Tema.MuyApagado : n.Quien == "yo" ? Tema.Cyan : Tema.Salvia,
                    Apagada = n.Hecho,
                    Celdas = new List<Celda>
                    {
                        Celda.C(n.Hecho ? "✓" : n.Fijada ? "★" : "", n.Hecho ? Tema.Salvia : Tema.Crema),
                        Celda.C(n.Modelo, n.Hecho ? Tema.Apagado : n.Quien == "yo" ? Tema.Cyan : Tema.Salvia),
                        Celda.C(n.Cuando.ToString("dd/MM HH:mm"), Tema.Apagado),
                        Celda.C(n.Quien, n.Quien == "yo" ? Tema.Cyan : Tema.Texto),
                        Celda.C(n.Conv, Tema.TextoSuave),
                        Celda.C(Una(n.Texto, 300), n.Hecho ? Tema.Apagado : Tema.Texto),
                        Celda.C(n.Vence.HasValue ? n.Vence.Value.ToString("dd/MM HH:mm") : "", n.Vence.HasValue ? Tema.Crema : Tema.Apagado),
                    }
                });
            int pendientes = radar.Notas.Count(n => !n.Hecho);
            tNotas.Poner(fn, null, radar.Notas.Count == 0 ? "" : $"{pendientes} pendiente/s de {radar.Notas.Count}");

            var fm = new List<FilaTabla>();
            foreach (var l in radar.Lectores)
                fm.Add(FilaTabla.F(l, l.Encontrados > 0 ? l.Tinte : Tema.MuyApagado,
                    Celda.C(l.Nombre, l.Encontrados > 0 ? l.Tinte : Tema.Apagado, l.Encontrados > 0),
                    Celda.C(l.Que, Tema.TextoSuave),
                    Celda.C(l.Encontrados.ToString(), l.Encontrados > 0 ? Tema.Texto : Tema.MuyApagado),
                    Celda.C(l.Ms.ToString(), Tema.Apagado)));
            tModelos.Poner(fm, null, radar.Lectores.Count + " micromodelos · " + radar.MsTotal + " ms");

            string detIA;
            bool hayIA = AsistenteIA.Disponible(out detIA);
            var datos = new List<Dato>
            {
                Dato.D("mensajes revisados", radar.Revisados.ToString("N0"), Tema.Cyan),
                Dato.D("ventana", radar.Dias + " día/s", Tema.Malva),
                Dato.D("corrido", radar.Corrido.HasValue ? Tema.Relativo(radar.Corrido.Value) : "nunca", Tema.TextoSuave),
                Dato.D("tardó", radar.MsTotal + " ms", radar.MsTotal > 3000 ? Tema.Durazno : Tema.Salvia),
                Dato.Titulo("avisos por micromodelo"),
            };
            foreach (var g in radar.Avisos.GroupBy(a => a.Modelo).OrderByDescending(g => g.Count()))
                datos.Add(Dato.D(g.Key, g.Count().ToString(), g.First().Tinte));
            if (radar.Avisos.Count == 0) datos.Add(Dato.D("nada", "ningún aviso", Tema.Apagado));
            datos.Add(Dato.Titulo("la libreta"));
            foreach (var g in radar.Notas.GroupBy(n => n.Modelo))
                datos.Add(Dato.D(g.Key, $"{g.Count(n => !n.Hecho)} de {g.Count()}", Tema.TextoSuave));
            datos.Add(Dato.Titulo("modelo local"));
            datos.Add(Dato.D("estado", hayIA ? "a mano" : "apagado", hayIA ? Tema.Salvia : Tema.Durazno));
            datos.Add(Dato.D("detalle", estadoIA.Length > 0 ? estadoIA : detIA, Tema.Apagado));
            fResumen.Poner(datos);

            Invalidate(rCards);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            int urgentes = radar.Avisos.Count(a => a.Prioridad >= 70);
            int deuda = radar.Avisos.Count(a => a.Modelo == "te deben respuesta");
            int pend = radar.Notas.Count(n => !n.Hecho);
            var r = Tarjetas(rCards, new[] { 1f, 1.2f, 1f, 1f, 1.5f });
            Ayuda.Tarjeta(g, esc, r[0], "PARA HOY", urgentes.ToString(), urgentes == 0 ? "nada urgente" : "avisos de prioridad alta", urgentes > 0 ? Tema.Rosa : Tema.Salvia);
            Ayuda.Tarjeta(g, esc, r[1], "SIN CONTESTAR", deuda.ToString(), deuda == 0 ? "no le debés respuesta a nadie" : "te preguntaron y no volviste", deuda > 0 ? Tema.Durazno : Tema.Salvia);
            Ayuda.Tarjeta(g, esc, r[2], "EN LA LIBRETA", pend.ToString(), radar.Notas.Count + " anotadas", Tema.Cyan);
            Ayuda.Tarjeta(g, esc, r[3], "MICROMODELOS", radar.Lectores.Count.ToString(), radar.MsTotal + " ms leyendo", Tema.Malva);
            Ayuda.Tarjeta(g, esc, r[4], "REVISADO", radar.Corrido.HasValue ? Tema.Relativo(radar.Corrido.Value) : "nunca",
                corriendo ? "leyendo tus conversaciones…" : $"{radar.Revisados:N0} mensajes de los últimos {radar.Dias} días",
                corriendo ? Tema.Durazno : radar.Corrido.HasValue ? Tema.Salvia : Tema.Apagado, Tema.Fina(15f));
        }
    }
}
