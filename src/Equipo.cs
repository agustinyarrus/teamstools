using System;
using System.Collections.Generic;
using System.Linq;

namespace TeamsTools
{
    // =====================================================================================================
    // RELACIONES: qué pasó entre vos y cada persona, sacado del historial ENTERO de Teams
    //
    // El observador de la pestaña «equipo» sabe quién está conectado AHORA. Eso es media foto: la otra mitad
    // son los 59.697 mensajes que ya están en el disco. Acá se cruzan las dos y aparece lo que ninguna de las
    // dos dice sola: con quién hablás de verdad, quién te hace esperar, a quién le debés una respuesta.
    // =====================================================================================================

    /// <summary>
    /// El vínculo con una persona. Los tiempos son MEDIANAS, no promedios: un solo mensaje contestado al otro
    /// día te arruina cualquier promedio, y justamente esos son los normales en un chat de trabajo.
    ///
    /// Se miden dos cosas distintas que suelen confundirse:
    ///   · ESPERA   — desde que el otro EMPEZÓ a escribir hasta tu respuesta. Lo que de verdad esperó.
    ///   · REACCIÓN — desde que TERMINÓ de escribir hasta tu respuesta. Lo rápido que saltás cuando ya frenó.
    /// La espera es la que importa; la reacción es la que se siente.
    /// </summary>
    internal sealed class Vinculo
    {
        public string Nombre = "", Clave = "";
        public int Suyos, Mios;                 // en los chats de a dos: la conversación de verdad
        public int EnGrupos;                    // lo que escribió en canales, reuniones y grupos
        // 🚨🚨 Hay DOS poblaciones y antes se mezclaban en la misma fila: lo que se escribieron EN PRIVADO
        //    y lo que esa persona escribió EN CUALQUIER LADO (canales, reuniones, grupos). Poner «264
        //    mensajes» al lado de «112 días» era falso: el chat privado tenía mensajes en 20 días; los
        //    otros 92 eran días en que la persona habló en un canal. Cada número lleva ahora su par.
        public int Palabras, PalabrasGrupos, Dias, DiasTodo, Chats;
        public DateTime Primero = DateTime.MaxValue, Ultimo = DateTime.MinValue;              // del chat de a dos
        public DateTime PrimeroTodo = DateTime.MaxValue, UltimoTodo = DateTime.MinValue;      // de cualquier lado
        public TimeSpan? MiEspera, SuEspera, MiReaccion, SuReaccion;
        public int Turnos, YoArranco, ElArranca;
        public bool Pendiente, DeADos;
        /// <summary>El más VIEJO de los mensajes suyos que dejaste sin contestar. Cuánto hace que le debés.</summary>
        public DateTime DebeDesde = DateTime.MaxValue;
        /// <summary>En cuántos de sus chats quedó la pelota de tu lado (Teams abre uno por reunión de a dos).</summary>
        public int HilosSinContestar;
        public string UltimoTexto = "";
        public readonly int[] PorHora = new int[24];
        public readonly int[] PorDia = new int[7];

        internal readonly List<double> misEsperas = new List<double>();
        internal readonly List<double> susEsperas = new List<double>();
        internal readonly List<double> misReacciones = new List<double>();
        internal readonly List<double> susReacciones = new List<double>();
        internal readonly HashSet<DateTime> diasDeADos = new HashSet<DateTime>();
        internal readonly HashSet<DateTime> diasVistos = new HashSet<DateTime>();

        public int Total => Suyos + Mios;
        public int Todo => Total + EnGrupos;
        public bool Hay => Total > 0;

        /// <summary>0 = habla solo él · 0,5 = parejo · 1 = hablás solo vos.</summary>
        public double Balance => Total > 0 ? (double)Mios / Total : 0.5;
        /// <summary>Hace cuánto que no te escribe EN PRIVADO. MaxValue si nunca lo hizo.</summary>
        public TimeSpan Silencio => Ultimo == DateTime.MinValue ? TimeSpan.MaxValue : DateTime.Now - Ultimo;
        /// <summary>Hace cuánto que no escribe en NINGÚN lado. Puede estar activísimo en canales y mudo con vos.</summary>
        public TimeSpan SilencioTodo => UltimoTodo == DateTime.MinValue ? TimeSpan.MaxValue : DateTime.Now - UltimoTodo;
        /// <summary>Palabras por mensaje en el chat de a dos.</summary>
        public double Largo => Suyos > 0 ? (double)Palabras / Suyos : 0;
        /// <summary>Palabras por mensaje contando todo lo que escribe, también en canales.</summary>
        public double LargoTodo => Todo > 0 ? (double)(Palabras + PalabrasGrupos) / Todo : 0;

        /// <summary>La hora del día en la que más te escribe. -1 si no hay nada.</summary>
        public int HoraPico
        {
            get { int b = -1, v = 0; for (int i = 0; i < 24; i++) if (PorHora[i] > v) { v = PorHora[i]; b = i; } return b; }
        }

        /// <summary>&gt;1 lo hacés esperar más de lo que te espera a vos · &lt;1 al revés. 1 cuando no se sabe.</summary>
        public double Deuda => MiEspera.HasValue && SuEspera.HasValue && SuEspera.Value.TotalSeconds > 1
            ? MiEspera.Value.TotalSeconds / SuEspera.Value.TotalSeconds : 1;

        /// <summary>Quién rompe los silencios largos: &gt;0 arrancás vos, &lt;0 arranca él.</summary>
        public int Iniciativa => YoArranco - ElArranca;

        /// <summary>
        /// ¿Hay suficientes turnos como para que las medianas signifiquen algo? Con dos intercambios, una
        /// mediana de «20h53» es una anécdota, no una costumbre — y ordenando por ella encabezaba la tabla.
        /// </summary>
        public bool Confiable => Turnos >= MinimoTurnos;
        public const int MinimoTurnos = 6;

        internal void Cerrar()
        {
            MiEspera = Mediana(misEsperas); SuEspera = Mediana(susEsperas);
            MiReaccion = Mediana(misReacciones); SuReaccion = Mediana(susReacciones);
            Turnos = misEsperas.Count + susEsperas.Count;
            Dias = diasDeADos.Count;
            DiasTodo = diasVistos.Count;
            if (Primero == DateTime.MaxValue) Primero = DateTime.MinValue;
            if (PrimeroTodo == DateTime.MaxValue) PrimeroTodo = DateTime.MinValue;
            if (!Pendiente) DebeDesde = DateTime.MinValue;
        }

        static TimeSpan? Mediana(List<double> xs)
        {
            if (xs.Count == 0) return null;
            xs.Sort();
            double v = xs.Count % 2 == 1 ? xs[xs.Count / 2] : (xs[xs.Count / 2 - 1] + xs[xs.Count / 2]) / 2.0;
            return TimeSpan.FromSeconds(v);
        }
    }

    /// <summary>Un turno de conversación: una corrida seguida de mensajes del mismo lado.</summary>
    internal sealed class Turno { public bool Mio; public DateTime Ini, Fin; }

    /// <summary>
    /// Arma los vínculos recorriendo el historial una sola vez. Sobre 59.697 mensajes tarda decenas de ms,
    /// así que se recalcula entero cada vez que cambia la historia en vez de mantener estado incremental.
    /// </summary>
    internal sealed class Relaciones
    {
        /// <summary>Un hueco de más de esto y lo que viene después es alguien ARRANCANDO, no contestando.</summary>
        public const double SegundosArranque = 4 * 3600;
        /// <summary>Más de un día no es una respuesta: es otra conversación. No entra en la mediana.</summary>
        public const double SegundosTope = 24 * 3600;

        readonly Dictionary<string, Vinculo> ix = new Dictionary<string, Vinculo>(StringComparer.Ordinal);

        public Vinculo[] Todos = new Vinculo[0];
        public string Yo = "";
        public int Mensajes, Chats, DeADos, Personas;
        public DateTime Calculado, Desde, Hasta;
        public long Ms;

        public bool Hay => Todos.Length > 0;
        public Vinculo De(string nombre)
        {
            Vinculo v;
            return nombre != null && ix.TryGetValue(Contactos.Normalizar(nombre), out v) ? v : null;
        }

        /// <summary>Un nombre de persona de verdad, no un id crudo de Teams (19:… / 48:…) ni un vacío.</summary>
        internal static bool EsNombre(string a) =>
            !string.IsNullOrEmpty(a) && a.Length > 1
            && !a.StartsWith("19:", StringComparison.Ordinal)
            && !a.StartsWith("48:", StringComparison.Ordinal)
            && !a.StartsWith("8:", StringComparison.Ordinal);

        public void Calcular(Historia h)
        {
            var reloj = System.Diagnostics.Stopwatch.StartNew();
            ix.Clear();

            var utiles = new List<MensajeHist>(h.Mensajes.Count);
            foreach (var m in h.Mensajes) if (!m.EsRuido && !m.SinFecha) utiles.Add(m);
            Mensajes = utiles.Count;
            if (utiles.Count == 0) { Todos = new Vinculo[0]; Personas = 0; Calculado = DateTime.Now; return; }

            // quién soy: el autor más repetido entre mis propios mensajes que no sea un id crudo
            Yo = utiles.Where(m => m.Mio && EsNombre(m.Autor))
                       .GroupBy(m => m.Autor, StringComparer.Ordinal)
                       .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
            string yoClave = Contactos.Normalizar(Yo);

            // 🚨 agrupar por ConvId y NO por Conv: Historia le pone al chat el nombre del otro, y en un grupo
            //    de cinco elige al que más habló. Dos conversaciones distintas terminarían con el mismo nombre
            //    y se fusionarían — el privado con alguien mezclado con el grupo donde esa persona domina.
            var porConv = new Dictionary<string, List<MensajeHist>>(StringComparer.Ordinal);
            foreach (var m in utiles)
            {
                string k = m.ConvId.Length > 0 ? m.ConvId : m.Conv;
                List<MensajeHist> l;
                if (!porConv.TryGetValue(k, out l)) { l = new List<MensajeHist>(); porConv[k] = l; }
                l.Add(m);
            }
            Chats = porConv.Count; DeADos = 0;

            foreach (var par in porConv)
            {
                var ms = par.Value;                     // h.Mensajes ya viene ordenado por fecha
                var otros = new HashSet<string>(StringComparer.Ordinal);
                foreach (var m in ms)
                    if (!m.Mio && EsNombre(m.Autor) && Contactos.Normalizar(m.Autor) != yoClave) otros.Add(m.Autor);

                // de a dos de verdad: un único interlocutor. Es la única forma honesta de medir quién le
                // contesta a quién; en un grupo de ocho, "el que habló después" no te estaba contestando.
                string dyad = otros.Count == 1 ? otros.First() : null;
                if (dyad != null) DeADos++;

                foreach (var m in ms)
                {
                    if (m.Mio)
                    {
                        // «días juntos» cuenta los días en que hubo charla, escriba quien escriba
                        if (dyad != null) { var mio = Traer(dyad); mio.Mios++; mio.diasDeADos.Add(m.Fecha.Date); }
                        continue;
                    }
                    if (!EsNombre(m.Autor) || Contactos.Normalizar(m.Autor) == yoClave) continue;
                    var v = Traer(m.Autor);
                    int pal = Palabras(m.Texto);
                    // lo de CUALQUIER lado: sirve para saber cuándo y cuánto escribe esta persona
                    v.PorHora[m.Fecha.Hour]++;
                    v.PorDia[(int)m.Fecha.DayOfWeek]++;
                    v.diasVistos.Add(m.Fecha.Date);
                    if (m.Fecha < v.PrimeroTodo) v.PrimeroTodo = m.Fecha;
                    if (m.Fecha > v.UltimoTodo) v.UltimoTodo = m.Fecha;
                    if (dyad == null) { v.EnGrupos++; v.PalabrasGrupos += pal; continue; }
                    // lo del chat DE A DOS: es la relación de verdad, y es lo que va al lado de «mensajes»
                    v.Suyos++;
                    v.Palabras += pal;
                    v.diasDeADos.Add(m.Fecha.Date);
                    if (m.Fecha < v.Primero) v.Primero = m.Fecha;
                    if (m.Fecha > v.Ultimo) { v.Ultimo = m.Fecha; if (m.Texto.Length > 0) v.UltimoTexto = Recortar(m.Texto, 140); }
                }
                if (dyad == null) continue;

                var vin = Traer(dyad);
                vin.DeADos = true; vin.Chats++;

                // Turnos: corridas seguidas del mismo lado — PERO se cortan también cuando hay un silencio
                // largo en el medio.
                // 🚨🚨 Sin ese corte, dos mensajes suyos separados por MESES caían en el mismo turno y el
                //    silencio se volvía invisible: el hueco se medía desde el FIN del turno, no desde el
                //    mensaje real. Medido con un chat de 264 mensajes: había turnos que abarcaban 233 días
                //    solos, y de 4 silencios de más de una semana no se contaba ninguno.
                var turnos = new List<Turno>();
                foreach (var m in ms)
                {
                    var ult = turnos.Count > 0 ? turnos[turnos.Count - 1] : null;
                    if (ult != null && ult.Mio == m.Mio && (m.Fecha - ult.Fin).TotalSeconds <= SegundosArranque)
                        ult.Fin = m.Fecha;
                    else
                        turnos.Add(new Turno { Mio = m.Mio, Ini = m.Fecha, Fin = m.Fecha });
                }
                for (int i = 1; i < turnos.Count; i++)
                {
                    Turno a = turnos[i - 1], b = turnos[i];
                    double reaccion = Math.Max(0, (b.Ini - a.Fin).TotalSeconds);
                    if (reaccion > SegundosArranque) { if (b.Mio) vin.YoArranco++; else vin.ElArranca++; }
                    // 🚨 Si el lado no cambió, no es una respuesta: es el mismo insistiendo tras el silencio.
                    //    Medir ahí una «espera» sería inventar un diálogo que no ocurrió.
                    if (a.Mio == b.Mio) continue;
                    double espera = (b.Ini - a.Ini).TotalSeconds;
                    if (espera <= 0 || espera > SegundosTope) continue;
                    if (b.Mio) { vin.misEsperas.Add(espera); vin.misReacciones.Add(reaccion); }
                    else { vin.susEsperas.Add(espera); vin.susReacciones.Add(reaccion); }
                }

                // ¿quedó la pelota de tu lado? El último mensaje con texto del chat de a dos.
                // 🚨 Una persona puede tener VARIOS chats de a dos: Teams abre uno por cada reunión donde
                //    estuvieron los dos solos (medido en un equipo real: 5, 5 y 4 veces). Antes esto ASIGNABA
                //    `Pendiente`, así que la última conversación que tocara el diccionario pisaba a las
                //    demás — y como el orden de un Dictionary es arbitrario, el número cambiaba entre
                //    corridas y tapaba deudas reales (16 en vez de 18). Ahora ACUMULA: la pelota es tuya
                //    si quedó de tu lado en CUALQUIERA de los hilos, y se guarda el más viejo.
                for (int i = ms.Count - 1; i >= 0; i--)
                {
                    if (ms[i].Texto.Length == 0) continue;
                    if (!ms[i].Mio)
                    {
                        vin.Pendiente = true;
                        vin.HilosSinContestar++;
                        if (ms[i].Fecha < vin.DebeDesde) vin.DebeDesde = ms[i].Fecha;
                    }
                    break;
                }
            }

            foreach (var v in ix.Values) v.Cerrar();
            Todos = ix.Values.Where(v => v.Todo > 0)
                             .OrderByDescending(v => v.Total).ThenByDescending(v => v.EnGrupos)
                             .ToArray();
            Personas = Todos.Length;
            Desde = h.Desde ?? DateTime.MinValue;
            Hasta = h.Hasta ?? DateTime.MinValue;
            Calculado = DateTime.Now;
            Ms = reloj.ElapsedMilliseconds;
        }

        Vinculo Traer(string nombre)
        {
            string k = Contactos.Normalizar(nombre);
            Vinculo v;
            if (!ix.TryGetValue(k, out v)) { v = new Vinculo { Nombre = nombre, Clave = k }; ix[k] = v; }
            return v;
        }

        static int Palabras(string t)
        {
            if (string.IsNullOrEmpty(t)) return 0;
            int n = 0; bool dentro = false;
            foreach (char c in t)
            {
                if (char.IsWhiteSpace(c)) dentro = false;
                else if (!dentro) { dentro = true; n++; }
            }
            return n;
        }

        internal static string Recortar(string s, int n)
        {
            s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }

        // ------------------------------------------------------------------ recortes para la vista

        /// <summary>A quiénes les debés una respuesta, la deuda MÁS VIEJA primero.</summary>
        public Vinculo[] Pendientes() =>
            Todos.Where(v => v.Pendiente).OrderBy(v => v.DebeDesde).ToArray();

        /// <summary>Con quiénes hablabas seguido y hace rato que no. Silencio raro = mucho más que tu ritmo.</summary>
        public Vinculo[] Enfriados(int minimo = 40)
        {
            var hoy = DateTime.Now;
            return Todos.Where(v => v.Total >= minimo && v.Dias > 3 && v.Ultimo > DateTime.MinValue)
                        .Select(v => new { v, ritmo = (v.Ultimo - v.Primero).TotalDays / Math.Max(1, v.Dias) })
                        .Where(x => x.ritmo > 0 && (hoy - x.v.Ultimo).TotalDays > Math.Max(14, x.ritmo * 6))
                        .OrderByDescending(x => (hoy - x.v.Ultimo).TotalDays / Math.Max(1, x.ritmo))
                        .Select(x => x.v).ToArray();
        }
    }
}
