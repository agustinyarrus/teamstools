using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace TeamsTools
{
    // =====================================================================================================
    // PERFIL: tu estado en Teams, a mano o con un guión que lo haga parecer humano
    // =====================================================================================================
    internal sealed class VistaPerfil : Pantalla
    {
        readonly Presencia pres;
        readonly LectorPresenciaLog logTeams;
        readonly GuionPresencia guion;
        readonly Actor actor;

        readonly Segmentado segEstado = new Segmentado { Etiqueta = "poner este estado ahora, sin abrir Teams", Opciones = GuionPresencia.Estados };
        readonly Interruptor swGuion = new Interruptor { Etiqueta = "guión de presencia", Acento = Tema.Cyan, Ayuda = "hace que tu estado parezca de una persona" };
        readonly Interruptor swHorario = new Interruptor { Etiqueta = "respetar el horario laboral", Acento = Tema.Salvia, Ayuda = "fuera de hora no manejo nada" };
        readonly Interruptor swManual = new Interruptor { Etiqueta = "respetar lo que pongas a mano", Acento = Tema.Crema, Ayuda = "si elegís vos, me hago a un lado" };
        readonly Interruptor swDesconectar = new Interruptor { Etiqueta = "desconectarme al salir del horario", Acento = Tema.Malva };
        readonly Interruptor swOnline = new Interruptor { Etiqueta = "sostener Disponible con la tecla fantasma", Acento = Tema.Cyan, Ayuda = "la F15 evita el Ausente por inactividad" };
        readonly Interruptor swForzar = new Interruptor { Etiqueta = "si igual se va, fijarlo en el menú", Acento = Tema.Rosa, Ayuda = "lo único que lo rescata de verdad" };
        readonly Interruptor swPantalla = new Interruptor { Etiqueta = "no dejar que se apague la pantalla", Acento = Tema.Durazno };
        readonly Deslizador dUmbral = new Deslizador { Etiqueta = "tocar tras", Min = 15, Max = 280, Paso = 5, Acento = Tema.Cyan, Marcas = new[] { 60, 120, 240 } };
        readonly Deslizador dMinutos = new Deslizador { Etiqueta = "dura", Min = 1, Max = 480, Paso = 1, Acento = Tema.Cielo, Marcas = new[] { 30, 60, 120, 240 } };
        readonly Deslizador dVariacion = new Deslizador { Etiqueta = "variación", Min = 0, Max = 80, Paso = 5, Sufijo = "%", Acento = Tema.Malva, Marcas = new[] { 20, 40 }, Ayuda = "sin variación los cambios caen siempre en el mismo minuto" };
        readonly Segmentado segPaso = new Segmentado { Etiqueta = "estado del tramo", Opciones = GuionPresencia.Estados };
        readonly Medidor medCiclo = new Medidor { Etiqueta = "vuelta completa del guión", Min = 0, Max = 600, AltoEsMalo = false, Umbral = 480 };
        readonly Medidor medFalta = new Medidor { Etiqueta = "falta para el próximo cambio", Min = 0, Max = 100, AltoEsMalo = true };
        readonly Escalon esPosponer = new Escalon { Etiqueta = "«quedarme» pospone", Min = 1, Max = 120, Paso = 5, Acento = Tema.Crema, Formato = v => v + " min" };
        readonly Tabla tPasos = new Tabla
        {
            Etiqueta = "el guión · se repite en bucle", Vacio = "sin tramos: agregá uno", AltoFila = 21,
            Columnas =
            {
                new Columna { Titulo = "#", Peso = 0, MinAncho = 22, Mono = true },
                new Columna { Titulo = "estado", Peso = 1.3f, MinAncho = 84 },
                new Columna { Titulo = "dura", Peso = 0, MinAncho = 56, Derecha = true, Mono = true },
                new Columna { Titulo = "variación", Peso = 0, MinAncho = 54, Derecha = true, Mono = true },
                new Columna { Titulo = "entre", Peso = 0.9f, MinAncho = 78, Derecha = true, Mono = true },
                new Columna { Titulo = "parte del ciclo", Peso = 0.8f, MinAncho = 62, Derecha = true, Mono = true },
            }
        };
        readonly Ficha fEstado = new Ficha { Etiqueta = "tu perfil ahora", Acento = Tema.Cyan };
        readonly Cargador carga = new Cargador { Modo = Cargador.Estilo.Pulso, Acento = Tema.Malva, MostrarTiempo = true };
        Chip chAplicar, chAgregar, chBorrar, chSubir, chBajar, chPlantilla, chRestablecer;
        Rectangle rCards, rColIzq, rColDer;
        int plantilla;
        static readonly string[] Plantillas = { "más real", "jornada", "foco", "siempre online" };
        static readonly string[] ClavesPlantilla = { "real", "jornada", "foco", "siempre" };
        bool cargando;

        public VistaPerfil(Contexto c, Presencia p, LectorPresenciaLog l, GuionPresencia g, Actor a) : base(c)
        {
            pres = p; logTeams = l; guion = g; actor = a;
            foreach (Control x in new Control[] { segEstado, swGuion, swHorario, swManual, swDesconectar, swOnline, swForzar, swPantalla,
                                                  dUmbral, dMinutos, dVariacion, segPaso, medCiclo, medFalta, esPosponer, tPasos, fEstado, carga })
                Controls.Add(x);
            foreach (var pal in Controls.OfType<Palanca>()) pal.Superficie = Tema.Fondo;
            // cada estado con su color: el segmentado deja de ser una fila de texto y se lee de un vistazo
            var tintes = GuionPresencia.Estados.Select(VistaPatrones.ColorDe).ToArray();
            segEstado.Tintes = tintes;
            segPaso.Tintes = tintes;

            dUmbral.Formato = v => Presencia.Fmt(v);
            dMinutos.Formato = v => v >= 60 ? $"{v / 60} h {v % 60:00}" : v + " min";
            medCiclo.Texto = "";

            segEstado.Cambio += (o, e) => PonerEstado(segEstado.Texto);
            swGuion.Cambio += (o, e) => { guion.Activo = swGuion.Prendido; guion.Guardar(); if (guion.Activo) actor.Reiniciar(); else actor.Despertar(); Refrescar(); };
            swHorario.Cambio += (o, e) => { guion.RespetarHorario = swHorario.Prendido; guion.Guardar(); actor.Despertar(); };
            swManual.Cambio += (o, e) => { guion.RespetarManual = swManual.Prendido; guion.Guardar(); actor.Despertar(); };
            swDesconectar.Cambio += (o, e) => { guion.DesconectarFuera = swDesconectar.Prendido; guion.Guardar(); };
            swOnline.Cambio += (o, e) => { ctx.Cfg.PresenciaSiempreOnline = swOnline.Prendido; ctx.Cfg.Guardar(); pres.Despertar(); };
            swForzar.Cambio += (o, e) => { ctx.Cfg.PresenciaForzarEnTeams = swForzar.Prendido; ctx.Cfg.Guardar(); };
            swPantalla.Cambio += (o, e) => { ctx.Cfg.PresenciaEvitarSuspension = swPantalla.Prendido; ctx.Cfg.Guardar(); pres.Despertar(); };
            dUmbral.Cambio += (o, e) => { ctx.Cfg.PresenciaUmbralSegundos = dUmbral.Valor; ctx.Cfg.Guardar(); pres.Despertar(); };
            esPosponer.Cambio += (o, e) => { ctx.Cfg.PosponerMinutos = esPosponer.Valor; ctx.Cfg.Guardar(); };
            dMinutos.Cambio += (o, e) => CambiarPaso(x => x.Minutos = dMinutos.Valor);
            dVariacion.Cambio += (o, e) => CambiarPaso(x => x.Variacion = dVariacion.Valor);
            segPaso.Cambio += (o, e) => CambiarPaso(x => x.Estado = segPaso.Texto);
            tPasos.SeleccionCambio += (o, e) => CargarPaso();

            chAplicar = Nuevo("aplicar el guión ahora", Chip.Modo.Boton, Tema.Cyan, (o, e) => { actor.Reiniciar(); Refrescar(); });
            chPlantilla = Nuevo("plantilla: más real", Chip.Modo.Valor, Tema.Malva, (o, e) => Plantilla());
            chAgregar = Nuevo("+ tramo", Chip.Modo.Boton, Tema.Salvia, (o, e) => { guion.Pasos.Add(new Paso()); guion.Guardar(); Refrescar(); });
            chBorrar = Nuevo("borrar tramo", Chip.Modo.Boton, Tema.Rosa, (o, e) => BorrarPaso());
            chSubir = Nuevo("▲", Chip.Modo.Boton, Tema.Apagado, (o, e) => Mover(-1));
            chBajar = Nuevo("▼", Chip.Modo.Boton, Tema.Apagado, (o, e) => Mover(1));
            chRestablecer = Nuevo("restablecer estado en Teams", Chip.Modo.Boton, Tema.Durazno, (o, e) => PonerEstado("Disponible"));

            actor.Cambio += () => { try { ctx.EnUi?.Invoke(Refrescar); } catch { } };
        }

        // ------------------------------------------------------------------ acciones
        void PonerEstado(string estado)
        {
            if (estado.Length == 0 || ctx.Chat == null) return;
            carga.Texto = "poniendo «" + estado + "» en Teams"; carga.Desde = DateTime.Now; carga.Activo = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string det;
                bool ok = ctx.Chat.FijarPresencia(GuionPresencia.ParaTeams(estado), out det);
                ctx.Log?.Escribir(ok ? Nivel.Ok : Nivel.Aviso, $"Perfil: {(ok ? "puse" : "no pude poner")} «{estado}» · {det}");
                try { ctx.EnUi?.Invoke(() => { carga.Activo = false; carga.Texto = ""; if (ok) ctx.Aviso?.Invoke("Estado cambiado", estado); Refrescar(); }); } catch { }
            });
        }

        void Plantilla()
        {
            plantilla = (plantilla + 1) % Plantillas.Length;
            chPlantilla.Text = "plantilla: " + Plantillas[plantilla];
            chPlantilla.Ajustar();
            var nuevo = GuionPresencia.Plantilla(ClavesPlantilla[plantilla]);
            guion.Pasos = nuevo.Pasos;
            guion.Guardar();
            actor.Reiniciar();
            Acomodar();
            Refrescar();
        }

        Paso Elegido => tPasos.Actual != null ? tPasos.Actual.Tag as Paso : null;

        void CambiarPaso(Action<Paso> que)
        {
            if (cargando) return;
            var p = Elegido;
            if (p == null) return;
            que(p);
            guion.Guardar();
            actor.Despertar();
            Refrescar();
        }

        void BorrarPaso()
        {
            var p = Elegido;
            if (p == null || guion.Pasos.Count <= 1) return;
            guion.Pasos.Remove(p);
            guion.Guardar();
            actor.Reiniciar();
            Refrescar();
        }

        void Mover(int d)
        {
            var p = Elegido;
            if (p == null) return;
            int i = guion.Pasos.IndexOf(p), j = i + d;
            if (i < 0 || j < 0 || j >= guion.Pasos.Count) return;
            guion.Pasos.RemoveAt(i);
            guion.Pasos.Insert(j, p);
            guion.Guardar();
            tPasos.Seleccion = j;
            Refrescar();
        }

        // ------------------------------------------------------------------ layout
        public override void Acomodar()
        {
            int pad = S(4), y = S(6);
            rCards = new Rectangle(pad, y, Width - pad * 2, S(74));
            y = rCards.Bottom + S(10);
            y = Flujo(chips, pad, y, Width - pad * 2);
            carga.SetBounds(pad, y, Math.Max(S(200), Width - pad * 2), S(18));
            y += S(24);

            int anchoDer = (int)((Width - pad * 2) * 0.30);
            int anchoIzq = Width - pad * 2 - anchoDer - S(12);
            int alto = Math.Max(S(200), Height - y - S(10));
            rColIzq = new Rectangle(pad, y, anchoIzq, alto);
            rColDer = new Rectangle(pad + anchoIzq + S(12), y, anchoDer, alto);

            // izquierda: estado ahora, tabla del guión y el editor del tramo
            int yy = y;
            segEstado.SetBounds(rColIzq.X, yy, rColIzq.Width, S(44)); yy += S(52);
            int altoTabla = Math.Max(S(110), (int)(alto * 0.42));
            tPasos.SetBounds(rColIzq.X, yy, rColIzq.Width, altoTabla); yy += altoTabla + S(10);

            int col = (rColIzq.Width - S(24)) / 3;
            segPaso.SetBounds(rColIzq.X, yy, col, S(44));
            dMinutos.SetBounds(rColIzq.X + col + S(12), yy, col, S(44));
            dVariacion.SetBounds(rColIzq.X + col * 2 + S(24), yy, rColIzq.Width - col * 2 - S(24), S(52));
            yy += S(60);
            medCiclo.SetBounds(rColIzq.X, yy, (rColIzq.Width - S(12)) / 2, S(32));
            medFalta.SetBounds(rColIzq.X + (rColIzq.Width - S(12)) / 2 + S(12), yy, (rColIzq.Width - S(12)) / 2, S(32));

            // derecha: interruptores y la ficha
            int yd = y, hSw = S(26), hSwAyuda = S(36);
            foreach (var sw in new[] { swGuion, swHorario, swManual, swDesconectar })
            { sw.SetBounds(rColDer.X, yd, rColDer.Width, sw.Ayuda.Length > 0 ? hSwAyuda : hSw); yd += (sw.Ayuda.Length > 0 ? hSwAyuda : hSw) + S(4); }
            yd += S(8);
            foreach (var sw in new[] { swOnline, swForzar, swPantalla })
            { sw.SetBounds(rColDer.X, yd, rColDer.Width, sw.Ayuda.Length > 0 ? hSwAyuda : hSw); yd += (sw.Ayuda.Length > 0 ? hSwAyuda : hSw) + S(4); }
            // los siete son una sola columna visual: alinearlos juntos evita el borde derecho dentado
            Interruptor.Alinear(swGuion, swHorario, swManual, swDesconectar, swOnline, swForzar, swPantalla);
            dUmbral.SetBounds(rColDer.X, yd, rColDer.Width, S(46)); yd += S(54);
            esPosponer.SetBounds(rColDer.X, yd, rColDer.Width, S(42)); yd += S(50);
            fEstado.SetBounds(rColDer.X, yd, rColDer.Width, Math.Max(S(90), rColDer.Bottom - yd));
        }

        // ------------------------------------------------------------------ datos
        public override void Refrescar()
        {
            cargando = true;
            swGuion.Poner(guion.Activo);
            swHorario.Poner(guion.RespetarHorario);
            swManual.Poner(guion.RespetarManual);
            swDesconectar.Poner(guion.DesconectarFuera);
            swOnline.Poner(ctx.Cfg.PresenciaSiempreOnline);
            swForzar.Poner(ctx.Cfg.PresenciaForzarEnTeams);
            swPantalla.Poner(ctx.Cfg.PresenciaEvitarSuspension);
            dUmbral.Poner(ctx.Cfg.PresenciaUmbralSegundos);
            esPosponer.Poner(ctx.Cfg.PosponerMinutos);
            if (pres.MedicionFresca) segEstado.Poner(Lindo(pres.PresenciaTeams));

            var activos = guion.Pasos.Where(p => p.Activo).ToList();
            int ciclo = Math.Max(1, guion.MinutosDelCiclo);
            var filas = new List<FilaTabla>();
            int acum = 0;
            for (int i = 0; i < guion.Pasos.Count; i++)
            {
                var p = guion.Pasos[i];
                int idx = activos.IndexOf(p);
                bool enCurso = guion.Activo && idx >= 0 && idx == actor.Indice;
                int desde = acum, hasta = acum + p.Minutos;
                if (p.Activo) acum = hasta;
                filas.Add(new FilaTabla
                {
                    Tag = p,
                    Punto = VistaPatrones.ColorDe(p.Estado),
                    Apagada = !p.Activo,
                    Celdas = new List<Celda>
                    {
                        Celda.C((i + 1).ToString(), enCurso ? Tema.Cyan : Tema.Apagado, enCurso),
                        Celda.C(p.Estado + (enCurso ? "  ← ahora" : ""), VistaPatrones.ColorDe(p.Estado), enCurso),
                        Celda.C(Dura(p.Minutos), Tema.Texto),
                        Celda.C(p.Variacion > 0 ? "±" + p.Variacion + "%" : "exacto", p.Variacion > 0 ? Tema.Malva : Tema.Apagado),
                        Celda.C(p.Variacion > 0 ? Dura((int)(p.Minutos * (1 - p.Variacion / 100.0))) + "–" + Dura((int)(p.Minutos * (1 + p.Variacion / 100.0))) : "—", Tema.TextoSuave),
                        Celda.C(p.Activo ? $"{p.Minutos * 100.0 / ciclo:0} %" : "—", Tema.Apagado),
                    }
                });
            }
            tPasos.Poner(filas, Elegido, $"vuelta completa: {Dura(guion.MinutosDelCiclo)}");
            CargarPaso();

            medCiclo.Max = Math.Max(120, guion.MinutosDelCiclo);
            medCiclo.Poner(guion.MinutosDelCiclo, Dura(guion.MinutosDelCiclo));
            var paso = actor.PasoActual();
            int total = paso != null ? Math.Max(1, paso.Minutos * 60) : 1;
            int falta = actor.SegundosParaElProximo;
            medFalta.Max = total;
            medFalta.Poner(total - falta, guion.Activo ? (falta > 0 ? Presencia.Fmt(falta) : "—") : "apagado");

            bool fresca = pres.MedicionFresca;
            fEstado.Poner(
                Dato.D("Teams dice que estás", fresca ? pres.PresenciaTeams : "sin medir", fresca ? VistaPatrones.ColorDe(pres.PresenciaTeams) : Tema.Apagado),
                Dato.D("así desde", fresca && pres.PresenciaDesde.HasValue ? pres.PresenciaDesde.Value.ToString("HH:mm") : "—", Tema.TextoSuave),
                Dato.D("cómo lo sé", fresca ? pres.FuentePresencia : "—", Tema.Cyan),
                Dato.Titulo("el guión"),
                Dato.D("estado", actor.Estado, guion.Activo ? (actor.EnPausa ? Tema.Durazno : Tema.Salvia) : Tema.Apagado),
                Dato.D("tramo", paso != null ? paso.Estado : "—", paso != null ? VistaPatrones.ColorDe(paso.Estado) : Tema.Apagado),
                Dato.D("próximo cambio", guion.Activo && falta > 0 ? "en " + Presencia.Fmt(falta) : "—", Tema.Cielo),
                Dato.D("cambios · fallos", $"{actor.Cambios} · {actor.Fallos}", actor.Fallos > 0 ? Tema.Rosa : Tema.Salvia),
                Dato.D("último", actor.UltimoCambio.HasValue ? Tema.Relativo(actor.UltimoCambio.Value) : "ninguno", Tema.TextoSuave),
                Dato.D("detalle", actor.UltimoDetalle.Length > 0 ? actor.UltimoDetalle : "—", Tema.Apagado),
                Dato.Titulo("permanencia online"),
                Dato.D("toques · fallos", $"{pres.Toques} · {pres.Fallos}", pres.Fallos > 0 ? Tema.Rosa : Tema.Salvia),
                Dato.D("derivas · corregidas", $"{pres.Derivas} · {pres.Correcciones}", pres.Derivas > pres.Correcciones ? Tema.Durazno : Tema.Salvia),
                Dato.D("forzados · sondas", $"{pres.Forzados} · {pres.Sondas}", Tema.TextoSuave));

            cargando = false;
            Invalidate(rCards);
        }

        void CargarPaso()
        {
            cargando = true;
            var p = Elegido;
            if (p != null)
            {
                segPaso.Poner(p.Estado);
                dMinutos.Poner(p.Minutos);
                dVariacion.Poner(p.Variacion);
            }
            cargando = false;
        }

        static string Dura(int min) => min >= 60 ? $"{min / 60}h{(min % 60 > 0 ? (min % 60).ToString("00") : "")}" : min + "m";

        /// <summary>Lo que Teams muestra («Ausente») contra lo que se puede elegir («Ausente» en nuestra lista).</summary>
        static string Lindo(string estadoDeTeams)
        {
            if (Estados.EsDisponible(estadoDeTeams)) return "Disponible";
            if (Estados.EsAusente(estadoDeTeams)) return "Ausente";
            if (Estados.EsDesconectado(estadoDeTeams)) return "Desconectado";
            var m = GuionPresencia.Estados.FirstOrDefault(x => string.Equals(x, estadoDeTeams, StringComparison.OrdinalIgnoreCase));
            return m ?? "Disponible";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            bool fresca = pres.MedicionFresca;
            var paso = actor.PasoActual();
            int falta = actor.SegundosParaElProximo;
            var r = Tarjetas(rCards, new[] { 1.3f, 1.2f, 1f, 1f, 1.2f });
            Ayuda.Tarjeta(g, esc, r[0], "ESTADO AHORA", fresca ? pres.PresenciaTeams : "sin medir",
                fresca && pres.PresenciaDesde.HasValue ? "desde las " + pres.PresenciaDesde.Value.ToString("HH:mm") : "",
                fresca ? VistaPatrones.ColorDe(pres.PresenciaTeams) : Tema.Apagado, Tema.Fina(16f));
            Ayuda.Tarjeta(g, esc, r[1], "EL GUIÓN", guion.Activo ? (actor.EnPausa ? "en pausa" : "manejando") : "apagado",
                guion.Activo ? actor.Estado : "prendelo en el interruptor", guion.Activo ? (actor.EnPausa ? Tema.Durazno : Tema.Salvia) : Tema.Apagado, Tema.Fina(15f));
            Ayuda.Tarjeta(g, esc, r[2], "PRÓXIMO CAMBIO", guion.Activo && falta > 0 ? Presencia.Fmt(falta) : "—",
                paso != null ? "sale de «" + paso.Estado + "»" : "", Tema.Cielo);
            Ayuda.Tarjeta(g, esc, r[3], "VUELTA COMPLETA", Dura(guion.MinutosDelCiclo),
                guion.Pasos.Count(p => p.Activo) + " tramo/s", Tema.Malva);
            Ayuda.Tarjeta(g, esc, r[4], "CAMBIOS DEL GUIÓN", actor.Cambios.ToString(),
                actor.Fallos > 0 ? actor.Fallos + " fallo/s · " + actor.UltimoDetalle : (actor.UltimoCambio.HasValue ? "último " + Tema.Relativo(actor.UltimoCambio.Value) : "ninguno todavía"),
                actor.Fallos > 0 ? Tema.Rosa : Tema.Cyan);
        }
    }
}
