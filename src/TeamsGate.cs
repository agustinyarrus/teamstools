using System;
using System.Diagnostics;
using System.Threading;

namespace TeamsTools
{
    /// <summary>
    /// La ADUANA de Teams: un único torniquete reentrante por el que pasa TODO acceso al DOM y a las ventanas
    /// de Teams desde los hilos de fondo (observador, autocontestador, presencia, cron, guión).
    ///
    /// 🚨 Por qué existe (bug real, 22-sep-2026): tres hilos tocaban las MISMAS ventanas de Teams a destiempo.
    ///    El observador destapaba/ocultaba la ventana cada 30 s, el autocontestador cada 6 s y la presencia
    ///    cada 20 s (más el rescate, que abre el menú del avatar y roba el foreground). Sin nadie que los
    ///    ordenara, uno le hacía `ShowWindow(SW_HIDE)` a la ventana mientras otro caminaba su árbol UIA para
    ///    escribir un mensaje → COMException y el texto quedaba a medias en el editor, sin enviarse. Y el salto
    ///    de foreground entre ventanas se veía como «se me cerró Chrome de la nada».
    ///
    /// La cura es de manual: un solo recurso compartido (las ventanas de Teams) ⇒ un solo candado que lo
    /// serializa. `Monitor` ya es reentrante por hilo, así que un envío que por dentro llama a `BuscarVentana`
    /// (también aduanado) no se traba a sí mismo; y dos hilos distintos hacen fila en vez de pisarse.
    ///
    /// Complejidad: adquirir es O(1); la espera es la del `Monitor` del runtime (cola FIFO aproximada). El
    /// tiempo que un hilo retiene la aduana es el de UNA operación completa sobre Teams (leer lista, abrir chat,
    /// escribir y enviar), no el de la app entera: los otros hilos esperan su turno, no se cuelgan.
    /// </summary>
    internal sealed class TeamsGate
    {
        readonly object llave = new object();
        readonly Logger log;

        // telemetría barata para el tablero salud: cuánto se esperó y quién tuvo la aduana
        public long EsperasMs { get; private set; }
        public int Entradas { get; private set; }
        public int Contenciones { get; private set; }          // veces que alguien tuvo que esperar de verdad
        public string Adentro { get; private set; } = "";       // motivo del que la tiene ahora (o "")
        public string UltimoMotivo { get; private set; } = "";

        public TeamsGate(Logger l) { log = l; }

        /// <summary>
        /// Entra a la aduana (bloqueante, reentrante). Devolvé el token con `using`: al salir del bloque se
        /// libera solo, pase lo que pase. Sólo para los HILOS DE FONDO — nunca desde el hilo de UI, que no
        /// puede quedarse esperando (para eso está <see cref="EntrarRapido"/>).
        /// </summary>
        public Token Entrar(string motivo)
        {
            var sw = Stopwatch.StartNew();
            bool libreAlToque = Monitor.TryEnter(llave);
            if (!libreAlToque)
            {
                Contenciones++;
                Monitor.Enter(llave);          // ahora sí, esperando el turno
            }
            sw.Stop();
            EsperasMs += sw.ElapsedMilliseconds;
            Entradas++;
            Adentro = motivo; UltimoMotivo = motivo;
            // sólo molesto al log si la espera fue notable: una fila de medio segundo no es noticia
            if (!libreAlToque && sw.ElapsedMilliseconds > 1500)
                log?.Debug($"Aduana de Teams: «{motivo}» esperó {sw.ElapsedMilliseconds} ms su turno");
            return new Token(this);
        }

        /// <summary>
        /// Entra sólo si la aduana está libre YA (o se libera en <paramref name="ms"/>). Para el hilo de UI y
        /// otros lugares donde bloquear sería peor que saltear: si está ocupada, devuelve null y el llamador
        /// hace su mejor esfuerzo con lo que tenga cacheado. NUNCA cuelga la interfaz.
        /// </summary>
        public Token EntrarRapido(string motivo, int ms = 0)
        {
            if (!Monitor.TryEnter(llave, ms)) return null;
            Entradas++;
            Adentro = motivo; UltimoMotivo = motivo;
            return new Token(this);
        }

        void Salir() { Adentro = ""; Monitor.Exit(llave); }

        /// <summary>El comprobante: liberar en un `finally` implícito vía `using`.</summary>
        internal sealed class Token : IDisposable
        {
            TeamsGate g;
            public Token(TeamsGate gate) { g = gate; }
            public void Dispose() { var x = g; g = null; x?.Salir(); }
        }
    }
}
