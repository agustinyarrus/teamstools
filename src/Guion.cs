using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace TeamsTools
{
    /// <summary>Un tramo del guión: quedate en este estado tanto tiempo, con esta variación.</summary>
    internal sealed class Paso
    {
        public string Estado = "Disponible";
        public int Minutos = 60;
        public int Variacion = 20;        // ±% para que no sea un metrónomo
        public bool Activo = true;

        public Paso Copia() => new Paso { Estado = Estado, Minutos = Minutos, Variacion = Variacion, Activo = Activo };
        public override string ToString() => $"{Estado} {Minutos} min ±{Variacion}%";
    }

    /// <summary>
    /// El guión de presencia: una lista de tramos que se repite en bucle para que tu estado parezca de una
    /// persona y no de un proceso. «Disponible 2 h → Ausente 5 min → Disponible 2 h…» con variación aleatoria.
    ///
    /// 🚨 Los nombres de estado que se pueden FIJAR no son los que Teams MUESTRA. El menú ofrece «Aparecer como
    /// ausente» (no existe un «Ausente» elegible: ese es el automático) y «Desconectado». Acá se guarda el nombre
    /// lindo y se traduce al del menú al momento de aplicarlo.
    /// </summary>
    internal sealed class GuionPresencia
    {
        public bool Activo;
        public bool RespetarHorario = true;     // fuera del horario laboral el guión no maneja nada
        public bool RespetarManual = true;      // si el user puso un estado a mano, el guión se hace a un lado
        public bool DesconectarFuera;           // al salir del horario, ponerse Desconectado una vez
        public List<Paso> Pasos = new List<Paso>();

        [ScriptIgnore] public string Ruta = "";

        // ---- lo que se puede fijar de verdad, verificado sobre el menú del avatar (17-sep)
        public static readonly string[] Estados = { "Disponible", "Ocupado", "No molestar", "Vuelvo enseguida", "Ausente", "Desconectado" };

        /// <summary>Traduce el nombre lindo al que realmente figura en el menú de Teams.</summary>
        public static string ParaTeams(string estado)
        {
            if (string.Equals(estado, "Ausente", StringComparison.OrdinalIgnoreCase)) return "Aparecer como ausente";
            return estado;
        }

        /// <summary>¿Es un estado en el que la gente te puede escribir esperando respuesta?</summary>
        public static bool EsOnline(string estado) =>
            string.Equals(estado, "Disponible", StringComparison.OrdinalIgnoreCase);

        public int MinutosDelCiclo => Pasos.Where(p => p.Activo).Sum(p => p.Minutos);

        // ---- plantillas
        public static GuionPresencia Plantilla(string cual)
        {
            var g = new GuionPresencia { Activo = false };
            switch ((cual ?? "").ToLowerInvariant())
            {
                case "real":
                    // lo que pidió el user: online un rato largo, una escapada corta, y de nuevo
                    g.Pasos.Add(new Paso { Estado = "Disponible", Minutos = 120, Variacion = 15 });
                    g.Pasos.Add(new Paso { Estado = "Ausente", Minutos = 5, Variacion = 40 });
                    break;
                case "jornada":
                    g.Pasos.Add(new Paso { Estado = "Disponible", Minutos = 90, Variacion = 20 });
                    g.Pasos.Add(new Paso { Estado = "Ausente", Minutos = 6, Variacion = 50 });
                    g.Pasos.Add(new Paso { Estado = "Disponible", Minutos = 75, Variacion = 20 });
                    g.Pasos.Add(new Paso { Estado = "Ocupado", Minutos = 30, Variacion = 25 });
                    g.Pasos.Add(new Paso { Estado = "Disponible", Minutos = 60, Variacion = 20 });
                    g.Pasos.Add(new Paso { Estado = "Ausente", Minutos = 8, Variacion = 40 });
                    break;
                case "foco":
                    g.Pasos.Add(new Paso { Estado = "No molestar", Minutos = 50, Variacion = 10 });
                    g.Pasos.Add(new Paso { Estado = "Disponible", Minutos = 10, Variacion = 20 });
                    break;
                case "siempre":
                    g.Pasos.Add(new Paso { Estado = "Disponible", Minutos = 480, Variacion = 0 });
                    break;
                default:
                    g.Pasos.Add(new Paso { Estado = "Disponible", Minutos = 120, Variacion = 15 });
                    g.Pasos.Add(new Paso { Estado = "Ausente", Minutos = 5, Variacion = 40 });
                    break;
            }
            return g;
        }

        public static GuionPresencia Cargar(string ruta)
        {
            try
            {
                if (File.Exists(ruta))
                {
                    var g = new JavaScriptSerializer().Deserialize<GuionPresencia>(File.ReadAllText(ruta, Encoding.UTF8));
                    if (g != null)
                    {
                        g.Ruta = ruta;
                        if (g.Pasos == null || g.Pasos.Count == 0) g.Pasos = Plantilla("real").Pasos;
                        foreach (var p in g.Pasos)
                        {
                            p.Minutos = Math.Max(1, Math.Min(720, p.Minutos));
                            p.Variacion = Math.Max(0, Math.Min(80, p.Variacion));
                            if (!Estados.Contains(p.Estado)) p.Estado = "Disponible";
                        }
                        return g;
                    }
                }
            }
            catch { /* un json roto no tiene que tumbar la app */ }
            var nuevo = Plantilla("real");
            nuevo.Ruta = ruta;
            nuevo.Guardar();
            return nuevo;
        }

        public void Guardar()
        {
            if (string.IsNullOrEmpty(Ruta)) return;
            try
            {
                Disco.Escribir(Ruta, new JavaScriptSerializer().Serialize(this));
            }
            catch { }
        }
    }

    /// <summary>
    /// El que hace correr el guión: cada tanto mira si venció el tramo y fija el estado siguiente en Teams,
    /// de forma invisible (menú del avatar por UIA, sin teclas ni ventanas).
    ///
    /// 🚨 El guión y la permanencia online se pisan por definición: si el guión pide Ausente 5 min, la escalera
    /// de rescate lo lee como una deriva y lo corrige a los 40 s. Por eso mientras el guión está activo MANDA él:
    /// `EstadoDeseado` se lo dice a Presencia, que deja de tocar y de rescatar cuando el estado pedido no es
    /// Disponible. Sin eso las dos mitades se sabotean y la presencia parpadea.
    /// </summary>
    internal sealed class Actor
    {
        readonly Config cfg;
        readonly Logger log;
        readonly GuionPresencia guion;
        readonly TeamsChat chat;
        readonly Random azar = new Random();
        Thread hilo;
        volatile bool parar;
        readonly AutoResetEvent despertar = new AutoResetEvent(false);

        public Presencia Presencia;
        public event Action Cambio;

        public int Indice { get; private set; }
        public DateTime? Vence { get; private set; }
        public int Cambios { get; private set; }
        public int Fallos { get; private set; }
        public DateTime? UltimoCambio { get; private set; }
        public string UltimoDetalle { get; private set; } = "";
        public string Estado { get; private set; } = "detenido";
        public bool EnPausa { get; private set; }

        public Actor(Config c, Logger l, GuionPresencia g, TeamsChat ch) { cfg = c; log = l; guion = g; chat = ch; }

        /// <summary>El estado que el guión quiere ahora, o "" si no está manejando nada.</summary>
        public string EstadoDeseado
        {
            get
            {
                if (!guion.Activo || EnPausa) return "";
                var p = PasoActual();
                return p != null ? p.Estado : "";
            }
        }

        public Paso PasoActual()
        {
            var act = guion.Pasos.Where(p => p.Activo).ToList();
            if (act.Count == 0) return null;
            return act[Math.Max(0, Math.Min(act.Count - 1, Indice))];
        }

        public int SegundosParaElProximo => Vence.HasValue ? Math.Max(0, (int)(Vence.Value - DateTime.Now).TotalSeconds) : 0;

        public void Iniciar()
        {
            if (hilo != null) return;
            hilo = new Thread(Bucle) { IsBackground = true, Name = "guion-presencia" };
            hilo.Start();
        }
        public void Detener() { parar = true; despertar.Set(); }
        public void Despertar() => despertar.Set();

        /// <summary>Arranca el guión desde el primer tramo, ahora. Lo que usa el botón «aplicar ahora».</summary>
        public void Reiniciar()
        {
            Indice = 0;
            Vence = null;
            despertar.Set();
        }

        void Bucle()
        {
            while (!parar)
            {
                try { Vuelta(); }
                catch (Exception ex) { log.Error("Guión: " + ex.Message); }
                despertar.WaitOne(5000);
            }
        }

        void Vuelta()
        {
            if (!guion.Activo)
            {
                if (Estado != "detenido") { Estado = "detenido"; Vence = null; Avisar(); }
                return;
            }
            var activos = guion.Pasos.Where(p => p.Activo).ToList();
            if (activos.Count == 0) { Estado = "sin tramos"; Avisar(); return; }

            // 1) fuera del horario laboral el guión no maneja: que Teams haga lo suyo
            if (guion.RespetarHorario && !cfg.EnHorario())
            {
                if (!EnPausa)
                {
                    EnPausa = true; Estado = "fuera de horario"; Vence = null;
                    if (guion.DesconectarFuera) Aplicar("Desconectado", "salió del horario");
                    log.Debug("Guión: fuera de horario, no manejo la presencia");
                    Avisar();
                }
                return;
            }

            // 2) si el estado lo puso él a mano, el guión se hace a un lado hasta que eso pase
            if (guion.RespetarManual && Presencia != null && Presencia.RespetandoManual)
            {
                if (!EnPausa) { EnPausa = true; Estado = "lo pusiste vos"; Vence = null; log.Info("Guión: pusiste un estado a mano, me hago a un lado"); Avisar(); }
                return;
            }

            if (EnPausa) { EnPausa = false; Vence = null; log.Debug("Guión: vuelvo a manejar la presencia"); }
            if (Indice >= activos.Count) Indice = 0;

            // 3) ¿todavía no arrancó, o venció el tramo?
            if (!Vence.HasValue)
            {
                Aplicar(activos[Indice].Estado, "arranque del guión");
                Vence = DateTime.Now.AddSeconds(Duracion(activos[Indice]));
                Estado = "en «" + activos[Indice].Estado + "»";
                Avisar();
                return;
            }
            if (DateTime.Now < Vence.Value) { Estado = "en «" + activos[Indice].Estado + "»"; return; }

            Indice = (Indice + 1) % activos.Count;
            var sig = activos[Indice];
            Aplicar(sig.Estado, "le tocaba el turno");
            Vence = DateTime.Now.AddSeconds(Duracion(sig));
            Estado = "en «" + sig.Estado + "»";
            Avisar();
        }

        /// <summary>Duración del tramo con su variación: sin esto los cambios caen siempre en el mismo minuto.</summary>
        int Duracion(Paso p)
        {
            double baseSeg = Math.Max(30, p.Minutos * 60);
            if (p.Variacion <= 0) return (int)baseSeg;
            double f = 1 + (azar.NextDouble() * 2 - 1) * (p.Variacion / 100.0);
            return (int)Math.Max(30, baseSeg * f);
        }

        void Aplicar(string estado, string porque)
        {
            if (chat == null) { Fallos++; UltimoDetalle = "sin acceso a Teams"; return; }
            string detalle;
            // el guión abre el menú del avatar (DOM de Teams): bajo la aduana, como todo lo demás
            bool ok;
            using (chat.Gate.Entrar("guión: fijar " + estado))
                ok = chat.FijarPresencia(GuionPresencia.ParaTeams(estado), out detalle);
            UltimoCambio = DateTime.Now;
            UltimoDetalle = detalle;
            if (ok)
            {
                Cambios++;
                log.Ok($"Guión: puse «{estado}» ({porque})");
            }
            else
            {
                Fallos++;
                log.Aviso($"Guión: no pude poner «{estado}» · {detalle}");
            }
            Avisar();
        }

        void Avisar() { try { Cambio?.Invoke(); } catch { } }
    }
}
