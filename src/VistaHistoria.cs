using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace TeamsTools
{
    // =====================================================================================================
    // HISTORIA: todo lo que se dijo en Teams, leído del disco y sin abrir una sola ventana
    // =====================================================================================================
    internal sealed class VistaHistoria : Pantalla
    {
        readonly Historia hist;
        readonly Campo cTexto = new Campo { Etiqueta = "buscar en todo el historial · varias palabras = todas tienen que estar", Pista = "deploy viernes" };
        readonly Campo cConv = new Campo { Etiqueta = "conversación", Pista = "Equipo Dev" };
        readonly Campo cAutor = new Campo { Etiqueta = "autor", Pista = "Rivas" };
        readonly Tabla tMsgs = new Tabla
        {
            Etiqueta = "resultados", Vacio = "escribí algo arriba, o tocá «releer Teams» si nunca extrajiste", AltoFila = 19,
            Columnas =
            {
                new Columna { Titulo = "fecha", Peso = 0, MinAncho = 72, Mono = true },
                new Columna { Titulo = "conversación", Peso = 0.9f, MinAncho = 84 },
                new Columna { Titulo = "autor", Peso = 0.75f, MinAncho = 78 },
                new Columna { Titulo = "mensaje", Peso = 3f, MinAncho = 200 },
                new Columna { Titulo = "señales", Peso = 0.9f, MinAncho = 90 },
                new Columna { Titulo = "prio", Peso = 0, MinAncho = 38, Derecha = true, Mono = true },
            }
        };
        readonly Tabla tSenales = new Tabla
        {
            Etiqueta = "micromodelos sobre el mensaje elegido", Vacio = "elegí un mensaje de la lista", AltoFila = 18,
            Columnas =
            {
                new Columna { Titulo = "modelo", Peso = 0, MinAncho = 76 },
                new Columna { Titulo = "dice", Peso = 1.2f, MinAncho = 76 },
                new Columna { Titulo = "por qué", Peso = 1.8f, MinAncho = 110 },
                new Columna { Titulo = "%", Peso = 0, MinAncho = 34, Derecha = true, Mono = true },
            }
        };
        readonly Barras gConvs = new Barras { Etiqueta = "dónde se habla más", Vacio = "sin datos" };
        readonly Ficha fResumen = new Ficha { Etiqueta = "el corpus", Acento = Tema.Cielo };
        readonly Cargador carga = new Cargador { Modo = Cargador.Estilo.Barra, Acento = Tema.Malva, MostrarTiempo = true };
        Chip chReleer, chRuido, chMios, chRango, chCopiar;
        bool incluirRuido, soloMios;
        int diasRango;                       // 0 = todo
        static readonly int[] PresetsRango = { 0, 1, 7, 30, 180, 365 };
        Rectangle rCards;
        MensajeHist elegido;
        string estado = "";

        public VistaHistoria(Contexto c, Historia h) : base(c)
        {
            hist = h;
            Controls.Add(cTexto); Controls.Add(cConv); Controls.Add(cAutor);
            Controls.Add(tMsgs); Controls.Add(tSenales); Controls.Add(gConvs); Controls.Add(fResumen); Controls.Add(carga);
            cTexto.Cambio += (o, e) => Buscar();
            cConv.Cambio += (o, e) => Buscar();
            cAutor.Cambio += (o, e) => Buscar();
            tMsgs.SeleccionCambio += (o, e) => MostrarSenales();

            chReleer = Nuevo("releer Teams", Chip.Modo.Boton, Tema.Cyan, (o, e) => { if (!hist.Trabajando) hist.ReleerAsync(); });
            chRango = Nuevo("todo el historial", Chip.Modo.Valor, Tema.Malva, (o, e) =>
            {
                int i = Array.IndexOf(PresetsRango, diasRango);
                diasRango = PresetsRango[(i + 1) % PresetsRango.Length];
                chRango.Text = diasRango == 0 ? "todo el historial" : diasRango == 1 ? "solo hoy" : "últimos " + diasRango + " días";
                chRango.Ajustar(); Acomodar(); Buscar();
            });
            chMios = Nuevo("solo los míos", Chip.Modo.Toggle, Tema.Salvia, (o, e) => { soloMios = !soloMios; chMios.Activo = soloMios; chMios.Invalidate(); Buscar(); });
            chRuido = Nuevo("incluir altas y llamadas", Chip.Modo.Toggle, Tema.Apagado, (o, e) => { incluirRuido = !incluirRuido; chRuido.Activo = incluirRuido; chRuido.Invalidate(); Buscar(); });
            chCopiar = Nuevo("copiar lo que se ve", Chip.Modo.Boton, Tema.Crema, (o, e) => Copiar());

            hist.Cambio += () => { try { ctx.EnUi?.Invoke(() => { estado = hist.Paso; Refrescar(); }); } catch { } };
        }

        void Copiar()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var f in tMsgs.Filas.Take(500))
                {
                    var m = f.Tag as MensajeHist;
                    if (m == null) continue;
                    sb.AppendLine($"{(m.SinFecha ? "sin fecha" : m.Fecha.ToString("yyyy-MM-dd HH:mm"))}  {m.Conv} · {m.Autor}: {m.Texto}");
                }
                if (sb.Length == 0) { ctx.Aviso?.Invoke("No hay nada que copiar", ""); return; }
                Clipboard.SetText(sb.ToString());
                ctx.Aviso?.Invoke("Copiado", $"{tMsgs.Filas.Count} línea/s al portapapeles");
            }
            catch (Exception ex) { ctx.Aviso?.Invoke("No pude copiar", ex.Message); }
        }

        public override void Acomodar()
        {
            int pad = S(4), y = S(6);
            rCards = new Rectangle(pad, y, Width - pad * 2, S(74));
            y = rCards.Bottom + S(10);
            int anchoBusq = (int)((Width - pad * 2) * 0.46);
            int anchoCol = (Width - pad * 2 - anchoBusq - S(24)) / 2;
            cTexto.SetBounds(pad, y, anchoBusq, S(46));
            cConv.SetBounds(pad + anchoBusq + S(12), y, anchoCol, S(46));
            cAutor.SetBounds(pad + anchoBusq + anchoCol + S(24), y, Width - pad * 2 - anchoBusq - anchoCol - S(24), S(46));
            y += S(46) + S(8);
            y = Flujo(chips, pad, y, Width - pad * 2);
            carga.SetBounds(pad, y, Math.Max(S(200), Width - pad * 2), S(18));
            y += S(22);

            int alto = Math.Max(S(150), Height - y - S(10));
            int anchoDer = (int)((Width - pad * 2) * 0.28);
            int anchoIzq = Width - pad * 2 - anchoDer - S(12);
            int altoArriba = (int)(alto * 0.62);
            tMsgs.SetBounds(pad, y, anchoIzq, altoArriba);
            tSenales.SetBounds(pad, y + altoArriba + S(12), anchoIzq, alto - altoArriba - S(12));
            fResumen.SetBounds(pad + anchoIzq + S(12), y, anchoDer, (int)(alto * 0.56));
            gConvs.SetBounds(pad + anchoIzq + S(12), y + (int)(alto * 0.56) + S(12), anchoDer, alto - (int)(alto * 0.56) - S(12));
        }

        public override void Refrescar()
        {
            if (!hist.Hay && !hist.Trabajando && hist.Problema.Length == 0) hist.CargarAsync();
            // 🚨 cambiar el texto cambia el ANCHO del chip: sin reacomodar la fila, se superpone con el de al lado
            string quiere = hist.Trabajando ? "trabajando…" : "releer Teams";
            if (chReleer.Text != quiere) { chReleer.Text = quiere; chReleer.Ajustar(); Acomodar(); }
            tMsgs.Cargando = hist.Trabajando || (!hist.Hay && hist.Problema.Length == 0);
            tSenales.Cargando = tMsgs.Cargando;
            if (hist.Trabajando)
            {
                if (!carga.Activo) { carga.Desde = DateTime.Now; carga.Activo = true; }
                carga.Texto = hist.Paso.Length > 0 ? hist.Paso : "trabajando";
            }
            else if (carga.Activo) { carga.Activo = false; carga.Texto = ""; }
            Buscar();
            LlenarResumen();
            Invalidate(rCards);
        }

        /// <summary>«buscar:texto» · «conv:texto» · «autor:texto» (separados por «;»): una búsqueda armada desde afuera, para fotografiarla.</summary>
        public override void Modo(string que)
        {
            if (string.IsNullOrEmpty(que)) { cTexto.Texto = ""; cConv.Texto = ""; cAutor.Texto = ""; Buscar(); return; }
            foreach (var parte in que.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int i = parte.IndexOf(':');
                if (i < 0) continue;
                string k = parte.Substring(0, i).Trim().ToLowerInvariant(), v = parte.Substring(i + 1).Trim();
                if (k == "buscar") cTexto.Texto = v; else if (k == "conv") cConv.Texto = v; else if (k == "autor") cAutor.Texto = v;
            }
            Buscar();
        }

        void Buscar()
        {
            DateTime? desde = diasRango > 0 ? (DateTime?)DateTime.Today.AddDays(-(diasRango - 1)) : null;
            var res = hist.Buscar(cTexto.Texto, cConv.Texto, cAutor.Texto, desde, null, incluirRuido, 3000);
            if (soloMios) res = res.Where(m => m.Mio).ToList();

            var filas = new List<FilaTabla>();
            foreach (var m in res.Take(1200))
            {
                var señales = Micromodelos.Detectar(m.Texto);
                int prio = Micromodelos.Prioridad(señales);
                filas.Add(FilaTabla.F(m, m.Mio ? Tema.Cyan : Tema.Malva,
                    Celda.C(m.SinFecha ? "—" : m.Fecha.ToString("dd/MM HH:mm"), Tema.Apagado),
                    Celda.C(m.Conv, Tema.TextoSuave),
                    Celda.C(m.Mio ? "yo" : m.Autor, m.Mio ? Tema.Cyan : Tema.Texto, m.Mio),
                    Celda.C(Una(m.Texto), m.Borrado ? Tema.MuyApagado : Tema.Texto),
                    Celda.C(Micromodelos.Resumen(señales), Tema.Apagado),
                    Celda.C(prio > 0 ? prio.ToString() : "", prio >= 50 ? Tema.Rosa : prio >= 25 ? Tema.Durazno : Tema.Apagado)));
            }
            tMsgs.Poner(filas, null, res.Count >= 3000 ? "3000+ (acotá la búsqueda)" : res.Count + " encontrado/s");

            gConvs.Poner(res.GroupBy(m => m.Conv).OrderByDescending(g => g.Count()).Take(12)
                            .Select(g => new Barra { Etiqueta = g.Key, Valor = g.Count(), Color = Tema.Cielo })
                            .ToList(), res.Count + " en el filtro");
            if (elegido == null || !res.Contains(elegido)) MostrarSenales();
        }

        static string Una(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            var s = t.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return s.Length > 400 ? s.Substring(0, 400) + "…" : s;
        }

        void MostrarSenales()
        {
            var m = tMsgs.Actual != null ? tMsgs.Actual.Tag as MensajeHist : null;
            elegido = m;
            if (m == null) { tSenales.Poner(new List<FilaTabla>(), null, ""); return; }
            var señales = Micromodelos.Detectar(m.Texto);
            var filas = new List<FilaTabla>();
            foreach (var s in señales.OrderByDescending(x => x.Puntaje))
                filas.Add(FilaTabla.F(s, s.Prendida ? s.Tinte : Tema.MuyApagado,
                    Celda.C(s.Modelo, s.Prendida ? s.Tinte : Tema.Apagado, s.Prendida),
                    Celda.C(s.Etiqueta, s.Prendida ? Tema.Texto : Tema.Apagado),
                    Celda.C(s.Evidencia, Tema.TextoSuave),
                    Celda.C((s.Puntaje * 100).ToString("0"), s.Prendida ? Tema.Texto : Tema.MuyApagado)));
            tSenales.Poner(filas, null, $"prioridad {Micromodelos.Prioridad(señales)} · {señales.Count(x => x.Prendida)} señal/es prendidas");
        }

        void LlenarResumen()
        {
            var datos = new List<Dato>
            {
                Dato.D("mensajes", hist.Mensajes.Count.ToString("N0"), hist.Hay ? Tema.Cyan : Tema.Apagado),
                Dato.D("con texto", hist.ConTexto.ToString("N0"), Tema.TextoSuave),
                Dato.D("míos · de otros", $"{hist.Mios:N0} · {(hist.Mensajes.Count - hist.Mios):N0}", Tema.TextoSuave),
                Dato.D("altas y llamadas", hist.Ruido.ToString("N0"), Tema.Apagado),
                Dato.D("conversaciones", hist.Conversaciones.Length.ToString(), Tema.Malva),
                Dato.D("personas", hist.Autores.Length.ToString(), Tema.Malva),
                Dato.D("desde", hist.Desde.HasValue ? hist.Desde.Value.ToString("dd/MM/yyyy") : "—", Tema.TextoSuave),
                Dato.D("hasta", hist.Hasta.HasValue ? hist.Hasta.Value.ToString("dd/MM/yyyy HH:mm") : "—", Tema.TextoSuave),
                Dato.D("extraído", hist.Extraido.HasValue ? Tema.Relativo(hist.Extraido.Value) : "nunca", hist.Extraido.HasValue ? Tema.Salvia : Tema.Durazno),
            };
            if (hist.Problema.Length > 0) datos.Add(Dato.D("problema", hist.Problema, Tema.Rosa));
            datos.Add(Dato.Titulo("por año"));
            foreach (var g in hist.Mensajes.Where(m => !m.SinFecha && !m.EsRuido).GroupBy(m => m.Fecha.Year).OrderByDescending(g => g.Key).Take(8))
                datos.Add(Dato.D(g.Key.ToString(), g.Count().ToString("N0"), g.Key == DateTime.Today.Year ? Tema.Cyan : Tema.TextoSuave));
            fResumen.Poner(datos);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            var r = Tarjetas(rCards, new[] { 1.1f, 1f, 1f, 1f, 1.5f });
            Ayuda.Tarjeta(g, esc, r[0], "MENSAJES", hist.Hay ? hist.Mensajes.Count.ToString("N0") : "—", hist.Hay ? hist.ConTexto.ToString("N0") + " con texto" : "todavía sin leer", hist.Hay ? Tema.Cyan : Tema.Apagado);
            Ayuda.Tarjeta(g, esc, r[1], "CONVERSACIONES", hist.Conversaciones.Length.ToString(), hist.Autores.Length + " personas", Tema.Malva);
            Ayuda.Tarjeta(g, esc, r[2], "EN PANTALLA", tMsgs.Filas.Count.ToString("N0"), "del filtro actual", Tema.Cielo);
            Ayuda.Tarjeta(g, esc, r[3], "AÑOS", hist.Desde.HasValue && hist.Hasta.HasValue ? (hist.Hasta.Value.Year - hist.Desde.Value.Year + 1).ToString() : "—",
                hist.Desde.HasValue ? "desde " + hist.Desde.Value.Year : "", Tema.Crema);
            string sub = hist.Trabajando ? (estado.Length > 0 ? estado : "trabajando…")
                : hist.Problema.Length > 0 ? hist.Problema
                : hist.Extraido.HasValue ? "extraído " + Tema.Relativo(hist.Extraido.Value) + " · sin abrir Teams" : "tocá «releer Teams»";
            Ayuda.Tarjeta(g, esc, r[4], "LECTURA DEL DISCO", hist.Trabajando ? "trabajando" : hist.Hay ? "al día" : "sin datos", sub,
                hist.Trabajando ? Tema.Durazno : hist.Hay ? Tema.Salvia : Tema.Apagado, Tema.Fina(15f));
        }
    }
}
