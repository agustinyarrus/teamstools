using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace TeamsTools
{
    // =====================================================================================================
    // VISOR DE TRANSCRIPCIÓN — leer la reunión como lo que fue: una conversación.
    //
    //   ┌ TRANSCRIPCIÓN · 7 voces · 183 turnos ──────────────────────────── 🔍  ▶  ⤢ ┐
    //   │ ▁▁▃▃██▅▁▁▃▃▃██▁▁▅▅   la CINTA: quién habló cuándo (en grande, un carril por voz) │
    //   │ ● Vale 38 %  ● Persona 4 12 %  …                  quién habló cuánto        │
    //   │ 00:42 ┃ Vale    Sí, exactamente. O sea, por cada interacción…                  │
    //   │ 00:53 ┃ Persona 3  María, ¿por qué te pone 12 usuarios activos ahora?          │
    //   └──────────────────────────────────────────────────────────────────────────────┘
    //
    // · Cada voz con su color en toda la pantalla (renglón, cinta, leyenda); «Yo» en malva, como TU MIC.
    // · Escuchar desde cualquier turno (clic en la hora) con KARAOKE: lo dicho se ilumina al ritmo del audio real
    //   (la posición sale del parlante, no de un reloj), la cinta muestra el cabezal y la vista lo sigue sola.
    // · Buscar escribiendo (sin tildes ni mayúsculas), con las apariciones marcadas en el texto, en la cinta y en la
    //   barra de desplazamiento. Resaltar una voz. Renombrar «Persona 3» con los nombres del panel de Teams, y el
    //   DETECTIVE sugiere quién es quién por cómo se nombran en la conversación.
    // · Todo dibujado a mano, virtualizado (solo se pinta lo visible: O(log n) para encontrar el primer turno) y sin
    //   medir texto: Cascadia es monoespaciada y el ajuste de línea es aritmética (ver Prosa).
    // · Nada de disco ni procesos en el hilo de la UI: la lectura va de fondo y el audio en su propio hilo.
    // =====================================================================================================

    /// <summary>Lo que el visor necesita de una grabación, leído de fondo.</summary>
    internal sealed class PaqueteTranscripcion
    {
        public Transcripcion T = Transcripcion.Vacia;
        public Dictionary<string, string> Nombres = new Dictionary<string, string>(StringComparer.Ordinal);
        public Participantes Gente = new Participantes();
        public List<Sugerencia> Sugerencias = new List<Sugerencia>();
        /// <summary>El audio para escuchar: audio.opus (siempre se guarda) o, si no, el 16 kHz. "" si no hay.</summary>
        public string Audio = "";
        public long Ms;

        static readonly Regex ReRotulo = new Regex(@"(?m)^\[\d{1,2}:\d{2}:\d{2}\]\s+[^:\r\n]{1,48}?:\s?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Lee todo lo de una grabación. Disco + CPU: SIEMPRE de fondo. O(tamaño de la transcripción).</summary>
        public static PaqueteTranscripcion Leer(Grabacion g, string rutaCorrillos)
        {
            var reloj = Stopwatch.StartNew();
            var p = new PaqueteTranscripcion();
            if (g == null) return p;
            string dir = g.Carpeta;
            bool hayCarpeta = dir.Length > 0 && Directory.Exists(dir);
            double dur = g.SegundosAudio > 0 ? g.SegundosAudio : g.SegundosTranscriptos;
            Transcripcion t = null;
            if (hayCarpeta)
            {
                var segs = Json.Lista(Json.LeerObjeto(Path.Combine(dir, "audio16.json")), "segments");
                if (segs.Count > 0) t = Transcripcion.DesdeSegmentos(segs, dur);
            }
            // el json manda si cuenta la misma reunión que el índice; si no está o quedó de otra corrida, manda el índice
            if (t == null || t.EsVacia || !Coincide(t, g.Texto)) t = Transcripcion.DesdeTexto(g.Texto, dur);
            p.T = t;
            if (hayCarpeta)
            {
                p.Nombres = NombresDeVoces.Leer(dir);
                string opus = g.Archivo.Length > 0 ? g.Archivo : Path.Combine(dir, "audio.opus"), w16 = Path.Combine(dir, "audio16.wav");
                p.Audio = File.Exists(opus) ? opus : File.Exists(w16) ? w16 : "";
            }
            t.AplicarNombres(p.Nombres);
            if (t.ConVoces)
            {
                p.Gente = Participantes.Leer(rutaCorrillos, g.Desde, g.Hasta);
                p.Sugerencias = Detective.Sugerir(t, p.Gente, g.Reunion, p.Nombres);
            }
            p.Ms = reloj.ElapsedMilliseconds;
            return p;
        }

        /// <summary>El json y el índice cuentan la misma reunión si tienen las mismas palabras (±3 %), sin los rótulos «[hh:mm:ss] Quién:».</summary>
        static bool Coincide(Transcripcion t, string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return true;
            int b = Transcripcion.ContarPalabras(ReRotulo.Replace(texto, ""));
            return Math.Abs(t.Palabras - b) <= Math.Max(12, b * 0.03);
        }
    }

    internal sealed class VisorTranscripcion : Control, IRueda
    {
        enum Estado { SinGrabacion, Cargando, SinTexto, Lista }

        sealed class Bloque
        {
            public int Turno, Y, Alto, Cabecera, Sangria;
            public string Prefijo = "";
            /// <summary>Compacto: la sugerencia del detective que va pegada al nombre («¿Valentina?»), parte del prefijo.</summary>
            public string Sugerida = "";
            public List<Prosa.Renglon> Renglones = new List<Prosa.Renglon>();
            public int[] YRenglon = new int[0];
        }

        sealed class Zona
        {
            public Rectangle R;
            /// <summary>La parte de la pantalla que la dibujó: al repintar esa parte, se rehacen solo sus zonas.</summary>
            public string Seccion = "";
            public string Que = "";
            public object Dato;
            public string Ayuda = "";
        }

        // ---------------------------------------------------------------- estado
        Estado estado = Estado.SinGrabacion;
        Grabacion g;
        string firma = "", rutaCorrillos = "";
        int generacion;
        PaqueteTranscripcion paq = new PaqueteTranscripcion();
        Transcripcion T => paq.T;

        // vista
        float esc = 1f;
        public bool Amplio { get; private set; }
        readonly List<Bloque> bloques = new List<Bloque>();
        int altoTotal, anchoArmado = -1;
        bool armadoAmplio;
        double desplaz, objetivo;                 // lo pintado persigue al objetivo: el desplazamiento es suave
        double desplazLateral;
        DateTime ultimoCuadro = DateTime.Now;
        Rectangle rEnc, rCinta, rEje, rLeyenda, rLateral, rCuerpo, rBarra;
        int gutter, lineH, gapBloque, gapParrafo, cabeceraAmplio;
        float avance;
        Font fTexto, fNombre, fHora, fMeta;
        readonly List<Zona> zonas = new List<Zona>();
        string seccion = "";                      // la sección que se está pintando: sus zonas se rehacen, las demás quedan
        /// <summary>
        /// Reloj PROPIO (no el compartido de Animacion, que repinta el control entero): cada cuadro invalida solo lo que
        /// cambió. El karaoke en grande repintando todo costaba ~40 ms por cuadro con la máquina cargada; así, unos pocos.
        /// </summary>
        readonly Timer reloj = new Timer { Interval = MsCuadro };
        const int MsCuadro = 33;
        const int MsParpadeo = 250;
        int ultimoSonando = -1, ultimoRenglon = -1, ultimoSegundo = -1, ultimoXCabezal = int.MinValue;
        Rectangle rTiempo;                        // dónde está la hora del encabezado (se repinta sola, una vez por segundo)
        DateTime ultimoParpadeo = DateTime.MinValue;
        Bitmap cintaCache;                        // lo quieto de la cinta (carriles, turnos, apariciones, eje): se blitea por cuadro
        string cintaClave = "";
        Rectangle rCintaCache;
        readonly Dictionary<string, int> carrilDe = new Dictionary<string, int>(StringComparer.Ordinal);
        int carriles = 1;

        // interacción
        Zona zonaHover;
        int turnoHover = -1;
        bool horaHover;
        Point raton;
        bool enCinta, arrastrandoCinta, arrastrandoBarra;
        int agarreBarra;
        string filtro = "";                      // voz resaltada
        string consulta = "";
        bool buscando;
        List<Aparicion> hallazgos = new List<Aparicion>();
        Dictionary<int, List<Aparicion>> hallazgosPorTurno = new Dictionary<int, List<Aparicion>>();
        int hallazgoActual = -1;

        // sonido
        Reproductor rep;
        bool siguiendo = true;
        double? posicionSimulada;                 // para las fotos y las pruebas: un karaoke sin audio
        DateTime simuladaHasta;                   // ...que vence solo: una foto nunca deja un karaoke fantasma andando
        const int SegundosSimulacion = 45;

        /// <summary>Por qué no se puede escuchar ahora ("" = se puede). Lo pone la pantalla: grabando una llamada, no.</summary>
        public Func<string> MotivoParaNoSonar = () => "";
        public event Action<bool> PideAmplio;
        public event Action<string, string> Aviso;
        public event Action NombresCambiaron;
        public event Action CambioSonido;

        public VisorTranscripcion()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Tema.Fondo;
            TabStop = false;
            Fuentes();
            reloj.Tick += (o, e) => Cuadro();
        }

        public IDictionary<string, string> Nombres => paq.Nombres;
        public bool Sonando => rep != null && rep.Activo;
        public Reproductor.Fase FaseSonido => rep?.Estado ?? Reproductor.Fase.Quieto;
        public string IdActual => g?.Id ?? "";

        // ================================================================== datos

        /// <summary>
        /// La grabación elegida. Se llama en cada refresco de la pantalla, así que solo relee si cambió algo que importa
        /// (otra grabación, texto nuevo, otro estado): una FIRMA barata decide. La lectura va de fondo.
        /// </summary>
        public void Poner(Grabacion x, string corrillos)
        {
            rutaCorrillos = corrillos ?? "";
            bool otra = x?.Id != g?.Id;
            g = x;
            if (otra)
            {
                // otra grabación: nada de la anterior queda a la vista (ni sus nombres, que el resumen usa enseguida)
                rep?.Parar(); posicionSimulada = null; filtro = ""; LimpiarBusqueda();
                desplaz = objetivo = 0; desplazLateral = 0; siguiendo = true;
                paq = new PaqueteTranscripcion();
            }
            if (x == null) { firma = ""; paq = new PaqueteTranscripcion(); estado = Estado.SinGrabacion; Reacomodar(); Latir(); Invalidate(); return; }
            string f = $"{x.Id}|{x.Texto.Length}|{x.Estado}|{(int)x.SegundosTranscriptos}";
            if (f == firma) { if (estado == Estado.SinTexto) Invalidate(); return; }
            firma = f;
            if (x.Texto.Length == 0) { paq = new PaqueteTranscripcion(); estado = Estado.SinTexto; Reacomodar(); Latir(); Invalidate(); return; }
            // la misma grabación con texto nuevo: se deja la vieja a la vista mientras llega la nueva. Otra grabación: los
            // bloques armados son de la ANTERIOR y el paquete ya está vacío → se rearma YA (si no, pintar indexaría
            // turnos que no existen)
            if (otra || estado != Estado.Lista) { estado = Estado.Cargando; Reacomodar(); }
            int gen = ++generacion;
            var copia = x;
            Latir(); Invalidate();
            Fondo.Correr(this, "leyendo la transcripción", () => PaqueteTranscripcion.Leer(copia, rutaCorrillos),
                p => { if (gen == generacion) PonerPaquete(copia, p); },
                ex => { if (gen == generacion) { estado = Estado.SinTexto; Aviso?.Invoke("No pude leer la transcripción", ex.Message); Invalidate(); } },
                Tema.Malva, mostrar: false);
        }

        /// <summary>Pone un paquete ya leído (la lectura de fondo termina acá; las fotos y pruebas lo llaman directo).</summary>
        public void PonerPaquete(Grabacion x, PaqueteTranscripcion p)
        {
            bool otra = x?.Id != g?.Id;
            g = x;
            paq = p ?? new PaqueteTranscripcion();
            if (otra) { filtro = ""; LimpiarBusqueda(); desplaz = objetivo = 0; }
            if (x != null && firma.Length == 0) firma = $"{x.Id}|{x.Texto.Length}|{x.Estado}|{(int)x.SegundosTranscriptos}";
            estado = x == null ? Estado.SinGrabacion : T.EsVacia ? Estado.SinTexto : Estado.Lista;
            cintaClave = "";                              // otra transcripción: la cinta cacheada ya no sirve
            if (filtro.Length > 0 && T.VozDe(filtro) == null) filtro = "";
            if (consulta.Length > 0) Buscar(consulta, false);
            Reacomodar();
            Latir();
            Invalidate();
            NombresCambiaron?.Invoke();          // llegaron los nombres de ESTA grabación: el resumen los usa
        }

        /// <summary>La transcripción para pegar en otro lado, con los nombres puestos y saltos de Windows. "" si no hay.</summary>
        public string TextoParaCopiar(int desde = 0) => T.EsVacia ? "" : T.ComoTexto(desde);

        public void CambiarAmplio(bool si)
        {
            if (Amplio == si) return;
            Amplio = si;
            Fuentes();
            anchoArmado = -1;
            PideAmplio?.Invoke(si);
            Reacomodar();
            Invalidate();
        }

        // ================================================================== medidas y armado

        int S(int px) => Dpi.S(esc, px);

        void Fuentes()
        {
            fTexto = Amplio ? Tema.Fina(12f) : Tema.Fina(10f);
            fNombre = Amplio ? Tema.Media(11f) : Tema.Media(10f);
            fHora = Amplio ? Tema.Mono(8.5f) : Tema.Mono(8f);
            fMeta = Amplio ? Tema.Fina(8.5f) : Tema.Fina(8f);
        }

        /// <summary>Dónde va cada cosa. Barato: solo rectángulos.</summary>
        void Medir()
        {
            int W = Width, H = Height;
            carrilDe.Clear();
            var voces = T.Voces;
            if (Amplio && voces.Length > 1)
            {
                // un carril por voz (las que más hablan arriba); con más de ocho, la octava junta a «las demás»
                carriles = Math.Min(8, voces.Length);
                for (int i = 0; i < voces.Length; i++) carrilDe[voces[i].Id] = Math.Min(i, carriles - 1);
            }
            else carriles = 1;

            if (Amplio)
            {
                int pad = S(20);
                rEnc = new Rectangle(pad, S(12), W - pad * 2, S(42));
                int altoCarril = S(6), sep = S(3);
                rCinta = new Rectangle(pad, rEnc.Bottom + S(14), W - pad * 2, carriles * altoCarril + (carriles - 1) * sep);
                rEje = new Rectangle(pad, rCinta.Bottom + S(4), W - pad * 2, S(13));
                int top = rEje.Bottom + S(14);
                int lateral = T.ConVoces ? Math.Min(S(290), Math.Max(S(220), W / 5)) : 0;
                rLateral = T.ConVoces ? new Rectangle(W - pad - lateral, top, lateral, H - top - S(14)) : Rectangle.Empty;
                int derecha = T.ConVoces ? rLateral.X - S(24) : W - pad;
                rCuerpo = new Rectangle(pad, top, Math.Max(S(120), derecha - pad), Math.Max(S(40), H - top - S(14)));
                rLeyenda = Rectangle.Empty;
                gutter = T.ConTiempos ? S(64) : S(18);
            }
            else
            {
                int pad = S(12);
                rEnc = new Rectangle(pad, S(7), W - pad * 2, S(18));
                rCinta = new Rectangle(pad, rEnc.Bottom + S(11), W - pad * 2, S(6));   // arriba van el cabezal y las marcas
                rEje = Rectangle.Empty;
                rLeyenda = T.ConVoces ? new Rectangle(pad - S(4), rCinta.Bottom + S(7), W - pad * 2 + S(4), S(18)) : Rectangle.Empty;
                rLateral = Rectangle.Empty;
                int top = (T.ConVoces ? rLeyenda.Bottom : rCinta.Bottom) + S(8);
                rCuerpo = new Rectangle(pad - S(4), top, W - pad * 2 + S(4), Math.Max(S(24), H - top - S(8)));
                gutter = T.ConTiempos ? S(44) : S(12);
            }
            rBarra = new Rectangle(rCuerpo.Right - S(5), rCuerpo.Y, S(5), rCuerpo.Height);
        }

        /// <summary>
        /// Arma los bloques (un turno = un bloque) para el ancho actual. Sin medir texto: columnas = ancho / avance.
        /// O(caracteres). Se rehace solo cuando cambia el ancho, el modo o los nombres.
        /// </summary>
        void Reacomodar()
        {
            if (!IsHandleCreated && Width <= 0) return;
            esc = Dpi.Escala(this);
            Medir();
            avance = Math.Max(1f, Prosa.Avance(fTexto));
            lineH = (int)Math.Ceiling(Prosa.Alto(fTexto) * (Amplio ? 1.34 : 1.2));
            gapParrafo = Math.Max(S(3), lineH / 2);
            gapBloque = Amplio ? S(16) : S(7);
            cabeceraAmplio = Amplio ? Math.Max(S(24), Prosa.Alto(fNombre) + S(8)) : 0;
            bloques.Clear();
            altoTotal = 0;
            cintaClave = "";
            anchoArmado = Width; armadoAmplio = Amplio;
            if (estado != Estado.Lista) return;
            int anchoTexto = rCuerpo.Width - gutter - S(Amplio ? 16 : 12) - rBarra.Width;
            if (Amplio) anchoTexto = Math.Min(anchoTexto, S(1000));
            int columnas = Math.Max(12, (int)(anchoTexto / avance));
            int y = S(2);
            foreach (var t in T.Turnos)
            {
                var b = new Bloque { Turno = t.Indice, Y = y };
                var v = T.VozDe(t.Quien);
                if (!Amplio && v != null)
                {
                    // compacto: el nombre abre el primer renglón, y si es demasiado largo va en su propio renglón
                    var sug = !v.Renombrada ? SugerenciaDe(v.Id) : null;
                    b.Sugerida = sug != null ? " ¿" + sug.Persona.Split(' ')[0] + "?" : "";
                    b.Prefijo = v.Nombre + b.Sugerida + "  ";
                    if (b.Prefijo.Length > columnas / 2) { b.Cabecera = lineH; b.Prefijo = ""; }
                    else b.Sangria = b.Prefijo.Length;
                }
                if (Amplio && v != null) b.Cabecera = cabeceraAmplio;
                b.Renglones = Prosa.Envolver(t.Texto, columnas, b.Sangria);
                b.YRenglon = new int[b.Renglones.Count];
                int yy = b.Cabecera;
                for (int i = 0; i < b.Renglones.Count; i++)
                {
                    b.YRenglon[i] = yy;
                    yy += lineH + (b.Renglones[i].FinDeParrafo ? gapParrafo : 0);
                }
                b.Alto = Math.Max(yy, lineH) + gapBloque;
                bloques.Add(b);
                y += b.Alto;
            }
            altoTotal = y;
            objetivo = Limitar(objetivo);
            desplaz = Limitar(desplaz);
        }

        double MaxDesplaz => Math.Max(0, altoTotal - rCuerpo.Height + S(10));
        double Limitar(double v) => Math.Max(0, Math.Min(MaxDesplaz, v));

        /// <summary>El primer bloque que asoma a partir de y (en coordenadas del contenido). Búsqueda binaria: O(log n).</summary>
        int BloqueEn(double y)
        {
            int lo = 0, hi = bloques.Count - 1, res = -1;             // −1: y cae después del último bloque
            while (lo <= hi)
            {
                int med = (lo + hi) >> 1;
                if (bloques[med].Y + bloques[med].Alto <= y) lo = med + 1; else { res = med; hi = med - 1; }
            }
            return res;
        }

        int RenglonDe(Bloque b, int car)
        {
            int lo = 0, hi = b.Renglones.Count - 1, res = 0;
            while (lo <= hi)
            {
                int med = (lo + hi) >> 1;
                if (b.Renglones[med].Ini <= car) { res = med; lo = med + 1; } else hi = med - 1;
            }
            return res;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width != anchoArmado || Amplio != armadoAmplio) Reacomodar();
            else { Medir(); objetivo = Limitar(objetivo); desplaz = Limitar(desplaz); }   // más alto: menos para desplazar
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            esc = Dpi.Escala(this);
            Reacomodar();
        }

        // ================================================================== pintura

        /// <summary>
        /// La pintura es una FUNCIÓN del estado (acá no se mueve nada: eso lo hace <see cref="Cuadro"/>) y pinta solo las
        /// secciones que tocan el área a repintar: en un cuadro de karaoke eso es el turno que suena, el cabezal y la hora.
        /// </summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            var gr = e.Graphics;
            gr.SmoothingMode = SmoothingMode.AntiAlias;
            var area = e.ClipRectangle;
            if (Width != anchoArmado || Amplio != armadoAmplio) { Reacomodar(); area = ClientRectangle; }
            areaPintura = area;
            bool Toca(Rectangle r) => r.Width > 0 && r.Height > 0 && r.IntersectsWith(area);
            // el fondo: lejos de los bordes basta un rectángulo liso (la tarjeta redondeada entera se rasteriza toda cada vez)
            if (Rectangle.Inflate(ClientRectangle, -S(14), -S(14)).Contains(area))
                using (var b = new SolidBrush(Tema.Panel)) gr.FillRectangle(b, area);
            else
            {
                gr.Clear(BackColor);
                Tema.Tarjeta_(gr, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), S(10), Tema.Panel, Amplio ? Tema.Borde : Tema.Panel);
            }

            // karaoke: dónde va el audio (o la posición simulada de una foto)
            double? pos = PosicionAudio();
            int sonando = pos.HasValue && estado == Estado.Lista ? T.TurnoSonando(pos.Value) : -1;

            if (Toca(rEnc)) { Seccion("enc"); PintarEncabezado(gr, pos); }
            if (estado != Estado.Lista && !(estado == Estado.Cargando && bloques.Count > 0))
            {
                zonas.RemoveAll(z => z.Seccion != "enc");            // lo de la grabación anterior ya no se puede cliquear
                if (estado == Estado.SinGrabacion) PintarVacio(gr, "elegí una grabación de la lista para leerla acá");
                else if (estado == Estado.SinTexto) PintarSinTexto(gr);
                else PintarEsqueleto(gr, rCuerpo);
                return;
            }
            if (Toca(ZonaCinta)) { Seccion("cinta"); PintarCinta(gr, pos); }
            if (Amplio) { if (Toca(rLateral)) { Seccion("lat"); PintarLateral(gr); } }
            else if (Toca(rLeyenda)) { Seccion("ley"); PintarLeyenda(gr); }
            if (Toca(rCuerpo))
            {
                Seccion("cuerpo");
                PintarCuerpo(gr, pos, sonando, area);
                if (pos.HasValue && !siguiendo && sonando >= 0) PintarVolver(gr, sonando);
            }
            if (Toca(rBarra)) { Seccion("barra"); PintarBarra(gr, pos); }
            PintarEtiquetaCinta(gr);
            PintarGlobo(gr);
        }

        void Seccion(string s) { seccion = s; zonas.RemoveAll(z => z.Seccion == s); }

        Rectangle areaPintura;

        /// <summary>
        /// Tema.Texto_, pero salteando lo que no toca el área a repintar: un texto de GDI cuesta ~0,4 ms en esta máquina
        /// (medido), así que en un cuadro de karaoke no se dibuja nada que no haya cambiado.
        /// </summary>
        void Txt(Graphics gr, string s, Font f, Color c, Rectangle r, TextFormatFlags banderas = TextFormatFlags.Left | TextFormatFlags.VerticalCenter)
        {
            if (r.IntersectsWith(areaPintura)) Tema.Texto_(gr, s, f, c, r, banderas);
        }

        /// <summary>Prosa.Linea, salteando lo que no toca el área a repintar.</summary>
        void Linea(Graphics gr, string s, Font f, int x, int y, Color c)
        {
            if (y <= areaPintura.Bottom && y + Prosa.Alto(f) >= areaPintura.Top) Prosa.Linea(gr, s, f, x, y, c);   // (la única llamada directa)
        }

        /// <summary>Todo lo que dibuja la cinta: las apariciones de arriba, los carriles, el eje y la etiqueta del mouse.</summary>
        /// 🚨 Nunca Rectangle.Union con Rectangle.Empty: el vacío está en (0,0) y la unión se estira hasta la esquina (en
        ///    compacto no hay eje, y la copia OPACA de la cinta tapaba el encabezado entero).
        Rectangle ZonaCinta
        {
            get
            {
                if (rCinta.Width <= 0) return Rectangle.Empty;
                int arriba = Math.Max(rEnc.Bottom + 1, rCinta.Y - S(10));
                var z = Rectangle.FromLTRB(rCinta.X - S(4), arriba, rCinta.Right + S(4), rCinta.Bottom + S(24));
                return rEje.Width > 0 ? Rectangle.Union(z, Rectangle.Inflate(rEje, 0, S(4))) : z;
            }
        }

        /// <summary>
        /// Un cuadro del reloj propio (30 por segundo, SOLO mientras algo se mueve). El estado se mueve ACÁ, y se invalida
        /// lo mínimo: desplazamiento suave → cuerpo, cinta y barra; karaoke → el turno que suena (y el que dejó de sonar),
        /// la cinta y el encabezado; cursor de búsqueda → el encabezado, dos veces por segundo; carga → todo (casi vacío).
        /// </summary>
        void Cuadro()
        {
            double dt = Math.Min(0.1, (DateTime.Now - ultimoCuadro).TotalSeconds);
            ultimoCuadro = DateTime.Now;
            bool sigue = false;
            if (estado == Estado.Cargando || (estado == Estado.SinTexto && EnCurso)) { Invalidate(); sigue = true; }
            double? pos = PosicionAudio();
            bool karaoke = pos.HasValue && estado == Estado.Lista && (posicionSimulada.HasValue || FaseSonido == Reproductor.Fase.Sonando || FaseSonido == Reproductor.Fase.Abriendo);
            if (karaoke && siguiendo) Seguir(pos.Value);
            // 🚨 cada parte se repinta SOLA (Invalidate + Update): si se invalidan varias juntas, Windows le pasa a OnPaint
            //    el rectángulo que las ENVUELVE a todas (del encabezado al turno que suena, a lo ancho) y se repinta casi todo
            // el desplazamiento persigue al objetivo con un suavizado exponencial (no salta, no rebota)
            if (Math.Abs(objetivo - desplaz) > 0.5)
            {
                desplaz += (objetivo - desplaz) * Math.Min(1, dt * 16);
                if (Math.Abs(objetivo - desplaz) <= 0.5) desplaz = objetivo;
                Repintar(Rectangle.Union(rCuerpo, rBarra));
                Repintar(ZonaCinta);
                sigue = true;
            }
            if (karaoke)
            {
                // lo que cambia en un cuadro es MUY poco: el renglón donde va el cursor, la hora una vez por segundo y el
                // cabezal de la cinta cuando se corre un píxel. Todo lo demás ya está pintado y no se toca.
                int k = T.TurnoSonando(pos.Value);
                if (k != ultimoSonando)
                {
                    InvalidarBloque(ultimoSonando); Update();
                    InvalidarBloque(k); Update();
                    Repintar(rBarra);
                    ultimoSonando = k; ultimoRenglon = -1;
                }
                else if (k >= 0 && k < bloques.Count)
                {
                    int ren = bloques[k].Renglones.Count > 0 ? RenglonDe(bloques[k], T.Turnos[k].CaracterEn(pos.Value)) : 0;
                    if (ren != ultimoRenglon && ultimoRenglon >= 0) RepintarRenglon(k, ultimoRenglon);
                    RepintarRenglon(k, ren);
                    ultimoRenglon = ren;
                }
                int seg = (int)Math.Floor(pos.Value);
                if (seg != ultimoSegundo) { ultimoSegundo = seg; Repintar(Rectangle.Inflate(rTiempo, S(2), 0)); }
                int xc = XCabezal(pos.Value);
                if (xc != ultimoXCabezal) { if (ultimoXCabezal != int.MinValue) Repintar(FranjaCabezal(ultimoXCabezal)); Repintar(FranjaCabezal(xc)); ultimoXCabezal = xc; }
                sigue = true;
            }
            else if (ultimoSonando >= 0)
            {
                InvalidarBloque(ultimoSonando); Update();
                ultimoSonando = -1; ultimoRenglon = -1; ultimoSegundo = -1; ultimoXCabezal = int.MinValue;
                Repintar(Rectangle.Union(ZonaCinta, rEnc));
            }
            if (buscando && Focused)
            {
                if ((DateTime.Now - ultimoParpadeo).TotalMilliseconds >= MsParpadeo) { ultimoParpadeo = DateTime.Now; Repintar(rEnc); }
                sigue = true;
            }
            if (!sigue) reloj.Stop();
        }

        void Repintar(Rectangle r) { if (r.Width > 0 && r.Height > 0) { Invalidate(r); Update(); } }

        /// <summary>Repinta UN renglón de un turno (con aire para el brillo del cursor). O(1).</summary>
        void RepintarRenglon(int k, int ren)
        {
            if (k < 0 || k >= bloques.Count || ren < 0 || ren >= bloques[k].YRenglon.Length) return;
            var b = bloques[k];
            var r = new Rectangle(rCuerpo.X, rCuerpo.Y + b.Y + b.YRenglon[ren] - (int)Math.Round(desplaz) - S(2), rCuerpo.Width - rBarra.Width, lineH + S(4));
            r.Intersect(rCuerpo);
            Repintar(r);
        }

        int XCabezal(double seg) => T.Duracion > 0 ? rCinta.X + (int)Math.Round(Math.Max(0, Math.Min(T.Duracion, seg)) / T.Duracion * rCinta.Width) : rCinta.X;

        /// <summary>La franjita de la cinta donde está el cabezal (la raya y el punto de arriba).</summary>
        Rectangle FranjaCabezal(int x) => new Rectangle(x - S(6), rCinta.Y - S(10), S(12), rCinta.Height + S(15));

        bool EnCurso => g != null && (EstadoGrab.EnCurso(g.Estado) || EstadoGrab.EnCola(g.Estado));

        /// <summary>Invalida el rectángulo en pantalla de un turno (con el aire de su fondo resaltado). O(1).</summary>
        void InvalidarBloque(int k)
        {
            if (k < 0 || k >= bloques.Count) return;
            var b = bloques[k];
            var r = new Rectangle(rCuerpo.X, rCuerpo.Y + b.Y - (int)Math.Round(desplaz) - S(5), rCuerpo.Width, b.Alto + S(10));
            r.Intersect(rCuerpo);
            if (r.Height > 0) Invalidate(r);
        }

        /// <summary>
        /// Las partes que se repintan (cada una sola) en el PEOR cuadro de karaoke: el renglón del cursor, más la hora y el
        /// cabezal (que en realidad cambian una vez por segundo). Para medirlo en las pruebas.
        /// </summary>
        internal Rectangle[] RegionKaraoke()
        {
            var partes = new List<Rectangle>();
            double? pos = PosicionAudio();
            int k = pos.HasValue ? T.TurnoSonando(pos.Value) : -1;
            if (k >= 0 && k < bloques.Count && bloques[k].Renglones.Count > 0)
            {
                var b = bloques[k];
                int ren = RenglonDe(b, T.Turnos[k].CaracterEn(pos.Value));
                var r = new Rectangle(rCuerpo.X, rCuerpo.Y + b.Y + b.YRenglon[ren] - (int)Math.Round(desplaz) - S(2), rCuerpo.Width - rBarra.Width, lineH + S(4));
                r.Intersect(rCuerpo);
                partes.Add(r);
            }
            if (rTiempo.Width > 0) partes.Add(Rectangle.Inflate(rTiempo, S(2), 0));
            if (pos.HasValue) partes.Add(FranjaCabezal(XCabezal(pos.Value)));
            return partes.ToArray();
        }

        /// <summary>Pinta como lo haría WinForms con esa área inválida (las pruebas miden así un cuadro parcial).</summary>
        internal void PintarEn(Graphics g, Rectangle area)
        {
            var clip = g.Clip;
            g.SetClip(area);
            OnPaint(new PaintEventArgs(g, area));
            g.Clip = clip;
        }

        // ---------------------------------------------------------------- encabezado

        void PintarEncabezado(Graphics gr, double? pos)
        {
            if (Amplio) { PintarEncabezadoAmplio(gr, pos); return; }
            var r = rEnc;
            int x = r.X;
            int iconos = S(20);
            int derecha = r.Right;
            // de derecha a izquierda: ampliar · sonar · (tiempo y velocidad) · buscar
            derecha -= iconos; Icono(gr, new Rectangle(derecha, r.Y - S(1), iconos, iconos), "ampliar", "leer en grande", Tema.TextoSuave);
            if (paq.Audio.Length > 0 && estado == Estado.Lista)
            {
                derecha -= iconos + S(4);
                bool suena = FaseSonido == Reproductor.Fase.Sonando || FaseSonido == Reproductor.Fase.Abriendo;
                Icono(gr, new Rectangle(derecha, r.Y - S(1), iconos, iconos), suena ? "pausa" : "sonar", suena ? "pausar · Espacio" : "escuchar desde lo que se ve · Espacio", suena ? Tema.Rosa : Tema.Cyan);
                if (Sonando || posicionSimulada.HasValue)
                {
                    string vel = Velocidad();
                    int wv = (int)(vel.Length * Prosa.Avance(fMeta)) + S(10);
                    derecha -= wv + S(4);
                    var rv = new Rectangle(derecha, r.Y, wv, S(16));
                    Pildora(gr, rv, vel, Tema.Malva, zonaHover?.Que == "velocidad");
                    zonas.Add(new Zona { Seccion = seccion, R = rv, Que = "velocidad", Ayuda = "velocidad (clic: más rápido · derecho: más lento)" });
                    string tiempo = Transcripcion.Reloj(pos ?? 0) + " / " + Transcripcion.Reloj(T.Duracion);
                    int wt = (int)(tiempo.Length * Prosa.Avance(fHora)) + S(6);
                    derecha -= wt + S(4);
                    rTiempo = new Rectangle(derecha, r.Y, wt, S(16));
                    Txt(gr, tiempo, fHora, Tema.TextoSuave, rTiempo, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }
            }
            if (estado == Estado.Lista)
            {
                derecha -= iconos + S(4);
                Icono(gr, new Rectangle(derecha, r.Y - S(1), iconos, iconos), "buscar", "buscar · escribí o Ctrl+F", buscando ? Tema.Crema : Tema.TextoSuave);
            }

            if (buscando || consulta.Length > 0)
            {
                PintarCampoBusqueda(gr, new Rectangle(x - S(2), r.Y - S(1), derecha - x - S(6), S(20)));
                return;
            }
            int pd = S(5);
            using (var b = new SolidBrush(Tema.Malva)) gr.FillEllipse(b, x, r.Y + (r.Height - pd) / 2f, pd, pd);
            x += pd + S(7);
            string eti = "TRANSCRIPCIÓN";
            var fEti = Tema.Media(7.5f);
            int wEti = Tema.Medir(gr, eti, fEti).Width;
            Txt(gr, eti, fEti, Tema.Apagado, new Rectangle(x, r.Y, wEti + S(2), r.Height));
            x += wEti + S(10);
            string meta = zonaHover != null && zonaHover.Ayuda.Length > 0 ? zonaHover.Ayuda : Meta(false);
            Txt(gr, meta, Tema.Fina(7.5f), zonaHover != null && zonaHover.Ayuda.Length > 0 ? Tema.TextoSuave : Tema.MuyApagado,
                        new Rectangle(x, r.Y, Math.Max(S(10), derecha - x - S(8)), r.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        void PintarEncabezadoAmplio(Graphics gr, double? pos)
        {
            var r = rEnc;
            int iconos = S(26);
            int derecha = r.Right;
            derecha -= iconos;
            Icono(gr, new Rectangle(derecha, r.Y + (r.Height - iconos) / 2, iconos, iconos), "cerrar", "volver a la pantalla de llamadas · Esc", Tema.TextoSuave);
            // el reproductor
            if (paq.Audio.Length > 0 && estado == Estado.Lista)
            {
                bool suena = FaseSonido == Reproductor.Fase.Sonando || FaseSonido == Reproductor.Fase.Abriendo;
                string vel = Velocidad();
                int wv = (int)(vel.Length * Prosa.Avance(fMeta)) + S(14);
                derecha -= wv + S(18);
                var rv = new Rectangle(derecha, r.Y + (r.Height - S(20)) / 2, wv, S(20));
                Pildora(gr, rv, vel, Tema.Malva, zonaHover?.Que == "velocidad");
                zonas.Add(new Zona { Seccion = seccion, R = rv, Que = "velocidad", Ayuda = "velocidad: clic más rápido · clic derecho más lento" });
                string tiempo = Transcripcion.Reloj(pos ?? 0) + " / " + Transcripcion.Reloj(T.Duracion);
                int wt = (int)(tiempo.Length * Prosa.Avance(fHora)) + S(8);
                derecha -= wt + S(8);
                rTiempo = new Rectangle(derecha, r.Y, wt, r.Height);
                Txt(gr, tiempo, fHora, pos.HasValue ? Tema.Texto : Tema.Apagado, rTiempo, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                derecha -= iconos + S(6);
                Icono(gr, new Rectangle(derecha, r.Y + (r.Height - iconos) / 2, iconos, iconos), suena ? "pausa" : "sonar", suena ? "pausar · Espacio" : "escuchar desde lo que se ve · Espacio", suena ? Tema.Rosa : Tema.Cyan);
            }
            // la búsqueda, siempre a la vista en grande
            int wb = Math.Min(S(300), Math.Max(S(160), (derecha - r.X) / 3));
            derecha -= wb + S(18);
            if (estado == Estado.Lista) PintarCampoBusqueda(gr, new Rectangle(derecha, r.Y + (r.Height - S(26)) / 2, wb, S(26)));

            // título y datos
            string titulo = g?.Reunion ?? "";
            Txt(gr, titulo, Tema.Fina(15f), Tema.Texto, new Rectangle(r.X, r.Y, Math.Max(S(40), derecha - r.X - S(16)), S(26)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            string meta = zonaHover != null && zonaHover.Ayuda.Length > 0 ? zonaHover.Ayuda : Meta(true);
            Txt(gr, meta, Tema.Fina(8.5f), zonaHover != null && zonaHover.Ayuda.Length > 0 ? Tema.TextoSuave : Tema.Apagado,
                        new Rectangle(r.X, r.Y + S(26), Math.Max(S(40), derecha - r.X - S(16)), S(16)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        string Meta(bool largo)
        {
            if (estado == Estado.Cargando) return "leyendo…";
            if (estado != Estado.Lista) return "";
            var partes = new List<string>();
            if (largo && g != null) partes.Add(g.Desde.ToString("dd/MM HH:mm"));
            if (largo && T.Duracion > 0) partes.Add(Grabador.Fmt(T.Duracion));
            if (T.ConVoces) partes.Add(T.Voces.Length == 1 ? "1 voz" : T.Voces.Length + " voces");
            partes.Add(T.Turnos.Length.ToString("N0") + (T.ConVoces ? " turnos" : " bloques"));
            partes.Add(T.Palabras.ToString("N0") + (largo ? " palabras" : " pal."));
            int pend = SugerenciasPendientes().Count;
            if (pend > 0) partes.Add(pend == 1 ? "1 nombre sugerido" : pend + " nombres sugeridos");
            return string.Join(" · ", partes);
        }

        void PintarCampoBusqueda(Graphics gr, Rectangle r)
        {
            bool activo = buscando && Focused;
            Tema.Tarjeta_(gr, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(5),
                          Tema.Mezcla(Tema.Panel, Tema.Texto, activo ? 0.06f : 0.035f), activo ? Tema.Alpha(Tema.Crema, 120) : Tema.Alpha(Tema.TextoSuave, 36));
            zonas.Add(new Zona { Seccion = seccion, R = r, Que = "campo", Ayuda = "buscar en la reunión · Enter la próxima · Esc limpia" });
            int d = Math.Min(r.Height - S(6), S(14));
            IconoLupa(gr, new Rectangle(r.X + S(6), r.Y + (r.Height - d) / 2, d, d), consulta.Length > 0 ? Tema.Crema : Tema.Apagado);
            int x = r.X + S(8) + d + S(4);
            // a la derecha: «3 de 12» y las flechas
            int derecha = r.Right - S(4);
            if (consulta.Length > 0)
            {
                int ic = r.Height - S(4);
                derecha -= ic; Icono(gr, new Rectangle(derecha, r.Y + S(2), ic, ic), "limpiar", "limpiar la búsqueda · Esc", Tema.TextoSuave);
                derecha -= ic; Icono(gr, new Rectangle(derecha, r.Y + S(2), ic, ic), "siguiente", "la próxima · Enter", Tema.TextoSuave);
                derecha -= ic; Icono(gr, new Rectangle(derecha, r.Y + S(2), ic, ic), "anterior", "la anterior · Shift+Enter", Tema.TextoSuave);
                string cuenta = hallazgos.Count == 0 ? "nada" : $"{hallazgoActual + 1} de {hallazgos.Count}";
                int wc = (int)(cuenta.Length * Prosa.Avance(fMeta)) + S(8);
                derecha -= wc;
                Txt(gr, cuenta, fMeta, hallazgos.Count == 0 ? Tema.Rosa : Tema.Crema, new Rectangle(derecha, r.Y, wc, r.Height), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
            var fq = Amplio ? Tema.Fina(9.5f) : Tema.Fina(8.5f);
            string q = consulta.Length > 0 ? consulta : activo ? "" : "buscar…";
            var rq = new Rectangle(x, r.Y, Math.Max(S(10), derecha - x - S(6)), r.Height);
            Txt(gr, q, fq, consulta.Length > 0 ? Tema.Texto : Tema.MuyApagado, rq, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (activo && ((int)(Animacion.T * 1.9)) % 2 == 0)
            {
                int xc = x + Math.Min(rq.Width, (int)(consulta.Length * Prosa.Avance(fq))) + S(1);
                using (var p = new Pen(Tema.Crema, Math.Max(1, S(1)))) gr.DrawLine(p, xc, r.Y + S(5), xc, r.Bottom - S(5));
            }
        }

        // ---------------------------------------------------------------- la cinta: quién habló cuándo

        void PintarCinta(Graphics gr, double? pos)
        {
            var r = rCinta;
            double dur = T.Duracion;
            if (!T.ConTiempos || dur <= 0 || r.Width < S(20)) return;
            float X(double s) => r.X + (float)(Math.Max(0, Math.Min(dur, s)) / dur * r.Width);
            // lo QUIETO (carriles, turnos, apariciones, eje) vive en un bitmap que se rearma solo cuando cambia: un cuadro de
            // karaoke lo copia de un saque en vez de volver a pintar cientos de rectángulos
            var zona = ZonaCinta;
            string clave = $"{zona}|{carriles}|{filtro}|{consulta}|{hallazgos.Count}|{hallazgoActual}|{Amplio}";
            if (cintaCache == null || clave != cintaClave)
            {
                cintaCache?.Dispose();
                // opaco (fondo de la tarjeta): se copia tal cual, sin mezclar alfa píxel por píxel
                cintaCache = new Bitmap(Math.Max(1, zona.Width), Math.Max(1, zona.Height), System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                using (var gc = Graphics.FromImage(cintaCache))
                {
                    gc.Clear(Tema.Panel);
                    gc.SmoothingMode = SmoothingMode.AntiAlias;
                    gc.TranslateTransform(-zona.X, -zona.Y);       // se dibuja con las coordenadas de siempre
                    CintaQuieta(gc, r, dur);
                }
                cintaClave = clave;
                rCintaCache = zona;
            }
            var modo = gr.CompositingMode;
            gr.CompositingMode = CompositingMode.SourceCopy;
            gr.DrawImageUnscaled(cintaCache, rCintaCache.X, rCintaCache.Y);
            gr.CompositingMode = modo;
            // lo que se ve ahora en el texto
            if (bloques.Count > 0)
            {
                int a = BloqueEn(desplaz), z = BloqueEn(desplaz + rCuerpo.Height);
                if (z < 0) z = bloques.Count - 1;
                if (a >= 0)
                {
                    double ta = T.Turnos[bloques[a].Turno].Ini, tb = T.Turnos[bloques[z].Turno].Fin;
                    var rv = new RectangleF(X(ta) - S(2), r.Y - S(3), Math.Max(S(4), X(tb) - X(ta) + S(4)), r.Height + S(6));
                    using (var p = Tema.Redondeado(rv, S(3)))
                    using (var b = new SolidBrush(Tema.Alpha(Tema.Texto, 16)))
                    using (var pen = new Pen(Tema.Alpha(Tema.Texto, 70), 1f))
                    { gr.FillPath(b, p); gr.DrawPath(pen, p); }
                }
            }
            // el cabezal del audio
            if (pos.HasValue)
            {
                float x = X(pos.Value);
                using (var p = new Pen(Tema.Texto, Math.Max(1.5f, S(2)))) gr.DrawLine(p, x, r.Y - S(4), x, r.Bottom + S(4));
                using (var b = new SolidBrush(Tema.Texto)) gr.FillEllipse(b, x - S(3), r.Y - S(7), S(6), S(6));
            }
            // el pelo del mouse (la etiqueta con la hora va al final de OnPaint: encima de la leyenda)
            if (enCinta)
                using (var p = new Pen(Tema.Alpha(Tema.Texto, 140), 1f)) gr.DrawLine(p, raton.X, r.Y - S(4), raton.X, r.Bottom + S(4));
            zonas.Add(new Zona { Seccion = seccion, R = new Rectangle(r.X, r.Y - S(6), r.Width, r.Height + S(12)), Que = "cinta", Ayuda = "la reunión entera: clic para ir ahí" + (paq.Audio.Length > 0 ? " (y si está sonando, salta)" : "") });
        }

        /// <summary>La etiqueta del mouse sobre la cinta: la hora y quién hablaba ahí. Última en pintarse: nada la tapa.</summary>
        void PintarEtiquetaCinta(Graphics gr)
        {
            var r = rCinta;
            double dur = T.Duracion;
            if (!enCinta || dur <= 0 || r.Width <= 0) return;
            double s = Math.Max(0, Math.Min(dur, (raton.X - r.X) / (double)r.Width * dur));
            int k = T.TurnoEn(s);
            var v = k >= 0 ? T.VozDe(T.Turnos[k].Quien) : null;
            string eti = Transcripcion.Reloj(s) + (v != null && k >= 0 && s <= T.Turnos[k].Fin + 1 ? " · " + v.Nombre : "");
            int w = (int)(eti.Length * Prosa.Avance(fHora)) + S(12);
            int xl = Math.Max(r.X, Math.Min(r.Right - w, raton.X - w / 2));
            var rl = new Rectangle(xl, Amplio ? rEje.Y - S(1) : r.Bottom + S(5), w, S(16));   // adentro de ZonaCinta: se repinta con ella
            if (!rl.IntersectsWith(areaPintura)) return;
            Tema.Tarjeta_(gr, rl, S(4), Tema.Tarjeta, Tema.Alpha(v?.Color ?? Tema.TextoSuave, 110));
            Txt(gr, eti, fHora, Tema.Texto, rl, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        /// <summary>Lo quieto de la cinta: el fondo de los carriles, cada turno en su carril y color, las apariciones y el eje. O(turnos).</summary>
        void CintaQuieta(Graphics gr, Rectangle r, double dur)
        {
            int altoCarril = carriles > 1 ? (r.Height - (carriles - 1) * S(3)) / carriles : r.Height;
            float X(double s) => r.X + (float)(Math.Max(0, Math.Min(dur, s)) / dur * r.Width);
            using (var b = new SolidBrush(Tema.Alpha(Tema.Texto, 10)))
                for (int c = 0; c < carriles; c++)
                    using (var p = Tema.Redondeado(new RectangleF(r.X, r.Y + c * (altoCarril + S(3)), r.Width, altoCarril), altoCarril / 2f)) gr.FillPath(b, p);
            using (var b = new SolidBrush(Color.White))
                foreach (var t in T.Turnos)
                {
                    var v = T.VozDe(t.Quien);
                    int c = v != null && carrilDe.TryGetValue(v.Id, out var k) ? k : 0;
                    Color col = v?.Color ?? Tema.Malva;
                    bool apagado = filtro.Length > 0 && t.Quien != filtro;
                    b.Color = Tema.Alpha(col, apagado ? 50 : 215);
                    float x0 = X(t.Ini), x1 = X(t.Fin);
                    gr.FillRectangle(b, x0, r.Y + c * (altoCarril + S(3)), Math.Max(1.5f, x1 - x0), altoCarril);
                }
            if (hallazgos.Count > 0)
                using (var b = new SolidBrush(Tema.Crema))
                    for (int i = 0; i < hallazgos.Count; i++)
                    {
                        var h = hallazgos[i];
                        float x = X(T.Turnos[h.Turno].SegundoEn(h.Car));
                        float d = i == hallazgoActual ? S(6) : S(3);
                        b.Color = i == hallazgoActual ? Tema.Crema : Tema.Alpha(Tema.Crema, 170);
                        gr.FillEllipse(b, x - d / 2, r.Y - S(5) - d / 2, d, d);
                    }
            if (Amplio && rEje.Height > 0) PintarEje(gr, dur);
        }

        void PintarEje(Graphics gr, double dur)
        {
            int[] pasos = { 60, 120, 300, 600, 900, 1200, 1800, 3600, 7200 };
            int paso = pasos.FirstOrDefault(p => dur / p <= 8);
            if (paso == 0) paso = 7200;
            for (int s = 0; s <= dur + 1; s += paso)
            {
                float x = rEje.X + (float)(s / dur * rEje.Width);
                using (var p = new Pen(Tema.Alpha(Tema.Texto, 30), 1f)) gr.DrawLine(p, x, rEje.Y - S(2), x, rEje.Y + S(1));
                string eti = Transcripcion.Reloj(s);
                int w = (int)(eti.Length * Prosa.Avance(Tema.Mono(7f))) + S(4);
                int xl = (int)Math.Max(rEje.X, Math.Min(rEje.Right - w, x - (s == 0 ? 0 : w / 2f)));
                // 🚨 se dibuja dentro del bitmap de la cinta, con el Graphics trasladado: TextRenderer ignora la traslación
                //    si no se le pide PreserveGraphicsTranslateTransform, y las horas quedaban corridas fuera del bitmap
                TextRenderer.DrawText(gr, eti, Tema.Mono(7f), new Rectangle(xl, rEje.Y + S(1), w, rEje.Height - S(1)), Tema.MuyApagado,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsTranslateTransform);
            }
        }

        // ---------------------------------------------------------------- quién habló cuánto

        /// <summary>Compacto: una tira de pastillas por voz (las que más hablaron primero) y «+N» si no entran.</summary>
        void PintarLeyenda(Graphics gr)
        {
            if (!T.ConVoces || rLeyenda.Width <= 0) return;
            var r = rLeyenda;
            var f = Tema.Fina(8f);
            float av = Prosa.Avance(f);
            int x = r.X;
            var voces = T.Voces;
            for (int i = 0; i < voces.Length; i++)
            {
                var v = voces[i];
                string nombre = v.Nombre.Length > 18 ? v.Nombre.Substring(0, 17) + "…" : v.Nombre;
                string pct = $"{v.Fraccion * 100:0}%";
                int w = S(20) + (int)((nombre.Length + 1 + pct.Length) * av) + S(8);
                int resto = voces.Length - i - 1;
                if (x + w > r.Right - (resto > 0 ? S(34) : 0))
                {
                    string mas = "+" + (voces.Length - i);
                    Txt(gr, mas, f, Tema.Apagado, new Rectangle(x + S(2), r.Y, S(34), r.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    zonas.Add(new Zona { Seccion = seccion, R = new Rectangle(x, r.Y, S(34), r.Height), Que = "ampliar", Ayuda = "ver todas las voces en grande" });
                    break;
                }
                var rp = new Rectangle(x, r.Y, w, r.Height);
                bool sel = filtro == v.Id, hov = zonaHover?.Que == "voz" && Equals(zonaHover.Dato, v.Id);
                if (sel || hov)
                    Tema.Tarjeta_(gr, new RectangleF(rp.X + 0.5f, rp.Y + 0.5f, rp.Width - 1, rp.Height - 1), S(4),
                                  Tema.Mezcla(Tema.Panel, v.Color, sel ? 0.20f : 0.08f), sel ? Tema.Alpha(v.Color, 150) : Tema.Mezcla(Tema.Panel, v.Color, 0.08f));
                int d = S(6);
                using (var b = new SolidBrush(v.Color)) gr.FillEllipse(b, rp.X + S(7), rp.Y + (rp.Height - d) / 2f, d, d);
                bool apagado = filtro.Length > 0 && !sel;
                Txt(gr, nombre, f, apagado ? Tema.Apagado : Tema.Texto, new Rectangle(rp.X + S(18), rp.Y, (int)(nombre.Length * av) + S(2), rp.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                Txt(gr, pct, f, apagado ? Tema.MuyApagado : Tema.Mezcla(v.Color, Tema.TextoSuave, 0.35f), new Rectangle(rp.X + S(18) + (int)((nombre.Length + 1) * av), rp.Y, (int)(pct.Length * av) + S(4), rp.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                var sug = SugerenciaDe(v.Id);
                string ayuda = $"{v.Nombre}: {Grabador.Fmt(v.Segundos)} en {v.Turnos} turnos · clic resalta · clic derecho le pone nombre" + (sug != null ? $" · ¿será {sug.Persona}?" : "");
                zonas.Add(new Zona { Seccion = seccion, R = rp, Que = "voz", Dato = v.Id, Ayuda = ayuda });
                x += w + S(4);
            }
        }

        /// <summary>En grande: la columna «quién habló», con avatar, cuánto, barra y la sugerencia de nombre del detective.</summary>
        void PintarLateral(Graphics gr)
        {
            if (!T.ConVoces || rLateral.Width <= 0) return;
            var r = rLateral;
            Txt(gr, "QUIÉN HABLÓ", Tema.Media(7.5f), Tema.Apagado, new Rectangle(r.X, r.Y, r.Width, S(14)));
            Txt(gr, T.Voces.Length == 1 ? "1 voz" : T.Voces.Length + " voces", Tema.Fina(7.5f), Tema.MuyApagado, new Rectangle(r.X, r.Y, r.Width, S(14)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            var pend = SugerenciasPendientes();
            int abajo = r.Bottom - (pend.Count > 0 ? S(58) : 0);
            var rl = new Rectangle(r.X, r.Y + S(22), r.Width, Math.Max(S(20), abajo - r.Y - S(22)));
            int fila = S(56);
            double max = Math.Max(0, T.Voces.Length * fila - rl.Height);
            desplazLateral = Math.Max(0, Math.Min(max, desplazLateral));
            var clip = gr.Clip;
            gr.SetClip(rl);
            int y = rl.Y - (int)desplazLateral;
            foreach (var v in T.Voces)
            {
                // una fila partida por el borde no se dibuja: TextRenderer no respeta el recorte y quedaría medio nombre afuera
                if (y < rl.Y) { y += fila; continue; }
                if (y + fila - S(6) > rl.Bottom) break;
                PintarFilaVoz(gr, v, new Rectangle(rl.X, y, rl.Width, fila - S(6)));
                y += fila;
            }
            gr.Clip = clip;
            if (max > 0)
            {
                float frac = rl.Height / (float)(T.Voces.Length * fila);
                float hb = Math.Max(S(16), rl.Height * frac), yb = rl.Y + (float)(desplazLateral / max) * (rl.Height - hb);
                using (var b = new SolidBrush(Tema.Alpha(Tema.Apagado, 90)))
                using (var p = Tema.Redondeado(new RectangleF(rl.Right + S(4), yb, S(3), hb), S(2))) gr.FillPath(b, p);
            }
            zonas.Add(new Zona { Seccion = seccion, R = rl, Que = "lateral" });
            if (pend.Count > 0)
            {
                var rb = new Rectangle(r.X, abajo + S(10), r.Width, S(26));
                bool hov = zonaHover?.Que == "aplicar";
                Tema.Tarjeta_(gr, new RectangleF(rb.X + 0.5f, rb.Y + 0.5f, rb.Width - 1, rb.Height - 1), S(3),
                              Tema.Mezcla(Tema.Panel, Tema.Crema, hov ? 0.16f : 0.08f), Tema.Alpha(Tema.Crema, hov ? 170 : 90));
                using (var b = new SolidBrush(Tema.Crema)) gr.FillRectangle(b, rb.X, rb.Y, S(3), rb.Height);
                string t = pend.Count == 1 ? $"ponerle a {Corto(T.VozDe(pend[0].Voz)?.Nombre, 14)} «{Corto(pend[0].Persona, 18)}»" : $"poner los {pend.Count} nombres sugeridos";
                Txt(gr, t, Tema.Fina(9f), Tema.Crema, new Rectangle(rb.X + S(12), rb.Y, rb.Width - S(16), rb.Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                zonas.Add(new Zona { Seccion = seccion, R = rb, Que = "aplicar", Ayuda = "el detective los sacó de cómo se nombran en la conversación: pasá el mouse por cada «¿…?» para ver por qué" });
                Txt(gr, "salen de cómo se nombran en la charla", Tema.Fina(7.5f), Tema.MuyApagado, new Rectangle(r.X, rb.Bottom + S(3), r.Width, S(14)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        void PintarFilaVoz(Graphics gr, Voz v, Rectangle r)
        {
            bool sel = filtro == v.Id, hov = zonaHover?.Que == "voz" && Equals(zonaHover.Dato, v.Id);
            if (sel || hov)
                Tema.Tarjeta_(gr, new RectangleF(r.X - S(6) + 0.5f, r.Y + 0.5f, r.Width + S(6) - 1, r.Height - 1), S(6),
                              Tema.Mezcla(Tema.Panel, v.Color, sel ? 0.14f : 0.05f), sel ? Tema.Alpha(v.Color, 130) : Tema.Mezcla(Tema.Panel, v.Color, 0.05f));
            int d = S(26);
            Avatar(gr, v, new Rectangle(r.X, r.Y + S(4), d, d), filtro.Length > 0 && !sel);
            int x = r.X + d + S(10);
            string pct = $"{v.Fraccion * 100:0} %";
            var fp = Tema.Fina(11f);
            int wp = (int)(pct.Length * Prosa.Avance(fp)) + S(2);
            Txt(gr, pct, fp, Tema.Texto, new Rectangle(r.Right - wp, r.Y + S(2), wp, S(18)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            Txt(gr, v.Nombre + (v.EsYo ? "  · vos" : ""), Tema.Fina(10f), filtro.Length > 0 && !sel ? Tema.Apagado : Tema.Texto,
                        new Rectangle(x, r.Y + S(2), r.Right - wp - x - S(6), S(18)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            var sug = SugerenciaDe(v.Id);
            var rs = new Rectangle(x, r.Y + S(20), r.Right - x, S(14));
            if (sug != null)
            {
                string s = $"¿{sug.Persona}? · {sug.Confianza:P0}";
                bool hs = zonaHover?.Que == "sugerencia" && Equals(zonaHover.Dato, v.Id);
                Txt(gr, s, Tema.Fina(8f), hs ? Tema.Crema : Tema.Mezcla(Tema.Crema, Tema.Apagado, 0.25f), rs, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                zonas.Add(new Zona { Seccion = seccion, R = rs, Que = "sugerencia", Dato = v.Id, Ayuda = "clic: ponerle «" + sug.Persona + "»" });
            }
            else
                Txt(gr, $"{Grabador.Fmt(v.Segundos)} · {v.Turnos} turnos · {v.Palabras:N0} pal.", Tema.Fina(8f), Tema.Apagado, rs, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            // la barra de cuánto habló
            var rb = new RectangleF(x, r.Y + S(38), r.Right - x, S(3));
            using (var b = new SolidBrush(Tema.Alpha(Tema.Texto, 14))) using (var p = Tema.Redondeado(rb, S(1))) gr.FillPath(b, p);
            double maxF = T.Voces.Length > 0 ? T.Voces.Max(z => z.Fraccion) : 1;
            var rf = new RectangleF(rb.X, rb.Y, Math.Max(S(2), (float)(rb.Width * (maxF > 0 ? v.Fraccion / maxF : 0))), rb.Height);
            using (var b = new SolidBrush(Tema.Alpha(v.Color, filtro.Length > 0 && !sel ? 70 : 220))) using (var p = Tema.Redondeado(rf, S(1))) gr.FillPath(b, p);
            zonas.Add(new Zona { Seccion = seccion, R = new Rectangle(r.X - S(6), r.Y, r.Width + S(6), r.Height), Que = "voz", Dato = v.Id, Ayuda = $"{v.Nombre}: clic la resalta · clic derecho le ponés nombre" });
        }

        void Avatar(Graphics gr, Voz v, Rectangle r, bool apagado)
        {
            Color c = v.Color;
            using (var b = new SolidBrush(Tema.Mezcla(Tema.Panel, c, apagado ? 0.08f : 0.20f))) gr.FillEllipse(b, r);
            using (var p = new Pen(Tema.Alpha(c, apagado ? 60 : 200), Math.Max(1f, S(1)))) gr.DrawEllipse(p, r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
            string ini = v.Iniciales;
            var f = ini.Length >= 3 ? Tema.Media(r.Width >= S(24) ? 6.5f : 5.5f) : Tema.Media(r.Width >= S(24) ? 7.5f : 6.5f);
            // con PreserveGraphicsClipping: un avatar a medio salir del cuerpo no pinta sus letras encima de la cinta
            TextRenderer.DrawText(gr, ini, f, r, apagado ? Tema.Apagado : c,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping);
        }

        // ---------------------------------------------------------------- el cuerpo: los turnos

        /// <summary>Los turnos a la vista: el primero por búsqueda binaria y solo los que tocan el área a repintar. O(log n + visibles).</summary>
        void PintarCuerpo(Graphics gr, double? pos, int sonando, Rectangle area)
        {
            var clip = gr.Clip;
            gr.SetClip(rCuerpo, CombineMode.Intersect);          // el cuerpo, y dentro del cuerpo solo lo que hay que repintar
            if (bloques.Count == 0) { Txt(gr, "sin texto", fTexto, Tema.Apagado, rCuerpo); gr.Clip = clip; return; }
            int primero = BloqueEn(desplaz);
            for (int i = Math.Max(0, primero); i < bloques.Count; i++)
            {
                var b = bloques[i];
                int y = rCuerpo.Y + b.Y - (int)Math.Round(desplaz);
                if (y > rCuerpo.Bottom || y - S(5) > area.Bottom) break;
                if (y + b.Alto + S(5) < area.Top) continue;
                PintarBloque(gr, b, y, i == sonando ? pos : null);
            }
            gr.Clip = clip;
            // un velo arriba y abajo: el texto entra y sale suave en vez de cortarse en seco
            var arriba = new Rectangle(rCuerpo.X, rCuerpo.Y, rCuerpo.Width - rBarra.Width, S(10));
            var abajo = new Rectangle(rCuerpo.X, rCuerpo.Bottom - S(12), rCuerpo.Width - rBarra.Width, S(12));
            if (arriba.IntersectsWith(area)) Velo(gr, arriba, true);
            if (abajo.IntersectsWith(area)) Velo(gr, abajo, false);
        }

        void Velo(Graphics gr, Rectangle r, bool arriba)
        {
            if (r.Height <= 0 || r.Width <= 0) return;
            using (var lg = new LinearGradientBrush(new Rectangle(r.X, r.Y - 1, r.Width, r.Height + 2),
                       arriba ? Tema.Panel : Tema.Alpha(Tema.Panel, 0), arriba ? Tema.Alpha(Tema.Panel, 0) : Tema.Panel, LinearGradientMode.Vertical))
                gr.FillRectangle(lg, r);
        }

        void PintarBloque(Graphics gr, Bloque b, int y, double? pos)
        {
            var t = T.Turnos[b.Turno];
            var v = T.VozDe(t.Quien);
            Color col = v?.Color ?? Tema.Malva;
            bool actual = pos.HasValue;
            bool apagado = filtro.Length > 0 && t.Quien != filtro;
            bool hov = turnoHover == b.Turno && !arrastrandoBarra && !arrastrandoCinta;
            int x0 = rCuerpo.X;
            int alto = b.Alto - gapBloque;
            int xTexto = x0 + gutter + S(Amplio ? 16 : 10);

            if (actual || hov)
            {
                var rf = new RectangleF(x0 + S(2), y - S(3), rCuerpo.Width - rBarra.Width - S(6), alto + S(6));
                using (var p = Tema.Redondeado(rf, S(6)))
                using (var br = new SolidBrush(actual ? Tema.Mezcla(Tema.Panel, col, 0.085f) : Tema.Mezcla(Tema.Panel, col, 0.035f))) gr.FillPath(br, p);
                if (actual) using (var p = Tema.Redondeado(rf, S(6))) using (var pen = new Pen(Tema.Alpha(col, 70), 1f)) gr.DrawPath(pen, p);
            }

            // la hora (clic = escuchar desde acá)
            if (T.ConTiempos)
            {
                string hora = Transcripcion.Reloj(t.Ini);
                bool hh = hov && horaHover && paq.Audio.Length > 0;
                int yh = y + (Amplio && b.Cabecera > 0 ? (b.Cabecera - Prosa.Alto(fHora)) / 2 : (lineH - Prosa.Alto(fHora)) / 2);
                if (hh)
                {
                    IconoPlay(gr, new Rectangle(x0 + S(6), yh + S(1), S(9), Prosa.Alto(fHora) - S(2)), col);
                    Linea(gr, hora, fHora, x0 + S(18), yh, col);
                }
                else Linea(gr, hora, fHora, x0 + S(6), yh, actual ? col : apagado ? Tema.MuyApagado : Tema.Apagado);
            }

            // el riel de la voz: su color de punta a punta del turno
            int xr = xTexto - S(Amplio ? 14 : 8);
            int yr0 = y + (Amplio && b.Cabecera > 0 ? b.Cabecera / 2 + S(10) : S(2));
            using (var br = new SolidBrush(Tema.Alpha(col, apagado ? 40 : actual ? 255 : hov ? 200 : 130)))
                gr.FillRectangle(br, xr, yr0, Math.Max(2, S(2)), Math.Max(S(4), y + alto - yr0 - S(2)));

            // amplio: avatar + nombre + cuánto duró + la sugerencia del detective
            if (Amplio && v != null)
            {
                int d = S(20);
                var ra = new Rectangle(xr + 1 - d / 2, y + (b.Cabecera - d) / 2 - S(1), d, d);
                if (ra.IntersectsWith(areaPintura)) Avatar(gr, v, ra, apagado);
                string nombre = v.Nombre;
                Linea(gr, nombre, fNombre, xTexto, y + (b.Cabecera - Prosa.Alto(fNombre)) / 2 - S(1), apagado ? Tema.Apagado : col);
                int xm = xTexto + (int)(nombre.Length * Prosa.Avance(fNombre)) + S(10);
                string meta = t.Dura >= 1 ? "· " + Grabador.Fmt(t.Dura) : "";
                var sug = !v.Renombrada ? SugerenciaDe(v.Id) : null;
                if (sug != null) meta += (meta.Length > 0 ? "  " : "") + $"¿{sug.Persona}?";
                if (meta.Length > 0)
                    Linea(gr, meta, fMeta, xm, y + (b.Cabecera - Prosa.Alto(fMeta)) / 2, sug != null ? Tema.Mezcla(Tema.Crema, Tema.Apagado, 0.35f) : Tema.MuyApagado);
            }
            else if (b.Cabecera > 0 && v != null)
                Linea(gr, v.Nombre, fNombre, xTexto, y, apagado ? Tema.Apagado : col);

            // el texto, renglón por renglón (lo que no se ve no se pinta)
            int cursor = actual ? t.CaracterEn(pos.Value) : -1;
            hallazgosPorTurno.TryGetValue(t.Indice, out var hs);
            List<Prosa.Marcado> marcas = null;
            if (hs != null)
            {
                marcas = new List<Prosa.Marcado>(hs.Count);
                foreach (var h in hs) marcas.Add(new Prosa.Marcado(h.Car, h.Car + h.Largo, hallazgoActual >= 0 && hallazgos[hallazgoActual].Turno == h.Turno && hallazgos[hallazgoActual].Car == h.Car));
            }
            Color baseCol = apagado ? Tema.Apagado : Tema.Texto;
            Color dicho = Tema.Mezcla(col, Color.White, 0.35f), porDecir = Tema.Mezcla(Tema.TextoSuave, Tema.Apagado, 0.3f);
            for (int i = 0; i < b.Renglones.Count; i++)
            {
                int yl = y + b.YRenglon[i];
                if (yl + lineH < rCuerpo.Y || yl + lineH < areaPintura.Top) continue;
                if (yl > rCuerpo.Bottom || yl > areaPintura.Bottom) break;
                var rg = b.Renglones[i];
                int xl = xTexto;
                if (i == 0 && b.Prefijo.Length > 0)
                {
                    Linea(gr, v?.Nombre ?? "", fNombre, xl, yl, apagado ? Tema.Apagado : col);
                    if (b.Sugerida.Length > 0)
                        Linea(gr, b.Sugerida, fMeta, xl + (int)Math.Round((v?.Nombre.Length ?? 0) * avance), yl + (Prosa.Alto(fNombre) - Prosa.Alto(fMeta)) / 2, Tema.Mezcla(Tema.Crema, Tema.Apagado, 0.35f));
                    xl += (int)Math.Round(b.Sangria * avance);
                }
                var tintas = cursor >= 0 || marcas != null
                    ? Prosa.Componer(rg, baseCol, cursor, dicho, porDecir, marcas, Tema.Crema, Tema.Alpha(Tema.Crema, 48), Tema.Fondo, Tema.Alpha(Tema.Crema, 225))
                    : null;
                Prosa.Dibujar(gr, t.Texto, rg, fTexto, avance, xl, yl, lineH, baseCol, tintas);
                // la cabeza del karaoke: un brillo que late donde va el audio
                if (cursor >= rg.Ini && cursor <= rg.Fin && (i == b.Renglones.Count - 1 || cursor < b.Renglones[i + 1].Ini))
                {
                    float xc = xl + (cursor - rg.Ini) * avance;
                    double lat = (Math.Sin(Animacion.T * 7) + 1) / 2;
                    using (var br = new SolidBrush(Tema.Alpha(col, (int)(90 + 120 * lat)))) gr.FillRectangle(br, xc, yl + S(1), Math.Max(2, S(2)), lineH - S(3));
                }
            }
        }

        // ---------------------------------------------------------------- barra, píldora, globo, vacíos

        void PintarBarra(Graphics gr, double? pos)
        {
            double max = MaxDesplaz;
            if (max <= 0) return;
            var r = rBarra;
            float frac = rCuerpo.Height / (float)Math.Max(1, altoTotal);
            float h = Math.Max(S(22), r.Height * frac), yb = r.Y + (float)(desplaz / max) * (r.Height - h);
            bool vivo = arrastrandoBarra || zonaHover?.Que == "barra";
            using (var b = new SolidBrush(Tema.Alpha(Tema.Apagado, vivo ? 170 : 100)))
            using (var p = Tema.Redondeado(new RectangleF(r.Right - S(4), yb, S(3), h), S(2))) gr.FillPath(b, p);
            // el minimapa de la barra: dónde están las apariciones y dónde va el audio
            float Y(int bloque) => r.Y + (float)(bloques[bloque].Y / (double)Math.Max(1, altoTotal)) * r.Height;
            if (hallazgos.Count > 0)
                using (var b = new SolidBrush(Tema.Alpha(Tema.Crema, 200)))
                    foreach (var hz in hallazgos) gr.FillRectangle(b, r.Right - S(6), Y(hz.Turno), S(5), Math.Max(1, S(2)));
            if (pos.HasValue)
            {
                int k = T.TurnoEn(pos.Value);
                if (k >= 0) using (var b = new SolidBrush(Tema.Texto)) gr.FillEllipse(b, r.Right - S(6), Y(k) - S(2), S(5), S(5));
            }
            zonas.Add(new Zona { Seccion = seccion, R = new Rectangle(r.X - S(3), r.Y, r.Width + S(3), r.Height), Que = "barra" });
        }

        void PintarVolver(Graphics gr, int sonando)
        {
            if (sonando >= bloques.Count) return;
            var b = bloques[sonando];
            if (b.Y + b.Alto >= desplaz && b.Y <= desplaz + rCuerpo.Height) return;     // lo que suena está a la vista
            bool arriba = b.Y + b.Alto < desplaz;
            string t = arriba ? "↑ volver a lo que suena" : "↓ volver a lo que suena";
            var f = Tema.Fina(8.5f);
            int w = (int)(t.Length * Prosa.Avance(f)) + S(22), h = S(22);
            var r = new Rectangle(rCuerpo.X + (rCuerpo.Width - w) / 2, arriba ? rCuerpo.Y + S(6) : rCuerpo.Bottom - h - S(8), w, h);
            bool hov = zonaHover?.Que == "volver";
            Tema.Tarjeta_(gr, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), h / 2f, Tema.Mezcla(Tema.Tarjeta, Tema.Malva, hov ? 0.28f : 0.16f), Tema.Alpha(Tema.Malva, 170));
            Txt(gr, t, f, Tema.Malva, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            zonas.Add(new Zona { Seccion = seccion, R = r, Que = "volver", Ayuda = "que la vista siga al audio otra vez" });
        }

        /// <summary>El porqué de una sugerencia, en una tarjetita que sigue al mouse (texto envuelto con Prosa).</summary>
        void PintarGlobo(Graphics gr)
        {
            if (zonaHover == null || (zonaHover.Que != "sugerencia" && !(zonaHover.Que == "voz" && Amplio))) return;
            var sug = SugerenciaDe(zonaHover.Dato as string);
            if (sug == null || sug.Pistas.Count == 0) return;
            var f = Tema.Fina(8.5f);
            float av = Prosa.Avance(f);
            int ancho = Math.Min(S(380), Width - S(40));
            int cols = Math.Max(20, (int)((ancho - S(24)) / av));
            string texto = $"¿{T.VozDe(sug.Voz)?.Nombre} es {sug.Persona}?\n" + string.Join("\n", sug.Pistas.Where(p => p.Peso > 0).Take(4).Select(p => "• " + p.Porque));
            var renglones = Prosa.Envolver(texto, cols);
            int lh = Prosa.Alto(f) + S(3);
            int alto = renglones.Count * lh + S(16) + renglones.Count(r => r.FinDeParrafo) * S(2);
            int x = Math.Max(S(8), Math.Min(Width - ancho - S(8), puntoGlobo.X - ancho - S(14)));
            int y = Math.Max(S(8), Math.Min(Height - alto - S(8), puntoGlobo.Y + S(12)));
            var rc = new Rectangle(x, y, ancho, alto);
            Tema.Tarjeta_(gr, new RectangleF(rc.X + 0.5f, rc.Y + 0.5f, rc.Width - 1, rc.Height - 1), S(8), Tema.Tarjeta, Tema.Alpha(Tema.Crema, 110));
            int yy = rc.Y + S(8);
            for (int i = 0; i < renglones.Count; i++)
            {
                Prosa.Dibujar(gr, texto, renglones[i], f, av, rc.X + S(12), yy, lh, i == 0 ? Tema.Crema : Tema.TextoSuave, null);
                yy += lh + (renglones[i].FinDeParrafo ? S(2) : 0);
            }
        }

        void PintarVacio(Graphics gr, string t)
        {
            Txt(gr, t, Tema.Fina(9f), Tema.Apagado, new Rectangle(S(14), rEnc.Bottom + S(10), Width - S(28), S(20)));
        }

        void PintarSinTexto(Graphics gr)
        {
            if (g == null) return;
            bool enCurso = EstadoGrab.EnCurso(g.Estado) || EstadoGrab.EnCola(g.Estado);
            string que = g.Estado == EstadoGrab.Fallo ? "la transcripción no salió" : enCurso ? "todavía no hay texto" : "(sin transcripción)";
            string det = g.Estado == EstadoGrab.Fallo ? g.Detalle : VistaLlamadas.TextoEstado(g) + (g.Etapa.Length > 0 ? " · " + g.Etapa : "");
            var r = new Rectangle(S(14), rEnc.Bottom + S(10), Width - S(28), S(18));
            Txt(gr, que, Tema.Fina(10f), g.Estado == EstadoGrab.Fallo ? Tema.Rosa : Tema.TextoSuave, r, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            Txt(gr, det, Tema.Fina(8.5f), Tema.Apagado, new Rectangle(r.X, r.Bottom + S(2), r.Width, S(16)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (enCurso && g.Progreso >= 0)
            {
                var rb = new RectangleF(r.X, r.Bottom + S(24), r.Width, S(4));
                using (var b = new SolidBrush(Tema.Alpha(Tema.Texto, 16))) using (var p = Tema.Redondeado(rb, S(2))) gr.FillPath(b, p);
                var rl = new RectangleF(rb.X, rb.Y, Math.Max(S(4), (float)(rb.Width * Math.Min(1, g.Progreso))), rb.Height);
                using (var b = new SolidBrush(Tema.Alpha(Tema.Malva, 220))) using (var p = Tema.Redondeado(rl, S(2))) gr.FillPath(b, p);
                Brillo.Bloque(gr, rl, S(2), Tema.Alpha(Tema.Malva, 0), Color.White);
            }
            if (enCurso) PintarEsqueleto(gr, new Rectangle(r.X, r.Bottom + S(40), r.Width, Height - r.Bottom - S(52)));
        }

        /// <summary>Renglones fantasma con destello mientras llega el texto: se lee «viene», no «no hay».</summary>
        void PintarEsqueleto(Graphics gr, Rectangle area)
        {
            if (area.Height < S(20)) return;
            int filas = Math.Min(12, area.Height / S(20));
            for (int i = 0; i < filas; i++)
            {
                double k = 0.45 + 0.5 * ((Math.Sin(i * 12.9898) * 43758.5453 % 1 + 1) % 1);
                int y = area.Y + i * S(20);
                Brillo.Bloque(gr, new RectangleF(area.X, y + S(5), S(34), S(6)), S(3), Tema.Panel, Tema.Apagado, i * 0.07);
                Brillo.Bloque(gr, new RectangleF(area.X + S(48), y + S(5), (float)((area.Width - S(56)) * k), S(6)), S(3), Tema.Panel, PaletaVoces.De(new Voz { Numero = 1 + i % 5 }), i * 0.07);
            }
        }

        // ---------------------------------------------------------------- iconos

        void Icono(Graphics gr, Rectangle r, string que, string ayuda, Color c)
        {
            bool hov = zonaHover != null && zonaHover.Que == que && zonaHover.R == r;
            if (hov) using (var p = Tema.Redondeado(new RectangleF(r.X, r.Y, r.Width, r.Height), S(4))) using (var b = new SolidBrush(Tema.Alpha(Tema.Texto, 16))) gr.FillPath(b, p);
            var ri = Rectangle.Inflate(r, -r.Width / 4, -r.Height / 4);
            Color cc = hov ? Tema.Mezcla(c, Color.White, 0.3f) : c;
            switch (que)
            {
                case "buscar": IconoLupa(gr, ri, cc); break;
                case "sonar": IconoPlay(gr, ri, cc); break;
                case "pausa":
                    using (var b = new SolidBrush(cc)) { gr.FillRectangle(b, ri.X + ri.Width * 0.15f, ri.Y, ri.Width * 0.24f, ri.Height); gr.FillRectangle(b, ri.X + ri.Width * 0.61f, ri.Y, ri.Width * 0.24f, ri.Height); }
                    break;
                case "ampliar":
                    using (var p = new Pen(cc, Math.Max(1.2f, S(1) * 1.3f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        float a = ri.Width * 0.38f;
                        gr.DrawLine(p, ri.X, ri.Y, ri.X + a, ri.Y); gr.DrawLine(p, ri.X, ri.Y, ri.X, ri.Y + a);
                        gr.DrawLine(p, ri.Right, ri.Bottom, ri.Right - a, ri.Bottom); gr.DrawLine(p, ri.Right, ri.Bottom, ri.Right, ri.Bottom - a);
                        gr.DrawLine(p, ri.X, ri.Y, ri.X + ri.Width * 0.42f, ri.Y + ri.Height * 0.42f);
                        gr.DrawLine(p, ri.Right, ri.Bottom, ri.Right - ri.Width * 0.42f, ri.Bottom - ri.Height * 0.42f);
                    }
                    break;
                case "cerrar":
                case "limpiar":
                    using (var p = new Pen(cc, Math.Max(1.2f, S(1) * 1.3f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    { gr.DrawLine(p, ri.X, ri.Y, ri.Right, ri.Bottom); gr.DrawLine(p, ri.Right, ri.Y, ri.X, ri.Bottom); }
                    break;
                case "anterior":
                case "siguiente":
                    using (var p = new Pen(cc, Math.Max(1.2f, S(1) * 1.3f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    {
                        float cx = ri.X + ri.Width / 2f, dy = ri.Height * 0.28f, cy = ri.Y + ri.Height / 2f;
                        if (que == "anterior") gr.DrawLines(p, new[] { new PointF(ri.X, cy + dy), new PointF(cx, cy - dy), new PointF(ri.Right, cy + dy) });
                        else gr.DrawLines(p, new[] { new PointF(ri.X, cy - dy), new PointF(cx, cy + dy), new PointF(ri.Right, cy - dy) });
                    }
                    break;
            }
            zonas.Add(new Zona { Seccion = seccion, R = r, Que = que, Ayuda = ayuda });
        }

        static void IconoLupa(Graphics gr, Rectangle r, Color c)
        {
            float d = r.Width * 0.68f;
            using (var p = new Pen(c, Math.Max(1.2f, r.Width / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                gr.DrawEllipse(p, r.X, r.Y, d, d);
                gr.DrawLine(p, r.X + d * 0.86f, r.Y + d * 0.86f, r.Right, r.Bottom);
            }
        }

        static void IconoPlay(Graphics gr, Rectangle r, Color c)
        {
            using (var b = new SolidBrush(c))
                gr.FillPolygon(b, new[] { new PointF(r.X + r.Width * 0.12f, r.Y), new PointF(r.Right, r.Y + r.Height / 2f), new PointF(r.X + r.Width * 0.12f, r.Bottom) });
        }

        void Pildora(Graphics gr, Rectangle r, string t, Color c, bool hov)
        {
            Tema.Tarjeta_(gr, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), r.Height / 2f, Tema.Mezcla(Tema.Panel, c, hov ? 0.22f : 0.10f), Tema.Alpha(c, hov ? 160 : 90));
            Txt(gr, t, fMeta, c, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // ================================================================== sonido

        double? PosicionAudio()
        {
            if (posicionSimulada.HasValue)
            {
                if (DateTime.Now <= simuladaHasta) return posicionSimulada;
                posicionSimulada = null;
            }
            if (rep == null) return null;
            var f = rep.Estado;
            return f == Reproductor.Fase.Sonando || f == Reproductor.Fase.Pausa || f == Reproductor.Fase.Abriendo ? rep.Posicion : (double?)null;
        }

        string Velocidad()
        {
            double v = rep?.Velocidad ?? 1.0;
            return (Math.Abs(v - Math.Round(v)) < 0.01 ? v.ToString("0") : v.ToString("0.##")) + "×";
        }

        Reproductor Rep()
        {
            if (rep != null) return rep;
            rep = new Reproductor();
            rep.Cambio += () => Fondo.EnUi(this, () =>
            {
                if (rep.Estado == Reproductor.Fase.Fallo && rep.Problema.Length > 0) Aviso?.Invoke("No pude reproducir", rep.Problema);
                Latir(); Invalidate();
                CambioSonido?.Invoke();
            });
            return rep;
        }

        /// <summary>Escuchar desde el turno k (un pelito antes, para no comerse la primera sílaba).</summary>
        public void EscucharDesde(int k)
        {
            if (estado != Estado.Lista || k < 0 || k >= T.Turnos.Length) return;
            EscucharEn(Math.Max(0, T.Turnos[k].Ini - 0.25));
        }

        void EscucharEn(double seg)
        {
            if (paq.Audio.Length == 0) { Aviso?.Invoke("No hay audio para esta grabación", "el .opus no está en la carpeta"); return; }
            string no = MotivoParaNoSonar?.Invoke() ?? "";
            if (no.Length > 0) { Aviso?.Invoke("Ahora no puedo reproducir", no); return; }
            posicionSimulada = null;
            siguiendo = true;
            var r = Rep();
            r.Tocar(paq.Audio, seg, r.Velocidad);
            Latir(); Invalidate();
        }

        /// <summary>El botón de siempre: pausa si suena, sigue si está en pausa, y si no, arranca desde lo que se ve.</summary>
        public void Escuchar()
        {
            var f = FaseSonido;
            if (f == Reproductor.Fase.Sonando || f == Reproductor.Fase.Abriendo) { rep.Pausar(); return; }
            if (f == Reproductor.Fase.Pausa) { siguiendo = true; rep.Seguir(); return; }
            int k = bloques.Count > 0 ? BloqueEn(desplaz + S(4)) : -1;
            EscucharDesde(k >= 0 && desplaz > S(4) ? bloques[k].Turno : 0);
        }

        /// <summary>Pausa lo que suena (por ejemplo, porque empezó una llamada: el audio se mezclaría en la grabación).</summary>
        public void Pausar(string motivo)
        {
            if (rep == null || FaseSonido != Reproductor.Fase.Sonando && FaseSonido != Reproductor.Fase.Abriendo) return;
            rep.Pausar();
            if (!string.IsNullOrEmpty(motivo)) Aviso?.Invoke("Pausé la escucha", motivo);
        }

        public void Detener() => rep?.Parar();

        void CiclarVelocidad(int paso)
        {
            var vs = Reproductor.Velocidades;
            double v = Rep().Velocidad;
            int i = Array.FindIndex(vs, x => Math.Abs(x - v) < 0.01);
            i = ((i < 0 ? 0 : i) + paso + vs.Length) % vs.Length;
            rep.CambiarVelocidad(vs[i]);
            Invalidate();
        }

        /// <summary>La vista sigue al audio: el renglón que suena queda a un tercio del alto.</summary>
        void Seguir(double pos)
        {
            int k = T.TurnoSonando(pos);
            if (k < 0) k = T.TurnoEn(pos);
            if (k < 0 || k >= bloques.Count) return;
            var b = bloques[k];
            int car = T.Turnos[k].CaracterEn(pos);
            int yl = b.Y + (b.Renglones.Count > 0 ? b.YRenglon[RenglonDe(b, car)] : 0);
            objetivo = Limitar(yl - rCuerpo.Height * 0.32);
        }

        // ================================================================== búsqueda y resaltado

        void Buscar(string q, bool saltar)
        {
            consulta = q ?? "";
            hallazgos = consulta.Trim().Length > 0 ? T.Buscar(consulta) : new List<Aparicion>();
            hallazgosPorTurno = hallazgos.GroupBy(h => h.Turno).ToDictionary(x => x.Key, x => x.ToList());
            if (hallazgos.Count == 0) hallazgoActual = -1;
            else if (hallazgoActual < 0 || hallazgoActual >= hallazgos.Count || saltar)
            {
                // la primera aparición desde lo que se está viendo, no desde el principio de la reunión
                int desde = Math.Max(0, BloqueEn(desplaz));
                int i = hallazgos.FindIndex(h => h.Turno >= (bloques.Count > 0 ? bloques[desde].Turno : 0));
                hallazgoActual = i >= 0 ? i : 0;
            }
            if (saltar && hallazgoActual >= 0) IrAHallazgo(hallazgoActual);
            Invalidate();
        }

        void IrAHallazgo(int i)
        {
            if (i < 0 || i >= hallazgos.Count) return;
            hallazgoActual = i;
            var h = hallazgos[i];
            if (h.Turno >= bloques.Count) return;
            var b = bloques[h.Turno];
            int yl = b.Y + (b.Renglones.Count > 0 ? b.YRenglon[RenglonDe(b, h.Car)] : 0);
            if (yl < objetivo + S(20) || yl > objetivo + rCuerpo.Height - lineH * 2) objetivo = Limitar(yl - rCuerpo.Height * 0.4);
            siguiendo = false;
            Latir(); Invalidate();
        }

        void Proximo(int paso)
        {
            if (hallazgos.Count == 0) return;
            IrAHallazgo(((hallazgoActual < 0 ? 0 : hallazgoActual + paso) % hallazgos.Count + hallazgos.Count) % hallazgos.Count);
        }

        void LimpiarBusqueda()
        {
            consulta = "";
            hallazgos = new List<Aparicion>();
            hallazgosPorTurno = new Dictionary<int, List<Aparicion>>();
            hallazgoActual = -1;
        }

        void Resaltar(string voz)
        {
            filtro = filtro == voz ? "" : voz ?? "";
            if (filtro.Length > 0)
            {
                // llevar la vista a su próximo turno si no hay ninguno a la vista
                int a = Math.Max(0, BloqueEn(desplaz)), z = BloqueEn(desplaz + rCuerpo.Height);
                if (z < 0) z = bloques.Count - 1;
                bool visible = false;
                for (int i = a; i <= z && i < bloques.Count; i++) if (T.Turnos[bloques[i].Turno].Quien == filtro) { visible = true; break; }
                if (!visible)
                {
                    int k = Array.FindIndex(T.Turnos, a, t => t.Quien == filtro);
                    if (k < 0) k = Array.FindIndex(T.Turnos, t => t.Quien == filtro);
                    if (k >= 0 && k < bloques.Count) { objetivo = Limitar(bloques[k].Y - S(6)); siguiendo = false; }
                }
            }
            Latir(); Invalidate();
        }

        // ================================================================== nombres

        Sugerencia SugerenciaDe(string voz)
        {
            if (string.IsNullOrEmpty(voz)) return null;
            var v = T.VozDe(voz);
            if (v == null || v.Renombrada || v.EsYo) return null;
            return paq.Sugerencias.FirstOrDefault(s => s.Voz == voz);
        }

        List<Sugerencia> SugerenciasPendientes() => paq.Sugerencias.Where(s => SugerenciaDe(s.Voz) != null).ToList();

        /// <summary>Le pone (o le saca, con ""), un nombre a una voz: se ve al toque en todo y queda en nombres.json.</summary>
        public void Renombrar(string voz, string nombre)
        {
            var v = T.VozDe(voz);
            if (g == null || v == null || v.EsYo) return;
            nombre = (nombre ?? "").Trim();
            if (nombre.Length == 0 || nombre == v.Id) paq.Nombres.Remove(voz); else paq.Nombres[voz] = nombre;
            NombresDeVoces.Guardar(g.Carpeta, paq.Nombres);
            T.AplicarNombres(paq.Nombres);
            paq.Sugerencias = Detective.Sugerir(T, paq.Gente, g.Reunion, paq.Nombres);
            Reacomodar();
            Invalidate();
            NombresCambiaron?.Invoke();
        }

        void AplicarSugerencias()
        {
            var pend = SugerenciasPendientes();
            if (pend.Count == 0 || g == null) return;
            foreach (var s in pend) paq.Nombres[s.Voz] = s.Persona;
            NombresDeVoces.Guardar(g.Carpeta, paq.Nombres);
            T.AplicarNombres(paq.Nombres);
            paq.Sugerencias = Detective.Sugerir(T, paq.Gente, g.Reunion, paq.Nombres);
            Reacomodar();
            Invalidate();
            NombresCambiaron?.Invoke();
            Aviso?.Invoke(pend.Count == 1 ? "Le puse nombre a una voz" : $"Les puse nombre a {pend.Count} voces", string.Join(" · ", pend.Select(s => s.Voz + " → " + s.Persona)));
        }

        // ================================================================== menús

        ContextMenuStrip NuevoMenu()
        {
            var m = new ContextMenuStrip { Renderer = new RenderOscuro(), BackColor = Tema.Panel, ForeColor = Tema.Texto, Font = Tema.Fina(9.5f), ShowImageMargin = false, ShowItemToolTips = true };
            m.Closed += (o, e) => BeginInvoke(new Action(m.Dispose));
            return m;
        }

        static ToolStripMenuItem Item(string t, Action a, Color? c = null, bool fuerte = false)
        {
            var it = new ToolStripMenuItem(t) { ForeColor = c ?? Tema.Texto };
            if (fuerte) it.Font = Tema.Media(9.5f);
            if (a != null) it.Click += (o, e) => a();
            return it;
        }

        static ToolStripLabel Titulo(string t) => new ToolStripLabel(t.ToUpperInvariant()) { ForeColor = Tema.Apagado, Font = Tema.Media(7.5f), Margin = new Padding(8, 6, 8, 2) };

        void MenuDeVoz(ContextMenuStrip m, Voz v)
        {
            if (v.EsYo)
            {
                m.Items.Add(Titulo("«Yo» sos vos"));
                m.Items.Add(new ToolStripLabel("sale de tu micrófono, no hace falta adivinar") { ForeColor = Tema.Apagado, Font = Tema.Fina(8.5f) });
                return;
            }
            m.Items.Add(Titulo($"¿quién es «{v.Id}»?"));
            var sug = paq.Sugerencias.FirstOrDefault(s => s.Voz == v.Id);
            var usados = new HashSet<string>(paq.Nombres.Where(kv => kv.Key != v.Id).Select(kv => kv.Value), StringComparer.OrdinalIgnoreCase);
            if (sug != null && !usados.Contains(sug.Persona))
            {
                var it = Item($"★ {sug.Persona}   · el detective: {sug.Confianza:P0}", () => Renombrar(v.Id, sug.Persona), Tema.Crema, true);
                it.ToolTipText = string.Join("\n", sug.Pistas.Where(p => p.Peso > 0).Take(4).Select(p => "• " + p.Porque));
                m.Items.Add(it);
            }
            int puestos = 0;
            foreach (var crudo in paq.Gente.Nombres)
            {
                string n = Participantes.Mostrar(crudo);
                if (usados.Contains(n) || (sug != null && n == sug.Persona)) continue;
                if (++puestos > 16) break;
                m.Items.Add(Item(n, () => Renombrar(v.Id, n), n == v.Nombre ? Tema.Salvia : (Color?)null));
            }
            if (paq.Gente.Nombres.Count == 0) m.Items.Add(new ToolStripLabel("el panel de Teams no quedó anotado en esta reunión") { ForeColor = Tema.Apagado, Font = Tema.Fina(8.5f) });
            m.Items.Add(new ToolStripSeparator());
            var caja = new ToolStripTextBox { BorderStyle = BorderStyle.FixedSingle, BackColor = Tema.Tarjeta, ForeColor = Tema.Texto, Font = Tema.Fina(9.5f), AutoSize = false, Width = Math.Max(160, S(240)) };
            caja.Text = v.Renombrada ? v.Nombre : "";
            caja.ToolTipText = "escribí un nombre y Enter";
            caja.KeyDown += (o, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                string n = caja.Text.Trim();
                m.Close();
                if (n.Length > 0) Renombrar(v.Id, n);
            };
            m.Items.Add(new ToolStripLabel("otro nombre (Enter para ponerlo):") { ForeColor = Tema.Apagado, Font = Tema.Fina(8.5f), Margin = new Padding(8, 4, 8, 0) });
            m.Items.Add(caja);
            if (v.Renombrada) m.Items.Add(Item($"volver a «{v.Id}»", () => Renombrar(v.Id, ""), Tema.TextoSuave));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item(filtro == v.Id ? "dejar de resaltar" : $"resaltar solo a {v.Nombre}", () => Resaltar(v.Id), Tema.Cielo));
            m.Opened += (o, e) => caja.Focus();
        }

        void MostrarMenuTurno(int k, Point p)
        {
            var t = T.Turnos[k];
            var v = T.VozDe(t.Quien);
            var m = NuevoMenu();
            if (paq.Audio.Length > 0 && T.ConTiempos) m.Items.Add(Item($"▶ escuchar desde {Transcripcion.Reloj(t.Ini)}", () => EscucharDesde(k), Tema.Cyan, true));
            m.Items.Add(Item("copiar este turno", () => Copiar(TurnoComoTexto(k), "el turno")));
            m.Items.Add(Item("copiar desde acá hasta el final", () => Copiar(T.ComoTexto(k), "desde " + Transcripcion.Reloj(t.Ini))));
            if (v != null) { m.Items.Add(new ToolStripSeparator()); MenuDeVoz(m, v); }
            m.Show(this, p);
        }

        string TurnoComoTexto(int k)
        {
            var t = T.Turnos[k];
            var v = T.VozDe(t.Quien);
            return (T.ConTiempos ? "[" + Transcripcion.Reloj(t.Ini, true) + "] " : "") + (v != null ? v.Nombre + ": " : "") + t.Texto.Replace("\n", "\r\n");
        }

        void Copiar(string texto, string que)
        {
            if (string.IsNullOrEmpty(texto)) return;
            try { Clipboard.SetText(texto); Aviso?.Invoke("Copiado", que); }
            catch (System.Runtime.InteropServices.ExternalException ex) { Aviso?.Invoke("No pude copiar", ex.Message); }   // el portapapeles lo tiene otra app
        }

        // ================================================================== mouse y teclado

        Zona ZonaEn(Point p)
        {
            for (int i = zonas.Count - 1; i >= 0; i--) if (zonas[i].R.Contains(p)) return zonas[i];
            return null;
        }

        /// <summary>El turno bajo el mouse y si está sobre su hora (la hora es el botón de escuchar). O(log n).</summary>
        int TurnoEnPunto(Point p, out bool sobreHora)
        {
            sobreHora = false;
            if (!rCuerpo.Contains(p) || bloques.Count == 0 || p.X >= rBarra.X) return -1;
            double yc = p.Y - rCuerpo.Y + desplaz;
            int i = BloqueEn(yc);
            if (i < 0 || yc < bloques[i].Y || yc > bloques[i].Y + bloques[i].Alto - gapBloque + S(3)) return -1;
            sobreHora = T.ConTiempos && p.X < rCuerpo.X + gutter + S(4);
            return bloques[i].Turno;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            raton = e.Location;
            if (arrastrandoBarra) { ArrastrarBarra(e.Y); return; }
            if (arrastrandoCinta) { IrATiempo(e.X, false); Invalidate(); return; }
            var z = ZonaEn(e.Location);
            bool sobreHora = false;
            int k = z == null ? TurnoEnPunto(e.Location, out sobreHora) : -1;
            bool cinta = z?.Que == "cinta";
            bool cambio = !ReferenceEquals(z, zonaHover) && (z == null || zonaHover == null || z.Que != zonaHover.Que || !Equals(z.Dato, zonaHover.Dato) || z.R != zonaHover.R);
            if (!(cambio || k != turnoHover || sobreHora != horaHover || cinta || enCinta != cinta)) return;
            var zonaAntes = zonaHover;
            int turnoAntes = turnoHover;
            bool horaAntes = horaHover, cintaAntes = enCinta;
            zonaHover = z; turnoHover = k; horaHover = sobreHora; enCinta = cinta;
            if (cambio) puntoGlobo = e.Location;
            Cursor = z != null && z.Que != "lateral" ? (z.Que == "campo" ? Cursors.IBeam : Cursors.Hand) : sobreHora && paq.Audio.Length > 0 ? Cursors.Hand : Cursors.Default;
            // se repinta SOLO lo que cambió (en grande, repintar todo por cada movimiento del mouse costaba decenas de ms)
            if (cambio && (ConGlobo(zonaAntes) || ConGlobo(z))) { Invalidate(); return; }     // la tarjeta del porqué puede caer en cualquier lado
            if (cambio)
            {
                if (zonaAntes != null) Repintar(Rectangle.Inflate(zonaAntes.R, S(4), S(4)));
                if (z != null) Repintar(Rectangle.Inflate(z.R, S(4), S(4)));
                Repintar(rEnc);                                        // el encabezado muestra la ayuda de lo que está bajo el mouse
            }
            if (k != turnoAntes) { InvalidarBloque(turnoAntes); Update(); InvalidarBloque(k); Update(); }
            else if (sobreHora != horaAntes) { InvalidarBloque(k); Update(); }
            if (cinta || cintaAntes) Repintar(ZonaCinta);
        }

        Point puntoGlobo;
        bool ConGlobo(Zona z) => z != null && (z.Que == "sugerencia" || (z.Que == "voz" && Amplio)) && SugerenciaDe(z.Dato as string) != null;

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            zonaHover = null; turnoHover = -1; horaHover = false; enCinta = false;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Focused) Focus();
            if (e.Button != MouseButtons.Left) return;
            var z = ZonaEn(e.Location);
            if (z?.Que == "cinta") { arrastrandoCinta = true; Capture = true; IrATiempo(e.X, false); return; }
            if (z?.Que == "barra") { arrastrandoBarra = true; Capture = true; agarreBarra = e.Y; ArrastrarBarra(e.Y); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (arrastrandoCinta) { arrastrandoCinta = false; Capture = false; IrATiempo(e.X, true); return; }
            if (arrastrandoBarra) { arrastrandoBarra = false; Capture = false; Invalidate(); return; }
            var z = ZonaEn(e.Location);
            if (e.Button == MouseButtons.Right)
            {
                if (z?.Que == "velocidad") { CiclarVelocidad(-1); return; }
                if ((z?.Que == "voz" || z?.Que == "sugerencia") && z.Dato is string id && T.VozDe(id) != null) { var m = NuevoMenu(); MenuDeVoz(m, T.VozDe(id)); m.Show(this, e.Location); return; }
                int kr = TurnoEnPunto(e.Location, out _);
                if (kr >= 0) MostrarMenuTurno(kr, e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (z != null) { Accion(z); return; }
            int k = TurnoEnPunto(e.Location, out bool hora);
            if (k < 0) return;
            if (hora && paq.Audio.Length > 0) { EscucharDesde(k); return; }
            // clic en el nombre (o en el avatar): resalta esa voz
            var t = T.Turnos[k];
            var b = bloques[k];
            int y = rCuerpo.Y + b.Y - (int)Math.Round(desplaz);
            int xTexto = rCuerpo.X + gutter + S(Amplio ? 16 : 10);
            bool enNombre = t.Quien.Length > 0 && (Amplio ? e.Y < y + b.Cabecera && e.X < xTexto + (int)((T.VozDe(t.Quien)?.Nombre.Length ?? 0) * Prosa.Avance(fNombre)) + S(4)
                                                          : e.Y < y + lineH && e.X >= xTexto && e.X < xTexto + (int)(b.Sangria * avance));
            if (enNombre) Resaltar(t.Quien);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left || ZonaEn(e.Location) != null) return;
            // doble clic en el encabezado: en grande ↔ compacto (como maximizar una ventana)
            if (rEnc.Contains(e.Location) && estado == Estado.Lista) { CambiarAmplio(!Amplio); return; }
            int k = TurnoEnPunto(e.Location, out bool hora);
            if (k >= 0 && !hora) Copiar(TurnoComoTexto(k), "el turno de las " + Transcripcion.Reloj(T.Turnos[k].Ini));
        }

        void Accion(Zona z)
        {
            switch (z.Que)
            {
                case "buscar": buscando = true; Focus(); Latir(); Invalidate(); break;
                case "campo": buscando = true; Focus(); Latir(); Invalidate(); break;
                case "limpiar": LimpiarBusqueda(); buscando = false; Invalidate(); break;
                case "anterior": Proximo(-1); break;
                case "siguiente": Proximo(+1); break;
                case "sonar": case "pausa": Escuchar(); break;
                case "velocidad": CiclarVelocidad(+1); break;
                case "ampliar": CambiarAmplio(true); break;
                case "cerrar": CambiarAmplio(false); break;
                case "voz": Resaltar(z.Dato as string); break;
                case "sugerencia": { var s = SugerenciaDe(z.Dato as string); if (s != null) Renombrar(s.Voz, s.Persona); break; }
                case "aplicar": AplicarSugerencias(); break;
                case "volver": siguiendo = true; Latir(); Invalidate(); break;
            }
        }

        /// <summary>Un clic (o arrastre) en la cinta lleva la vista a ese momento; al soltar, si suena, salta ahí.</summary>
        void IrATiempo(int x, bool soltar)
        {
            double dur = T.Duracion;
            if (dur <= 0 || rCinta.Width <= 0) return;
            double s = Math.Max(0, Math.Min(dur, (x - rCinta.X) / (double)rCinta.Width * dur));
            int k = T.TurnoEn(s);
            if (k < 0) k = 0;
            if (k < bloques.Count) objetivo = Limitar(bloques[k].Y - S(6));
            siguiendo = false;
            if (soltar && Sonando) EscucharEn(s);
            Latir(); Invalidate();
        }

        void ArrastrarBarra(int y)
        {
            double max = MaxDesplaz;
            if (max <= 0) return;
            float frac = rCuerpo.Height / (float)Math.Max(1, altoTotal);
            float h = Math.Max(S(22), rBarra.Height * frac);
            double f = (y - rBarra.Y - h / 2) / Math.Max(1, rBarra.Height - h);
            objetivo = desplaz = Limitar(f * max);
            siguiendo = false;
            Invalidate();
        }

        public void Rueda(int delta)
        {
            if (Amplio && rLateral.Contains(PointToClient(MousePosition)))
            {
                desplazLateral = Math.Max(0, desplazLateral - Math.Sign(delta) * S(56));
                Invalidate();
                return;
            }
            Desplazar(-Math.Sign(delta) * lineH * 3.0 * Math.Max(1, Math.Abs(delta) / 120));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Rueda(e.Delta);
            // 🚨 con el foco acá la rueda llega DIRECTO: si no se marca atendida, DefWindowProc la sube a la ventana, que
            //    la vuelve a mandar al control bajo el mouse (este) y cada muesca desplaza dos veces
            if (e is HandledMouseEventArgs h) h.Handled = true;
        }

        void Desplazar(double px)
        {
            objetivo = Limitar(objetivo + px);
            if (Sonando) siguiendo = false;          // moverse a mano mientras suena: la vista deja de seguir (hasta «volver»)
            Latir(); Invalidate();
        }

        /// <summary>
        /// Esc cierra lo último que se abrió acá: la búsqueda, el resaltado de una voz, la vista en grande.
        /// 🚨 La ventana tiene KeyPreview y con Esc se va a la bandeja: si esto se atendiera en OnKeyDown, la ventana lo
        ///    vería ANTES y se escondería. ProcessCmdKey corre primero; si acá no hay nada que cerrar, Esc sigue su camino.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                if (buscando || consulta.Length > 0) { LimpiarBusqueda(); buscando = false; Invalidate(); return true; }
                if (filtro.Length > 0) { Resaltar(filtro); return true; }
                if (Amplio) { CambiarAmplio(false); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override bool IsInputKey(Keys k)
        {
            var c = k & Keys.KeyCode;
            return c == Keys.Up || c == Keys.Down || c == Keys.Left || c == Keys.Right || c == Keys.Space || c == Keys.Enter || c == Keys.Escape
                   || c == Keys.PageUp || c == Keys.PageDown || c == Keys.Home || c == Keys.End || base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            bool enBusqueda = buscando || consulta.Length > 0;
            switch (e.KeyCode)
            {
                case Keys.F when e.Control: buscando = true; Latir(); Invalidate(); break;
                case Keys.C when e.Control:
                    if (turnoHover >= 0) Copiar(TurnoComoTexto(turnoHover), "el turno");
                    else Copiar(TextoParaCopiar(), "la transcripción entera");
                    break;
                case Keys.F3: Proximo(e.Shift ? -1 : 1); break;
                case Keys.Enter when enBusqueda: Proximo(e.Shift ? -1 : 1); break;
                case Keys.Back when enBusqueda:
                    if (consulta.Length > 0) Buscar(consulta.Substring(0, consulta.Length - 1), true);
                    break;
                case Keys.Space when !buscando: Escuchar(); break;
                case Keys.Left when !buscando && Sonando: EscucharEn(Math.Max(0, rep.Posicion - 5)); break;
                case Keys.Right when !buscando && Sonando: EscucharEn(rep.Posicion + 5); break;
                case Keys.Up: Desplazar(-lineH * 2); break;
                case Keys.Down: Desplazar(lineH * 2); break;
                case Keys.PageUp: Desplazar(-(rCuerpo.Height - lineH * 2)); break;
                case Keys.PageDown: Desplazar(rCuerpo.Height - lineH * 2); break;
                case Keys.Home: objetivo = 0; siguiendo = false; Latir(); Invalidate(); break;
                case Keys.End: objetivo = MaxDesplaz; siguiendo = false; Latir(); Invalidate(); break;
                default: return;
            }
            e.Handled = true;
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (estado != Estado.Lista || e.KeyChar < ' ' || ModifierKeys.HasFlag(Keys.Control)) return;
            if (!buscando && e.KeyChar == ' ') return;          // el espacio, fuera de la búsqueda, es play/pausa
            buscando = true;
            Buscar(consulta + e.KeyChar, true);
            e.Handled = true;
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Latir(); Invalidate(rEnc); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); if (consulta.Length == 0) buscando = false; Latir(); Invalidate(rEnc); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { reloj.Stop(); reloj.Dispose(); rep?.Dispose(); cintaCache?.Dispose(); cintaCache = null; }
            base.Dispose(disposing);
        }

        /// <summary>El reloj corre SOLO mientras algo se mueve (audio, desplazamiento suave, cursor de búsqueda o carga): quieto, cero CPU.</summary>
        void Latir()
        {
            bool mover = PosicionAudio().HasValue && FaseSonido != Reproductor.Fase.Pausa
                         || Math.Abs(objetivo - desplaz) > 0.5
                         || (buscando && Focused)
                         || estado == Estado.Cargando
                         || (estado == Estado.SinTexto && EnCurso)
                         || ultimoSonando >= 0;
            if (mover && !reloj.Enabled) { ultimoCuadro = DateTime.Now; reloj.Start(); }
        }

        static string Corto(string s, int n) { s = (s ?? "").Trim(); return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        // ================================================================== para las fotos y las pruebas

        /// <summary>El menú del clic derecho de una voz, armado pero sin mostrar (para las pruebas).</summary>
        internal ContextMenuStrip MenuDeVozParaPrueba(string voz)
        {
            var m = new ContextMenuStrip { Renderer = new RenderOscuro() };
            var v = T.VozDe(voz);
            if (v != null) MenuDeVoz(m, v);
            return m;
        }

        /// <summary>
        /// Pone el visor en un estado puntual sin mouse (las fotos de `--foto-transcripcion` y `--foto llamadas --modo …`):
        /// «buscar:texto», «voz:Persona 1», «sonar:125» (karaoke simulado en ese segundo), «turno:40», «hover:12», «arriba».
        /// </summary>
        public void Probar(string que)
        {
            que = que ?? "";
            int dos = que.IndexOf(':');
            string cmd = dos > 0 ? que.Substring(0, dos) : que, arg = dos > 0 ? que.Substring(dos + 1) : "";
            switch (cmd)
            {
                case "buscar": buscando = true; Buscar(arg, true); desplaz = objetivo; break;
                case "voz": Resaltar(arg); desplaz = objetivo; break;
                case "sonar":
                    if (double.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s))
                    { posicionSimulada = s; simuladaHasta = DateTime.Now.AddSeconds(SegundosSimulacion); siguiendo = true; Seguir(s); desplaz = objetivo; Latir(); }
                    break;
                case "turno":
                    if (int.TryParse(arg, out int k) && k >= 0 && k < bloques.Count) { objetivo = desplaz = Limitar(bloques[k].Y - S(6)); siguiendo = false; }
                    break;
                case "hover":
                    if (int.TryParse(arg, out int h)) { turnoHover = h; horaHover = true; }
                    break;
                case "arriba": objetivo = desplaz = 0; break;
                case "reiniciar":
                    // lo que dejó una foto: sin karaoke simulado, sin búsqueda, sin voz resaltada, sin hover
                    posicionSimulada = null; filtro = ""; LimpiarBusqueda(); buscando = false; turnoHover = -1; horaHover = false;
                    break;
            }
            Invalidate();
        }
    }
}
