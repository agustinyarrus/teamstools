using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace TeamsTools
{
    // =====================================================================================================
    // PULSO: «el audio está entrando», 20 veces por segundo.
    //
    // loopcap 3 (-levels) publica por stdout una línea por evento:
    //     LV <ms> <idx>:<dB> <idx>:<dB> …                 pico de cada pista en los últimos 50 ms
    //     DEV <+|~|z|-> <idx> <salida|mic> <seg> <nombre>  se sumó · volvió · en espera · se desconectó
    // Este objeto las digiere y la banda en vivo las pinta: dos cintas que corren (lo que suena en la llamada y
    // lo que entra por tu micrófono), el nivel de cada una y los dispositivos que se van sumando.
    //
    // UN escritor (el hilo que lee a loopcap) y UN lector (la UI a 30 cuadros por segundo): sin candados. Las
    // historias son anillos cuyo contador se publica con Volatile DESPUÉS de escribir la muestra; la lista de
    // pistas se reemplaza entera (copiar al escribir), así la UI nunca ve una lista a medio armar.
    // =====================================================================================================

    /// <summary>Una pista que loopcap está grabando (una salida o un micrófono), vista desde la interfaz.</summary>
    internal sealed class PistaEnVivo
    {
        public int Indice;
        public bool EsMic;
        public string Nombre = "";
        public string Estado = "grabando";          // grabando · en espera · desconectada
        public double SeSumoEn;                     // segundo de la grabación en que se sumó
        public DateTime Cambio = DateTime.Now;      // último cambio de estado (el aviso animado de la banda)
        public string UltimoAviso = "se sumó";
        public volatile float Db = -120;            // último nivel: lo escribe el lector de loopcap, lo lee la UI
        public long UltimaSenalTicks;               // última vez que pasó el umbral de señal (DateTime.Ticks)

        public bool ConSenal(double segundos) => (DateTime.Now.Ticks - Interlocked.Read(ref UltimaSenalTicks)) < TimeSpan.FromSeconds(segundos).Ticks;
    }

    internal sealed class PulsoAudio
    {
        public const int Capacidad = 256;           // 12,8 s de historia a 20 lecturas por segundo
        public const float UmbralSenalDb = -50f;    // por encima: hay señal (voz, música); por debajo: ruido de fondo
        public const float PisoDb = -120f;

        readonly float[] llamada = new float[Capacidad], mic = new float[Capacidad];
        long escritas;                               // muestras escritas en cada anillo (van a la par)
        PistaEnVivo[] pistas = new PistaEnVivo[0];
        long ultimoNivelTicks;
        int tramo = 1;

        public PulsoAudio()
        {
            for (int i = 0; i < Capacidad; i++) { llamada[i] = PisoDb; mic[i] = PisoDb; }
        }

        /// <summary>Un cambio de pista, para la bitácora de la grabación: (pista, «se sumó» / «volvió» / …).</summary>
        public event Action<PistaEnVivo, string> Aviso;

        // ------------------------------------------------------------------ lo que lee la UI (nunca bloquea)

        public PistaEnVivo[] Pistas => Volatile.Read(ref pistas);
        public long Escritas => Volatile.Read(ref escritas);
        public DateTime UltimoNivel => new DateTime(Interlocked.Read(ref ultimoNivelTicks));
        public int Tramo => Volatile.Read(ref tramo);
        /// <summary>Lo que dice Teams de tu micrófono: true = en silencio (no se graba), false = abierto, null = no se sabe.</summary>
        public bool? MicSilenciado { get; set; }
        public DateTime MicSilenciadoDesde { get; set; } = DateTime.MinValue;

        /// <summary>
        /// Copia las últimas <paramref name="n"/> muestras de una historia (la más nueva al final) y devuelve el
        /// contador de esa lectura. O(n). Si el escritor avanzara durante la copia, solo se tocarían las muestras
        /// más viejas del anillo (a 20 por segundo y n ≤ 200 no pasa).
        /// </summary>
        public long Copiar(bool deMic, float[] destino, int n)
        {
            long hasta = Escritas;
            var src = deMic ? mic : llamada;
            for (int i = 0; i < n; i++)
            {
                long k = hasta - n + i;
                destino[i] = k < 0 ? PisoDb : src[(int)(k % Capacidad)];
            }
            return hasta;
        }

        // ------------------------------------------------------------------ lo que escribe el lector de loopcap

        /// <summary>Un tramo nuevo = un loopcap nuevo: los índices de pista vuelven a empezar. La historia sigue.</summary>
        public void NuevoTramo(int n)
        {
            Volatile.Write(ref pistas, new PistaEnVivo[0]);
            Volatile.Write(ref tramo, n);
        }

        /// <summary>Una línea de stdout de loopcap. O(pistas). Lo que no entiende, lo ignora (nunca tira).</summary>
        public void Linea(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            if (s.StartsWith("LV ", StringComparison.Ordinal)) Niveles(s);
            else if (s.StartsWith("DEV ", StringComparison.Ordinal)) Dispositivo(s);
        }

        void Niveles(string s)
        {
            var ps = Pistas;
            float maxLlamada = PisoDb, maxMic = PisoDb;
            long ahora = DateTime.Now.Ticks;
            var toks = s.Split(' ');
            for (int i = 2; i < toks.Length; i++)
            {
                int dos = toks[i].IndexOf(':');
                if (dos <= 0) continue;
                if (!int.TryParse(toks[i].Substring(0, dos), NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)) continue;
                if (!float.TryParse(toks[i].Substring(dos + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float db)) continue;
                var p = Buscar(ps, idx);
                if (p == null) continue;                 // una pista cuyo DEV todavía no llegó: se ignora un instante
                p.Db = db;
                if (db > UmbralSenalDb) Interlocked.Exchange(ref p.UltimaSenalTicks, ahora);
                if (p.EsMic) { if (db > maxMic) maxMic = db; }
                else if (db > maxLlamada) maxLlamada = db;
            }
            long k = escritas;
            llamada[(int)(k % Capacidad)] = maxLlamada;
            mic[(int)(k % Capacidad)] = maxMic;
            Volatile.Write(ref escritas, k + 1);         // recién ahora la UI ve la muestra
            Interlocked.Exchange(ref ultimoNivelTicks, ahora);
        }

        void Dispositivo(string s)
        {
            // DEV <signo> <idx> <tipo> <seg> <nombre con espacios>
            var t = s.Split(new[] { ' ' }, 6);
            if (t.Length < 6 || !int.TryParse(t[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)) return;
            double.TryParse(t[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double seg);
            string signo = t[1], estado, aviso;
            switch (signo)
            {
                case "+": estado = "grabando"; aviso = "se sumó"; break;
                case "~": estado = "grabando"; aviso = "volvió"; break;
                case "z": estado = "en espera"; aviso = t[3] == "mic" ? "en espera: Teams ya no lo usa" : "en espera: nadie lo usa"; break;
                case "-": estado = "desconectada"; aviso = "se desconectó"; break;
                default: return;
            }
            var viejas = Pistas;
            var p = Buscar(viejas, idx);
            var nuevas = new List<PistaEnVivo>(viejas);
            if (p == null)
            {
                p = new PistaEnVivo { Indice = idx, EsMic = t[3] == "mic", Nombre = t[5].Trim(), SeSumoEn = seg };
                nuevas.Add(p);
            }
            p.Estado = estado;
            p.UltimoAviso = aviso;
            p.Cambio = DateTime.Now;
            if (estado != "grabando") p.Db = PisoDb;
            Volatile.Write(ref pistas, nuevas.ToArray());
            try { Aviso?.Invoke(p, aviso); } catch { }
        }

        static PistaEnVivo Buscar(PistaEnVivo[] ps, int idx)
        {
            foreach (var p in ps) if (p.Indice == idx) return p;
            return null;
        }
    }
}
